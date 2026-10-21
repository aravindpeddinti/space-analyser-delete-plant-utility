# Delete Plant Utility v4.5 - source analysis

**Internal use only.** This document describes an internal administration tool.

Scope: static reading of `Delete Plant Utility.py` (1,011 lines), `.spec`, `version.txt`,
`README.txt` and the build command. I did not have an Oracle or SQL Server site to run it against,
so behaviour described below is from reading the code, not from running it.

---

## 1. Current workflow

1. Banner, then "Press anything to Continue" (typing `exit` quits).
2. Ask for the `smartplantv4.ini` path (file or folder). The file is UTF-16 and is read by **fixed line
   number**: line 3 = DB type, 4 = SQL Server name, 5 = Oracle instance *or* SQL site database,
   6 = site schema, 8 = admin user, 9 = encrypted password. The password is decoded with the
   Smart Engineering Manager codec (`decrypt`).
3. Connect as the site administrator.
4. List root plants (`T_RootItem` where parent is `'0'` or null) and ask which to delete
   (numbers, comma list, or `all`).
5. For each plant: collect project ids, find schemas/database, find reference-data paths, print what
   will go, confirm (single plant only), delete folders, drop database objects, remove site records.
6. Loop back to step 1 so the tool can be reused without restarting.

## 2. Business logic worth preserving

| Rule | Where |
|---|---|
| A "plant" is a root item; its projects are children (`sp_parentrootitemid`). | both branches |
| Schemas/databases come from `T_DB_DATA` rows for plant + project ids. | both |
| Reference-data folders come from `T_OPTIONSETTING` in the SPEL / SPPID schemas, using two fixed lists of option ids (11 for P&ID, 27 for Electrical). | both |
| Folder order: reference data, project folders, plant folder. Empty parents are then removed up the tree. | `delfolder` |
| Active connections to the plant are ended so it can be dropped. | both |
| Site cleanup: `T_ROOTITEM`, `T_DB_DATA`, `T_ROLES`, `T_ROLEUSERS`, `T_ROLERIGHTS`, in one transaction. | both |
| SQL Server: if the plant database is already gone, still remove folders, logins and site rows. | SQL |
| Oracle: optionally drop the tablespaces the schemas used. | Oracle |
| Credentials for a plant's own DB server come from `T_DB_DATA.sysuid/syspassword`. | both |

## 3. Oracle-specific functions

`find_oracle_clients` (registry, 32-bit view), `get_tnsnames_path`, `oracledb.init_oracle_client`
(thick mode), `checkandkillsession` (`v$session`, `ALTER SYSTEM KILL SESSION`), `DROP USER ... CASCADE`
with ORA-01940 retry, `all_users` existence check, `get_affiliated_tablespaces` (`dba_segments`),
`get_all_users_for_tablespaces`, `purge_recyclebin`, `drop_unused_tablespaces`,
`get_temp_tablespaces_for_schemas` (`dba_users`), `is_temp_tablespace_in_use` (`v$sort_usage`),
`drop_unused_temp_tablespaces`, `alter session set current_schema`.

## 4. SQL Server-specific functions

`sqlcon` (ODBC driver fallback 11, 17, 18), three-part names `[db].[schema].[table]`, `db_id()` check,
`msdb.dbo.sp_delete_database_backuphistory`, `ALTER DATABASE ... SET SINGLE_USER WITH ROLLBACK IMMEDIATE`,
`DROP DATABASE`, `sys.server_principals`, `DROP LOGIN`, switching `autocommit` for the site transaction.

## 5. Plant selection process

`askuserplants()` accepts `exit`, `all`, or comma-separated digits. Numbers index the plant list.
Single plant: details are shown and a 1/0 prompt appears. Several plants or `all`: **no prompt at all**
(`userconfirmation = 1` is hard-coded).

## 6. Deletion process (order of side effects)

reference folders -> project folders -> plant folder -> drop schemas (Oracle) / database (SQL) ->
drop logins (SQL) -> delete site rows -> optional tablespaces (Oracle).
Everything except the final site-row delete is irreversible and not transactional.

## 7. Existing validations

ini path exists and ends with `smartplantv4.ini`; plant input is numeric / `all` / `exit`; confirm accepts
only 1 or 0; `tnsnames.ora` must exist; schema must exist in `all_users` before it is dropped; `db_id()`
must be non-null before a database is dropped; ref paths must exist; folder delete checks existence;
parent pruning stops at the first non-empty folder; tablespace drop skips tablespaces still owned by
other schemas; site-row delete is rolled back on error.

## 8. Safety risks and where accidental deletion can happen

**Critical**

| # | Risk | Evidence |
|---|---|---|
| 1 | Multi-plant and `all` delete everything with no confirmation. | `userconfirmation = 1` |
| 2 | Entering `0` selects the **last** plant (`plantnamelist[-1]`); out-of-range numbers crash mid-run; `1,1` queues the same plant twice. | `plantnamelist[plantselection-1]` |
| 3 | Reference-data deletion removes the **common ancestor** of all paths, even if it also holds other data, and the fallback `commonpath(paths)` ignores the safety check. Nothing checks whether another plant shares those folders. | `get_drive_commonpaths` (also removes from a list while iterating it) |
| 4 | Folder paths come straight from the database. Empty, relative, drive-root or system paths are not rejected. Deleted with PowerShell `Remove-Item -Recurse -Force -ErrorAction SilentlyContinue`; the path is pasted inside a quoted command string. | `delfolder` |
| 5 | Folder failures are silent, then the database and site rows are dropped anyway. A locked folder leaves orphans, and the reference paths (which live in the database) are lost for a retry. | `SilentlyContinue`, no result check |
| 6 | Oracle temp tablespaces are dropped when `v$sort_usage` is empty. That does not mean no other user is assigned to it, so a shared or default `TEMP` could be dropped. System tablespaces are not protected. | `is_temp_tablespace_in_use` |
| 7 | A schema/login registered to another plant's `T_DB_DATA` row is dropped without checking. | no cross-check |
| 8 | Active sessions are killed / `SINGLE_USER ROLLBACK IMMEDIATE` with no warning in the UI (only in the README). | by design |
| 9 | SQL is built with f-strings: unquoted identifiers, Python tuple repr as `IN (...)`, empty tuples give invalid SQL `in ()`. | everywhere |

**High: defects in the supplied snapshot** (found by reading; please confirm which build is in production)

| # | Defect |
|---|---|
| 10 | Typing `exit` at the first prompt raises `NameError` (`exitapp` reads `conn` / `conn1` before they exist). |
| 11 | Oracle, no client found: `if oracle_home:` raises `NameError`, so the manual host/port/service path is unreachable. |
| 12 | Oracle: `difftbspacelist,_ = ... if cond else None` raises `TypeError` whenever no schema lives on another server; `cursor1` / `conn1` are used when never created. This looks like half-finished v5 work. |
| 13 | Oracle: schemas on another server are detected but never dropped (the drop loop uses only `dropschemaslist`). |
| 14 | SQL Server: `plantprojschemalist` holds username strings, but `i[1]`, `i[3]`, `i[4]` are then used as columns, so they pick **characters of the username** as the server and admin user. |
| 15 | Empty role list gives `in ()` (invalid) or `rolessp_idlist[0]` `IndexError`, after the database is already dropped. Site rows then remain. |
| 16 | SQL Server: `DROP LOGIN` is unguarded in the "database exists" branch; one failure aborts before site rows are removed. |
| 17 | Values are split with `split('=')[1]`, so an encrypted password containing `=` is truncated and fails to decode. |
| 18 | The Oracle tablespace drop only happens for tablespaces in `ts_users`; ones with no remaining segments are handled inconsistently. |

**Environment constraints**

- The Oracle path assumes a 32-bit Oracle client, which forces a 32-bit Python build.
- ODBC driver order starts at "Driver 11".
- 53 `time.sleep()` calls slow every run.

## 9. User interaction flow problems

Console only; no preview you can edit; numbers are the only way to pick; typing errors restart a prompt;
progress is a rewritten single line; no log; no way to deselect one folder; results are not summarised.

## 10. How v5 responds

| Risk | v5 behaviour |
|---|---|
| 1, 2 | Check-box list; no index arithmetic; typed confirmation always required (plant name, or `DELETE n PLANTS`) plus an acknowledgement. |
| 3 | The common folder is used only if it holds nothing but the reference data; otherwise each folder is deleted on its own and a note explains why. |
| 4 | Paths rejected: empty, relative, drive/share root, symlink, system folders, the app's own folder. Deleted with Python (`shutil.rmtree`), no shell. |
| 5 | If any folder cannot be fully deleted, **database, logins and site rows are left untouched** for that plant so it can be re-run. |
| 6 | System, default and undo tablespaces are blocked; a tablespace is dropped only if no other user owns segments in it or has it as default/temp. Tablespace drop is opt-in. |
| 7 | Schemas, logins and databases that appear for another plant are blocked. Protected accounts (SYS, SYSTEM, `sa`, `##...`) are blocked. |
| 8 | The plan lists everything first; sessions are ended only inside the confirmed run, and logged. |
| 9 | Bind parameters for values; validated or bracket-quoted identifiers; chunked `IN` lists; empty lists skipped. |
| 10-18 | Rewritten without the broken branches; remote Oracle schemas are dropped over their own connection; SQL uses real row columns; ini parsed on the first `=`. |
| UX | Four-step window, dry-run plan, per-item opt-out, live log, cancel between steps, audit log, final summary. |

## 11. Decisions you should know about

- **64-bit app, Oracle thin mode by default.** PySide6 has no 32-bit Windows build, so the app cannot load a
  32-bit Oracle client. Thin mode needs no client and still reads `tnsnames.ora`. It supports Oracle 12.1 and
  later; for older servers use the "Thick" option with a 64-bit client. If you must stay 32-bit, port the UI to
  PyQt5 or Tkinter; the `app/core` package has no UI imports.
- **64-bit ODBC driver required** for SQL Server (`ODBC Driver 17/18`). 32-bit-only machines will not work.
- Reference-data folders found only through paths the tool cannot see (other servers) are shown as
  "not found" and skipped.
- Tablespace drops moved to the end of the run and default to unticked.
