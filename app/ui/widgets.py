from PyQt5.QtCore import QThread, pyqtSignal as Signal
from PyQt5.QtWidgets import QFrame, QLabel, QPushButton, QVBoxLayout


def label(text: str, name: str = "", wrap: bool = True) -> QLabel:
    lb = QLabel(text)
    if name:
        lb.setObjectName(name)
    lb.setWordWrap(wrap)
    return lb


def button(text: str, kind: str = "") -> QPushButton:
    b = QPushButton(text)
    if kind:
        b.setObjectName(kind)
    return b


def card(name: str = "card") -> tuple[QFrame, QVBoxLayout]:
    f = QFrame()
    f.setObjectName(name)
    lay = QVBoxLayout(f)
    lay.setContentsMargins(18, 14, 18, 14)
    lay.setSpacing(8)
    return f, lay


class TaskThread(QThread):
    """Runs fn() off the UI thread. One task at a time (DB connections are not shared concurrently)."""
    succeeded = Signal(object)
    failed = Signal(object)

    def __init__(self, fn, parent=None):
        super().__init__(parent)
        self._fn = fn

    def run(self):
        try:
            self.succeeded.emit(self._fn())
        except BaseException as e:   # noqa: BLE001 - surfaced to the UI
            self.failed.emit(e)


class RunThread(QThread):
    log = Signal(str, str)
    progress = Signal(int, int, str)
    succeeded = Signal(object)
    failed = Signal(object)

    def __init__(self, backend, plan, parent=None):
        super().__init__(parent)
        self.backend = backend
        self.plan = plan
        self._cancel = False

    def request_cancel(self):
        self._cancel = True

    def run(self):
        from app.core.models import RunContext
        ctx = RunContext(
            on_log=lambda lvl, msg: self.log.emit(lvl, msg),
            on_progress=lambda d, t, m: self.progress.emit(d, t, m),
            is_cancelled=lambda: self._cancel,
            total=self.plan.total_steps(),
        )
        try:
            self.succeeded.emit(self.backend.execute(self.plan, ctx))
        except BaseException as e:   # noqa: BLE001
            self.failed.emit(e)
