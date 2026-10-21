from __future__ import annotations

import html
import os
import shutil

from PyQt5.QtCore import QTimer, Qt
from PyQt5.QtGui import QDesktopServices
from PyQt5.QtWidgets import (QFileDialog, QHBoxLayout, QMessageBox, QPlainTextEdit,
                             QProgressBar, QVBoxLayout, QWidget)

from app.core import audit
from .widgets import RunThread, button, label

COLORS = {"info": "#D5DDE5", "ok": "#7DDC9E", "warn": "#F4D58A", "error": "#FF8A80"}


class RunPage(QWidget):
    def __init__(self, win):
        super().__init__()
        self.setObjectName("page")
        self.win = win
        self.worker = None

        v = QVBoxLayout(self)
        v.setContentsMargins(36, 30, 36, 24)
        v.setSpacing(12)
        self.title = label("Deleting", "h1")
        v.addWidget(self.title)
        self.status = label("", "muted")
        v.addWidget(self.status)
        self.bar = QProgressBar()
        v.addWidget(self.bar)
        self.log = QPlainTextEdit()
        self.log.setReadOnly(True)
        v.addWidget(self.log, 1)
        self.result = label("", "h2")
        v.addWidget(self.result)

        foot = QHBoxLayout()
        self.b_cancel = button("Stop after current step")
        self.b_cancel.setFocusPolicy(Qt.NoFocus)
        self.b_cancel.clicked.connect(self._cancel)
        self.b_save = button("Save log...")
        self.b_save.clicked.connect(self._save_log)
        self.b_open = button("Open log folder")
        self.b_open.clicked.connect(self.win.open_log_folder)
        self.b_more = button("Delete more plants")
        self.b_more.clicked.connect(self._more)
        self.b_done = button("Finish", "primary")
        self.b_done.clicked.connect(self.win.close)
        foot.addWidget(self.b_save)
        foot.addWidget(self.b_open)
        foot.addStretch(1)
        for b in (self.b_cancel, self.b_more, self.b_done):
            foot.addWidget(b)
        v.addLayout(foot)
        self._finished_ui(False)

    def on_enter(self):
        pass

    def _finished_ui(self, finished: bool):
        self.b_cancel.setVisible(not finished)
        self.b_more.setVisible(finished)
        self.b_done.setVisible(finished)
        self.b_save.setEnabled(finished)

    def start(self):
        st = self.win.state
        plan = st.plan
        self.log.clear()
        self.result.setText("")
        self.title.setText("Deleting")
        self.bar.setRange(0, max(plan.total_steps(), 1))
        self.bar.setValue(0)
        self.status.setText("Starting...")
        self.b_cancel.setEnabled(False)          # brief lock-out so a double-click on Delete cannot hit Stop
        QTimer.singleShot(2000, lambda: self.b_cancel.setEnabled(bool(self.worker and self.worker.isRunning())))
        self.b_cancel.setText("Stop after current step")
        self._finished_ui(False)
        self.win.set_busy(True)
        self.worker = RunThread(st.backend, plan, self)
        self.worker.log.connect(self._on_log)
        self.worker.progress.connect(self._on_progress)
        self.worker.succeeded.connect(self._on_done)
        self.worker.failed.connect(self._on_failed)
        self.worker.start()

    def _on_log(self, level: str, msg: str):
        audit.write(level, msg)
        self.log.appendHtml(f'<span style="color:{COLORS.get(level, COLORS["info"])}">{html.escape(msg)}</span>')

    def _on_progress(self, done: int, total: int, msg: str):
        self.bar.setRange(0, max(total, 1))
        self.bar.setValue(done)
        if msg:
            self.status.setText(msg)

    def _cancel(self):
        if QMessageBox.question(self, "Stop deleting?",
                                "Stop after the current step?\n\nItems already deleted stay deleted.",
                                QMessageBox.Yes | QMessageBox.No,
                                QMessageBox.No) != QMessageBox.Yes:
            return
        self._on_log("warn", "Operator pressed 'Stop after current step'.")
        if self.worker:
            self.worker.request_cancel()
        self.b_cancel.setEnabled(False)
        self.b_cancel.setText("Stopping...")
        self.status.setText("Stopping after the current step finishes...")

    def _on_done(self, results):
        self.win.set_busy(False)
        ok = [r for r in results if r.status == "ok"]
        bad = [r for r in results if r.status != "ok"]
        self.title.setText("Finished" if not bad else "Finished with items that need attention")
        self.status.setText("")
        self.bar.setValue(self.bar.maximum())
        text = f"{len(ok)} of {len(results)} item(s) fully removed."
        if bad:
            text += " Needs attention: " + "; ".join(f"{r.plant} ({r.status})" for r in bad)
            for r in bad:
                for m in r.messages:
                    self._on_log("warn", f"{r.plant}: {m}")
        self.result.setText(text)
        audit.write("info", text)
        self._finished_ui(True)

    def _on_failed(self, err):
        self.win.set_busy(False)
        self.title.setText("Stopped by an unexpected error")
        self._on_log("error", str(err))
        self.result.setText("Some steps may have completed. Review the log, then rebuild the plan to see what remains.")
        self._finished_ui(True)

    def _more(self):
        self.win.go(1)
        self.win.plants_page.reload()

    def _save_log(self):
        src = audit.current_path()
        if not src or not os.path.isfile(src):
            return
        dest, _ = QFileDialog.getSaveFileName(self, "Save log", os.path.basename(src), "Log files (*.log);;All files (*.*)")
        if dest:
            shutil.copyfile(src, dest)
