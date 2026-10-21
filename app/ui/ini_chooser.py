from __future__ import annotations

from PyQt5.QtCore import Qt
from PyQt5.QtWidgets import (QDialog, QHBoxLayout, QHeaderView, QTreeWidget,
                             QTreeWidgetItem, QVBoxLayout)

from .widgets import button, label


class IniChooserDialog(QDialog):
    """Shown when more than one smartplantv4.ini is found under the chosen folder."""

    def __init__(self, parent, root: str, infos: list[dict], stopped_early: bool):
        super().__init__(parent)
        self.setWindowTitle("Choose the site file")
        self.setModal(True)
        self.resize(940, 420)
        self.selected = ""
        v = QVBoxLayout(self)
        v.setContentsMargins(22, 20, 22, 16)
        v.setSpacing(10)
        v.addWidget(label(f"{len(infos)} site files were found", "h2"))
        v.addWidget(label(f"Under {root}. Choose the one for the site you want to change.", "muted"))
        if stopped_early:
            v.addWidget(label("The search stopped early, so more files may exist. Choose a deeper "
                              "folder to narrow it down.", "error"))
        self.tree = QTreeWidget()
        self.tree.setColumnCount(5)
        self.tree.setHeaderLabels(["Site file", "Database", "Server / instance", "Site schema", "Modified"])
        self.tree.setRootIsDecorated(False)
        self.tree.setAlternatingRowColors(True)
        self.tree.setUniformRowHeights(True)
        h = self.tree.header()
        h.setSectionResizeMode(0, QHeaderView.Stretch)
        for c in (1, 2, 3, 4):
            h.setSectionResizeMode(c, QHeaderView.ResizeToContents)
        for i in infos:
            it = QTreeWidgetItem([i["path"], i["db"], i["server"], i["schema"],
                                  i["modified"] if not i["error"] else "Unreadable"])
            it.setData(0, Qt.UserRole, i["path"])
            it.setToolTip(0, i["path"] if not i["error"] else f'{i["path"]}\n{i["error"]}')
            self.tree.addTopLevelItem(it)
        self.tree.itemSelectionChanged.connect(self._sel)
        self.tree.itemDoubleClicked.connect(lambda *_: self._accept_if_selected())
        v.addWidget(self.tree, 1)
        row = QHBoxLayout()
        row.addStretch(1)
        b_cancel = button("Cancel")
        b_cancel.clicked.connect(self.reject)
        self.b_ok = button("Use this site file", "primary")
        self.b_ok.setEnabled(False)
        self.b_ok.clicked.connect(self._accept_if_selected)
        row.addWidget(b_cancel)
        row.addWidget(self.b_ok)
        v.addLayout(row)

    def _sel(self):
        self.b_ok.setEnabled(bool(self.tree.selectedItems()))

    def _accept_if_selected(self):
        items = self.tree.selectedItems()
        if items:
            self.selected = items[0].data(0, Qt.UserRole)
            self.accept()
