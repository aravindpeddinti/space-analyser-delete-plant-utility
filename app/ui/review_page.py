from __future__ import annotations

import html

from PyQt5.QtCore import QTimer, Qt
from PyQt5.QtGui import QBrush, QColor
from PyQt5.QtWidgets import (QCheckBox, QHBoxLayout, QHeaderView, QLineEdit,
                             QTextBrowser, QTreeWidget, QTreeWidgetItem,
                             QVBoxLayout, QWidget)

from app.core import audit
from app.core.processes import config_path, find_running_apps
from . import theme
from .widgets import TaskThread, button, card, label

KIND_TEXT = {
    "schema": "Database schema",
    "database": "Database",
    "login": "SQL Server login",
    "tablespace": "Tablespace",
    "temp_tablespace": "Temporary tablespace",
    "site_records": "Site records",
    "site_folder": "Site folder",
    "site_file": "Site configuration file",
}
UR = Qt.UserRole


class ReviewPage(QWidget):
    def __init__(self, win):
        super().__init__()
        self.setObjectName("page")
        self.win = win
        self.targets = []
        self.phrase = ""
        self.running_apps: list[str] = []
        self._checking = False
        self._app_worker = None
        self.timer = QTimer(self)
        self.timer.setInterval(4000)
        self.timer.timeout.connect(self._check_apps)

        v = QVBoxLayout(self)
        v.setContentsMargins(36, 30, 36, 24)
        v.setSpacing(10)
        v.addWidget(label("Review what will be deleted", "h1"))
        v.addWidget(label("Everything below was read from the site and is only a plan. Untick anything "
                          "you want to keep. Items shown in red are blocked by safety checks and will "
                          "never be touched.", "muted"))

        self.app_warn, awl = card("dangerCard")
        self.app_warn_text = label("", "")
        awl.addWidget(self.app_warn_text)
        self.app_warn.setVisible(False)
        v.addWidget(self.app_warn)

        self.notes = QTextBrowser()
        self.notes.setObjectName("notes")
        self.notes.setMaximumHeight(92)
        self.notes.setVisible(False)
        v.addWidget(self.notes)

        self.tree = QTreeWidget()
        self.tree.setColumnCount(3)
        self.tree.setHeaderLabels(["Item", "What it is", "Status"])
        self.tree.setAlternatingRowColors(True)
        self.tree.setUniformRowHeights(True)
        hd = self.tree.header()
        hd.setSectionResizeMode(0, QHeaderView.Stretch)
        hd.setSectionResizeMode(1, QHeaderView.ResizeToContents)
        hd.setSectionResizeMode(2, QHeaderView.ResizeToContents)
        self.tree.itemChanged.connect(self._changed)
        v.addWidget(self.tree, 1)

        self.summary = label("", "h2")
        v.addWidget(self.summary)

        self.confirm_card, cl = card("dangerCard")
        self.confirm_text = label("", "")
        cl.addWidget(self.confirm_text)
        row = QHBoxLayout()
        self.typed = QLineEdit()
        self.typed.textChanged.connect(self._gate)
        self.ack = QCheckBox("I understand this is permanent and cannot be undone.")
        self.ack.toggled.connect(self._gate)
        row.addWidget(self.typed, 1)
        cl.addLayout(row)
        cl.addWidget(self.ack)
        v.addWidget(self.confirm_card)

        foot = QHBoxLayout()
        foot.addStretch(1)
        b_back = button("Back")
        b_back.clicked.connect(lambda: self.win.go(1))
        self.b_go = button("Delete permanently", "danger")
        self.b_go.setEnabled(False)
        self.b_go.clicked.connect(self._go)
        foot.addWidget(b_back)
        foot.addWidget(self.b_go)
        v.addLayout(foot)

    # ---- build tree -----------------------------------------------------------------
    def on_enter(self):
        plan = self.win.state.plan
        self.targets = []
        self.tree.blockSignals(True)
        self.tree.clear()
        lines = []
        f = self.tree.font()
        f.setBold(True)

        for pp in plan.plants:
            top = QTreeWidgetItem([pp.plant.name, f"Plant - {len(pp.plant.projects)} project(s)", ""])
            top.setFlags(top.flags() | Qt.ItemIsUserCheckable)
            top.setFont(0, f)
            self.tree.addTopLevelItem(top)
            for t in pp.targets:
                self._add(top, t)
            top.setExpanded(True)
            self._sync_parent(top)
            lines += [f"<b>{html.escape(pp.plant.name)}:</b> {html.escape(w)}" for w in pp.warnings]
            lines += [f"<i>{html.escape(pp.plant.name)}: {html.escape(n)}</i>" for n in pp.notes]

        if plan.tablespaces:
            top = QTreeWidgetItem(["Oracle tablespaces (optional)", "Dropped only if no other schema uses them", ""])
            top.setFlags(top.flags() | Qt.ItemIsUserCheckable)
            top.setFont(0, f)
            self.tree.addTopLevelItem(top)
            for t in plan.tablespaces:
                self._add(top, t)
            top.setExpanded(True)
            self._sync_parent(top)

        if plan.delete_site and plan.site_targets:
            top = QTreeWidgetItem([f"Site - {plan.site_name}", "Site database, schemas, files and folders", ""])
            top.setFlags(top.flags() | Qt.ItemIsUserCheckable)
            top.setFont(0, f)
            self.tree.addTopLevelItem(top)
            for t in plan.site_targets:
                self._add(top, t)
            top.setExpanded(True)
            self._sync_parent(top)

        self.tree.blockSignals(False)
        self.notes.setVisible(bool(lines))
        self.notes.setHtml("<br>".join(lines))

        n = len(plan.plants)
        if plan.delete_site:
            if n == 0:
                self.phrase = f"DELETE SITE {plan.site_name}"
            else:
                self.phrase = f"DELETE SITE AND {n} PLANTS"
        else:
            self.phrase = plan.plants[0].plant.name if n == 1 else f"DELETE {n} PLANTS"

        self.confirm_text.setText(
            f"To continue, type <b>{html.escape(self.phrase)}</b> below and tick the box. "
            f"Deleting items will run without further prompts.")
        self.typed.setPlaceholderText(self.phrase)
        self.typed.clear()
        self.ack.setChecked(False)
        self._refresh()

    def _add(self, parent: QTreeWidgetItem, t):
        idx = len(self.targets)
        self.targets.append(t)
        if t.kind in ("folder", "site_folder", "site_file"):
            cols = [t.name, t.label or KIND_TEXT.get(t.kind, t.kind), ""]
        else:
            cols = [t.label or t.name, KIND_TEXT.get(t.kind, t.kind) + (f" on {t.server}" if t.server else ""), ""]
        cols[2] = f"Blocked: {t.blocked}" if t.blocked else t.note
        it = QTreeWidgetItem(parent, cols)
        it.setData(0, UR, idx)
        if not t.blocked:
            it.setFlags(it.flags() | Qt.ItemIsUserCheckable)
            it.setCheckState(0, Qt.Checked if t.enabled else Qt.Unchecked)
        self._tint(it, t)

    @staticmethod
    def _tint(it: QTreeWidgetItem, t):
        tk = theme.current()
        if t.blocked:
            for c in range(3):
                it.setForeground(c, QBrush(QColor(tk["danger"])))
        elif t.note:
            it.setForeground(2, QBrush(QColor(tk["muted"])))

    def retint(self):
        def walk(item):
            idx = item.data(0, UR)
            if idx is not None:
                self._tint(item, self.targets[idx])
            for i in range(item.childCount()):
                walk(item.child(i))
        for i in range(self.tree.topLevelItemCount()):
            walk(self.tree.topLevelItem(i))

    @staticmethod
    def _checkable_children(parent: QTreeWidgetItem):
        return [parent.child(i) for i in range(parent.childCount())
                if parent.child(i).flags() & Qt.ItemIsUserCheckable]

    def _sync_parent(self, parent: QTreeWidgetItem):
        kids = self._checkable_children(parent)
        states = {k.checkState(0) for k in kids}
        if not kids or states == {Qt.Unchecked}:
            st = Qt.Unchecked
        elif states == {Qt.Checked}:
            st = Qt.Checked
        else:
            st = Qt.PartiallyChecked
        parent.setCheckState(0, st)

    def _changed(self, item: QTreeWidgetItem, col: int):
        if col != 0:
            return
        idx = item.data(0, UR)
        self.tree.blockSignals(True)
        try:
            if idx is None:
                state = item.checkState(0)
                if state == Qt.PartiallyChecked:
                    state = Qt.Checked
                    item.setCheckState(0, state)
                for kid in self._checkable_children(item):
                    kid.setCheckState(0, state)
                    self.targets[kid.data(0, UR)].enabled = (state == Qt.Checked)
            else:
                self.targets[idx].enabled = (item.checkState(0) == Qt.Checked)
                if item.parent():
                    self._sync_parent(item.parent())
        finally:
            self.tree.blockSignals(False)
        self._refresh()

    # ---- summary & confirmation gate ------------------------------------------------
    def _refresh(self):
        plan = self.win.state.plan
        act = [t for t in plan.all_targets() if t.active]
        count = lambda *k: len([t for t in act if t.kind in k])
        summary_parts = []
        if count('folder', 'site_folder'):
            summary_parts.append(f"{count('folder', 'site_folder')} folders")
        if count('schema', 'database'):
            summary_parts.append(f"{count('schema', 'database')} databases / schemas")
        if count('login'):
            summary_parts.append(f"{count('login')} logins")
        if count('tablespace', 'temp_tablespace'):
            summary_parts.append(f"{count('tablespace', 'temp_tablespace')} tablespaces")
        if count('site_records'):
            summary_parts.append(f"{count('site_records')} site record sets")
        if count('site_file'):
            summary_parts.append(f"{count('site_file')} site config file")

        self.summary.setText("   ".join(summary_parts) if summary_parts else "0 items selected")
        self._gate()

    def _gate(self, *_):
        plan = self.win.state.plan
        has_work = plan is not None and plan.total_steps() > 0
        ok = (has_work and not self.running_apps
              and self.typed.text().strip() == self.phrase and self.ack.isChecked())
        self.b_go.setEnabled(bool(ok))

    # ---- running applications ---------------------------------------------------------
    def showEvent(self, e):
        super().showEvent(e)
        self._check_apps()
        self.timer.start()

    def hideEvent(self, e):
        self.timer.stop()
        super().hideEvent(e)

    def _check_apps(self):
        if self._checking:
            return
        self._checking = True
        t = TaskThread(find_running_apps, self)
        t.succeeded.connect(self._apps_found)
        t.failed.connect(lambda _e: self._apps_found([]))
        t.finished.connect(lambda: setattr(self, "_checking", False))
        self._app_worker = t
        t.start()

    def _apps_found(self, apps):
        if apps != self.running_apps:
            self.running_apps = apps
            if apps:
                audit.write("warn", "Blocked: applications still open: " + ", ".join(apps))
        self.app_warn.setVisible(bool(apps))
        if apps:
            self.app_warn_text.setText(
                "<b>Close these applications before deleting:</b><br>" + "<br>".join(html.escape(a) for a in apps)
                + "<br><br>Open applications lock plant folders and keep database sessions alive, "
                  "so the delete would fail or leave a plant half-removed. This page re-checks "
                  "automatically. If a name is wrong, edit<br><i>"
                + html.escape(config_path()) + "</i>")
        self._gate()

    def _go(self):
        try:
            self.running_apps = find_running_apps()
        except Exception:
            self.running_apps = []
        if self.running_apps:
            self._apps_found(self.running_apps)
            return
        plan = self.win.state.plan
        audit.write("warn", "OPERATOR CONFIRMED DELETION. Plan:")
        for pp in plan.plants:
            for t in pp.targets:
                if t.active:
                    audit.write("warn", f"  [{pp.plant.name}] {t.kind}: {t.name}")
        for t in plan.tablespaces:
            if t.active:
                audit.write("warn", f"  [tablespace] {t.kind}: {t.name} @ {t.server}")
        if plan.delete_site:
            for t in plan.site_targets:
                if t.active:
                    audit.write("warn", f"  [site] {t.kind}: {t.name}")
        self.win.go(3)
        self.win.run_page.start()
