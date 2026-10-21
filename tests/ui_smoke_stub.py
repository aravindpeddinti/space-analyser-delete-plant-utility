"""UI smoke test with a stubbed PyQt5 (no display needed). Run: python tests/ui_smoke_stub.py
Catches import/name/wiring errors only."""
import sys, types, os
sys.path.insert(0, os.path.dirname(os.path.dirname(os.path.abspath(__file__))))

class Meta(type):
    def __getattr__(cls, n):
        if n.startswith("__"): raise AttributeError(n)
        return Stub()
class Stub(metaclass=Meta):
    def __init__(self, *a, **k): self._items = {}
    def __getattr__(self, n):
        if n.startswith("__"): raise AttributeError(n)
        return Stub()
    def __call__(self, *a, **k): return Stub()
    def __or__(self, o): return self
    __ror__ = __and__ = __or__
    def __iter__(self): return iter(())
    def __bool__(self): return True
    def __int__(self): return 0
    def __index__(self): return 0
    def __eq__(self, o): return self is o
    def __hash__(self): return id(self)

def mkmod(name):
    m = types.ModuleType(name)
    cache = {}
    def ga(n):
        if n.startswith("__"): raise AttributeError(n)
        if n not in cache: cache[n] = type(n, (Stub,), {})
        return cache[n]
    m.__getattr__ = ga
    sys.modules[name] = m
    return m
for n in ["PyQt5", "PyQt5.QtCore", "PyQt5.QtGui", "PyQt5.QtWidgets"]:
    mkmod(n)
sys.modules["PyQt5.QtCore"].pyqtSignal = lambda *a, **k: Stub()

import importlib
mods = ["app.ui.theme","app.ui.widgets","app.ui.connect_page","app.ui.plants_page",
        "app.ui.review_page","app.ui.run_page","app.ui.main_window"]
for m in mods: importlib.import_module(m)
print("imports ok")

from app.ui.main_window import MainWindow
w = MainWindow()
print("MainWindow constructed")

from app.core.models import *
plant = Plant("1","PlantA","/tmp/x",[Project("2","P1","/tmp/y")])
pp = PlantPlan(plant, [Target("folder","/tmp/x","Plant folder"),
                       Target("schema","S1","Schema S1","SRV"),
                       Target("schema","S2","Schema S2","SRV",blocked="shared",enabled=False),
                       Target("site_records","PlantA","Site")], ["warn1"], ["note1"])
plan = DeletionPlan([pp],[Target("tablespace","TS1","Tablespace TS1","SRV",enabled=False)])
w.state.plan = plan
w.state.plants = [plant]
w.plants_page._fill(); w.plants_page.on_enter()
w.review_page.on_enter()
print("review on_enter ok; phrase:", w.review_page.phrase)
w.review_page._refresh()
print("smoke ok")
