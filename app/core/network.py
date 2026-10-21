from __future__ import annotations

import os
import re
import socket
import subprocess


def parse_host_and_port(server: str, default_port: int = 1521) -> tuple[str, int, str]:
    """Parse server string into (host, port, instance_or_service).

    Handles formats like:
      - 'MACHINE\\INSTANCE'
      - 'HOST,PORT'
      - 'HOST:PORT'
      - 'HOST'
    """
    server = (server or "").strip()
    if "\\" in server:
        host, instance = server.split("\\", 1)
        return host.strip(), default_port, instance.strip()
    if "," in server:
        parts = server.split(",", 1)
        try:
            return parts[0].strip(), int(parts[1].strip()), ""
        except ValueError:
            return parts[0].strip(), default_port, ""
    if ":" in server and not server.startswith("(") :
        parts = server.split(":", 1)
        try:
            return parts[0].strip(), int(parts[1].strip()), ""
        except ValueError:
            return parts[0].strip(), default_port, ""
    return server, default_port, ""


def is_host_pingable(host: str, timeout_sec: float = 1.5) -> bool:
    """Return True if host responds to ICMP ping, or is local."""
    h = (host or "").strip().lower()
    if not h or h in ("localhost", "127.0.0.1", "::1", "."):
        return True
    try:
        # On Windows, -n 1 sends 1 echo request, -w timeout in ms
        cmd = ["ping", "-n", "1", "-w", str(int(timeout_sec * 1000)), host]
        proc = subprocess.run(cmd, stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL,
                              timeout=timeout_sec + 1.0)
        return proc.returncode == 0
    except Exception:
        # Fallback to TCP attempt on standard port if ping fails or is disabled by firewall
        return False


def is_port_open(host: str, port: int, timeout_sec: float = 2.0) -> bool:
    """Return True if a TCP socket connection can be established to host:port."""
    h = (host or "").strip()
    if not h:
        return False
    try:
        s = socket.socket(socket.AF_INET, socket.SOCK_STREAM)
        s.settimeout(timeout_sec)
        res = s.connect_ex((h, int(port)))
        s.close()
        return res == 0
    except Exception:
        return False


def diagnose_connection_failure(server: str, db_type: str, default_port: int | None = None,
                                service_or_instance: str = "") -> str | None:
    """Run ping and port check to provide a specific, user-friendly diagnostic message."""
    if default_port is None:
        default_port = 1521 if db_type.upper() == "ORACLE" else 1433

    host, port, parsed_instance = parse_host_and_port(server, default_port)
    target_name = service_or_instance or parsed_instance or server

    if not host:
        return None

    # Step 1: Check if host machine is reachable
    ping_ok = is_host_pingable(host)
    if not ping_ok:
        # Secondary check: if port is open despite ICMP ping block
        if not is_port_open(host, port, timeout_sec=1.5):
            return (f"Machine '{host}' is turned off or unreachable on the network. "
                    f"Please ensure the machine is powered on and connected.")

    # Step 2: Check if database service / port is open
    port_ok = is_port_open(host, port, timeout_sec=2.0)
    if not port_ok:
        if db_type.upper() == "ORACLE":
            return (f"Machine '{host}' is powered on, but the Oracle Listener / service for "
                    f"'{target_name}' (port {port}) is not responding. "
                    f"Please ensure the Oracle service and listener are started.")
        else:
            return (f"Machine '{host}' is powered on, but the SQL Server service for instance "
                    f"'{target_name}' (port {port}) is not responding. "
                    f"Please ensure the SQL Server service is running.")

    return None
