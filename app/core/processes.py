"""Detect Smart Engineering Manager applications that are still open.

Open applications keep plant folders locked and hold database sessions, which makes
a delete fail or leave half-removed plants. Matching is by case-insensitive
substring of the process name, using patterns from blocking_apps.txt so the list
can be adjusted without a new build.
"""
from __future__ import annotations

import csv
import io
import os
import subprocess
import sys

from . import audit

DEFAULT_PATTERNS = [
    "smartplant", "smart plant", "smartelectrical", "smart electrical",
    "drawingmanager", "drawing manager", "engineeringmanager", "intergraph",
]

HEADER = """# Applications that must be closed before plants are deleted.
# One entry per line. An entry matches any running process whose name CONTAINS it
# (case-insensitive, ".exe" optional). Lines starting with # are ignored.
# Add the exact process names used on your servers; remove entries that cause false alarms.
"""


def config_path() -> str:
    return os.path.join(os.path.dirname(audit.log_dir()), "blocking_apps.txt")


def load_patterns() -> list[str]:
    path = config_path()
    if not os.path.isfile(path):
        try:
            with open(path, "w", encoding="utf-8") as f:
                f.write(HEADER + "\n".join(DEFAULT_PATTERNS) + "\n")
        except OSError:
            return list(DEFAULT_PATTERNS)
    try:
        with open(path, "r", encoding="utf-8") as f:
            pats = [ln.strip().lower() for ln in f if ln.strip() and not ln.lstrip().startswith("#")]
    except OSError:
        return list(DEFAULT_PATTERNS)
    return pats or list(DEFAULT_PATTERNS)


def match_processes(process_names: list[str], patterns: list[str]) -> list[str]:
    own = os.path.basename(sys.executable).lower()
    hits = set()
    for name in process_names:
        low = name.lower()
        if low == own or "delete plant utility" in low or "deleteplantutility" in low:
            continue
        stem = low[:-4] if low.endswith(".exe") else low
        for p in patterns:
            p = p[:-4] if p.endswith(".exe") else p
            if p and p in stem:
                hits.add(name)
                break
    return sorted(hits, key=str.lower)


def _running_names(*, strict: bool = False, host: str | None = None) -> list[str]:
    if os.name != "nt":
        if strict:
            raise RuntimeError("Cannot verify blocking SEM processes on this non-Windows host")
        return []
    try:
        args = ["tasklist"]
        if host:
            args.extend(["/S", host])
        args.extend(["/FO", "CSV", "/NH"])
        out = subprocess.run(args, capture_output=True, text=True,
                             timeout=15, creationflags=0x08000000)  # CREATE_NO_WINDOW
    except (OSError, subprocess.SubprocessError):
        if strict:
            raise RuntimeError("Could not query running processes; refusing destructive operation")
        return []
    if strict and out.returncode != 0:
        raise RuntimeError("Could not query running processes; refusing destructive operation")
    names = []
    for row in csv.reader(io.StringIO(out.stdout)):
        if row:
            names.append(row[0])
    return names


def find_running_apps(*, strict: bool = False, host: str | None = None) -> list[str]:
    """Return configured SEM process matches; strict mode fails closed on probe errors."""
    return match_processes(_running_names(strict=strict, host=host), load_patterns())
