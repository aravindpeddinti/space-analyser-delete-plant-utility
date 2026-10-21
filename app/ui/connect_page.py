from __future__ import annotations

import os

from PyQt5.QtCore import QSettings, Qt
from PyQt5.QtWidgets import (QComboBox, QDialog, QFileDialog, QFormLayout, QFrame,
                             QGridLayout, QHBoxLayout, QLabel, QLineEdit,
                             QProgressBar, QVBoxLayout, QWidget)

from app import ORGANIZATION
from app.core import audit
from app.core.backends import make_backend
from app.core.errors import BackendError, ManualConnectionRequired
from app.core.ini_reader import (describe_ini, find_ini_files, read_site_ini,
                                 resolve_ini_path)
from .ini_chooser import IniChooserDialog
from .manual_oracle_dialog import ManualOracleDialog
from .widgets import TaskThread, button, card, label


class ConnectPage(QWidget):
    def __init__(self, win):
        super().__init__()
        self.setObjectName("page")
        self.win = win
        self.site = None
        self.worker = None
        self.settings = QSettings(ORGANIZATION, "DeletePlantUtility")
        self.setAcceptDrops(True)

        v = QVBoxLayout(self)
        v.setContentsMargins(36, 30, 36, 24)
        v.setSpacing(14)
        v.addWidget(label("Connect to a site", "h1"))
        v.addWidget(label("Select the smartplantv4.ini file of the Smart Engineering Manager "
                          "site. You can also drop the file onto this window.", "muted"))

        row = QHBoxLayout()
        self.path = QComboBox()
        self.path.setEditable(True)
        self.path.setMinimumWidth(480)
        self.path.lineEdit().setPlaceholderText(r"C:\...\smartplantv4.ini  or any folder - subfolders are searched")
        for p in self.settings.value("recent_ini", []) or []:
            self.path.addItem(p)
        self.path.setCurrentIndex(-1)
        row.addWidget(self.path, 1)
        b_browse = button("Browse...")
        b_browse.clicked.connect(self._browse)
        b_read = button("Read site file")
        b_read.clicked.connect(self._read_clicked)
        row.addWidget(b_browse)
        row.addWidget(b_read)
        v.addLayout(row)

        # Site details card
        self.site_card, cl = card()
        self.site_card.setVisible(False)
        cl.addWidget(label("Site found in this file", "h2"))
        grid = QGridLayout()
        grid.setHorizontalSpacing(24)
        grid.setVerticalSpacing(6)
        self.fields: dict[str, QLabel] = {}
        for i, (key, title) in enumerate([("db", "Database type"), ("server", "Server / instance"),
                                          ("sitedb", "Site database"), ("schema", "Site schema"),
                                          ("user", "Administrator account")]):
            t = label(title, "muted", wrap=False)
            val = QLabel("-")
            val.setTextInteractionFlags(Qt.TextSelectableByMouse)
            self.fields[key] = val
            grid.addWidget(t, i, 0)
            grid.addWidget(val, i, 1)
        grid.setColumnStretch(2, 1)
        cl.addLayout(grid)
        cl.addWidget(label("Check that this is the site you intend to change. The password is read "
                           "from the file and is never displayed or logged.", "muted"))
        v.addWidget(self.site_card)

        # Oracle options
        self.oracle_box, ol = card()
        self.oracle_box.setVisible(False)
        ol.addWidget(label("Oracle connection", "h2"))
        form = QFormLayout()
        form.setHorizontalSpacing(18)
        self.mode = QComboBox()
        self.mode.addItem("Thin - no Oracle client needed (recommended)", "thin")
        self.mode.addItem("Thick - use the installed Oracle client", "thick")
        form.addRow("Oracle driver", self.mode)
        ol.addLayout(form)
        self.manual = QFrame()
        mf = QFormLayout(self.manual)
        mf.setContentsMargins(0, 6, 0, 0)
        mf.setHorizontalSpacing(18)
        self.manual_msg = label("", "error")
        mf.addRow(self.manual_msg)
        tns_row = QHBoxLayout()
        self.tns = QLineEdit()
        self.tns.setPlaceholderText(r"Folder that contains tnsnames.ora (optional)")
        tb = button("Browse...")
        tb.clicked.connect(self._browse_tns)
        tns_row.addWidget(self.tns, 1)
        tns_row.addWidget(tb)
        mf.addRow("TNS folder", tns_row)
        self.host = QLineEdit()
        self.port = QLineEdit("1521")
        self.service = QLineEdit()
        mf.addRow("Host name", self.host)
        mf.addRow("Port", self.port)
        mf.addRow("Service name", self.service)
        self.manual.setVisible(False)
        ol.addWidget(self.manual)
        v.addWidget(self.oracle_box)

        v.addStretch(1)
        self.status = label("", "muted")
        v.addWidget(self.status)
        self.bar = QProgressBar()
        self.bar.setRange(0, 0)
        self.bar.setVisible(False)
        v.addWidget(self.bar)
        foot = QHBoxLayout()
        foot.addStretch(1)
        self.btn_connect = button("Connect", "primary")
        self.btn_connect.setEnabled(False)
        self.btn_connect.clicked.connect(self._connect)
        foot.addWidget(self.btn_connect)
        v.addLayout(foot)

    def on_enter(self):
        self.win.state.close()

    # ---- drag & drop --------------------------------------------------------------
    def dragEnterEvent(self, e):
        if e.mimeData().hasUrls():
            e.acceptProposedAction()

    def dropEvent(self, e):
        for u in e.mimeData().urls():
            p = u.toLocalFile()
            if p:
                self.path.setEditText(p)
                self._read_clicked()
                break

    # ---- actions ------------------------------------------------------------------
    def _browse(self):
        f, _ = QFileDialog.getOpenFileName(self, "Select smartplantv4.ini", "", "smartplantv4.ini (smartplantv4.ini);;All files (*.*)")
        if f:
            self.path.setEditText(f)
            self._read_clicked()

    def _browse_tns(self):
        d = QFileDialog.getExistingDirectory(self, "Folder that contains tnsnames.ora")
        if d:
            self.tns.setText(d)

    def _set_status(self, text: str, kind: str = "muted"):
        self.status.setObjectName(kind)
        self.status.setStyleSheet("")          # force re-polish after object-name change
        self.status.style().unpolish(self.status)
        self.status.style().polish(self.status)
        self.status.setText(text)

    def _read_clicked(self):
        raw = self.path.currentText().strip().strip('"')
        if raw and os.path.isdir(os.path.abspath(raw)):
            self._search_folder(os.path.abspath(raw))
            return
        try:
            self._load_site()
        except BackendError as e:
            self.site = None
            self.site_card.setVisible(False)
            self.oracle_box.setVisible(False)
            self.btn_connect.setEnabled(False)
            self._set_status(str(e), "error")

    # ---- folder search ------------------------------------------------------------
    def _search_folder(self, folder: str):
        self.site = None
        self.site_card.setVisible(False)
        self.oracle_box.setVisible(False)
        self.btn_connect.setEnabled(False)
        self._busy(True, f"Searching {folder} and its subfolders for smartplantv4.ini...")

        def work():
            found, scanned, stopped = find_ini_files(folder)
            return folder, found, scanned, stopped, [describe_ini(p) for p in found]

        self.worker = TaskThread(work, self)
        self.worker.succeeded.connect(self._search_done)
        self.worker.failed.connect(self._failed)
        self.worker.start()

    def _search_done(self, res):
        folder, found, scanned, stopped, infos = res
        self._busy(False)
        audit.write("info", f"Searched {folder}: {len(found)} site file(s) in {scanned} folder(s)")
        if not found:
            more = " The search stopped early." if stopped else ""
            self._set_status(f"No smartplantv4.ini was found in {folder} or its subfolders "
                             f"({scanned} folders searched).{more}", "error")
            return
        if len(found) == 1:
            self.path.setEditText(found[0])
            self._read_clicked()
            return
        dlg = IniChooserDialog(self, folder, infos, stopped)
        if dlg.exec_() == QDialog.Accepted and dlg.selected:
            self.path.setEditText(dlg.selected)
            self._read_clicked()
        else:
            self._set_status(f"{len(found)} site files were found. Choose one to continue.", "muted")

    def _load_site(self):
        p = resolve_ini_path(self.path.currentText())
        site = read_site_ini(p)
        self.site = site
        self.fields["db"].setText(site.db_label)
        self.fields["server"].setText(site.server)
        self.fields["sitedb"].setText(site.site_database or "-")
        self.fields["schema"].setText(site.site_schema)
        self.fields["user"].setText(site.username)
        self.site_card.setVisible(True)
        self.oracle_box.setVisible(site.db_type == "ORACLE")
        self.btn_connect.setEnabled(True)
        self._set_status("Site file read. Press Connect to continue.", "muted")
        recent = [p] + [x for x in (self.settings.value("recent_ini", []) or []) if x != p]
        self.settings.setValue("recent_ini", recent[:6])
        audit.write("info", f"Site file read: {p} ({site.db_label}, {site.server}, schema {site.site_schema})")
        return site

    def _connect(self, override_manual: dict | None = None):
        if not isinstance(override_manual, dict):
            override_manual = None
        raw = self.path.currentText().strip().strip('"')
        if raw and os.path.isdir(os.path.abspath(raw)):
            self._read_clicked()
            return
        try:
            site = self._load_site()
        except BackendError as e:
            self._set_status(str(e), "error")
            return

        manual = override_manual
        if not manual and self.manual.isVisible():
            manual = {"tns_dir": self.tns.text(), "host": self.host.text(),
                      "port": self.port.text(), "service": self.service.text()}
        mode = self.mode.currentData()
        self.win.state.close()
        self._busy(True, f"Connecting to {site.db_label} at {site.server}...")

        def work():
            backend = make_backend(site)
            try:
                backend.connect(manual, mode)
                plants = backend.list_plants()
            except Exception:
                backend.close()
                raise
            return backend, plants

        self.worker = TaskThread(work, self)
        self.worker.succeeded.connect(self._connected)
        self.worker.failed.connect(self._failed)
        self.worker.start()

    def _busy(self, on: bool, msg: str = ""):
        self.bar.setVisible(on)
        self.btn_connect.setEnabled(not on and self.site is not None)
        self.win.set_busy(on)
        if msg:
            self._set_status(msg, "muted")

    def _connected(self, result):
        backend, plants = result
        self._busy(False)
        st = self.win.state
        st.site, st.backend, st.plants, st.plan = self.site, backend, plants, None
        self.win.plants_page._shown = []
        audit.write("info", f"Connected to {self.site.server} as {self.site.username}; {len(plants)} plant(s) found")
        self._set_status("", "muted")
        self.win.go(1)

    def _failed(self, err):
        self._busy(False)
        if isinstance(err, ManualConnectionRequired) and self.site and self.site.db_type == "ORACLE":
            dlg = ManualOracleDialog(
                self,
                service_name=self.site.oracle_service or self.site.server,
                username=self.site.username,
                password=self.site.password,
                initial_host=self.host.text().strip(),
                initial_port=self.port.text().strip() or "1521"
            )
            if dlg.exec_() == QDialog.Accepted:
                details = dlg.get_details()
                self.host.setText(details["host"])
                self.port.setText(details["port"])
                self.service.setText(details["service"])
                self._connect(override_manual=details)
            else:
                self.manual.setVisible(True)
                self.manual_msg.setText(str(err))
                self._set_status("Manual connection cancelled. Enter Oracle host details to connect.", "error")
        else:
            audit.write("error", f"Connection failed: {err}")
            self._set_status(f"Could not connect.\n{err}", "error")
