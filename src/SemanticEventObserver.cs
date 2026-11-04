using FM.Match;
using System.Diagnostics;
using System.Text.Json;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace FM26.VisualDiagnostics;

// Main-thread consumer copies only. No geometry, animation data or game writes.
internal sealed class SemanticEventObserver
{
    private const int MaximumOutputBytes = 8 * 1024 * 1024;
    private static string _acceptedSession;
    private GameObject _root;
    private MatchPlaybackController _controller;
    private Match _publisher;
    private PlayerBinding _actor;
    private int _sceneHandle, _startedFrame, _thread;
    private int _lastFrame = -1;
    private long _started, _last;
    private bool _attempted, _finalized;
    private string _contextPredicate, _contextStage, _callbackStage;
    private SemanticEventRequest _request;
    private object _selected, _known;
    private readonly List<object> _snapshots = new();
    private readonly List<(Renderer Renderer, int Id, bool Enabled, bool Active)> _originals = new();
    private int _snapshotBytes;
    public bool Enabled { get; private set; }
    public bool Attempted => _attempted;
    public string RequestId => _request?.RequestId;
    public bool OriginalPassDemo => _request?.OriginalPassDemo ?? false;
    public bool AnticipatoryPassDemo => _request?.AnticipatoryPassDemo ?? false;
    public JsonElement CopiedKnownActors => JsonSerializer.SerializeToElement(_known);
    public long StartedQpcTicks => _started;
    public int StartedFrame => _startedFrame;
    public object SelectedActor => _selected;
    public object ContextIdentity { get; private set; }
    public Action<JsonElement, long> SnapshotCopied { get; set; }
    public Action<string, string> Stopped { get; set; }
    public Action AdditionalContext { get; set; }
    public Action FinalPublished { get; set; }
    public object[] OriginalRendererFlags() => RendererFlags();
    public void RequireDemoContext()
    {
        if(!Enabled) throw new InvalidOperationException("Semantic reader inactive.");
        RequireBeforeDeadline(); RequireContext("demo-context"); RequireBeforeDeadline();
    }

    public static object Budget(int seconds) => new
    {
        MaximumSeconds = seconds, MinimumPollMilliseconds = 100, MaximumSnapshots = 200,
        MaximumSourceEvents = 8192, MaximumEventsPerSnapshot = 32, MaximumParticipantIds = 16,
        MaximumKnownActors = 64, MaximumOutputBytes,
    };

    private static object Metadata(PlayerBinding actor) => new
    { actor.ActorInstanceId, actor.PlayerId, actor.Name, actor.TeamId, actor.ShirtNumber };

    public void Start(SemanticEventRequest request, GameObject root, List<PlayerBinding> players, bool overlap)
    {
        if (_attempted || Enabled || overlap || _acceptedSession == request.Session) throw new InvalidOperationException("Semantic start already used or another probe is active.");
        var matches = players.Where(p => p.ActorInstanceId == request.ActorInstanceId).ToArray();
        if (request.Action != "start-events" || request.Session != DiagnosticsPlugin.Session || root == null
            || matches.Length != 1 || !matches[0].Eligible)
            throw new InvalidOperationException("Semantic target is not exactly one current eligible signed actor.");
        var controller = root.GetComponentInParent<MatchPlaybackController>();
        var controllers = root.GetComponentsInParent<MatchPlaybackController>(true);
        if (controller == null || controllers.Length != 1 || controllers[0] != controller || controller.m_match == null)
            throw new InvalidOperationException("No unambiguous typed consumer publisher on live root.");
        if (File.Exists(Path.Combine(DiagnosticsPlugin.Output, $"semantic-events-{request.RequestId}.json")))
            throw new InvalidOperationException("Request-bound semantic report already exists.");
        _root = root; _controller = controller; _publisher = controller.m_match; _actor = matches[0];
        _sceneHandle = SceneManager.GetActiveScene().handle;
        if(request.OriginalPassDemo) ContextIdentity = new { SceneHandle = _sceneHandle, RootInstanceId = _root.GetInstanceID(),
            ActorInstanceId = _actor.ActorInstanceId, ControllerInstanceId = _controller.GetInstanceID(),
            PublisherInstanceId = _publisher.GetInstanceID() };
        _thread = Environment.CurrentManagedThreadId;
        _request = request;
        // A rejected pre-start attempt has no final and may be retried. Its
        // diagnostic must never leak into a different request's accepted run.
        _contextPredicate = null; _contextStage = null;
        _selected = Metadata(_actor);
        _known = players.Take(64).Select(Metadata).ToArray();
        _originals.Clear();
        if (_actor.Renderers.Count > 256) throw new InvalidOperationException("Renderer metadata exceeds owned bound.");
        foreach (var renderer in _actor.Renderers)
        {
            if (renderer == null) throw new InvalidOperationException("Original renderer unavailable at start.");
            _originals.Add((renderer, renderer.GetInstanceID(), renderer.enabled, renderer.gameObject.activeInHierarchy));
        }
        RequireContext(); // No commentary/frame reads until all command/context checks succeed.
        if (JsonSerializer.SerializeToUtf8Bytes(new { _selected, _known, OriginalRendererFlags = RendererFlags() }).Length > 65536)
            throw new InvalidOperationException("Cached metadata exceeds final-envelope reserve.");
        _startedFrame = Time.frameCount;
        _started = Stopwatch.GetTimestamp();
        _last = _started - (long)Math.Ceiling(Stopwatch.Frequency / 10m);
        _acceptedSession = request.Session;
        _attempted = true; // Only validated accepted starts consume the process-session allowance.
        Enabled = true;
    }

    public void SetCallbackStage(string stage)
    {
        _callbackStage = stage;
    }

    public void Tick(bool overlap)
    {
        Tick(overlap, _callbackStage);
    }

    public void Tick(bool overlap, string stage)
    {
        if (!Enabled) return;
        _callbackStage = stage;
        try
        {
            if (overlap) throw new InvalidOperationException("Probe overlap detected during semantic capture.");
            RequireContext(stage + ".entry");
            var now = Stopwatch.GetTimestamp();
            var decision = SemanticEventPolicy.Decide(now, _started, _last, Stopwatch.Frequency, _request.MaximumSeconds, _snapshots.Count);
            if (decision.StartsWith("stop-", StringComparison.Ordinal)) { Stop(decision); return; }
            if (decision == "wait") return;
            if (!SemanticEventPolicy.CanCaptureFrame(Time.frameCount, _lastFrame)) return;
            Capture();
        }
        catch (TimeoutException) { Stop("stop-time-budget"); }
        catch (Exception exception) { Stop("context-or-read-error", exception.Message); }
    }

    private void RequireBeforeDeadline()
    {
        if (Environment.CurrentManagedThreadId != _thread) throw new InvalidOperationException("Semantic callback is not on its owner thread.");
        if ((decimal)Stopwatch.GetTimestamp() - _started >= (decimal)Stopwatch.Frequency * _request.MaximumSeconds)
            throw new TimeoutException("Semantic event deadline reached.");
    }

    // Boolean arguments are evaluated BEFORE this CLR-only latch. Never pre-read
    // native properties, split grouped arguments or catch a getter as a refusal.
    private bool ContextRefused(bool refused, string predicate, string stage)
    {
        if (refused && AnticipatoryPassDemo && _contextPredicate == null)
        {
            _contextPredicate = predicate;
            _contextStage = stage;
        }
        return refused;
    }

    private void RequireContext()
    {
        RequireContext("start");
    }

    private void RequireContext(string stage)
    {
        if (ContextRefused(Environment.CurrentManagedThreadId != _thread, "owner-thread", stage)
            || ContextRefused(_root == null, "root-null", stage)
            || ContextRefused(!_root.activeInHierarchy, "root-inactive", stage)
            || ContextRefused(_controller == null, "controller-null", stage)
            || ContextRefused(_publisher == null, "publisher-null", stage)
            || ContextRefused(_actor.Player == null, "actor-null", stage)
            || ContextRefused(SceneManager.GetActiveScene().handle != _sceneHandle, "active-scene-handle", stage)
            || ContextRefused(SceneManager.GetActiveScene().name != "MatchPlayback", "active-scene-name", stage)
            || ContextRefused(!SemanticEventPolicy.SceneAssociation(_sceneHandle, _root.scene.handle,
                _actor.Player.gameObject.scene.handle, _controller.gameObject.scene.handle, _publisher.gameObject.scene.handle), "scene-association", stage)
            || ContextRefused((GameObject.Find("MatchPlaybackController/MatchComponents/Match3DBuilder") ?? GameObject.Find("Match3DBuilder")) != _root, "root-find", stage)
            || ContextRefused(_root.GetComponentInParent<MatchPlaybackController>() != _controller, "root-controller", stage)
            || ContextRefused(_controller.m_match != _publisher, "controller-publisher", stage)
            || ContextRefused(!_controller.gameObject.activeInHierarchy, "controller-inactive", stage)
            || ContextRefused(!_publisher.gameObject.activeInHierarchy, "publisher-inactive", stage)
            || ContextRefused(_actor.Player.gameObject.GetInstanceID() != _request.ActorInstanceId, "actor-instance", stage)
            || ContextRefused(!_actor.Player.transform.IsChildOf(_root.transform), "actor-root-child", stage)
            || ContextRefused(!_actor.Player.gameObject.activeInHierarchy, "actor-inactive", stage)
            || ContextRefused(!_actor.Player.IsActive, "player-inactive", stage)
            || ContextRefused(_actor.Player.m_visuals3D == null, "visuals-null", stage)
            || ContextRefused(_actor.Player.m_visuals3D.IsLoading, "visuals-loading", stage)
            || ContextRefused(_actor.Player.m_visuals3D.IsSubstitute, "visuals-substitute", stage)
            || ContextRefused(_actor.Player.IsGoalkeeper, "player-goalkeeper", stage)
            || ContextRefused(_actor.Player.m_visuals3D.IsMedic, "visuals-medic", stage)
            || ContextRefused(_actor.Player.ID != _actor.PlayerId, "player-id", stage)
            || ContextRefused(_actor.Player.ShirtNumber != _actor.ShirtNumber, "player-shirt", stage)
            || ContextRefused(!_actor.Eligible, "cached-eligible", stage))
            throw new InvalidOperationException("Semantic root/scene/actor/publisher context changed.");
        foreach (var name in PlayerBinding.RequiredAnchors)
            if (ContextRefused(!_actor.Anchors.TryGetValue(name, out var anchor), "anchor-missing", stage)
                || ContextRefused(anchor == null, "anchor-null", stage)
                || ContextRefused(!anchor.IsChildOf(_actor.Player.transform), "anchor-child", stage))
                throw new InvalidOperationException("Semantic actor anchor lifetime changed.");
        var controllers = _root.GetComponentsInParent<MatchPlaybackController>(true);
        if (ContextRefused(controllers.Length != 1, "controller-count", stage)
            || ContextRefused(controllers[0] != _controller, "controller-identity", stage))
            throw new InvalidOperationException("Semantic controller binding became ambiguous.");
        AdditionalContext?.Invoke();
    }

    private T ReadPublished<T>(Func<T> read)
    {
        RequireBeforeDeadline();
        T value = read();
        RequireBeforeDeadline();
        return value;
    }

    private void Capture()
    {
        RequireBeforeDeadline();
        RequireContext(_callbackStage + ".capture-before");
        RequireBeforeDeadline();
        long begin = Stopwatch.GetTimestamp();
        var frame = ReadPublished(() => _publisher.m_matchFrame);
        if (frame == null) throw new InvalidOperationException("Published match frame unavailable.");
        RequireBeforeDeadline();
        int first = ReadPublished(() => frame._CurrentFirstEventIndex_k__BackingField);
        int last = ReadPublished(() => frame._CurrentLastEventIndex_k__BackingField);
        int timeSlice = ReadPublished(() => frame._CurrentTimeSlice_k__BackingField);
        float time = ReadPublished(() => frame._CurrentTime_k__BackingField);
        float speed = ReadPublished(() => frame._CurrentPlaybackSpeed_k__BackingField);
        int mode = ReadPublished(() => (int)frame._CurrentPlaybackMode_k__BackingField);
        int period = ReadPublished(() => (int)frame._Period_k__BackingField);
        if (!float.IsFinite(time) || !float.IsFinite(speed)) throw new InvalidOperationException("Nonfinite published scalar.");
        RequireBeforeDeadline();
        var source = ReadPublished(() => frame._CommentaryEvents_k__BackingField);
        int count = source == null ? 0 : ReadPublished(() => source.Count);
        var window = SemanticEventPolicy.Window(first, count);
        var events = new List<object>();
        for (int index = window.Start; index < window.Start + window.Count; index++)
        {
            RequireBeforeDeadline();
            var value = ReadPublished(() => source[index]);
            if (value == null) throw new InvalidOperationException("Null commentary event entry.");
            RequireBeforeDeadline();
            var ids = ReadPublished(() => value.PlayerUniqueIDs);
            int[] ownedIds = null;
            if (ids != null)
            {
                if (ReadPublished(() => ids.Length) > 16) throw new InvalidOperationException("Participant array exceeds fixed bound.");
                ownedIds = new int[ReadPublished(() => ids.Length)];
                for (int p = 0; p < ownedIds.Length; p++) { RequireBeforeDeadline(); ownedIds[p] = ReadPublished(() => ids[p]); }
            }
            RequireBeforeDeadline();
            var code = ReadPublished(() => value.CommentaryEventID);
            events.Add(new
            {
                SourceIndex = index, CommentaryEventID = (int)code,
                CommentaryEventName = Enum.GetName(typeof(SI.MatchTypes.CommentaryEventDefinitions), code),
                TimeSlice = ReadPublished(() => value.TimeSlice), Duration = ReadPublished(() => value.Duration), ContributorTimeSlice = ReadPublished(() => value.ContributorTimeSlice),
                Flags = ReadPublished(() => (int)value.Flags), PlayerUniqueIDs = ownedIds,
            });
            RequireBeforeDeadline();
        }
        RequireBeforeDeadline();
        int afterCount = source == null ? 0 : ReadPublished(() => source.Count);
        // Re-read publisher's frame/list identity as well as the retained list's count.
        var afterFrame = ReadPublished(() => _publisher.m_matchFrame);
        RequireBeforeDeadline();
        if (afterFrame == null) throw new InvalidOperationException("Published frame disappeared.");
        var afterSource = ReadPublished(() => afterFrame._CommentaryEvents_k__BackingField);
        if ((source == null) != (afterSource == null) || (source != null && source.Pointer != afterSource.Pointer)
            || afterCount != count || (afterSource != null && ReadPublished(() => afterSource.Count) != count))
            throw new InvalidOperationException("Consumer list identity/count changed; snapshot rejected.");
        RequireContext(_callbackStage + ".capture-after");
        RequireBeforeDeadline();
        long end = Stopwatch.GetTimestamp();
        if ((decimal)end - _started >= (decimal)Stopwatch.Frequency * _request.MaximumSeconds)
            throw new TimeoutException("Semantic row completed at or beyond deadline; discarded.");
        int capturedFrame = Time.frameCount;
        var row = new
        {
            Sequence = _snapshots.Count, FrameCount = capturedFrame, QpcTicks = begin, QpcEndTicks = end,
            TimestampUtc = DateTime.UtcNow, CurrentTimeSlice = timeSlice, CurrentTime = time,
            CurrentPlaybackMode = mode, CurrentPlaybackSpeed = speed, MatchPeriod = period,
            CurrentFirstEventIndex = first, CurrentLastEventIndex = last, SourceListNull = source == null,
            SourceCount = source == null ? (int?)null : count, CaptureStartIndex = window.Start,
            WindowTruncated = window.Count < count, Events = events,
        };
        int bytes = JsonSerializer.SerializeToUtf8Bytes(row).Length;
        if ((long)_snapshotBytes + bytes > MaximumOutputBytes - 131072) { Stop("stop-output-budget"); return; }
        _snapshots.Add(row); _snapshotBytes += bytes; _last = begin; _lastFrame = capturedFrame;
        if (_request.OriginalPassDemo)
        {
            // Only serialized already-owned CLR values cross this bridge. No Unity/native references.
            var owned = JsonSerializer.SerializeToElement(row);
            SnapshotCopied?.Invoke(owned, Stopwatch.GetTimestamp());
        }
        if (_snapshots.Count >= 200) Stop("stop-snapshot-budget");
    }

    private object[] RendererFlags() => _originals.Select(item =>
    {
        string status; bool? enabled = null, active = null;
        try
        {
            if (item.Renderer == null) status = "dead";
            else
            {
                enabled = item.Renderer.enabled; active = item.Renderer.gameObject.activeInHierarchy;
                status = enabled == item.Enabled && active == item.Active ? "unchanged" : "changed";
            }
        }
        catch { status = "unavailable"; }
        return (object)new { RendererInstanceId = item.Id, BeforeEnabled = item.Enabled, BeforeActiveInHierarchy = item.Active,
            AfterEnabled = enabled, AfterActiveInHierarchy = active, Status = status };
    }).ToArray();

    public void Stop(string reason, string error = null)
    {
        if (!Enabled || _finalized) return;
        Enabled = false; _finalized = true;
        long stopped = Stopwatch.GetTimestamp();
        try { Stopped?.Invoke(reason, error); }
        catch(Exception callbackError) { DiagnosticsPlugin.Logger.LogError($"[VISDIAG] SEMANTIC_STOP_CALLBACK_FAILED: {callbackError}"); }
        try
        {
            var report = new
            {
                Plugin = "FM26.VisualDiagnostics", Version = "0.2.12", SchemaVersion = 1, OriginalPassDemo = _request.OriginalPassDemo, AnticipatoryPassDemo = _request.AnticipatoryPassDemo,
                Session = _request.Session, RequestId = _request.RequestId, ActorInstanceId = _request.ActorInstanceId,
                TimestampUtc = DateTime.UtcNow, Budget = Budget(_request.MaximumSeconds),
                StartedFrame = _startedFrame, StartedQpcTicks = _started, StoppedFrame = Time.frameCount, StoppedQpcTicks = stopped,
                StopReason = reason, Error = error, Enabled = false, QpcFrequency = Stopwatch.Frequency,
                QpcIsHighResolution = Stopwatch.IsHighResolution, SelectedActor = _selected, KnownActors = _known,
                OriginalRendererFlags = RendererFlags(), Snapshots = _snapshots,
                TechnicalWindowMembershipVerified = false, ActionSemanticsVerified = false, ActorIdentityJoinVerified = false,
                TimeUnitConversionVerified = false, ContactTimingVerified = false, NativePublicationVerified = false,
                VisualReplacementActivated = false, SimulationModified = false, ProtectedProducerAccessed = false,
                NativePosePayloadAccessed = false,
            };
            byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(report);
            if (bytes.Length > MaximumOutputBytes) throw new InvalidOperationException("Final owned report exceeds fixed limit.");
            var target = Path.Combine(DiagnosticsPlugin.Output, $"semantic-events-{_request.RequestId}.json");
            var temporary = target + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                using (var file = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                { file.Write(bytes); file.Flush(true); }
                File.Move(temporary, target, false);
            }
            finally { if (File.Exists(temporary)) File.Delete(temporary); }
            FinalPublished?.Invoke();
            // Separate optional channel, only AFTER the unchanged main final is safe.
            WriteContextDiagnostic(report.StoppedFrame, report.StoppedQpcTicks, report.QpcFrequency, reason, error);
        }
        catch (Exception exception) { DiagnosticsPlugin.Logger.LogError($"[VISDIAG] SEMANTIC_FINAL_REPORT_FAILED: {exception}"); }
    }

    private void WriteContextDiagnostic(int stoppedFrame, long stoppedQpcTicks, long frequency, string reason, string error)
    {
        if (!AnticipatoryPassDemo || _contextPredicate == null || ContextIdentity == null) return;
        bool demo = _contextStage == "demo-context";
        bool reader = _contextStage == "Update.entry" || _contextStage == "LateUpdate.entry"
            || _contextStage == "Update.capture-before" || _contextStage == "LateUpdate.capture-before"
            || _contextStage == "Update.capture-after" || _contextStage == "LateUpdate.capture-after";
        if (!(demo && reason == "owned-demo-failure") && !(reader && reason == "context-or-read-error")) return;
        string expectedError = _contextPredicate.StartsWith("anchor-", StringComparison.Ordinal)
            ? "Semantic actor anchor lifetime changed."
            : _contextPredicate == "controller-count" || _contextPredicate == "controller-identity"
                ? "Semantic controller binding became ambiguous."
                : "Semantic root/scene/actor/publisher context changed.";
        if (demo)
        {
            // OriginalPassDemo preserves Exception.ToString(), unlike Tick's
            // Message. Do not change either old error channel to join them.
            string prefix = "System.InvalidOperationException: " + expectedError;
            if (error == null || !(error == prefix || error.StartsWith(prefix + "\n", StringComparison.Ordinal)
                || error.StartsWith(prefix + "\r\n", StringComparison.Ordinal))) return;
        }
        else if (error != expectedError) return;
        try
        {
            byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(new
            {
                Plugin = "FM26.VisualDiagnostics", Version = "0.2.12", SchemaVersion = 1,
                Diagnostic = "semantic-context-first-refusal", _request.Session, _request.RequestId, _request.ActorInstanceId,
                Stage = _contextStage, Predicate = _contextPredicate, StoppedFrame = stoppedFrame,
                StoppedQpcTicks = stoppedQpcTicks, QpcFrequency = frequency, ExpectedContext = ContextIdentity,
            });
            if (bytes.Length > 4096) throw new InvalidOperationException("Context diagnostic exceeds fixed limit.");
            var target = Path.Combine(DiagnosticsPlugin.Output, $"semantic-context-{_request.RequestId}.json");
            var temporary = target + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                using (var file = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                { file.Write(bytes); file.Flush(true); }
                File.Move(temporary, target, false);
            }
            finally { if (File.Exists(temporary)) File.Delete(temporary); }
        }
        catch (Exception exception)
        {
            // Diagnostics cannot replace the latched failure or the main final.
            try { DiagnosticsPlugin.Logger.LogError($"[VISDIAG] SEMANTIC_CONTEXT_DIAGNOSTIC_FAILED: {exception}"); }
            catch { /* Optional diagnostic logging is best-effort too. */ }
        }
    }
}
