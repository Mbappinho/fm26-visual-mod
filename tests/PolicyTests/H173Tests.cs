#nullable disable
using FM26.VisualDiagnostics;
internal static class H173Tests
{
    private static int _checks;
    private static void Check(bool ok, string label) { _checks++; if (!ok) throw new Exception("H173 FAIL: " + label); }
    internal static void Run(string one = null)
    {
        var tests = new Dictionary<string, Action> { ["speed-filter"] = SpeedFilter, ["direction-filter"] = DirectionFilter, ["dwell"] = Dwell, ["queue"] = Queue, ["native-tier"] = NativeTier, ["idle"] = Idle, ["rate"] = Rate, ["reset"] = Reset, ["pause"] = Pause, ["stats"] = Stats, ["trace"] = Trace, ["interrupted-dwell"] = InterruptedDwell, ["idle-minimum-dwell"] = IdleMinimumDwell, ["exact-trace-replay"] = ExactTraceReplay, ["phase-wrap"] = PhaseWrap, ["forced-direction"] = ForcedDirection, ["current-fade-contract"] = CurrentFadeContract, ["idle-timer-contract"] = IdleTimerContract, ["absolute-cycle-entry"] = AbsoluteCycleEntry };
        foreach (var (name, test) in tests) if (one == null || one == "all" || name == one) { test(); Console.WriteLine("H173 PASS: " + name); }
        if (one != null && one != "all" && !tests.ContainsKey(one)) throw new ArgumentException("Unknown H173 case: " + one);
        Console.WriteLine($"PASS: {_checks} H173 stability assertions.");
    }
    private static FifaLocomotion New() => new(FifaLocomotionTests.PackHeader());
    private static void Step(FifaLocomotion l, double speed, double angle = 0, double dt = .01) { double r = angle * Math.PI / 180; l.Update(dt, speed, speed * .25 * Math.Sin(r), speed * .25 * Math.Cos(r), 0, 1); }
    private static void Settle(FifaLocomotion l, double speed, double angle = 0, int n = 250) { for (int i = 0; i < n; i++) Step(l, speed, angle); }
    private static void AbsoluteCycleEntry()
    {
        var p = FifaLocomotionTests.PackHeader(); var walk = p.Clips["walk"]; var jog = p.Clips["jog"];
        for (int seed = 0; seed < 20; seed++)
        {
            var l = New(); Settle(l, 1.9, 0, 210 + seed * 7); double before = l.TargetPhase;
            for (int i = 0; i < 300 && l.TargetClip != "jog"; i++) { before = l.TargetPhase; Step(l, 3.2); }
            Check(l.TargetClip == "jog", "absolute-cycle entry probe reaches jog");
            double u = (before - walk.CycleOriginKey.Value) / walk.KeysPerCycle.Value; u -= Math.Floor(u);
            double expected = (jog.CycleOriginKey.Value + u * jog.KeysPerCycle.Value + .01 * 30 * l.Rate) % jog.Keys;
            Check(Math.Abs(l.TargetPhase - expected) < 1e-9, "incoming clip enters declared absolute cycle, not merely equivalent fractional gait phase");
        }
    }
    private static void CurrentFadeContract()
    {
        var l = New(); Settle(l, 1.9);
        for (int i = 0; i < 300 && l.TargetClip != "jog"; i++) Step(l, 3.2);
        Check(l.TargetClip == "jog" && l.FromClip == "walk" && Math.Abs(l.Weight - .01 / .25) < 1e-9, "current tier fade starts with dt/0.25 target weight");
        Settle(l, 3.2);
        for (int i = 0; i < 300 && l.TargetClip != "strafe_right_jog"; i++) Step(l, 3.2, 90);
        Check(l.TargetClip == "strafe_right_jog" && l.FromClip == "jog" && Math.Abs(l.Weight - .01 / .3) < 1e-9, "current direction-only fade starts with dt/0.3 target weight");
        string target = l.TargetClip; double phase = l.TargetPhase; l.Snap();
        Check(!l.Blending && l.TargetClip == target && l.TargetPhase == phase && l.Stats().RebaselineSnaps == 1, "Snap while actually blending retains target phase and ends source");
        l = New(); Settle(l, 1.9);
        for (int i = 0; i < 600 && l.TargetClip != "idle"; i++) Step(l, 0);
        Check(l.TargetClip == "idle" && l.FromClip == "walk", "current moving-to-idle fade exercised");
        double before = l.FromPhase; Step(l, 0);
        Check(Math.Abs(l.FromPhase - (before + .01 * 30 * .6) % FifaLocomotionTests.PackHeader().Clips["walk"].Keys) < 1e-9, "moving source keeps its clamped native-informed rate while fading to idle");
    }
    private static void IdleTimerContract()
    {
        var l = New(); Settle(l, 1);
        void BelowUntilIdle(bool interrupt)
        {
            int below = 0;
            for (int i = 0; i < 600; i++)
            {
                Step(l, 0); if (l.FilteredSpeed < .25) below++; else below = 0;
                if (below > 0 && below < 20) Check(l.Tier == FifaLocomotion.Walk, "idle requires full uninterrupted 0.2 seconds below the filtered threshold");
                if (interrupt && below == 5) { Step(l, 3, 0, .1); Check(l.FilteredSpeed > .25, "timer reset control crosses above threshold"); return; }
                if (below == 20) { Check(l.Tier == FifaLocomotion.Idle, "idle enters at 0.2 seconds below"); return; }
            }
            throw new Exception("idle probe did not reach threshold");
        }
        BelowUntilIdle(true); BelowUntilIdle(false);
    }
    private static void ForcedDirection()
    {
        var l = New(); Settle(l, 3); Step(l, 3, 90, .4); Step(l, 5, 90, .5);
        Check(l.Tier == FifaLocomotion.Run && l.Direction == FifaLocomotion.Forward, "run forces forward after an incomplete strafe candidate");
        Step(l, 3, 90, .3); Step(l, 3, 90, .2);
        Check(l.Tier == FifaLocomotion.Jog && l.Direction == FifaLocomotion.Forward, "forced forward invalidates the old partial strafe dwell");
        Step(l, 3, 90, .3); Check(l.Direction == FifaLocomotion.StrafeRight, "a fresh sustained strafe candidate is still applied");
    }
    private static void ExactTraceReplay()
    {
        var l = New(); var trace = new FifaMotionTrace();
        for (int i = 0; i < 2000; i++)
        {
            double t = i * .01, speed = 2.6 + 2.5 * Math.Sin(t * .7), r = 3 * Math.Sin(t * 1.3);
            int resets = i % 173 == 0 ? 1 : 0; bool paused = i % 23 == 0, ready = i % 173 > 25, disc = resets != 0;
            if (resets != 0) l.Snap();
            double mx = speed * .25 * Math.Sin(r), mz = speed * .25 * Math.Cos(r), dt = paused ? 0 : .01;
            if (ready && !paused && !disc) l.Update(dt, speed, mx, mz, 0, 1);
            trace.Add(i, t, dt, speed, mx, mz, 0, 1, resets, ready, paused, disc, l);
        }
        var bytes = System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(trace.Snapshot());
        using var d = System.Text.Json.JsonDocument.Parse(bytes);
        Check(H173Replay.VerifyTrace(d.RootElement, FifaLocomotionTests.PackHeader()) == 2000, "serialized complete prefix replays exactly including pauses, cuts and refill holds");
    }
    private static void PhaseWrap()
    {
        double r = -170 * Math.PI / 180, f = 170 * Math.PI / 180;
        Check(Math.Abs(FifaLocomotion.SignedAngle(Math.Sin(r), Math.Cos(r), Math.Sin(f), Math.Cos(f)).Value - 20) < 1e-9, "signed angle wraps below minus 180");
        var p = FifaLocomotionTests.PackHeader(); var l = new FifaLocomotion(p); int movingBlends = 0;
        for (int i = 0; i < 12000; i++)
        {
            double t = i * .01; Step(l, 4 + 3.9 * Math.Sin(t * .8), 180 * Math.Sin(t * .4));
            var target = p.Clips[l.TargetClip]; Check(l.TargetPhase >= 0 && l.TargetPhase < target.Keys, "current target phase remains inside clip");
            if (l.Blending)
            {
                var source = p.Clips[l.FromClip]; Check(l.FromPhase >= 0 && l.FromPhase < source.Keys && l.Weight >= 0 && l.Weight <= 1, "current source and weight bounded");
                if (source.KeysPerCycle.HasValue && target.KeysPerCycle.HasValue)
                {
                    double a = (l.FromPhase - source.CycleOriginKey.Value) / source.KeysPerCycle.Value; a -= Math.Floor(a);
                    double b = (l.TargetPhase - target.CycleOriginKey.Value) / target.KeysPerCycle.Value; b -= Math.Floor(b);
                    double delta = Math.Abs(a - b); Check(Math.Min(delta, 1 - delta) < 1e-9, "current moving blend retains normalized gait phase"); movingBlends++;
                }
            }
        }
        Check(movingBlends > 100, "phase probe exercises nontrivial current-source blends");
    }
    private static void IdleMinimumDwell()
    {
        var l = New(); Settle(l, 5); l.Snap(); Step(l, 0, 0, .2);
        Check(l.Tier == FifaLocomotion.Run, "idle delay does not bypass minimum tier dwell after a reset");
        Step(l, 0, 0, .25); Check(l.Tier == FifaLocomotion.Idle, "idle still enters after minimum dwell and 0.2 below threshold");
    }
    private static void InterruptedDwell()
    {
        var l = New(); Step(l, 1, 0, .3); Step(l, 0, 0, 1.0); Step(l, 1, 0, .2);
        Check(l.Tier == FifaLocomotion.Idle, "idle interval cancels the previous incomplete walk candidate");
    }
    private static void Trace()
    {
        var l = New(); var trace = new FifaMotionTrace();
        Check(FifaMotionTrace.Capacity == 3200, "dense prefix retains 3200 frames for each of the first two actors");
        for (int i = 0; i < 5000; i++) { Step(l, 3); trace.Add(i, i * .01, .01, 3, 0, .75, 0, 1, 0, true, false, false, l); }
        var s = trace.Snapshot();
        Check(s.Rows.Length == FifaMotionTrace.Capacity && s.TotalRows == 5000 && s.DroppedRows == 5000 - FifaMotionTrace.Capacity, "trace retains bounded PREFIX with exact drop count");
        Check(s.Rows[0].Frame == 0 && s.Rows[^1].Frame == FifaMotionTrace.Capacity - 1, "trace includes constructor start, not an unreplayable tail");
        Check(s.Columns.Length == s.Rows[0].Values().Length && s.InitialState.Contains("idle"), "trace declares columns and constructor start state");
        string json = System.Text.Json.JsonSerializer.Serialize(s, new System.Text.Json.JsonSerializerOptions { WriteIndented = true });
        Check(json.Contains("fm26.fifa-motion-trace.v1") && json.Contains("[0,"), "compact numeric row serialization with schema");
    }
    private static void Stats()
    {
        var l = New();
        for (int i = 0; i < 12000; i++) { double t = i * .01; Step(l, 2.5 + 1.6 * Math.Sin(t * 2.1), 150 * Math.Sin(t * 2.9)); }
        using var d = System.Text.Json.JsonDocument.Parse(System.Text.Json.JsonSerializer.Serialize(l.Stats())); var s = d.RootElement;
        Check(s.TryGetProperty("PendingApplications", out var pending), "bounded pending-application counter is reported");
        Check(pending.GetInt64() > 0, "pending target really applied in the probe");
        Check(s.GetProperty("DwellHistogram").GetArrayLength() == 6, "six fixed dwell histogram bins");
        Check(s.GetProperty("DwellHistogram").EnumerateArray().Sum(x => x.GetInt64()) == l.Stats().TierFades + l.Stats().DirectionFades, "histogram counts actual clip switches");
        Check(s.GetProperty("SwitchesPerMatchSecond").GetDouble() > 0, "match-time switch rate reported");
    }
    private static void Pause()
    {
        var l = New(); Settle(l, 3); double phase = l.TargetPhase, speed = l.FilteredSpeed; var stats = l.Stats();
        for (int i = 0; i < 100; i++) l.Update(0, 0, 0, 0, 0, 1);
        Check(l.TargetPhase == phase && l.FilteredSpeed == speed && l.Stats().Updates == stats.Updates, "pause advances no filter, phase, counter or dwell time");
    }
    private static void Reset()
    {
        var l = New(); Settle(l, 5); string clip = l.TargetClip; double phase = l.TargetPhase;
        Step(l, 1.9, 0, .4); l.Snap();
        Check(l.TargetClip == clip && !l.Blending, "rebaseline retains the current clip and drops any cross-cut blend");
        Step(l, 1.9);
        Check(Math.Abs(l.FilteredSpeed - 1.9) < 1e-12, "rebaseline reseeds speed filter instead of leaking pre-cut history");
        Check(l.TargetClip == clip, "candidate dwell is reset across a cut");
        Check(l.Stats().Rebaselines == 1, "one explicit rebaseline counted once");
    }
    private static void Rate()
    {
        var p = FifaLocomotionTests.PackHeader();
        Check(FifaLocomotion.ClipRate(p.Clips["walk"], .1) == .6, "moving playback floor 0.6");
        Check(FifaLocomotion.ClipRate(p.Clips["sprint"], 15) == 1.5, "moving playback ceiling 1.5");
        Check(Math.Abs(FifaLocomotion.ClipRate(p.Clips["run"], 4.72) - 1) < 1e-12, "native speed maps to rate 1");
    }
    private static void Idle()
    {
        var l = New(); Settle(l, .35); Check(l.Tier == FifaLocomotion.Idle, "idle stays below the separate 0.5 exit threshold");
        Settle(l, 1.0); Check(l.Tier == FifaLocomotion.Walk, "leave idle above 0.5 after dwell");
        Settle(l, .35); Check(l.Tier == FifaLocomotion.Walk, "walking stays above the separate 0.25 idle entry threshold");
        // Seed a valid low-speed estimate after a cut: the idle timer must start fresh, not inherit the pre-cut timer.
        Settle(l, .1, 0, 600); Check(l.Tier == FifaLocomotion.Idle, "idle after filtered speed below 0.25 for at least 0.2 seconds");
        Settle(l, .4); Check(l.Tier == FifaLocomotion.Idle, "idle exit uses two-sided hysteresis");
    }
    private static void NativeTier()
    {
        var l = New(); Settle(l, 1.9); Settle(l, 2.6);
        Check(l.Tier == FifaLocomotion.Walk, "forward walk/jog upper boundary is native midpoint plus 0.35");
        Settle(l, 3.2); Check(l.Tier == FifaLocomotion.Jog, "native jog speed selects jog");
        Settle(l, 2.4); Check(l.Tier == FifaLocomotion.Jog, "lower boundary is native midpoint minus 0.35");
        Settle(l, 1.9); Check(l.Tier == FifaLocomotion.Walk, "below lower boundary selects walk");
        l = New(); Settle(l, 5); Settle(l, 6.5); Check(l.Tier == FifaLocomotion.Run, "run/sprint midpoint derives from both native speeds");
        Settle(l, 7.5); Check(l.Tier == FifaLocomotion.Sprint, "sustained sprint selected");
    }
    private static void Queue()
    {
        var l = New();
        for (int i = 0; i < 12000; i++) { double t = i * .01; Step(l, 2.5 + 1.6 * Math.Sin(t * 2.1), 150 * Math.Sin(t * 2.9)); }
        var s = l.Stats();
        Check(s.TierFades + s.DirectionFades > 40, "queue probe exercises many genuine transitions");
        Check(s.MidFadeSnaps == 0, "a new selection never interrupts an in-progress two-clip fade");
    }
    private static void Dwell()
    {
        var l = New(); Settle(l, 3);
        Step(l, 5, 0, .4); Check(l.Tier == FifaLocomotion.Jog, "new tier must persist for 0.45 match seconds");
        Step(l, 5, 0, .06); Check(l.Tier == FifaLocomotion.Run, "sustained tier is applied after dwell");
        l = New(); Settle(l, 3);
        Step(l, 3, 90, .4); Check(l.Direction == FifaLocomotion.Forward, "new filtered direction must persist for 0.45 match seconds");
        Step(l, 3, 90, .06); Check(l.Direction == FifaLocomotion.StrafeRight, "sustained direction is applied after dwell");
        Step(l, 3, -90, .4); Check(l.Direction == FifaLocomotion.StrafeRight, "opposite turn cannot bypass direction dwell");
    }
    private static void DirectionFilter()
    {
        var l = New(); Settle(l, 3); Step(l, 3, 90);
        Check(l.Direction == FifaLocomotion.Forward, "one-frame 90-degree jitter is filtered");
        Settle(l, 3, 90); Check(l.Direction == FifaLocomotion.StrafeRight, "sustained right turn follows the signal");
        l.Update(.01, 3, 0, 0, 0, 1); Check(l.Direction == FifaLocomotion.StrafeRight, "zero displacement does not poison heading");
        l = New(); Settle(l, 3, 179); Settle(l, 3, -179);
        Check(l.Direction == FifaLocomotion.Backward, "unit-vector filter crosses 180 rather than zero degrees");
        Settle(l, 3, 0); Check(l.Direction == FifaLocomotion.Forward && double.IsFinite(l.Rate), "opposite vectors recover without NaN");
    }
    private static void SpeedFilter()
    {
        var l = New(); Settle(l, 1.9); Step(l, 3.0);
        double expected = 1.9 + (3 - 1.9) * (1 - Math.Exp(-.01 / .3));
        Check(l.TargetClip == "walk", "a one-frame speed spike does not change tier");
        Check(Math.Abs(l.Rate - expected / 1.898) < 1e-9, "speed uses exact dt-aware exponential 0.3 match-second filter");
        Settle(l, 3.0); Check(l.TargetClip == "jog", "sustained speed eventually selects jog");
    }
}
