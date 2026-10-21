"""Light / dark theme. Follows the Windows app setting unless the user picks one."""
from __future__ import annotations

from PyQt5.QtGui import QColor, QFont, QPalette
from PyQt5.QtWidgets import QApplication

from .tokens import DARK, LIGHT

_state = {"mode": "system", "dark": False}


def current() -> dict:
    return DARK if _state["dark"] else LIGHT


def is_dark() -> bool:
    return _state["dark"]


def mode() -> str:
    return _state["mode"]


def system_is_dark(app: QApplication) -> bool:
    try:                                   # Read the Windows theme setting
        import winreg
        with winreg.OpenKey(winreg.HKEY_CURRENT_USER,
                            r"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize") as k:
            return winreg.QueryValueEx(k, "AppsUseLightTheme")[0] == 0
    except Exception:
        return False


def _palette(t: dict) -> QPalette:
    p = QPalette()
    c = QColor
    R, G = QPalette.ColorRole, QPalette.ColorGroup
    for role, key in ((R.Window, "bg"), (R.WindowText, "ink"), (R.Base, "surface"),
                      (R.AlternateBase, "row_alt"), (R.Text, "ink"), (R.Button, "surface"),
                      (R.ButtonText, "ink"), (R.Highlight, "sel_bg"), (R.HighlightedText, "ink"),
                      (R.ToolTipBase, "surface_alt"), (R.ToolTipText, "ink"),
                      (R.PlaceholderText, "muted"), (R.Link, "focus"), (R.BrightText, "danger")):
        p.setColor(role, c(t[key]))
    for role in (R.Text, R.ButtonText, R.WindowText):
        p.setColor(G.Disabled, role, c(t["dis_text"]))
    return p


def _qss(t: dict) -> str:
    return f"""
* {{ font-family: "Segoe UI", "Inter", sans-serif; font-size: 10pt; color: {t['ink']}; }}
QMainWindow, QDialog, QMessageBox, QWidget#page {{ background: {t['bg']}; }}
QLabel {{ background: transparent; }}
QLabel#h1 {{ font-size: 19pt; font-weight: 600; }}
QLabel#h2 {{ font-size: 12pt; font-weight: 600; }}
QLabel#muted {{ color: {t['muted']}; }}
QLabel#error {{ color: {t['danger']}; }}
QLabel#okText {{ color: {t['ok']}; }}
QToolTip {{ background: {t['surface_alt']}; color: {t['ink']}; border: 1px solid {t['border']}; padding: 4px; }}

QFrame#rail {{ background: {t['rail']}; }}
QFrame#rail QLabel {{ color: {t['rail_text']}; }}
QLabel#railTitle {{ color: {t['rail_title']}; font-size: 14pt; font-weight: 600; }}
QLabel#railVersion {{ color: {t['rail_idle']}; }}
QLabel#stepIdle {{ color: {t['rail_idle']}; padding: 9px 12px; }}
QLabel#stepDone {{ color: {t['rail_text']}; padding: 9px 12px; }}
QLabel#stepNow {{ color: {t['rail_title']}; font-weight: 600; padding: 9px 12px;
                  background: {t['rail_now_bg']}; border-left: 3px solid {t['rail_now_edge']}; }}
QLabel#railNotice {{ color: {t['rail_note_text']}; border: 1px solid {t['rail_note_border']};
                     background: {t['rail_note_bg']}; padding: 10px; border-radius: 4px; }}

QLabel#banner {{ background: {t['warn_bg']}; color: {t['warn_text']}; border-bottom: 1px solid {t['warn_border']};
                 padding: 8px 20px; font-weight: 600; }}

QFrame#card {{ background: {t['surface']}; border: 1px solid {t['border']}; border-radius: 6px; }}
QFrame#card QLabel {{ border: none; }}
QFrame#dangerCard {{ background: {t['dcard_bg']}; border: 1px solid {t['dcard_border']}; border-radius: 6px; }}
QFrame#dangerCard QLabel, QFrame#dangerCard QCheckBox {{ border: none; }}

QPushButton {{ background: {t['surface']}; color: {t['ink']}; border: 1px solid {t['input_border']};
               border-radius: 4px; padding: 7px 16px; }}
QPushButton:hover {{ background: {t['hover']}; }}
QPushButton:disabled {{ color: {t['dis_text']}; background: {t['dis_bg']}; border-color: {t['border']}; }}
QPushButton:focus {{ border: 2px solid {t['focus']}; padding: 6px 15px; }}
QPushButton#primary {{ background: {t['accent']}; color: {t['on_accent']}; border: 1px solid {t['accent_dark']}; font-weight: 600; }}
QPushButton#primary:hover {{ background: {t['accent_dark']}; }}
QPushButton#primary:disabled {{ background: {t['primary_dis_bg']}; border-color: {t['primary_dis_bg']}; color: {t['primary_dis_text']}; }}
QPushButton#danger {{ background: {t['danger_btn']}; color: {t['on_accent']}; border: 1px solid {t['danger_btn_dark']}; font-weight: 600; }}
QPushButton#danger:hover {{ background: {t['danger_btn_dark']}; }}
QPushButton#danger:disabled {{ background: {t['danger_dis_bg']}; border-color: {t['danger_dis_bg']}; color: {t['danger_dis_text']}; }}

QLineEdit {{ background: {t['surface']}; color: {t['ink']}; border: 1px solid {t['input_border']};
             border-radius: 4px; padding: 6px 8px; selection-background-color: {t['sel_bg']}; selection-color: {t['ink']}; }}
QLineEdit:focus {{ border: 2px solid {t['focus']}; padding: 5px 7px; }}
QComboBox QLineEdit {{ border: none; background: transparent; padding: 0; }}
QComboBox QAbstractItemView {{ background: {t['surface']}; color: {t['ink']}; selection-background-color: {t['sel_bg']};
                               selection-color: {t['ink']}; border: 1px solid {t['border']}; }}

QTableWidget, QTreeWidget {{ background: {t['surface']}; color: {t['ink']}; border: 1px solid {t['border']}; border-radius: 6px;
                             gridline-color: {t['border']}; alternate-background-color: {t['row_alt']}; }}
QTableWidget::item, QTreeWidget::item {{ padding: 4px 4px; }}
QTableWidget::item:selected, QTreeWidget::item:selected {{ background: {t['sel_bg']}; color: {t['ink']}; }}
QHeaderView::section {{ background: {t['surface_alt']}; color: {t['ink']}; border: none;
                        border-bottom: 1px solid {t['border']}; padding: 7px 8px; font-weight: 600; }}
QTextBrowser#notes {{ background: {t['surface']}; color: {t['ink']}; border: 1px solid {t['border']};
                      border-radius: 6px; padding: 6px; }}
QProgressBar {{ background: {t['surface_alt']}; border: none; border-radius: 4px; height: 10px; text-align: center; color: transparent; }}
QProgressBar::chunk {{ background: {t['accent']}; border-radius: 4px; }}
QPlainTextEdit {{ background: {t['console_bg']}; color: {t['console_text']}; border-radius: 6px;
                  border: 1px solid {t['border']}; padding: 8px;
                  font-family: Consolas, "Cascadia Mono", monospace; font-size: 9.5pt; }}
QCheckBox {{ spacing: 8px; color: {t['ink']}; }}
QMenuBar {{ background: {t['surface']}; color: {t['ink']}; border-bottom: 1px solid {t['border']}; }}
QMenuBar::item {{ padding: 5px 10px; background: transparent; }}
QMenuBar::item:selected {{ background: {t['sel_bg']}; }}
QMenu {{ background: {t['surface']}; color: {t['ink']}; border: 1px solid {t['border']}; }}
QMenu::item {{ padding: 6px 22px; }}
QMenu::item:selected {{ background: {t['sel_bg']}; }}
QMenu::separator {{ height: 1px; background: {t['border']}; margin: 4px 8px; }}
"""


def apply_theme(app: QApplication, requested: str = "system") -> bool:
    """requested: 'system' | 'light' | 'dark'. Returns True when the dark theme is active."""
    _state["mode"] = requested if requested in ("system", "light", "dark") else "system"
    dark = system_is_dark(app) if _state["mode"] == "system" else _state["mode"] == "dark"
    _state["dark"] = dark
    t = current()
    app.setStyle("Fusion")
    app.setFont(QFont("Segoe UI", 10))
    app.setPalette(_palette(t))
    app.setStyleSheet(_qss(t))
    return dark


def watch_system(app: QApplication, callback) -> None:
    """Periodic poll or event hook for Windows theme changes if running under system mode."""
    pass
