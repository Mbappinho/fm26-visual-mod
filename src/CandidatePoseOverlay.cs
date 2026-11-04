using FM.Match;
using FM.Match.Animation;
using SIShared.Skinning;
using System.Diagnostics;
using System.Security.Cryptography;
using UnityEngine;
using UnityEngine.Experimental.Rendering;
using UnityEngine.Rendering;
using UnityEngine.SceneManagement;
using NVector3 = System.Numerics.Vector3;
using Object = UnityEngine.Object;
using NativeCharacters = FM.GamePlugin.Bindings.MarshallingWrappers.SI_Anim_ICharacters_NativeWrapper;

namespace FM26.VisualDiagnostics;

// EXPERIMENTAL: mesh index == GPU index, two rows == common-space T/Q, and
// actor.TransformPoint(T.xyz) are hypotheses. No animation/simulation method is called.
// Tick MUST keep running after Stop so deferred Destroy can be read back on a later frame.
internal sealed class CandidatePoseOverlay
{
    private static string _lastAttemptedSession;
    // Indices into the literal RequiredAnchors list, not native bone indices.
    private static readonly int[] Edges = {0,1, 1,2, 1,3, 3,4, 4,5, 1,6, 6,7, 7,8, 0,9, 9,10, 10,11, 0,12, 12,13, 13,14};
    private readonly Stopwatch _clock = new();
    private readonly CandidateContextGuard _contextGuard = new();
    private readonly List<RendererRecord> _originals = new();
    private readonly List<(Object Value, int Id, string Kind)> _ownedHandles = new();
    private readonly List<Object> _destroyTargets = new();
    private readonly List<MeshRenderer> _ownedRenderers = new();
    private readonly List<Transform> _segments = new(), _points = new();
    private readonly List<Sample> _samples = new();
    private readonly HashSet<string> _hashes = new(StringComparer.Ordinal);
    private readonly HashSet<int> _renderedSamples = new();
    private readonly List<string> _cleanupErrors = new();
    private Action<string, object> _write;
    private PlayerBinding _target;
    private Player _actor;
    private GameObject _matchRoot, _ownedRoot;
    private MatchPlaybackController _controller;
    private Match3DAnimation _owner;
    private CustomAnimationState _animationState;
    private Texture2D _texture;
    private SkinnedMeshRenderer _indexSource;
    private Mesh _mesh;
    private Material _material;
    private Shader _shader;
    private Camera _displayCamera;
    private int _displayCameraId;
    private NVector3 _displayOffset;
    private object _lastDisplayEvidence;
    private readonly List<object> _renderEvidence = new();
    private const int MaximumTimelineRows = 2048;
    private readonly List<object> _timeline = new();
    private int _droppedTimelineRows;
    private CandidatePoseBudget _budget = new();
    private CandidatePoseRoi _roi;
    private AsyncGPUReadbackRequest _request;
    private string _session, _requestId, _name, _status = "disabled", _error, _stopReason;
    private string _lod, _backend, _ownedRootName, _lastHash, _cleanupState = "not-created";
    private IntPtr _actorPointer, _visualPointer, _skeletonPointer;
    private long _nativeService;
    private int _actorId, _playerId, _actorTransformId, _actorParentId, _matchRootId, _controllerId;
    private int _sceneHandle, _entities, _bones, _width, _height, _sourceMeshId, _sourceRootBoneId, _sourceRendererId;
    private int _ownerId, _stateId, _textureId;
    private bool _identityTableRead;
    private ushort _characterId;
    private int _entityIndex, _startFrame, _lastSubmissionFrame = -1, _pendingFrame, _completedFrame;
    private int _submitted, _completedRequests, _abandonedPendingRequests, _ownedRootId, _createdRenderers, _ownedRendererWrites, _ownedTransformWrites;
    private int _destroyRequests, _cleanupRequestedFrame, _cleanupVerifiedFrame = -1, _cleanupLastObservedFrame = -1;
    private int _ownedAliveCount, _ownedRenderersAliveCount, _reportFailures, _lateFrames, _lastRenderedSample;
    private int _distinctTransitions, _movingTransitions;
    private double _maximumRelativeDelta, _maximumRelativeRms;
    private bool _used, _enabled, _pending, _cleanupRequested, _cleanupFinished, _objectsRemoved, _geometryAttempted;
    public bool SemanticOverlapActive => _enabled || _pending || (_cleanupRequested && !_cleanupFinished);
    private bool _originalFlagsUnchanged = true, _alwaysDepthProperty;
    private string[] _boneNames;
    private int[] _boneReferences, _boneParents, _namedIndices;
    private NVector3[] _latestPositions, _previousRelative;
    private float[] _firstLengths;
    private object _submissionRoot, _lastRenderRoot;
    private CandidatePoseMotion _lastMotion;

    private sealed class RendererRecord
    {
        public Renderer Value;
        public int Id, MeshId;
        public bool Enabled, Active;
        public int[] MaterialIds, ShaderIds;
    }

    private sealed class Sample
    {
        public int Sequence { get; set; }
        public int SubmittedRequestNumber { get; set; }
        public int SubmissionFrame { get; set; }
        public int CompletionFrame { get; set; }
        public int LatencyFrames { get; set; }
        public DateTime TimestampUtc { get; set; }
        public object SubmissionRoot { get; set; }
        public object CompletionRoot { get; set; }
        public string Encoding => "float32-le-base64";
        public int FloatCount { get; set; }
        public int ByteCount { get; set; }
        public string Sha256 { get; set; }
        public string Base64 { get; set; }
        public bool DistinctFromPreviousCopy { get; set; }
        public bool PoseInterpretationAccepted { get; set; }
        public string RejectionReason { get; set; }
        public object[] NamedCandidatePositions { get; set; }
        public object[] HipsRelativeCandidatePositions { get; set; }
        public float[] SegmentLengths { get; set; }
        public CandidatePoseMotion Motion { get; set; }
    }

    public void Start(GameObject root, PlayerBinding target, string requestId, Action<string, object> write,
        int submissionIntervalFrames = CandidatePosePolicy.SubmissionIntervalFrames)
    {
        if (_used || _cleanupRequested) throw new InvalidOperationException("Candidate overlay is single-attempt per instance and cannot start after shutdown.");
        _budget = new CandidatePoseBudget(submissionIntervalFrames); // Validate before consuming any attempt.
        _used = true;
        _session = DiagnosticsPlugin.Session;
        _requestId = requestId;
        _write = write;
        _target = target;
        _actorId = target?.ActorInstanceId ?? 0;
        _playerId = target?.PlayerId ?? 0;
        _name = target?.Name;
        _startFrame = Time.frameCount;
        _clock.Start();
        try
        {
            if (string.IsNullOrWhiteSpace(_session) || string.IsNullOrWhiteSpace(requestId) || requestId.Length > 128
                || requestId.Any(char.IsControl) || write == null)
                throw new ArgumentException("An explicit current-session request and report writer are required.");
            if (_lastAttemptedSession == _session) throw new InvalidOperationException("This session already attempted a candidate overlay.");
            _lastAttemptedSession = _session;
            if (root == null || target == null || !target.Eligible || target.Player == null || _actorId == 0)
                throw new ArgumentException("An exact eligible actor binding is required; no automatic player selection.");
            _actor = target.Player;
            _actorPointer = _actor.Pointer;
            _visualPointer = _actor.m_visuals3D == null ? IntPtr.Zero : _actor.m_visuals3D.Pointer;
            _matchRoot = root;
            _matchRootId = root.GetInstanceID();
            var scene = SceneManager.GetActiveScene();
            if (scene.name != "MatchPlayback") throw RejectStartContext("active-scene-not-match-playback");
            if (root.scene.handle != scene.handle) throw RejectStartContext("start-match-root-outside-active-scene");
            if (_actor.gameObject.scene.handle != scene.handle) throw RejectStartContext("start-actor-outside-active-scene");
            if (!_actor.transform.IsChildOf(root.transform)) throw RejectStartContext("actor-outside-match-root");
            _sceneHandle = scene.handle;
            _actorTransformId = _actor.transform.GetInstanceID();
            _actorParentId = Id(_actor.transform.parent);
            _controller = root.GetComponentInParent<MatchPlaybackController>();
            if (_controller == null) throw RejectStartContext("controller-unavailable");
            _controllerId = _controller.GetInstanceID();
            _owner = FindOwner();
            if (_owner == null) throw RejectStartContext("unique-current-owner-unavailable");
            _animationState = _owner.m_animationState;
            _ownerId = _owner.GetInstanceID();
            _stateId = _animationState.GetInstanceID();
            _entities = _animationState._EntityCount_k__BackingField;
            _bones = _animationState._BoneCount_k__BackingField;
            _entityIndex = _actor.EntityIndex;
            var visual = _actor.m_visuals3D;
            if (visual == null) throw RejectStartContext("visual-unavailable");
            if (visual.m_skeleton == null) throw RejectStartContext("skeleton-unavailable");
            _characterId = (ushort)visual.m_characterID;
            _skeletonPointer = visual.m_skeleton.Pointer;
            _lod = visual.m_skeleton.LOD.ToString();
            var left = visual.m_characters == null ? null : visual.m_characters.TryCast<NativeCharacters>();
            var right = _owner.m_sianimCharacters == null ? null : _owner.m_sianimCharacters.TryCast<NativeCharacters>();
            if (left == null) throw RejectStartContext("visual-native-wrapper-unavailable");
            if (right == null) throw RejectStartContext("owner-native-wrapper-unavailable");
            var leftInstance = left.instance.ToInt64();
            var rightInstance = right.instance.ToInt64();
            if (!PoseOwnerPolicy.HasMatchingNativeService(true, true, leftInstance, rightInstance))
                throw RejectStartContext(leftInstance == 0 ? "native-service-token-zero" : "native-service-wrapper-token-mismatch");
            _nativeService = left.instance.ToInt64(); // Identity token only. Never dereference it.
            _texture = _animationState.m_boneTransforms;
            if (_texture == null) throw RejectStartContext("texture-unavailable");
            _textureId = _texture.GetInstanceID();
            _width = _texture.width; _height = _texture.height;
            _backend = SystemInfo.graphicsDeviceType.ToString();
            _roi = CurrentRoi();
            CaptureOriginalsAndIndexSource();
            _namedIndices = CandidatePosePolicy.NameIndices(_boneNames, _boneReferences, PlayerBinding.RequiredAnchors, _bones);
            _shader = SelectLoadedShader();
            if (_shader == null) throw new InvalidOperationException("No loaded actor URP Lit/Unlit shader; originals remain untouched.");
            _displayCamera = SelectDisplayCamera();
            if (_displayCamera == null) throw new InvalidOperationException("No unambiguous active game camera for readable ghost.");
            _displayCameraId = _displayCamera.GetInstanceID();
            var cameraRight = _displayCamera.transform.right;
            _displayOffset = GhostDisplayPolicy.DisplayOffset(new NVector3(cameraRight.x,cameraRight.y,cameraRight.z));
            if (!ContextMatches("start")) throw new InvalidOperationException("Initial candidate context is unstable.");
            _enabled = true;
            _status = "armed-experimental";
            Publish();
        }
        catch (Exception error) { _error = error.ToString(); Stop("start-rejected"); }
    }

    // Poll before submitting; never Update/WaitForCompletion/WaitAllRequests or retain a Unity view.
    public void Tick()
    {
        if (_cleanupRequested) { ObserveCleanup(); return; }
        if (!_enabled) return;
        try
        {
            var frame = Time.frameCount;
            var decision = CandidatePosePolicy.DecideForInterval(frame, _startFrame, _lastSubmissionFrame, _submitted,
                _pending, _pendingFrame, _clock.Elapsed.TotalSeconds, _budget.SubmissionIntervalFrames);
            if (decision.StartsWith("stop-", StringComparison.Ordinal)) { Stop(decision); return; }
            if (!ContextMatches("update")) { Stop("context-changed"); return; }
            if (_pending)
            {
                if (_request.hasError) { Stop("gpu-request-error"); return; }
                if (_request.done) CompleteCopy(frame);
            }
            if (!_enabled) return;
            decision = CandidatePosePolicy.DecideForInterval(Time.frameCount, _startFrame, _lastSubmissionFrame, _submitted,
                _pending, _pendingFrame, _clock.Elapsed.TotalSeconds, _budget.SubmissionIntervalFrames);
            if (decision.StartsWith("stop-", StringComparison.Ordinal)) { Stop(decision); return; }
            if (decision == "submit") Submit();
        }
        catch (Exception error) { _error = error.ToString(); Stop("tick-failed"); }
    }

    private void Submit()
    {
        if (!ContextMatches("update") || _pending) throw new InvalidOperationException("Submission context/pending gate failed.");
        _pendingFrame = Time.frameCount;
        _lastSubmissionFrame = _pendingFrame;
        _submissionRoot = RootMetadata(_pendingFrame);
        _submitted++; // Count the native request attempt even if Request throws; never retry it.
        // Exact local IL2CPP overload, no guessed callback or CPU-side pose access.
        _request = AsyncGPUReadback.Request((Texture)_texture, 0, _roi.X, _roi.Width, _roi.Y, _roi.Height,
            _roi.Z, _roi.Depth, null);
        _pending = true;
        _status = "pending-experimental";
        Publish();
    }

    private void CompleteCopy(int frame)
    {
        if (!ContextMatches("update")) throw new InvalidOperationException("Completion context changed.");
        if (!CandidatePosePolicy.CanCopy(_request.done, _request.hasError, frame - _pendingFrame,
            _request.layerCount, _request.layerDataSize, _roi.FloatCount, _roi.FloatCount))
            throw new InvalidOperationException("GPU layer metadata does not match the requested ROI.");
        var view = _request.GetData<float>(0);
        // NativeArray's generated == overload throws for null: use pattern matching.
        if (view is null || !CandidatePosePolicy.CanCopy(_request.done, _request.hasError, frame - _pendingFrame,
            _request.layerCount, _request.layerDataSize, view.Length, _roi.FloatCount))
            throw new InvalidOperationException("GPU completion is not exactly one bounded ROI layer.");
        var raw = CandidatePosePolicy.CopyRoi(_bones, view.Length, i => view[i]);
        // The borrowed view is not stored or disposed. All following work uses CLR-owned data.
        if (!ContextMatches("update")) throw new InvalidOperationException("Context changed during ROI copy.");
        _pending = false;
        _completedRequests++;
        _completedFrame = frame;
        var bytes = GpuCapturePolicy.ToBytes(raw);
        var hash = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
        var sample = new Sample
        {
            Sequence = _samples.Count + 1, SubmittedRequestNumber = _submitted,
            SubmissionFrame = _pendingFrame, CompletionFrame = frame, LatencyFrames = frame - _pendingFrame,
            TimestampUtc = DateTime.UtcNow, SubmissionRoot = _submissionRoot, CompletionRoot = RootMetadata(frame),
            FloatCount = raw.Length, ByteCount = bytes.Length, Sha256 = hash, Base64 = Convert.ToBase64String(bytes),
            DistinctFromPreviousCopy = _samples.Count != 0 && hash != _samples[^1].Sha256,
        };
        _samples.Add(sample); // Includes honestly copied but subsequently rejected candidate interpretations.
        _hashes.Add(hash);
        try
        {
            var positions = CandidatePosePolicy.Positions(raw, _bones, _namedIndices);
            var lengths = CandidatePosePolicy.CheckLengths(positions, Edges, _firstLengths);
            var hipsIndex = Array.IndexOf(PlayerBinding.RequiredAnchors, "Character_Hips");
            var relative = CandidatePosePolicy.RelativeTo(positions, hipsIndex);
            var distinct = _previousRelative != null && hash != _lastHash;
            var motion = _previousRelative == null ? null : CandidatePosePolicy.MeasureMotion(_previousRelative, relative, distinct);
            sample.NamedCandidatePositions = NamedPositions(positions);
            sample.HipsRelativeCandidatePositions = NamedPositions(relative);
            sample.SegmentLengths = lengths;
            sample.Motion = motion;
            sample.PoseInterpretationAccepted = true;
            _firstLengths ??= lengths;
            _latestPositions = positions;
            _previousRelative = relative;
            _lastHash = hash;
            _lastMotion = motion;
            if (distinct)
            {
                _distinctTransitions++;
                if (motion.MovingJointCount > 0) _movingTransitions++;
                _maximumRelativeDelta = Math.Max(_maximumRelativeDelta, motion.MaximumDelta);
                _maximumRelativeRms = Math.Max(_maximumRelativeRms, motion.RmsDelta);
            }
            if (!_geometryAttempted) { _geometryAttempted = true; CreateOwnedGeometry(); }
            else if (_ownedRoot == null) throw new InvalidOperationException("Owned geometry was removed; no recreation/retry is permitted.");
            _status = "candidate-copy-only-overlay";
            Publish();
        }
        catch (Exception error)
        {
            sample.RejectionReason = error.ToString();
            _error = error.ToString();
            Stop("candidate-sanity-or-creation-rejected");
        }
    }

    // Only owned renderer transforms change. No reads of FM bone poses or Animator updates.
    public void LateTick()
    {
        if (!_enabled || _latestPositions == null) return;
        try
        {
            var decision = CandidatePosePolicy.DecideForInterval(Time.frameCount, _startFrame, _lastSubmissionFrame, _submitted,
                _pending, _pendingFrame, _clock.Elapsed.TotalSeconds, _budget.SubmissionIntervalFrames);
            if (decision.StartsWith("stop-", StringComparison.Ordinal)) { Stop(decision); return; }
            if (!ContextMatches("late")) { Stop("late-context-changed"); return; }
            if (_ownedRoot == null)
            { RejectContext("owned-root-unavailable", "late"); Stop("late-context-changed"); return; }
            var actorRoot = _actor.transform;
            if (_displayCamera == null)
            { RejectContext("display-camera-unavailable", "late"); Stop("display-camera-unavailable"); return; }
            if (!_displayCamera.enabled)
            { RejectContext("display-camera-disabled", "late"); Stop("display-camera-unavailable"); return; }
            if (!_displayCamera.gameObject.activeInHierarchy)
            { RejectContext("display-camera-inactive", "late"); Stop("display-camera-unavailable"); return; }
            if (_displayCamera.cameraType != CameraType.Game)
            { RejectContext("display-camera-not-game", "late"); Stop("display-camera-unavailable"); return; }
            if (_displayCamera.targetTexture != null)
            { RejectContext("display-camera-target-texture", "late"); Stop("display-camera-unavailable"); return; }
            if ((_displayCamera.cullingMask & 1) == 0)
            { RejectContext("display-camera-layer-excluded", "late"); Stop("display-camera-unavailable"); return; }
            var unshifted = _latestPositions.Select(p => actorRoot.TransformPoint(new Vector3(p.X, p.Y, p.Z))).ToArray();
            var shifted = GhostDisplayPolicy.ApplyDisplayOffset(unshifted.Select(p => new NVector3(p.x,p.y,p.z)).ToArray(),_displayOffset);
            var world = shifted.Select(p => new Vector3(p.X,p.Y,p.Z)).ToArray();
            foreach (var p in world)
                if (!float.IsFinite(p.x) || !float.IsFinite(p.y) || !float.IsFinite(p.z) || p.sqrMagnitude > 100000000)
                    throw new InvalidOperationException("Invalid transformed candidate positions; no correction.");
            for (var e = 0; e < _segments.Count; e++)
            {
                var a = world[Edges[2 * e]]; var b = world[Edges[2 * e + 1]];
                var direction = b - a;
                if (direction.sqrMagnitude < 0.000001f) throw new InvalidOperationException("Collapsed candidate segment.");
                _segments[e].position = (a + b) * 0.5f;
                _segments[e].rotation = Quaternion.FromToRotation(Vector3.up, direction);
                var radius = GhostDisplayPolicy.SegmentRadius(e);
                _segments[e].localScale = new Vector3(radius, direction.magnitude, radius);
                _ownedTransformWrites += 3;
            }
            for (var n = 0; n < _points.Count; n++)
            {
                _points[n].position = world[n];
                _points[n].rotation = Quaternion.identity;
                var volume = GhostDisplayPolicy.PointScale(n);
                _points[n].localScale = new Vector3(volume.X,volume.Y,volume.Z);
                _ownedTransformWrites += 3;
            }
            _ownedRoot.SetActive(true);
            _lateFrames++;
            _lastRenderedSample = _samples.Count;
            var firstRender = _renderedSamples.Add(_lastRenderedSample);
            _lastRenderRoot = RootMetadata(Time.frameCount);
            // Managed observations of successful owned rendering, not a publication barrier.
            // Keep the first bounded rows; held samples remain distinct render observations.
            if (_timeline.Count < MaximumTimelineRows)
            {
                var renderedSample = _samples[^1];
                _timeline.Add(new
                {
                    Frame = Time.frameCount, QpcTicks = Stopwatch.GetTimestamp(), QpcFrequency = Stopwatch.Frequency,
                    TimestampUtc = DateTime.UtcNow, SampleSequence = renderedSample.Sequence,
                    SubmissionFrame = renderedSample.SubmissionFrame, CompletionFrame = renderedSample.CompletionFrame,
                    HeldAgeFrames = Time.frameCount - renderedSample.CompletionFrame,
                    OriginalRoot = _lastRenderRoot, CameraInstanceId = _displayCameraId,
                    CurrentObservedCamera = new
                    {
                        WorldToCameraMatrix = MatrixElements(_displayCamera.worldToCameraMatrix),
                        ProjectionMatrix = MatrixElements(_displayCamera.projectionMatrix),
                        Position = Position(_displayCamera.transform.position), Rotation = Rotation(_displayCamera.transform.rotation),
                        FieldOfView = _displayCamera.fieldOfView, Orthographic = _displayCamera.orthographic,
                        OrthographicSize = _displayCamera.orthographicSize, Aspect = _displayCamera.aspect,
                        NearClipPlane = _displayCamera.nearClipPlane, FarClipPlane = _displayCamera.farClipPlane,
                        PixelRect = new {X=_displayCamera.pixelRect.x,Y=_displayCamera.pixelRect.y,
                            Width=_displayCamera.pixelRect.width,Height=_displayCamera.pixelRect.height},
                    },
                    CurrentOriginalRootScreen = Position(_displayCamera.WorldToScreenPoint(actorRoot.position)),
                    ScreenWidth = Screen.width, ScreenHeight = Screen.height,
                    DisplayOffset = new {X=_displayOffset.X,Y=_displayOffset.Y,Z=_displayOffset.Z},
                    UnshiftedCandidateScreen = unshifted.Select(p => Position(_displayCamera.WorldToScreenPoint(p))).ToArray(),
                    OwnedDisplayScreen = world.Select(p => Position(_displayCamera.WorldToScreenPoint(p))).ToArray(),
                    DisplayOffsetIsPoseCorrection = false, NativePublicationVerified = false, PoseFidelityVerified = false,
                    QpcEndTicks = Stopwatch.GetTimestamp(),
                });
            }
            else _droppedTimelineRows++;
            if (firstRender)
            {
                _lastDisplayEvidence = new
                {
                Frame = Time.frameCount, SampleSequence = _lastRenderedSample, TimestampUtc = DateTime.UtcNow,
                CameraInstanceId = _displayCameraId, OriginalRoot = _lastRenderRoot,
                CurrentOriginalRootScreen = Position(_displayCamera.WorldToScreenPoint(actorRoot.position)),
                DisplayOffset = new {X=_displayOffset.X,Y=_displayOffset.Y,Z=_displayOffset.Z},
                UnshiftedWorldCandidate = unshifted.Select(Position).ToArray(),
                OwnedDisplayWorld = world.Select(Position).ToArray(),
                OwnedDisplayScreen = world.Select(p => Position(_displayCamera.WorldToScreenPoint(p))).ToArray(),
                ScreenWidth = Screen.width, ScreenHeight = Screen.height,
                DisplayOffsetIsPoseCorrection = false, PoseFidelityVerified = false,
                };
                _renderEvidence.Add(_lastDisplayEvidence);
                Publish();
            }
        }
        catch (Exception error) { _error = error.ToString(); Stop("late-render-failed"); }
    }

    private Match3DAnimation FindOwner()
    {
        if (_controller == null || _controller._MatchComponents_k__BackingField == null) return null;
        Match3DAnimation found = null;
        var count = 0;
        foreach (var component in _controller._MatchComponents_k__BackingField)
        {
            if (++count > 128) return null;
            var candidate = component == null ? null : component.TryCast<Match3DAnimation>();
            if (candidate == null || !candidate._IsInitialised_k__BackingField || candidate.m_isShuttingDown
                || candidate.m_pluginShutDown || candidate.m_animationState == null) continue;
            if (found != null) return null;
            found = candidate;
        }
        return found;
    }

    private CandidatePoseRoi CurrentRoi()
        => CandidatePosePolicy.Roi(_entities, _bones, _texture.width, _texture.height, _entityIndex,
            _texture.format == TextureFormat.RGBAFloat && _texture.graphicsFormat == GraphicsFormat.R32G32B32A32_SFloat,
            SystemInfo.supportsAsyncGPUReadback, SystemInfo.IsFormatSupported(_texture.graphicsFormat, GraphicsFormatUsage.ReadPixels));

    // Failure recording reads only Unity's managed frame counter/QPC and supplied scalar facts.
    // Never inspect another predicate or re-read a producer/object to enrich the failure.
    private InvalidOperationException RejectStartContext(string predicate)
    {
        RejectContext(predicate, "start");
        return new InvalidOperationException("Candidate start context rejected: " + predicate);
    }

    private bool RejectContext(string predicate, string stage, int? subjectInstanceId = null,
        int? expected = null, int? observed = null)
        => _contextGuard.Reject(predicate, stage, Time.frameCount, Stopwatch.GetTimestamp(), Stopwatch.Frequency,
            subjectInstanceId, expected, observed);

    private bool IdentityMatches(string stage)
    {
        if (_actor == null) return RejectContext("actor-unavailable", stage, _actorId);
        if (_actor.Pointer != _actorPointer) return RejectContext("actor-pointer-changed", stage, _actorId);
        if (_actor.gameObject.GetInstanceID() != _actorId) return RejectContext("actor-instance-changed", stage, _actorId);
        if (_actor.ID != _playerId) return RejectContext("player-id-changed", stage, _actorId);
        if (_target == null) return RejectContext("target-binding-unavailable", stage, _actorId);
        if (_target.Player == null) return RejectContext("target-player-unavailable", stage, _actorId);
        if (_target.Player.Pointer != _actorPointer) return RejectContext("target-player-pointer-changed", stage, _actorId);
        if (_target.ActorInstanceId != _actorId) return RejectContext("target-actor-id-changed", stage, _actorId);
        if (_target.PlayerId != _playerId) return RejectContext("target-player-id-changed", stage, _actorId);
        if (_actor.EntityIndex != _entityIndex) return RejectContext("actor-entity-index-changed", stage, _actorId);
        if (!_actor.IsActive) return RejectContext("actor-inactive", stage, _actorId);
        if (!_actor.gameObject.activeInHierarchy) return RejectContext("actor-hierarchy-inactive", stage, _actorId);
        if (_actor.IsGoalkeeper) return RejectContext("actor-is-goalkeeper", stage, _actorId);
        if (_actor.ShirtNumber <= 0) return RejectContext("actor-shirt-number-invalid", stage, _actorId);
        if (_actor.transform.GetInstanceID() != _actorTransformId) return RejectContext("actor-transform-changed", stage, _actorTransformId);
        if (Id(_actor.transform.parent) != _actorParentId) return RejectContext("actor-parent-changed", stage, _actorTransformId);
        var visual = _actor.m_visuals3D;
        if (visual == null) return RejectContext("visual-unavailable", stage, _actorId);
        if (visual.Pointer != _visualPointer) return RejectContext("visual-pointer-changed", stage, _actorId);
        if (!visual.gameObject.activeInHierarchy) return RejectContext("visual-hierarchy-inactive", stage, _actorId);
        if (visual.IsSubstitute) return RejectContext("visual-is-substitute", stage, _actorId);
        if (visual.IsMedic) return RejectContext("visual-is-medic", stage, _actorId);
        if (visual.IsLoading) return RejectContext("visual-loading", stage, _actorId);
        if (!visual.m_initialised) return RejectContext("visual-not-initialized", stage, _actorId);
        if (visual.m_isDestroying) return RejectContext("visual-destroying", stage, _actorId);
        if (visual.m_pluginShutDown) return RejectContext("visual-plugin-shut-down", stage, _actorId);
        if ((ushort)visual.m_characterID != _characterId) return RejectContext("visual-character-id-changed", stage, _actorId);
        if (visual.m_skeleton == null) return RejectContext("skeleton-unavailable", stage, _actorId);
        if (visual.m_skeleton.Pointer != _skeletonPointer) return RejectContext("skeleton-pointer-changed", stage, _actorId);
        if (visual.m_skeleton.LOD.ToString() != _lod) return RejectContext("skeleton-lod-changed", stage, _actorId);
        var ids = _owner.m_entitiesSIAnimCharactersIDs;
        if (!ids.IsCreated) return RejectContext("identity-table-not-created", stage, _ownerId);
        if (ids.Length != _entities) return RejectContext("identity-table-length-changed", stage, _ownerId);
        if (_entityIndex < 0) return RejectContext("entity-index-negative", stage, _actorId);
        if (_entityIndex >= ids.Length) return RejectContext("entity-index-out-of-range", stage, _actorId);
        var left = visual.m_characters == null ? null : visual.m_characters.TryCast<NativeCharacters>();
        var right = _owner.m_sianimCharacters == null ? null : _owner.m_sianimCharacters.TryCast<NativeCharacters>();
        if (left == null) return RejectContext("visual-native-wrapper-unavailable", stage, _actorId);
        if (right == null) return RejectContext("owner-native-wrapper-unavailable", stage, _ownerId);
        var leftInstance = left.instance.ToInt64();
        var rightInstance = right.instance.ToInt64();
        if (!PoseOwnerPolicy.HasMatchingNativeService(true, true, leftInstance, rightInstance))
            return RejectContext(leftInstance == 0 ? "native-service-token-zero" : "native-service-wrapper-token-mismatch", stage);
        if (left.instance.ToInt64() != _nativeService) return RejectContext("native-service-token-changed", stage);
        _identityTableRead = true;
        var tableLength = ids.Length;
        var tableId = (ushort)ids[_entityIndex]; // Only after created/length/index/service checks, as before.
        if (!PoseOwnerPolicy.HasMatchingIdentity(_entityIndex, tableLength, _characterId, tableId, true))
            return RejectContext(_characterId == ushort.MaxValue ? "character-id-sentinel" : "identity-table-character-id-mismatch",
                stage, _actorId, _characterId, tableId);
        return true;
    }

    private bool ContextMatches(string stage)
    {
        var scene = SceneManager.GetActiveScene();
        if (_session != DiagnosticsPlugin.Session) return RejectContext("session-changed", stage);
        if (scene.name != "MatchPlayback") return RejectContext("active-scene-name-changed", stage);
        if (scene.handle != _sceneHandle) return RejectContext("active-scene-handle-changed", stage, expected: _sceneHandle, observed: scene.handle);
        if (_matchRoot == null) return RejectContext("match-root-unavailable", stage, _matchRootId);
        if (_matchRoot.GetInstanceID() != _matchRootId) return RejectContext("match-root-identity-changed", stage, _matchRootId);
        if (!_matchRoot.activeInHierarchy) return RejectContext("match-root-inactive", stage, _matchRootId);
        if (_matchRoot.scene.handle != _sceneHandle) return RejectContext("match-root-scene-changed", stage, _matchRootId);
        if (_controller == null) return RejectContext("controller-unavailable", stage, _controllerId);
        if (_controller.GetInstanceID() != _controllerId) return RejectContext("controller-identity-changed", stage, _controllerId);
        if (_matchRoot.GetComponentInParent<MatchPlaybackController>() == null) return RejectContext("root-controller-unavailable", stage, _controllerId);
        if (_matchRoot.GetComponentInParent<MatchPlaybackController>().Pointer != _controller.Pointer) return RejectContext("root-controller-pointer-changed", stage, _controllerId);
        if (_owner == null) return RejectContext("owner-unavailable", stage, _ownerId);
        if (_owner.GetInstanceID() != _ownerId) return RejectContext("owner-identity-changed", stage, _ownerId);
        if (!_owner._IsInitialised_k__BackingField) return RejectContext("owner-not-initialized", stage, _ownerId);
        if (_owner.m_isShuttingDown) return RejectContext("owner-shutting-down", stage, _ownerId);
        if (_owner.m_pluginShutDown) return RejectContext("owner-plugin-shut-down", stage, _ownerId);
        if (_animationState == null) return RejectContext("animation-state-unavailable", stage, _stateId);
        if (_animationState.GetInstanceID() != _stateId) return RejectContext("animation-state-identity-changed", stage, _stateId);
        if (_owner.m_animationState == null) return RejectContext("owner-animation-state-unavailable", stage, _stateId);
        if (_owner.m_animationState.Pointer != _animationState.Pointer) return RejectContext("owner-animation-state-pointer-changed", stage, _stateId);
        if (_animationState._EntityCount_k__BackingField != _entities) return RejectContext("entity-count-changed", stage, _stateId);
        if (_animationState._BoneCount_k__BackingField != _bones) return RejectContext("bone-count-changed", stage, _stateId);
        if (_texture == null) return RejectContext("texture-unavailable", stage, _textureId);
        if (_texture.GetInstanceID() != _textureId) return RejectContext("texture-identity-changed", stage, _textureId);
        if (_animationState.m_boneTransforms == null) return RejectContext("state-texture-unavailable", stage, _textureId);
        if (_animationState.m_boneTransforms.Pointer != _texture.Pointer) return RejectContext("state-texture-pointer-changed", stage, _textureId);
        if (_texture.width != _width) return RejectContext("texture-width-changed", stage, _textureId);
        if (_texture.height != _height) return RejectContext("texture-height-changed", stage, _textureId);
        if (SystemInfo.graphicsDeviceType.ToString() != _backend) return RejectContext("graphics-backend-changed", stage);
        if (_geometryAttempted)
        {
            if (_ownedRoot == null) return RejectContext("owned-root-unavailable", stage, _ownedRootId);
            if (_ownedRoot.transform.parent != null) return RejectContext("owned-root-parent-changed", stage, _ownedRootId);
            if (_ownedRoot.scene.handle != _sceneHandle) return RejectContext("owned-root-scene-changed", stage, _ownedRootId);
            if (_ownedHandles.Any(h => h.Value == null)) return RejectContext("owned-handle-unavailable", stage);
        }
        var currentOwner = FindOwner();
        if (currentOwner == null) return RejectContext("unique-current-owner-unavailable", stage, _ownerId);
        if (currentOwner.Pointer != _owner.Pointer) return RejectContext("current-owner-pointer-changed", stage, _ownerId);
        if (!IdentityMatches(stage)) return false; // Keep the nested guard's first failure.
        if (_actor.gameObject.scene.handle != _sceneHandle) return RejectContext("actor-scene-changed", stage, _actorId);
        if (!_actor.transform.IsChildOf(_matchRoot.transform)) return RejectContext("actor-outside-match-root", stage, _actorId);
        var roi = CurrentRoi(); // Rechecks exact format/capabilities/dimensions, never just the old descriptor.
        if (roi.ByteCount != _roi.ByteCount) return RejectContext("roi-byte-count-changed", stage, _textureId, _roi.ByteCount, roi.ByteCount);
        if (roi.Y != _roi.Y) return RejectContext("roi-row-changed", stage, _textureId, _roi.Y, roi.Y);
        if (!OriginalsMatch(stage)) return false;
        if (_indexSource == null) return RejectContext("index-source-unavailable", stage, _sourceRendererId);
        if (_indexSource.sharedMesh == null) return RejectContext("index-source-mesh-unavailable", stage, _sourceRendererId);
        if (_indexSource.sharedMesh.GetInstanceID() != _sourceMeshId) return RejectContext("index-source-mesh-changed", stage, _sourceMeshId);
        if (Id(_indexSource.rootBone) != _sourceRootBoneId) return RejectContext("index-source-root-bone-changed", stage, _sourceRootBoneId);
        DescribeBones(_indexSource, out var names, out var references, out var parents);
        if (!CandidatePosePolicy.SameBoneOrder(_boneNames, _boneReferences, names, references)) return RejectContext("mesh-bone-order-changed", stage, _sourceRendererId);
        if (!_boneParents.SequenceEqual(parents)) return RejectContext("mesh-bone-parents-changed", stage, _sourceRendererId);
        return true;
    }

    private void CaptureOriginalsAndIndexSource()
    {
        var renderers = _actor.GetComponentsInChildren<Renderer>(true);
        if (renderers.Length == 0 || renderers.Length > 96) throw new InvalidOperationException("Unbounded/empty original renderer set.");
        var boundIds = _target.Renderers.Select(r => Id(r)).ToHashSet();
        if (boundIds.Count != renderers.Length || renderers.Any(r => !boundIds.Contains(Id(r))))
            throw new InvalidOperationException("Selected binding renderer set is stale.");
        foreach (var renderer in renderers)
        {
            if (renderer == null || renderer.gameObject.name.StartsWith("FM26Test_", StringComparison.Ordinal))
                throw new InvalidOperationException("Another visual test exists on actor; refusing overlap.");
            MaterialIdentities(renderer, out var materialIds, out var shaderIds);
            _originals.Add(new RendererRecord {Value = renderer, Id = renderer.GetInstanceID(), MeshId = MeshId(renderer),
                Enabled = renderer.enabled, Active = renderer.gameObject.activeInHierarchy, MaterialIds = materialIds, ShaderIds = shaderIds});
            var skinned = renderer.TryCast<SkinnedMeshRenderer>();
            if (skinned == null) continue;
            DescribeBones(skinned, out var names, out var references, out var parents);
            if (!PlayerBinding.RequiredAnchors.All(n => names.Contains(n, StringComparer.Ordinal))) continue;
            CandidatePosePolicy.NameIndices(names, references, PlayerBinding.RequiredAnchors, _bones);
            if (_indexSource != null) throw new InvalidOperationException("Multiple complete mesh-index candidates; no ambiguous selection.");
            if (skinned.sharedMesh == null) throw new InvalidOperationException("Index-source mesh absent.");
            _indexSource = skinned; _sourceRendererId = skinned.GetInstanceID();
            _sourceMeshId = skinned.sharedMesh.GetInstanceID(); _sourceRootBoneId = Id(skinned.rootBone);
            _boneNames = names; _boneReferences = references; _boneParents = parents;
        }
        if (_indexSource == null) throw new InvalidOperationException("No unique ordered mesh list containing all required names.");
        if (!_originals.Any(r => r.Enabled && r.Active)) throw new InvalidOperationException("No active enabled original renderer; refusing an invisible original actor.");
    }

    private static void DescribeBones(SkinnedMeshRenderer skinned, out string[] names, out int[] references, out int[] parents)
    {
        var bones = skinned.bones;
        if (bones == null || bones.Length == 0 || bones.Length > 255) throw new InvalidOperationException("Unbounded/empty mesh bone list.");
        names = new string[bones.Length]; references = new int[bones.Length]; parents = new int[bones.Length];
        for (var i = 0; i < bones.Length; i++)
        {
            var bone = bones[i];
            if (bone == null) continue; // Keep the literal null slot; never compact it.
            names[i] = bone.name; references[i] = bone.GetInstanceID(); parents[i] = Id(bone.parent);
        }
    }

    private bool OriginalsMatch(string stage)
    {
        var current = _actor.GetComponentsInChildren<Renderer>(true);
        if (current.Length != _originals.Count) return RejectContext("original-renderer-count-changed", stage, _actorId, _originals.Count, current.Length);
        var ids = current.Select(r => Id(r)).ToHashSet();
        foreach (var item in _originals)
        {
            var renderer = item.Value;
            if (renderer == null) return RejectContext("original-renderer-unavailable", stage, item.Id);
            if (!ids.Contains(item.Id)) return RejectContext("original-renderer-set-changed", stage, item.Id);
            if (renderer.enabled != item.Enabled)
            { _originalFlagsUnchanged = false; return RejectContext("original-renderer-enabled-changed", stage, item.Id); }
            if (renderer.gameObject.activeInHierarchy != item.Active)
            { _originalFlagsUnchanged = false; return RejectContext("original-renderer-active-changed", stage, item.Id); }
            if (MeshId(renderer) != item.MeshId) return RejectContext("original-renderer-mesh-changed", stage, item.Id);
            MaterialIdentities(renderer, out var materialIds, out var shaderIds);
            if (!item.MaterialIds.SequenceEqual(materialIds)) return RejectContext("original-renderer-materials-changed", stage, item.Id);
            if (!item.ShaderIds.SequenceEqual(shaderIds)) return RejectContext("original-renderer-shaders-changed", stage, item.Id);
        }
        return true;
    }

    private static int MeshId(Renderer renderer)
    {
        var skinned = renderer.TryCast<SkinnedMeshRenderer>();
        if (skinned != null) return Id(skinned.sharedMesh);
        var filter = renderer.TryCast<MeshRenderer>() == null ? null : renderer.GetComponent<MeshFilter>();
        return filter == null ? 0 : Id(filter.sharedMesh);
    }

    private static void MaterialIdentities(Renderer renderer, out int[] materialIds, out int[] shaderIds)
    {
        var materials = renderer.sharedMaterials;
        if (materials.Length > 16) throw new InvalidOperationException("Unbounded material slots.");
        materialIds = new int[materials.Length]; shaderIds = new int[materials.Length];
        for (var i = 0; i < materials.Length; i++)
        {
            var material = materials[i];
            materialIds[i] = Id(material); shaderIds[i] = material == null ? 0 : Id(material.shader);
        }
    }

    private Shader SelectLoadedShader()
    {
        Shader lit = null;
        foreach (var item in _originals)
        foreach (var material in item.Value.sharedMaterials)
        {
            if (material == null || material.shader == null) continue;
            if (material.shader.name == "Universal Render Pipeline/Unlit") return material.shader;
            if (material.shader.name == "Universal Render Pipeline/Lit") lit = material.shader;
        }
        return lit; // Never Shader.Find, clone a borrowed material, or write shared actor resources.
    }

    private Camera SelectDisplayCamera()
    {
        bool Usable(Camera camera) => camera != null && camera.enabled && camera.gameObject.activeInHierarchy
            && camera.cameraType == CameraType.Game && camera.targetTexture == null
            && (camera.cullingMask & 1) != 0 && camera.WorldToScreenPoint(_actor.transform.position).z > 0;
        var main = Camera.main;
        if (Usable(main)) return main;
        var cameras = Camera.allCameras;
        if (cameras is null)
        { RejectContext("display-camera-inventory-unavailable", "start"); return null; }
        if (cameras.Length > 32)
        { RejectContext("display-camera-inventory-unbounded", "start", expected: 32, observed: cameras.Length); return null; }
        var eligible = cameras.Where(Usable).ToArray();
        if (eligible.Length == 1) return eligible[0];
        if (eligible.Length == 0)
            RejectContext("display-camera-no-usable-candidate", "start", expected: 1, observed: eligible.Length);
        else RejectContext("display-camera-candidates-ambiguous", "start", expected: 1, observed: eligible.Length);
        return null; // Refuse ambiguity; no camera writes or overrides.
    }

    private void Track(Object value, string kind, bool destroyDirectly)
    {
        _ownedHandles.Add((value, value.GetInstanceID(), kind));
        if (destroyDirectly) _destroyTargets.Add(value);
        _ownedAliveCount = _ownedHandles.Count;
    }

    private void CreateOwnedGeometry()
    {
        _ownedRootName = $"FM26Test_CANDIDATE_UNVERIFIED_{_actorId}_{_requestId}";
        _ownedRoot = new GameObject(_ownedRootName);
        _ownedRootId = _ownedRoot.GetInstanceID();
        Track(_ownedRoot, "root-game-object", true);
        _ownedRoot.SetActive(false);
        // No SetParent on the root: it is independent of the FM actor hierarchy.
        _mesh = new Mesh {name = "FM26Test_CandidateOriginalPrism"};
        Track(_mesh, "mesh", true);
        var geometry = PrismGeometry.Create(8);
        _mesh.vertices = geometry.Vertices.Select(v => new Vector3(v.X, v.Y, v.Z)).ToArray();
        _mesh.triangles = geometry.Indices;
        _mesh.RecalculateNormals(); _mesh.RecalculateBounds();
        _material = new Material(_shader) {name = "FM26Test_CandidateOwnedCyan"};
        Track(_material, "material", true);
        if (_material.HasProperty("_BaseColor")) _material.SetColor("_BaseColor", new Color(0.02f, 1f, 0.8f, 1f));
        if (_material.HasProperty("_Color")) _material.SetColor("_Color", new Color(0.02f, 1f, 0.8f, 1f));
        // Only owned material settings. A loaded shader may not expose a configurable ZTest;
        // report this honestly instead of claiming an always-visible/x-ray overlay.
        _alwaysDepthProperty = _material.HasProperty("_ZTest");
        if (_alwaysDepthProperty) _material.SetInt("_ZTest", (int)CompareFunction.Always);
        if (_material.HasProperty("_ZWrite")) _material.SetInt("_ZWrite", 0);
        _material.renderQueue = 4000;
        for (var e = 0; e < Edges.Length / 2; e++) _segments.Add(AddPart($"Segment_{e}"));
        foreach (var name in PlayerBinding.RequiredAnchors) _points.Add(AddPart("Point_" + name));
    }

    private Transform AddPart(string label)
    {
        var part = new GameObject("FM26Test_Candidate_" + label);
        Track(part, "part-game-object", true);
        part.transform.SetParent(_ownedRoot.transform, false);
        _ownedTransformWrites++;
        var filter = part.AddComponent<MeshFilter>();
        Track(filter, "mesh-filter", false);
        filter.sharedMesh = _mesh;
        var renderer = part.AddComponent<MeshRenderer>();
        Track(renderer, "mesh-renderer", false);
        _ownedRenderers.Add(renderer);
        _createdRenderers++; _ownedRenderersAliveCount++;
        renderer.sharedMaterial = _material;
        renderer.shadowCastingMode = ShadowCastingMode.Off;
        renderer.receiveShadows = false;
        _ownedRendererWrites += 3;
        return part.transform; // No primitive, Collider, Rigidbody, Animator, or gameplay component.
    }

    public void Stop(string reason)
    {
        if (_cleanupRequested) return; // Do not clear reports/metrics or queue repeated destruction.
        _enabled = false;
        if (_pending) _abandonedPendingRequests++;
        _pending = false; // Abandon request: no blocking cancellation or borrowed resource destruction.
        _clock.Stop();
        _stopReason = reason;
        _status = "stopped";
        _cleanupRequested = true;
        _cleanupRequestedFrame = Time.frameCount;
        _cleanupState = "destroy-requested-awaiting-later-frame";
        if (_ownedRoot != null)
        {
            try { _ownedRoot.SetActive(false); }
            catch (Exception error) { _cleanupErrors.Add(error.ToString()); }
        }
        foreach (var value in _destroyTargets)
        {
            if (value == null) continue;
            try { Object.Destroy(value); _destroyRequests++; }
            catch (Exception error) { _cleanupErrors.Add(error.ToString()); }
        }
        // Keep references/instance IDs. Cleared lists are NOT proof that Unity removed objects.
        SafePublish();
    }

    private void ObserveCleanup()
    {
        if (_cleanupFinished || Time.frameCount <= _cleanupRequestedFrame || Time.frameCount == _cleanupLastObservedFrame) return;
        _cleanupLastObservedFrame = Time.frameCount;
        try
        {
            // UnityEngine.Object equality checks actual native-object lifetime, not CLR list membership.
            _ownedAliveCount = _ownedHandles.Count(h => h.Value != null);
            _ownedRenderersAliveCount = _ownedRenderers.Count(r => r != null);
            _objectsRemoved = _ownedAliveCount == 0 && _ownedRenderersAliveCount == 0;
            _cleanupState = _objectsRemoved ? "verified-removed-on-later-frame" : "owned-objects-still-alive";
            if (_objectsRemoved) { _cleanupFinished = true; _cleanupVerifiedFrame = Time.frameCount; }
            else if ((long)Time.frameCount - _cleanupRequestedFrame >= CandidatePosePolicy.TimeoutFrames)
            { _cleanupFinished = true; _cleanupState = "cleanup-verification-timeout"; }
        }
        catch (Exception error)
        {
            _objectsRemoved = false;
            _cleanupErrors.Add(error.ToString());
            _cleanupFinished = true; _cleanupState = "cleanup-readback-failed";
        }
        SafePublish();
    }

    public object Describe() => new
    {
        Plugin = "FM26.VisualDiagnostics", Version = "0.2.12", Session = _session, RequestId = _requestId,
        TimestampUtc = DateTime.UtcNow, ActorInstanceId = _actorId, PlayerId = _playerId, Name = _name,
        State = _status, Enabled = _enabled, StartAttempted = _used, Error = _error, StopReason = _stopReason,
        ContextFailure = _contextGuard.Failure,
        SubmittedRequestCount = _submitted, CompletedRequestCount = _completedRequests, SampleCount = _samples.Count,
        DistinctPayloadCount = _hashes.Count, AcceptedCandidateSampleCount = _samples.Count(s => s.PoseInterpretationAccepted),
        PendingRequest = _pending, AbandonedPendingRequestCount = _abandonedPendingRequests, StartFrame = _startFrame, LastSubmissionFrame = _lastSubmissionFrame,
        LastCompletionFrame = _completedFrame, ElapsedSeconds = _clock.Elapsed.TotalSeconds,
        Budget = new {CandidatePosePolicy.MaximumRequests, SubmissionIntervalFrames = _budget.SubmissionIntervalFrames,
            CandidatePosePolicy.TimeoutFrames, CandidatePosePolicy.MaximumSeconds, MaximumPendingRequests = 1},
        MotionMetrics = new {Origin = "copied Character_Hips, never actor world root", DistinctTransitionCount = _distinctTransitions,
            MovingTransitionCount = _movingTransitions, MaximumHipsRelativeJointDelta = _maximumRelativeDelta,
            MaximumHipsRelativeRmsDelta = _maximumRelativeRms, LastTransition = _lastMotion, ArticulationThreshold = 0.0001},
        OwnedRootName = _ownedRootName, OwnedRootInstanceId = _ownedRootId,
        OwnedRendererCount = _ownedRenderersAliveCount, CreatedOwnedRendererCount = _createdRenderers,
        OwnedGeometryCreationAttemptCount = _geometryAttempted ? 1 : 0,
        OwnedObjectHandleCount = _ownedHandles.Count, OwnedObjectAliveCount = _ownedAliveCount,
        OwnedRendererPropertyWrites = _ownedRendererWrites, OwnedTransformWrites = _ownedTransformWrites,
        OriginalRendererWrites = 0, OriginalTransformWrites = 0, OriginalRenderersHiddenByOverlay = 0,
        OriginalRendererFlagsUnchangedWhileGuarded = _originalFlagsUnchanged, OriginalRendererCount = _originals.Count,
        OriginalActiveEnabledRendererCountAtStart = _originals.Count(r => r.Enabled && r.Active),
        OriginalRendererBaselines = _originals.Select(r => new {InstanceId = r.Id, r.Enabled, ActiveInHierarchy = r.Active, r.MeshId}).ToArray(),
        NativeCpuPoseRead = false, NativeCpuPosePayloadRead = false, NativeCpuIdentityTableRead = _identityTableRead,
        NativeGpuMappingVerified = false, PoseDecodingVerified = false, RootCoherenceVerified = false, NativePublicationVerified = false,
        PoseFidelityVerified = false, VisualReplacementActivated = false, SimulationMethodCalls = 0,
        CleanupState = _cleanupState, OwnedObjectsRemoved = _objectsRemoved, OwnedDestroyRequestCount = _destroyRequests,
        CleanupRequestedFrame = _cleanupRequested ? (int?)_cleanupRequestedFrame : null,
        CleanupObservedFrame = _cleanupLastObservedFrame, CleanupVerifiedFrame = _cleanupVerifiedFrame, CleanupErrors = _cleanupErrors.ToArray(),
        AliveCountsReadBackOnLaterFrame = _cleanupLastObservedFrame > _cleanupRequestedFrame && _cleanupRequested,
        OwnedHandles = _ownedHandles.Select(h => new {InstanceId = h.Id, h.Kind}).ToArray(),
        ReportWriteFailureCount = _reportFailures, UpdatedOwnedRendererFrames = _lateFrames,
        RenderedCompletedSampleCount = _renderedSamples.Count, LastRenderedSample = _lastRenderedSample, LastRenderRoot = _lastRenderRoot,
        LastCompletedSample = _samples.Count == 0 ? null : _samples[^1],
        Roi = _roi, EntityIndex = _entityIndex, CharacterID = _characterId, EntityCount = _entities, BoneCount = _bones,
        MeshIndexSourceRendererInstanceId = _sourceRendererId, MeshInstanceId = _sourceMeshId,
        OwnerInstanceId = _ownerId, AnimationStateInstanceId = _stateId, TextureInstanceId = _textureId,
        TextureWidth = _width, TextureHeight = _height, ActiveSceneHandle = _sceneHandle,
        MatchRootInstanceId = _matchRootId, ActorRootTransformInstanceId = _actorTransformId,
        MeshOrderedBoneNames = _boneNames, MeshOrderedBoneReferences = _boneReferences,
        CandidateNamedMeshIndices = _namedIndices,
        NativeServiceIdentityToken = _nativeService, LodAtStart = _lod, Backend = _backend,
        AlwaysDepthPropertyAvailable = _alwaysDepthProperty, AlwaysVisibleOverlayVerified = false,
        Model = "original-side-by-side-prism-mannequin", DisplayCameraInstanceId = _displayCameraId,
        DisplayOffset = new {X=_displayOffset.X,Y=_displayOffset.Y,Z=_displayOffset.Z},
        DisplayOffsetIsPoseCorrection = false, SameOriginRenderingVerified = false,
        DiagnosticSeparation = "fixed start-camera-right offset for owned visualization only; no pose/height/root correction",
        LastDisplayEvidence = _lastDisplayEvidence, RenderEvidenceCount = _renderEvidence.Count,
        Hypotheses = "UNVERIFIED: rows 2*i/2*i+1 are T/Q; equal mesh/GPU index order; T.xyz common actor space; current actor TransformPoint. No h fallback, interpolation, Q normalization or CPU pose reads.",
        SubmissionPhase = "Update-observed-not-a-native-publication-barrier",
        RootTiming = "Submission/completion/current render roots are separate observations; neither Update nor readback completion is a native publication barrier.",
    };

    // Describe uses cached identities only, remaining safe after actor/source destruction.

    private object RootMetadata(int frame)
    {
        var transform = _actor.transform;
        var matrix = transform.localToWorldMatrix;
        return new {Frame = frame, TransformInstanceId = _actorTransformId, Position = Position(transform.position),
            Rotation = Rotation(transform.rotation), LocalPosition = Position(transform.localPosition),
            LocalScale = Position(transform.localScale), LossyScale = Position(transform.lossyScale),
            LocalToWorldMatrix = new[] {matrix.m00,matrix.m01,matrix.m02,matrix.m03,matrix.m10,matrix.m11,matrix.m12,matrix.m13,
                matrix.m20,matrix.m21,matrix.m22,matrix.m23,matrix.m30,matrix.m31,matrix.m32,matrix.m33}};
    }

    private object[] NamedPositions(NVector3[] positions) => positions.Select((p, i) => (object)new
    {Name = PlayerBinding.RequiredAnchors[i], MeshIndexHypothesis = _namedIndices[i], X = p.X, Y = p.Y, Z = p.Z}).ToArray();
    private static int Id(Object value) => value == null ? 0 : value.GetInstanceID();
    private static float[] MatrixElements(UnityEngine.Matrix4x4 m) => new[]
    {m.m00,m.m01,m.m02,m.m03,m.m10,m.m11,m.m12,m.m13,m.m20,m.m21,m.m22,m.m23,m.m30,m.m31,m.m32,m.m33};
    private static object Position(Vector3 p) => new {X = p.x, Y = p.y, Z = p.z};
    private static object Rotation(Quaternion q) => new {X = q.x, Y = q.y, Z = q.z, W = q.w};

    private void Publish()
    {
        _write?.Invoke("candidate-overlay-state.json", Describe());
        if (_samples.Count > 0) _write?.Invoke("candidate-overlay-samples.json", new
        {Report = Describe(), Samples = _samples.ToArray(), PayloadsAreActualOwnedGpuCopies = true});
        if (_renderEvidence.Count > 0) _write?.Invoke("candidate-overlay-render-evidence.json", new
        {Report = Describe(), Frames = _renderEvidence.ToArray(), DisplayOffsetIsPoseCorrection = false});
        // Export only on the existing publication schedule, including Stop/later cleanup.
        _write?.Invoke("candidate-overlay-timeline.json", new
        {
            Report = Describe(), QpcFrequency = Stopwatch.Frequency, QpcIsHighResolution = Stopwatch.IsHighResolution, MaximumTimelineRows,
            RetainedRowCount = _timeline.Count, DroppedRowCount = _droppedTimelineRows,
            CandidatePointNames = PlayerBinding.RequiredAnchors, Rows = _timeline.ToArray(),
            DisplayOffset = new {X=_displayOffset.X,Y=_displayOffset.Y,Z=_displayOffset.Z},
            DisplayOffsetIsPoseCorrection = false, NativePublicationVerified = false, PoseFidelityVerified = false,
            Timing = "Managed LateTick observations after owned transform writes; not GPU presentation or native publication timing.",
        });
    }
    private void SafePublish()
    {
        try { Publish(); }
        catch (Exception error)
        {
            _reportFailures++;
            DiagnosticsPlugin.Logger?.LogError($"[CANDIDATE-OVERLAY] report failed: {error}");
        }
    }
}
