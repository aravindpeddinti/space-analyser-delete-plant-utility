import unittest
from unittest.mock import patch

from app.core.headless import UtilitySession, confirmation_phrase, plan_to_dict
from app.core.models import DeletionPlan, Plant, PlantPlan, Target


class FakeBackend:
    def __init__(self):
        self.calls = 0

    def execute(self, plan, ctx):
        self.calls += 1
        return []

    def list_plants(self):
        return [Plant("1", "Plant A", "C:/a"), Plant("2", "Plant B", "C:/b")]

    def build_plan(self, plants, delete_site=False):
        return DeletionPlan(plants=[PlantPlan(p) for p in plants], delete_site=delete_site)


def session_for(plan):
    session = UtilitySession.__new__(UtilitySession)
    session._closed = False
    session.backend = FakeBackend()
    session.logger = lambda *_: None
    session._plans = {id(plan): plan}
    session._consumed_plans = set()
    return session


class HeadlessApiTests(unittest.TestCase):
    def test_confirmation_phrase_matches_ui_for_plant_and_site(self):
        plan = DeletionPlan(plants=[PlantPlan(Plant("1", "Plant A", "C:/plant"))])
        self.assertEqual(confirmation_phrase(plan), "Plant A")
        site = DeletionPlan(delete_site=True, site_name="SEM")
        self.assertEqual(confirmation_phrase(site), "DELETE SITE SEM")

    def test_plan_serialization_includes_safety_state(self):
        target = Target("schema", "PLANT_A", enabled=False, blocked="Shared")
        plan = DeletionPlan(plants=[PlantPlan(Plant("1", "Plant A", "C:/plant"), [target])])
        serialized = plan_to_dict(plan)
        out = serialized["plants"][0]["targets"][0]
        self.assertFalse(out["active"])
        self.assertEqual(out["blocked"], "Shared")

    def test_whole_site_plan_selects_all_plants_and_rejects_subset(self):
        session = session_for(DeletionPlan())
        plan = session.build_plan(delete_site=True)
        self.assertEqual([p.plant.name for p in plan.plants], ["Plant A", "Plant B"])
        with self.assertRaises(ValueError):
            session.build_plan(["Plant A"], delete_site=True)

    @patch("app.core.headless.find_running_apps", return_value=[])
    def test_execution_needs_exact_phrase_and_acknowledgement(self, _process_check):
        plan = DeletionPlan(plants=[PlantPlan(Plant("1", "Plant A", "C:/plant"),
                                             [Target("folder", "C:/plant")])])
        session = session_for(plan)
        with self.assertRaises(ValueError):
            session.execute(plan, confirmation="wrong", acknowledge=True)
        with self.assertRaises(ValueError):
            session.execute(plan, confirmation="Plant A", acknowledge=False)
        self.assertEqual(session.backend.calls, 0)

    @patch("app.core.headless.find_running_apps", return_value=[])
    def test_plan_is_consumed_after_execution(self, _process_check):
        plan = DeletionPlan(plants=[PlantPlan(Plant("1", "Plant A", "C:/plant"),
                                             [Target("folder", "C:/plant")])])
        session = session_for(plan)
        session.execute(plan, confirmation="Plant A", acknowledge=True)
        with self.assertRaises(ValueError):
            session.execute(plan, confirmation="Plant A", acknowledge=True)
        self.assertEqual(session.backend.calls, 1)

    @patch("app.core.headless.find_running_apps", side_effect=RuntimeError("probe failed"))
    def test_process_probe_error_fails_closed(self, _process_check):
        plan = DeletionPlan(plants=[PlantPlan(Plant("1", "Plant A", "C:/plant"),
                                             [Target("folder", "C:/plant")])])
        session = session_for(plan)
        with self.assertRaises(RuntimeError):
            session.execute(plan, confirmation="Plant A", acknowledge=True)
        self.assertEqual(session.backend.calls, 0)


if __name__ == "__main__":
    unittest.main()
