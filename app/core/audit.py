"""Plain-text audit log. Passwords are never written."""
from __future__ import annotations

import datetime
import getpass
import os
import socket
import tempfile
import threading

_lock = threading.Lock()
_path: str | None = None


def log_dir() -> str:
    base = os.environ.get("LOCALAPPDATA") or tempfile.gettempdir()
    d = os.path.join(base, "DeletePlantUtility", "logs")
    os.makedirs(d, exist_ok=True)
    return d


def start_session(version: str) -> str:
    global _path
    stamp = datetime.datetime.now().strftime("%Y%m%d_%H%M%S")
    _path = os.path.join(log_dir(), f"session_{stamp}.log")
    write("info", f"Delete Plant Utility {version} (internal use only) started")
    try:
        write("info", f"Operator: {getpass.getuser()} on {socket.gethostname()}")
    except Exception:
        pass
    return _path


def current_path() -> str | None:
    return _path


def write(level: str, msg: str) -> None:
    if not _path:
        return
    ts = datetime.datetime.now().strftime("%Y-%m-%d %H:%M:%S")
    line = f"{ts} [{level.upper():5}] {msg}\n"
    with _lock:
        try:
            with open(_path, "a", encoding="utf-8") as f:
                f.write(line)
        except OSError:
            pass
