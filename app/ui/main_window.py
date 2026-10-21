from __future__ import annotations

import os

from PyQt5.QtCore import QSettings, QUrl, Qt
from PyQt5.QtGui import QDesktopServices
from PyQt5.QtWidgets import (QAction, QActionGroup, QCheckBox, QDialog, QFrame, QHBoxLayout, QLabel,
                             QMainWindow, QMessageBox, QStackedWidget,
                             QVBoxLayout, QWidget)

from app import (APP_NAME, APP_VERSION, INTERNAL_NOTICE_BODY,
                 INTERNAL_NOTICE_TITLE, ORGANIZATION)
from app.core import audit
from . import theme
from .connect_page import ConnectPage
from .plants_page import PlantsPage
from .review_page import ReviewPage
from .run_page import RunPage
from .widgets import button, label

STEPS = ["Connect", "Choose plants", "Review", "Delete"]
CHECK = "\u2713"


class AppState:
    def __init__(self):
        self.site = None
        self.backend = None
        self.plants = []
        self.plan = None

    def close(self):
        if self.backend:
            try:
                self.backend.close()
            except Exception:
                pass
        self.site = self.backend = self.plan = None
        self.plants = []


class NoticeDialog(QDialog):
    def __init__(self, parent=None, first_run: bool = True):
        super().__init__(parent)
        from PyQt5.QtWidgets import QApplication
        self.setWindowTitle(f"{APP_NAME} - {INTERNAL_NOTICE_TITLE}")
        self.setWindowIcon(QApplication.windowIcon())
        self.setModal(True)
        self.setMinimumWidth(520)
        v = QVBoxLayout(self)
        v.setContentsMargins(24, 22, 24, 18)
        v.setSpacing(12)
        v.addWidget(label(INTERNAL_NOTICE_TITLE, "h1"))
        v.addWidget(label(INTERNAL_NOTICE_BODY.replace("\n\n", "<br><br>")))
        row = QHBoxLayout()
        row.addStretch(1)
        if first_run:
            b_exit = button("Exit")
            b_exit.clicked.connect(self.reject)
            row.addWidget(b_exit)
            b_ok = button("I am authorized - continue", "primary")
        else:
            b_ok = button("Close", "primary")
        b_ok.clicked.connect(self.accept)
        row.addWidget(b_ok)
        v.addLayout(row)


class MainWindow(QMainWindow):
    def __init__(self):
        super().__init__()
        self.state = AppState()
        self._busy = False
        self.setWindowTitle(f"{APP_NAME} {APP_VERSION} - Internal use only")
        self.resize(1200, 780)
        self.setMinimumSize(1000, 660)

        central = QWidget()
        root = QHBoxLayout(central)
        root.setContentsMargins(0, 0, 0, 0)
        root.setSpacing(0)
        root.addWidget(self._build_rail())

        right = QWidget()
        rv = QVBoxLayout(right)
        rv.setContentsMargins(0, 0, 0, 0)
        rv.setSpacing(0)
        banner = QLabel("Internal use only. For authorized Smart Engineering Manager administrators. "
                        "Deleted plants, databases and files cannot be recovered.")
        banner.setObjectName("banner")
        rv.addWidget(banner)
        self.stack = QStackedWidget()
        self.connect_page = ConnectPage(self)
        self.plants_page = PlantsPage(self)
        self.review_page = ReviewPage(self)
        self.run_page = RunPage(self)
        self.pages = [self.connect_page, self.plants_page, self.review_page, self.run_page]
        for p in self.pages:
            self.stack.addWidget(p)
        rv.addWidget(self.stack, 1)
        root.addWidget(right, 1)
        self.setCentralWidget(central)
        self._build_menu()
        self.go(0)

    # ---- chrome ---------------------------------------------------------------------
    def _build_rail(self) -> QFrame:
        rail = QFrame()
        rail.setObjectName("rail")
        rail.setFixedWidth(228)
        v = QVBoxLayout(rail)
        v.setContentsMargins(0, 24, 0, 18)
        v.setSpacing(0)
        t = QLabel(APP_NAME)
        t.setObjectName("railTitle")
        t.setContentsMargins(18, 0, 12, 0)
        ver = QLabel(f"Version {APP_VERSION}")
        ver.setObjectName("railVersion")
        ver.setContentsMargins(18, 0, 12, 22)
        v.addWidget(t)
        v.addWidget(ver)
        self.step_labels = []
        for i, s in enumerate(STEPS):
            lb = QLabel(f"{i + 1}   {s}")
            lb.setObjectName("stepIdle")
            self.step_labels.append(lb)
            v.addWidget(lb)
        v.addStretch(1)
        note = QLabel("Internal use only.\nThis tool permanently deletes data. "
                      "Every session is logged on this computer.")
        note.setObjectName("railNotice")
        note.setWordWrap(True)
        wrap = QHBoxLayout()
        wrap.setContentsMargins(14, 0, 14, 0)
        wrap.addWidget(note)
        v.addLayout(wrap)
        return rail

    def _build_menu(self):
        view = self.menuBar().addMenu("View")
        group = QActionGroup(self)
        group.setExclusive(True)
        saved = QSettings(ORGANIZATION, "DeletePlantUtility").value("theme", "system")
        for key, text in (("system", "Match Windows setting"), ("light", "Light"), ("dark", "Dark")):
            a = QAction(text, self, checkable=True)
            a.setChecked(key == saved)
            a.triggered.connect(lambda _=False, k=key: self.set_theme(k))
            group.addAction(a)
            view.addAction(a)
        m = self.menuBar().addMenu("Help")
        a1 = QAction("Internal use notice", self)
        a1.triggered.connect(lambda: NoticeDialog(self, first_run=False).exec_())
        a2 = QAction("Open log folder", self)
        a2.triggered.connect(self.open_log_folder)
        m.addAction(a1)
        m.addAction(a2)

    def set_theme(self, mode: str):
        from PyQt5.QtWidgets import QApplication
        QSettings(ORGANIZATION, "DeletePlantUtility").setValue("theme", mode)
        theme.apply_theme(QApplication.instance(), mode)
        self.on_theme_changed()

    def on_theme_changed(self):
        self.review_page.retint()
        self._style_steps(self.stack.currentIndex())

    def _style_steps(self, current: int):
        for i, lb in enumerate(self.step_labels):
            name = "stepNow" if i == current else ("stepDone" if i < current else "stepIdle")
            lb.setObjectName(name)
            text = STEPS[i]
            mark = CHECK if i < current else str(i + 1)
            lb.setText(f"{mark}   {text}")
            lb.style().unpolish(lb)
            lb.style().polish(lb)

    # ---- navigation -----------------------------------------------------------------
    def go(self, index: int):
        self.stack.setCurrentIndex(index)
        self._style_steps(index)
        self.pages[index].on_enter()

    def set_busy(self, busy: bool):
        self._busy = busy

    def open_log_folder(self):
        QDesktopServices.openUrl(QUrl.fromLocalFile(audit.log_dir()))

    def confirm_notice(self) -> bool:
        return NoticeDialog(self, first_run=True).exec_() == QDialog.Accepted

    def closeEvent(self, e):
        if self._busy:
            QMessageBox.warning(self, APP_NAME,
                                "An operation is still running. Wait for it to finish, or use "
                                "'Stop after current step' on the Delete page.")
            e.ignore()
            return
        self.state.close()
        audit.write("info", "Application closed")
        e.accept()
