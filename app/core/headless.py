"""Headless, UI-independent API for planning and executing deletions.

The public boundary deliberately requires an explicit confirmation phrase and
rechecks configured SEM processes immediately before destructive execution.
"""
from __future__ import annotations

from typing import Callable

from .backends import make_backend
from . import audit
from .errors import BackendError
from .ini_reader import read_site_ini, resolve_ini_path
from .models import DeletionPlan, RunContext, Target
from .processes import find_running_apps


def target_to_dict(t: Target) -> dict:
    return {"kind": t.kind, "name": t.name, "label": t.label,
            "server": t.server, "category": t.category, "enabled": t.enabled,
            "blocked": t.blocked, "note": t.note, "active": t.active}


def plan_to_dict(plan: DeletionPlan) -> dict:
    return {
        "site_name": plan.site_name,
        "delete_site": plan.delete_site,
        "total_steps": plan.total_steps(),
        "plants": [{"name": pp.plant.name, "root_id": pp.plant.root_id,
                    "path": pp.plant.path,
                    "projects": [{"name": p.name, "root_id": p.root_id, "path": p.path}
                                 for p in pp.plant.projects],
                    "warnings": list(pp.warnings), "notes": list(pp.notes),
                    "targets": [target_to_dict(t) for t in pp.targets]}
                   for pp in plan.plants],
        "tablespaces": [target_to_dict(t) for t in plan.tablespaces],
        "site_targets": [target_to_dict(t) for t in plan.site_targets],
    }


def confirmation_phrase(plan: DeletionPlan) -> str:
    n = len(plan.plants)
    if plan.delete_site:
        return f"DELETE SITE {plan.site_name}" if n == 0 else f"DELETE SITE AND {n} PLANTS"
    return plan.plants[0].plant.name if n == 1 else f"DELETE {n} PLANTS"


class UtilitySession:
    """A connected session. A plan must be created and executed in this instance."""

    def __init__(self, ini_path: str, *, oracle_mode: str = "thin",
                 manual_connection: dict | None = None,
                 logger: Callable[[str, str], None] | None = None):
        resolved = resolve_ini_path(ini_path)
        self.site = read_site_ini(resolved)
        self.backend = make_backend(self.site)
        audit.start_session("5.0.0 headless")
        def log(level: str, message: str) -> None:
            audit.write(level, message)
            if logger:
                logger(level, message)
        self.logger = log
        self._plans: dict[int, DeletionPlan] = {}
        self._consumed_plans: set[int] = set()
        self._closed = False
        self.backend.connect(manual=manual_connection, mode=oracle_mode)

    def list_plants(self) -> list[dict]:
        self._ensure_open()
        plants = self.backend.list_plants()
        return [{"name": p.name, "root_id": p.root_id, "path": p.path,
                 "projects": [{"name": pr.name, "root_id": pr.root_id, "path": pr.path}
                              for pr in p.projects]} for p in plants]

    def inventory(self) -> dict:
        """Read site plants and their database allocation without changing data."""
        self._ensure_open()
        plants = self.backend.list_plants()
        storage = self.backend.inventory_storage(plants)
        measured = storage.get("by_plant", {})
        result = []
        for plant in plants:
            row = measured.get(plant.root_id, {"bytes": None, "items": []})
            result.append({"name": plant.name, "root_id": plant.root_id,
                           "path": plant.path, "database_bytes": row.get("bytes"),
                           "database_items": row.get("items", []),
                           "projects": [{"name": pr.name, "root_id": pr.root_id,
                                         "path": pr.path} for pr in plant.projects]})
        return {"site": {"name": self.site.site_schema or self.site.site_database,
                          "database": self.site.db_label, "server": self.site.server,
                          "site_database": self.site.site_database,
                          "site_schema": self.site.site_schema,
                          "ini_path": self.site.ini_path},
                "plants": result,
                "shared_tablespaces": storage.get("shared_tablespaces", []),
                "shared_databases": storage.get("shared_databases", []),
                "shared_schemas": storage.get("shared_schemas", []),
                "warnings": storage.get("warnings", [])}

    def build_plan(self, plant_names: list[str] | None = None, *,
                   delete_site: bool = False, include_tablespaces: bool = False) -> DeletionPlan:
        self._ensure_open()
        available = self.backend.list_plants()
        # Match the GUI's whole-site action: a site wipe on a populated site
        # always includes every discovered plant; callers cannot accidentally
        # request a site-only wipe while leaving plant rows behind.
        all_names = [p.name for p in available]
        if delete_site and plant_names and {n.casefold() for n in plant_names} != {
                n.casefold() for n in all_names}:
            raise ValueError("Whole-site deletion includes every discovered plant; omit --plant "
                             "or specify all plant names")
        names = all_names if delete_site else (list(plant_names) if plant_names else [])
        lookup = {p.name.casefold(): p for p in available}
        unknown = [name for name in names if name.casefold() not in lookup]
        if unknown:
            raise ValueError("Unknown plant name(s): " + ", ".join(unknown))
        if len({n.casefold() for n in names}) != len(names):
            raise ValueError("Plant names must not contain duplicates")
        if not names and not delete_site:
            raise ValueError("Select at least one plant, or explicitly request delete_site=True")
        selected = [lookup[n.casefold()] for n in names]
        plan = self.backend.build_plan(selected, delete_site=delete_site)
        # Oracle table-space drops are opt-in, also in the headless interface.
        for target in plan.tablespaces:
            if target.kind in ("tablespace", "temp_tablespace") and not include_tablespaces:
                target.enabled = False
        self._plans[id(plan)] = plan  # retain the object to make id reuse impossible
        return plan

    def execute(self, plan: DeletionPlan, *, confirmation: str,
                acknowledge: bool = False,
                is_cancelled: Callable[[], bool] | None = None,
                process_host: str | None = None) -> list[dict]:
        self._ensure_open()
        if self._plans.get(id(plan)) is not plan or id(plan) in self._consumed_plans:
            raise ValueError("Plan was not created by this live UtilitySession")
        expected = confirmation_phrase(plan)
        if confirmation != expected or not acknowledge:
            raise ValueError(f"Execution requires confirmation={expected!r} and acknowledge=True")
        if plan.total_steps() == 0:
            raise ValueError("Plan has no enabled, unblocked targets")
        # Fail closed if the process list cannot be checked.
        blockers = find_running_apps(strict=True, host=process_host)
        if blockers:
            raise BackendError("Close SEM application(s) before deletion: " + ", ".join(blockers))
        self._consumed_plans.add(id(plan))
        ctx = RunContext(self.logger, lambda done, total, message: None,
                         is_cancelled or (lambda: False), total=plan.total_steps())
        results = self.backend.execute(plan, ctx)
        return [{"plant": r.plant, "status": r.status, "messages": list(r.messages)}
                for r in results]

    def close(self) -> None:
        if not self._closed:
            try:
                self.backend.close()
            finally:
                self._closed = True

    def _ensure_open(self) -> None:
        if self._closed:
            raise RuntimeError("UtilitySession is closed")

    def __enter__(self) -> "UtilitySession":
        return self

    def __exit__(self, exc_type, exc, tb) -> None:
        self.close()
