#nullable disable
using FM26.VisualDiagnostics;

// H203 (0.10.2): FM key-moment presentation settles/repositions players on the first frame(s) after a view-change resume,
// with the clock running (live-012: suspension 1 resumed at frame 7783, clock 277.2531; frame 7784 Normal, dt 0.039,
// 5.09 m on actor -140804) or frozen (live-011 frame 24071, H200). Pure CLR, executable.
internal static class H203ResumeSettleTests
{
    private const double LiveDisplacement = 5.09, ResumeClock = 277.2531, NextClock = 277.2921;
    private static readonly long F = System.Diagnostics.Stopwatch.Frequency;
    private static readonly List<(string Name, Action Test)> Cases = new() {
        ("settle-characterization", SettleCharacterization),
        ("settling-running-jump-is-discontinuity", SettlingRunningJump),
        ("window-opens-on-resume", WindowOpensOnResume),
        ("record-resume-reposition", RecordResumeReposition),
        // H203-CASES
    };
    private static void Check(bool okay, string label) { if (!okay) throw new Exception("FAIL: H203 " + label); }

    public static void Run(string name = null)
    {
        foreach (var (caseName, test) in Cases)
        {
            if (name != null && name != caseName) continue;
            test();
            Console.WriteLine($"H203 PASS: {caseName}");
        }
        if (name != null && !Cases.Any(c => c.Name == name)) throw new Exception($"H203 unknown case {name}");
    }
    private static void SettleCharacterization()
    {
        // Characterization (passes on the skeleton): every conjunct of the settle rule other than the window itself.
        Check(!FifaTimeDiscontinuities.IsDiscontinuity(FifaClockStep.Normal, LiveDisplacement, false), "a 5.09 m jump with a RUNNING clock OUTSIDE the settle window stays a teleport");
        Check(!FifaTimeDiscontinuities.IsDiscontinuity(FifaClockStep.Normal, 2.0, true), "a small running move inside the window is ordinary motion");
        Check(!FifaTimeDiscontinuities.IsDiscontinuity(FifaClockStep.Normal, FifaOverlayPolicy.MaximumFrameDisplacementMeters, true), "exactly the 3 m bound inside the window is ordinary motion");
        Check(!FifaTimeDiscontinuities.IsDiscontinuity(FifaClockStep.Normal, double.NaN, true), "nonfinite displacement inside the window is never a discontinuity");
        Check(!FifaTimeDiscontinuities.IsDiscontinuity(FifaClockStep.NonFinite, LiveDisplacement, true), "nonfinite clock inside the window is never a discontinuity");
        Check(FifaTimeDiscontinuities.IsDiscontinuity(FifaClockStep.Paused, 20.32, false) && !FifaTimeDiscontinuities.IsDiscontinuity(FifaClockStep.Paused, 2.0, false), "H200 paused-reposition unchanged outside the window");
        Check(FifaTimeDiscontinuities.IsDiscontinuity(FifaClockStep.Backward, 0, false) && FifaTimeDiscontinuities.IsDiscontinuity(FifaClockStep.Forward, 0, false), "H164 backward/forward unchanged");
        Check(FifaResumeSettle.SettleSeconds == 1.0, "declared settle window = 1.0 s real");
        var never = new FifaResumeSettle();
        Check(!never.InWindow(0, F) && !never.InWindow(F / 50, F), "never resumed => never in the settle window");
        var s = new FifaResumeSettle(); s.MarkResume(1000);
        Check(!s.InWindow(1000 + (long)(1.2 * F), F) && !s.InWindow(1000 + 60 * F, F), "1.2 s / 60 s after the resume => outside the settle window");
        var d = new FifaTimeDiscontinuities(); string error = null;
        try { d.TryRecord(7784, 1, ResumeClock, NextClock, LiveDisplacement, false); } catch (InvalidOperationException e) { error = e.Message; }
        Check(error != null && d.Count == 0, "a running-clock jump outside the window is never recorded");
    }
    private static void SettlingRunningJump()
    {
        var step = FifaTimeDiscontinuities.Classify(ResumeClock, NextClock);
        Check(step == FifaClockStep.Normal, "setup: live-012 clock 277.2531 -> 277.2921 is an ordinary running step");
        Check(FifaTimeDiscontinuities.IsDiscontinuity(step, LiveDisplacement, true), "a running-clock 5.09 m jump inside the post-resume settle window (live-012 frame 7784) is a discontinuity");
    }
    private static void WindowOpensOnResume()
    {
        var s = new FifaResumeSettle(); s.MarkResume(1000);
        Check(s.InWindow(1000 + F / 50, F), "the first frame after a resume (+20 ms real; both live cases at +1 frame) is inside the settle window");
        Check(s.InWindow(1000 + F / 2, F), "+0.5 s real after the resume is inside the settle window");
    }
    private static void RecordResumeReposition()
    {
        var d = new FifaTimeDiscontinuities(); bool recorded = false; string error = null;
        try { recorded = d.TryRecord(7784, 777, ResumeClock, NextClock, LiveDisplacement, true); } catch (InvalidOperationException e) { error = e.Message; }
        Check(error == null && recorded && d.Count == 1, "a running-clock reposition inside the settle window is recorded (" + error + ")");
        var r = d.Records[0];
        Check(r.Direction == "resume-reposition" && r.StepSeconds == NextClock - ResumeClock && r.MatchTimeBefore == ResumeClock && r.MatchTimeAfter == NextClock && r.DisplacementMeters == LiveDisplacement && r.Frame == 7784 && r.Index == 1, "record fields: resume-reposition, actual dt step, live values");
        var full = new FifaTimeDiscontinuities();
        for (int i = 0; i < FifaTimeDiscontinuities.MaximumDiscontinuities; i++) full.TryRecord(i, i, 100, 100, 5);
        Check(!full.TryRecord(501, 501, ResumeClock, NextClock, LiveDisplacement, true) && full.Count == FifaTimeDiscontinuities.MaximumDiscontinuities, "resume-reposition records share the 500 bound");
    }
    // H203-METHODS
}
