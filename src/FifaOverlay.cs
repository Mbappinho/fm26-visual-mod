using FM.Match;
using Il2CppInterop.Runtime.InteropTypes.Arrays;
using System.Diagnostics;
using System.Text.Json;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.SceneManagement;
using Object = UnityEngine.Object;

namespace FM26.VisualDiagnostics;

// H154 stage 1: opt-in, eligible outfield player(s), owned FIFA-animated mesh at the FM player root.
// Reads only ordinary Transform positions/rotations of the selected actors/anchors. CPU vertex upload only. Pack integrity
// before any allocation. H166 (0.6.0): one session host (lifecycle, pack, shared materials, deadline, match clock,
// discontinuities, cleanup, report) drives N per-actor states (FifaOverlayActor); the legacy single-actor form is N=1.
internal sealed partial class FifaOverlay
{
    private const int RowEvery = 10;
    // H166: every owned Unity handle (shared materials: Actor 0; per actor: its ActorInstanceId), budget declared before allocation.
    private readonly List<(Object Value, int Id, string Kind, bool Direct, int Actor)> _handles = new();
    private int _handleBudget;
    private readonly List<string> _cleanupErrors = new();
    private readonly List<FifaOverlayActor> _actors = new();
    private int[] _teams = Array.Empty<int>();
    private readonly Dictionary<int, Material[]> _materials = new(); // H166: one owned material per pack piece PER TEAM (shared)
    private FifaOverlayRequest _request;
    private Action<string, object> _write;
    // H206: _root/_controller/_publisher and the session identities live in FifaOverlaySession.cs (shared partial).
    private Shader _shader;
    private FifaPackHeader _pack;
    private string _packDirectory, _packHeaderSha256;
    private readonly Dictionary<string, float[]> _frames = new();
    private int[] _triangles;
    private int[][] _pieces;      // H160 (2): one submesh index array per pack piece (shared read-only by every actor)
    private int _lastFrame = -1, _startedFrame, _stoppedFrame;
    private long _started, _stopped, _lastQpc;
    private bool _created, _allocationUncertain;
    // H156: every start/stop/cleanup/publication/exclusion transition is decided by the pure, tested state machine.
    private readonly FifaOverlayLifecycle _lifecycle = new();
    // H164/H166: ONE match clock => discontinuities are global (recorded once, every active actor re-baselined).
    private readonly FifaTimeDiscontinuities _discontinuities = new();
    private readonly FifaResumeSettle _settle = new(); // H203: post-resume settle window (opened by the last resume)
    private double? _clock;                 // last match clock read on a processed frame (classification baseline)
    private int _frameClockFrame = -1; private double _frameClock;
    // H166: global view-change episodes (all running actors suspended at once) and per-frame overlay work cost.
    private readonly FifaGlobalSuspension _globalSuspension = new();
    private readonly FifaFrameCost _frameCost = new();
    private FifaFailureDetail _failureDetail;
    private FifaOverlayActor _failing;
    public bool Active => _lifecycle.Active;
    public string RequestId => _request?.RequestId;
    public bool OverlapActive => _lifecycle.Blocked;
    // H191: lifecycle facts for the segment policy (read only).
    public bool Finalized => _lifecycle.Finalized;
    public bool CleanupVerified => _lifecycle.CleanupVerified;
    public bool Published => _lifecycle.Published;
    public string Error => _lifecycle.Error;
    internal FifaOverlayRequest Request => _request;
    internal FifaPackHeader Pack => _pack;
    internal int[][] Pieces => _pieces;
    internal int Scene => _scene;

    public void Start(FifaOverlayRequest request, GameObject root, IReadOnlyList<PlayerBinding> actors, Action<string, object> write)
    {
        _request = request; _write = write; _root = root;
        try
        {
            if (request == null || request.Action != FifaOverlayPolicy.StartAction || actors == null || root == null || actors.Count < 1 || actors.Count > FifaOverlayPolicy.MaximumActors
                || (!request.Multi && (actors.Count != 1 || actors[0] == null || actors[0].ActorInstanceId != request.ActorInstanceId)))
                throw new InvalidOperationException("FIFA overlay requires the exact requested eligible actor set.");
            _lifecycle.Arm(File.Exists(ReportPath)); // B1: refused here => no publish obligation, nothing allocated
            _thread = Environment.CurrentManagedThreadId;
            _scene = SceneManager.GetActiveScene().handle; _rootId = root.GetInstanceID();
            _controller = root.GetComponentInParent<MatchPlaybackController>();
            if (_controller == null) throw new InvalidOperationException("Match controller unavailable.");
            _controllerId = _controller.GetInstanceID();
            _publisher = _controller.m_match;
            if (_publisher == null) throw new InvalidOperationException("Match publisher unavailable.");
            foreach (var actor in actors)
            {
                if (actor == null || actor.Player == null || !FifaActorSelection.Admissible(request.Multi, actor.Eligible, actor.FifaEligible) || _actors.Any(a => a.ActorInstanceId == actor.ActorInstanceId))
                    throw new InvalidOperationException("FIFA overlay requires the exact requested eligible actor.");
                var a = new FifaOverlayActor(this, actor, FifaReportBudget.HeadRows(request.Multi), FifaReportBudget.TailRows(request.Multi), _actors.Count < FifaMotionTrace.MaximumActors);
                _actors.Add(a);
                a.Capture(FifaReportBudget.RendererBound(request.Multi), ref _shader); // H160: hide before-state recorded here (read only)
            }
            _teams = _actors.Select(a => FifaTeamPalettes.Key(request.Multi, a.TeamId, a.Goalkeeper)).Distinct().OrderBy(t => t).ToArray();
            if (_shader == null) throw new InvalidOperationException("No loaded URP shader reference on the selected actors.");
            RequireSession(); foreach (var a in _actors) { a.RequireContext("start"); a.RequireFlags(); }
            // Pack integrity (size, sha256, shape, finite) before ANY owned Unity allocation. Managed arrays only.
            // H171: default = pack v2 folder when its header exists, else v1 (0.6.x); an explicit PackDirectory is used as given.
            LoadPack(request.PackDirectory ?? FifaOverlayPolicy.ResolveDefaultPackDirectory(Environment.GetEnvironmentVariable("LOCALAPPDATA"), File.Exists));
            // H166 (2): declared owned-handle budget (per actor 5 + 6 pieces x teams used) checked before any allocation.
            FifaHandleBudget.Require(_actors.Count, _pack.Pieces.Count, _teams.Length);
            _handleBudget = FifaHandleBudget.Required(_actors.Count, _pack.Pieces.Count, _teams.Length);
            double startClock = ReadMatchTime(); // match clock + start roots, before any owned allocation
            foreach (var a in _actors) a.Begin(_pack.RestProxyHeight, startClock);
            _clock = startClock;
            _started = Stopwatch.GetTimestamp(); _startedFrame = Time.frameCount; _lastQpc = _started;
            _lifecycle.Activate();
            CreateOwned();
            DiagnosticsPlugin.Logger.LogInfo($"[VISDIAG] FIFA_OVERLAY_STARTED: request {request.RequestId}; actors {_actors.Count} ({string.Join(",", _actors.Select(a => a.ActorInstanceId))}).");
        }
        catch (Exception error)
        {
            if (_lifecycle.Armed) Stop("start-failure", error.Message);
            throw;
        }
    }

    private string ReportPath => Path.Combine(DiagnosticsPlugin.Output, FifaOverlayReport.FileName(_request.RequestId));

    private static byte[] ReadBounded(string path, long expected)
    {
        var info = new FileInfo(path);
        if (!info.Exists || info.Length != expected) throw new InvalidOperationException($"Pack file missing or wrong size: {Path.GetFileName(path)}");
        return File.ReadAllBytes(path);
    }

    private void LoadPack(string directory)
    {
        _packDirectory = directory;
        var headerPath = Path.Combine(directory, "fifa-pack.json");
        var info = new FileInfo(headerPath);
        if (!info.Exists) throw new InvalidOperationException($"FIFA pack missing: {headerPath}. Build it locally with tools/fifa_pack.py build.");
        if (info.Length > FifaOverlayPolicy.MaximumHeaderBytes) throw new InvalidOperationException("FIFA pack header oversized.");
        var headerBytes = File.ReadAllBytes(headerPath);
        if (headerBytes.LongLength > FifaOverlayPolicy.MaximumHeaderBytes) throw new InvalidOperationException("FIFA pack header grew.");
        _packHeaderSha256 = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(headerBytes)).ToLowerInvariant();
        using (var document = JsonDocument.Parse(headerBytes)) _pack = FifaOverlayPolicy.ValidateHeader(document.RootElement);
        var tri = ReadBounded(Path.Combine(directory, _pack.TrianglesFile), _pack.TrianglesBytes);
        FifaOverlayPolicy.RequirePayload(tri, _pack.TrianglesSha256, _pack.TrianglesBytes);
        _triangles = FifaOverlayPolicy.DecodeTriangles(tri, _pack.VertexCount, _pack.FlipWinding);
        foreach (var clip in _pack.Clips.Values)
        {
            var data = ReadBounded(Path.Combine(directory, clip.File), clip.Bytes);
            FifaOverlayPolicy.RequirePayload(data, clip.Sha256, clip.Bytes);
            _frames[clip.Name] = FifaOverlayPolicy.ApplyAxis(_pack.Matrix, FifaOverlayPolicy.DecodeFrames(data, clip.Frames, _pack.VertexCount));
        }
        _pieces = FifaOverlayPolicy.SplitPieces(_triangles, _pack.Pieces);
    }

    internal float[] Frames(string clip) => _frames[clip];
    // H171: standing/creation pose = idle key 0 (pack frame 1) for pack v2; v1 keeps walk_F2 key 0.
    internal string StandClip => _pack.FormatVersion == 2 ? "idle" : FifaOverlayPolicy.StandClip;

    internal void RequireWindow()
    {
        if (!_lifecycle.Active || !FifaOverlayPolicy.WithinWindow(Stopwatch.GetTimestamp(), _started, Stopwatch.Frequency, _request.MaximumSeconds))
            throw new TimeoutException("FIFA overlay write window closed.");
    }

    internal void Reserve(int count) { if (_handles.Count + count > _handleBudget) throw new InvalidOperationException("Owned handle budget exceeded."); }
    internal void Retain(Object value, string kind, bool direct, int actor)
    {
        int index = _handles.Count; _handles.Add((value, 0, kind, direct, actor)); // wrapper retained before identity lookup
        _handles[index] = (value, value.GetInstanceID(), kind, direct, actor);
    }
    internal IEnumerable<(Object Value, int Id, string Kind, bool Direct, int Actor)> HandlesOf(int actor) => _handles.Where(h => h.Actor == actor);
    internal Material[] Materials(int team) => _materials[team];

    private void CreateOwned()
    {
        _allocationUncertain = true;
        CreateSharedMaterials();
        foreach (var a in _actors) a.CreateOwned();
        _created = true; _allocationUncertain = false;
    }

    // H166 (3): one owned material per pack piece and per TEAM used (shared by that team's actors); legacy N=1 = fixed H160 palette.
    private void CreateSharedMaterials()
    {
        foreach (var team in _teams)
        {
            var palette = FifaTeamPalettes.ForKey(_request.Multi, team);
            var materials = new Material[_pieces.Length];
            for (int k = 0; k < _pieces.Length; k++)
            {
                Reserve(1); RequireWindow(); var material = new Material(_shader); Retain(material, $"material-{_pack.Pieces[k].Role}-team-{team}", true, 0);
                materials[k] = material;
                RequireWindow(); material.name = "FM26Test_FifaOwnedMaterial_" + _pack.Pieces[k].Role + "_" + team;
                var rgba = palette[_pack.Pieces[k].Role];
                var color = new Color(rgba[0], rgba[1], rgba[2], rgba[3]);
                if (material.HasProperty("_BaseColor")) { RequireWindow(); material.SetColor("_BaseColor", color); }
                if (material.HasProperty("_Color")) { RequireWindow(); material.SetColor("_Color", color); }
            }
            _materials[team] = materials;
        }
    }

    // H209: RequireSession (H166 session part of the 0.5.1 RequireContext) moved unchanged to FifaOverlaySession.cs (executed by PolicyTests).
    internal Transform RootTransform => _root.transform;

    // H158: consumer read of the already-published match clock (seconds of match time); no write, no new producer access.
    private double ReadMatchTime()
    {
        var frame = _publisher.m_matchFrame;
        if (frame == null) throw new InvalidOperationException("Published match frame unavailable.");
        float time = frame._CurrentTime_k__BackingField;
        if (!float.IsFinite(time)) throw new InvalidOperationException("Nonfinite published match time.");
        return time;
    }

    // H166: at most ONE guarded match-clock read per Unity frame, shared by every actor (active pose path and resumes).
    internal double FrameClock(int frame)
    {
        if (_frameClockFrame == frame) return _frameClock;
        RequireWindow(); _frameClock = ReadMatchTime();
        _frameClockFrame = frame;
        return _frameClock;
    }

    internal float ReadPublishedPlaybackSpeed()
    {
        var frame = _publisher.m_matchFrame;
        if (frame == null) throw new InvalidOperationException("Published match frame unavailable.");
        return frame._CurrentPlaybackSpeed_k__BackingField; // literal, reported only (never used to compute)
    }

    // H160 (3): same structural identities as RequireSession (root, scene, controller, publisher); unreadable => not intact.
    // H164 (H163 suggestion 6): thread mismatch / read exception => Unreadable (failure while suspended), never "ended".
    private bool StructureIntact() => Structure() == FifaStructureState.Intact;

    private FifaStructureState Structure()
    {
        try
        {
            if (Environment.CurrentManagedThreadId != _thread) return FifaStructureState.Unreadable;
            return _root != null && _root.GetInstanceID() == _rootId && SceneManager.GetActiveScene().handle == _scene
                && _controller != null && _controller.GetInstanceID() == _controllerId && _controller.m_match == _publisher ? FifaStructureState.Intact : FifaStructureState.Ended;
        }
        catch { return FifaStructureState.Unreadable; }
    }

    // One host tick per Unity frame for ALL actors. Order per frame: deadline, session guards, per-actor guards + transform
    // reads (refusal => per-actor scope), suspended actors' decisions (a resume applies the session refusal, then its actor
    // guards, then the clock read), wall step, ONE guarded clock read, global
    // discontinuity (record once, re-baseline every active actor) or per-actor motion, per-actor pose writes, global
    // suspension record, all-ended check, frame cost guard.
    public void Tick()
    {
        CleanupTick();
        if (!_lifecycle.Active) return;
        long work = Stopwatch.GetTimestamp();
        FifaOverlayActor current = null;
        int blending = 0; // H171: actors that blended two clips this frame (FrameCost)
        try
        {
            long now = Stopwatch.GetTimestamp(); int frame = Time.frameCount;
            if (!FifaOverlayPolicy.WithinWindow(now, _started, Stopwatch.Frequency, _request.MaximumSeconds)) { Stop("stop-time-budget"); return; }
            if (frame == _lastFrame) return;
            // H166 (4): an ended actor is certified on a strictly later frame; an unverified per-actor cleanup stops everything.
            foreach (var e in _actors)
                if (e.VerifyEnded(frame, now) == false) { _failing = e; Stop(FifaViewSuspension.FailureReason, "Per-actor end cleanup not verified on a later frame."); return; }
            foreach (var a in _actors) if (a.Running) a.BeginFrame(frame, now); // H164: exact stop-frame observation per actor
            Exception session = null;
            try { RequireSession(); } catch (Exception e) { session = e; }
            var active = new List<FifaOverlayActor>(_actors.Count);
            foreach (var a in _actors)
            {
                if (!a.Running || a.Suspended) continue;
                current = a;
                try { if (session != null) throw session; a.Guard(); active.Add(a); }
                catch (Exception error)
                {
                    // H206: a SESSION predicate refusal (not thread) that is not a view-change suspension = fm-context-changed (normal end).
                    var scope = FifaSessionTransition.Apply(FifaActorScope.OnActiveFailure(_request.Multi, session?.Message, error is TimeoutException, error is FifaGuardException, StructureIntact(), a.ObserveRenderers(), _request.Multi && a.HardIdentityLost(), error.Message), FrameSessionPredicate);
                    if (scope.Kind == FifaScopeKind.SuspendActor && a.Created)
                    {
                        if (a.Suspend(frame, now)) continue;
                        scope = FifaActorScope.OnSuspendLimitCount(_request.Multi, session?.Message);
                    }
                    if (scope.Kind == FifaScopeKind.EndActor) { if (EndActor(a, scope.Reason, scope.Error, frame, now)) continue; return; }
                    _failing = a; Stop(scope.Kind == FifaScopeKind.SuspendActor ? FifaViewSuspension.FailureReason : scope.Reason, scope.Kind == FifaScopeKind.SuspendActor ? error.Message : scope.Error);
                    return;
                }
            }
            current = null;
            foreach (var a in _actors)
            {
                if (!a.Running || !a.Suspended || active.Contains(a)) continue;
                current = a;
                var scope = FifaActorScope.OnSuspended(_request.Multi, session?.Message, Structure(), a.Decide(Structure(), now));
                scope = FifaSessionTransition.ApplySuspended(scope, FrameSessionPredicate); // H238: suspended-path session-context change
                scope = FifaResumeRefusal.Apply(scope, _request.Multi, FrameSessionPredicate, scope.Kind == FifaScopeKind.Resume ? a.ContextRefusal() : null); // H240: resume-frame actor refusal
                if (scope.Kind == FifaScopeKind.Stay) continue;
                if (scope.Kind == FifaScopeKind.Resume) { a.Resume(frame, now, session); _settle.MarkResume(now); continue; }
                if (scope.Kind == FifaScopeKind.EndActor) { if (EndActor(a, scope.Reason, scope.Error, frame, now)) continue; return; }
                _failing = a; Stop(scope.Reason, scope.Error);
                return;
            }
            current = null;
            double dt = (double)(now - _lastQpc) / Stopwatch.Frequency;
            if (active.Count > 0)
            {
                foreach (var a in active) a.SetStage("wall-clock", dt);
                if (dt < 0 || dt > FifaOverlayPolicy.MaximumStepSeconds) throw new InvalidOperationException("External clock step backwards or stalled.");
                foreach (var a in active) a.SetStage("match-clock", null);
                double matchTime = FrameClock(frame);
                foreach (var a in active) a.SetClock(matchTime);
                // H164: finite discontinuity after the full guards = FM time discontinuity (one clock => recorded ONCE, every
                // active actor re-baselined, no teleport test on that frame); nonfinite still fails in ReadMatchTime/Observe.
                // H200: a frozen clock while FM moved some root > 3 m (key-moment repositioning) is one too.
                var step = FifaTimeDiscontinuities.Classify(_clock.Value, matchTime);
                // H203: inside the post-resume settle window a running-clock > 3 m jump is an FM reposition too (live-012).
                bool settling = _settle.InWindow(now, Stopwatch.Frequency);
                bool discontinuity = FifaTimeDiscontinuities.IsDiscontinuity(step, active.Max(a => a.DisplacementNow()), settling);
                if (discontinuity)
                {
                    if (!_discontinuities.TryRecord(frame, now, _clock.Value, matchTime, active.Max(a => a.DisplacementNow()), settling)) { Stop(FifaTimeDiscontinuities.LimitReason); return; }
                    foreach (var a in active) a.Rebaseline(matchTime);
                }
                else foreach (var a in active) { current = a; a.Observe(matchTime); }
                foreach (var a in active) { current = a; a.WritePose(frame, now, dt, matchTime, discontinuity); if (a.Blending) blending++; }
                current = null;
                _clock = matchTime;
            }
            else if (_frameClockFrame == frame) _clock = _frameClock; // resume-only frame: the resumed actors' baseline
            int running = _actors.Count(a => a.Running), suspended = _actors.Count(a => a.Running && a.Suspended);
            _globalSuspension.Observe(frame, now, Stopwatch.Frequency, running, suspended, _frameClockFrame == frame ? _frameClock : null);
            var ended = FifaActorScope.AllEnded(_request.Multi, running);
            if (ended != null) { Stop(ended); return; }
            _lastFrame = frame; _lastQpc = now; // every processed frame (also fully suspended ones) refreshes the wall reference
            if (_frameCost.Add((double)(Stopwatch.GetTimestamp() - work) * 1000.0 / Stopwatch.Frequency, blending)) { Stop(FifaFrameCost.Reason); return; }
        }
        catch (Exception error)
        {
            // Our own exception / clock / teleport / write failure on ANY actor stops the whole session (conservative); a guard
            // refusal on a resume keeps the 0.5.1 classification against that actor's renderers.
            var stop = FifaOverlayPolicy.ClassifyStop(error is TimeoutException, error is FifaGuardException, StructureIntact(), current?.ObserveRenderers(), error.Message);
            _failing = current; Stop(stop.Reason, stop.Error);
        }
    }

    // H166 (4): end ONE actor (its own restore, owned objects destroyed, later-frame verification); a failing per-actor
    // cleanup write stops the whole session. false = session stopped.
    private bool EndActor(FifaOverlayActor a, string reason, string error, int frame, long now)
    {
        a.End(reason, error, frame, now);
        if (!a.EndFailed) return true;
        _failing = a; Stop(FifaViewSuspension.FailureReason, "Per-actor end cleanup failed.");
        return false;
    }

    public void Stop(string reason, string error = null)
    {
        if (!_lifecycle.Armed || _lifecycle.CleanupRequested) return; // refused arm: no obligation; later stops: first cause wins
        _stopped = Stopwatch.GetTimestamp();
        try { _stoppedFrame = Time.frameCount; } catch (Exception e) { _stoppedFrame = -1; CleanupError(e); }
        _lifecycle.RequestStop(reason, OriginalPassPolicy.CanonicalError(error), _stoppedFrame, _stopped);
        // H164: triggering values of a failure stop (only when the stop frame itself was observed) + unconditional final rows.
        var failing = _failing ?? (_actors.Count == 1 ? _actors[0] : null);
        _failureDetail = _lifecycle.Error != null && failing != null ? failing.FailureSnapshot(_stoppedFrame) : null;
        _globalSuspension.CloseOnStop(_stoppedFrame, _stopped, Stopwatch.Frequency, reason);
        // H160: every actor restores its natives BEFORE its owned overlay is hidden/destroyed (every stop path, first cause latched).
        foreach (var a in _actors)
        {
            try { a.StopSession(reason, _stoppedFrame, _stopped); } catch (Exception e) { CleanupError(e); }
        }
        foreach (var h in _handles)
        {
            if (!h.Direct || h.Actor != 0) continue; // shared materials; per-actor objects are destroyed by their actor
            try { if (h.Value != null) Object.Destroy(h.Value); } catch (Exception e) { CleanupError(e); }
        }
        DiagnosticsPlugin.Logger.LogInfo($"[VISDIAG] FIFA_OVERLAY_STOP: {reason}; {_lifecycle.Error}");
    }

    internal void CleanupError(Exception e) { if (_cleanupErrors.Count < 128) _cleanupErrors.Add(FifaReportBudget.CleanupText(e.Message)); }

    public void CleanupTick()
    {
        if (!_lifecycle.CleanupRequested || _lifecycle.Finalized) return;
        int frame = Time.frameCount; long now = Stopwatch.GetTimestamp();
        if (frame <= _lifecycle.CleanupFrame) return; // strictly later Unity frame required
        var cleanup = ReadCleanup(frame, now, false);
        // Verified -> publish (releases exclusion only if written). Timeout 120 frames / 5 s -> publish UNCONFIRMED, stays blocked.
        if (_lifecycle.ShouldPublish(frame, now, Stopwatch.Frequency, cleanup.Verified)) Publish(cleanup);
    }

    public void Shutdown()
    {
        Stop("diagnostics-destroyed");
        if (_lifecycle.CleanupRequested && !_lifecycle.Finalized) Publish(ReadCleanup(Time.frameCount, Stopwatch.GetTimestamp(), true));
    }

    private sealed class CleanupResult
    {
        public int RequestedFrame { get; set; } public int VerificationFrame { get; set; } public long VerificationQpcTicks { get; set; }
        public bool Verified { get; set; } public int AliveCount { get; set; } public int UnknownCount { get; set; } public bool AllocationUncertain { get; set; }
        public object[] Handles { get; set; } public string[] Errors { get; set; }
        public bool NativeRestorationVerified { get; set; }
    }

    private CleanupResult ReadCleanup(int frame, long now, bool forceUnconfirmed)
    {
        int alive = 0, unknown = 0;
        var handles = _handles.Select(h =>
        {
            string status;
            try { status = h.Id == 0 ? "unknown" : h.Value == null ? "dead" : "alive"; } catch { status = "unknown"; }
            if (status == "alive") alive++; if (status == "unknown") unknown++;
            return (object)new { InstanceId = h.Id, h.Kind, Actor = h.Actor, Status = status };
        }).ToArray();
        // H160/H166: EVERY hidden renderer of EVERY actor read back on this (strictly later, except Shutdown) frame.
        bool restored = true;
        foreach (var a in _actors) { try { a.ReadRestoration(); } catch (Exception e) { CleanupError(e); } restored &= a.RestorationVerified; }
        return new CleanupResult
        {
            RequestedFrame = _lifecycle.CleanupFrame, VerificationFrame = frame, VerificationQpcTicks = now,
            Verified = !forceUnconfirmed && _cleanupErrors.Count == 0 && FifaOverlayPolicy.CanCertifyCleanup(_lifecycle.CleanupFrame, frame, alive, unknown, _allocationUncertain)
                && restored,
            NativeRestorationVerified = restored,
            AliveCount = alive, UnknownCount = unknown, AllocationUncertain = _allocationUncertain, Handles = handles, Errors = _cleanupErrors.ToArray(),
        };
    }

    private void Publish(CleanupResult cleanup)
    {
        bool written = false;
        try
        {
            var single = !_request.Multi && _actors.Count == 1 ? _actors[0] : null;
            var report = new
            {
                SchemaVersion = 1, Plugin = "FM26.VisualDiagnostics", Version = "0.10.7", _request.Session, PluginSession = DiagnosticsPlugin.Session, _request.RequestId,
                Mode = _request.Multi ? "multi" : "single", _request.ActorInstanceId, _request.ActorInstanceIds, _request.AllEligible, MaximumSeconds = _request.MaximumSeconds,
                SelectedActor = single?.Identity(), SelectedActorInstanceIds = _actors.Select(a => a.ActorInstanceId).ToArray(),
                Pack = new { Directory = _packDirectory, HeaderSha256 = _packHeaderSha256, PayloadsVerified = _pack != null && _frames.Count == _pack.Clips.Count && _triangles != null, FormatVersion = _pack?.FormatVersion,
                    AxisMatrix = _pack?.Matrix, FlipWinding = _pack?.FlipWinding, RestProxyHeight = _pack?.RestProxyHeight, RestExtent = _pack?.RestExtent,
                    Pieces = _pack?.Pieces, Palette = FifaOverlayPolicy.Palette, TeamPalettes = _request.Multi ? FifaTeamPalettes.All : null },
                VisualFit = single == null ? null : new { FmHeadToFeetAnchorDistance = single.FmHeadToFeet, UniformScale = single.Scale, Declared = true },
                PlaybackSpeedSource = FifaOverlayPolicy.PlaybackSpeedSource,
                Heuristics = FifaOverlayPolicy.Heuristics,
                Thresholds = new { FifaOverlayPolicy.StationaryBelowMetersPerSecond, FifaOverlayPolicy.JogEnterAtMetersPerSecond, FifaOverlayPolicy.JogLeaveBelowMetersPerSecond,
                    FifaOverlayPolicy.StandClip, FifaOverlayPolicy.StandFrame, FifaOverlayPolicy.WalkReferenceMetersPerSecond,
                    FifaOverlayPolicy.JogReferenceMetersPerSecond, FifaOverlayPolicy.MinimumRate, FifaOverlayPolicy.MaximumRate, FifaOverlayPolicy.SpeedWindowSeconds, FifaOverlayPolicy.KeysPerSecond,
                    FifaOverlayPolicy.MaximumFrameDisplacementMeters, FifaOverlayPolicy.MaximumPlausibleSpeed, FifaOverlayPolicy.MaximumMatchStepSeconds, FifaOverlayPolicy.MaximumStepSeconds,
                    HandleBudget = _handleBudget, MaximumRenderersPerActor = FifaReportBudget.RendererBound(_request.Multi) },
                StartedQpcTicks = _started, StartedFrame = _startedFrame, StoppedQpcTicks = _stopped, StoppedFrame = _stoppedFrame, QpcFrequency = Stopwatch.Frequency,
                StopReason = _lifecycle.Reason, Error = _lifecycle.Error, Created = _created, Shown = _actors.Any(a => a.Shown),
                ManagedPoseUpdates = _actors.Sum(a => a.PoseUpdates), MeshUploads = _actors.Sum(a => a.MeshUploads),
                FrameCost = _frameCost.Stats(),
                // Legacy N=1 shape (0.5.1 fields at the top level); multi: per-actor bounded sections + GLOBAL lists.
                ClipUpdates = single?.ClipUpdates, Locomotion = single?.LocomotionStats, MotionTrace = single?.MotionTraceReport, TimelineEvery = RowEvery, TimelineHeadRows = FifaReportBudget.HeadRows(_request.Multi), TimelineTailRows = FifaReportBudget.TailRows(_request.Multi),
                TimelineRowsTotal = single?.TimelineTotal, TimelineRowsDropped = single?.TimelineDropped, Timeline = single?.TimelineRows,
                SuspensionCount = single != null ? single.SuspensionCount : _globalSuspension.Count, MaximumSuspensions = FifaViewSuspension.MaximumSuspensions,
                MaximumSuspendedSeconds = FifaViewSuspension.MaximumSuspendedSeconds,
                Suspensions = single != null ? single.SuspensionRecords : _globalSuspension.Records, GlobalSuspensionsDropped = _globalSuspension.Dropped,
                DiscontinuityCount = _discontinuities.Count, MaximumDiscontinuities = FifaTimeDiscontinuities.MaximumDiscontinuities, Discontinuities = _discontinuities.Records,
                FailureDetail = _failureDetail, FailingActorInstanceId = _failing?.ActorInstanceId, SessionPredicate, ActorPredicate, ActorPredicateStage,
                OriginalRendererFlags = single?.Flags(), HideNativePlayer = _request.HideNativePlayer, NativePlayerHidden = _actors.Any(a => a.NativeHidden),
                HiddenRenderers = single?.HiddenRenderers,
                NativeHideMechanism = _request.HideNativePlayer ? "Renderer.forceRenderingOff on each target's start-captured renderers; restored to the recorded value at its end/stop" : null,
                Actors = _request.Multi ? _actors.Select(a => a.Report()).ToArray() : null,
                Cleanup = cleanup,
                Certifications = new { NativeFifaTiming = false, NativeBoneMapping = false, PixelVisibility = false, GestureFidelity = false, SimulationUnchanged = "not-touched-by-design-not-measured" },
            };
            // B1: CreateNew + non-overwriting move; an existing request-bound report is never replaced.
            FifaOverlayReport.WriteNew(ReportPath, JsonSerializer.SerializeToUtf8Bytes(report, new JsonSerializerOptions { WriteIndented = true }), FifaOverlayReport.MaximumBytes);
            written = true;
        }
        catch (Exception e) { DiagnosticsPlugin.Logger.LogError($"[VISDIAG] FIFA_OVERLAY_PUBLISH_FAILED: {e}"); }
        _lifecycle.RecordPublication(cleanup.Verified, written); // failed publication or unverified cleanup => blocked for the session
    }

    public object Describe() => new
    {
        Plugin = "FM26.VisualDiagnostics", Version = "0.10.7", Session = DiagnosticsPlugin.Session, RequestId = _request?.RequestId, Mode = _request == null ? null : _request.Multi ? "multi" : "single",
        Actors = _actors.Count, Running = _actors.Count(a => a.Running), Suspended = _actors.Any(a => a.Running && a.Suspended), SuspendedActors = _actors.Count(a => a.Running && a.Suspended),
        Suspensions = _request != null && !_request.Multi && _actors.Count == 1 ? _actors[0].SuspensionCount : _globalSuspension.Count, Discontinuities = _discontinuities.Count,
        Active = _lifecycle.Active, Created = _created, Shown = _actors.Any(a => a.Shown), HideNativePlayer = _request?.HideNativePlayer, NativePlayerHidden = _actors.Any(a => a.NativeHidden),
        Clip = _actors.Count == 1 ? _actors[0].Clip : null, PoseUpdates = _actors.Sum(a => a.PoseUpdates), MeshUploads = _actors.Sum(a => a.MeshUploads),
        FrameCostP95Milliseconds = _frameCost.Stats().P95Milliseconds,
        StopReason = _lifecycle.Reason, Error = _lifecycle.Error, SessionPredicate, ActorPredicate, ActorPredicateStage, CleanupRequested = _lifecycle.CleanupRequested, Finalized = _lifecycle.Finalized,
        CleanupVerified = _lifecycle.CleanupVerified, ReportPublished = _lifecycle.Published, PublicationFailed = _lifecycle.PublicationFailed, Blocked = _lifecycle.Blocked,
    };

    internal static System.Numerics.Vector3 ToN(Vector3 v) => new(v.x, v.y, v.z);
    internal static FifaRowVector V(Vector3 v) => new() { X = v.x, Y = v.y, Z = v.z };
    internal static void RequireFinite(Vector3 v)
    {
        if (!float.IsFinite(v.x) || !float.IsFinite(v.y) || !float.IsFinite(v.z) || v.sqrMagnitude > 100000000)
            throw new InvalidOperationException("Nonfinite/unbounded visual position.");
    }
}

// H166: ONE selected actor (the whole 0.5.1 single-actor state): context/flag guards, motion, clip/phase, suspension, hide
// ledger, owned mesh/root, timeline. Driven only by the session host Tick; shares the session's pack and team materials.
internal sealed class FifaOverlayActor
{
    private const int RowEvery = 10;
    private readonly FifaOverlay _s;
    private readonly PlayerBinding _actor;
    private readonly List<(Renderer Renderer, int Id, bool Enabled, bool Active)> _originals = new();
    // H162: whole-match bounded timeline (first rows + ring of the last rows); H166: per-actor caps from FifaReportBudget.
    private readonly FifaTimelineSampler _timeline;
    private readonly Dictionary<string, long> _clipUpdates = new() { ["stand"] = 0, ["walk_F2"] = 0, ["jog_F2"] = 0 };
    private GameObject _ownedRoot;
    private Transform _actorTransform, _head, _leftFoot, _rightFoot;
    private FifaMatchMotion _motion;
    private Mesh _mesh;
    private Il2CppStructArray<Vector3> _vertices; // H166 (5): one reused IL2CPP vertex buffer per actor (no per-upload conversion)
    private int _actorTransformId, _shownFrame = -1, _endFrame = -1;
    private long _poseUpdates, _meshUploads;
    private double _phase, _scale, _fmHeadToFeet;
    private string _clip = "stand", _shownClip, _endReason, _endError;
    private bool _created, _shown, _ended;
    // H171: pack v2 locomotion selector/blender (null for a v1 pack: 0.6.x walk/jog path unchanged); _shownBlend = last upload was a blend.
    private FifaLocomotion _loco;
    private bool _shownBlend;
    private readonly bool _traceEligible;
    private FifaMotionTrace _motionTrace;
    private int _traceResets;
    // H160 (1): opt-in reversible native hide ledger (Renderer.forceRenderingOff only; empty unless HideNativePlayer).
    private readonly FifaNativeHideLedger _hide = new();
    // H162: FM view change => this actor SUSPENDED (owned root inactive, natives untouched), resumed with re-baselined motion.
    private readonly FifaViewSuspension _suspension = new();
    private bool _visible;
    private double? _lastMatchTime;
    private readonly FifaFrameObservation _obs = new();
    private Vector3 _position, _forward;
    private FifaActorCleanup _endCleanup;
    private readonly List<string> _endErrors = new();

    public FifaOverlayActor(FifaOverlay session, PlayerBinding actor, int headRows, int tailRows, bool traceEligible)
    {
        _s = session; _actor = actor; _timeline = new FifaTimelineSampler(headRows, tailRows); _traceEligible = traceEligible;
    }

    public int ActorInstanceId => _actor.ActorInstanceId;
    public int TeamId => _actor.TeamId;
    public bool Goalkeeper => _actor.Goalkeeper; // H178a: start role (palette key)
    public bool Running => !_ended;
    public bool Suspended => _suspension.Suspended;
    public bool Created => _created;
    public bool Shown => _shown;
    public bool NativeHidden => _hide.Hidden;
    public bool EndFailed => _endErrors.Count > 0;
    public string Clip => _clip;
    public long PoseUpdates => _poseUpdates;
    public long MeshUploads => _meshUploads;
    public double Scale => _scale;
    public double FmHeadToFeet => _fmHeadToFeet;
    public IReadOnlyDictionary<string, long> ClipUpdates => _clipUpdates;
    public bool Blending => _loco != null && _loco.Blending;
    public FifaLocomotionStats LocomotionStats => _loco?.Stats();
    public FifaMotionTraceReport MotionTraceReport => _motionTrace?.Snapshot();
    public long TimelineTotal => _timeline.Total;
    public long TimelineDropped => _timeline.Dropped;
    public IReadOnlyList<FifaTimelineRow> TimelineRows => _timeline.Rows;
    public int SuspensionCount => _suspension.Count;
    public IReadOnlyList<FifaSuspensionRecord> SuspensionRecords => _suspension.Records;
    public IReadOnlyList<FifaHiddenRenderer> HiddenRenderers => _hide.Entries;
    public bool RestorationVerified => _hide.RestorationVerified;
    public object Identity() => new { _actor.ActorInstanceId, _actor.PlayerId, Name = FifaReportBudget.NameText(_actor.Name), _actor.TeamId, _actor.ShirtNumber };

    public void Capture(int rendererBound, ref Shader shader)
    {
        var request = _s.Request;
        _actorTransform = _actor.Player.transform; _actorTransformId = _actorTransform.GetInstanceID();
        if (!_actor.Anchors.TryGetValue("Character_Head", out _head) || !_actor.Anchors.TryGetValue("Character_LeftFoot", out _leftFoot)
            || !_actor.Anchors.TryGetValue("Character_RightFoot", out _rightFoot))
            throw new InvalidOperationException("Required head/foot anchors unavailable.");
        if (_actor.Renderers.Count == 0 || _actor.Renderers.Count > rendererBound) throw new InvalidOperationException("Original renderer inventory unavailable/unbounded.");
        foreach (var renderer in _actor.Renderers)
        {
            if (renderer == null) throw new InvalidOperationException("Original renderer unavailable.");
            _originals.Add((renderer, renderer.GetInstanceID(), renderer.enabled, renderer.gameObject.activeInHierarchy));
            // H160: exact before-state of the only flag a hide run may write (read only; nothing written before the overlay is shown).
            if (request.HideNativePlayer) _hide.Record(_originals[^1].Id, renderer.forceRenderingOff, _originals[^1].Enabled, _originals[^1].Active);
            var materials = renderer.sharedMaterials;
            if (materials.Length > 16) throw new InvalidOperationException("Original material slots exceed bound.");
            foreach (var m in materials)
            {
                if (m == null || m.shader == null) continue;
                if (m.shader.name == "Universal Render Pipeline/Lit") shader = m.shader;
                else if (shader == null && m.shader.name == "Universal Render Pipeline/Unlit") shader = m.shader;
            }
        }
    }

    // Scale + match motion baseline (start clock shared by all actors), before any owned allocation.
    public void Begin(double packProxy, double startClock)
    {
        var head = _head.position; var left = _leftFoot.position; var right = _rightFoot.position;
        FifaOverlay.RequireFinite(head); FifaOverlay.RequireFinite(left); FifaOverlay.RequireFinite(right);
        _fmHeadToFeet = Vector3.Distance(head, (left + right) * 0.5f);
        _scale = FifaOverlayPolicy.UniformScale(_fmHeadToFeet, packProxy);
        _motion = new FifaMatchMotion(startClock, FifaOverlay.ToN(_actorTransform.position));
        _lastMatchTime = startClock;
        _loco = _s.Pack.FormatVersion == 2 ? new FifaLocomotion(_s.Pack) : null;
        if (_traceEligible && _loco != null) _motionTrace = new FifaMotionTrace();
    }

    public void CreateOwned()
    {
        var pack = _s.Pack; int id = _actor.ActorInstanceId;
        _s.Reserve(2); _s.RequireWindow(); _ownedRoot = new GameObject("FM26Test_FIFA_OVERLAY_" + _s.Request.RequestId + "_" + id); _s.Retain(_ownedRoot, "root-game-object", true, id);
        _s.Retain(_ownedRoot.transform, "root-transform", false, id);
        _s.RequireWindow(); _ownedRoot.SetActive(false); _s.RequireWindow(); _ownedRoot.layer = 0;
        _s.Reserve(1); _s.RequireWindow(); _mesh = new Mesh(); _s.Retain(_mesh, "mesh", true, id);
        _s.RequireWindow(); _mesh.name = "FM26Test_FifaPlayerMesh_" + id;
        _vertices = new Il2CppStructArray<Vector3>(pack.VertexCount); // IL2CPP managed array (GC handle), not a Unity Object
        FillVertices("stand", FifaOverlayPolicy.StandFrame);
        _s.RequireWindow(); _mesh.vertices = _vertices;
        var pieces = _s.Pieces;
        _s.RequireWindow(); _mesh.subMeshCount = pieces.Length;
        for (int k = 0; k < pieces.Length; k++) { _s.RequireWindow(); _mesh.SetTriangles(pieces[k], k); }
        _s.RequireWindow(); _mesh.RecalculateNormals(); _s.RequireWindow(); _mesh.RecalculateBounds();
        _s.RequireWindow(); _mesh.MarkDynamic();
        _s.Reserve(2);
        _s.RequireWindow(); var filter = _ownedRoot.AddComponent<MeshFilter>(); _s.Retain(filter, "mesh-filter", false, id); _s.RequireWindow(); filter.sharedMesh = _mesh;
        _s.RequireWindow(); var renderer = _ownedRoot.AddComponent<MeshRenderer>(); _s.Retain(renderer, "mesh-renderer", false, id); RequireWindow(); renderer.sharedMaterials = _s.Materials(FifaTeamPalettes.Key(_s.Request.Multi, _actor.TeamId, _actor.Goalkeeper));
        _s.RequireWindow(); renderer.shadowCastingMode = ShadowCastingMode.Off; _s.RequireWindow(); renderer.receiveShadows = false;
        _s.RequireWindow(); _ownedRoot.transform.localScale = Vector3.one * (float)_scale;
        _shownClip = "stand"; _shownFrame = FifaOverlayPolicy.StandFrame; _meshUploads = 1;
        _created = true;
    }

    private void RequireWindow() => _s.RequireWindow();

    private void FillVertices(string clip, int frame)
    {
        // H156 declared heuristic: stand = walk_F2 key 0 (pack frame 1); the T-pose rest frame 0 is kept in the pack for verification only.
        var source = _s.Frames(clip == "stand" ? _s.StandClip : clip);
        FifaVertexCopy.CopyFrame<Vector3>(source, frame, _s.Pack.VertexCount, _vertices.AsSpan());
    }

    // Actor part of the 0.5.1 RequireContext (the session part runs once per frame in FifaOverlay.RequireSession).
    // H209: the actor predicates run in the shared FifaActorContext (FifaOverlayActorContext.cs, literal 0.10.3 chain);
    // where = caller stage ("start" | "tick" | "resume").
    public void RequireContext(string where)
    {
        FifaActorContext.Require(_s, _obs, where, _actor, _actorTransform, _actorTransformId);
        foreach (var anchor in new[] { _head, _leftFoot, _rightFoot })
            if (anchor == null || !anchor.IsChildOf(_actorTransform)) throw new FifaGuardException("FIFA overlay anchor lifetime changed.");
    }

    // H240: read-only actor guard predicate (no latch, no throw) for the resume-frame scope decision (FifaResumeRefusal).
    public string ContextRefusal() => FifaActorContext.Refusal(_s, _actor, _actorTransform, _actorTransformId);

    // H166 (4): read-only HARD identity loss (substitution / red card / despawn), used only to scope a guard refusal.
    // Activity flags (IsActive, activeInHierarchy) are NOT identity: FM cuts may toggle them, so they stay view-change/failure.
    public bool HardIdentityLost()
    {
        try
        {
            var p = _actor.Player;
            return p == null || _actorTransform == null || p.transform != _actorTransform || _actorTransform.GetInstanceID() != _actorTransformId
                || p.ID != _actor.PlayerId || p.ShirtNumber != _actor.ShirtNumber || p.m_visuals3D == null || p.m_visuals3D.IsSubstitute || p.m_visuals3D.IsMedic
                || FifaActorScope.RoleIdentityLost(_actor.Goalkeeper, p.IsGoalkeeper);
        }
        catch { return false; } // unreadable is not proof of departure => conservative session stop
    }

    private void RequireOwned()
    {
        foreach (var h in _s.HandlesOf(_actor.ActorInstanceId).Concat(_s.HandlesOf(0)))
            if (h.Value == null || h.Value.GetInstanceID() != h.Id) throw new InvalidOperationException("Owned handle lifetime changed.");
        if (_ownedRoot.transform.parent != null || _ownedRoot.scene.handle != _s.Scene || _ownedRoot.layer != 0)
            throw new InvalidOperationException("Owned root hierarchy/scene/layer changed.");
    }

    public FifaRendererFlagReport[] Flags() => _originals.Select(o =>
    {
        bool? enabled = null, active = null, forceOff = null; string status;
        try
        {
            if (o.Renderer == null) status = "dead";
            else
            {
                enabled = o.Renderer.enabled; active = o.Renderer.gameObject.activeInHierarchy;
                // H160: forceRenderingOff is read only in hide runs (default runs keep the 0.3.2 read set) and compared with the ledger.
                forceOff = _s.Request.HideNativePlayer ? o.Renderer.forceRenderingOff : null;
                status = FifaOverlayPolicy.RendererFlagStatus(o.Enabled, o.Active, enabled.Value, active.Value, _s.Request.HideNativePlayer && _hide.ExpectedForceRenderingOff(o.Id), forceOff);
            }
        }
        catch { status = "unavailable"; }
        return new FifaRendererFlagReport { RendererInstanceId = o.Id, BeforeEnabled = o.Enabled, BeforeActiveInHierarchy = o.Active, AfterEnabled = enabled, AfterActiveInHierarchy = active,
            AfterForceRenderingOff = forceOff, Status = status };
    }).ToArray();

    // H160 (1): the ONLY native write of the mod (Renderer.forceRenderingOff set to true) on this actor's renderers captured at
    // start, after its owned overlay is shown. Intent recorded before each write; readback must be true. No enabled/SetActive writes.
    private void HideNative()
    {
        foreach (var o in _originals)
        {
            if (!_hide.NeedsHide(o.Id)) continue;
            RequireWindow(); _hide.BeginHide(o.Id);
            o.Renderer.forceRenderingOff = true;
            _hide.ConfirmHide(o.Id, o.Renderer.forceRenderingOff);
        }
    }

    // H160 (1): cleanup write on every stop/end path (also after the deadline): recorded before value back on every attempted renderer.
    // H168 (S2): actorDeparted only from an actor-left End (confirmed departure, session intact): a renderer FM destroyed is then
    // recorded destroyed-by-fm by the pure ledger; otherwise (actor still present) the ledger raises the 0.5.1 failure.
    private void RestoreNative(bool actorDeparted = false)
    {
        foreach (var entry in _hide.RestorePlan)
        {
            try
            {
                if (entry.RestoreWriteAttempted) continue; // H166: an actor already ended was restored by its own End
                _hide.BeginRestore(entry.RendererInstanceId);
                var renderer = _originals.First(o => o.Id == entry.RendererInstanceId).Renderer;
                if (renderer == null) { _hide.RendererDestroyed(entry.RendererInstanceId, actorDeparted); continue; }
                renderer.forceRenderingOff = entry.BeforeForceRenderingOff;
            }
            catch (Exception e) { Error(e); }
        }
    }

    private void Error(Exception e)
    {
        if (_ended && _endCleanup == null && _endErrors.Count < 16) _endErrors.Add(FifaReportBudget.CleanupText(e.Message));
        _s.CleanupError(e);
    }

    public void RequireFlags()
    {
        if (_originals.Count == 0 || !FifaOverlayPolicy.FlagsUnchanged(Flags()))
            throw new FifaGuardException("Original renderer flags changed or unavailable; overlay stops (a hide run restores only its own forceRenderingOff writes).");
    }

    // H160 (3): read-only observation used only to classify a guard refusal (never to decide a write).
    public (bool, bool, bool?, bool?)[] ObserveRenderers()
    {
        static bool? Read(Func<bool> read) { try { return read(); } catch { return null; } }
        var result = new (bool, bool, bool?, bool?)[_originals.Count];
        for (int i = 0; i < _originals.Count; i++)
        {
            var renderer = _originals[i].Renderer;
            result[i] = (_originals[i].Enabled, _originals[i].Active, renderer == null ? null : Read(() => renderer.enabled), renderer == null ? null : Read(() => renderer.gameObject.activeInHierarchy));
        }
        return result;
    }

    public void BeginFrame(int frame, long now) => _obs.Begin(frame, now, _lastMatchTime, _motion == null ? null : _motion.LastPosition);
    public void SetStage(string stage, double? wallDt) { _obs.Stage = stage; if (wallDt.HasValue) _obs.SetWallDt(wallDt.Value); }
    public void SetClock(double matchTime) => _obs.SetClock(matchTime);

    // Active path, part 1: full guards then the transform reads (0.5.1 order).
    public void Guard()
    {
        _obs.Stage = "guards"; RequireContext("tick"); RequireOwned(); RequireFlags();
        _obs.Stage = "actor-transform"; RequireWindow(); _position = _actorTransform.position;
        RequireWindow(); _forward = _actorTransform.forward;
        _obs.SetPosition(FifaOverlay.ToN(_position));
        FifaOverlay.RequireFinite(_position); FifaOverlay.RequireFinite(_forward);
    }

    public double DisplacementNow() => _motion.DisplacementTo(FifaOverlay.ToN(_position));
    public void Rebaseline(double matchTime) { _obs.Stage = "motion"; _motion.Rebaseline(matchTime, FifaOverlay.ToN(_position)); _loco?.Snap(); _traceResets++; }
    // H158: speed window, per-frame jump test and phase all in MATCH seconds (FM playback speed independent).
    public void Observe(double matchTime) { _obs.Stage = "motion"; _motion.Observe(matchTime, FifaOverlay.ToN(_position)); }

    // Active path, part 2: clip/phase, vertex upload, owned pose, show, opt-in hide, sampled row.
    public void WritePose(int frame, long now, double dt, double matchTime, bool discontinuity)
    {
        _obs.Stage = "pose-write";
        var clip = _clip; double rate = 0; int index; bool changed;
        if (_loco == null)
        {
            if (!_motion.Paused && !discontinuity) // paused: match clock frozen => clip/phase/pose held, no stop (H164: same on a discontinuity frame)
            {
                clip = FifaOverlayPolicy.SelectClip(_motion.Speed, _clip); // walk/jog hysteresis uses the previous selection
                rate = FifaOverlayPolicy.PlaybackRate(clip, _motion.Speed);
                if (clip != _clip) { _phase = FifaOverlayPolicy.CarryPhase(_phase, Keys(_clip), Keys(clip)); _clip = clip; }
                if (clip != "stand") _phase = FifaOverlayPolicy.AdvancePhase(_phase, _motion.MatchDt, rate, Keys(clip));
            }
            index = FifaOverlayPolicy.FrameIndex(clip, _phase, Keys(clip));
            changed = clip != _shownClip || index != _shownFrame;
            if (changed) FillVertices(clip, index);
        }
        else
        {
            // H171 pack v2: tier/direction selection + phase-synced crossfade in MATCH time (pure FifaLocomotion); paused => held;
            // FM time discontinuity => snap to the current target (no fade across the jump), pose held that frame.
            if (!discontinuity && !_motion.Paused && _motion.WindowSeconds > 0) _loco.Update(_motion.MatchDt, _motion.Speed, _motion.WindowDeltaX, _motion.WindowDeltaZ, _forward.x, _forward.z);
            clip = _clip = _loco.TargetClip; _phase = _loco.TargetPhase; index = _loco.TargetFrame;
            rate = _motion.Paused || discontinuity ? 0 : _loco.Rate;
            changed = clip != _shownClip || index != _shownFrame || _loco.Blending || _shownBlend;
            if (changed) FillLocomotion();
            _shownBlend = _loco.Blending;
            _motionTrace?.Add(frame, matchTime, _motion.MatchDt, _motion.Speed, _motion.WindowDeltaX, _motion.WindowDeltaZ, _forward.x, _forward.z, _traceResets, _motion.WindowSeconds > 0, _motion.Paused, discontinuity, _loco);
            _traceResets = 0;
        }
        double yaw = FifaOverlayPolicy.YawDegrees(FifaOverlay.ToN(_forward));
        if (changed)
        {
            RequireWindow(); _mesh.vertices = _vertices;
            RequireWindow(); _mesh.RecalculateNormals();
            RequireWindow(); _mesh.RecalculateBounds();
            _shownClip = clip; _shownFrame = index; _meshUploads++;
        }
        var rotation = Quaternion.Euler(0f, (float)yaw, 0f);
        RequireWindow(); _ownedRoot.transform.SetPositionAndRotation(_position, rotation);
        if (!_visible) { RequireWindow(); _ownedRoot.SetActive(true); _visible = true; _shown = true; }
        if (_s.Request.HideNativePlayer && !_hide.Hidden) HideNative(); // H160: only once the owned overlay exists and is shown
        _poseUpdates++; if (_loco == null) _clipUpdates[clip]++;
        if ((_poseUpdates - 1) % RowEvery == 0 || discontinuity)
        {
            // H160 (H159 suggestion 1): nonfinite literal => null + flag, so JsonSerializer can never lose the final report.
            var speed = FifaOverlayPolicy.ReportablePlaybackSpeed(_s.ReadPublishedPlaybackSpeed());
            _timeline.Add(new FifaTimelineRow { Kind = discontinuity ? "discontinuity" : "sample", FrameCount = frame, QpcTicks = now, WallDt = dt, MatchTimeSeconds = matchTime, MatchDt = _motion.MatchDt,
                MatchSpeedMetersPerSecond = _motion.Speed, MatchSpeedWindowSeconds = _motion.WindowSeconds, FrameDisplacementMeters = _motion.FrameDisplacement,
                Paused = _motion.Paused, PublishedPlaybackSpeed = speed.Value, PublishedPlaybackSpeedNonFinite = speed.NonFinite, Clip = clip, Rate = rate,
                PhaseKeys = _phase, PackFrame = index, YawDegrees = yaw, RootPosition = FifaOverlay.V(_position), RootForward = FifaOverlay.V(_forward),
                BlendClip = _loco?.FromClip, BlendPackFrame = _loco != null && _loco.Blending ? _loco.FromFrame : null, BlendWeight = _loco != null && _loco.Blending ? _loco.Weight : null,
                Tier = _loco == null ? null : FifaLocomotion.TierNames[_loco.Tier], Direction = _loco == null ? null : FifaLocomotion.DirectionNames[_loco.Direction], Fallback = _loco?.Fallback });
        }
        _lastMatchTime = matchTime;
    }

    // H171: v2 pose into the reused per-actor buffer: a two-clip lerp while fading, else one block copy. No allocation.
    private void FillLocomotion()
    {
        if (_loco.Blending)
            FifaVertexCopy.BlendFrames<Vector3>(_s.Frames(_loco.FromClip), _loco.FromFrame, _s.Frames(_loco.TargetClip), _loco.TargetFrame, _loco.Weight, _s.Pack.VertexCount, _vertices.AsSpan());
        else FillVertices(_loco.TargetClip, _loco.TargetFrame);
    }

    private int Keys(string clip) => clip == "stand" ? 0 : _s.Pack.Clips[clip].Keys;

    // H162 (a): enter SUSPENDED: only this actor's OWNED root is deactivated (deadline checked first); natives, ledger and the
    // session lifecycle untouched. false = per-actor count bound reached (caller applies FifaActorScope.OnSuspendLimitCount).
    public bool Suspend(int frame, long now)
    {
        if (!_suspension.TryBegin(frame, now, _lastMatchTime)) return false;
        RequireWindow(); _ownedRoot.SetActive(false); _visible = false;
        return true;
    }

    // H162 (b)/(d): one suspended frame decided by the pure policy (read-only observation; pose frozen).
    public (FifaSuspendedDecision Decision, string Reason, string Error) Decide(FifaStructureState structure, long now)
    {
        _obs.Stage = "suspended";
        return FifaViewSuspension.Decide(structure, ActorPresent(), ObserveSuspended(), (double)(now - _suspension.Records[^1].StartQpcTicks) / Stopwatch.Frequency);
    }

    // H168 (L1, the 0.5.1 resume order restored): the frame's SESSION refusal (RequireSession: thread, root active, scene
    // MatchPlayback, Find(Match3DBuilder) == root, GetComponentInParent == controller, publisher) throws before anything else,
    // then this actor's guards, then the frame's single guarded clock read (FrameClock, cached per frame), then the position
    // read and the re-baseline of match motion. The owned root is re-shown by the next full guarded pose update (WritePose).
    public void Resume(int frame, long now, Exception session)
    {
        if (session != null) throw session;
        RequireContext("resume"); RequireOwned(); RequireFlags();
        double matchTime = _s.FrameClock(frame);
        RequireWindow(); var position = _actorTransform.position;
        FifaOverlay.RequireFinite(position);
        _motion.Rebaseline(matchTime, FifaOverlay.ToN(position));
        _loco?.Snap(); _traceResets++; // H173: no crossfade/history across a cut
        _suspension.Resume(frame, now, Stopwatch.Frequency, matchTime);
        _lastMatchTime = matchTime;
    }

    // H162: read-only observation of this actor's renderers while suspended; forceRenderingOff read only in hide runs.
    private (bool, bool, bool?, bool?, bool?, bool?)[] ObserveSuspended()
    {
        static bool? Read(Func<bool> read) { try { return read(); } catch { return null; } }
        var result = new (bool, bool, bool?, bool?, bool?, bool?)[_originals.Count];
        for (int i = 0; i < _originals.Count; i++)
        {
            var o = _originals[i]; var renderer = o.Renderer;
            bool? expected = _s.Request.HideNativePlayer ? _hide.ExpectedForceRenderingOff(o.Id) : null;
            bool? forceOff = _s.Request.HideNativePlayer ? (renderer == null ? null : Read(() => renderer.forceRenderingOff)) : null;
            result[i] = (o.Enabled, o.Active, renderer == null ? null : Read(() => renderer.enabled), renderer == null ? null : Read(() => renderer.gameObject.activeInHierarchy), expected, forceOff);
        }
        return result;
    }

    // H162 (d): selected actor still alive with the same Transform; H166 multi: and the same hard identity.
    // H171 (H169 suggestion 4): a read exception is UNREADABLE (null => failure decision), not "absent" (which could become actor-left).
    private bool? ActorPresent()
    {
        try
        {
            return _actor.Player != null && _actorTransform != null && _actor.Player.transform == _actorTransform && _actorTransform.GetInstanceID() == _actorTransformId
                && (!_s.Request.Multi || !HardIdentityLost());
        }
        catch { return null; }
    }

    // H166 (4): end THIS actor only (actor-left / per-actor suspend-limit): stop row, close its suspension, restore its natives
    // BEFORE its owned objects are hidden/destroyed; certified later by VerifyEnded on a strictly later frame.
    public void End(string reason, string error, int frame, long now)
    {
        if (_ended) return;
        _ended = true; _endReason = reason; _endError = OriginalPassPolicy.CanonicalError(error); _endFrame = frame;
        AddStopRow(frame, now);
        _suspension.CloseOnStop(frame, now, Stopwatch.Frequency, reason);
        RestoreNative(reason == FifaActorScope.ActorLeftReason);
        try { if (_ownedRoot != null) _ownedRoot.SetActive(false); } catch (Exception e) { Error(e); }
        foreach (var h in _s.HandlesOf(_actor.ActorInstanceId).ToArray())
        {
            if (!h.Direct) continue;
            try { if (h.Value != null) Object.Destroy(h.Value); } catch (Exception e) { Error(e); }
        }
        DiagnosticsPlugin.Logger.LogInfo($"[VISDIAG] FIFA_OVERLAY_ACTOR_END: {_actor.ActorInstanceId} {reason}");
    }

    // null = nothing to verify (running, or already certified) / pending (same frame); true = verified; false = not verified.
    public bool? VerifyEnded(int frame, long now)
    {
        if (!_ended || _endCleanup != null || _endFrame < 0) return null;
        if (frame <= _endFrame) return null;
        int alive = 0, unknown = 0;
        foreach (var h in _s.HandlesOf(_actor.ActorInstanceId))
        {
            string status;
            try { status = h.Id == 0 ? "unknown" : h.Value == null ? "dead" : "alive"; } catch { status = "unknown"; }
            if (status == "alive") alive++; if (status == "unknown") unknown++;
        }
        ReadRestoration();
        _endCleanup = new FifaActorCleanup { RequestedFrame = _endFrame, VerificationFrame = frame, VerificationQpcTicks = now, AliveCount = alive, UnknownCount = unknown,
            NativeRestorationVerified = _hide.RestorationVerified, Errors = _endErrors.ToArray(),
            Verified = _endErrors.Count == 0 && FifaOverlayPolicy.CanCertifyCleanup(_endFrame, frame, alive, unknown, false) && _hide.RestorationVerified };
        return _endCleanup.Verified;
    }

    // H160: restoration read back (null = renderer dead/unreadable).
    public void ReadRestoration()
    {
        foreach (var entry in _hide.RestorePlan)
        {
            if (entry.DestroyedByFm) continue; // H168 (S2): nothing to read back on a renderer FM destroyed after a confirmed departure
            bool? value = null;
            try { var renderer = _originals.First(o => o.Id == entry.RendererInstanceId).Renderer; if (renderer != null) value = renderer.forceRenderingOff; } catch { value = null; }
            try { _hide.RecordRestoreReadback(entry.RendererInstanceId, value); } catch (Exception e) { _s.CleanupError(e); }
        }
    }

    // Session stop: still-running actors get their stop row, suspension close and restore (every stop path), then owned objects go.
    public void StopSession(string reason, int frame, long now)
    {
        if (!_ended) { AddStopRow(frame, now); _suspension.CloseOnStop(frame, now, Stopwatch.Frequency, reason); }
        RestoreNative(); // H160: native renderers back BEFORE the owned overlay is hidden/destroyed (no-op unless hidden)
        try { if (_ownedRoot != null) _ownedRoot.SetActive(false); } catch (Exception e) { _s.CleanupError(e); }
        foreach (var h in _s.HandlesOf(_actor.ActorInstanceId).ToArray())
        {
            if (!h.Direct) continue;
            try { if (h.Value != null) Object.Destroy(h.Value); } catch (Exception e) { _s.CleanupError(e); }
        }
    }

    public FifaFailureDetail FailureSnapshot(int stoppedFrame) => _obs.Frame == stoppedFrame ? _obs.Snapshot() : null;

    // H164: the exact stop/end frame is always the last timeline row (Kind "stop"); values read on that frame only, null otherwise.
    // No native read here: state fields are the overlay's own last values.
    private void AddStopRow(int frame, long now)
    {
        try
        {
            var o = _obs.Frame == frame ? _obs.Snapshot() : null;
            _timeline.Add(new FifaTimelineRow { Kind = "stop", FrameCount = frame, QpcTicks = now, WallDt = o?.WallDt, MatchTimeSeconds = o?.MatchTime,
                MatchDt = o?.MatchDt, MatchSpeedMetersPerSecond = _motion?.Speed ?? 0, MatchSpeedWindowSeconds = _motion?.WindowSeconds ?? 0,
                FrameDisplacementMeters = o?.DisplacementMeters, Paused = _motion?.Paused ?? false, Clip = _clip, PhaseKeys = _phase, PackFrame = _shownFrame,
                RootPosition = o?.RootPosition });
        }
        catch (Exception e) { Error(e); }
    }

    public FifaActorReport Report() => new()
    {
        ActorInstanceId = _actor.ActorInstanceId, PlayerId = _actor.PlayerId, Name = FifaReportBudget.NameText(_actor.Name), TeamId = _actor.TeamId, ShirtNumber = _actor.ShirtNumber,
        Role = FifaActorRole.Of(_actor.Goalkeeper),
        State = _ended ? "ended" : _suspension.Suspended ? "suspended" : "running", EndReason = _endReason, EndError = _endError, EndFrame = _ended ? _endFrame : null,
        Shown = _shown, PoseUpdates = _poseUpdates, MeshUploads = _meshUploads, ClipUpdates = _clipUpdates, FmHeadToFeetAnchorDistance = _fmHeadToFeet, UniformScale = _scale,
        SuspensionCount = _suspension.Count, Suspensions = _suspension.Records, TimelineRowsTotal = _timeline.Total, TimelineRowsDropped = _timeline.Dropped, Timeline = _timeline.Rows,
        FailureDetail = null, OriginalRendererFlags = Flags(), NativePlayerHidden = _hide.Hidden, HiddenRenderers = _hide.Entries, Cleanup = _endCleanup,
        Locomotion = _loco?.Stats(), MotionTrace = _motionTrace?.Snapshot(),
    };
}

// H160 (3): refusal of a context/flags guard predicate (as opposed to a write, clock, teleport or pack failure).
internal sealed class FifaGuardException : InvalidOperationException
{
    public FifaGuardException(string message) : base(message) { }
}
