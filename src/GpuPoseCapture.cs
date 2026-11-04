using FM.Match.Animation;
using SIShared.Skinning;
using System.Security.Cryptography;
using UnityEngine;
using UnityEngine.Experimental.Rendering;
using UnityEngine.Rendering;
using UnityEngine.SceneManagement;

namespace FM26.VisualDiagnostics;

// A single GPU transport experiment. Never drives animation or the replacement model.
internal sealed class GpuPoseCapture
{
    private bool _armed, _attempted, _pending;
    public bool SemanticOverlapActive => (_armed && !_attempted) || _pending || (_owned != null && !_stableNextFrame);
    private string _requestId;
    public void Arm(string requestId)
    {
        if (_armed || _attempted || string.IsNullOrWhiteSpace(requestId))
            throw new InvalidOperationException("GPU probe is single-shot and requires an explicit request.");
        _requestId = requestId;
        _armed = true;
    }
    private AsyncGPUReadbackRequest _request;
    private Match3DAnimation _owner;
    private CustomAnimationState _state;
    private Texture2D _texture;
    private PlayerBinding _target;
    private int _submittedFrame, _completedFrame, _expectedBytes, _entities, _bones, _width, _height;
    private ushort _characterId;
    private int _entityIndex;
    private float[] _owned;
    private string _hash, _status = "waiting-for-eligible-player", _error;
    private object _ticket;
    private bool _stableNextFrame;
    private Action<string, object> _write;

    public void TryBegin(GameObject root, List<PlayerBinding> players, Action<string, object> write)
    {
        if (!_armed || _attempted || SceneManager.GetActiveScene().name != "MatchPlayback") return;
        var target = players.FirstOrDefault(player => player.Eligible);
        if (root == null || target == null) return;
        var controller = root.GetComponentInParent<FM.Match.MatchPlaybackController>();
        if (controller == null || controller._MatchComponents_k__BackingField == null) return;
        Match3DAnimation owner = null;
        foreach (var component in controller._MatchComponents_k__BackingField)
        {
            var candidate = component == null ? null : component.TryCast<Match3DAnimation>();
            if (candidate == null || !candidate._IsInitialised_k__BackingField
                || candidate.m_isShuttingDown || candidate.m_pluginShutDown || candidate.m_animationState == null) continue;
            if (owner != null) return; // No ambiguous owner selection.
            owner = candidate;
        }
        if (owner == null) return;
        _write = write;
        _owner = owner;
        _state = owner.m_animationState;
        _target = target;
        _entityIndex = target.Player.EntityIndex;
        _characterId = (ushort)target.Player.m_visuals3D.m_characterID;
        _entities = _state._EntityCount_k__BackingField;
        _bones = _state._BoneCount_k__BackingField;
        if (!IdentityMatches()) return;
        _texture = _state.m_boneTransforms;
        if (_texture == null) return;
        _attempted = true; // At most one submitted request; no automatic retry after any failure.
        try
        {
            _width = _texture.width;
            _height = _texture.height;
            var format = _texture.graphicsFormat;
            var supported = SystemInfo.supportsAsyncGPUReadback;
            var formatSupported = SystemInfo.IsFormatSupported(format, GraphicsFormatUsage.ReadPixels);
            _expectedBytes = GpuCapturePolicy.ExpectedByteCount(_entities, _bones, _width, _height,
                _texture.format == TextureFormat.RGBAFloat && format == GraphicsFormat.R32G32B32A32_SFloat,
                supported, formatSupported);
            if (!ContextMatches()) throw new InvalidOperationException("GPU probe context is not stable.");
            _submittedFrame = Time.frameCount;
            var transform = target.Player.transform;
            _ticket = new
            {
                Session = DiagnosticsPlugin.Session, RequestId = _requestId, SubmittedFrame = _submittedFrame,
                SubmissionPhase = "diagnostics-Update-observed-not-a-publication-barrier",
                OwnerInstanceId = owner.GetInstanceID(), StateInstanceId = _state.GetInstanceID(),
                TextureInstanceId = _texture.GetInstanceID(), EntityCount = _entities, BoneCount = _bones,
                Width = _width, Height = _height, TextureFormat = _texture.format.ToString(), GraphicsFormat = format.ToString(),
                Backend = SystemInfo.graphicsDeviceType.ToString(), DeviceSupported = supported, FormatSupported = formatSupported,
                ExpectedBytes = _expectedBytes, MipIndex = 0, RequestCount = 1,
                Target = new {target.ActorInstanceId, target.PlayerId, target.Name, EntityIndex = _entityIndex, CharacterID = _characterId,
                    RootInstanceId = transform.GetInstanceID(), ParentPath = PlayerBinding.HierarchyPath(transform.parent),
                    Position = Position(transform.position), Rotation = Rotation(transform.rotation),
                    LocalPosition = Position(transform.localPosition), LocalScale = Position(transform.localScale)},
            };
            _request = AsyncGPUReadback.Request((Texture)_texture, 0, null);
            _pending = true;
            _status = "pending";
            Publish();
        }
        catch (Exception exception) { Fail("submission-failed", exception); }
    }

    // Called every frame before the slower diagnostics poll. No request.Update or blocking wait.
    public void Tick()
    {
        try
        {
            if (_owned != null && !_stableNextFrame && Time.frameCount > _completedFrame)
            {
                if (Digest(_owned) != _hash) throw new InvalidOperationException("Owned snapshot mutated.");
                _stableNextFrame = true;
                Publish(); // Only re-hash CLR-owned data, never re-read an expired Unity view.
            }
            if (!_pending) return;
            var elapsed = Time.frameCount - _submittedFrame;
            if (elapsed < 0 || elapsed >= GpuCapturePolicy.TimeoutFrames)
            { Fail("timeout", null); return; }
            if (!ContextMatches()) { Fail("context-changed", null); return; }
            if (_request.hasError) { Fail("request-error", null); return; }
            if (!_request.done) return;
            if (!GpuCapturePolicy.CanCopy(true, false, true, elapsed, _request.layerCount,
                _request.layerDataSize, _expectedBytes)) throw new InvalidOperationException("Unexpected GPU layer dimensions.");
            var view = _request.GetData<float>(0);
            if (view is null || view.Length != _expectedBytes / 4)
                throw new InvalidOperationException("Unexpected GPU float count.");
            var owned = GpuCapturePolicy.CopyOwned(view.Length, index => view[index]);
            // No borrowed view is retained or disposed; only this private CLR array survives the frame.
            if (!ContextMatches()) throw new InvalidOperationException("Context changed while copying.");
            _owned = owned;
            _hash = Digest(owned);
            _completedFrame = Time.frameCount;
            _pending = false;
            _status = "COPY_ONLY";
            Publish();
        }
        catch (Exception exception) { Fail("completion-failed", exception); }
    }

    private bool IdentityMatches()
    {
        if (_target?.Player == null || _owner == null || _state == null) return false;
        var player = _target.Player;
        var visual = player.m_visuals3D;
        if (visual == null || !player.IsActive || !player.gameObject.activeInHierarchy
            || visual.IsSubstitute || !visual.m_initialised || visual.m_isDestroying || visual.m_pluginShutDown
            || player.EntityIndex != _entityIndex || (ushort)visual.m_characterID != _characterId) return false;
        var ids = _owner.m_entitiesSIAnimCharactersIDs;
        if (!ids.IsCreated || ids.Length != _entities || _entityIndex < 0 || _entityIndex >= ids.Length) return false;
        var left = visual.m_characters;
        var right = _owner.m_sianimCharacters;
        if (left == null || right == null) return false;
        var lw = left.TryCast<FM.GamePlugin.Bindings.MarshallingWrappers.SI_Anim_ICharacters_NativeWrapper>();
        var rw = right.TryCast<FM.GamePlugin.Bindings.MarshallingWrappers.SI_Anim_ICharacters_NativeWrapper>();
        var sameService = left.Pointer == right.Pointer || PoseOwnerPolicy.HasMatchingNativeService(lw != null, rw != null,
            lw == null ? 0 : lw.instance.ToInt64(), rw == null ? 0 : rw.instance.ToInt64());
        return PoseOwnerPolicy.HasMatchingIdentity(_entityIndex, ids.Length, _characterId, (ushort)ids[_entityIndex], sameService);
    }

    private bool ContextMatches()
        => SceneManager.GetActiveScene().name == "MatchPlayback" && _owner != null
           && _owner._IsInitialised_k__BackingField && !_owner.m_isShuttingDown && !_owner.m_pluginShutDown
           && _owner.m_animationState != null && _owner.m_animationState.Pointer == _state.Pointer
           && _state._EntityCount_k__BackingField == _entities && _state._BoneCount_k__BackingField == _bones
           && _texture != null && _state.m_boneTransforms != null && _state.m_boneTransforms.Pointer == _texture.Pointer
           && _texture.width == _width && _texture.height == _height && IdentityMatches();

    public void Cancel() { if (_pending) Fail("diagnostics-cancelled", null); }

    public object Describe() => new
    {
        Plugin = "FM26.VisualDiagnostics", Version = "0.2.12", Session = DiagnosticsPlugin.Session,
        TimestampUtc = DateTime.UtcNow, RequestId = _requestId, State = _status, Error = _error, Ticket = _ticket,
        CompletedFrame = _completedFrame, LatencyFrames = _owned == null ? (int?)null : _completedFrame - _submittedFrame,
        GpuRequestSucceeded = _owned != null, CopyStableNextFrame = _stableNextFrame,
        GpuPosePayloadCopied = _owned != null, NativeCpuPosePayloadRead = false,
        RootCoherenceVerified = false, PoseDecodingVerified = false, NativePublicationVerified = false,
        VisualReplacementActivated = false,
    };

    private void Publish()
    {
        _write?.Invoke("gpu-status.json", Describe());
        if (_owned == null) return;
        var bytes = GpuCapturePolicy.ToBytes(_owned);
        _write?.Invoke("gpu-snapshot.json", new
        {
            Report = Describe(), Payload = new {Encoding = "float32-le-base64", ByteCount = bytes.Length,
                FloatCount = _owned.Length, Sha256 = _hash, Base64 = Convert.ToBase64String(bytes)},
        });
    }

    private void Fail(string status, Exception error)
    {
        _pending = false;
        _status = status;
        _error = error?.ToString();
        try { _write?.Invoke("gpu-status.json", Describe()); }
        catch (Exception writeError) { DiagnosticsPlugin.Logger.LogError($"[GPUCAPTURE] report failed: {writeError}"); }
        DiagnosticsPlugin.Logger.LogWarning($"[GPUCAPTURE] {status}: {_error ?? "capture rejected"}");
    }

    private static string Digest(float[] owned) => Convert.ToHexString(SHA256.HashData(GpuCapturePolicy.ToBytes(owned))).ToLowerInvariant();
    private static object Position(Vector3 value) => new {X = value.x, Y = value.y, Z = value.z};
    private static object Rotation(Quaternion value) => new {X = value.x, Y = value.y, Z = value.z, W = value.w};
}
