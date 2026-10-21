from __future__ import annotations

from typing import Iterable, Sequence


def placeholders(n: int, style: str) -> str:
    if style == "oracle":
        return ",".join(f":{i}" for i in range(1, n + 1))
    return ",".join(["?"] * n)


def chunked(seq: Sequence, size: int = 900):
    seq = list(seq)
    for i in range(0, len(seq), size):
        yield seq[i:i + size]


def select_in(cur, sql_template: str, values: Iterable, style: str) -> list:
    """Run `sql_template` once per chunk; the token %IN% becomes the bind list."""
    rows: list = []
    for chunk in chunked(list(values)):
        cur.execute(sql_template.replace("%IN%", placeholders(len(chunk), style)), list(chunk))
        rows.extend(cur.fetchall())
    return rows


def delete_in(cur, table: str, column: str, values: Iterable, style: str) -> int:
    n = 0
    for chunk in chunked(list(values)):
        cur.execute(
            f"delete from {table} where {column} in ({placeholders(len(chunk), style)})",
            list(chunk),
        )
        n += max(cur.rowcount or 0, 0)
    return n
