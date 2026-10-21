from __future__ import annotations

import os
import re

import pyodbc

from ..crypto import decrypt
from ..errors import BackendError, RunCancelled
from ..models import (DeletionPlan, Plant, PlantPlan, PlantResult, Project,
                      RunContext, SiteConfig, Target)
from ..network import diagnose_connection_failure
from ..safety import assess_path, qident_mssql
from ..sqlutil import delete_in, select_in
from .base import (APP_SCHEMA_TYPES, PID_OPTION_IDS, SEL_OPTION_IDS, Backend)

SYSTEM_DATABASES = {"master", "model", "msdb", "tempdb"}
PROTECTED_DATABASES = SYSTEM_DATABASES
PROTECTED_LOGINS = {"sa"}


def _odbc_value(v: str) -> str:
    return "{" + (v or "").replace("}", "}}") + "}"


def _preferred_drivers() -> list[str]:
    installed = pyodbc.drivers()
    modern = []
    for d in installed:
        m = re.match(r"ODBC Driver (\d+) for SQL Server", d)
        if m:
            modern.append((int(m.group(1)), d))
    ordered = [d for _, d in sorted(modern, reverse=True)]
    for legacy in ("SQL Server Native Client 11.0", "SQL Server Native Client 10.0", "SQL Server"):
        if legacy in installed:
            ordered.append(legacy)
    return ordered


def open_sql_connection(driver_list: list[str], server: str, user: str, pwd: str):
    if not driver_list:
        raise BackendError(
            "No SQL Server ODBC driver was found for this application. Install "
            "'ODBC Driver 17/18 for SQL Server'.")
    last = None
    for driver in driver_list:
        parts = [f"DRIVER={{{driver}}}", f"SERVER={server}", "DATABASE=master",
                 f"UID={_odbc_value(user)}", f"PWD={_odbc_value(pwd)}"]
        m = re.match(r"ODBC Driver (\d+)", driver)
        if m and int(m.group(1)) >= 18:
            parts.append("TrustServerCertificate=yes")
        try:
            return pyodbc.connect(";".join(parts), autocommit=True, timeout=15)
        except pyodbc.Error as e:
            last = e
            state = str(e.args[0]) if e.args else ""
            text = str(e).lower()
            if state in ("IM002", "IM003") or "ssl" in text or "certificate" in text or "tls" in text:
                continue
            diag = diagnose_connection_failure(server, "MSSQL")
            err_msg = f"{diag}\n\nTechnical error: {e}" if diag else f"Failed to connect to {server}: {e}"
            raise BackendError(err_msg) from e

    diag = diagnose_connection_failure(server, "MSSQL")
    err_msg = f"{diag}\n\nTechnical error: {last}" if diag else f"Failed to connect to {server}: {last}"
    raise BackendError(err_msg)


class MssqlBackend(Backend):
    db_type = "MSSQL"

    def __init__(self, site: SiteConfig):
        super().__init__(site)
        self.conn = None
        self.remote: dict[str, object] = {}
        self.creds: dict[str, tuple[str, str]] = {}
        self.drivers = _preferred_drivers()
        self.db = qident_mssql(site.site_database)
        self.schema = qident_mssql(site.site_schema)

    def T(self, table: str) -> str:
        return f"{self.db}.{self.schema}.{table}"

    def connect(self, manual: dict | None = None, mode: str = "thin") -> None:
        self.conn = open_sql_connection(self.drivers, self.site.server,
                                        self.site.username, self.site.password)

    def close(self) -> None:
        for c in list(self.remote.values()) + [self.conn]:
            try:
                if c:
                    c.close()
            except Exception:
                pass
        self.remote.clear()
        self.conn = None

    def _connection(self, server: str):
        if not server or server.upper() == self.site.server.upper():
            return self.conn
        key = server.upper()
        if key not in self.remote:
            uid, pwd = self.creds.get(key, (None, None))
            if not uid:
                uid, pwd = self.site.username, self.site.password
            self.remote[key] = open_sql_connection(self.drivers, server, uid, pwd)
        return self.remote[key]

    # ---- read side -----------------------------------------------------------------
    def list_plants(self) -> list[Plant]:
        cur = self.conn.cursor()
        cur.execute(f"select sp_id, name, path, sp_parentrootitemid from {self.T('T_RootItem')}")
        rows = cur.fetchall()
        plants: dict[str, Plant] = {}
        for sp_id, name, path, parent in rows:
            if parent in (None, "", "0"):
                plants[str(sp_id)] = Plant(str(sp_id), name or "(unnamed)", path or "")
        for sp_id, name, path, parent in rows:
            if parent not in (None, "", "0") and str(parent) in plants:
                plants[str(parent)].projects.append(Project(str(sp_id), name or "", path or ""))
        return sorted(plants.values(), key=lambda p: p.name.lower())

    def inventory_storage(self, plants: list[Plant]) -> dict:
        """Return allocated SQL Server database-file bytes by plant."""
        cur = self.conn.cursor()
        cur.execute(f"select sp_rootitemid, username, dbserver, data_base, syspassword, sysuid "
                    f"from {self.T('T_DB_Data')}")
        rows = cur.fetchall()
        id_to_plant = {root_id: p.root_id for p in plants for root_id in p.all_ids}
        mappings: dict[str, list[tuple[str, str, str]]] = {}
        owners: dict[tuple[str, str], set[str]] = {}
        for root_id, username, server, database, encrypted, uid in rows:
            if not database:
                continue
            root_id = str(root_id)
            plant_id = id_to_plant.get(root_id)
            if not plant_id:
                continue
            server = server or self.site.server
            key = (server.upper(), str(database).casefold())
            owners.setdefault(key, set()).add(plant_id)
            if uid:
                self.creds[server.upper()] = (uid, decrypt(encrypted or ""))
            value = (server, str(database), plant_id)
            if value not in mappings.setdefault(plant_id, []):
                mappings[plant_id].append(value)
        by_id: dict[str, dict] = {}
        warnings: list[str] = []
        for plant in plants:
            keys = mappings.get(plant.root_id, [])
            items: list[dict] = []
            total = 0
            complete = True
            for server, database, plant_id in keys:
                server_key = server.upper()
                db_key = database.casefold()
                try:
                    c = self._connection(server).cursor()
                    c.execute("select coalesce(sum(size), 0) * 8192 from sys.master_files "
                              "where database_id = db_id(?)", [database])
                    value = int(c.fetchone()[0] or 0)
                    shared = len(owners.get((server_key, db_key), set())) > 1
                    items.append({"name": database, "server": server, "bytes": value,
                                  "shared": shared})
                    if not shared:
                        total += value
                except Exception as exc:
                    complete = False
                    items.append({"name": database, "server": server, "bytes": None,
                                  "shared": len(owners.get((server_key, db_key), set())) > 1,
                                  "error": str(exc)})
                    warnings.append(f"Could not measure database {database} for {plant.name}: {exc}")
            by_id[plant.root_id] = {"bytes": total if complete else None, "items": items}
        shared = [dict(name=key[1], server=key[0], plants=len(ids))
                  for key, ids in owners.items() if len(ids) > 1]
        return {"by_plant": by_id, "shared_tablespaces": [],
                "shared_databases": shared, "warnings": warnings}

    def build_plan(self, plants: list[Plant], delete_site: bool = False) -> DeletionPlan:
        plan = DeletionPlan()
        ids_all = {i for p in plants for i in p.all_ids}
        cur = self.conn.cursor()
        cur.execute(f"select sp_rootitemid, username, data_base from {self.T('T_DB_Data')}")
        other_rows = [r for r in cur.fetchall() if str(r[0]) not in ids_all]
        users_elsewhere = {str(r[1]).lower() for r in other_rows if r[1]}
        dbs_elsewhere = {str(r[2]).lower() for r in other_rows if r[2]}

        for plant in plants:
            pp = PlantPlan(plant)
            rows = select_in(
                cur,
                f"select username, dbserver, data_base, syspassword, sysuid, sp_schema_type "
                f"from {self.T('T_DB_Data')} where sp_rootitemid in (%IN%)",
                plant.all_ids, "mssql")

            for r in rows:
                if r[1] and r[4]:
                    self.creds[str(r[1]).upper()] = (r[4], decrypt(r[3] or ""))

            # Handle databases across all project/plant rows
            dbs_seen = set()
            pid_paths: list[str] = []
            sel_paths: list[str] = []

            for r in rows:
                plant_db = r[2]
                server = r[1] or self.site.server
                if not plant_db or plant_db.lower() in dbs_seen:
                    continue
                dbs_seen.add(plant_db.lower())

                db_exists = False
                try:
                    c = self._connection(server).cursor()
                    c.execute("select db_id(?)", [plant_db])
                    row_db = c.fetchone()
                    db_exists = row_db is not None and row_db[0] is not None
                except (pyodbc.Error, BackendError) as e:
                    pp.warnings.append(f"Could not check database {plant_db} on {server}: {e}")

                if not db_exists:
                    pp.notes.append(f"Database {plant_db} is not available on {server}. Proceeding to delete physical files, logins, and site records.")
                else:
                    dbt = Target("database", plant_db, f"Database {plant_db}", server)
                    if plant_db.lower() in SYSTEM_DATABASES:
                        dbt.blocked, dbt.enabled = "System database", False
                    elif plant_db.lower() in dbs_elsewhere:
                        dbt.blocked, dbt.enabled = "Also used by a plant that is not selected", False
                    pp.targets.append(dbt)

                    try:
                        cs = self._connection(server).cursor()
                        cs.execute("select count(*) from sys.dm_exec_sessions where database_id = db_id(?)", [plant_db])
                        row_cs = cs.fetchone()
                        n = row_cs[0] if row_cs else 0
                        if n:
                            pp.warnings.append(f"{n} active session(s) on database {plant_db} - close applications before deleting.")
                    except Exception:
                        pass

                    if not dbt.blocked:
                        c = self._connection(server).cursor()
                        for u_name, srv_name, d_name, _p, _u, stype in rows:
                            if d_name != plant_db or (stype or "").upper() not in APP_SCHEMA_TYPES or not u_name:
                                continue
                            try:
                                for ids, bucket in ((PID_OPTION_IDS, pid_paths), (SEL_OPTION_IDS, sel_paths)):
                                    c.execute(f"select value from {qident_mssql(d_name)}.{qident_mssql(u_name)}"
                                              f".t_optionsetting where sp_id in ({','.join(['?'] * len(ids))})", ids)
                                    bucket.extend(x[0] for x in c.fetchall())
                            except pyodbc.Error as e:
                                pp.warnings.append(f"Could not read reference-data paths from {d_name}.{u_name}: {e}")

            # Check logins
            seen_logins: set[str] = set()
            for r in rows:
                login = r[0]
                server = r[1] or self.site.server
                if not login or login.lower() in seen_logins:
                    continue
                seen_logins.add(login.lower())
                try:
                    c = self._connection(server).cursor()
                    c.execute("select 1 from sys.server_principals where name = ?", [login])
                    if c.fetchone() is None:
                        continue
                except (pyodbc.Error, BackendError):
                    continue
                t = Target("login", login, f"Login {login}", server)
                if login.lower() in PROTECTED_LOGINS or login.startswith("##") or login.upper().startswith("NT "):
                    t.blocked, t.enabled = "Protected server login", False
                elif login.lower() in users_elsewhere:
                    t.blocked, t.enabled = "Also used by a plant that is not selected", False
                pp.targets.append(t)

            folders, notes = self.folder_targets(plant, pid_paths, sel_paths)
            pp.notes.extend(notes)
            pp.targets = folders + pp.targets
            pp.targets.append(Target("site_records", plant.name,
                                     f"Site records for {plant.name} (plant, projects, roles)"))
            plan.plants.append(pp)

        if delete_site:
            plan.delete_site = True
            plan.site_name = self.site.site_database or self.site.site_schema
            plan.site_targets = self.build_site_targets()

        return plan

    def build_site_targets(self) -> list[Target]:
        targets: list[Target] = []
        cur = self.conn.cursor()

        # Folders from T_SiteSetting
        try:
            cur.execute(f"select name, value from {self.T('t_sitesetting')} "
                        f"where name in ('Backup Location', 'Role Template Location')")
            for name, val in cur.fetchall():
                if val:
                    t = self.folder_target(val, f"Site folder ({name}): {val}", "site", kind="site_folder")
                    if t:
                        targets.append(t)
        except Exception:
            pass

        # Target smartplantv4.ini
        if self.site.ini_path:
            file_target = Target("site_file", self.site.ini_path,
                                 f"Site file: {self.site.ini_path}", category="site")
            reason, warning = assess_path(self.site.ini_path)
            file_target.note = warning
            if reason:
                file_target.blocked, file_target.enabled = reason, False
            targets.append(file_target)

        # Target site database
        if self.site.site_database:
            database = Target("database", self.site.site_database,
                              f"Site database {self.site.site_database}",
                              self.site.server, category="site")
            if self.site.site_database.lower() in PROTECTED_DATABASES:
                database.blocked, database.enabled = "Protected SQL Server system database", False
            targets.append(database)

        # Target site login
        if self.site.site_schema:
            login = Target("login", self.site.site_schema,
                           f"Site login {self.site.site_schema}",
                           self.site.server, category="site")
            if (login.name.lower() in PROTECTED_LOGINS or login.name.startswith("##")
                    or login.name.upper().startswith(("NT ", "NT\\"))):
                login.blocked, login.enabled = "Protected SQL Server login", False
            targets.append(login)

        return targets

    # ---- write side ----------------------------------------------------------------
    def execute(self, plan: DeletionPlan, ctx: RunContext) -> list[PlantResult]:
        results: list[PlantResult] = []
        cancelled = False
        for pp in plan.plants:
            res = PlantResult(pp.plant.name)
            results.append(res)
            if cancelled:
                res.status = "cancelled"
                continue
            ctx.info(f"=== Plant: {pp.plant.name} ===")
            try:
                self._execute_plant(pp, ctx, res)
            except RunCancelled:
                cancelled = True
                res.status = "cancelled"
                ctx.warn("Cancelled by user. Nothing further was changed for this plant.")
            except Exception as e:
                res.status = "failed"
                res.messages.append(str(e))
                ctx.error(f"Unexpected error: {e}")

        plant_failure = any(r.status in ("partial", "failed", "cancelled")
                            for r in results if not r.plant.startswith("Site ("))
        if not cancelled and plan.delete_site and not plant_failure:
            res_site = PlantResult(f"Site ({self.site.site_database or self.site.site_schema})")
            results.append(res_site)
            ctx.info(f"=== Site Deletion: {self.site.site_database or self.site.site_schema} ===")
            try:
                self._execute_site_deletion(plan, ctx)
                res_site.status = "ok"
                ctx.ok("Site deleted completely.")
            except RunCancelled:
                res_site.status = "cancelled"
                ctx.warn("Site deletion cancelled by user.")
            except Exception as e:
                res_site.status = "failed"
                res_site.messages.append(str(e))
                ctx.error(f"Site deletion error: {e}")
        elif plan.delete_site and plant_failure:
            message = "Site deletion skipped because one or more selected plants did not complete successfully."
            results.append(PlantResult(f"Site ({self.site.site_database or self.site.site_schema})",
                                       "partial", [message]))
            ctx.error(message)

        return results

    def _execute_plant(self, pp: PlantPlan, ctx: RunContext, res: PlantResult) -> None:
        if not self.delete_folders(pp, ctx):
            res.status = "partial"
            res.messages.append("Some folders could not be removed. The database, logins and "
                                "site records were NOT touched, so you can close the locking "
                                "program and run the plant again.")
            ctx.warn(res.messages[-1])
            return
        failures = 0
        for t in pp.active("database"):
            ctx.check()
            ctx.status(f"Dropping database {t.name}")
            try:
                self._drop_database(t, ctx)
                ctx.ok(f"Dropped database {t.name}")
            except Exception as e:
                failures += 1
                ctx.error(f"Could not drop database {t.name}: {e}")
                res.messages.append(f"Database {t.name}: {e}")
            ctx.advance()
        if failures:
            res.status = "partial"
            ctx.warn("Logins and site records were left in place because the database could not be dropped.")
            return
        for t in pp.active("login"):
            ctx.check()
            ctx.status(f"Dropping login {t.name}")
            try:
                self._drop_login(t, ctx)
                ctx.ok(f"Dropped login {t.name}")
            except Exception as e:
                failures += 1
                ctx.error(f"Could not drop login {t.name}: {e}")
                res.messages.append(f"Login {t.name}: {e}")
            ctx.advance()
        if failures:
            res.status = "partial"
            ctx.warn("Site records were left in place because one or more logins could not be dropped.")
            return
        for t in pp.active("site_records"):
            ctx.check()
            ctx.status("Removing site records")
            try:
                self._delete_site_records(pp.plant)
                ctx.ok(f"Removed {pp.plant.name} from the site")
            except Exception as e:
                res.status = "partial"
                res.messages.append(f"Site records: {e}")
                ctx.error(f"Site records were not removed (rolled back): {e}")
            ctx.advance()
        if failures and res.status == "ok":
            res.status = "partial"

    def _drop_database(self, t: Target, ctx: RunContext) -> None:
        cur = self._connection(t.server).cursor()
        q = qident_mssql(t.name)
        try:
            cur.execute("exec msdb.dbo.sp_delete_database_backuphistory @database_name = ?", [t.name])
        except pyodbc.Error:
            pass
        cur.execute(f"ALTER DATABASE {q} SET SINGLE_USER WITH ROLLBACK IMMEDIATE")
        ctx.info(f"Ended active connections to {t.name}")
        try:
            cur.execute(f"DROP DATABASE {q}")
        except pyodbc.Error:
            try:
                cur.execute(f"ALTER DATABASE {q} SET MULTI_USER")
            except pyodbc.Error:
                pass
            raise

    def _drop_login(self, t: Target, ctx: RunContext) -> None:
        cur = self._connection(t.server).cursor()
        cur.execute("select session_id from sys.dm_exec_sessions where login_name = ?", [t.name])
        for (sid,) in cur.fetchall():
            try:
                cur.execute(f"KILL {int(sid)}")
            except pyodbc.Error:
                pass
        cur.execute(f"DROP LOGIN {qident_mssql(t.name)}")

    def _delete_site_records(self, plant: Plant) -> None:
        ids = plant.all_ids
        cur = self.conn.cursor()
        self.conn.autocommit = False
        try:
            rows = select_in(cur, f"select sp_id from {self.T('t_roles')} where proj_id in (%IN%)", ids, "mssql")
            role_ids = [r[0] for r in rows]
            delete_in(cur, self.T("t_roleusers"), "role_id", role_ids, "mssql")
            delete_in(cur, self.T("t_rolerights"), "role_id", role_ids, "mssql")
            delete_in(cur, self.T("t_roles"), "proj_id", ids, "mssql")
            delete_in(cur, self.T("t_db_data"), "sp_rootitemid", ids, "mssql")
            delete_in(cur, self.T("t_rootitem"), "sp_id", ids, "mssql")
            self.conn.commit()
        except Exception:
            self.conn.rollback()
            raise
        finally:
            self.conn.autocommit = True

    def _execute_site_deletion(self, plan: DeletionPlan, ctx: RunContext) -> None:
        blocked = [t for t in plan.site_targets if t.blocked]
        if blocked:
            raise RuntimeError("Site deletion refused because protected/unsafe site targets exist: "
                               + "; ".join(f"{t.name}: {t.blocked}" for t in blocked))
        # Step 1: Remove site folders
        for t in plan.site_targets:
            if t.kind == "site_folder" and t.active:
                ctx.check()
                ctx.status(f"Deleting site folder {t.name}")
                if not self.delete_site_folder(t.name, ctx):
                    raise RuntimeError(f"Site folder deletion failed; refusing to continue: {t.name}")
                ctx.advance()

        # Step 2: Remove site ini file
        for t in plan.site_targets:
            if t.kind == "site_file" and t.active:
                ctx.check()
                ctx.status(f"Deleting site file {t.name}")
                if not self.delete_site_file(t.name, ctx):
                    raise RuntimeError(f"Site INI deletion failed; refusing to drop the site database: {t.name}")
                ctx.advance()

        # Step 3: Drop database
        for t in plan.site_targets:
            if t.kind == "database" and t.active:
                ctx.check()
                ctx.status(f"Dropping site database {t.name}")
                self._drop_database(t, ctx)
                ctx.ok(f"Dropped site database {t.name}")
                ctx.advance()

        # Step 4: Drop login
        for t in plan.site_targets:
            if t.kind == "login" and t.active:
                ctx.check()
                ctx.status(f"Dropping site login {t.name}")
                try:
                    self._drop_login(t, ctx)
                    ctx.ok(f"Dropped site login {t.name}")
                except Exception as e:
                    ctx.error(f"Could not drop site login {t.name}: {e}")
                    raise
                ctx.advance()
