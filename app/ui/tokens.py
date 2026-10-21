"""Colour tokens for the light and dark themes. Pure data - no Qt - so contrast can be unit tested."""

LIGHT = dict(
    bg="#EDF0F2", surface="#FFFFFF", surface_alt="#F1F4F6", row_alt="#F8FAFB",
    ink="#17212B", muted="#55626F", border="#D3DAE0", input_border="#9FACB8",
    accent="#1D5FA6", accent_dark="#174D88", on_accent="#FFFFFF", focus="#1D5FA6",
    danger="#B42318", danger_btn="#B42318", danger_btn_dark="#8F1B12",
    ok="#1E7B4B",
    warn_bg="#FFF1CC", warn_border="#E0A81C", warn_text="#5C4100",
    dcard_bg="#FFF5F4", dcard_border="#E9B4AE",
    sel_bg="#DCE9F7", hover="#F4F7F9",
    dis_text="#5F6B77", dis_bg="#F1F4F6",
    primary_dis_bg="#D5DEE8", primary_dis_text="#4A5A6B",
    danger_dis_bg="#EBD3CF", danger_dis_text="#6E403B",
    rail="#17232F", rail_text="#B8C4CF", rail_idle="#8896A4", rail_title="#FFFFFF",
    rail_now_bg="#243746", rail_now_edge="#6CA7E0",
    rail_note_text="#F4D58A", rail_note_bg="#2A2616", rail_note_border="#5A4A1E",
    console_bg="#101A24", console_text="#D5DDE5",
)

DARK = dict(
    bg="#11161C", surface="#1A2129", surface_alt="#222B35", row_alt="#1E262F",
    ink="#E8EDF2", muted="#A3AFBB", border="#2F3A46", input_border="#566574",
    accent="#2D6DB8", accent_dark="#3A7CCB", on_accent="#FFFFFF", focus="#6CA7E0",
    danger="#FF8F87", danger_btn="#C0392F", danger_btn_dark="#D2463C",
    ok="#5FD39A",
    warn_bg="#3B3010", warn_border="#9A7516", warn_text="#F6DB93",
    dcard_bg="#2B1B1B", dcard_border="#7C3430",
    sel_bg="#27456B", hover="#232D38",
    dis_text="#8794A1", dis_bg="#1D242C",
    primary_dis_bg="#2A3C52", primary_dis_text="#A9BBCE",
    danger_dis_bg="#47231F", danger_dis_text="#D9AEA9",
    rail="#0B1015", rail_text="#B8C4CF", rail_idle="#8896A4", rail_title="#FFFFFF",
    rail_now_bg="#1B2733", rail_now_edge="#6CA7E0",
    rail_note_text="#F4D58A", rail_note_bg="#221F12", rail_note_border="#5A4A1E",
    console_bg="#0A1017", console_text="#D5DDE5",
)

# (foreground token, background token) pairs that carry text and must stay readable
TEXT_PAIRS = [
    ("ink", "bg"), ("ink", "surface"), ("ink", "surface_alt"), ("ink", "row_alt"),
    ("ink", "sel_bg"), ("ink", "hover"),
    ("muted", "bg"), ("muted", "surface"), ("muted", "surface_alt"), ("muted", "row_alt"),
    ("danger", "bg"), ("danger", "surface"), ("danger", "dcard_bg"), ("danger", "row_alt"),
    ("ok", "bg"), ("ok", "surface"),
    ("warn_text", "warn_bg"),
    ("ink", "dcard_bg"),
    ("on_accent", "accent"), ("on_accent", "danger_btn"),
    ("rail_text", "rail"), ("rail_idle", "rail"), ("rail_title", "rail"),
    ("rail_title", "rail_now_bg"), ("rail_note_text", "rail_note_bg"),
    ("console_text", "console_bg"),
    ("primary_dis_text", "primary_dis_bg"), ("danger_dis_text", "danger_dis_bg"),
    ("dis_text", "dis_bg"),
]


def _lum(hex_color: str) -> float:
    h = hex_color.lstrip("#")
    ch = [int(h[i:i + 2], 16) / 255 for i in (0, 2, 4)]
    ch = [c / 12.92 if c <= 0.03928 else ((c + 0.055) / 1.055) ** 2.4 for c in ch]
    return 0.2126 * ch[0] + 0.7152 * ch[1] + 0.0722 * ch[2]


def contrast(a: str, b: str) -> float:
    la, lb = _lum(a), _lum(b)
    hi, lo = max(la, lb), min(la, lb)
    return (hi + 0.05) / (lo + 0.05)
