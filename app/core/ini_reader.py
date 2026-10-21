from __future__ import annotations

import datetime
import os
import time

from .crypto import decrypt
from .errors import BackendError
from .models import SiteConfig

INI_NAME = "smartplantv4.ini"


def resolve_ini_path(raw: str) -> str:
    raw = (raw or "").strip().strip('"')
    if not raw:
        raise BackendError("Choose the smartplantv4.ini file or the folder that contains it.")
    p = os.path.abspath(raw)
    if os.path.isdir(p):
        p = os.path.join(p, INI_NAME)
    if not os.path.isfile(p):
        raise BackendError(f"File not found: {p}")
    if os.path.basename(p).lower() != INI_NAME:
        raise BackendError(f"The file must be named {INI_NAME}.")
    return p


def _read_lines(path: str) -> list[str]:
    for enc in ("utf-16", "utf-8-sig", "utf-8", "cp1252"):
        try:
            with open(path, "r", encoding=enc) as f:
                return f.readlines()
        except (UnicodeError, UnicodeDecodeError):
            continue
    raise BackendError("The ini file could not be read (unexpected encoding).")


def _parse_sections(lines: list[str]) -> dict[str, dict[str, str]]:
    sections: dict[str, dict[str, str]] = {}
    current = ""
    for line in lines:
        s = line.strip()
        if not s or s.startswith(";") or s.startswith("#"):
            continue
        if s.startswith("[") and s.endswith("]"):
            current = s[1:-1].strip().upper()
            sections[current] = {}
        elif "=" in s and current:
            k, v = s.split("=", 1)
            sections[current][k.strip().upper()] = v.strip()
    return sections


def read_site_ini(path: str) -> SiteConfig:
    """Parse smartplantv4.ini.

    Supports structured section format ([SITESCHEMA], [DICTIONARY])
    and legacy fixed-line index positions.
    Values are split on the FIRST '=' only to protect encoded passwords containing '='.
    """
    lines = _read_lines(path)
    sections = _parse_sections(lines)

    if "SITESCHEMA" in sections:
        sec = sections["SITESCHEMA"]
        db_type = sec.get("DBTYPE", "ORACLE").upper()
        server = sec.get("DBSERVER", "") or sec.get("ORACLE_SERVICE", "")
        oracle_service = sec.get("ORACLE_SERVICE", "") or server
        site_database = sec.get("DATABASE", "") or sec.get("DBNAME", "")
        if db_type != "ORACLE" and not site_database and len(lines) > 4 and "=" in lines[4]:
            site_database = lines[4].split("=", 1)[1].strip()
        site_schema = sec.get("UID", "")
        dba_user = sec.get("DBAUSER", "")
        username = dba_user or site_schema
        enc_pwd = sec.get("PWD", "")
        enc_dba = sec.get("DBA", "")
        site_pwd = decrypt(enc_pwd)
        dba_password = decrypt(enc_dba) if enc_dba else site_pwd
        password = dba_password if dba_user else site_pwd

        dict_sec = sections.get("DICTIONARY", {})
        dict_schema = dict_sec.get("UID", "")

        return SiteConfig(
            ini_path=path,
            db_type=db_type,
            server=server,
            site_database=site_database,
            site_schema=site_schema,
            username=username,
            password=password,
            oracle_service=oracle_service,
            dictionary_schema=dict_schema,
            dba_user=dba_user,
            dba_password=dba_password,
        )

    # Legacy line-by-line fallback
    if len(lines) < 9:
        raise BackendError("The ini file is shorter than expected. Is this a smartplantv4.ini?")

    def val(idx: int) -> str:
        line = lines[idx]
        if "=" not in line:
            raise BackendError(f"Line {idx + 1} of the ini file has no '=' - unexpected format.")
        return line.split("=", 1)[1].strip()

    db_type = val(2).upper()
    schema = val(5)
    user = val(7)
    enc = val(8)
    password = decrypt(enc)
    if enc and not password:
        raise BackendError("The stored administrator password could not be decoded from the ini file.")

    if db_type == "ORACLE":
        srv = val(4)
        return SiteConfig(
            ini_path=path,
            db_type="ORACLE",
            server=srv,
            site_database="",
            site_schema=schema,
            username=user,
            password=password,
            oracle_service=srv,
        )
    return SiteConfig(
        ini_path=path,
        db_type="MSSQL",
        server=val(3),
        site_database=val(4),
        site_schema=schema,
        username=user,
        password=password,
    )


_SKIP_DIRS = {"$recycle.bin", "system volume information", ".git", "node_modules", "__pycache__"}


def find_ini_files(root: str, limit: int = 50, time_limit: float = 45.0, cancel=None):
    """Find every smartplantv4.ini under `root` (any depth, case-insensitive name)."""
    root = os.path.abspath(root)
    found: list[str] = []
    scanned = 0
    stopped = False
    start = time.monotonic()
    for cur, dirs, files in os.walk(root, onerror=lambda e: None):
        scanned += 1
        dirs[:] = [d for d in dirs if d.lower() not in _SKIP_DIRS]
        for f in files:
            if f.lower() == INI_NAME:
                found.append(os.path.join(cur, f))
        if len(found) >= limit or time.monotonic() - start > time_limit or (cancel and cancel()):
            stopped = True
            break
    return sorted(found, key=str.lower), scanned, stopped


def describe_ini(path: str) -> dict:
    """Short, password-free summary used to tell several site files apart."""
    info = {"path": path, "db": "", "server": "", "schema": "", "modified": "", "error": ""}
    try:
        info["modified"] = datetime.datetime.fromtimestamp(os.path.getmtime(path)).strftime("%Y-%m-%d %H:%M")
    except OSError:
        pass
    try:
        site = read_site_ini(path)
        info.update(db=site.db_label, server=site.server, schema=site.site_schema)
    except Exception as e:
        info["error"] = str(e)
    return info
