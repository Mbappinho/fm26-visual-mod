#nullable disable
using System.Text.Json;
using FM26.VisualDiagnostics;

// H162: executable suspend/resume state machine of the FIFA overlay (pure CLR). FM view changes (replays, cuts) suspend
// the owned overlay instead of ending it; resume re-baselines match motion; bounded suspensions and report.
internal static class FifaOverlaySuspensionTests
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
        Bookkeeping(); Decide(); LiveFixtures(); Rebaseline(); Timeline(); ReportSize();
        Console.WriteLine($"PASS: {_assertions} FIFA overlay suspend/resume assertions (pure CLR state machine).");
    }

    // C2: entering/leaving a suspension is recorded (frames, real duration, match clock before/after, reason); bounded count.
    private static void Bookkeeping()
    {
        var s = new FifaViewSuspension();
        Check(!s.Suspended && s.Count == 0 && s.Records.Count == 0, "starts not suspended");
        Check(s.TryBegin(100, 1_000, 107.5), "first suspension accepted");
        Check(s.Suspended && s.Count == 1, "suspended after begin");
        Reject(() => s.TryBegin(101, 1_010, 107.5), "nested suspension");
        var r = s.Records[0];
        Check(r.Index == 1 && r.StartFrame == 100 && r.StartQpcTicks == 1_000 && r.MatchTimeBefore == 107.5 && r.Reason == "fm-view-changed"
              && r.EndFrame == null && r.End == null, "open record");
        s.Resume(160, 1_000 + 25 * 10, 10, 95.25);
        Check(!s.Suspended, "resumed");
        Check(r.EndFrame == 160 && r.EndQpcTicks == 1_250 && r.RealSeconds == 25 && r.MatchTimeAfter == 95.25 && r.End == "resumed", "closed record (clock may go backwards across a suspension)");
        Reject(() => s.Resume(161, 1_300, 10, 96), "resume without suspension");
        Check(s.TryBegin(200, 2_000, null), "second suspension; unknown clock before stays null");
        s.CloseOnStop(210, 2_050, 10, "requested-stop");
        var r2 = s.Records[1];
        Check(!s.Suspended && r2.End == "requested-stop" && r2.EndFrame == 210 && r2.RealSeconds == 5 && r2.MatchTimeAfter == null, "stop while suspended closes the record");
        s.CloseOnStop(211, 2_060, 10, "late");
        Check(r2.End == "requested-stop", "close on stop is a no-op when not suspended");
        var limit = new FifaViewSuspension();
        for (int i = 0; i < FifaViewSuspension.MaximumSuspensions; i++) { Check(limit.TryBegin(i * 10, i * 100, 1), "within count bound"); limit.Resume(i * 10 + 1, i * 100 + 1, 10, 1); }
        Check(FifaViewSuspension.MaximumSuspensions == 200, "declared suspension count bound");
        Check(!limit.TryBegin(9_999, 99_999, 1) && !limit.Suspended && limit.Records.Count == 200, "201st suspension refused (caller stops: suspend-limit), record list bounded");
    }

    // Renderer tuple during a suspension: start-captured (enabled, active), current readback (null = dead/unreadable),
    // forceRenderingOff expectation/readback (both null in default runs: never read without HideNativePlayer).
    private static (bool, bool, bool?, bool?, bool?, bool?) R(bool be, bool ba, bool? ae, bool? aa, bool? xf = null, bool? af = null) => (be, ba, ae, aa, xf, af);
    private static void Is((FifaSuspendedDecision Decision, string Reason, string Error) d, FifaSuspendedDecision expected, string reason, bool nullError, string label)
        => Check(d.Decision == expected && d.Reason == reason && (d.Error == null) == nullError, label + $" (got {d.Decision}/{d.Reason}/{d.Error})");

    // C3: every suspended-frame decision. Order: structure/actor lost -> match end; unreadable/enabled change -> failure;
    // FM reset our forceRenderingOff -> clean stop; real-time bound -> suspend-limit; all back to start activity -> resume;
    // all inactive -> stay; partial reactivation -> failure (any doubt stops).
    private static void Decide()
    {
        var off = new[] { R(true, true, true, false), R(true, true, true, false), R(true, false, true, false) };
        var back = new[] { R(true, true, true, true), R(true, true, true, true), R(true, false, true, false) };
        Is(FifaViewSuspension.Decide(true, true, off, 1), FifaSuspendedDecision.Stay, null, true, "all inactive: stay suspended");
        Is(FifaViewSuspension.Decide(true, true, back, 1), FifaSuspendedDecision.Resume, null, true, "renderers back to their start activity: resume");
        Is(FifaViewSuspension.Decide(true, true, new[] { R(true, true, true, true), R(true, true, true, false), R(true, false, true, false) }, 1),
            FifaSuspendedDecision.StopFailure, "owned-update-failure", false, "partial reactivation: failure stop");
        Is(FifaViewSuspension.Decide(true, true, new[] { R(true, true, true, true), R(true, true, true, true), R(true, false, true, true) }, 1),
            FifaSuspendedDecision.StopFailure, "owned-update-failure", false, "a renderer inactive at start became active: failure stop (not the start state)");
        Is(FifaViewSuspension.Decide(true, true, new[] { R(true, true, false, false), R(true, true, true, false) }, 1),
            FifaSuspendedDecision.StopFailure, "owned-update-failure", false, "enabled changed while suspended: failure");
        Is(FifaViewSuspension.Decide(true, true, new[] { R(true, true, null, null), R(true, true, true, false) }, 1),
            FifaSuspendedDecision.StopFailure, "owned-update-failure", false, "unreadable renderer with structure intact: failure");
        Is(FifaViewSuspension.Decide(true, true, Array.Empty<(bool, bool, bool?, bool?, bool?, bool?)>(), 1),
            FifaSuspendedDecision.StopFailure, "owned-update-failure", false, "empty inventory: failure");
        Is(FifaViewSuspension.Decide(false, true, off, 1), FifaSuspendedDecision.StopMatchEnded, "fm-match-ended", true, "root/scene/controller/publisher gone: normal match end");
        Is(FifaViewSuspension.Decide(true, false, off, 1), FifaSuspendedDecision.StopMatchEnded, "fm-match-ended", true, "actor gone: normal match end");
        Is(FifaViewSuspension.Decide(false, true, new[] { R(true, true, null, null), R(true, true, null, null) }, 1),
            FifaSuspendedDecision.StopMatchEnded, "fm-match-ended", true, "scene unloaded (renderers dead): normal end; restoration certified separately by cleanup");
        Is(FifaViewSuspension.Decide(false, true, new[] { R(true, true, false, false), R(true, true, null, null) }, 1),
            FifaSuspendedDecision.StopFailure, "owned-update-failure", false, "structure lost with a readable enabled change: not structurally clean, failure");
        // Hide runs: forceRenderingOff must still be what we wrote; FM resetting it ends the run cleanly (ledger restore).
        var hidOff = new[] { R(true, true, true, false, true, true), R(true, false, true, false, true, true) };
        Is(FifaViewSuspension.Decide(true, true, hidOff, 1), FifaSuspendedDecision.Stay, null, true, "hidden + inactive + flag still set: stay");
        Is(FifaViewSuspension.Decide(true, true, new[] { R(true, true, true, true, true, true), R(true, false, true, false, true, true) }, 1),
            FifaSuspendedDecision.Resume, null, true, "hidden + back + flag still set: resume");
        Is(FifaViewSuspension.Decide(true, true, new[] { R(true, true, true, true, true, false), R(true, false, true, false, true, true) }, 1),
            FifaSuspendedDecision.StopHideReset, "native-hide-reset", true, "FM reset forceRenderingOff on reactivation: clean stop, never re-hide");
        Is(FifaViewSuspension.Decide(true, true, new[] { R(true, true, true, false, true, false), R(true, false, true, false, true, true) }, 1),
            FifaSuspendedDecision.StopHideReset, "native-hide-reset", true, "FM reset forceRenderingOff while inactive: clean stop");
        Is(FifaViewSuspension.Decide(true, true, new[] { R(true, true, true, false, true, null), R(true, false, true, false, true, true) }, 1),
            FifaSuspendedDecision.StopFailure, "owned-update-failure", false, "forceRenderingOff unreadable in a hide run: failure");
        // Real-time bound per suspension.
        Check(FifaViewSuspension.MaximumSuspendedSeconds == 120, "declared per-suspension real-time bound 120 s");
        Is(FifaViewSuspension.Decide(true, true, off, 119.999), FifaSuspendedDecision.Stay, null, true, "below 120 s: stay");
        Is(FifaViewSuspension.Decide(true, true, off, 120), FifaSuspendedDecision.StopSuspendLimit, "suspend-limit", true, "120 s suspended: normal suspend-limit stop");
        Is(FifaViewSuspension.Decide(true, true, back, 120), FifaSuspendedDecision.StopSuspendLimit, "suspend-limit", true, "bound checked before resume");
        // H164 (H163 suggestion 5): a nonfinite/negative suspended duration is an inconsistency, not a normal bound.
        Is(FifaViewSuspension.Decide(true, true, off, double.NaN), FifaSuspendedDecision.StopFailure, "owned-update-failure", false, "nonfinite duration: failure");
        Is(FifaViewSuspension.Decide(true, true, off, double.PositiveInfinity), FifaSuspendedDecision.StopFailure, "owned-update-failure", false, "infinite duration: failure");
        Is(FifaViewSuspension.Decide(true, true, off, -1), FifaSuspendedDecision.StopFailure, "owned-update-failure", false, "backwards real clock: failure");
        Is(FifaViewSuspension.Decide(true, true, off, 0), FifaSuspendedDecision.Stay, null, true, "zero duration (same QPC tick): stay");
        // H164 (H163 suggestion 6): thread mismatch / structure read exception while suspended is a failure, not a match end.
        Is(FifaViewSuspension.Decide(FifaStructureState.Unreadable, true, off, 1), FifaSuspendedDecision.StopFailure, "owned-update-failure", false, "unreadable structure / thread mismatch: failure");
        Is(FifaViewSuspension.Decide(FifaStructureState.Unreadable, false, off, 1), FifaSuspendedDecision.StopFailure, "owned-update-failure", false, "unreadable structure has precedence over actor loss");
        Is(FifaViewSuspension.Decide(FifaStructureState.Ended, true, off, 1), FifaSuspendedDecision.StopMatchEnded, "fm-match-ended", true, "structure readably gone: match end");
        Is(FifaViewSuspension.Decide(FifaStructureState.Intact, true, off, 1), FifaSuspendedDecision.Stay, null, true, "intact: stay");
        Is(FifaViewSuspension.Decide(FifaStructureState.Intact, false, off, 1), FifaSuspendedDecision.StopMatchEnded, "fm-match-ended", true, "intact structure, actor gone: match end");
        Is(FifaViewSuspension.Decide(false, true, off, 500), FifaSuspendedDecision.StopMatchEnded, "fm-match-ended", true, "match end has precedence over the bound");
    }

    // Fixtures derived from the real live-002 (0.3.2) and live-003 (0.4.0, HideNativePlayer) final reports.
    private static void LiveFixtures()
    {
        foreach (var name in new[] { "fifa-overlay-live-002.flags.json", "fifa-overlay-live-003.flags.json" })
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(Path.Combine(RepoRoot(), "tests", "fixtures", name)));
            var flags = doc.RootElement.GetProperty("Flags").EnumerateArray().ToArray();
            bool hide = flags[0].TryGetProperty("AfterHideForceRenderingOff", out _);
            bool B(JsonElement f, string p) => f.GetProperty(p).GetBoolean();
            // Exact H160 entry condition (ClassifyStop) holds on the stop observation => the run SUSPENDS instead of ending.
            var stopView = flags.Select(f => (B(f, "BeforeEnabled"), B(f, "BeforeActiveInHierarchy"), (bool?)B(f, "AfterEnabled"), (bool?)B(f, "AfterActiveInHierarchy"))).ToArray();
            Check(flags.Length == 39 && stopView.Count(x => x.Item2) == 37, name + ": 39 renderers, 37 active at start");
            Check(FifaOverlayPolicy.ClassifyStop(false, true, true, stopView, "FIFA overlay root/scene/actor context changed.").Reason == FifaOverlayPolicy.ViewChangedReason, name + ": entry condition holds (suspend)");
            // While suspended (hide run: last observed flag = what we wrote, i.e. AfterHideForceRenderingOff=true; not observed live during the cut).
            var during = flags.Select(f => R(B(f, "BeforeEnabled"), B(f, "BeforeActiveInHierarchy"), B(f, "AfterEnabled"), B(f, "AfterActiveInHierarchy"),
                hide ? true : null, hide ? B(f, "AfterHideForceRenderingOff") : null)).ToArray();
            Is(FifaViewSuspension.Decide(true, true, during, 1), FifaSuspendedDecision.Stay, null, true, name + ": suspended frame stays suspended");
            var resumed = flags.Select(f => R(B(f, "BeforeEnabled"), B(f, "BeforeActiveInHierarchy"), B(f, "BeforeEnabled"), B(f, "BeforeActiveInHierarchy"),
                hide ? true : null, hide ? B(f, "AfterHideForceRenderingOff") : null)).ToArray();
            Is(FifaViewSuspension.Decide(true, true, resumed, 10), FifaSuspendedDecision.Resume, null, true, name + ": start activity restored => resume");
            if (hide)
            {
                var reset = resumed.Select((x, i) => i == 5 ? (x.Item1, x.Item2, x.Item3, x.Item4, x.Item5, (bool?)false) : x).ToArray();
                Is(FifaViewSuspension.Decide(true, true, reset, 10), FifaSuspendedDecision.StopHideReset, "native-hide-reset", true, name + ": one flag reset by FM => clean stop");
            }
        }
    }

    // C4: after a suspension the replay/cut jump and a backwards clock are a new baseline, not a teleport/backwards error;
    // the same jumps WITHOUT an intervening re-baseline still stop (unchanged H158 guards).
    private static void Rebaseline()
    {
        var p0 = new System.Numerics.Vector3(12.7f, 0, -27.2f);
        var m = new FifaMatchMotion(100.0, p0);
        for (int i = 1; i <= 12; i++) m.Observe(100.0 + i * 0.029, p0 + new System.Numerics.Vector3(0.04f * i, 0, 0));
        Check(m.Speed > 1.0, "moving before the cut");
        var far = new System.Numerics.Vector3(-30f, 0, 10f);
        Reject(() => new FifaMatchMotion(100.0, p0).Observe(95.0, p0), "backwards clock without suspension still stops");
        Reject(() => new FifaMatchMotion(100.0, p0).Observe(100.03, far), "teleport without suspension still stops");
        m.Rebaseline(92.5, far); // replay: clock went BACKWARDS and the player is elsewhere
        Check(m.Speed == 0 && m.WindowSeconds == 0 && m.MatchDt == 0 && m.FrameDisplacement == 0 && !m.Paused, "re-baseline resets speed window and per-frame state");
        m.Observe(92.529, far + new System.Numerics.Vector3(0.04f, 0, 0));
        Check(Math.Abs(m.MatchDt - 0.029) < 1e-9 && m.FrameDisplacement < 0.05, "first step measured from the new baseline");
        Reject(() => m.Observe(80.0, far), "backwards clock after the re-baseline (no new suspension) stops again");
        var n = new FifaMatchMotion(100.0, p0); n.Rebaseline(100.0, p0);
        Reject(() => n.Observe(100.03, far), "teleport after the re-baseline (no new suspension) stops again");
        Reject(() => new FifaMatchMotion(1, p0).Rebaseline(double.NaN, p0), "nonfinite re-baseline clock refused");
    }

    private static FifaTimelineRow Row(int i) => new FifaTimelineRow
    {
        FrameCount = int.MaxValue - i, QpcTicks = long.MaxValue - i, WallDt = 0.016666666666666666 + i * 1e-17, MatchTimeSeconds = 5399.123456789012 + i, MatchDt = 0.029167175292968753,
        MatchSpeedMetersPerSecond = 14.999999999999998, MatchSpeedWindowSeconds = 0.25000000000000006, FrameDisplacementMeters = 2.9999999999999996, Paused = false,
        PublishedPlaybackSpeed = 1.7500001f, PublishedPlaybackSpeedNonFinite = false, Clip = "walk_F2", Rate = 2.9999999999999996, PhaseKeys = 21.999999999999996,
        PackFrame = 4096, YawDegrees = -179.99999999999997, RootPosition = new FifaRowVector { X = -12345.679f, Y = -0.00012345679f, Z = -12345.679f },
        RootForward = new FifaRowVector { X = -0.70710677f, Y = -0.00012345679f, Z = -0.70710677f },
    };

    // C5: timeline bounded for a whole match: first rows kept, then a ring of the LAST rows; dropped count exact.
    private static void Timeline()
    {
        var t = new FifaTimelineSampler(1024, 1024);
        Check(FifaTimelineSampler.DefaultHeadRows == 1024 && FifaTimelineSampler.DefaultTailRows == 1024, "declared head/tail row bounds");
        for (int i = 0; i < 1500; i++) t.Add(Row(i));
        Check(t.Rows.Count == 1500 && t.Dropped == 0 && t.Total == 1500, "below capacity: all rows, in order");
        int total = 3600 * 60 / 10; // 3600 s at 60 fps, one row per 10 frames
        for (int i = 1500; i < total; i++) t.Add(Row(i));
        var rows = t.Rows;
        Check(rows.Count == 2048 && t.Total == total && t.Dropped == total - 2048, "whole match: 2048 rows kept, exact dropped count");
        Check(rows[0].FrameCount == int.MaxValue && rows[1023].FrameCount == int.MaxValue - 1023, "head = first 1024 rows");
        Check(rows[1024].FrameCount == int.MaxValue - (total - 1024) && rows[2047].FrameCount == int.MaxValue - (total - 1), "tail = last 1024 rows, chronological");
        Check(Enumerable.Range(1, 2047).All(k => rows[k].FrameCount < rows[k - 1].FrameCount), "kept rows strictly chronological");
        Reject(() => new FifaTimelineSampler(0, 1024), "zero head");
        Reject(() => new FifaTimelineSampler(1024, 100_000), "unbounded tail");
    }

    // C5: worst-case final report of a 3600 s run stays far below the 8 MiB output cap (same row/record classes as the plugin).
    private static void ReportSize()
    {
        var t = new FifaTimelineSampler(FifaTimelineSampler.DefaultHeadRows, FifaTimelineSampler.DefaultTailRows);
        for (int i = 0; i < 3600 * 240 / 10; i++) t.Add(Row(i)); // even at 240 fps
        var s = new FifaViewSuspension();
        for (int i = 0; i < FifaViewSuspension.MaximumSuspensions; i++) { s.TryBegin(int.MaxValue - i, long.MaxValue - i, 5399.123456789012); s.Resume(int.MaxValue - i, long.MaxValue - i, 3, -5399.123456789012); }
        var flags = Enumerable.Range(0, 256).Select(i => new { RendererInstanceId = int.MinValue + i, BeforeEnabled = true, BeforeActiveInHierarchy = true, AfterEnabled = (bool?)true,
            AfterActiveInHierarchy = (bool?)false, AfterForceRenderingOff = (bool?)false, Status = "unchanged" }).ToArray();
        var ledger = new FifaNativeHideLedger();
        for (int i = 0; i < FifaNativeHideLedger.MaximumRenderers; i++) ledger.Record(int.MinValue + i, false, true, true);
        foreach (var e in ledger.Entries.ToArray()) { ledger.BeginHide(e.RendererInstanceId); ledger.ConfirmHide(e.RendererInstanceId, true); }
        foreach (var e in ledger.RestorePlan) { ledger.BeginRestore(e.RendererInstanceId); ledger.RecordRestoreReadback(e.RendererInstanceId, false); }
        // H164: + 500 discontinuity records and a full FailureDetail, + the stop row.
        var disc = new FifaTimeDiscontinuities();
        for (int i = 0; i < FifaTimeDiscontinuities.MaximumDiscontinuities; i++) disc.TryRecord(int.MaxValue - i, long.MaxValue - i, 5399.123456789012, -5399.123456789012, 12345.678901234567);
        var obs = new FifaFrameObservation(); obs.Begin(int.MaxValue, long.MaxValue, 5399.123456789012, new System.Numerics.Vector3(-12345.679f, -0.00012345679f, -12345.679f));
        obs.Stage = "pose-write"; obs.SetWallDt(0.016666666666666666); obs.SetPosition(new System.Numerics.Vector3(12345.679f, 0.00012345679f, 12345.679f)); obs.SetClock(-5399.123456789012);
        t.Add(new FifaTimelineRow { Kind = "stop", FrameCount = int.MaxValue, QpcTicks = long.MaxValue, MatchTimeSeconds = 5399.123456789012, Clip = "walk_F2" });
        Check(disc.Count == 500 && t.Rows[^1].Kind == "stop", "worst case includes 500 discontinuities and the stop row");
        var body = new { Timeline = t.Rows, TimelineRowsDropped = t.Dropped, Suspensions = s.Records, Discontinuities = disc.Records, FailureDetail = obs.Snapshot(), OriginalRendererFlags = flags, HiddenRenderers = ledger.Entries,
            Header = new string('x', 64 * 1024) }; // allowance for the fixed header/pack/thresholds/heuristics/cleanup handles (live-003 non-timeline part ~25 KB)
        long bytes = JsonSerializer.SerializeToUtf8Bytes(body, new JsonSerializerOptions { WriteIndented = true }).LongLength;
        Console.WriteLine($"PROBE: worst-case 3600 s FIFA overlay report body {bytes} bytes (cap {FifaOverlayReport.MaximumBytes}).");
        Check(bytes < FifaOverlayReport.MaximumBytes / 2, "worst-case 3600 s report below half the 8 MiB cap");
    }

    internal static string RepoRoot()
    {
        var d = new DirectoryInfo(AppContext.BaseDirectory);
        while (d != null && !File.Exists(Path.Combine(d.FullName, "FM26.VisualDiagnostics.csproj"))) d = d.Parent;
        return d?.FullName ?? throw new Exception("repo root not found");
    }
}
