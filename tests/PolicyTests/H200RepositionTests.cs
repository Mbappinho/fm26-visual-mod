#nullable disable
using System.Numerics;
using FM26.VisualDiagnostics;

// H200 (0.10.1): FM key-moment (highlights) presentation repositions players to the next highlight's start positions
// while the match clock is frozen. Live-011 segment 2, frame 24071: clock 1879.25 -> 1879.25 (Paused, dt 0),
// root (-0.125,0,27.20) -> (-0.78,0,47.51) = 20.32 m. Pure CLR, executable.
internal static class H200RepositionTests
{
    private const double LiveDisplacement = 20.32;
    private static readonly List<(string Name, Action Test)> Cases = new() {
        ("paused-over-bound-is-discontinuity", PausedOverBound),
        ("running-and-small-not-discontinuity", NotDiscontinuity),
        ("record-paused-reposition", RecordPausedReposition),
        // H200-CASES
    };
    private static int _assertions;
    private static void Check(bool okay, string label) { _assertions++; if (!okay) throw new Exception("FAIL: H200 " + label); }

    public static void Run(string name = null)
    {
        foreach (var (caseName, test) in Cases)
        {
            if (name != null && name != caseName) continue;
            test();
            Console.WriteLine($"H200 PASS: {caseName}");
        }
        if (name != null && !Cases.Any(c => c.Name == name)) throw new Exception($"H200 unknown case {name}");
    }
    private static void PausedOverBound()
    {
        var step = FifaTimeDiscontinuities.Classify(1879.25, 1879.25);
        Check(step == FifaClockStep.Paused, "setup: live clock 1879.25 -> 1879.25 is a pause");
        Check(FifaTimeDiscontinuities.IsDiscontinuity(step, LiveDisplacement), "a frozen clock with a 20.32 m root jump (key-moment repositioning) is a discontinuity");
    }
    private static void NotDiscontinuity()
    {
        Check(!FifaTimeDiscontinuities.IsDiscontinuity(FifaClockStep.Normal, LiveDisplacement), "a 20.32 m jump with a RUNNING clock stays a teleport (not a discontinuity)");
        Check(!FifaTimeDiscontinuities.IsDiscontinuity(FifaClockStep.Paused, 2.0), "paused small move is held, not a discontinuity");
        Check(!FifaTimeDiscontinuities.IsDiscontinuity(FifaClockStep.Paused, FifaOverlayPolicy.MaximumFrameDisplacementMeters), "exactly the 3 m bound is still an accepted paused move");
        Check(FifaTimeDiscontinuities.IsDiscontinuity(FifaClockStep.Paused, 3.001), "just over the 3 m bound with a frozen clock is a discontinuity");
        Check(!FifaTimeDiscontinuities.IsDiscontinuity(FifaClockStep.Paused, double.NaN), "nonfinite displacement is never a discontinuity");
        Check(!FifaTimeDiscontinuities.IsDiscontinuity(FifaClockStep.NonFinite, LiveDisplacement), "nonfinite clock is never a discontinuity");
        Check(FifaTimeDiscontinuities.IsDiscontinuity(FifaClockStep.Backward, 0) && FifaTimeDiscontinuities.IsDiscontinuity(FifaClockStep.Forward, 0), "H164 backward/forward unchanged");
    }
    private static void RecordPausedReposition()
    {
        var d = new FifaTimeDiscontinuities();
        bool recorded = false; string error = null;
        try { recorded = d.TryRecord(24071, 777, 1879.25, 1879.25, LiveDisplacement); } catch (InvalidOperationException e) { error = e.Message; }
        Check(error == null && recorded && d.Count == 1, "a paused-clock reposition over the bound is recorded (" + error + ")");
        var r = d.Records[0];
        Check(r.Direction == "paused-reposition" && r.StepSeconds == 0 && r.MatchTimeBefore == 1879.25 && r.MatchTimeAfter == 1879.25 && r.DisplacementMeters == LiveDisplacement && r.Frame == 24071 && r.Index == 1, "record fields: paused-reposition, 0 s step, live values");
        try { d.TryRecord(1, 1, 100, 100, 2.0); error = null; } catch (InvalidOperationException e) { error = e.Message; }
        Check(error != null && d.Count == 1, "a paused small move is still refused (never recorded)");
        var full = new FifaTimeDiscontinuities();
        for (int i = 0; i < FifaTimeDiscontinuities.MaximumDiscontinuities; i++) full.TryRecord(i, i, 100, 100, 5);
        Check(!full.TryRecord(501, 501, 100, 100, 5) && full.Count == FifaTimeDiscontinuities.MaximumDiscontinuities, "paused-reposition records share the 500 bound");
    }
    // H200-METHODS
}
