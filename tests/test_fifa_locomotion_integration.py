"""H171 (0.7.1): source wiring of pack v2 locomotion (selection + phase-synced crossfades) in the FIFA overlay host, plus the H169
follow-ups that live in the Unity-bound host. Source-bound checks only; runtime behaviour is NOT certified here (pure parts are
executed in tests/PolicyTests/FifaLocomotionTests.cs and FifaOverlayMultiTests.cs)."""
import re
import unittest
from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]


def body(text, signature, start_marker=None):
    start = text.index(start_marker) if start_marker else 0
    rest = text[text.index(signature, start):]
    return rest[:rest.index("\n    }\n")]


def code_lines(text):
    """Uncommented code only (a commented-out statement must not satisfy a wiring check)."""
    out = []
    for line in text.splitlines():
        stripped = line.strip()
        if stripped.startswith("//"):
            continue
        out.append(line.split(" // ")[0])
    return "\n".join(out)


class LocomotionWiringTests(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        cls.overlay = (ROOT / "src/FifaOverlay.cs").read_text(encoding="utf-8")
        cls.code = code_lines(cls.overlay)
        cls.loco = (ROOT / "src/FifaLocomotion.cs").read_text(encoding="utf-8")

    def am(self, signature):
        return code_lines(body(self.overlay, signature, "internal sealed class FifaOverlayActor"))

    def hm(self, signature):
        return code_lines(body(self.overlay, signature))

    def test_default_pack_prefers_v2_and_explicit_directory_unchanged(self):
        start = self.hm("public void Start(")
        self.assertIn('LoadPack(request.PackDirectory ?? FifaOverlayPolicy.ResolveDefaultPackDirectory(Environment.GetEnvironmentVariable("LOCALAPPDATA"), File.Exists));', start)
        self.assertNotIn("FifaOverlayPolicy.DefaultPackDirectory(", self.code, "legacy v1-only default no longer used by the host")
        # pack check (sizes, hashes, structure incl. v2 clip rules in ValidateHeader) still before any owned allocation
        self.assertLess(start.index("LoadPack("), start.index("FifaHandleBudget.Require("))
        self.assertLess(start.index("LoadPack("), start.index("CreateOwned();"))
        load = self.hm("private void LoadPack(")
        self.assertIn("FifaOverlayPolicy.RequirePayload(data, clip.Sha256, clip.Bytes);", load, "every clip of v1 or v2 is size+sha checked")
        self.assertIn("foreach (var clip in _pack.Clips.Values)", load)
        self.assertIn("PayloadsVerified = _pack != null && _frames.Count == _pack.Clips.Count && _triangles != null", self.code)

    def test_locomotion_only_for_pack_v2_created_before_allocation(self):
        begin = self.am("public void Begin(")
        self.assertIn("_loco = _s.Pack.FormatVersion == 2 ? new FifaLocomotion(_s.Pack) : null;", begin)
        start = self.hm("public void Start(")
        self.assertLess(start.index("a.Begin("), start.index("CreateOwned();"))
        self.assertIn('internal string StandClip => _pack.FormatVersion == 2 ? "idle" : FifaOverlayPolicy.StandClip;', self.code)
        fill = self.am("private void FillVertices(")
        self.assertIn('_s.Frames(clip == "stand" ? _s.StandClip : clip)', fill, "v2 standing pose = idle key 0 (pack frame 1); v1 unchanged")

    def test_write_pose_v1_path_kept_and_v2_path_selected_in_match_time(self):
        pose = self.am("public void WritePose(")
        # v1 (0.6.x) selection kept literally for v1 packs
        self.assertIn("clip = FifaOverlayPolicy.SelectClip(_motion.Speed, _clip);", pose)
        self.assertIn("if (_loco == null)", pose)
        # v2: update only on a processed (not paused, not discontinuity) frame, inputs all in MATCH time / FM transforms
        loco = pose[pose.index("else"):]
        self.assertNotIn("_loco.Snap();", loco, "Rebaseline already reset the selector on this discontinuity")
        self.assertIn("if (!discontinuity && !_motion.Paused && _motion.WindowSeconds > 0) _loco.Update(_motion.MatchDt, _motion.Speed, _motion.WindowDeltaX, _motion.WindowDeltaZ, _forward.x, _forward.z);", loco)
        # survivor blend-not-uploaded: every blended frame (and the first frame after a fade) is uploaded even on the same key
        self.assertIn("changed = clip != _shownClip || index != _shownFrame || _loco.Blending || _shownBlend;", loco)
        self.assertIn("_shownBlend = _loco.Blending;", loco)
        self.assertLess(loco.index("if (changed) FillLocomotion();"), loco.index("_shownBlend = _loco.Blending;"))
        self.assertIn("FifaVertexCopy.BlendFrames<Vector3>(_s.Frames(_loco.FromClip), _loco.FromFrame, _s.Frames(_loco.TargetClip), _loco.TargetFrame, _loco.Weight, _s.Pack.VertexCount, _vertices.AsSpan());", self.code)

    def test_single_upload_site_with_deadline_before_each_write(self):
        pose = self.am("public void WritePose(")
        self.assertEqual(self.code.count("_mesh.vertices = _vertices;"), 2, "creation + ONE per-frame upload site shared by v1/v2")
        self.assertLess(pose.index("RequireWindow(); _mesh.vertices = _vertices;"), pose.index("RequireWindow(); _mesh.RecalculateNormals();"))
        self.assertLess(pose.index("RequireWindow(); _ownedRoot.transform.SetPositionAndRotation(_position, rotation);"), pose.index("HideNative();"))
        self.assertEqual(self.code.count("SetActive(true)"), 1, "one show site")
        writes = [line.strip() for line in self.code.splitlines() if ".forceRenderingOff =" in line]
        self.assertEqual(writes, ["o.Renderer.forceRenderingOff = true;", "renderer.forceRenderingOff = entry.BeforeForceRenderingOff;"], "exactly 2 forceRenderingOff write sites")
        self.assertNotIn("new float[", pose, "no per-frame float array")
        self.assertNotIn("new Vector3[", self.code)

    def test_rebaseline_and_resume_snap_the_blend(self):
        rebase = self.am("public void Rebaseline(")
        self.assertIn("_motion.Rebaseline(matchTime, FifaOverlay.ToN(_position)); _loco?.Snap();", rebase)
        resume = self.am("public void Resume(")
        self.assertLess(resume.index("_motion.Rebaseline(matchTime, FifaOverlay.ToN(position));"), resume.index("_loco?.Snap();"))
        self.assertLess(resume.index("if (session != null) throw session;"), resume.index("_loco?.Snap();"), "session refusal still first (H168 L1)")

    def test_frame_cost_counts_blending_actors_and_reports_locomotion(self):
        tick = self.hm("public void Tick()")
        self.assertIn("if (_frameCost.Add((double)(Stopwatch.GetTimestamp() - work) * 1000.0 / Stopwatch.Frequency, blending)) { Stop(FifaFrameCost.Reason); return; }", tick)
        self.assertIn("foreach (var a in active) { current = a; a.WritePose(frame, now, dt, matchTime, discontinuity); if (a.Blending) blending++; }", tick)
        self.assertIn("int blending = 0;", tick)
        self.assertIn("Locomotion = _loco?.Stats(),", self.code, "per-actor bounded locomotion stats in the multi report")
        self.assertIn("Locomotion = single?.LocomotionStats,", self.code, "legacy N=1 report top-level stats")
        self.assertIn("FormatVersion = _pack?.FormatVersion,", self.code)
        self.assertEqual(self.overlay.count('Version = "0.10.7"'), 2)  # H178a; H186: 0.9.0; H186b: 0.10.0; H200: 0.10.1; H203: 0.10.2; H206: 0.10.3; H209: 0.10.4; H211: 0.10.5; H234: 0.10.6; H238: 0.10.7

    def test_h169_followups_in_the_host(self):
        flags = self.am("public FifaRendererFlagReport[] Flags()")
        self.assertEqual(flags.count('if (o.Renderer == null) status = "dead";'), 1, "H169-5: a destroyed renderer is reported dead, never unchanged")
        tick = self.hm("public void Tick()")
        self.assertIn("scope = FifaActorScope.OnSuspendLimitCount(_request.Multi, session?.Message);", tick, "H169 suggestion 2")
        present = self.am("private bool? ActorPresent()")
        self.assertIn("catch { return null; }", present, "H169 suggestion 4: unreadable presence is not absence")
        self.assertNotIn("catch { return false; }", present)
        lost = self.am("public bool HardIdentityLost()")
        self.assertIn("catch { return false; }", lost, "HardIdentityLost unchanged (unreadable is not a departure)")


if __name__ == "__main__":
    unittest.main()
