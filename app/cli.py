"""Command-line interface to the same backend operations used by the GUI."""
from __future__ import annotations

import argparse
import json
import os
import sys
from .core.errors import BackendError
from .core.headless import UtilitySession, confirmation_phrase, plan_to_dict


def _parser() -> argparse.ArgumentParser:
    parser = argparse.ArgumentParser(prog="delete-plant-utility",
        description="Headless SEM plant/site listing, read-only planning and deletion.")
    parser.add_argument("--ini", required=True, help="smartplantv4.ini file or its containing directory")
    parser.add_argument("--oracle-mode", choices=("thin", "thick"), default="thin")
    parser.add_argument("--oracle-host", help="Manual Oracle host; when set, use manual connection")
    parser.add_argument("--oracle-port", default="1521")
    parser.add_argument("--oracle-service", help="Oracle service name for manual connection")
    parser.add_argument("--process-host", help="Machine whose SEM applications must be closed before deletion")
    parser.add_argument("--output", help="Write JSON to this file instead of standard output")
    sub = parser.add_subparsers(dest="command", required=True)
    list_cmd = sub.add_parser("list", help="List plants and projects")
    inventory = sub.add_parser("inventory", help="Read plant database space and shared storage")
    # Accept --output after the command too, as remote Windows process launchers
    # append it after the operation and its options.
    for cmd in (list_cmd, inventory):
        cmd.add_argument("--output", default=argparse.SUPPRESS,
                         help="Write JSON to this file instead of standard output")
    plan = sub.add_parser("plan", help="Build and print a read-only deletion plan")
    execute = sub.add_parser("execute", help="Rebuild a plan, then execute after confirmation")
    for cmd in (plan, execute):
        cmd.add_argument("--output", default=argparse.SUPPRESS,
                         help="Write JSON to this file instead of standard output")
        cmd.add_argument("--plant", action="append", default=[], help="Plant name (repeat for multiple plants)")
        cmd.add_argument("--delete-site", action="store_true", help="Include destructive site-wide cleanup")
        cmd.add_argument("--include-tablespaces", action="store_true", help="Enable eligible Oracle tablespace drops")
    execute.add_argument("--confirm", required=True, help="Exact phrase printed by the plan command")
    execute.add_argument("--acknowledge", action="store_true", help="Explicitly acknowledge irreversible deletion")
    return parser


def _emit(value, path: str | None = None) -> None:
    text = json.dumps(value, ensure_ascii=False, indent=2)
    if not path:
        print(text)
        return
    full = os.path.abspath(path)
    os.makedirs(os.path.dirname(full), exist_ok=True)
    temporary = full + ".tmp"
    with open(temporary, "w", encoding="utf-8") as f:
        f.write(text)
    os.replace(temporary, full)


def main(argv: list[str] | None = None) -> int:
    args = _parser().parse_args(argv)
    manual = None
    if args.oracle_host:
        manual = {"host": args.oracle_host, "port": args.oracle_port,
                  "service": args.oracle_service or ""}
    try:
        with UtilitySession(args.ini, oracle_mode=args.oracle_mode,
                            manual_connection=manual,
                            logger=lambda level, message: print(f"[{level}] {message}", file=sys.stderr)) as session:
            if args.command == "list":
                _emit({"ok": True, "plants": session.list_plants()}, args.output)
                return 0
            if args.command == "inventory":
                _emit({"ok": True, **session.inventory()}, args.output)
                return 0
            plan = session.build_plan(args.plant, delete_site=args.delete_site,
                                      include_tablespaces=args.include_tablespaces)
            if args.command == "plan":
                result = {"ok": True, "confirmation_phrase": confirmation_phrase(plan),
                          "plan": plan_to_dict(plan)}
                _emit(result, args.output)
                return 0
            results = session.execute(plan, confirmation=args.confirm,
                                      acknowledge=args.acknowledge,
                                      process_host=args.process_host)
            _emit({"ok": all(r["status"] == "ok" for r in results),
                   "plan": plan_to_dict(plan), "results": results}, args.output)
            return 0 if all(r["status"] == "ok" for r in results) else 2
    except (BackendError, ValueError, RuntimeError, OSError) as exc:
        _emit({"ok": False, "error": str(exc)}, getattr(args, "output", None))
        return 1


if __name__ == "__main__":
    raise SystemExit(main())
