#nullable disable
namespace FM26.VisualDiagnostics;

// H166 (0.6.0): pure parts of the multi-actor FIFA overlay (no Unity types; executable in PolicyTests).

// Actor set of a start request, resolved against the CURRENT plugin inventory (same Eligible flag as the legacy path).
// Legacy single form: exactly the one requested eligible actor (fixed palette, team not required).
// Multi: "all-eligible" = every eligible actor in inventory order, or the explicit list in request order; every selected
// actor must be eligible, present exactly once and have a declared team palette (TeamId 0/1). 1..24 actors, never truncated.
public static class FifaActorSelection
{
    // H178a: FIFA-overlay eligibility used by the multi selection: the legacy requirements except the goalkeeper refusal,
    // plus a declared team palette (TeamId 0/1).
    public static bool FifaEligible(int shirtNumber, bool active, bool substitute, bool goalkeeper, bool medic, int playerId, int anchorCount, int teamId)
        => VisualPolicy.HasValidShirtNumber(shirtNumber) && VisualPolicy.IsEligible(active, substitute, false, medic, playerId, anchorCount)
            && FifaTeamPalettes.Declared(teamId);

    // H178a: per-frame role revalidation (start role vs current FM IsGoalkeeper): any role change is refused.
    public static bool RoleRefused(bool selectedGoalkeeper, bool currentGoalkeeper) => currentGoalkeeper != selectedGoalkeeper;

    // H178a: per-actor admissibility of a selected actor (selection, Start, per-frame context): multi = FIFA eligibility
    // (goalkeepers allowed), legacy N=1 = the legacy outfield-only Eligible flag.
    public static bool Admissible(bool multi, bool eligible, bool fifaEligible) => multi ? fifaEligible : eligible;

    public static int[] Resolve(FifaOverlayRequest request, IReadOnlyList<(int ActorInstanceId, int TeamId, bool Eligible)> players) =>
        ResolveFifa(request, players?.Select(p => (p.ActorInstanceId, p.TeamId, p.Eligible, p.Eligible, false)).ToArray());

    // H178a: same contract with the FIFA eligibility and role of each inventory actor (host input).
    public static int[] ResolveFifa(FifaOverlayRequest request, IReadOnlyList<(int ActorInstanceId, int TeamId, bool Eligible, bool FifaEligible, bool Goalkeeper)> players)
    {
        if (request == null || players == null) throw new ArgumentException("Selection inputs unavailable.");
        var byId = new Dictionary<int, (int ActorInstanceId, int TeamId, bool Eligible, bool FifaEligible, bool Goalkeeper)>();
        foreach (var p in players)
            if (!byId.TryAdd(p.ActorInstanceId, p)) throw new InvalidOperationException("Duplicate actor instance id in the inventory.");
        int[] ids = !request.Multi ? new[] { request.ActorInstanceId }
            : request.AllEligible ? players.Where(p => p.FifaEligible || p.Eligible).Select(p => p.ActorInstanceId).ToArray()
            : request.ActorInstanceIds;
        if (ids == null || ids.Length < 1 || ids.Length > FifaOverlayPolicy.MaximumActors)
            throw new InvalidOperationException($"FIFA overlay needs 1..{FifaOverlayPolicy.MaximumActors} eligible actors (found {ids?.Length ?? 0}).");
        foreach (var id in ids)
        {
            if (!byId.TryGetValue(id, out var p) || !Admissible(request.Multi, p.Eligible, p.FifaEligible || p.Eligible)) throw new InvalidOperationException("Target is not exactly one current eligible outfield actor.");
            if (request.Multi && !FifaTeamPalettes.Declared(p.TeamId)) throw new InvalidOperationException($"No declared team palette for TeamId {p.TeamId}.");
        }
        // H178a: FM fields one goalkeeper per team; two keeper-flagged selected actors in one team = inconsistent inventory.
        if (ids.Where(id => byId[id].Goalkeeper).GroupBy(id => byId[id].TeamId).Any(team => team.Count() > 1))
            throw new InvalidOperationException("At most one goalkeeper per team in a FIFA overlay selection.");
        return ids.ToArray();
    }
}

// H166 (3): two FIXED declared team palettes by FM TeamId (FM kit colours are a later step). Legacy single form keeps
// FifaOverlayPolicy.Palette unchanged.
public static class FifaTeamPalettes
{
    public static readonly int[] Teams = { 0, 1 };
    private static IReadOnlyDictionary<string, float[]> Build(float[] shirt, float[] shorts, float[] socks) => new Dictionary<string, float[]>
    {
        ["skin"] = FifaOverlayPolicy.Palette["skin"], ["head"] = FifaOverlayPolicy.Palette["head"], ["shoes"] = FifaOverlayPolicy.Palette["shoes"],
        ["shirt"] = shirt, ["shorts"] = shorts, ["socks"] = socks,
    };
    // team 0: blue shirt / white shorts / white socks; team 1: red shirt / black shorts / black socks (linear RGBA 0..1).
    private static readonly IReadOnlyDictionary<int, IReadOnlyDictionary<string, float[]>> ByTeam = new Dictionary<int, IReadOnlyDictionary<string, float[]>>
    {
        [0] = Build(new[] { 0.10f, 0.30f, 0.78f, 1f }, new[] { 0.95f, 0.95f, 0.95f, 1f }, new[] { 0.95f, 0.95f, 0.95f, 1f }),
        [1] = Build(new[] { 0.80f, 0.10f, 0.10f, 1f }, new[] { 0.05f, 0.05f, 0.05f, 1f }, new[] { 0.05f, 0.05f, 0.05f, 1f }),
    };
    public static IReadOnlyDictionary<int, IReadOnlyDictionary<string, float[]>> All => ByTeam;
    public static bool Declared(int teamId) => ByTeam.ContainsKey(teamId);
    public static IReadOnlyDictionary<string, float[]> For(int teamId) =>
        ByTeam.TryGetValue(teamId, out var palette) ? palette : throw new InvalidOperationException($"No declared team palette for TeamId {teamId}.");
    public static IReadOnlyDictionary<string, float[]> Resolve(bool multi, int teamId) => multi ? For(teamId) : FifaOverlayPolicy.Palette;
    // H178a: goalkeeper palettes by TeamId: team 0 amber shirt/socks, team 1 green shirt/socks, both near-black shorts.
    private static readonly IReadOnlyDictionary<int, IReadOnlyDictionary<string, float[]>> ByGoalkeeper = new Dictionary<int, IReadOnlyDictionary<string, float[]>>
    {
        [0] = Build(new[] { 0.85f, 0.65f, 0.05f, 1f }, new[] { 0.08f, 0.08f, 0.08f, 1f }, new[] { 0.85f, 0.65f, 0.05f, 1f }),
        [1] = Build(new[] { 0.15f, 0.70f, 0.35f, 1f }, new[] { 0.08f, 0.08f, 0.08f, 1f }, new[] { 0.15f, 0.70f, 0.35f, 1f }),
    };
    // H178a: shared-material set key of an actor (team + role) and its palette: multi goalkeepers use key TeamId + 2.
    public static int Key(bool multi, int teamId, bool goalkeeper) => multi && goalkeeper ? teamId + 2 : teamId;
    public static IReadOnlyDictionary<string, float[]> ForKey(bool multi, int key) => multi && key >= 2 ? ByGoalkeeper[key - 2] : Resolve(multi, key);
}

// H166 (2): owned Unity handle budget, checked BEFORE any allocation. Per actor: root GameObject, root Transform, Mesh,
// MeshFilter, MeshRenderer (meshes are per actor: CPU-uploaded vertices differ). Materials shared: one per pack piece PER TEAM used.
public static class FifaHandleBudget
{
    // H178a: materials are shared per team+role key (FifaTeamPalettes.Key): at most 4 palettes (2 teams x outfield/goalkeeper).
    public const int PerActor = 5, Pieces = 6, MaximumTeams = 2, MaximumPalettes = 4;
    public const int Maximum = FifaOverlayPolicy.MaximumActors * PerActor + Pieces * MaximumPalettes;
    public static int Required(int actors, int pieces, int teams) => actors * PerActor + pieces * teams;
    public static void Require(int actors, int pieces, int teams)
    {
        if (actors < 1 || actors > FifaOverlayPolicy.MaximumActors || pieces != Pieces || teams < 1 || teams > MaximumPalettes || Required(actors, pieces, teams) > Maximum)
            throw new InvalidOperationException("Owned handle budget exceeded or undeclared shape.");
    }
    public static int TeamsUsed(IEnumerable<int> teamIds) => teamIds.Distinct().Count();
}

// H166 (4): per-actor lifecycle scope. Precedence on an active-path refusal: (1) deadline => session stop-time-budget;
// (2) the exact H160 fm-view-changed condition on THIS actor's renderers => suspend this actor; (3) multi only: a guard
// refusal with the match structure intact and a HARD identity loss of this actor (destroyed, other Transform, PlayerId or
// shirt changed, substitute/medic) => end this actor only (actor-left; its own restore + later-frame verification);
// (4) everything else (our exception, readback mismatch, teleport, partial renderers, structure change) => stop the whole
// session (conservative: one failing actor means our model of the scene is wrong for all of them).
// H168 (S1): sessionRefusal = the frame's RequireSession refusal message (null = session intact). A session refusal is never
// an individual departure: never EndActor (so never actor-left nor, summed over actors, all-actors-ended). It keeps the 0.5.1
// classification (exact fm-view-changed => suspend; otherwise a whole-session failure stop).
public enum FifaScopeKind { Stay, Resume, SuspendActor, EndActor, StopSession }

public static class FifaActorScope
{
    public const string ActorLeftReason = "actor-left", AllActorsEndedReason = "all-actors-ended";

    public static (FifaScopeKind Kind, string Reason, string Error) OnActiveFailure(bool multi, string sessionRefusal, bool timeout, bool guardRefusal, bool structureIntact,
        IReadOnlyList<(bool BeforeEnabled, bool BeforeActive, bool? AfterEnabled, bool? AfterActive)> renderers, bool hardIdentityLost, string error)
    {
        var stop = FifaOverlayPolicy.ClassifyStop(timeout, guardRefusal, structureIntact, renderers, error);
        if (timeout) return (FifaScopeKind.StopSession, stop.Reason, stop.Error);
        if (stop.Reason == FifaOverlayPolicy.ViewChangedReason) return (FifaScopeKind.SuspendActor, stop.Reason, null);
        if (multi && sessionRefusal == null && guardRefusal && structureIntact && hardIdentityLost) return (FifaScopeKind.EndActor, ActorLeftReason, null);
        return (FifaScopeKind.StopSession, stop.Reason, stop.Error);
    }

    public static (FifaScopeKind Kind, string Reason, string Error) OnSuspended(bool multi, string sessionRefusal, FifaStructureState structure, (FifaSuspendedDecision Decision, string Reason, string Error) d)
    {
        switch (d.Decision)
        {
            case FifaSuspendedDecision.Stay: return (FifaScopeKind.Stay, null, null);
            case FifaSuspendedDecision.Resume: return (FifaScopeKind.Resume, null, null);
            case FifaSuspendedDecision.StopMatchEnded:
                // H171 (H169 suggestion 1): Structure()==Ended always carries its own RequireSession refusal (session predicates include
                // the structure ones): a real match end is fm-match-ended; a refusal with the structure intact/unreadable stays a failure.
                if (multi && sessionRefusal != null && structure != FifaStructureState.Ended) return SessionFailure(sessionRefusal);
                return multi && structure == FifaStructureState.Intact ? (FifaScopeKind.EndActor, ActorLeftReason, null) : (FifaScopeKind.StopSession, d.Reason, d.Error);
            case FifaSuspendedDecision.StopSuspendLimit:
                if (multi && sessionRefusal != null) return SessionFailure(sessionRefusal);
                return multi ? (FifaScopeKind.EndActor, d.Reason, null) : (FifaScopeKind.StopSession, d.Reason, d.Error);
            default: return (FifaScopeKind.StopSession, d.Reason, d.Error);
        }
    }

    // H168 (S1): multi end-type decision under a session refusal => the whole session stops as a failure (legacy N=1 keeps the
    // 0.5.1 suspended decisions, which never read the session part).
    private static (FifaScopeKind Kind, string Reason, string Error) SessionFailure(string sessionRefusal) =>
        (FifaScopeKind.StopSession, FifaViewSuspension.FailureReason, sessionRefusal);

    // H171 (H169 suggestion 2): under a session refusal the multi count bound is a session failure (never EndActor, so never
    // all-actors-ended by accumulation); legacy N=1 unchanged.
    public static (FifaScopeKind Kind, string Reason, string Error) OnSuspendLimitCount(bool multi, string sessionRefusal) =>
        multi && sessionRefusal != null ? SessionFailure(sessionRefusal)
        : (multi ? FifaScopeKind.EndActor : FifaScopeKind.StopSession, FifaViewSuspension.LimitReason, null);

    // H178a: a role change (start role vs current FM IsGoalkeeper, e.g. an outfielder sent in goal after a keeper red card) is a
    // HARD identity loss: the actor's eligibility and palette were bound to its start role (multi => actor-left only).
    public static bool RoleIdentityLost(bool selectedGoalkeeper, bool currentGoalkeeper) => selectedGoalkeeper != currentGoalkeeper;

    public static string AllEnded(bool multi, int running) => multi && running == 0 ? AllActorsEndedReason : null;
}

// H166 (4): GLOBAL view-change record (one FM cut hides everybody): opens when every running actor is suspended, closes
// on the first resume (or on stop). Informative only; decisions stay per actor. Bounded like the per-actor list.
public sealed class FifaGlobalSuspension
{
    private readonly List<FifaSuspensionRecord> _records = new();
    public bool Suspended { get; private set; }
    public int Count => _records.Count;
    public int Dropped { get; private set; }
    public IReadOnlyList<FifaSuspensionRecord> Records => _records;

    public void Observe(int frame, long qpc, long frequency, int running, int suspended, double? matchTime)
    {
        if (running < 0 || suspended < 0 || suspended > running) throw new InvalidOperationException("Global suspension counts inconsistent.");
        bool all = running > 0 && suspended == running;
        if (all && !Suspended)
        {
            if (_records.Count >= FifaViewSuspension.MaximumSuspensions) { Dropped++; Suspended = true; return; }
            _records.Add(new FifaSuspensionRecord { Index = _records.Count + 1, Reason = FifaOverlayPolicy.ViewChangedReason, StartFrame = frame, StartQpcTicks = qpc, MatchTimeBefore = matchTime });
            Suspended = true;
        }
        else if (!all && Suspended) Close(frame, qpc, frequency, "resumed", matchTime);
    }

    public void CloseOnStop(int frame, long qpc, long frequency, string reason) { if (Suspended) Close(frame, qpc, frequency, reason, null); }

    private void Close(int frame, long qpc, long frequency, string end, double? matchTime)
    {
        Suspended = false;
        var r = _records.Count > 0 && _records[^1].End == null ? _records[^1] : null; // null = the dropped (unrecorded) episode
        if (r == null) return;
        r.EndFrame = frame; r.EndQpcTicks = qpc; r.RealSeconds = frequency > 0 ? (double)(qpc - r.StartQpcTicks) / frequency : null; r.End = end; r.MatchTimeAfter = matchTime;
    }
}

// H166 (5): per-frame overlay work cost (managed Stopwatch around one host Tick, all actors). Fixed 0.01 ms histogram up to
// 100 ms (+ exact Max beyond), nearest-rank percentiles reported as the bucket upper bound (<= Max). Declared guard: more
// than BudgetMilliseconds on ConsecutiveFrames consecutive measured frames => normal stop "perf-budget". No per-frame allocation.
public sealed class FifaFrameCostStats
{
    public long Frames { get; init; }
    public double? P50Milliseconds { get; init; }
    public double? P95Milliseconds { get; init; }
    public double? MaxMilliseconds { get; init; }
    public long OverBudgetFrames { get; init; }
    public int MaxConsecutiveOverBudget { get; init; }
    public double BudgetMilliseconds { get; init; }
    public int ConsecutiveFrames { get; init; }
    public double ResolutionMilliseconds { get; init; }
    // H171: frames with at least one actor blending two clips, and the blended actor-frames (pack v2 crossfades).
    public long BlendFrames { get; init; }
    public long BlendActorFrames { get; init; }
}

public sealed class FifaFrameCost
{
    public const double BudgetMilliseconds = 8.0, ResolutionMilliseconds = 0.01, HistogramMilliseconds = 100.0;
    public const int ConsecutiveFrames = 60;
    public const string Reason = "perf-budget";
    private readonly long[] _buckets = new long[(int)(HistogramMilliseconds / ResolutionMilliseconds) + 1]; // last = overflow
    private long _frames, _over, _blendFrames, _blendActorFrames; private int _run, _maxRun; private double _max;

    // H171: same guard, plus the number of actors that blended two clips on this frame.
    public bool Add(double milliseconds, int blendedActors)
    {
        if (blendedActors < 0) throw new InvalidOperationException("Blended actor count negative.");
        bool tripped = Add(milliseconds);
        if (blendedActors > 0) { _blendFrames++; _blendActorFrames += blendedActors; }
        return tripped;
    }

    // true = the guard tripped on this frame (caller stops the session normally with Reason).
    public bool Add(double milliseconds)
    {
        if (!double.IsFinite(milliseconds) || milliseconds < 0) throw new InvalidOperationException("Frame cost nonfinite or negative.");
        _frames++; if (milliseconds > _max) _max = milliseconds;
        int bucket = milliseconds >= HistogramMilliseconds ? _buckets.Length - 1 : Math.Min(_buckets.Length - 2, (int)Math.Floor(milliseconds / ResolutionMilliseconds));
        _buckets[bucket]++;
        if (milliseconds > BudgetMilliseconds) { _over++; _run++; if (_run > _maxRun) _maxRun = _run; } else _run = 0;
        return _run >= ConsecutiveFrames;
    }

    private double? Percentile(double p)
    {
        if (_frames == 0) return null;
        long rank = Math.Max(1, (long)Math.Ceiling(p * _frames)), seen = 0;
        for (int i = 0; i < _buckets.Length; i++)
        {
            seen += _buckets[i];
            if (seen >= rank) return i == _buckets.Length - 1 ? _max : Math.Min(_max, (i + 1) * ResolutionMilliseconds);
        }
        return _max;
    }

    public FifaFrameCostStats Stats() => new()
    {
        Frames = _frames, P50Milliseconds = Percentile(0.50), P95Milliseconds = Percentile(0.95), MaxMilliseconds = _frames == 0 ? null : _max,
        OverBudgetFrames = _over, MaxConsecutiveOverBudget = _maxRun, BudgetMilliseconds = BudgetMilliseconds, ConsecutiveFrames = ConsecutiveFrames,
        ResolutionMilliseconds = ResolutionMilliseconds, BlendFrames = _blendFrames, BlendActorFrames = _blendActorFrames,
    };
}

// H166 (5): pack frames are pre-baked vertex positions (x,y,z float triples, axis already applied at load): one frame is
// copied with ONE block copy into a reused per-actor buffer. T must be a 3-float struct (UnityEngine.Vector3 in the plugin,
// System.Numerics.Vector3 in tests). Zero allocation.
public static class FifaVertexCopy
{
    public static void CopyFrame<T>(float[] source, int frame, int vertexCount, Span<T> destination) where T : unmanaged
    {
        if (System.Runtime.CompilerServices.Unsafe.SizeOf<T>() != 12) throw new ArgumentException("Vertex element must be three floats.");
        if (source == null || vertexCount < 1 || destination.Length != vertexCount || frame < 0 || (long)(frame + 1) * vertexCount * 3 > source.Length)
            throw new ArgumentException("Vertex frame copy out of range.");
        System.Runtime.InteropServices.MemoryMarshal.Cast<float, T>(new ReadOnlySpan<float>(source, frame * vertexCount * 3, vertexCount * 3)).CopyTo(destination);
    }

    // H171: phase-normalised two-clip crossfade = per-vertex lerp of two pre-baked frames, source*(1-w) + target*w, written into
    // the same reused per-actor buffer. Zero allocation.
    public static void BlendFrames<T>(float[] source, int sourceFrame, float[] target, int targetFrame, double weight, int vertexCount, Span<T> destination) where T : unmanaged
    {
        if (System.Runtime.CompilerServices.Unsafe.SizeOf<T>() != 12) throw new ArgumentException("Vertex element must be three floats.");
        if (!double.IsFinite(weight) || weight < 0 || weight > 1) throw new ArgumentException("Blend weight outside [0,1].");
        if (source == null || target == null || vertexCount < 1 || destination.Length != vertexCount || sourceFrame < 0 || targetFrame < 0
            || (long)(sourceFrame + 1) * vertexCount * 3 > source.Length || (long)(targetFrame + 1) * vertexCount * 3 > target.Length)
            throw new ArgumentException("Vertex frame blend out of range.");
        var output = System.Runtime.InteropServices.MemoryMarshal.Cast<T, float>(destination);
        var a = new ReadOnlySpan<float>(source, sourceFrame * vertexCount * 3, vertexCount * 3);
        var b = new ReadOnlySpan<float>(target, targetFrame * vertexCount * 3, vertexCount * 3);
        float w = (float)weight, v = 1f - w;
        for (int i = 0; i < output.Length; i++) output[i] = a[i] * v + b[i] * w;
    }
}

// H166 (6): report bounds. Legacy N=1 keeps the 0.5.1 shape/caps; multi uses per-actor sections with a smaller timeline.
public static class FifaReportBudget
{
    public const int ActorHeadRows = 8, ActorTailRows = 8, MaximumRenderersPerActor = 64;
    public const int CleanupErrorChars = 256, DisplayNameChars = 64;
    public static string CleanupText(string value) => value == null || value.Length <= CleanupErrorChars ? value : value.Substring(0, CleanupErrorChars);
    public static string NameText(string value) => value == null || value.Length <= DisplayNameChars ? value : value.Substring(0, DisplayNameChars);
    public static int HeadRows(bool multi) => multi ? ActorHeadRows : FifaTimelineSampler.DefaultHeadRows;
    public static int TailRows(bool multi) => multi ? ActorTailRows : FifaTimelineSampler.DefaultTailRows;
    public static int RendererBound(bool multi) => multi ? MaximumRenderersPerActor : FifaNativeHideLedger.MaximumRenderers;
}

public sealed class FifaRendererFlagReport
{
    public int RendererInstanceId { get; init; }
    public bool BeforeEnabled { get; init; }
    public bool BeforeActiveInHierarchy { get; init; }
    public bool? AfterEnabled { get; init; }
    public bool? AfterActiveInHierarchy { get; init; }
    public bool? AfterForceRenderingOff { get; init; }
    public string Status { get; init; }
}

// Per-actor end (actor-left / per-actor suspend-limit): its own restore + strictly-later-frame verification.
public sealed class FifaActorCleanup
{
    public int RequestedFrame { get; init; }
    public int VerificationFrame { get; init; }
    public long VerificationQpcTicks { get; init; }
    public bool Verified { get; init; }
    public int AliveCount { get; init; }
    public int UnknownCount { get; init; }
    public bool NativeRestorationVerified { get; init; }
    public string[] Errors { get; init; }
}

// H178a: role label of a multi actor (final report Actors[].Role; the legacy N=1 report has no Actors[] section).
public static class FifaActorRole
{
    public static string Of(bool goalkeeper) => goalkeeper ? "goalkeeper" : "outfield";
}

public sealed class FifaActorReport
{
    public int ActorInstanceId { get; init; }
    public int PlayerId { get; init; }
    public string Name { get; init; }
    public int TeamId { get; init; }
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public string Role { get; init; } // H178a
    public int ShirtNumber { get; init; }
    public string State { get; init; }
    public string EndReason { get; init; }
    public string EndError { get; init; }
    public int? EndFrame { get; init; }
    public bool Shown { get; init; }
    public long PoseUpdates { get; init; }
    public long MeshUploads { get; init; }
    public IReadOnlyDictionary<string, long> ClipUpdates { get; init; }
    public double FmHeadToFeetAnchorDistance { get; init; }
    public double UniformScale { get; init; }
    public int SuspensionCount { get; init; }
    public IReadOnlyList<FifaSuspensionRecord> Suspensions { get; init; }
    public long TimelineRowsTotal { get; init; }
    public long TimelineRowsDropped { get; init; }
    public IReadOnlyList<FifaTimelineRow> Timeline { get; init; }
    public FifaFailureDetail FailureDetail { get; init; }
    public FifaRendererFlagReport[] OriginalRendererFlags { get; init; }
    public bool NativePlayerHidden { get; init; }
    public IReadOnlyList<FifaHiddenRenderer> HiddenRenderers { get; init; }
    public FifaActorCleanup Cleanup { get; init; }
    public FifaMotionTraceReport MotionTrace { get; init; }
    public FifaLocomotionStats Locomotion { get; init; } // H171: pack v2 only (null for v1)
}
