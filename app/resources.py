import os
import sys


def resource_path(rel: str) -> str:
    """Works from source and from a PyInstaller one-file build."""
    base = getattr(sys, "_MEIPASS", os.path.dirname(os.path.dirname(os.path.abspath(__file__))))
    return os.path.join(base, rel)
