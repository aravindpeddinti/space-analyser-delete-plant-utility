# Delete Plant Utility — Headless API and CLI Reference

Version 5.0 source package · Internal use only

This guide describes using the deletion engine without starting the PyQt GUI. The supported integration surface is a **Python API** plus a JSON-producing **CLI** (`python -m app.cli`). A DLL is not the right first interface for this Python application: using the Python API keeps the same backend and data model, while the CLI is a stable process boundary for other languages. A .NET/C/C++ caller can start the CLI and consume JSON; a native DLL/COM wrapper is not included.

> **Danger:** these operations can permanently delete plant/project folders, Oracle schemas and tablespaces, SQL Server databases and logins, SEM catalog rows, site files, and an entire site. First run `plan`, inspect the complete output and validate in a non-production environment. Never pass automatic confirmation from an unattended caller without a separately approved change-control gate.

## 1. Runtime and setup

Run commands from this project directory (the directory containing `app/`):

```powershell
python -m pip install -r requirements.txt
python -m app.cli --help
```

Requirements: supported Python (see `requirements.txt`), Oracle `oracledb` Thin mode or a configured Oracle client for Thick mode, and for SQL Server an installed Microsoft ODBC driver supported by `pyodbc`. Windows is required for process-list safety verification. The INI is the source of DB connection settings; do not put passwords on a command line. The CLI deliberately exposes no password argument.

The source code and UI require PyQt5 for the GUI. The headless API and CLI import `app.core` only; they do not instantiate a Qt application or show windows.

## 2. CLI

Global options come before the command:

```text
python -m app.cli --ini <smartplantv4.ini-or-folder> [--oracle-mode thin|thick]
                  [--oracle-host HOST] [--oracle-port 1521] [--oracle-service SERVICE]
                  <list|inventory|plan|execute> [command options]
```

`--ini` accepts the actual `smartplantv4.ini` path or its containing directory. Oracle connects through the TNS alias by default. For manual Oracle fallback, provide `--oracle-host`; optionally set port and service. Credentials still come from the INI. SQL Server uses the server/database and encrypted credentials read from the INI.

### List the site contents (read only)

```powershell
python -m app.cli --ini "D:\SEM\smartplantv4.ini" list
```

Output: JSON object with `ok` and `plants`; each plant has `name`, `root_id`, `path`, and a `projects` array containing each project's `name`, `root_id`, and `path`.

### Read plant storage (read only)

```powershell
python -m app.cli --ini "D:\SEM\smartplantv4.ini" inventory
```

This returns the site identity, plant folder paths, Oracle schema bytes or SQL Server database-file bytes, shared Oracle schemas/tablespaces, shared SQL Server databases, and any measurement warnings. Shared storage is listed separately rather than assigned to each plant.

### Build and review a read-only plan

```powershell
python -m app.cli --ini "D:\SEM\smartplantv4.ini" plan --plant "Plant A"
python -m app.cli --ini "D:\SEM\smartplantv4.ini" plan --plant "Plant A" --plant "Plant B"
python -m app.cli --ini "D:\SEM\smartplantv4.ini" plan --delete-site
```

The plan output includes `confirmation_phrase` and a `plan` with plant/project metadata, warnings, notes, all targets and their `enabled`, `blocked`, `active` and `note` fields, plus `total_steps`. Blocked targets must never be executed. Oracle tablespaces are disabled by default; `--include-tablespaces` opts eligible targets in (still subject to backend ownership/default tablespace checks).

An empty plant selection is rejected unless `--delete-site` was explicitly supplied. For a populated site, `--delete-site` automatically includes **all discovered plants and projects** before the site; a subset is rejected. For a genuinely empty site it means site-only deletion. Review every path/name and safety block before proceeding.

### Execute (irreversible)

Execution rebuilds the plan from the live database; it does not trust a plan JSON file from an earlier run. Supply the exact phrase returned by the immediately preceding plan, the same selection and flags, and the explicit acknowledgement:

```powershell
python -m app.cli --ini "D:\SEM\smartplantv4.ini" execute --plant "Plant A" `
  --confirm "Plant A" --acknowledge
```

Multiple plants:

```powershell
python -m app.cli --ini "D:\SEM\smartplantv4.ini" execute --plant "Plant A" --plant "Plant B" `
  --confirm "DELETE 2 PLANTS" --acknowledge
```

Whole-site operation (confirmation phrase depends on how many plants are selected):

```powershell
python -m app.cli --ini "D:\SEM\smartplantv4.ini" execute --delete-site `
  --confirm "DELETE SITE AND 3 PLANTS" --acknowledge
```

For an empty site, use `--delete-site --confirm "DELETE SITE <site name>" --acknowledge`. Use the exact phrase from `plan` rather than constructing it yourself. Omit `--delete-site` unless full-site destruction is explicitly intended. To remove eligible Oracle tablespaces, pass `--include-tablespaces` identically to both plan and execute.

Exit codes: `0` success, `1` validation/connection/safety/command error, `2` execution returned a partial or failed result. Stdout is JSON; progress/audit log messages are written to stderr. Errors are emitted as `{"ok": false, "error": "..."}`. Successful execution returns `ok`, the rebuilt plan, and per-item `results` (`plant`, `status`, `messages`). Status is `ok`, `partial`, `failed`, or `cancelled`.

## 3. Python API

Import and use a single context-managed session. The session owns its backend connection and any plan is valid only in the session that created it.

```python
from app.core.headless import UtilitySession, plan_to_dict, confirmation_phrase

with UtilitySession(r"D:\SEM\smartplantv4.ini") as sem:
    plants = sem.list_plants()                 # JSON-safe list[dict]
    plan = sem.build_plan(["Plant A"])         # read-only DeletionPlan
    print(plan_to_dict(plan))                   # inspect before proceeding
    phrase = confirmation_phrase(plan)          # exact required phrase

    # Only call after an independent approval/review step:
    results = sem.execute(plan, confirmation=phrase, acknowledge=True)
```

Oracle manual connection and thick mode:

```python
with UtilitySession(
    r"D:\SEM\smartplantv4.ini",
    oracle_mode="thin",  # or "thick" when Oracle Client is installed and bitness matches
    manual_connection={"host": "dbhost", "port": "1521", "service": "SEM"},
) as sem:
    plants = sem.list_plants()
```

For SQL Server, `manual_connection` is ignored; use the INI's server and database values.

### Public API signatures

- `UtilitySession(ini_path, *, oracle_mode="thin", manual_connection=None, logger=None)`: resolve/read INI, create the backend, connect, and start the local audit session. `logger(level, message)` is an optional callback; messages are also written to the existing local audit log. Passwords are excluded from the public plan and not printed by this API.
- `list_plants() -> list[dict]`: discover plants and child projects. Read only.
- `build_plan(plant_names=None, *, delete_site=False, include_tablespaces=False) -> DeletionPlan`: rebuild selection from live data and build deletion targets. Names are case-insensitive and must exactly match discovered plant names. Empty selection is legal only for explicit site deletion. `delete_site=True` automatically selects all discovered plants (and rejects a supplied subset); a truly empty site receives a site-only plan. Tablespaces remain disabled unless opted in.
- `plan_to_dict(plan) -> dict`: serialize only operational plan fields; no `SiteConfig` or credentials are included.
- `confirmation_phrase(plan) -> str`: produce the phrase expected by the GUI-equivalent confirmation gate.
- `execute(plan, *, confirmation, acknowledge=False, is_cancelled=None) -> list[dict]`: execute only a plan made by this same live session, after exact phrase and explicit acknowledgement. Rechecks blocking SEM processes immediately before execution; if process enumeration is unavailable, execution is refused. `is_cancelled` is a callback polled by backend operations between steps; it cannot interrupt a database DDL command or an in-progress recursive filesystem deletion.
- `close()` / context manager: closes backend connections. Always use `with` where possible.

`DeletionPlan` is an in-memory Python object, not a durable authorization token. Do not deserialize plan JSON and execute it. CLI execution re-queries the database and re-plans. API callers should likewise build a fresh plan immediately before execution and present the plan to an authorized operator.

## 4. JSON plan/result schema

Plan root fields: `site_name: str`, `delete_site: bool`, `total_steps: int`, `plants: []`, `tablespaces: []`, `site_targets: []`.

Plant-plan fields: `name`, `root_id`, `path`, `projects[]`, `warnings[]`, `notes[]`, `targets[]`. Target fields: `kind`, `name`, `label`, `server`, `category`, `enabled`, `blocked`, `note`, `active`. `active` means enabled and not blocked. Kinds include `folder`, `schema`, `database`, `login`, `tablespace`, `temp_tablespace`, `site_records`, `site_folder`, and `site_file`.

Each execution result has `plant`, `status`, and `messages`. Results report completed steps and failures; a partial result may mean some irreversible actions already completed. Do not blindly rerun after a partial/failed run—re-list and re-plan first.

## 5. Safety behavior and limitations

The following controls are enforced by the current headless boundary and backend:

1. **Plan first:** listing and planning do not issue delete/drop operations. Review the generated JSON plan, including every target path and `blocked` reason.
2. **Explicit confirmation:** execution requires exact phrase match and `acknowledge=True` / `--acknowledge`. Site deletion requires explicit `delete_site` / `--delete-site`.
3. **Fresh process check:** configured SEM applications are checked directly before execution. Probe errors fail closed; non-Windows hosts cannot verify this and are refused.
4. **Folders before DB objects:** an incomplete plant folder deletion stops its database/login/site-record work.
5. **No cascading site wipe after plant errors:** Oracle and SQL Server site deletion is skipped if any selected plant is partial, failed, or cancelled.
6. **Site-folder and INI failure stops the wipe:** failure to delete a selected site folder or INI raises an error before dropping site schemas/database. Site INI deletion also applies normal path safety assessment.
7. **Site targets get protection:** Oracle system schemas and SQL Server system databases/protected logins are blocked, as are unsafe site INI paths.
8. **SQL Server login cleanup:** a plant's catalog rows are retained if an associated login cannot be dropped; failure to remove the site login fails the site wipe rather than being silently reported as success.
9. **Existing safeguards remain:** protected/shared resources, identifier validation, path safety, Oracle tablespace ownership/default checks and transactional site-record cleanup.

Important operational limitations:

- Directory deletion and database DDL are not transactional across the file system and database. A crash can leave a partial state; inspect results and rebuild the plan before another attempt.
- Existing SEM process-pattern configuration lives in `%LOCALAPPDATA%\DeletePlantUtility\blocking_apps.txt`; review it for your environment. A successful probe only checks configured process names.
- Connection/credential parsing follows the INI reader. Do not modify or reproduce the SEM password codec in integrations.
- No live production database testing is implied by the unit tests. Validate driver versions, permissions, cross-server routing and file access in a controlled non-production site first.
- `plan_to_dict` does not save a plan, and the CLI does not expose target-by-target toggling. The GUI remains the review surface when operators need per-target checkbox selection. The headless interface uses enabled, unblocked targets from the fresh backend plan.

## 6. Implementation map

- `app/core/headless.py`: reusable `UtilitySession`, phrase generation, plan serialization.
- `app/cli.py`: CLI argument validation and JSON I/O.
- `app/core/backends/oracle_backend.py`, `mssql_backend.py`: actual deletion and backend-specific safety/order rules.
- `app/core/backends/base.py`: shared folder/path deletion helpers.
- `app/core/models.py`: plan, target, result and cancellation data structures.
- `app/core/processes.py`: configured process matching and fail-closed strict process query.
- `app/core/ini_reader.py`: INI path resolution, parsing and credential decoding.

## 7. Test

From the project root:

```powershell
python -m unittest discover -s tests -v
```

Tests use fake database drivers; they do not authorize or validate a real deletion. A live environment smoke test should stop after `list` and `plan` until operational approval has been obtained.
