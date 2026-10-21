"""Backends driven by fake DB drivers - verifies planning, ordering and fail-safe behaviour."""
import os
import sys
import tempfile
import types
import unittest

from app.core.crypto import encrypt


# ---------------------------------------------------------------- fake drivers
class FakeError(Exception):
    pass


class FakeDbError(FakeError):
    def __init__(self, code):
        self.args = (types.SimpleNamespace(code=code),)


class FakeCursor:
    def __init__(self, conn):
        self.conn, self.rows, self.rowcount = conn, [], 0

    def execute(self, sql, params=None):
        s = " ".join(sql.split())
        self.conn.log.append((s, list(params) if params else []))
        for key, rows in self.conn.script:
            if key.lower() in s.lower():
                self.rows = rows() if callable(rows) else list(rows)
                break
        else:
            self.rows = []
        if s.lower().startswith("delete"):
            self.rowcount = 1
        hook = self.conn.hooks.get(s.split(" ")[0].upper() + " " + (s.split(" ")[1].upper() if " " in s else ""))
        if hook:
            hook(s)

    def fetchall(self): return list(self.rows)
    def fetchone(self): return self.rows[0] if self.rows else None


class FakeConn:
    def __init__(self, script, hooks=None):
        self.script, self.hooks, self.log = script, hooks or {}, []
        self.committed = self.rolled = 0
        self.autocommit = True

    def cursor(self): return FakeCursor(self)
    def commit(self): self.committed += 1
    def rollback(self): self.rolled += 1
    def close(self): pass

    def sqls(self, prefix):
        return [s for s, _ in self.log if s.lower().startswith(prefix.lower())]


def install_fakes(conn_factory):
    ora = types.ModuleType("oracledb")
    ora.Error = FakeError
    ora.DatabaseError = FakeError
    ora.connect = lambda **kw: conn_factory()
    ora.init_oracle_client = lambda **kw: None
    sys.modules["oracledb"] = ora
    odbc = types.ModuleType("pyodbc")
    odbc.Error = FakeError
    odbc.drivers = lambda: ["ODBC Driver 17 for SQL Server"]
    odbc.connect = lambda *a, **kw: conn_factory()
    sys.modules["pyodbc"] = odbc
    for m in [k for k in sys.modules if k.startswith("app.core.backends.") and k != "app.core.backends.base"]:
        del sys.modules[m]


def make_dirs(base):
    plant = os.path.join(base, "Plants", "PlantA")
    proj = os.path.join(plant, "Proj1")
    ref = os.path.join(base, "Ref", "PlantA")
    for d in (proj, ref):
        os.makedirs(d, exist_ok=True)
        with open(os.path.join(d, "f.txt"), "w") as f:
            f.write("x")
    return plant, proj, ref


def ctx():
    from app.core.models import RunContext
    logs = []
    c = RunContext(lambda l, m: logs.append((l, m)), lambda *a: None, lambda: False)
    return c, logs


class OracleFakeTests(unittest.TestCase):
    def setUp(self):
        self.tmp = tempfile.TemporaryDirectory()
        self.plant, self.proj, self.ref = make_dirs(self.tmp.name)
        enc = encrypt("syspw")

        def script():
            return [
                ("from T_RootItem", [("1", "PlantA", self.plant, "0"), ("2", "Proj1", self.proj, "1"),
                                     ("5", "PlantB", "/x/PlantB", None)]),
                ("select sp_rootitemid, username from t_db_data", [("5", "SHARED_SCH")]),
                ("from t_db_data where sp_rootitemid in", [
                    ("PLA_SCH", "ORCL", enc, "system", "SPPID"),
                    ("SHARED_SCH", "ORCL", enc, "system", "SPEL"),
                    ("SYSTEM", "ORCL", enc, "system", "SPEL"),
                    ("GONE_SCH", "ORCL", enc, "system", "SPEL")]),
                ("from v$session where username", [(3,)]),
                ("from all_users", self._exists),
                ("t_optionsetting", [(self.ref,)]),
                ("select distinct tablespace_name from dba_segments", [("PLA_TS",), ("USERS",)]),
                ("select distinct temporary_tablespace", [("PLA_TEMP",), ("TEMP",)]),
                ("database_properties", [("USERS",), ("TEMP",)]),
                ("contents = 'UNDO'", [("UNDOTBS1",)]),
                ("from t_roles where proj_id", [("r1",), ("r2",)]),
                ("t_sitesetting", [("Backup Location", os.path.join(self.tmp.name, "SiteBackup")),
                                   ("Role Template Location", os.path.join(self.tmp.name, "RoleTemplates"))]),
            ]
        self.exists_calls = []
        self.conn = FakeConn(script())
        self.conn.script = script()
        install_fakes(lambda: self.conn)
        from app.core.backends.oracle_backend import OracleBackend
        from app.core.models import SiteConfig
        self.b = OracleBackend(SiteConfig(os.path.join(self.tmp.name, "smartplantv4.ini"), "ORACLE", "ORCL", "", "SITE", "admin", "pw",
                                          oracle_service="ORCL", dictionary_schema="SITED"))
        self.b.connect({"host": "h", "service": "s"})

    def _exists(self):
        sql, params = self.conn.log[-1]
        return [] if params and params[0] == "GONE_SCH" else [(params[0],)]

    def tearDown(self):
        self.tmp.cleanup()

    def test_list_plants(self):
        plants = self.b.list_plants()
        self.assertEqual([p.name for p in plants], ["PlantA", "PlantB"])
        self.assertEqual(len(plants[0].projects), 1)

    def test_plan_blocks_protected_and_shared(self):
        plant = self.b.list_plants()[0]
        plan = self.b.build_plan([plant])
        pp = plan.plants[0]
        schemas = {t.name: t for t in pp.targets if t.kind == "schema"}
        self.assertTrue(schemas["PLA_SCH"].active)
        self.assertTrue(any("active session" in w for w in pp.warnings))
        self.assertIn("SHARED_SCH", schemas)
        self.assertTrue(schemas["SHARED_SCH"].blocked)          # used by plant 5
        self.assertTrue(schemas["SYSTEM"].blocked)              # protected account
        self.assertNotIn("GONE_SCH", schemas)                   # already gone -> note only
        self.assertTrue(any("GONE_SCH" in n for n in pp.notes))
        ts = {t.name: t for t in plan.tablespaces}
        self.assertTrue(ts["USERS"].blocked and ts["TEMP"].blocked)
        self.assertFalse(ts["PLA_TS"].enabled)                  # opt-in
        folders = [t.name for t in pp.targets if t.kind == "folder"]
        self.assertIn(self.ref, folders)

    def test_execute_order_and_site_records(self):
        plant = self.b.list_plants()[0]
        plan = self.b.build_plan([plant])
        for t in plan.tablespaces:
            if t.name == "PLA_TS":
                t.enabled = True
        c, logs = ctx()
        res = self.b.execute(plan, c)
        self.assertEqual(res[0].status, "ok")
        self.assertFalse(os.path.exists(self.plant))
        self.assertFalse(os.path.exists(self.ref))
        drops = self.conn.sqls("DROP USER")
        self.assertEqual(drops, ["DROP USER PLA_SCH CASCADE"])   # shared/protected never dropped
        dels = [s for s in self.conn.sqls("delete from")]
        self.assertTrue(dels[-1].startswith("delete from t_rootitem"))   # parent row last
        self.assertEqual(self.conn.committed, 1)
        self.assertTrue(self.conn.sqls("DROP TABLESPACE PLA_TS"))
        self.assertFalse(self.conn.sqls("DROP TABLESPACE USERS"))

    def test_locked_folder_halts_before_database(self):
        plant = self.b.list_plants()[0]
        plan = self.b.build_plan([plant])
        import app.core.backends.base as base
        orig = base.delete_tree
        base.delete_tree = lambda *a, **k: False       # simulate a locked file
        try:
            c, logs = ctx()
            res = self.b.execute(plan, c)
        finally:
            base.delete_tree = orig
        self.assertEqual(res[0].status, "partial")
        self.assertFalse(self.conn.sqls("DROP USER"))
        self.assertFalse(self.conn.sqls("delete from"))

    def test_site_wipe_skipped_when_plant_is_partial(self):
        plant = self.b.list_plants()[0]
        plan = self.b.build_plan([plant], delete_site=True)
        import app.core.backends.base as base
        orig = base.delete_tree
        base.delete_tree = lambda *a, **k: False
        try:
            c, logs = ctx()
            res = self.b.execute(plan, c)
        finally:
            base.delete_tree = orig
        self.assertEqual(res[0].status, "partial")
        self.assertEqual(res[-1].status, "partial")
        self.assertFalse(any("DROP USER SITE" in s for s in self.conn.sqls("DROP USER")))

    def test_delete_empty_site(self):
        plan = self.b.build_plan([], delete_site=True)
        self.assertTrue(plan.delete_site)
        site_kinds = {t.kind for t in plan.site_targets}
        self.assertIn("schema", site_kinds)
        self.assertIn("site_file", site_kinds)
        c, logs = ctx()
        res = self.b.execute(plan, c)
        self.assertEqual(res[0].status, "ok")
        drops = self.conn.sqls("DROP USER")
        self.assertIn("DROP USER SITED CASCADE", drops)
        self.assertIn("DROP USER SITE CASCADE", drops)


class MssqlFakeTests(unittest.TestCase):
    def setUp(self):
        self.tmp = tempfile.TemporaryDirectory()
        self.plant, self.proj, self.ref = make_dirs(self.tmp.name)
        enc = encrypt("syspw")
        script = [
            ("sp_parentrootitemid from [SITEDB].[dbo].T_RootItem",
             [("1", "PlantA", self.plant, "0"), ("2", "Proj1", self.proj, "1")]),
            ("select sp_rootitemid, username, data_base", [("9", "other_login", "OtherDB")]),
            ("from [SITEDB].[dbo].T_DB_Data where sp_rootitemid in", [
                ("pla_login", "SQLSRV", "PlantDB", enc, "sa", "SPEL"),
                ("other_login", "SQLSRV", "PlantDB", enc, "sa", "SPPID"),
                ("sa", "SQLSRV", "PlantDB", enc, "sa", "SPPID")]),
            ("sys.dm_exec_sessions where database_id", [(2,)]),
            ("db_id", [(7,)]),
            ("sys.server_principals", [(1,)]),
            ("t_optionsetting", [(self.ref,)]),
            ("from [SITEDB].[dbo].t_roles", [("r1",)]),
            ("t_sitesetting", [("Backup Location", os.path.join(self.tmp.name, "SiteBackup"))]),
        ]
        self.conn = FakeConn(script)
        install_fakes(lambda: self.conn)
        from app.core.backends.mssql_backend import MssqlBackend
        from app.core.models import SiteConfig
        self.b = MssqlBackend(SiteConfig(os.path.join(self.tmp.name, "smartplantv4.ini"), "MSSQL", "SQLSRV", "SITEDB", "dbo", "sa", "pw"))
        self.b.connect()

    def tearDown(self):
        self.tmp.cleanup()

    def test_plan_and_execute(self):
        plant = self.b.list_plants()[0]
        plan = self.b.build_plan([plant])
        pp = plan.plants[0]
        kinds = {(t.kind, t.name): t for t in pp.targets}
        self.assertTrue(kinds[("database", "PlantDB")].active)
        self.assertTrue(any("active session" in w for w in pp.warnings))
        self.assertTrue(kinds[("login", "pla_login")].active)
        self.assertTrue(kinds[("login", "sa")].blocked)
        self.assertTrue(kinds[("login", "other_login")].blocked)
        c, logs = ctx()
        res = self.b.execute(plan, c)
        self.assertEqual(res[0].status, "ok", logs)
        self.assertFalse(os.path.exists(self.plant))
        self.assertTrue(self.conn.sqls("ALTER DATABASE [PlantDB] SET SINGLE_USER"))
        self.assertTrue(self.conn.sqls("DROP DATABASE [PlantDB]"))
        self.assertEqual(self.conn.sqls("DROP LOGIN"), ["DROP LOGIN [pla_login]"])
        self.assertEqual(self.conn.committed, 1)
        self.assertTrue(self.conn.autocommit)

    def test_sql_injection_in_names_is_quoted(self):
        from app.core.safety import qident_mssql
        self.assertEqual(qident_mssql("x]; drop database master;--"), "[x]]; drop database master;--]")

    def test_delete_empty_site(self):
        plan = self.b.build_plan([], delete_site=True)
        self.assertTrue(plan.delete_site)
        c, logs = ctx()
        res = self.b.execute(plan, c)
        self.assertEqual(res[0].status, "ok")
        self.assertTrue(self.conn.sqls("DROP DATABASE [SITEDB]"))

    def test_site_wipe_skipped_when_plant_is_partial(self):
        plant = self.b.list_plants()[0]
        plan = self.b.build_plan([plant], delete_site=True)
        import app.core.backends.base as base
        orig = base.delete_tree
        base.delete_tree = lambda *a, **k: False
        try:
            c, logs = ctx()
            res = self.b.execute(plan, c)
        finally:
            base.delete_tree = orig
        self.assertEqual(res[0].status, "partial")
        self.assertEqual(res[-1].status, "partial")
        self.assertFalse(self.conn.sqls("DROP DATABASE [SITEDB]"))


if __name__ == "__main__":
    unittest.main()
