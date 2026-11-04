"""H186 host-glue characterization (text guards). The decisions are executable in PolicyTests
(H186AutoHostTests / H185NativeObservationTests); these checks only pin the IL2CPP glue wiring."""
import unittest
from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]


class H186AutoWiring(unittest.TestCase):
    def setUp(self):
        self.host = (ROOT / "src/FifaOverlayHost.cs").read_text(encoding="utf-8")

    def test_auto_step_runs_before_the_standard_command_path(self):
        process = self.host[self.host.index("public static void Process("):self.host.index("private static FifaAutoHost _auto;")]
        self.assertLess(process.index("AutoStep(path,"), process.index("if (File.Exists(path)) Command("))

    def test_absent_config_returns_before_any_native_capture(self):
        body = self.host[self.host.index("private static void AutoStep("):self.host.index("private static byte[] ReadBounded(")]
        self.assertLess(body.index("if (!File.Exists(configPath)) return;"), body.index("FifaAutoNativeObservation.Capture("))
        self.assertIn('"fifa-overlay-auto.json"', body)

    def test_command_is_moved_without_overwrite(self):
        body = self.host[self.host.index("private static void AutoStep("):self.host.index("private static byte[] ReadBounded(")]
        self.assertIn("File.Move(temporary, commandPath);", body)
        self.assertNotIn("File.Move(temporary, commandPath, true)", body)
        self.assertNotIn("File.WriteAllBytes(commandPath", body)

    def test_one_auto_host_per_plugin_session(self):
        self.assertEqual(self.host.count("new FifaAutoHost("), 1)
        self.assertIn("_auto ??= new FifaAutoHost(DiagnosticsPlugin.Session);", self.host)

    def test_file_facts_wired_to_step_in_order(self):
        self.assertIn("File.Exists(packHeader), File.Exists(commandPath),", self.host)
        # H191: report existence is evaluated for the RequestId of the segment about to be submitted.
        self.assertIn("requestId => File.Exists(Path.Combine(DiagnosticsPlugin.Output, FifaOverlayReport.FileName(requestId))));", self.host)

    def test_match_loaded_gate_wired_from_root_and_players(self):
        self.assertIn("bool matchLoaded = root != null && players != null && players.Count > 0;", self.host)
        self.assertIn("_auto.Step(ReadBounded(configPath), matchLoaded,", self.host)

    def test_segment_and_roster_wiring(self):
        self.assertIn("_auto.Step(ReadBounded(configPath), matchLoaded, canStartSegment, _overlay?.RequestId,", self.host)
        self.assertIn("if (_rosterWatch.Observe(_overlaySelected,", self.host)
        self.assertIn("PlayerBinding.Collect(root).Select(", self.host)
        self.assertIn('_overlay.Stop("roster-changed");', self.host)
        process = self.host[self.host.index("public static void Process("):self.host.index("private static FifaAutoHost _auto;")]
        self.assertLess(process.index("RosterCheck(root, players, candidateOrPrototypeSession);"), process.index("AutoStep(path,"))


if __name__ == "__main__":
    unittest.main()
