# Space Analyzer — Developer Handoff

**Version:** 3.3.0
**Language/Runtime:** C# (single source file), compiled against **.NET Framework 4.x** with the built-in Windows compiler. No NuGet, no external libraries.
**UI:** Windows Forms, custom-drawn controls.
**Audience of this doc:** the developer taking over maintenance.

---

## 1. What the utility does

A Windows desktop tool for finding and reclaiming disk space, on the local machine **and** on domain VMs. Main capabilities:

- Fast disk scan (reads the NTFS Master File Table directly — WizTree-style) with fallback to normal enumeration.
- Views: Cleanup Advisor, Biggest Folders (+ treemap), Biggest Files, File Types (donut), By User.
- Cleanup Advisor: known safe-to-clean locations (temp, Windows Update cache, browser caches, Recycle Bin, crash dumps, IIS logs, old .bak, hibernation, WinSxS component cleanup) with per-file review.
- Recycle Bin browser (local and per-VM) with restore/delete.
- Network VMs: deploy-and-run scanning of other domain machines over WMI + admin shares; per-VM tabs; remote delete/cleanup.
- Per-user analysis: ranks people by old data across Windows profiles and team folders.
- Scheduled maintenance: a Windows Scheduled Task runs the app headless to scan VMs and email folder owners their old files.
- Old-files → owners emailing (on-demand and scheduled).

There is also an **Azure** sign-in module (`AzureClient`/`AzureForm`) that was an earlier approach. It is currently **not wired to a button** (the header button was repurposed to "Network VMs"). The code remains for reference; remove it if not needed.

---

## 2. How to build

1. Keep all files in one folder.
2. Double-click **Build.bat** (or run it from a Developer/normal command prompt). It locates `csc.exe` under `%WINDIR%\Microsoft.NET\Framework64\v4.0.30319\` and compiles `SpaceAnalyzer.cs` into `SpaceAnalyzer.exe`.
3. Output is a single self-contained `.exe`. Ship just the exe to users.

Build command (inside Build.bat), note the referenced assemblies and flags:

```
csc /target:winexe /platform:anycpu /optimize+ /out:SpaceAnalyzer.exe
    /win32icon:app.ico /codepage:65001 /win32manifest:app.manifest
    /r:System.dll /r:System.Core.dll /r:System.Drawing.dll /r:System.Windows.Forms.dll
    /r:System.Net.Http.dll /r:System.Web.Extensions.dll
    /r:System.DirectoryServices.dll /r:System.Management.dll
    SpaceAnalyzer.cs
```

- `/codepage:65001` — source is UTF-8 (there are a few non-ASCII characters like → in UI strings).
- `app.manifest` requests **requireAdministrator** (needed to read all folders, read the raw volume for the MFT scan, and clean system locations).

### Compiler gotchas (this is .NET Framework csc, ~C# 5/6)
- **No inline `out` variables** (`Foo(out string x)` fails). Declare first: `string x; Foo(out x);`
- Be careful with backslashes in string literals (`"\\\\" + host + "\\C$"`). Several past build breaks came from mangled escapes.
- Unused private methods/fields only warn; they don't break the build.

### Pre-build sanity checker
`check.py` (included) scans the source for the exact mistakes that repeatedly broke past builds: invalid escape sequences, unterminated strings, unbalanced braces/parens/brackets (string-aware), `x ? null : null`, and inline `out` declarations. **Run it before every build.** It is not a compiler, but it catches the common slip-ups. It can't run the real compiler (that must be done on Windows).

---

## 3. Architecture overview

Everything is one file, `SpaceAnalyzer.cs`, organized as many small classes. Key ones:

**Entry / headless**
- `Program.Main` — sets TLS 1.2, installs global exception handlers, then either runs headless (if args contain `/report`, `/agent`, `/delete`, `/cleanup`, `/listdir`, `/scheduled`, `/recyclelist`, `/recycledel`) or launches `MainForm`.
- `Headless` — all command-line/agent modes. This is what gets copied to a VM and run remotely. Writes result/`.done`/`.err`/`.log` files that the controller reads back.

**Scan engine**
- `Scanner` — orchestrates a scan; chooses fast (MFT) or normal per drive.
- `MftReader` — reads the raw NTFS MFT via `\\.\C:`. `MftIndex` — in-memory index used by the advisor and folder-listing.
- `DirNode`, `FileEntry`, `ExtStat` — the scan data model.
- `FilterSpec` — parses the filter box (`.bak`, `older than 1 year`, `>1gb`, words).
- `DupFinder` — duplicate detection (feature currently hidden from UI).

**Cleanup**
- `CleanupRules` (the rule list), `Advisor` (analyze + clean, parallel delete), `AdvisorRow`, `RuleFilesForm` (per-file review).

**Owners / email**
- `OwnerConfig`, `OwnerScanner`, `OwnerMail`, `OwnerReportForm`.

**Network / remote**
- `NetDiscovery` (online check, drive probe, saved list), `NetMachine`, `NetworkForm`.
- `RemoteAgent` — controller side: copies the exe to `\\VM\C$\Windows\Temp`, launches it via **WMI `Win32_Process.Create`**, polls for the result file, parses it. Also remote delete/cleanup/recycle/list-folder jobs.
- `MachineSession` — one machine's complete state; `MainForm` keeps a list and shows one at a time via the machine tab bar.

**UI building blocks**
- `Theme` (colors/fonts), `Canvas`/`FlatBtn`/`Card`/`DriveCard`/`VmDriveTile`/`Treemap`/`Donut`/`Bars`/`SearchBox`/`GridStyle` — all custom-drawn.
- `MainForm` — the main window; owns the tabs, drive tiles, machine bar, and all the per-view fill/action logic. **This is the biggest class and where most UI logic lives.**

**Other features**
- `RecycleBin`/`RecycleForm` (local + per-VM), `UserAnalyzer`/`UserStat`/`UserFolders` (by-user), `ScheduleConfig`/`ScheduleForm` (scheduled task).

### How remote scanning works (important mental model)
1. Controller copies `SpaceAnalyzer.exe` to the VM's `C$\Windows\Temp` as `SpaceAnalyzerAgent.exe`.
2. Controller calls WMI `Win32_Process.Create` to run it with `/agent <resultpath> /drives C,D`.
3. The agent runs the **same scan code** locally on the VM (full MFT speed) and writes a compact text result (`SA1` format: `F` folder lines, `XF` files, `XE` exts, `AD`/`AF` advisor + files, `DV` drive sizes).
4. Controller polls for the result file (with a grace period after the process exits, because writing a big result takes a few seconds), reads it over the admin share, parses it into a `MachineSession`, and shows it.
5. Remote delete/cleanup send a job file + WMI launch the agent in `/delete` or `/cleanup` mode.

### Threading rule
Background threads must never touch the UI directly. Every form has a `SafeInvoke(Action)` helper that checks `IsHandleCreated`/`IsDisposed`. **Always marshal UI updates through `SafeInvoke`** — a past crash ("Invoke cannot be called until the window handle is created") came from not doing this.

---

## 4. Files in this package

| File | Purpose |
|---|---|
| `SpaceAnalyzer.cs` | The entire source. |
| `Build.bat` | Compiles the exe with the correct references/flags. |
| `app.manifest` | Requests administrator rights + OS compatibility. |
| `app.ico` | App icon. |
| `check.py` | Pre-build structural checker (run before building). |
| `User Guide.html` | End-user documentation (ship with the exe). |
| `README.txt` | Short readme for end users. |
| `HANDOFF.md` | This document. |

Config/data files the app creates next to the exe at runtime (not shipped, generated on use): `SpaceAnalyzer_Log.csv`, `NetworkVMs.csv`, `TeamFolders.txt`, `OwnerMap.csv`, `EmailSettings.ini`, `ScheduledVMs.txt`, `SpaceAnalyzer_Schedule.log`, `SpaceAnalyzer_Debug.log`. (Template `OwnerMap.csv`, `EmailSettings.ini`, `TeamFolders.txt` are included as examples.)

---

## 5. Requirements & limitations

- **Local use:** Windows with .NET Framework 4.x (built in). Must run as admin. Needs the normal desktop (not Server Core, because of the WinForms UI).
- **Network VMs:** caller must be a **local admin on the target**; the VM must be **on, NTFS, domain-reachable**, with firewall rules for **admin shares (445)** and **WMI (WMI-In / RPC 135 + dynamic)**. Not an Azure/cloud integration — VPN/Bastion-only VMs won't connect. Remote deletes are **permanent** (no network Recycle Bin).
- **MFT fast scan** needs elevation and NTFS; otherwise it falls back to normal enumeration automatically.
- **Scheduled task** runs under whatever account you set; it needs network/admin rights to the VMs and a reachable SMTP server for emails. Emails require `OwnerMap.csv` + `EmailSettings.ini` filled in.
- **Antivirus** may flag remote process creation (WMI) or the copied agent exe; whitelist if needed.
- Can't be tested without Windows + a domain; the author built it without a live compiler, so **always compile and smoke-test on a real machine** after changes.

---

## 6. Suggested next steps / backlog

- Code-sign the exe (removes the SmartScreen "unknown publisher" warning).
- Re-enable or remove the hidden **Duplicates** tab and the **Azure** module depending on need.
- Per-user analysis currently estimates "old data" from the files whose dates the scan captured (top + big lists); for exact per-user old-data, compute it fully in the agent.
- Consider splitting `SpaceAnalyzer.cs` into multiple files (and a real `.csproj`/MSBuild) if moving to Visual Studio — this would unlock modern C# and proper debugging. If you do, drop the `.NET Framework csc` constraints noted above.
- Add retry/queue resilience for multi-VM scans (currently sequential, stops on a hard error).
- Scheduled run: add a "report only to admin" option and a dry-run summary.

---

## 7. Safety model (don't regress these)

- Every destructive action confirms first, default button = No.
- `Util.IsProtected` blocks Windows, Program Files, ProgramData\Microsoft, System Volume Information, Recovery, Boot, page/hibernation files, Users root, and whole user profiles. Keep this honored in **all** delete paths (local, advisor, remote, recycle).
- All deletions are logged to `SpaceAnalyzer_Log.csv`.
- Local file/folder deletes go to the Recycle Bin; advisor cleanups and all remote deletes are permanent by nature — keep the confirmations explicit about this.
