"""H178a (0.8.0): source-wiring GUARDS for the goalkeeper decisions in the Unity-bound host. These are extra guards only:
every behavior is decided by a pure FifaOverlayMulti.cs function with its own behavioral RED/GREEN cycle in
tests/PolicyTests/H178Tests.cs. Each check asserts an exact, uncommented source line (a commented-out copy cannot satisfy it)."""
import unittest
from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]


def code_lines(path):
    lines = []
    for raw in (ROOT / path).read_bytes().decode("utf-8").splitlines():
        s = raw.strip()
        if s.startswith("//"):
            continue
        lines.append(s.split(" //", 1)[0].rstrip())
    return lines


class H178KeeperWiringTests(unittest.TestCase):
    def setUp(self):
        self.overlay = code_lines("src/FifaOverlay.cs") + code_lines("src/FifaOverlayActorContext.cs")  # H209: actor guard chain extracted
        self.host = code_lines("src/FifaOverlayHost.cs")
        self.binding = code_lines("src/PlayerBindingEligibility.cs")

    def has(self, lines, fragment):
        hits = [line for line in lines if fragment in line]
        self.assertEqual(len(hits), 1, f"exactly one uncommented line containing: {fragment}")

    def test_selection_uses_fifa_eligibility_and_role(self):
        self.has(self.host, "FifaActorSelection.ResolveFifa(request, players.Select(p => (p.ActorInstanceId, p.TeamId, p.Eligible, p.FifaEligible, p.Goalkeeper)).ToArray());")
        self.has(self.host, "if (matches.Length != 1 || !matches[0].Eligible) throw")
        self.has(self.binding, "public bool FifaEligible => FifaActorSelection.FifaEligible(ShirtNumber, Active, Substitute, Goalkeeper, Medic, PlayerId,")

    def test_start_and_context_use_admissibility(self):
        self.has(self.overlay, "if (actor == null || actor.Player == null || !FifaActorSelection.Admissible(request.Multi, actor.Eligible, actor.FifaEligible) || _actors.Any(a => a.ActorInstanceId == actor.ActorInstanceId))")
        self.has(self.overlay, "if (!FifaActorSelection.Admissible(_s.Request.Multi, _actor.Eligible, _actor.FifaEligible)) return \"admissible\";")  # H209: named predicate

    def test_role_revalidated_every_frame_and_classified(self):
        self.has(self.overlay, "if (FifaActorSelection.RoleRefused(_actor.Goalkeeper, p.IsGoalkeeper)) return \"role\";")  # H209: named predicate
        self.has(self.overlay, "|| FifaActorScope.RoleIdentityLost(_actor.Goalkeeper, p.IsGoalkeeper);")
        self.assertNotIn("|| p.IsGoalkeeper ||", "\n".join(self.overlay), "no unconditional keeper refusal left")

    def test_materials_keyed_by_team_and_role(self):
        self.has(self.overlay, "_teams = _actors.Select(a => FifaTeamPalettes.Key(request.Multi, a.TeamId, a.Goalkeeper)).Distinct().OrderBy(t => t).ToArray();")
        self.has(self.overlay, "var palette = FifaTeamPalettes.ForKey(_request.Multi, team);")
        self.has(self.overlay, "renderer.sharedMaterials = _s.Materials(FifaTeamPalettes.Key(_s.Request.Multi, _actor.TeamId, _actor.Goalkeeper));")
        self.has(self.overlay, "public bool Goalkeeper => _actor.Goalkeeper;")
        start = "\n".join(self.overlay)
        self.assertLess(start.index("FifaHandleBudget.Require(_actors.Count, _pack.Pieces.Count, _teams.Length);"), start.index("CreateOwned();"),
                        "budget (with team+role keys) checked before any allocation")

    def test_report_role_and_frozen_plugin(self):
        self.has(self.overlay, "Role = FifaActorRole.Of(_actor.Goalkeeper),")
        self.assertNotIn("Role", "".join(l for l in self.overlay if "public object Identity()" in l), "legacy SelectedActor identity unchanged")
        writes = sum(line.count(".forceRenderingOff =") for line in self.overlay)
        self.assertEqual(writes, 2, "exactly two forceRenderingOff write sites")


if __name__ == "__main__":
    unittest.main()
