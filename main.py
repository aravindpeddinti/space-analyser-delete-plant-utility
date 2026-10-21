import ctypes
import os
import sys

from PyQt5.QtCore import QSettings, Qt
from PyQt5.QtGui import QIcon, QPixmap
from PyQt5.QtWidgets import QApplication, QSplashScreen

from app import APP_NAME, APP_VERSION, ORGANIZATION
from app.resources import resource_path


def main() -> int:
    if os.name == "nt":   # own taskbar group + icon instead of python's
        try:
            ctypes.windll.shell32.SetCurrentProcessExplicitAppUserModelID("Hexagon.DeletePlantUtility.5")
        except Exception:
            pass
    meipass = getattr(sys, "_MEIPASS", None)
    if meipass:
        qt_plugins = os.path.join(meipass, "PyQt5", "Qt5", "plugins")
        if os.path.isdir(qt_plugins):
            os.environ["QT_PLUGIN_PATH"] = qt_plugins
            QApplication.addLibraryPath(qt_plugins)

    app = QApplication(sys.argv)
    app.setApplicationName(APP_NAME)
    app.setOrganizationName(ORGANIZATION)
    icon = QIcon(resource_path(os.path.join("assets", "app.ico")))
    icon.addFile(resource_path(os.path.join("assets", "app.png")))
    app.setWindowIcon(icon)

    # Show something straight away; the rest of the imports and window setup follow.
    pm = QPixmap(resource_path(os.path.join("assets", "app.png")))
    splash = QSplashScreen(pm.scaled(180, 180, Qt.KeepAspectRatio, Qt.SmoothTransformation))
    splash.show()
    app.processEvents()

    from app.core import audit
    from app.ui import theme
    from app.ui.main_window import MainWindow

    mode = QSettings(ORGANIZATION, "DeletePlantUtility").value("theme", "system")
    theme.apply_theme(app, mode)
    audit.start_session(APP_VERSION)
    win = MainWindow()
    win.setWindowIcon(icon)
    theme.watch_system(app, lambda: (theme.mode() == "system") and (
        theme.apply_theme(app, "system"), win.on_theme_changed()))
    splash.close()
    if not win.confirm_notice():
        audit.write("info", "Operator declined the internal-use notice; exiting")
        return 0
    win.show()
    return app.exec_()


if __name__ == "__main__":
    sys.exit(main())
