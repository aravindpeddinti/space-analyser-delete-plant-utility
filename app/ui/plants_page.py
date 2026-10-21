from __future__ import annotations

from PyQt5.QtCore import Qt
from PyQt5.QtWidgets import (QAbstractItemView, QCheckBox, QHBoxLayout, QHeaderView,
                             QLineEdit, QProgressBar, QTableWidget,
                             QTableWidgetItem, QVBoxLayout, QWidget)

from app.core import audit
from .widgets import TaskThread, button, label


class PlantsPage(QWidget):
    def __init__(self, win):
        super().__init__()
        self.setObjectName("page")
        self.win = win
        self.worker = None
        self._shown = []

        v = QVBoxLayout(self)
        v.setContentsMargins(36, 30, 36, 24)
        v.setSpacing(12)
        v.addWidget(label("Choose plants to delete", "h1"))
        self.site_line = label("", "muted")
        v.addWidget(self.site_line)

        bar = QHBoxLayout()
        self.filter = QLineEdit()
        self.filter.setPlaceholderText("Filter by plant name")
        self.filter.setClearButtonEnabled(True)
        self.filter.textChanged.connect(self._apply_filter)
        bar.addWidget(self.filter, 1)
        b_all = button("Select shown")
        b_none = button("Clear selection")
        b_ref = button("Refresh")
        b_all.clicked.connect(lambda: self._set_all(True))
        b_none.clicked.connect(lambda: self._set_all(False))
        b_ref.clicked.connect(self.reload)
        for b in (b_all, b_none, b_ref):
            bar.addWidget(b)
        v.addLayout(bar)

        self.table = QTableWidget(0, 4)
        self.table.setHorizontalHeaderLabels(["", "Plant", "Projects", "Plant folder"])
        self.table.verticalHeader().setVisible(False)
        self.table.setAlternatingRowColors(True)
        self.table.setShowGrid(False)
        self.table.setSelectionBehavior(QAbstractItemView.SelectRows)
        self.table.setEditTriggers(QAbstractItemView.NoEditTriggers)
        h = self.table.horizontalHeader()
        h.setSectionResizeMode(0, QHeaderView.Fixed)
        self.table.setColumnWidth(0, 40)
        h.setSectionResizeMode(1, QHeaderView.ResizeToContents)
        h.setSectionResizeMode(2, QHeaderView.ResizeToContents)
        h.setSectionResizeMode(3, QHeaderView.Stretch)
        self.table.itemChanged.connect(lambda *_: self._update_count())
        self.table.cellClicked.connect(self._row_clicked)
        v.addWidget(self.table, 1)

        # Empty site options
        self.empty_box = QWidget()
        ev = QVBoxLayout(self.empty_box)
        ev.setContentsMargins(0, 10, 0, 10)
        ev.addWidget(label("No plants were found in this site.", "muted"))
        self.b_delete_empty = button("Delete Empty Site", "danger")
        self.b_delete_empty.clicked.connect(self._delete_empty_site)
        ev.addWidget(self.b_delete_empty)
        self.empty_box.setVisible(False)
        v.addWidget(self.empty_box)

        # Delete site option for populated site
        self.chk_delete_site = QCheckBox("Delete Site & All Plants (completely remove all plants AND the site itself)")
        self.chk_delete_site.stateChanged.connect(self._on_delete_site_toggled)
        v.addWidget(self.chk_delete_site)

        self.msg = label("", "error")
        v.addWidget(self.msg)
        self.busy_bar = QProgressBar()
        self.busy_bar.setRange(0, 0)
        self.busy_bar.setVisible(False)
        v.addWidget(self.busy_bar)

        foot = QHBoxLayout()
        self.count = label("0 plants selected", "muted")
        foot.addWidget(self.count, 1)
        self.b_back = button("Back")
        self.b_back.clicked.connect(lambda: self.win.go(0))
        self.b_plan = button("Build deletion plan", "primary")
        self.b_plan.clicked.connect(self._build_plan)
        foot.addWidget(self.b_back)
        foot.addWidget(self.b_plan)
        v.addLayout(foot)

    # ---- population -----------------------------------------------------------------
    def on_enter(self):
        st = self.win.state
        if st.site:
            self.site_line.setText(f"{st.site.db_label} - {st.site.server} - site schema {st.site.site_schema}. "
                                   f"Nothing is changed until you confirm on the review step.")
        self.msg.setText("")
        self.chk_delete_site.setChecked(False)
        self._fill()

    def _fill(self):
        plants = self.win.state.plants or []
        keep = {self._shown[r].root_id for r in self._selected_rows()} if self._shown else set()
        self._shown = list(plants)
        self.table.blockSignals(True)
        self.table.setRowCount(0)
        for p in plants:
            r = self.table.rowCount()
            self.table.insertRow(r)
            chk = QTableWidgetItem()
            chk.setFlags(Qt.ItemIsUserCheckable | Qt.ItemIsEnabled)
            chk.setCheckState(Qt.Checked if p.root_id in keep else Qt.Unchecked)
            self.table.setItem(r, 0, chk)
            self.table.setItem(r, 1, QTableWidgetItem(p.name))
            self.table.setItem(r, 2, QTableWidgetItem(str(len(p.projects))))
            self.table.setItem(r, 3, QTableWidgetItem(p.path))
        self.table.blockSignals(False)

        is_empty = (len(plants) == 0)
        self.table.setVisible(not is_empty)
        self.empty_box.setVisible(is_empty)
        self.chk_delete_site.setVisible(not is_empty)

        self._apply_filter()
        self._update_count()

    def reload(self):
        st = self.win.state
        if not st.backend:
            return
        self.win.set_busy(True)
        self.busy_bar.setVisible(True)
        self.msg.setText("")
        self.worker = TaskThread(st.backend.list_plants, self)
        self.worker.succeeded.connect(self._reloaded)
        self.worker.failed.connect(self._reload_failed)
        self.worker.start()

    def _reloaded(self, plants):
        self.win.set_busy(False)
        self.busy_bar.setVisible(False)
        self.win.state.plants = plants
        self._fill()

    def _reload_failed(self, err):
        self.win.set_busy(False)
        self.busy_bar.setVisible(False)
        self.msg.setText(f"Could not refresh the plant list.\n{err}")

    # ---- selection ------------------------------------------------------------------
    def _row_clicked(self, row, col):
        if col != 0:
            it = self.table.item(row, 0)
            it.setCheckState(Qt.Unchecked if it.checkState() == Qt.Checked else Qt.Checked)

    def _apply_filter(self):
        q = self.filter.text().strip().lower()
        for r in range(self.table.rowCount()):
            name = self.table.item(r, 1).text().lower()
            self.table.setRowHidden(r, bool(q) and q not in name)

    def _set_all(self, on: bool):
        self.table.blockSignals(True)
        for r in range(self.table.rowCount()):
            if on and self.table.isRowHidden(r):
                continue
            self.table.item(r, 0).setCheckState(Qt.Checked if on else Qt.Unchecked)
        self.table.blockSignals(False)
        self._update_count()

    def _on_delete_site_toggled(self, state: int):
        if state == Qt.Checked:
            self._set_all(True)
        self._update_count()

    def _selected_rows(self) -> list[int]:
        return [r for r in range(self.table.rowCount())
                if self.table.item(r, 0).checkState() == Qt.Checked]

    def _update_count(self):
        n = len(self._selected_rows())
        total = self.table.rowCount()
        self.count.setText(f"{n} of {total} plants selected")

        if self.chk_delete_site.isChecked():
            self.b_plan.setText("Build Site Deletion Plan")
            self.b_plan.setEnabled(True)
        else:
            self.b_plan.setText("Build deletion plan")
            self.b_plan.setEnabled(n > 0)

    # ---- next step ------------------------------------------------------------------
    def _delete_empty_site(self):
        st = self.win.state
        audit.write("info", f"Planning deletion of empty site: {st.site.site_schema}")
        self.win.set_busy(True)
        self.busy_bar.setVisible(True)
        self.msg.setText("")
        self.worker = TaskThread(lambda: st.backend.build_plan([], delete_site=True), self)
        self.worker.succeeded.connect(self._plan_ready)
        self.worker.failed.connect(self._plan_failed)
        self.worker.start()

    def _build_plan(self):
        st = self.win.state
        rows = self._selected_rows()
        delete_site = self.chk_delete_site.isChecked()
        if not rows and not delete_site:
            return
        chosen = [self._shown[r] for r in rows]
        audit.write("info", ("Planning deletion of site and: " if delete_site else "Planning deletion of: ") +
                    ", ".join(p.name for p in chosen))
        self.win.set_busy(True)
        self.busy_bar.setVisible(True)
        self.msg.setText("")
        self.worker = TaskThread(lambda: st.backend.build_plan(chosen, delete_site=delete_site), self)
        self.worker.succeeded.connect(self._plan_ready)
        self.worker.failed.connect(self._plan_failed)
        self.worker.start()

    def _plan_ready(self, plan):
        self.win.set_busy(False)
        self.busy_bar.setVisible(False)
        self.win.state.plan = plan
        self.win.go(2)

    def _plan_failed(self, err):
        self.win.set_busy(False)
        self.busy_bar.setVisible(False)
        audit.write("error", f"Planning failed: {err}")
        self.msg.setText(f"The deletion plan could not be built. Nothing was changed.\n{err}")
