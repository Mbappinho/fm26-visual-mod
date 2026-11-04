#nullable disable
using System.Numerics;
using System.Text.Json;
using FM26.VisualDiagnostics;

// H164 (0.5.1): FM match-clock discontinuities WITHOUT a view cut (goal replay = clock backwards with players visible;
// dead-time skip = clock forward > 4 s) are re-baselined and recorded instead of failing. Pure CLR, executable.
internal static class FifaOverlayDiscontinuityTests
{
    private static int _assertions;
    private static void Check(bool okay, string label) { _assertions++; if (!okay) throw new Exception("FAIL: " + label); }
    private static void Reject(Action action, string label)
    {
        _assertions++;
        try { action(); } catch (InvalidOperationException) { return; }
        throw new Exception("FAIL: accepted " + label);
    }

    public static void Run()
    {
        Classify(); Bookkeeping(); Motion(); LiveFixture004(); Observation(); StopRow();
        Console.WriteLine($"PASS: {_assertions} FIFA overlay match-clock discontinuity assertions (pure CLR).");
    }

    // D1: per-frame clock step classification (previous completed update -> current read).
    private static void Classify()
    {
        Check(FifaTimeDiscontinuities.Classify(100, 100.029) == FifaClockStep.Normal, "ordinary 1.75x frame step");
        Check(FifaTimeDiscontinuities.Classify(100, 104.0) == FifaClockStep.Normal, "4 s step is still an ordinary (hitch) step");
        Check(FifaTimeDiscontinuities.Classify(100, 100) == FifaClockStep.Paused, "frozen clock = pause");
        Check(FifaTimeDiscontinuities.Classify(100, 104.001) == FifaClockStep.Forward, "forward > 4 s = FM time skip");
        Check(FifaTimeDiscontinuities.Classify(923.3, 905.0) == FifaClockStep.Backward, "backwards = replay start");
        Check(FifaTimeDiscontinuities.Classify(100, 99.999) == FifaClockStep.Backward, "any backwards step (jitter indistinguishable from a replay start; declared)");
        foreach (var bad in new[] { double.NaN, double.PositiveInfinity, double.NegativeInfinity })
        {
            Check(FifaTimeDiscontinuities.Classify(100, bad) == FifaClockStep.NonFinite, "nonfinite current clock " + bad);
            Check(FifaTimeDiscontinuities.Classify(bad, 100) == FifaClockStep.NonFinite, "nonfinite previous clock " + bad);
        }
        Check(FifaTimeDiscontinuities.Classify(-1e308, 1e308) == FifaClockStep.NonFinite, "overflowing step is nonfinite");
        Check(FifaOverlayPolicy.MaximumMatchStepSeconds == 4.0, "forward skip threshold = existing 4 s match-step bound");
    }

    // D2: bounded Discontinuities[] list; 501st => caller stops normally with discontinuity-limit.
    private static void Bookkeeping()
    {
        var d = new FifaTimeDiscontinuities();
        Check(d.Count == 0 && d.Records.Count == 0, "starts empty");
        Check(d.TryRecord(31514, 35139875428, 923.3, 905.0, 18.25), "replay recorded");
        var r = d.Records[0];
        Check(r.Index == 1 && r.Frame == 31514 && r.QpcTicks == 35139875428 && r.MatchTimeBefore == 923.3 && r.MatchTimeAfter == 905.0
              && r.Direction == "backward" && Math.Abs(r.StepSeconds - (905.0 - 923.3)) < 1e-12 && r.DisplacementMeters == 18.25, "backward record fields");
        Check(d.TryRecord(40000, 1, 905.0, 960.0, 0.5) && d.Records[1].Direction == "forward" && d.Records[1].Index == 2, "forward record");
        Reject(() => d.TryRecord(1, 1, 100, 100.029, 0), "an ordinary step is not a discontinuity");
        Reject(() => d.TryRecord(1, 1, 100, 100, 0), "a pause is not a discontinuity");
        Reject(() => d.TryRecord(1, 1, 100, double.NaN, 0), "nonfinite clock is never recorded (still a failure)");
        Reject(() => d.TryRecord(1, 1, 100, 90, double.NaN), "nonfinite displacement refused");
        Check(d.Count == 2, "refusals record nothing");
        Check(FifaTimeDiscontinuities.MaximumDiscontinuities == 500 && FifaTimeDiscontinuities.LimitReason == "discontinuity-limit", "declared bound and reason");
        var full = new FifaTimeDiscontinuities();
        for (int i = 0; i < 500; i++) Check(full.TryRecord(i, i, 100 + i, 50 + i, 1), "within bound");
        Check(!full.TryRecord(501, 501, 1000, 900, 1) && full.Count == 500, "501st refused (caller stops: discontinuity-limit), list bounded");
        var json = JsonSerializer.Serialize(d.Records);
        Check(json.Contains("\"Direction\":\"backward\"") && json.Contains("\"MatchTimeBefore\":923.3"), "records serialize");
    }

    // D3: the overlay's discontinuity frame = classify against the motion baseline, record, re-baseline (no Observe):
    // a > 3 m teleport on that same frame is accepted; the same teleport or clock step on any other frame still stops.
    private static FifaMatchMotion Running(out double t, out Vector3 p)
    {
        var m = new FifaMatchMotion(900, P(0, 0)); t = 900; p = P(0, 0);
        for (int i = 1; i <= 30; i++) { t = 900 + i * 0.0292; p = P(0, (float)(i * 0.0292 * 1.9)); m.Observe(t, p); }
        return m;
    }

    private static void Motion()
    {
        var m = Running(out var t, out var p);
        Check(m.LastTime == t, "motion exposes the last accepted match time (classification baseline)");
        Check(Math.Abs(m.DisplacementTo(P(3, 4) + p) - 5) < 1e-5 && m.DisplacementTo(p + new Vector3(0, 9, 0)) == 0, "horizontal displacement from the last accepted root");
        Check(Math.Abs(m.Speed - 1.9) < 0.01, "walking 1.9 m/s before the replay");
        var far = P(-30, 12);
        Reject(() => Running(out _, out _).Observe(t - 18.3, far), "backwards clock through Observe still throws (only the classified discontinuity path re-baselines)");
        Reject(() => Running(out _, out _).Observe(t + 0.0292, far), "teleport > 3 m without a clock discontinuity still stops");
        Reject(() => Running(out _, out _).Observe(t + 4.5, p), "forward > 4 s through Observe still throws");
        // Replay start: backwards 18.3 s, player 32 m away, same frame.
        Check(FifaTimeDiscontinuities.Classify(m.LastTime, t - 18.3) == FifaClockStep.Backward, "replay start classified");
        m.Rebaseline(t - 18.3, far);
        double r0 = t - 18.3;
        // Q12 (H163 surviving mutant): speed after >= 0.25 s of match time post re-baseline must come from the new samples only.
        for (int i = 1; i <= 12; i++) m.Observe(r0 + i * 0.0292, far + P(0, (float)(i * 0.0292 * 6.0)));
        Check(m.WindowSeconds >= 0.25 && Math.Abs(m.Speed - 6.0) < 0.05, $"post re-baseline speed from new samples only (got {m.Speed})");
        // Replay end: forward jump back to live time (> 4 s), player elsewhere again.
        double r1 = r0 + 12 * 0.0292;
        Check(FifaTimeDiscontinuities.Classify(m.LastTime, r1 + 22.0) == FifaClockStep.Forward, "replay end (back to live) classified forward");
        m.Rebaseline(r1 + 22.0, P(4, -20));
        for (int i = 1; i <= 12; i++) m.Observe(r1 + 22.0 + i * 0.0292, P(4, -20) + P((float)(i * 0.0292 * 2.0), 0));
        Check(Math.Abs(m.Speed - 2.0) < 0.05, $"post replay-end speed from new samples only (got {m.Speed})");
        // Backwards across a suspension re-baseline too (Q12 for the H162 path).
        var n = Running(out var tn, out _); n.Rebaseline(tn - 30, far);
        for (int i = 1; i <= 12; i++) n.Observe(tn - 30 + i * 0.0292, far + P((float)(i * 0.0292 * 3.0), 0));
        Check(Math.Abs(n.Speed - 3.0) < 0.05, "post-suspension backwards re-baseline speed correct");
    }

    // D4: numbers from the real live-004 report (0.5.0): 14 cut suspensions, all FORWARD jumps 14-24 s; the stop frame was not
    // sampled. Re-run the last sampled rows through the motion tracker, then the two candidate stop-frame steps.
    private static void LiveFixture004()
    {
        using var doc = JsonDocument.Parse(File.ReadAllText(Path.Combine(FifaOverlaySuspensionTests.RepoRoot(), "tests", "fixtures", "fifa-overlay-live-004.discontinuity.json")));
        var root = doc.RootElement;
        Check(root.GetProperty("Error").GetString() == "Match clock stepped backwards, jumped or is nonfinite.", "live-004 failure message");
        var susp = root.GetProperty("Suspensions").EnumerateArray().ToArray();
        Check(susp.Length == 14, "14 real suspensions");
        foreach (var s in susp)
        {
            double before = s.GetProperty("MatchTimeBefore").GetDouble(), after = s.GetProperty("MatchTimeAfter").GetDouble();
            Check(FifaTimeDiscontinuities.Classify(before, after) == FifaClockStep.Forward && after - before > 14 && after - before < 25, "real cut = forward skip 14-24 s");
            Check(s.GetProperty("RealSeconds").GetDouble() < 0.23, "real cut lasted < 0.23 s");
        }
        var rows = root.GetProperty("LastRows").EnumerateArray().ToArray();
        int stopFrame = root.GetProperty("StoppedFrame").GetInt32();
        var last = rows[^1];
        Check(stopFrame - last.GetProperty("FrameCount").GetInt32() == 5, "stop frame 5 frames after the last sampled row (not in the 0.5.0 timeline)");
        // Rebuild a motion tracker at row cadence (10 frames, ~0.29 s match), as dense frames are not retained.
        var m = new FifaMatchMotion(rows[0].GetProperty("MatchTimeSeconds").GetDouble(), P(rows[0].GetProperty("X").GetSingle(), rows[0].GetProperty("Z").GetSingle()));
        foreach (var row in rows.Skip(1))
        {
            Check(FifaTimeDiscontinuities.Classify(m.LastTime, row.GetProperty("MatchTimeSeconds").GetDouble()) == FifaClockStep.Normal, "sampled rows: ordinary steps");
            m.Observe(row.GetProperty("MatchTimeSeconds").GetDouble(), P(row.GetProperty("X").GetSingle(), row.GetProperty("Z").GetSingle()));
        }
        double expected = last.GetProperty("MatchTimeSeconds").GetDouble() + 5 * last.GetProperty("MatchDt").GetDouble();
        // SYNTHETIC stop-frame values (the real one was not sampled): a goal replay going back 20 s, or a forward skip of 20 s.
        foreach (var candidate in new[] { expected - 20, expected + 20 })
        {
            var step = FifaTimeDiscontinuities.Classify(m.LastTime, candidate);
            Check(step == FifaClockStep.Backward || step == FifaClockStep.Forward, "live-004 stop-frame candidate is a discontinuity under 0.5.1");
            var d = new FifaTimeDiscontinuities();
            Check(d.TryRecord(stopFrame, root.GetProperty("StoppedQpcTicks").GetInt64(), m.LastTime, candidate, 0), "recorded instead of failing");
        }
        Check(FifaTimeDiscontinuities.Classify(m.LastTime, double.NaN) == FifaClockStep.NonFinite, "NaN at the same frame would still fail");
    }

    // D5: exact stop-frame observation (FailureDetail): what was read on the frame, up to which stage, triggering values.
    private static void Observation()
    {
        var o = new FifaFrameObservation();
        Check(o.Snapshot() == null, "no observation before the first frame");
        o.Begin(31514, 35139875428, 923.1528930664062, P(4.2270274f, -20.120258f));
        o.Stage = "match-clock";
        var a = o.Snapshot();
        Check(a.Frame == 31514 && a.QpcTicks == 35139875428 && a.Stage == "match-clock" && a.PreviousMatchTime == 923.1528930664062 && a.MatchTime == null
              && a.MatchDt == null && a.RootPosition == null && a.DisplacementMeters == null && a.ClockStep == null, "partial frame: only what was read");
        o.SetWallDt(0.0167); o.SetPosition(P(7.2270274f, -16.120258f)); o.SetClock(905.0);
        var b = o.Snapshot();
        Check(b.WallDt == 0.0167 && b.MatchTime == 905.0 && Math.Abs(b.MatchDt.Value - (905.0 - 923.1528930664062)) < 1e-12 && b.ClockStep == "Backward"
              && Math.Abs(b.DisplacementMeters.Value - 5) < 1e-4 && b.PreviousRootPosition.X == 4.2270274f && b.RootPosition.Z == -16.120258f, "full triggering values");
        Check(a.MatchTime == null, "snapshot is an independent copy");
        o.SetClock(double.NaN); o.SetPosition(new Vector3(float.NaN, 0, 0));
        var c = o.Snapshot();
        Check(c.MatchTime == null && c.MatchDt == null && c.RootPosition == null && c.DisplacementMeters == null && c.NonFiniteObserved && c.ClockStep == "NonFinite", "nonfinite values => null + flag (report never lost)");
        var json = JsonSerializer.Serialize(new[] { a, b, c });
        Check(json.Contains("\"Stage\":\"match-clock\"") && !json.Contains("NaN"), "serializable");
        o.SetClock(905.0); o.SetWallDt(0.02); o.SetPosition(P(1, 1)); o.Stage = "pose-write"; // M11 (H164 survivor): Begin must clear a previous frame's values
        o.Begin(31515, 1, null, null);
        var d = o.Snapshot();
        Check(d.Frame == 31515 && d.WallDt == null && d.MatchTime == null && !d.NonFiniteObserved && d.Stage == null && d.PreviousMatchTime == null, "Begin resets every field");
    }

    // D6: the stop-frame row is always the LAST timeline row (head not full, ring full), even off the 1-in-10 cadence.
    private static void StopRow()
    {
        FifaTimelineRow R(int f, string kind = "sample") => new FifaTimelineRow { FrameCount = f, Kind = kind, MatchTimeSeconds = f, Clip = "walk_F2" };
        var small = new FifaTimelineSampler(4, 4);
        small.Add(R(1)); small.Add(R(2, "stop"));
        Check(small.Rows[^1].Kind == "stop" && small.Rows.Count == 2, "stop row last (head not full)");
        var t = new FifaTimelineSampler(4, 4);
        for (int i = 0; i < 100; i++) t.Add(R(i * 10));
        t.Add(new FifaTimelineRow { FrameCount = 995, Kind = "stop", Clip = "walk_F2", MatchTimeSeconds = null, MatchDt = null, WallDt = null, FrameDisplacementMeters = null });
        Check(t.Rows[^1].FrameCount == 995 && t.Rows[^1].Kind == "stop" && t.Total == 101, "stop row last (ring full), counted");
        var json = JsonSerializer.Serialize(t.Rows[^1]);
        Check(json.Contains("\"MatchTimeSeconds\":null") && json.Contains("\"Kind\":\"stop\""), "unread stop-frame fields are null, not invented");
    }

    internal static Vector3 P(float x, float z) => new(x, 0, z);
}
