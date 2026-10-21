from __future__ import annotations

from dataclasses import dataclass, field
from typing import Callable, Optional

from .errors import RunCancelled


@dataclass
class SiteConfig:
    ini_path: str
    db_type: str          # "ORACLE" or "MSSQL"
    server: str           # Oracle TNS alias / SQL Server name
    site_database: str    # SQL Server site database (empty for Oracle)
    site_schema: str
    username: str
    password: str = field(default="", repr=False)
    oracle_service: str = ""
    dictionary_schema: str = ""
    dba_user: str = ""
    dba_password: str = field(default="", repr=False)

    @property
    def db_label(self) -> str:
        return "Oracle" if self.db_type == "ORACLE" else "SQL Server"


@dataclass
class Project:
    root_id: str
    name: str
    path: str


@dataclass
class Plant:
    root_id: str
    name: str
    path: str
    projects: list[Project] = field(default_factory=list)

    @property
    def all_ids(self) -> list[str]:
        return [p.root_id for p in self.projects] + [self.root_id]


@dataclass
class Target:
    """One thing the utility will remove. Shown as a checkbox in the review."""
    kind: str             # folder | schema | database | login | tablespace | temp_tablespace | site_records | site_folder | site_file
    name: str
    label: str = ""
    server: str = ""
    category: str = ""    # for folders: plant | project | pid_ref | sel_ref | site
    enabled: bool = True
    blocked: str = ""     # non-empty -> never executed, shown in red
    note: str = ""

    @property
    def active(self) -> bool:
        return self.enabled and not self.blocked


@dataclass
class PlantPlan:
    plant: Plant
    targets: list[Target] = field(default_factory=list)
    warnings: list[str] = field(default_factory=list)
    notes: list[str] = field(default_factory=list)

    def active(self, *kinds: str) -> list[Target]:
        return [t for t in self.targets if t.active and (not kinds or t.kind in kinds)]

    def step_count(self) -> int:
        return len(self.active())


@dataclass
class DeletionPlan:
    plants: list[PlantPlan] = field(default_factory=list)
    tablespaces: list[Target] = field(default_factory=list)
    delete_site: bool = False
    site_targets: list[Target] = field(default_factory=list)
    site_name: str = ""

    def total_steps(self) -> int:
        return (sum(p.step_count() for p in self.plants)
                + len([t for t in self.tablespaces if t.active])
                + len([t for t in self.site_targets if t.active]))

    def all_targets(self) -> list[Target]:
        out: list[Target] = []
        for p in self.plants:
            out.extend(p.targets)
        out.extend(self.tablespaces)
        out.extend(self.site_targets)
        return out


@dataclass
class PlantResult:
    plant: str
    status: str = "ok"    # ok | partial | failed | cancelled
    messages: list[str] = field(default_factory=list)


@dataclass
class RunContext:
    on_log: Callable[[str, str], None]
    on_progress: Callable[[int, int, str], None]
    is_cancelled: Callable[[], bool]
    total: int = 0
    done: int = 0

    def log(self, level: str, msg: str) -> None:
        self.on_log(level, msg)

    def info(self, msg: str) -> None:
        self.on_log("info", msg)

    def warn(self, msg: str) -> None:
        self.on_log("warn", msg)

    def error(self, msg: str) -> None:
        self.on_log("error", msg)

    def ok(self, msg: str) -> None:
        self.on_log("ok", msg)

    def status(self, msg: str) -> None:
        self.on_progress(self.done, self.total, msg)

    def advance(self, msg: str = "") -> None:
        self.done += 1
        self.on_progress(self.done, self.total, msg)

    def check(self) -> None:
        if self.is_cancelled():
            raise RunCancelled()
