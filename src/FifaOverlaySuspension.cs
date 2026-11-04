#nullable disable
namespace FM26.VisualDiagnostics;

// H162: pure suspend/resume state machine of the FIFA overlay (no Unity types). An FM view change (the exact H160
// fm-view-changed condition) SUSPENDS the owned overlay instead of ending it; FifaOverlay.cs delegates the bookkeeping
// and every suspended-frame decision here so the transitions are executable in PolicyTests.
public sealed class FifaSuspensionRecord
{
    public int Index { get; init; }
    public string Reason { get; init; }
    public int StartFrame { get; init; }
    public long StartQpcTicks { get; init; }
    public double? MatchTimeBefore { get; init; }
    public int? EndFrame { get; internal set; }
    public long? EndQpcTicks { get; internal set; }
    public double? RealSeconds { get; internal set; }
    public double? MatchTimeAfter { get; internal set; }
    public string End { get; internal set; }
}

public enum FifaStructureState { Intact, Ended, Unreadable }

public enum FifaSuspendedDecision { Stay, Resume, StopMatchEnded, StopHideReset, StopSuspendLimit, StopFailure }

public sealed class FifaViewSuspension
{
    public const int MaximumSuspensions = 200;
    public const double MaximumSuspendedSeconds = 120.0;
    public const string LimitReason = "suspend-limit", MatchEndedReason = "fm-match-ended", HideResetReason = "native-hide-reset", FailureReason = "owned-update-failure";
    private readonly List<FifaSuspensionRecord> _records = new();
    public bool Suspended { get; private set; }
    public int Count => _records.Count;
    public IReadOnlyList<FifaSuspensionRecord> Records => _records;

    // false = count bound reached (caller ends the run normally with suspend-limit); nothing recorded.
    public bool TryBegin(int frame, long qpc, double? matchTimeBefore)
    {
        if (Suspended) throw new InvalidOperationException("FIFA overlay already suspended.");
        if (_records.Count >= MaximumSuspensions) return false;
        _records.Add(new FifaSuspensionRecord { Index = _records.Count + 1, Reason = FifaOverlayPolicy.ViewChangedReason, StartFrame = frame, StartQpcTicks = qpc, MatchTimeBefore = matchTimeBefore });
        Suspended = true;
        return true;
    }

    public void Resume(int frame, long qpc, long frequency, double matchTimeAfter)
    {
        if (!Suspended) throw new InvalidOperationException("FIFA overlay resume without suspension.");
        Close(frame, qpc, frequency, "resumed").MatchTimeAfter = matchTimeAfter;
    }

    public void CloseOnStop(int frame, long qpc, long frequency, string reason)
    {
        if (Suspended) Close(frame, qpc, frequency, reason);
    }

    private FifaSuspensionRecord Close(int frame, long qpc, long frequency, string end)
    {
        var r = _records[^1];
        r.EndFrame = frame; r.EndQpcTicks = qpc; r.RealSeconds = frequency > 0 ? (double)(qpc - r.StartQpcTicks) / frequency : null; r.End = end;
        Suspended = false;
        return r;
    }

    // One suspended frame. Renderer tuple = (BeforeEnabled, BeforeActive, AfterEnabled, AfterActive, ExpectedForceOff, AfterForceOff);
    // null After* = dead/unreadable; forceRenderingOff pair is null in default runs (never read without HideNativePlayer).
    public static (FifaSuspendedDecision Decision, string Reason, string Error) Decide(bool structureIntact, bool actorPresent,
        IReadOnlyList<(bool BeforeEnabled, bool BeforeActive, bool? AfterEnabled, bool? AfterActive, bool? ExpectedForceOff, bool? AfterForceOff)> renderers, double suspendedSeconds)
        => Decide(structureIntact ? FifaStructureState.Intact : FifaStructureState.Ended, actorPresent, renderers, suspendedSeconds);

    // H171 (H169 suggestion 4): actor presence UNREADABLE (null) while suspended => failure, like an unreadable structure and like
    // HardIdentityLost (unreadable is not proof of departure); never fm-match-ended / actor-left.
    public static (FifaSuspendedDecision Decision, string Reason, string Error) Decide(FifaStructureState structure, bool? actorPresent,
        IReadOnlyList<(bool BeforeEnabled, bool BeforeActive, bool? AfterEnabled, bool? AfterActive, bool? ExpectedForceOff, bool? AfterForceOff)> renderers, double suspendedSeconds)
    {
        if (structure != FifaStructureState.Unreadable && !actorPresent.HasValue)
            return (FifaSuspendedDecision.StopFailure, FailureReason, "Selected actor presence unreadable while suspended.");
        return Decide(structure, actorPresent ?? false, renderers, suspendedSeconds);
    }

    // H164 (H163 suggestion 6): Unreadable = thread mismatch or a structure read exception => failure (never a match end).
    public static (FifaSuspendedDecision Decision, string Reason, string Error) Decide(FifaStructureState structure, bool actorPresent,
        IReadOnlyList<(bool BeforeEnabled, bool BeforeActive, bool? AfterEnabled, bool? AfterActive, bool? ExpectedForceOff, bool? AfterForceOff)> renderers, double suspendedSeconds)
    {
        static (FifaSuspendedDecision, string, string) Fail(string message) => (FifaSuspendedDecision.StopFailure, FailureReason, message);
        if (structure == FifaStructureState.Unreadable) return Fail("Match structure unreadable or wrong thread while suspended.");
        bool structureIntact = structure == FifaStructureState.Intact;
        bool enabledChanged = renderers != null && renderers.Any(r => r.AfterEnabled.HasValue && r.AfterEnabled.Value != r.BeforeEnabled);
        if (!structureIntact || !actorPresent)
            return enabledChanged ? Fail("Target renderer enabled flag changed while the match structure ended.") : (FifaSuspendedDecision.StopMatchEnded, MatchEndedReason, null);
        if (renderers == null || renderers.Count == 0) return Fail("Target renderer inventory empty while suspended.");
        if (renderers.Any(r => !r.AfterEnabled.HasValue || !r.AfterActive.HasValue)) return Fail("Target renderer unreadable while suspended.");
        if (enabledChanged) return Fail("Target renderer enabled flag changed while suspended.");
        foreach (var r in renderers)
        {
            if (!r.ExpectedForceOff.HasValue) continue;
            if (!r.AfterForceOff.HasValue) return Fail("forceRenderingOff unreadable while suspended.");
            if (r.AfterForceOff.Value != r.ExpectedForceOff.Value) return (FifaSuspendedDecision.StopHideReset, HideResetReason, null);
        }
        if (!double.IsFinite(suspendedSeconds) || suspendedSeconds < 0) return Fail("Suspended real duration nonfinite or negative.");
        if (suspendedSeconds >= MaximumSuspendedSeconds) return (FifaSuspendedDecision.StopSuspendLimit, LimitReason, null);
        if (renderers.All(r => r.AfterActive.Value == r.BeforeActive)) return (FifaSuspendedDecision.Resume, null, null);
        if (renderers.All(r => !r.AfterActive.Value)) return (FifaSuspendedDecision.Stay, null, null);
        return Fail("Target renderers partially reactivated while suspended.");
    }
}

// H162: one timeline row (same fields as the 0.4.0 anonymous row) as a plain CLR class so the bounded report size is testable.
public sealed class FifaRowVector
{
    public float X { get; init; }
    public float Y { get; init; }
    public float Z { get; init; }
}

public sealed class FifaTimelineRow
{
    // H164: "sample" (1 pose update in 10), "discontinuity" (FM clock jump frame) or "stop" (exact stop frame, always last).
    public string Kind { get; init; }
    public int FrameCount { get; init; }
    public long QpcTicks { get; init; }
    // H164: null only in a stop row whose frame stopped before that value was read (never invented).
    public double? WallDt { get; init; }
    public double? MatchTimeSeconds { get; init; }
    public double? MatchDt { get; init; }
    public double MatchSpeedMetersPerSecond { get; init; }
    public double MatchSpeedWindowSeconds { get; init; }
    public double? FrameDisplacementMeters { get; init; }
    public bool Paused { get; init; }
    public float? PublishedPlaybackSpeed { get; init; }
    public bool PublishedPlaybackSpeedNonFinite { get; init; }
    public string Clip { get; init; }
    public double Rate { get; init; }
    public double PhaseKeys { get; init; }
    public int PackFrame { get; init; }
    public double YawDegrees { get; init; }
    public FifaRowVector RootPosition { get; init; }
    public FifaRowVector RootForward { get; init; }
    // H171 (pack v2 only, null otherwise): crossfade source clip/frame/weight-of-target and the selected tier/direction.
    public string BlendClip { get; init; }
    public int? BlendPackFrame { get; init; }
    public double? BlendWeight { get; init; }
    public string Tier { get; init; }
    public string Direction { get; init; }
    public bool? Fallback { get; init; }
}

// H162: whole-match timeline bound: the first HeadRows rows, then a ring of the LAST TailRows rows; exact dropped count.
public sealed class FifaTimelineSampler
{
    public const int DefaultHeadRows = 1024, DefaultTailRows = 1024, MaximumRowsPerPart = 4096;
    private readonly int _head, _tail;
    private readonly List<FifaTimelineRow> _first = new();
    private readonly Queue<FifaTimelineRow> _last = new();
    public long Total { get; private set; }
    public long Dropped { get; private set; }

    public FifaTimelineSampler(int headRows, int tailRows)
    {
        if (headRows < 1 || tailRows < 1 || headRows > MaximumRowsPerPart || tailRows > MaximumRowsPerPart) throw new InvalidOperationException("Timeline bounds out of range.");
        _head = headRows; _tail = tailRows;
    }

    public void Add(FifaTimelineRow row)
    {
        Total++;
        if (_first.Count < _head) { _first.Add(row); return; }
        _last.Enqueue(row);
        if (_last.Count > _tail) { _last.Dequeue(); Dropped++; }
    }

    public IReadOnlyList<FifaTimelineRow> Rows => _first.Concat(_last).ToArray();
}

// H164 (0.5.1): per-frame match-clock step. Live-004 (0.5.0) ended on a finite clock discontinuity with the target
// renderers still active (user: right after a goal, i.e. FM's goal replay re-plays the past with players visible).
public enum FifaClockStep { Normal, Paused, Backward, Forward, NonFinite }

public sealed class FifaDiscontinuityRecord
{
    public int Index { get; init; }
    public int Frame { get; init; }
    public long QpcTicks { get; init; }
    public double MatchTimeBefore { get; init; }
    public double MatchTimeAfter { get; init; }
    public double StepSeconds { get; init; }
    public string Direction { get; init; }
    public double DisplacementMeters { get; init; }
}

// H164: bounded list of FM time discontinuities (replay start/end, dead-time skip) accepted on the active path after the
// full context/owned/flag guards; the caller re-baselines match motion on that frame. Count bound => normal stop.
public sealed class FifaTimeDiscontinuities
{
    public const int MaximumDiscontinuities = 500;
    public const string LimitReason = "discontinuity-limit";
    private readonly List<FifaDiscontinuityRecord> _records = new();
    public int Count => _records.Count;
    public IReadOnlyList<FifaDiscontinuityRecord> Records => _records;

    // Backward = ANY negative step (small jitter cannot be told apart from a replay start; declared). Forward = step > 4 s.
    public static FifaClockStep Classify(double previous, double current)
    {
        double dt = current - previous;
        if (!double.IsFinite(previous) || !double.IsFinite(current) || !double.IsFinite(dt)) return FifaClockStep.NonFinite;
        if (dt < 0) return FifaClockStep.Backward;
        if (dt == 0) return FifaClockStep.Paused;
        return dt > FifaOverlayPolicy.MaximumMatchStepSeconds ? FifaClockStep.Forward : FifaClockStep.Normal;
    }

    // H200: Backward/Forward steps, or a FROZEN clock while FM moved some active root further than the per-frame bound
    // (key-moment highlights reposition players for the next highlight with the clock paused).
    public static bool IsDiscontinuity(FifaClockStep step, double maxDisplacementMeters) => step == FifaClockStep.Backward || step == FifaClockStep.Forward
        || (step == FifaClockStep.Paused && maxDisplacementMeters > FifaOverlayPolicy.MaximumFrameDisplacementMeters);

    // H203: plus, inside the post-resume settle window, a RUNNING clock while FM moved some active root further than the
    // per-frame bound (key-moment players settled for the resumed view; live-012 5.09 m at dt 0.039).
    public static bool IsDiscontinuity(FifaClockStep step, double maxDisplacementMeters, bool settling) => IsDiscontinuity(step, maxDisplacementMeters)
        || (settling && step == FifaClockStep.Normal && maxDisplacementMeters > FifaOverlayPolicy.MaximumFrameDisplacementMeters);

    public bool TryRecord(int frame, long qpc, double before, double after, double displacementMeters) => TryRecord(frame, qpc, before, after, displacementMeters, false);

    // false = count bound reached (caller ends the run normally with discontinuity-limit); nothing recorded.
    // H203: settling = the frame lies in the post-resume settle window (FifaResumeSettle).
    public bool TryRecord(int frame, long qpc, double before, double after, double displacementMeters, bool settling)
    {
        var step = Classify(before, after);
        if (!IsDiscontinuity(step, displacementMeters, settling)) throw new InvalidOperationException("Not a finite match-clock discontinuity.");
        if (!double.IsFinite(displacementMeters) || displacementMeters < 0) throw new InvalidOperationException("Discontinuity displacement nonfinite.");
        if (_records.Count >= MaximumDiscontinuities) return false;
        _records.Add(new FifaDiscontinuityRecord { Index = _records.Count + 1, Frame = frame, QpcTicks = qpc, MatchTimeBefore = before, MatchTimeAfter = after,
            StepSeconds = after - before, Direction = step == FifaClockStep.Backward ? "backward" : step == FifaClockStep.Forward ? "forward" : step == FifaClockStep.Paused ? "paused-reposition" : "resume-reposition", DisplacementMeters = displacementMeters });
        return true;
    }
}

// H203 (0.10.2): post-resume settle window. In key-moment presentation FM repositions players on the first frame after a
// view-change resume, with the clock frozen (live-011: resume 24070, jump 24071) or running (live-012: resume 7783, jump
// 7784, dt 0.039, 5.09 m). Both live cases are at +1 frame; one frame may legitimately take up to the 1 s wall-stall bound
// (FifaOverlayPolicy.MaximumStepSeconds) before the stall guard fires, so a 1.0 s real (QPC) window always contains the
// first post-resume frame while staying far shorter than any ordinary run of play. Host-level: the last resume opens it.
public sealed class FifaResumeSettle
{
    public const double SettleSeconds = 1.0;
    private long? _resumeQpc;
    public void MarkResume(long qpc) => _resumeQpc = qpc;
    public bool InWindow(long now, long frequency) => _resumeQpc.HasValue && now - _resumeQpc.Value <= SettleSeconds * frequency;
}

// H164: what the overlay read on the CURRENT frame and how far it got (Stage); snapshot = report FailureDetail and stop row.
// Nonfinite values are kept as null + NonFiniteObserved so the report can always be serialized.
public sealed class FifaFailureDetail
{
    public int Frame { get; init; }
    public long QpcTicks { get; init; }
    public string Stage { get; init; }
    public double? WallDt { get; init; }
    public double? PreviousMatchTime { get; init; }
    public double? MatchTime { get; init; }
    public double? MatchDt { get; init; }
    public string ClockStep { get; init; }
    public FifaRowVector PreviousRootPosition { get; init; }
    public FifaRowVector RootPosition { get; init; }
    public double? DisplacementMeters { get; init; }
    public bool NonFiniteObserved { get; init; }
    // H209: refusing actor-part context predicate of the failing actor on the stop frame (null = none refused).
    public string ActorPredicate { get; init; }
}

public sealed class FifaFrameObservation
{
    private bool _begun, _nonFinite;
    private int _frame; private long _qpc;
    private double? _wallDt, _previousTime, _time; private string _step;
    private System.Numerics.Vector3? _previousPosition, _position;
    public string Stage { get; set; }
    // H209: this frame's refusing actor-part context predicate (set by FifaActorContext.Require, copied into FailureDetail).
    public string ActorPredicate { get; set; }
    public int Frame => _begun ? _frame : -1;

    public void Begin(int frame, long qpc, double? previousMatchTime, System.Numerics.Vector3? previousPosition)
    {
        _begun = true; _frame = frame; _qpc = qpc; Stage = null; ActorPredicate = null; _nonFinite = false; _wallDt = null; _time = null; _step = null; _position = null;
        _previousTime = previousMatchTime.HasValue && double.IsFinite(previousMatchTime.Value) ? previousMatchTime : null;
        _previousPosition = previousPosition;
    }

    public void SetWallDt(double dt) { if (double.IsFinite(dt)) _wallDt = dt; else _nonFinite = true; }
    public void SetPosition(System.Numerics.Vector3 p)
    {
        if (float.IsFinite(p.X) && float.IsFinite(p.Y) && float.IsFinite(p.Z)) _position = p; else { _position = null; _nonFinite = true; }
    }
    public void SetClock(double matchTime)
    {
        _step = (_previousTime.HasValue ? FifaTimeDiscontinuities.Classify(_previousTime.Value, matchTime) : double.IsFinite(matchTime) ? FifaClockStep.Normal : FifaClockStep.NonFinite).ToString();
        if (double.IsFinite(matchTime)) _time = matchTime; else { _time = null; _nonFinite = true; }
    }

    public FifaFailureDetail Snapshot()
    {
        if (!_begun) return null;
        static FifaRowVector V(System.Numerics.Vector3? v) => v.HasValue ? new FifaRowVector { X = v.Value.X, Y = v.Value.Y, Z = v.Value.Z } : null;
        double? dt = _time.HasValue && _previousTime.HasValue ? _time - _previousTime : null;
        double? displacement = null;
        if (_position.HasValue && _previousPosition.HasValue)
        {
            double dx = (double)_position.Value.X - _previousPosition.Value.X, dz = (double)_position.Value.Z - _previousPosition.Value.Z;
            double d = Math.Sqrt(dx * dx + dz * dz); if (double.IsFinite(d)) displacement = d;
        }
        return new FifaFailureDetail { Frame = _frame, QpcTicks = _qpc, Stage = Stage, WallDt = _wallDt, PreviousMatchTime = _previousTime, MatchTime = _time,
            MatchDt = dt.HasValue && double.IsFinite(dt.Value) ? dt : null, ClockStep = _step, PreviousRootPosition = V(_previousPosition), RootPosition = V(_position),
            DisplacementMeters = displacement, NonFiniteObserved = _nonFinite, ActorPredicate = ActorPredicate };
    }
}
