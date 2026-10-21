from __future__ import annotations

import os
from abc import ABC, abstractmethod

from ..fs_ops import delete_tree, prune_empty_parents
from ..models import (DeletionPlan, Plant, PlantPlan, PlantResult, RunContext,
                      SiteConfig, Target)
from ..safety import assess_path, compute_reference_folders

# Option-setting ids that hold reference-data paths (unchanged from v4.5)
PID_OPTION_IDS = ["680", "681", "682", "683", "684", "685", "703", "709", "804", "809", "824"]
SEL_OPTION_IDS = ["1001", "1002", "1004", "1005", "1008", "1010", "1011", "1018", "1022",
                  "1023", "1024", "1034", "1035", "1040", "1047", "1048", "1049", "1050",
                  "1051", "1052", "1054", "1055", "1061", "1063", "1074", "1075", "1076"]
APP_SCHEMA_TYPES = ("SPEL", "SPPID")


class Backend(ABC):
    db_type = ""

    def __init__(self, site: SiteConfig):
        self.site = site

    # ---- contract used by the UI -------------------------------------------------
    @abstractmethod
    def connect(self, manual: dict | None = None, mode: str = "thin") -> None: ...

    @abstractmethod
    def list_plants(self) -> list[Plant]: ...

    def inventory_storage(self, plants: list[Plant]) -> dict:
        """Read plant-attributed database storage without changing the site."""
        return {"by_plant": {}, "shared_tablespaces": [], "warnings": []}

    @abstractmethod
    def build_plan(self, plants: list[Plant], delete_site: bool = False) -> DeletionPlan:
        """Read-only. Never changes anything."""

    @abstractmethod
    def execute(self, plan: DeletionPlan, ctx: RunContext) -> list[PlantResult]: ...

    @abstractmethod
    def close(self) -> None: ...

    @abstractmethod
    def build_site_targets(self) -> list[Target]: ...

    # ---- shared helpers ----------------------------------------------------------
    @staticmethod
    def folder_target(path: str, label: str, category: str, kind: str = "folder") -> Target | None:
        if path is None or str(path).strip() == "":
            return None
        p = os.path.normpath(str(path).strip().strip('"'))
        blocked, warning = assess_path(p)
        t = Target(kind=kind, name=p, label=label, category=category, note=warning)
        if blocked:
            t.blocked = blocked
            t.enabled = False
        elif not os.path.isdir(p):
            t.enabled = False
            t.note = "Not found from this computer - nothing to delete"
        return t

    def folder_targets(self, plant: Plant, pid_paths, sel_paths) -> tuple[list[Target], list[str]]:
        targets: list[Target] = []
        notes: list[str] = []
        seen: set[str] = set()

        def add(t: Target | None):
            if t is None:
                return
            key = os.path.normcase(t.name)
            if key in seen:
                return
            seen.add(key)
            targets.append(t)

        for label, cat, raw in (("P&ID reference data", "pid_ref", pid_paths),
                                ("Electrical reference data", "sel_ref", sel_paths)):
            folders, n = compute_reference_folders(raw)
            notes.extend(n)
            for f in folders:
                add(self.folder_target(f, label, cat))
        for pr in plant.projects:
            add(self.folder_target(pr.path, f"Project folder - {pr.name}", "project"))
        add(self.folder_target(plant.path, f"Plant folder - {plant.name}", "plant"))
        return targets, notes

    def delete_folders(self, pp: PlantPlan, ctx: RunContext) -> bool:
        """Returns False if any folder could not be fully removed."""
        all_ok = True
        for t in pp.active("folder"):
            ctx.check()
            ctx.status(f"Deleting {t.name}")
            reason, _ = assess_path(t.name)
            if reason:
                ctx.error(f"Skipped {t.name}: {reason}")
                all_ok = False
            elif delete_tree(t.name, ctx.log):
                ctx.ok(f"Deleted folder {t.name}")
                prune_empty_parents(t.name, ctx.log)
            else:
                ctx.error(f"Could not fully delete {t.name} (files may be locked by another program)")
                all_ok = False
            ctx.advance()
        return all_ok

    def delete_site_folder(self, path: str, ctx: RunContext) -> bool:
        reason, _ = assess_path(path)
        if reason:
            ctx.error(f"Skipped site folder {path}: {reason}")
            return False
        if not os.path.exists(path):
            ctx.ok(f"Site folder already gone: {path}")
            return True
        if delete_tree(path, ctx.log):
            ctx.ok(f"Deleted site folder {path}")
            prune_empty_parents(path, ctx.log)
            return True
        ctx.error(f"Could not fully delete site folder {path}")
        return False

    def delete_site_file(self, path: str, ctx: RunContext) -> bool:
        reason, _ = assess_path(path)
        if reason:
            ctx.error(f"Skipped site file {path}: {reason}")
            return False
        if not os.path.exists(path):
            ctx.ok(f"Site file already gone: {path}")
            return True
        try:
            os.remove(path)
            ctx.ok(f"Deleted site file {path}")
            return True
        except Exception as e:
            ctx.error(f"Could not delete site file {path}: {e}")
            return False
