from __future__ import annotations

from PyQt5.QtCore import Qt
from PyQt5.QtWidgets import (QDialog, QFormLayout, QHBoxLayout, QLineEdit,
                             QVBoxLayout)

from .widgets import button, card, label


class ManualOracleDialog(QDialog):
    """Modal dialog shown when an Oracle service/alias is not found in tnsnames.ora."""

    def __init__(self, parent, service_name: str, username: str, password: str,
                 initial_host: str = "", initial_port: str = "1521"):
        super().__init__(parent)
        self.setWindowTitle("Oracle Connection Details")
        self.setModal(True)
        self.setMinimumWidth(500)

        self.service_name = service_name
        self.username = username
        self.password = password

        v = QVBoxLayout(self)
        v.setContentsMargins(24, 22, 24, 18)
        v.setSpacing(14)

        v.addWidget(label("Oracle Connection Details", "h2"))
        v.addWidget(label("The Oracle database alias was not found in tnsnames.ora. "
                          "Enter the host machine details to connect directly.", "muted"))

        c_frame, c_lay = card()
        form = QFormLayout()
        form.setSpacing(10)

        # Host name: empty by default (or initial_host), editable
        self.txt_host = QLineEdit(initial_host)
        self.txt_host.setPlaceholderText("Hostname or IP address (e.g. server01 or 192.168.1.50)")
        self.txt_host.textChanged.connect(self._validate)
        form.addRow("Host Name *", self.txt_host)

        # Service name: prefilled, non-editable
        self.txt_service = QLineEdit(service_name)
        self.txt_service.setReadOnly(True)
        form.addRow("Service Name", self.txt_service)

        # Port: default 1521, editable
        self.txt_port = QLineEdit(initial_port or "1521")
        self.txt_port.setPlaceholderText("1521")
        form.addRow("Port", self.txt_port)

        # Username: prefilled, non-editable
        self.txt_user = QLineEdit(username)
        self.txt_user.setReadOnly(True)
        form.addRow("Username", self.txt_user)

        # Password: prefilled, non-editable, masked
        self.txt_pwd = QLineEdit(password)
        self.txt_pwd.setEchoMode(QLineEdit.Password)
        self.txt_pwd.setReadOnly(True)
        form.addRow("Password", self.txt_pwd)

        c_lay.addLayout(form)
        v.addWidget(c_frame)

        self.err_label = label("", "error")
        self.err_label.setVisible(False)
        v.addWidget(self.err_label)

        row = QHBoxLayout()
        row.addStretch(1)
        b_cancel = button("Cancel")
        b_cancel.clicked.connect(self.reject)
        self.b_connect = button("Connect", "primary")
        self.b_connect.clicked.connect(self.accept)
        row.addWidget(b_cancel)
        row.addWidget(self.b_connect)
        v.addLayout(row)

        self._validate()
        self.txt_host.setFocus()

    def _validate(self):
        has_host = bool(self.txt_host.text().strip())
        self.b_connect.setEnabled(has_host)

    def set_error(self, message: str):
        self.err_label.setText(message)
        self.err_label.setVisible(bool(message))

    def get_details(self) -> dict:
        return {
            "host": self.txt_host.text().strip(),
            "port": self.txt_port.text().strip() or "1521",
            "service": self.txt_service.text().strip(),
            "user": self.username,
            "password": self.password,
        }
