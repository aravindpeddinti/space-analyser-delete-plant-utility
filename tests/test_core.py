import os
import tempfile
import unittest

from app.core.crypto import decrypt, encrypt
from app.core.errors import BackendError, RunCancelled
from app.core.ini_reader import read_site_ini, resolve_ini_path
from app.core.backends.base import Backend
from app.core.models import (DeletionPlan, Plant, PlantPlan, Project, RunContext,
                             SiteConfig, Target)
from app.core.network import parse_host_and_port, is_host_pingable
from app.core.safety import (assess_path, compute_reference_folders, qident_mssql,
                             safe_oracle_ident)
from app.core.sqlutil import chunked, placeholders


class Dummy(Backend):
    def connect(self, manual=None, mode="thin"): ...
    def list_plants(self): return []
    def build_plan(self, plants, delete_site=False): return DeletionPlan()
    def execute(self, plan, ctx): return []
    def close(self): ...
    def build_site_targets(self): return []


def mk(base, *parts, files=("a.txt",)):
    p = os.path.join(base, *parts)
    os.makedirs(p, exist_ok=True)
    for f in files:
        with open(os.path.join(p, f), "w") as fh:
            fh.write("x")
    return p


def ctx_collect(cancel=False):
    logs = []
    c = RunContext(lambda l, m: logs.append((l, m)), lambda *a: None, lambda: cancel)
    return c, logs


class CryptoTests(unittest.TestCase):
    def test_roundtrip(self):
        for pw in ["a", "ab", "sa", "Passw0rd", "Intergraph1", "x" * 30, "p@ss=word;1"]:
            enc = encrypt(pw)
            self.assertEqual(len(enc), 64)
            self.assertEqual(decrypt(enc), pw)

    def test_bad_length(self):
        self.assertEqual(decrypt("short"), "")
        self.assertEqual(decrypt(""), "")


class IniTests(unittest.TestCase):
    def write_ini(self, d, db="ORACLE", password="Secret=1"):
        lines = ["[Site]", "x=1", f"DBType={db}", "SQLServer=SQLHOST", "Instance=ORCL",
                 "Schema=SITEUSER", "y=1", "User=admin", f"Password={encrypt(password)}", ""]
        p = os.path.join(d, "smartplantv4.ini")
        with open(p, "w", encoding="utf-16") as f:
            f.write("\n".join(lines))
        return p

    def test_oracle(self):
        with tempfile.TemporaryDirectory() as d:
            self.write_ini(d)
            s = read_site_ini(resolve_ini_path(d))
            self.assertEqual((s.db_type, s.server, s.site_schema, s.username), ("ORACLE", "ORCL", "SITEUSER", "admin"))
            self.assertEqual(s.password, "Secret=1")   # '=' inside value survives

    def test_mssql(self):
        with tempfile.TemporaryDirectory() as d:
            self.write_ini(d, db="MSSQL")
            s = read_site_ini(resolve_ini_path(d))
            self.assertEqual((s.db_type, s.server, s.site_database), ("MSSQL", "SQLHOST", "ORCL"))

    def test_sections_ini(self):
        with tempfile.TemporaryDirectory() as d:
            content = f"""[SITESCHEMA]
DBTYPE=ORACLE
ORACLE_SERVICE=basfsite
DBSERVER=basfsite
UID=basfsite
PWD={encrypt("SitePwd")}
DBAUSER=system
DBA={encrypt("DbaPwd")}
[DICTIONARY]
DBTYPE=ORACLE
ORACLE_SERVICE=basfsite
DBSERVER=basfsite
UID=basfsited
PWD={encrypt("DictPwd")}
"""
            p = os.path.join(d, "smartplantv4.ini")
            with open(p, "w", encoding="utf-8") as f:
                f.write(content)
            s = read_site_ini(p)
            self.assertEqual(s.db_type, "ORACLE")
            self.assertEqual(s.server, "basfsite")
            self.assertEqual(s.oracle_service, "basfsite")
            self.assertEqual(s.site_schema, "basfsite")
            self.assertEqual(s.dictionary_schema, "basfsited")
            self.assertEqual(s.username, "system")
            self.assertEqual(s.dba_user, "system")
            self.assertEqual(s.password, "DbaPwd")
            self.assertEqual(s.dba_password, "DbaPwd")

    def test_bad_inputs(self):
        with tempfile.TemporaryDirectory() as d:
            with self.assertRaises(BackendError): resolve_ini_path(d)
            with self.assertRaises(BackendError): resolve_ini_path("")


class SafetyTests(unittest.TestCase):
    def test_blocks(self):
        home = tempfile.mkdtemp()
        old = os.environ.get("USERPROFILE")
        os.environ["USERPROFILE"] = home
        try:
            for bad in ["", "   ", "relative/path", "/", home]:
                self.assertTrue(assess_path(bad)[0], bad)
        finally:
            if old is None: os.environ.pop("USERPROFILE", None)
            else: os.environ["USERPROFILE"] = old
            os.rmdir(home)

    def test_allows_normal(self):
        with tempfile.TemporaryDirectory() as d:
            p = mk(d, "plants", "PlantA")
            self.assertEqual(assess_path(p)[0], "")

    def test_idents(self):
        self.assertEqual(safe_oracle_ident("pl_ant$1"), "PL_ANT$1")
        for bad in ["a b", "a;drop user x", "1abc", "", 'a"b']:
            with self.assertRaises(ValueError): safe_oracle_ident(bad)
        self.assertEqual(qident_mssql("a]b"), "[a]]b]")

    def test_sql_helpers(self):
        self.assertEqual(placeholders(3, "oracle"), ":1,:2,:3")
        self.assertEqual(placeholders(3, "mssql"), "?,?,?")
        self.assertEqual([len(c) for c in chunked(range(2000), 900)], [900, 900, 200])


class RefFolderTests(unittest.TestCase):
    def test_exclusive_common_root_is_used(self):
        with tempfile.TemporaryDirectory() as d:
            a = mk(d, "Ref", "PID", "Symbols")
            b = mk(d, "Ref", "EL", "Templates")
            out, notes = compute_reference_folders([a, b])
            self.assertEqual(out, [os.path.join(d, "Ref")])

    def test_foreign_data_is_preserved(self):
        with tempfile.TemporaryDirectory() as d:
            a = mk(d, "Ref", "PID", "Symbols")
            b = mk(d, "Ref", "EL", "Templates")
            mk(d, "Ref", "Other", "KeepMe")
            out, notes = compute_reference_folders([a, b])
            self.assertCountEqual(out, [a, b])
            self.assertTrue(notes)

    def test_missing_and_junk_ignored(self):
        with tempfile.TemporaryDirectory() as d:
            a = mk(d, "Ref", "One")
            out, _ = compute_reference_folders([a, None, "", "True", os.path.join(d, "nope")])
            self.assertEqual(out, [a])

    def test_nested_collapsed(self):
        with tempfile.TemporaryDirectory() as d:
            a = mk(d, "Ref")
            b = mk(d, "Ref", "Sub")
            out, _ = compute_reference_folders([a, b])
            self.assertEqual(out, [a])


class FolderDeleteTests(unittest.TestCase):
    def plan_for(self, backend, plant, pid=(), sel=()):
        pp = PlantPlan(plant)
        pp.targets, _ = backend.folder_targets(plant, list(pid), list(sel))
        return pp

    def test_deletes_and_prunes_empty_parents(self):
        with tempfile.TemporaryDirectory() as d:
            mk(d, "sentinel")      # keeps d itself non-empty so pruning stops there
            plant_dir = mk(d, "Plants", "Site1", "PlantA")
            proj_dir = mk(plant_dir, "Proj1")
            plant = Plant("1", "PlantA", plant_dir, [Project("2", "Proj1", proj_dir)])
            b = Dummy(SiteConfig("x", "ORACLE", "S", "", "SCH", "u"))
            pp = self.plan_for(b, plant)
            ctx, logs = ctx_collect()
            self.assertTrue(b.delete_folders(pp, ctx))
            self.assertFalse(os.path.exists(plant_dir))
            self.assertFalse(os.path.exists(os.path.join(d, "Plants")))  # emptied parents pruned
            self.assertTrue(os.path.isdir(d))

    def test_parent_with_other_content_kept(self):
        with tempfile.TemporaryDirectory() as d:
            plant_dir = mk(d, "Plants", "PlantA")
            other = mk(d, "Plants", "PlantB")
            plant = Plant("1", "PlantA", plant_dir)
            b = Dummy(SiteConfig("x", "ORACLE", "S", "", "SCH", "u"))
            pp = self.plan_for(b, plant)
            ctx, _ = ctx_collect()
            b.delete_folders(pp, ctx)
            self.assertTrue(os.path.isdir(other))

    def test_disabled_and_blocked_untouched(self):
        with tempfile.TemporaryDirectory() as d:
            keep = mk(d, "Keep")
            plant = Plant("1", "P", keep)
            b = Dummy(SiteConfig("x", "ORACLE", "S", "", "SCH", "u"))
            pp = self.plan_for(b, plant)
            for t in pp.targets: t.enabled = False
            ctx, _ = ctx_collect()
            b.delete_folders(pp, ctx)
            self.assertTrue(os.path.isdir(keep))

    def test_cancel_raises_before_deleting(self):
        with tempfile.TemporaryDirectory() as d:
            keep = mk(d, "Keep")
            plant = Plant("1", "P", keep)
            b = Dummy(SiteConfig("x", "ORACLE", "S", "", "SCH", "u"))
            pp = self.plan_for(b, plant)
            ctx, _ = ctx_collect(cancel=True)
            with self.assertRaises(RunCancelled): b.delete_folders(pp, ctx)
            self.assertTrue(os.path.isdir(keep))

    def test_drive_root_is_blocked_in_plan(self):
        b = Dummy(SiteConfig("x", "ORACLE", "S", "", "SCH", "u"))
        t = b.folder_target("/", "root", "plant")
        self.assertTrue(t.blocked)
        self.assertFalse(t.active)


class PlanTests(unittest.TestCase):
    def test_step_counts(self):
        pp = PlantPlan(Plant("1", "P", "/x"))
        pp.targets = [Target("folder", "/a"), Target("schema", "S"), Target("site_records", "P"),
                      Target("folder", "/b", enabled=False), Target("folder", "/c", blocked="no")]
        plan = DeletionPlan([pp], [Target("tablespace", "T1", enabled=True)])
        self.assertEqual(plan.total_steps(), 4)

    def test_site_deletion_plan_steps(self):
        plan = DeletionPlan(
            plants=[],
            delete_site=True,
            site_name="basfsite",
            site_targets=[
                Target("site_folder", "/data/backup", enabled=True),
                Target("site_file", "/data/smartplantv4.ini", enabled=True),
                Target("schema", "basfsited", enabled=True),
                Target("schema", "basfsite", enabled=True),
            ]
        )
        self.assertEqual(plan.total_steps(), 4)


class NetworkTests(unittest.TestCase):
    def test_parse_host_and_port(self):
        self.assertEqual(parse_host_and_port("SERVER01\\INST02", 1433), ("SERVER01", 1433, "INST02"))
        self.assertEqual(parse_host_and_port("192.168.1.10,14333", 1433), ("192.168.1.10", 14333, ""))
        self.assertEqual(parse_host_and_port("orclhost:1522", 1521), ("orclhost", 1522, ""))
        self.assertEqual(parse_host_and_port("orclhost", 1521), ("orclhost", 1521, ""))

    def test_is_host_pingable_localhost(self):
        self.assertTrue(is_host_pingable("localhost"))
        self.assertTrue(is_host_pingable("127.0.0.1"))


class ProcessMatchTests(unittest.TestCase):
    def test_matches_substring_case_insensitive(self):
        from app.core.processes import match_processes
        names = ["explorer.exe", "SmartPlantPID.exe", "DrawingManager.EXE", "chrome.exe", "Delete Plant Utility.exe"]
        self.assertEqual(match_processes(names, ["smartplant", "drawingmanager.exe"]),
                         ["DrawingManager.EXE", "SmartPlantPID.exe"])

    def test_never_matches_itself_or_unrelated(self):
        from app.core.processes import match_processes
        self.assertEqual(match_processes(["Delete Plant Utility.exe", "notepad.exe"], ["plant", "pad"]), ["notepad.exe"])


class ThemeContrastTests(unittest.TestCase):
    def test_text_is_readable_in_both_themes(self):
        from app.ui.tokens import DARK, LIGHT, TEXT_PAIRS, contrast
        for name, t in (("light", LIGHT), ("dark", DARK)):
            for fg, bg in TEXT_PAIRS:
                self.assertGreaterEqual(contrast(t[fg], t[bg]), 4.5, f"{name}: {fg} on {bg}")

    def test_both_themes_define_same_tokens(self):
        from app.ui.tokens import DARK, LIGHT
        self.assertEqual(set(LIGHT), set(DARK))


class FindIniTests(unittest.TestCase):
    def test_finds_nested_and_case_insensitive(self):
        from app.core.ini_reader import find_ini_files
        with tempfile.TemporaryDirectory() as d:
            for sub, name in (("SiteA", "smartplantv4.ini"), ("x/y/SiteB", "SmartPlantV4.INI"),
                              ("SiteC", "other.ini"), ("$RECYCLE.BIN/z", "smartplantv4.ini")):
                os.makedirs(os.path.join(d, sub), exist_ok=True)
                with open(os.path.join(d, sub, name), "w") as f:
                    f.write("test")
            found, scanned, stopped = find_ini_files(d)
            self.assertEqual(len(found), 2)
            self.assertFalse(stopped)

    def test_none_and_limit(self):
        from app.core.ini_reader import find_ini_files
        with tempfile.TemporaryDirectory() as d:
            self.assertEqual(find_ini_files(d)[0], [])
            for i in range(5):
                os.makedirs(os.path.join(d, f"s{i}"))
                with open(os.path.join(d, f"s{i}", "smartplantv4.ini"), "w") as f:
                    f.write("test")
            found, _, stopped = find_ini_files(d, limit=3)
            self.assertEqual(len(found), 3)
            self.assertTrue(stopped)

    def test_describe_hides_password_and_survives_bad_file(self):
        from app.core.ini_reader import describe_ini
        with tempfile.TemporaryDirectory() as d:
            p = os.path.join(d, "smartplantv4.ini")
            with open(p, "w") as f:
                f.write("junk")
            info = describe_ini(p)
            self.assertTrue(info["error"])
            self.assertNotIn("password", info)


if __name__ == "__main__":
    unittest.main()
