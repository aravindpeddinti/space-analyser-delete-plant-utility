"""Guards that keep a bad database value from becoming a catastrophic delete."""
from __future__ import annotations

import os
import re
import sys

_ORACLE_IDENT = re.compile(r"^[A-Za-z][A-Za-z0-9_$#]{0,127}$")


def safe_oracle_ident(name: str) -> str:
    """Validate an Oracle identifier before it is placed into DDL. Returns UPPER."""
    if not name or not _ORACLE_IDENT.match(name.strip()):
        raise ValueError(f"Unsafe or invalid Oracle identifier: {name!r}")
    return name.strip().upper()


def qident_mssql(name: str) -> str:
    """Bracket-quote a SQL Server identifier."""
    if name is None or str(name).strip() == "" or len(str(name)) > 128:
        raise ValueError(f"Invalid SQL Server identifier: {name!r}")
    return "[" + str(name).strip().replace("]", "]]") + "]"


def _norm(p: str) -> str:
    return os.path.normcase(os.path.normpath(p))


def _protected_roots() -> list[str]:
    roots: list[str] = []
    for var in ("SystemRoot", "windir", "ProgramFiles", "ProgramFiles(x86)",
                "ProgramW6432", "ProgramData", "USERPROFILE", "PUBLIC"):
        v = os.environ.get(var)
        if v:
            roots.append(v)
    prof = os.environ.get("USERPROFILE")
    if prof:
        roots.append(os.path.dirname(prof))  # C:\Users
    # never delete the folder the tool itself runs from
    try:
        roots.append(os.path.dirname(os.path.abspath(sys.executable)))
        roots.append(os.getcwd())
    except OSError:
        pass
    return [_norm(r) for r in roots if r]


def assess_path(path: str) -> tuple[str, str]:
    """Return (blocked_reason, warning). blocked_reason == '' means allowed."""
    if path is None or str(path).strip().strip('"') == "":
        return "Path is empty", ""
    p = os.path.normpath(str(path).strip().strip('"'))
    if not os.path.isabs(p):
        return "Path is not absolute", ""
    drive, rest = os.path.splitdrive(p)
    parts = [x for x in re.split(r"[\\/]+", rest) if x]
    if not parts:
        return "Path is a drive or share root", ""
    if os.path.islink(p):
        return "Path is a symbolic link", ""
    n = _norm(p)
    for root in _protected_roots():
        if n == root or root.startswith(n + os.sep):
            return "Path contains a protected system or application folder", ""
    for sysroot in (os.environ.get("SystemRoot"), os.environ.get("ProgramFiles"),
                    os.environ.get("ProgramFiles(x86)"), os.environ.get("ProgramData")):
        if sysroot and n.startswith(_norm(sysroot) + os.sep):
            return "Path is inside a protected system folder", ""
    warning = ""
    if len(parts) == 1:
        warning = "Top-level folder on its drive - confirm this is correct"
    return "", warning


def count_items(path: str) -> int:
    n = 0
    if os.path.exists(path):
        for _, dirs, files in os.walk(path):
            n += len(dirs) + len(files)
    return n


def _inside(child: str, parent: str) -> bool:
    c, p = _norm(child), _norm(parent)
    return c == p or c.startswith(p + os.sep)


def _drop_nested(paths: list[str]) -> list[str]:
    out: list[str] = []
    for p in sorted(set(paths), key=lambda x: len(_norm(x))):
        if not any(_inside(p, q) for q in out):
            out.append(p)
    return out


def _has_foreign_items(common: str, group: list[str]) -> bool:
    """True if `common` holds anything that is not (inside / an ancestor of) one of `group`."""
    gn = [_norm(g) for g in group]

    def is_ancestor(p: str) -> bool:
        n = _norm(p)
        return any(g.startswith(n + os.sep) for g in gn)

    def in_group(p: str) -> bool:
        n = _norm(p)
        return any(n == g or n.startswith(g + os.sep) for g in gn)

    for root, dirs, files in os.walk(common):
        keep = []
        for d in dirs:
            full = os.path.join(root, d)
            if in_group(full):
                continue            # part of what we are deleting; do not descend
            if is_ancestor(full):
                keep.append(d)      # on the way to a ref path; descend
                continue
            return True
        dirs[:] = keep
        for f in files:
            full = os.path.join(root, f)
            if not in_group(full):
                return True
    return False


def compute_reference_folders(raw_paths) -> tuple[list[str], list[str]]:
    """Turn raw reference-data paths into the minimal, safe set of folders to delete.

    v4.5 deleted the common ancestor of all paths whether or not it held other
    data. Here the common ancestor is used only when it contains nothing but the
    reference data itself; otherwise each folder is deleted on its own.
    Returns (folders, notes).
    """
    notes: list[str] = []
    clean: list[str] = []
    seen = set()
    for r in raw_paths or []:
        if r is None:
            continue
        s = str(r).strip().strip('"')
        if not s or not os.path.isabs(s):
            continue
        n = os.path.normpath(s)
        if _norm(n) in seen:
            continue
        seen.add(_norm(n))
        clean.append(n)
    existing = [p for p in clean if os.path.isdir(p)]
    groups: dict[str, list[str]] = {}
    for p in existing:
        groups.setdefault(os.path.normcase(os.path.splitdrive(p)[0]), []).append(p)

    result: list[str] = []
    for group in groups.values():
        group = _drop_nested(group)
        try:
            common = os.path.commonpath(group)
        except ValueError:
            result.extend(group)
            continue
        if len(group) > 1 and common not in group:
            blocked, _ = assess_path(common)
            if blocked:
                result.extend(group)
            elif _has_foreign_items(common, group):
                notes.append(
                    f"{common} also contains other items, so only the reference "
                    f"folders inside it will be deleted."
                )
                result.extend(group)
            else:
                result.append(common)
        else:
            result.extend(group)
    return _drop_nested(result), notes
