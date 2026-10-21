# Space Analyzer + Delete Plant Utility

This project combines Space Analyzer with SEM plant discovery and deletion. Space Analyzer finds `smartplantv4.ini` files on the one machine being scanned, groups plants by site, and shows each plant's folder size and database allocation separately. Oracle shared schemas/tablespaces and SQL Server databases referenced by multiple plants are identified separately to avoid assigning shared storage to one plant.

## Build and run the combined app

On Windows, install Python 3.10+ and the packages in `requirements.txt`, then run `BuildCombined.bat`. It creates `SpaceAnalyzer.exe` and the required `Delete Plant Utility CLI.exe` helper in the project folder. Keep both files together. Start Space Analyzer, scan the local computer or one selected VM, then choose **SEM plants**. Discovery and size lookup are read-only. To remove one plant, review the fresh deletion plan and type the exact confirmation phrase.

The scan searches fixed drives on the selected machine for `smartplantv4.ini`, skipping Windows protected folders and directory links. VM scanning uses the existing Space Analyzer remote access requirements. For remote SEM inventory or deletion, Space Analyzer copies the helper to the selected VM's Windows temporary folder and runs it there.

The sections below describe the Delete Plant Utility backend, command line interface, and safeguards.

---

# Delete Plant Utility v5.0

**Internal use only.** This Windows utility administers Hexagon Smart Engineering Manager (SEM) sites. It can permanently delete plants/projects, their folders, database objects and SEM catalog rows, and optionally an entire site. The application provides a PyQt5 GUI and a headless Python API/CLI for automation and integration.

## Choose an interface

- **GUI:** run `Delete Plant Utility.exe` or `python main.py`. The GUI provides per-target review and checkbox selection.
- **CLI:** run `python -m app.cli` from the project root. It provides `list`, `plan` and `execute` operations with JSON output.
- **Python integration:** import `UtilitySession` from `app.core.headless` and call the same backend without creating a Qt application.
- A native DLL is not provided. For a non-Python caller, invoke the CLI as a child process and consume its JSON. See `HEADLESS_API.md` for complete schemas, arguments, outputs and examples.

## Prerequisites

1. The operator/service identity needs access to the INI, plant/project/reference-data folders, and the relevant database servers.
2. Use a supported Python version and install `requirements.txt` when running from source.
3. Oracle needs `python-oracledb` (Thin mode, normally no Oracle client) or a compatible Oracle client for Thick mode. SQL Server requires an installed `pyodbc`-supported Microsoft ODBC driver.
4. Run destructive operations on Windows. The headless safety gate refuses execution if it cannot enumerate processes or if called on a non-Windows host.
5. Keep `smartplantv4.ini` credentials protected. The tool reads/decrypts the SEM values; do not copy passwords onto a command line or into source code.

## Critical safety precautions

- **Deletion is irreversible.** First run `list` and `plan`, inspect all folders/schemas/databases/logins/site records and every `blocked` reason, validate the plan against the approved change, and test in a non-production site.
- `plan` performs discovery and planning, not deletion. CLI `execute` makes a fresh connection and rebuilds the plan; it does not execute a previously saved JSON plan.
- Execution requires the exact `confirmation_phrase` returned by `plan`, plus the CLI `--acknowledge` flag or API `acknowledge=True`. Treat the confirmation phrase as an operator check, not as identity/approval authorization.
- `--delete-site` explicitly requests whole-site deletion. On a populated site, it includes **all discovered plants**; a partial selection is rejected. It is site-only only when the site is genuinely empty.
- Oracle tablespace drops are disabled by default and require `--include-tablespaces`; backend checks still protect default/system/shared/used tablespaces.
- Configured running SEM applications block headless execution. Process-list errors fail closed. Review `%LOCALAPPDATA%\DeletePlantUtility\blocking_apps.txt` for the right process patterns in your environment.
- Plant folders are removed before schemas/databases because their metadata can be needed to finish cleanup. If folder deletion fails, the backend stops that plant's later database and catalog work.
- Site deletion is skipped if any selected plant fails, is partial, or is cancelled. A site-folder or INI deletion failure stops the site wipe before database/schema drops. Protected/unsafe site targets refuse the wipe.
- Cross-layer file/database deletion is not transactional. A crash or database/filesystem error may leave a partial result. Re-list and rebuild the plan before attempting recovery; do not blindly repeat execution.
- The GUI's checkboxes allow target-level review. The headless CLI does not expose individual target toggles; it executes the enabled, unblocked targets from its freshly generated plan.

## CLI quick start

Open a terminal in the project root (where `app/` exists):

```powershell
python -m pip install -r requirements.txt
python -m app.cli --help
```

Global connection options go before the command:

```text
python -m app.cli --ini <smartplantv4.ini-or-folder> [--oracle-mode thin|thick]
                  [--oracle-host HOST] [--oracle-port 1521] [--oracle-service SERVICE]
                  <list|plan|execute> [options]
```

Examples:

```powershell
# Read-only plant/project discovery
python -m app.cli --ini "D:\SEM\smartplantv4.ini" list

# Read-only plan for one or several plants
python -m app.cli --ini "D:\SEM\smartplantv4.ini" plan --plant "Plant A"
python -m app.cli --ini "D:\SEM\smartplantv4.ini" plan --plant "Plant A" --plant "Plant B"

# Then, only after reviewing/approving the fresh plan, execute with the exact phrase it returned
python -m app.cli --ini "D:\SEM\smartplantv4.ini" execute --plant "Plant A" --confirm "Plant A" --acknowledge

# Explicit whole-site plan and execution (phrase depends on discovered plant count; copy it from plan JSON)
python -m app.cli --ini "D:\SEM\smartplantv4.ini" plan --delete-site
python -m app.cli --ini "D:\SEM\smartplantv4.ini" execute --delete-site --confirm "DELETE SITE AND 3 PLANTS" --acknowledge

# Oracle Thick mode or manual Oracle host/service fallback; credentials still come from the INI
python -m app.cli --ini "D:\SEM\smartplantv4.ini" --oracle-mode thick plan --plant "Plant A"
python -m app.cli --ini "D:\SEM\smartplantv4.ini" --oracle-host dbhost --oracle-port 1521 --oracle-service SEM list
```

Run `plan` immediately before `execute`; use the same INI, plant selection and flags. `--delete-site` automatically selects all plants. If Oracle tablespaces are deliberately in scope, pass `--include-tablespaces` to both plan and execute.

### CLI input/output contract

- `--ini`: path to `smartplantv4.ini` or its containing directory.
- `--oracle-mode`: `thin` (default) or `thick`.
- `--oracle-host`, `--oracle-port`, `--oracle-service`: optional manual Oracle connection settings; the user/password still come from INI. Not used by SQL Server.
- `list`: returns JSON `{ "ok": true, "plants": [...] }`. Each plant includes its name, root ID, path and projects.
- `plan`: accepts repeated `--plant`, optional `--delete-site`, and optional `--include-tablespaces`. Returns `confirmation_phrase` plus the plan, including target kind/name/label/server/category, enabled/blocked/note/active fields, warnings, notes and total step count.
- `execute`: same selection flags, exact `--confirm` phrase from the fresh plan and required `--acknowledge`. Returns rebuilt plan and result records (`plant`, `status`, `messages`).
- Stdout is JSON; progress/audit messages go to stderr. Exit code `0` means all returned results were `ok`, `1` is validation/connection/safety error, `2` indicates partial/failed/cancelled execution. A partial result may include already-completed irreversible steps.

## Python API quick start

```python
from app.core.headless import UtilitySession, plan_to_dict, confirmation_phrase

with UtilitySession(r"D:\SEM\smartplantv4.ini") as sem:
    plants = sem.list_plants()
    plan = sem.build_plan(["Plant A"])  # read-only
    review = plan_to_dict(plan)          # inspect/serialize without credentials
    print(review)

    # Only following an independent review/approval process:
    results = sem.execute(
        plan,
        confirmation=confirmation_phrase(plan),
        acknowledge=True,
    )
```

For site deletion use `sem.build_plan(delete_site=True)`; this selects all discovered plants and projects. A caller-supplied subset is rejected. For manual Oracle connection, pass `manual_connection={"host": "dbhost", "port": "1521", "service": "SEM"}`. Use `oracle_mode="thick"` only with a compatible installed Oracle client and matching process bitness. SQL Server uses the INI settings.

`UtilitySession` owns DB connections; use it as a context manager. Plans must be built and executed by the same live session, are consumed once, and are not serialized authorization tokens. The API writes to the established local audit log without writing passwords; an optional `logger(level, message)` callback can receive operational log events. `is_cancelled` can be passed to `execute`; cancellation is checked between backend steps, not during an active DDL or filesystem operation.

## Existing functional behavior

- Parses structured `[SITESCHEMA]`/`[DICTIONARY]` and legacy `smartplantv4.ini` layouts; DBA credentials are obtained from the encrypted INI entries.
- Supports Oracle TNS auto-discovery and manual host/port/service fallback, plus Oracle Thin/Thick modes.
- Supports SQL Server ODBC driver selection and cross-server/instance routing based on SEM database mappings.
- Builds a plan of plant, project, reference-data and site-level folders; schemas/databases/logins; catalog rows; and eligible Oracle tablespaces.
- Protects system schemas/databases/logins/tablespaces, shared resources used by unselected plants, unsafe paths, and open SEM processes.
- Follows folders → DB objects → logins → catalog rows → optional unused tablespaces → optional site wipe. Already-missing plant databases/schemas can be treated as unavailable while physical files and catalog cleanup proceed, according to backend behavior.
- Writes per-session audit logs under `%LOCALAPPDATA%\DeletePlantUtility\logs\`; process patterns are stored in `%LOCALAPPDATA%\DeletePlantUtility\blocking_apps.txt`.

## Tests and build

Run from the project root:

```powershell
python -m unittest discover -s tests -v
python tests/ui_smoke_stub.py
```

The unit suite uses fake database drivers and does not test a live SEM site. At the current handover update, 46 tests pass. Build the Windows GUI executable with `build_exe.bat`; for `--onedir`, distribute the full `dist\Delete Plant Utility` folder including `_internal`. The CLI/Python headless interface can be run from source with the configured Python and database drivers; this does not package a native DLL.

## Documentation

- `HANDOVER.md`: system purpose, architecture, database/backend flow, safety controls and maintenance map.
- `HEADLESS_API.md`: full CLI and programmatic API reference, JSON fields, examples and integration caveats.

## Version history

The `legacy_versions/` directory preserves earlier console utilities. v5.0 is the PyQt5 GUI/dual-database implementation with read-only planning, safety controls, multi-instance routing, site cleanup and the headless API/CLI. Consult `HANDOVER.md` for detailed technical constraints and changes.
