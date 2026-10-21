from __future__ import annotations

import os
import shutil
import stat
import sys
import time

from .safety import assess_path


def _long(path: str) -> str:
    """Extended-length path prefix so deep SmartPlant trees delete on Windows."""
    if os.name != "nt" or path.startswith("\\\\?\\"):
        return path
    p = os.path.abspath(path)
    if len(p) < 240:
        return p
    if p.startswith("\\\\"):
        return "\\\\?\\UNC\\" + p[2:]
    return "\\\\?\\" + p


def _make_writable_and_retry(func, p, _exc):
    try:
        os.chmod(p, stat.S_IWRITE)
        func(p)
    except OSError:
        pass


def delete_tree(path: str, log=None, attempts: int = 3) -> bool:
    """Delete a folder tree. Returns True when the folder is gone afterwards."""
    if not os.path.lexists(path):
        return True
    for attempt in range(1, attempts + 1):
        try:
            if sys.version_info >= (3, 12):
                shutil.rmtree(_long(path), onexc=_make_writable_and_retry)
            else:
                shutil.rmtree(_long(path), onerror=lambda f, p, e: _make_writable_and_retry(f, p, e))
        except OSError as e:
            if log:
                log("warn", f"Attempt {attempt}/{attempts} could not remove {path}: {e}")
        if not os.path.lexists(path):
            return True
        time.sleep(1.0)
    return not os.path.lexists(path)


def prune_empty_parents(path: str, log=None) -> list[str]:
    """Remove now-empty parent folders (v4 behaviour), never crossing a protected folder."""
    removed: list[str] = []
    parent = os.path.dirname(os.path.normpath(path))
    while parent and parent != os.path.dirname(parent):
        if assess_path(parent)[0]:
            break
        try:
            if os.listdir(parent):
                break
            os.rmdir(parent)
        except OSError:
            break
        removed.append(parent)
        if log:
            log("info", f"Removed empty parent folder {parent}")
        parent = os.path.dirname(parent)
    return removed
