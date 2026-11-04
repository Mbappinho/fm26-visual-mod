"""H173 source-bound Unity host gates. These are wiring checks, not runtime or visual proof."""
import unittest
from pathlib import Path
from tests.test_fifa_locomotion_integration import body, code_lines
ROOT = Path(__file__).resolve().parents[1]
class StabilityWiringTests(unittest.TestCase):
    def setUp(self):
        self.overlay = (ROOT/'src/FifaOverlay.cs').read_text(encoding='utf-8')
        self.code = code_lines(self.overlay)
    def actor(self, signature):
        return code_lines(body(self.overlay, signature, 'internal sealed class FifaOverlayActor'))
    def test_hold_until_speed_window_refills_and_single_snap(self):
        pose=self.actor('public void WritePose(')
        self.assertIn('if (!discontinuity && !_motion.Paused && _motion.WindowSeconds > 0) _loco.Update(_motion.MatchDt, _motion.Speed, _motion.WindowDeltaX, _motion.WindowDeltaZ, _forward.x, _forward.z);',pose)
        self.assertNotIn('_loco.Snap()',pose,'Rebaseline already snaps; never count a discontinuity twice')
        self.assertIn('_motion.Rebaseline(matchTime, FifaOverlay.ToN(_position)); _loco?.Snap();',self.code)
    def test_dense_trace_first_two_actors_and_reset_events(self):
        self.assertIn('_actors.Count < FifaMotionTrace.MaximumActors', self.code)
        begin=self.actor('public void Begin(')
        self.assertIn('if (_traceEligible && _loco != null) _motionTrace = new FifaMotionTrace();', begin)
        pose=self.actor('public void WritePose(')
        self.assertIn('_motionTrace?.Add(frame, matchTime, _motion.MatchDt, _motion.Speed, _motion.WindowDeltaX, _motion.WindowDeltaZ, _forward.x, _forward.z, _traceResets, _motion.WindowSeconds > 0, _motion.Paused, discontinuity, _loco);',pose)
        self.assertLess(pose.index('_motionTrace?.Add('),pose.index('_mesh.vertices = _vertices;'))
        self.assertIn('_traceResets = 0;',pose)
        self.assertIn('_loco?.Snap(); _traceResets++;', self.code)
        self.assertIn('_traceResets++;',self.actor('public void Resume('))
        self.assertIn('MotionTrace = _motionTrace?.Snapshot(),',self.code)
        self.assertIn('MotionTrace = single?.MotionTraceReport,',self.code)
    def test_report_text_bounds_do_not_change_primary_stop_cause(self):
        self.assertIn('_cleanupErrors.Add(FifaReportBudget.CleanupText(e.Message))',self.code)
        self.assertIn('_endErrors.Add(FifaReportBudget.CleanupText(e.Message))',self.code)
        self.assertEqual(self.code.count('Name = FifaReportBudget.NameText(_actor.Name)'),2)
        self.assertIn('_lifecycle.RequestStop(reason, OriginalPassPolicy.CanonicalError(error), _stoppedFrame, _stopped);',self.code)
        self.assertIn('_endError = OriginalPassPolicy.CanonicalError(error);',self.code)
    def test_clip_update_gate_and_blend_operand_order(self):
        pose=self.actor('public void WritePose(')
        self.assertIn('_poseUpdates++; if (_loco == null) _clipUpdates[clip]++;',pose)
        self.assertIn('_loco = _s.Pack.FormatVersion == 2 ? new FifaLocomotion(_s.Pack) : null;', self.actor('public void Begin('))
        self.assertIn('FifaVertexCopy.BlendFrames<Vector3>(_s.Frames(_loco.FromClip), _loco.FromFrame, _s.Frames(_loco.TargetClip), _loco.TargetFrame, _loco.Weight, _s.Pack.VertexCount, _vertices.AsSpan());',self.actor('private void FillLocomotion('))
if __name__ == '__main__': unittest.main()
