from __future__ import annotations

import os
import time

import oracledb

from ..crypto import decrypt
from ..errors import BackendError, ManualConnectionRequired, RunCancelled
from ..models import (DeletionPlan, Plant, PlantPlan, PlantResult, Project,
                      RunContext, SiteConfig, Target)
from ..network import diagnose_connection_failure
from ..safety import assess_path, safe_oracle_ident
from ..sqlutil import delete_in, select_in
from .base import (APP_SCHEMA_TYPES, PID_OPTION_IDS, SEL_OPTION_IDS, Backend)

PROTECTED_SCHEMAS = {"SYS", "SYSTEM", "DBSNMP", "OUTLN", "XDB", "SYSMAN", "CTXSYS",
                     "MDSYS", "ORDSYS", "WMSYS", "ANONYMOUS", "APPQOSSYS", "AUDSYS"}
PROTECTED_TABLESPACES = {"SYSTEM", "SYSAUX", "USERS", "TEMP"}

_thick_dir: str | None = None
_thick_ready = False


def _err_code(e: Exception) -> int:
    try:
        return e.args[0].code
    except Exception:
        return 0


def _friendly(e: Exception) -> str:
    msg = str(e)
    if "DPY-3010" in msg or "DPY-3015" in msg:
        return (msg + "\n\nThis database version needs the Oracle client. Choose "
                "'Thick' as the Oracle driver on the Connect page and restart the app.")
    if "DPY-4026" in msg or "DPY-4027" in msg or "DPY-4000" in msg:
        return msg + "\n\nThe TNS alias could not be resolved. Check the tnsnames.ora folder."
    return msg


class OracleBackend(Backend):
    db_type = "ORACLE"

    def __init__(self, site: SiteConfig):
        super().__init__(site)
        self.conn = None
        self.remote: dict[str, object] = {}
        self.creds: dict[str, tuple[str, str]] = {}
        self.manual_servers: dict[str, dict[str, str]] = {}
        self.tns_dir: str | None = None
        self.mode = "thin"
        self.site_schema = safe_oracle_ident(site.site_schema)

    # ---- discovery & connection ----------------------------------------------------
    @staticmethod
    def discover_tns_dirs() -> list[str]:
        dirs: list[str] = []
        env = os.environ.get("TNS_ADMIN")
        if env:
            dirs.append(env)
        try:
            import winreg
        except ImportError:
            winreg = None
        if winreg:
            for flag in (winreg.KEY_WOW64_64KEY, winreg.KEY_WOW64_32KEY):
                try:
                    with winreg.OpenKey(winreg.HKEY_LOCAL_MACHINE, r"SOFTWARE\ORACLE", 0,
                                        winreg.KEY_READ | flag) as key:
                        i = 0
                        while True:
                            try:
                                sub = winreg.EnumKey(key, i)
                            except OSError:
                                break
                            i += 1
                            if "HOME" not in sub.upper():
                                continue
                            try:
                                with winreg.OpenKey(key, sub, 0, winreg.KEY_READ | flag) as sk:
                                    home = winreg.QueryValueEx(sk, "ORACLE_HOME")[0]
                                    dirs.append(os.path.join(home, "network", "admin"))
                            except OSError:
                                pass
                except OSError:
                    continue
        out: list[str] = []
        for d in dirs:
            if d not in out and os.path.isfile(os.path.join(d, "tnsnames.ora")):
                out.append(d)
        return out

    def _init_thick(self, tns_dir: str | None) -> None:
        global _thick_ready
        if _thick_ready:
            return
        kwargs = {}
        if tns_dir:
            kwargs["config_dir"] = tns_dir
            home = os.path.dirname(os.path.dirname(tns_dir))
            if os.path.isfile(os.path.join(home, "bin", "oci.dll")):
                kwargs["lib_dir"] = os.path.join(home, "bin")
        try:
            oracledb.init_oracle_client(**kwargs)
            _thick_ready = True
        except oracledb.Error as e:
            raise BackendError(
                f"The Oracle client could not be loaded ({e}). Its bitness must match this "
                f"application. Use the 'Thin' driver instead if possible.") from e

    def connect(self, manual: dict | None = None, mode: str = "thin") -> None:
        manual = manual or {}
        self.mode = mode
        host = (manual.get("host") or "").strip()
        port = (manual.get("port") or "1521").strip()
        service = (manual.get("service") or self.site.oracle_service or self.site.server).strip()
        tns_dir = (manual.get("tns_dir") or "").strip() or None

        if not tns_dir:
            found = self.discover_tns_dirs()
            tns_dir = found[0] if found else None

        # Check if TNS alias exists in discovered tnsnames.ora
        tns_has_alias = False
        if tns_dir and os.path.isfile(os.path.join(tns_dir, "tnsnames.ora")):
            try:
                with open(os.path.join(tns_dir, "tnsnames.ora"), "r", errors="ignore") as tf:
                    tns_content = tf.read().upper()
                    tns_has_alias = self.site.server.upper() in tns_content
            except Exception:
                tns_has_alias = False

        if not host and (not tns_dir or not tns_has_alias):
            raise ManualConnectionRequired(
                f"The Oracle alias '{self.site.server}' was not found in tnsnames.ora. "
                f"Enter the database host details to connect directly.")

        self.tns_dir = tns_dir
        if mode == "thick":
            self._init_thick(tns_dir)

        user = manual.get("user") or self.site.username
        password = manual.get("password") or self.site.password
        kwargs = {"user": user, "password": password}

        if host:
            kwargs["dsn"] = (f"(DESCRIPTION=(ADDRESS_LIST=(ADDRESS=(PROTOCOL=TCP)(HOST={host})"
                             f"(PORT={port})))(CONNECT_DATA=(SERVICE_NAME={service})))")
            self.manual_servers[self.site.server.upper()] = {
                "host": host, "port": port, "service": service
            }
        else:
            kwargs["dsn"] = self.site.server
            kwargs["config_dir"] = tns_dir

        try:
            self.conn = oracledb.connect(**kwargs)
            cur = self.conn.cursor()
            cur.execute(f"alter session set current_schema = {self.site_schema}")
        except oracledb.Error as e:
            target = host or self.site.server
            diag = diagnose_connection_failure(target, "ORACLE", int(port) if port.isdigit() else 1521, service)
            err_msg = f"{diag}\n\nTechnical error: {_friendly(e)}" if diag else _friendly(e)
            raise BackendError(err_msg) from e

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
        """Connection for a database server; the site connection when it is the same one."""
        if not server or server.upper() == self.site.server.upper():
            return self.conn
        key = server.upper()
        if key not in self.remote:
            uid, pwd = self.creds.get(key, (None, None))
            if not uid:
                uid, pwd = self.site.username, self.site.password

            manual_info = self.manual_servers.get(key)
            if not manual_info:
                site_manual = self.manual_servers.get(self.site.server.upper())
                if site_manual:
                    manual_info = {"host": site_manual["host"], "port": site_manual.get("port", "1521"), "service": server}

            try:
                if manual_info:
                    m_host = manual_info["host"]
                    m_port = manual_info.get("port", "1521")
                    m_svc = manual_info.get("service", server)
                    dsn = (f"(DESCRIPTION=(ADDRESS_LIST=(ADDRESS=(PROTOCOL=TCP)(HOST={m_host})"
                           f"(PORT={m_port})))(CONNECT_DATA=(SERVICE_NAME={m_svc})))")
                    self.remote[key] = oracledb.connect(user=uid, password=pwd, dsn=dsn)
                elif self.tns_dir:
                    self.remote[key] = oracledb.connect(user=uid, password=pwd, dsn=server,
                                                        config_dir=self.tns_dir)
                else:
                    raise BackendError(f"Cannot resolve secondary Oracle database '{server}' without host details or TNS.")
            except oracledb.Error as e:
                host_target = manual_info.get("host") if manual_info else server
                port_target = int(manual_info.get("port", 1521)) if manual_info else 1521
                diag = diagnose_connection_failure(host_target, "ORACLE", port_target, server)
                err_msg = f"{diag}\n\nTechnical error: {_friendly(e)}" if diag else _friendly(e)
                raise BackendError(f"Cannot connect to {server}: {err_msg}") from e
        return self.remote[key]

    # ---- read side -----------------------------------------------------------------
    def list_plants(self) -> list[Plant]:
        cur = self.conn.cursor()
        cur.execute("select sp_id, name, path, sp_parentrootitemid from T_RootItem")
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
        """Return allocated schema bytes and shared tablespace sizes."""
        cur = self.conn.cursor()
        rows = cur.execute("select sp_rootitemid, username, dbserver, syspassword, sysuid "
                           "from t_db_data").fetchall()
        by_id: dict[str, dict] = {}
        warnings: list[str] = []
        owner_ids: dict[str, set[str]] = {}
        owner_servers: dict[str, str] = {}
        for root_id, username, server, encrypted, uid in rows:
            if not username:
                continue
            owner = str(username).upper()
            server = server or self.site.server
            owner_ids.setdefault(owner, set()).add(str(root_id))
            owner_servers[owner] = server
            if uid:
                self.creds[server.upper()] = (uid, decrypt(encrypted or ""))

        owners_by_plant: dict[str, set[str]] = {}
        for plant in plants:
            for owner, ids in owner_ids.items():
                if set(plant.all_ids).intersection(ids):
                    owners_by_plant.setdefault(owner, set()).add(plant.root_id)
        shared_schemas = {owner: sorted(ids) for owner, ids in owners_by_plant.items()
                          if len(ids) > 1}

        for plant in plants:
            total = 0
            complete = True
            items: list[dict] = []
            for owner, ids in owner_ids.items():
                if not set(plant.all_ids).intersection(ids):
                    continue
                server = owner_servers[owner]
                try:
                    name = safe_oracle_ident(owner)
                    c = self._connection(server).cursor()
                    c.execute("select nvl(sum(bytes), 0) from dba_segments where owner = :1", [name])
                    value = int(c.fetchone()[0] or 0)
                    is_shared = len(owners_by_plant.get(owner, set())) > 1
                    if not is_shared:
                        total += value
                    items.append({"name": name, "server": server, "bytes": value,
                                  "shared": is_shared})
                except Exception as exc:
                    complete = False
                    items.append({"name": owner, "server": server, "bytes": None, "error": str(exc)})
                    warnings.append(f"Could not measure schema {owner} for {plant.name}: {exc}")
            by_id[plant.root_id] = {"bytes": total if complete else None, "items": items}

        shared: list[dict] = []
        try:
            c = self.conn.cursor()
            c.execute("select f.tablespace_name, f.bytes, s.owner_count "
                      "from (select tablespace_name, sum(bytes) bytes from dba_data_files "
                      "group by tablespace_name) f "
                      "join (select tablespace_name, count(distinct owner) owner_count "
                      "from dba_segments group by tablespace_name having count(distinct owner) > 1) s "
                      "on s.tablespace_name = f.tablespace_name")
            shared = [{"name": r[0], "bytes": int(r[1] or 0), "owners": int(r[2] or 0)}
                      for r in c.fetchall()]
        except Exception as exc:
            warnings.append(f"Could not read shared Oracle tablespaces: {exc}")
        shared_schema_rows = [{"name": owner, "plants": ids}
                              for owner, ids in shared_schemas.items()]
        return {"by_plant": by_id, "shared_tablespaces": shared,
                "shared_schemas": shared_schema_rows, "warnings": warnings}

    def _schemas_used_elsewhere(self, ids_being_deleted: set[str]) -> set[str]:
        cur = self.conn.cursor()
        cur.execute("select sp_rootitemid, username from t_db_data")
        return {str(u).upper() for rid, u in cur.fetchall()
                if u and str(rid) not in ids_being_deleted}

    def build_plan(self, plants: list[Plant], delete_site: bool = False) -> DeletionPlan:
        plan = DeletionPlan()
        ids_all = {i for p in plants for i in p.all_ids}
        elsewhere = self._schemas_used_elsewhere(ids_all)
        cur = self.conn.cursor()
        dropped_by_server: dict[str, set[str]] = {}

        for plant in plants:
            pp = PlantPlan(plant)
            rows = select_in(
                cur,
                "select username, dbserver, syspassword, sysuid, sp_schema_type "
                "from t_db_data where sp_rootitemid in (%IN%)", plant.all_ids, "oracle")
            pid_paths: list[str] = []
            sel_paths: list[str] = []
            seen_users: set[str] = set()

            for username, dbserver, syspwd, sysuid, stype in rows:
                if not username or username.upper() in seen_users:
                    continue
                seen_users.add(username.upper())
                server = dbserver or self.site.server
                if sysuid:
                    self.creds[server.upper()] = (sysuid, decrypt(syspwd or ""))
                try:
                    name = safe_oracle_ident(username)
                except ValueError:
                    pp.targets.append(Target("schema", username, f"Schema {username}", server,
                                             enabled=False, blocked="Invalid schema name"))
                    continue
                try:
                    c = self._connection(server).cursor()
                    c.execute("select username from all_users where username = :1", [name])
                    exists = c.fetchone() is not None
                except (oracledb.Error, BackendError) as e:
                    pp.warnings.append(f"Could not check schema {name} on {server}: {e}")
                    exists = False

                if not exists:
                    pp.notes.append(f"Schema {name} is not available on {server}. Proceeding to delete physical files and site info.")
                else:
                    t = Target("schema", name, f"Schema {name}", server)
                    if name in PROTECTED_SCHEMAS:
                        t.blocked, t.enabled = "Protected database account", False
                    elif name in elsewhere:
                        t.blocked, t.enabled = ("Also used by a plant that is not selected", False)
                    pp.targets.append(t)
                    if not t.blocked:
                        try:
                            c.execute("select count(*) from v$session where username = :1", [name])
                            row = c.fetchone()
                            n = row[0] if row else 0
                            if n:
                                pp.warnings.append(f"{n} active session(s) on schema {name} - close applications before deleting.")
                        except Exception:
                            pass
                        dropped_by_server.setdefault(server.upper(), set()).add(name)

                if exists and (stype or "").upper() in APP_SCHEMA_TYPES:
                    try:
                        c = self._connection(server).cursor()
                        for ids, bucket in ((PID_OPTION_IDS, pid_paths), (SEL_OPTION_IDS, sel_paths)):
                            c.execute(f"select value from {name}.t_optionsetting where sp_id in "
                                      f"({','.join(':' + str(i) for i in range(1, len(ids) + 1))})", ids)
                            bucket.extend(r[0] for r in c.fetchall())
                    except oracledb.Error as e:
                        pp.warnings.append(f"Could not read reference-data paths from {name}: {e}")

            folders, notes = self.folder_targets(plant, pid_paths, sel_paths)
            pp.notes.extend(notes)
            pp.targets = folders + pp.targets
            pp.targets.append(Target("site_records", plant.name,
                                     f"Site records for {plant.name} (plant, projects, roles)"))
            plan.plants.append(pp)

        plan.tablespaces = self._plan_tablespaces(dropped_by_server)

        if delete_site:
            plan.delete_site = True
            plan.site_name = self.site.site_schema
            plan.site_targets = self.build_site_targets()

        return plan

    def build_site_targets(self) -> list[Target]:
        targets: list[Target] = []
        cur = self.conn.cursor()

        # Query T_SiteSetting for folder paths
        try:
            cur.execute(f"select name, value from {self.site_schema}.t_sitesetting "
                        f"where name in ('Backup Location', 'Role Template Location')")
            for name, val in cur.fetchall():
                if val:
                    t = self.folder_target(val, f"Site folder ({name}): {val}", "site", kind="site_folder")
                    if t:
                        targets.append(t)
        except Exception:
            pass

        # Target smartplantv4.ini file
        if self.site.ini_path:
            file_target = Target("site_file", self.site.ini_path,
                                 f"Site file: {self.site.ini_path}", category="site")
            reason, warning = assess_path(self.site.ini_path)
            file_target.note = warning
            if reason:
                file_target.blocked, file_target.enabled = reason, False
            targets.append(file_target)

        # Target dictionary and site schemas
        if self.site.dictionary_schema:
            dictionary = Target("schema", self.site.dictionary_schema,
                                f"Dictionary schema {self.site.dictionary_schema}",
                                self.site.server, category="site")
            if self.site.dictionary_schema.upper() in PROTECTED_SCHEMAS:
                dictionary.blocked, dictionary.enabled = "Protected Oracle system schema", False
            targets.append(dictionary)
        site_schema = Target("schema", self.site.site_schema,
                             f"Site schema {self.site.site_schema}",
                             self.site.server, category="site")
        if self.site.site_schema.upper() in PROTECTED_SCHEMAS:
            site_schema.blocked, site_schema.enabled = "Protected Oracle system schema", False
        targets.append(site_schema)

        return targets

    def _plan_tablespaces(self, dropped_by_server: dict[str, set[str]]) -> list[Target]:
        out: list[Target] = []
        for server_key, schemas in dropped_by_server.items():
            if not schemas:
                continue
            server = next((s for s in [self.site.server] + list(self.creds)
                           if s.upper() == server_key), server_key)
            try:
                c = self._connection(server).cursor()
                names = sorted(schemas)
                binds = ",".join(f":{i}" for i in range(1, len(names) + 1))
                c.execute(f"select distinct tablespace_name from dba_segments where owner in ({binds})", names)
                perm = [r[0] for r in c.fetchall() if r[0]]
                c.execute(f"select distinct temporary_tablespace from dba_users where username in ({binds})", names)
                temp = [r[0] for r in c.fetchall() if r[0]]
                protected = set(PROTECTED_TABLESPACES)
                c.execute("select property_value from database_properties where property_name in "
                          "('DEFAULT_PERMANENT_TABLESPACE','DEFAULT_TEMP_TABLESPACE')")
                protected.update(r[0] for r in c.fetchall())
                c.execute("select tablespace_name from dba_tablespaces where contents = 'UNDO'")
                protected.update(r[0] for r in c.fetchall())
            except oracledb.Error as e:
                out.append(Target("tablespace", "(unavailable)", "Tablespaces could not be evaluated",
                                  server, enabled=False, blocked=f"Needs DBA views access: {e}"))
                continue
            for kind, items in (("tablespace", perm), ("temp_tablespace", temp)):
                for ts in items:
                    t = Target(kind, ts, f"{'Temporary tablespace' if kind == 'temp_tablespace' else 'Tablespace'} {ts}",
                               server, enabled=False,
                               note="Dropped only if no other schema still uses it")
                    if ts.upper() in {p.upper() for p in protected}:
                        t.blocked = "Database default / system tablespace"
                    out.append(t)
        return out

    # ---- write side ----------------------------------------------------------------
    def execute(self, plan: DeletionPlan, ctx: RunContext) -> list[PlantResult]:
        results: list[PlantResult] = []
        dropped: dict[str, set[str]] = {}
        cancelled = False
        for pp in plan.plants:
            res = PlantResult(pp.plant.name)
            results.append(res)
            if cancelled:
                res.status = "cancelled"
                continue
            ctx.info(f"=== Plant: {pp.plant.name} ===")
            try:
                self._execute_plant(pp, ctx, res, dropped)
            except RunCancelled:
                cancelled = True
                res.status = "cancelled"
                ctx.warn("Cancelled by user. Nothing further was changed for this plant.")
            except Exception as e:
                res.status = "failed"
                res.messages.append(str(e))
                ctx.error(f"Unexpected error: {e}")

        if not cancelled:
            try:
                self._drop_tablespaces(plan, ctx, dropped)
            except RunCancelled:
                ctx.warn("Cancelled by user before tablespaces were processed.")

        plant_failure = any(r.status in ("partial", "failed", "cancelled")
                            for r in results if not r.plant.startswith("Site ("))
        if not cancelled and plan.delete_site and not plant_failure:
            res_site = PlantResult(f"Site ({self.site.site_schema})")
            results.append(res_site)
            ctx.info(f"=== Site Deletion: {self.site.site_schema} ===")
            try:
                self._execute_site_deletion(plan, ctx)
                res_site.status = "ok"
                ctx.ok(f"Site {self.site.site_schema} deleted completely.")
            except RunCancelled:
                res_site.status = "cancelled"
                ctx.warn("Site deletion cancelled by user.")
            except Exception as e:
                res_site.status = "failed"
                res_site.messages.append(str(e))
                ctx.error(f"Site deletion error: {e}")
        elif plan.delete_site and plant_failure:
            message = "Site deletion skipped because one or more selected plants did not complete successfully."
            results.append(PlantResult(f"Site ({self.site.site_schema})", "partial", [message]))
            ctx.error(message)

        return results

    def _execute_plant(self, pp: PlantPlan, ctx: RunContext, res: PlantResult, dropped) -> None:
        if not self.delete_folders(pp, ctx):
            res.status = "partial"
            res.messages.append("Some folders could not be removed. Database objects and site "
                                "records were NOT touched, so you can close the locking program "
                                "and run the plant again.")
            ctx.warn(res.messages[-1])
            return
        failures = 0
        for t in pp.active("schema"):
            ctx.check()
            ctx.status(f"Dropping schema {t.name}")
            try:
                self._drop_schema(t, ctx)
                dropped.setdefault(t.server.upper(), set()).add(t.name)
                ctx.ok(f"Dropped schema {t.name}")
            except Exception as e:
                failures += 1
                ctx.error(f"Could not drop schema {t.name}: {e}")
                res.messages.append(f"Schema {t.name}: {e}")
            ctx.advance()
        if failures:
            res.status = "partial"
            ctx.warn("Site records were left in place because a schema could not be dropped.")
            return
        for t in pp.active("site_records"):
            ctx.check()
            ctx.status("Removing site records")
            try:
                self._delete_site_records(pp.plant, ctx)
                ctx.ok(f"Removed {pp.plant.name} from the site")
            except Exception as e:
                res.status = "partial"
                res.messages.append(f"Site records: {e}")
                ctx.error(f"Site records were not removed (rolled back): {e}")
            ctx.advance()

    def _drop_schema(self, t: Target, ctx: RunContext) -> None:
        name = safe_oracle_ident(t.name)
        conn = self._connection(t.server)
        cur = conn.cursor()
        for attempt in range(4):
            try:
                cur.execute(f"DROP USER {name} CASCADE")
                return
            except oracledb.DatabaseError as e:
                code = _err_code(e)
                if code == 1918:      # user does not exist
                    return
                if code == 1940 and attempt < 3:   # user is connected
                    ctx.warn(f"{name} has open sessions - ending them")
                    self._kill_sessions(cur, name, ctx)
                    time.sleep(2)
                    continue
                raise

    @staticmethod
    def _kill_sessions(cur, schema: str, ctx: RunContext) -> None:
        cur.execute("select sid, serial# from v$session where username = :1", [schema.upper()])
        for sid, serial in cur.fetchall():
            try:
                cur.execute(f"ALTER SYSTEM KILL SESSION '{int(sid)},{int(serial)}' IMMEDIATE")
            except oracledb.DatabaseError as e:
                ctx.warn(f"Could not end session {sid},{serial}: {e}")

    def _delete_site_records(self, plant: Plant, ctx: RunContext) -> None:
        ids = plant.all_ids
        cur = self.conn.cursor()
        try:
            rows = select_in(cur, "select sp_id from t_roles where proj_id in (%IN%)", ids, "oracle")
            role_ids = [r[0] for r in rows]
            delete_in(cur, "t_roleusers", "role_id", role_ids, "oracle")
            delete_in(cur, "t_rolerights", "role_id", role_ids, "oracle")
            delete_in(cur, "t_roles", "proj_id", ids, "oracle")
            delete_in(cur, "t_db_data", "sp_rootitemid", ids, "oracle")
            delete_in(cur, "t_rootitem", "sp_id", ids, "oracle")
            self.conn.commit()
        except Exception:
            self.conn.rollback()
            raise

    def _drop_tablespaces(self, plan: DeletionPlan, ctx: RunContext, dropped) -> None:
        for t in plan.tablespaces:
            if not t.active:
                continue
            ctx.check()
            ctx.status(f"Checking tablespace {t.name}")
            try:
                ts = safe_oracle_ident(t.name)
                cur = self._connection(t.server).cursor()
                mine = dropped.get(t.server.upper(), set())
                if t.kind == "tablespace":
                    cur.execute("select distinct owner from dba_segments where tablespace_name = :1", [ts])
                    others = {r[0].upper() for r in cur.fetchall()} - mine
                    cur.execute("select username from dba_users where default_tablespace = :1", [ts])
                    others |= {r[0].upper() for r in cur.fetchall()} - mine
                else:
                    cur.execute("select username from dba_users where temporary_tablespace = :1", [ts])
                    others = {r[0].upper() for r in cur.fetchall()} - mine
                if others:
                    ctx.warn(f"Kept {ts}: still used by {', '.join(sorted(others)[:5])}")
                else:
                    cur.execute(f"DROP TABLESPACE {ts} INCLUDING CONTENTS AND DATAFILES CASCADE CONSTRAINTS")
                    ctx.ok(f"Dropped tablespace {ts}")
            except Exception as e:
                ctx.error(f"Could not drop tablespace {t.name}: {e}")
            ctx.advance()

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
                    raise RuntimeError(f"Site INI deletion failed; refusing to drop site schemas: {t.name}")
                ctx.advance()

        # Step 3: Drop schemas (dictionary first, then site schema)
        schemas_to_drop = [t for t in plan.site_targets if t.kind == "schema" and t.active]
        # Ensure dictionary schema is dropped before site schema
        schemas_to_drop.sort(key=lambda t: 0 if self.site.dictionary_schema and t.name.upper() == self.site.dictionary_schema.upper() else 1)

        for t in schemas_to_drop:
            ctx.check()
            ctx.status(f"Dropping site schema {t.name}")
            try:
                self._drop_schema(t, ctx)
                ctx.ok(f"Dropped site schema {t.name}")
            except Exception as e:
                ctx.error(f"Could not drop site schema {t.name}: {e}")
                raise
                raise
            ctx.advance()
