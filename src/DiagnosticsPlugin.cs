using BepInEx;
using BepInEx.Logging;
using BepInEx.Unity.Common;
using BepInEx.Unity.IL2CPP;
using Il2CppInterop.Runtime.Attributes;
using System.Reflection;
using System.Text.Json;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace FM26.VisualDiagnostics;

[BepInPlugin("theo.fm26.visualdiagnostics", "FM26 Visual Diagnostics", "0.2.12")]
public sealed class DiagnosticsPlugin : BasePlugin
{
    internal static ManualLogSource Logger = null!;
    internal static string Output = "";
    internal static string Session = "";

    public override void Load()
    {
        Logger = Log;
        Session = DateTime.UtcNow.ToString("yyyyMMdd-HHmmss");
        Output = Path.Combine(Paths.BepInExRootPath, "visual-diagnostics");
        Directory.CreateDirectory(Output);
        Log.LogInfo("[VISDIAG] LOADING: visual-only inspection; no simulation patches.");
        try
        {
            AddComponent<DiagnosticsBehaviour>();
            AddComponent<FifaOverlayBehaviour>(); // H154: idle unless an opt-in FIFA overlay command is accepted.
            Log.LogInfo("[VISDIAG] COMPONENT_REGISTERED: waiting for its first Update.");
        }
        catch (Exception exception)
        {
            Log.LogError($"[VISDIAG] LOAD_FAILED: {exception}");
            throw;
        }
    }
}

public sealed class DiagnosticsBehaviour : MonoBehaviour
{
    private DateTime _nextPoll = DateTime.MinValue;
    private DateTime _nextCapture = DateTime.MinValue;
    private string _lastScene = "";
    private bool _firstUpdate = true;
    private int _snapshots;
    private int _errors;
    private List<PlayerBinding> _players = new();
    private VisualPrototype _prototype;
    private GameObject _lastMatchRoot;
    private readonly GpuPoseCapture _gpuCapture = new();
    private CandidatePoseOverlay _candidateOverlay;
    private bool _boneInventoryCaptured;
    private readonly SemanticEventObserver _semanticEvents = new();
    private OriginalPassDemo _originalPass;
    private readonly MultiPassCommandHost _multiPass = new();
    private bool SemanticBlocked => _semanticEvents.Enabled || (_originalPass?.OverlapActive ?? false) || _multiPass.Blocked || FifaOverlayHost.Blocked;
    private bool SemanticOverlap => _gpuCapture.SemanticOverlapActive || (_candidateOverlay?.SemanticOverlapActive ?? false) || (_prototype?.SemanticOverlapActive ?? false);

    public void LateUpdate()
    {
        _originalPass?.CleanupTick();
        _semanticEvents.SetCallbackStage("LateUpdate");
        _semanticEvents.Tick(SemanticOverlap);
        _originalPass?.Tick();
        _multiPass.Tick();
        _candidateOverlay?.LateTick();
        try { _prototype?.UpdatePose(); }
        catch (Exception exception)
        {
            DiagnosticsPlugin.Logger.LogError($"[VISDIAG] PROTOTYPE_POSE_ERROR: {exception}");
            _prototype?.Restore("pose-error");
        }
    }

    public void OnDestroy()
    {
        _semanticEvents.Stop("diagnostics-destroyed");
        _originalPass?.Shutdown();
        _multiPass.Shutdown();
        _candidateOverlay?.Stop("diagnostics-destroyed");
        _prototype?.Restore("diagnostics-destroyed");
    }

    public DiagnosticsBehaviour(IntPtr pointer) : base(pointer) { }

    public void Update()
    {
        _originalPass?.CleanupTick();
        _semanticEvents.SetCallbackStage("Update");
        _semanticEvents.Tick(SemanticOverlap); // Deadline/lifetime checks are never behind the inventory/error gate.
        _originalPass?.Tick();
        _multiPass.Tick();
        _candidateOverlay?.Tick(); // Includes actual deferred cleanup after Stop.
        _gpuCapture.Tick(); // Poll readback every frame, before the slow inventory gate.
        var now = DateTime.UtcNow;
        if (now < _nextPoll || _errors >= 3) return;
        _nextPoll = now.AddSeconds(2);
        try
        {
            var scene = SceneManager.GetActiveScene();
            var sceneName = scene.IsValid() ? scene.name : "[invalid]";
            var scenes = new List<string>();
            for (var index = 0; index < SceneManager.sceneCount; index++)
                scenes.Add(SceneManager.GetSceneAt(index).name);
            var root = GameObject.Find("MatchPlaybackController/MatchComponents/Match3DBuilder")
                       ?? GameObject.Find("Match3DBuilder");
            var renderers = root == null ? null : root.GetComponentsInChildren<SkinnedMeshRenderer>(true);
            var meshCount = renderers?.Length ?? 0;
            var canCapture = DiagnosticsPolicy.CanCapture(root != null, meshCount);
            if (_lastMatchRoot != root)
            {
                _candidateOverlay?.Stop("match-root-changed");
                FifaOverlayHost.Stop("match-root-changed");
                _prototype?.Restore("match-root-changed");
                _lastMatchRoot = root;
            }
            if (!SemanticBlocked) _players = canCapture ? PlayerBinding.Collect(root) : new List<PlayerBinding>();
            ProcessSemanticCommand(root);
            _multiPass.Process(root, _players, _semanticEvents, SemanticBlocked, SemanticOverlap);
            FifaOverlayHost.Process(root, _players, SemanticBlocked, _candidateOverlay != null || _prototype != null, WriteJson);
            if (!SemanticBlocked && _players.Count > 0)
                WriteJson("players.json", new
                {
                    Plugin = "FM26.VisualDiagnostics", Version = "0.2.12", Session = DiagnosticsPlugin.Session,
                    TimestampUtc = now, Scene = sceneName, Players = _players.Select(player => player.Describe()).ToArray(),
                });
            if (!SemanticBlocked && _players.Count > 0)
                WriteJson("animation-owner.json", new
                {
                    Plugin = "FM26.VisualDiagnostics", Version = "0.2.12", Session = DiagnosticsPlugin.Session,
                    TimestampUtc = now, Probe = NativePoseAudit.Capture(root, _players),
                });
            if (!SemanticBlocked && !_boneInventoryCaptured && _players.Any(player => player.Eligible))
            {
                try
                {
                    WriteJson("bone-mapping.json", new BoneMappingAudit().Capture(root, _players));
                    _boneInventoryCaptured = true;
                }
                catch (Exception exception)
                {
                    _boneInventoryCaptured = true; // No repeated expensive capture on failure.
                    WriteJson("bone-mapping-error.json", new {Session = DiagnosticsPlugin.Session, TimestampUtc = now,
                        Error = exception.ToString(), PoseDecodingVerified = false});
                }
            }
            ProcessGpuRequest(root);
            ProcessCandidateCommand(root);
            ProcessVisualCommand();
            if (_candidateOverlay != null) WriteJson("candidate-overlay-state.json", _candidateOverlay.Describe());
            WriteJson("gpu-status.json", _gpuCapture.Describe());
            if (_prototype != null) WriteJson("prototype-state.json", _prototype.Describe());
            WriteJson("status.json", new
            {
                Plugin = "FM26.VisualDiagnostics", Version = "0.2.12", Session = DiagnosticsPlugin.Session,
                TimestampUtc = now, UnityVersion = UnityInfo.Version.ToString(),
                State = canCapture ? "match-detected" : "waiting-for-match",
                ActiveScene = sceneName, LoadedScenes = scenes,
                MatchRootFound = root != null, SkinnedMeshRendererCount = meshCount,
                SnapshotsWritten = _snapshots,
            });
            if (_firstUpdate)
            {
                _firstUpdate = false;
                WriteJson($"session-{DiagnosticsPlugin.Session}.json", new
                {
                    Plugin = "FM26.VisualDiagnostics", TimestampUtc = now,
                    UnityVersion = UnityInfo.Version.ToString(), InitialScene = sceneName,
                    Scope = "Inspect visual Unity objects; optional original body test model on one explicit target. No match decisions, trajectory changes, camera changes or save writes.",
                });
                DumpCandidateTypes();
                DiagnosticsPlugin.Logger.LogInfo("[VISDIAG] READY: Update executed and runtime status written.");
            }
            if (sceneName != _lastScene)
            {
                _lastScene = sceneName;
                DiagnosticsPlugin.Logger.LogInfo($"[VISDIAG] SCENE: {sceneName}");
            }
            var request = Path.Combine(DiagnosticsPlugin.Output, "request.txt");
            var requested = File.Exists(request);
            if (requested) File.Delete(request);
            if (!SemanticBlocked && canCapture && (requested || (_snapshots < 3 && now >= _nextCapture)))
            {
                CaptureMatch(root!);
                _snapshots++;
                _nextCapture = now.AddSeconds(15);
            }
            else if (requested)
            {
                WriteJson("request-result.json", new { TimestampUtc = now, Captured = false, Reason = "No populated match renderer root found" });
            }
        }
        catch (Exception exception)
        {
            _errors++;
            DiagnosticsPlugin.Logger.LogError($"[VISDIAG] INSPECTION_ERROR {_errors}/3: {exception}");
            if (_errors >= 3)
            {
                _semanticEvents.Stop("diagnostic-error-limit");
                _candidateOverlay?.Stop("diagnostic-error-limit");
                FifaOverlayHost.Stop("diagnostic-error-limit");
                _gpuCapture.Cancel();
                _prototype?.Restore("diagnostic-error-limit");
                WriteJson("disabled.json", new { TimestampUtc = now, Reason = exception.ToString() });
            }
        }
    }

    [HideFromIl2Cpp]
    private void ProcessSemanticCommand(GameObject root)
    {
        var path = Path.Combine(DiagnosticsPlugin.Output, "semantic-events-command.json");
        if (!File.Exists(path)) return;
        SemanticEventRequest request = null;
        bool startedHere = false;
        try
        {
            byte[] bytes;
            using (var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                if (file.Length > 4096) throw new InvalidOperationException("Oversized semantic command.");
                var buffer = new byte[4097];
                int total = 0, read;
                while (total < buffer.Length && (read = file.Read(buffer, total, buffer.Length - total)) > 0) total += read;
                if (total > 4096) throw new InvalidOperationException("Growing semantic command exceeds bound.");
                bytes = buffer.Take(total).ToArray();
            }
            using var document = JsonDocument.Parse(bytes);
            request = SemanticEventPolicy.Parse(document.RootElement, DiagnosticsPlugin.Session);
            if (request.Action == "start-events")
            {
                if(SemanticBlocked) throw new InvalidOperationException("Semantic/demo observation or cleanup already active.");
                OriginalPassDemo demo=null;
                if(request.OriginalPassDemo)
                {
                    var matches=_players.Where(p=>p.ActorInstanceId==request.ActorInstanceId).ToArray();
                    if(matches.Length!=1) throw new InvalidOperationException("Demo target ambiguous or absent.");
                    demo=new OriginalPassDemo();
                    demo.Prepare(request,root,matches[0]);
                }
                _semanticEvents.Start(request, root, _players, SemanticOverlap);
                startedHere = true;
                if(demo!=null) {_originalPass=demo;demo.Start(_semanticEvents);}
            }
            else
            {
                if (!_semanticEvents.Enabled || request.RequestId != _semanticEvents.RequestId)
                    throw new InvalidOperationException("Stop must identify the current accepted semantic start.");
                _semanticEvents.Stop("requested-stop");
            }
            WriteJson("semantic-events-request-result.json", new
            {
                Plugin = "FM26.VisualDiagnostics", Version = "0.2.12", SchemaVersion = 1,
                Session = request.Session, RequestId = request.RequestId, Action = request.Action,
                ActorInstanceId = request.Action == "start-events" ? (int?)request.ActorInstanceId : null,
                MaximumSeconds = request.Action == "start-events" ? (int?)request.MaximumSeconds : null,
                Budget = request.Action == "start-events" ? SemanticEventObserver.Budget(request.MaximumSeconds) : null,
                OriginalPassDemo = _semanticEvents.OriginalPassDemo,
                AnticipatoryPassDemo = _semanticEvents.AnticipatoryPassDemo,
                TimestampUtc = DateTime.UtcNow, Accepted = true,
            });
        }
        catch (Exception exception)
        {
            if (startedHere) _semanticEvents.Stop("acceptance-write-error", exception.Message);
            WriteJson("semantic-events-request-result.json", new
            {
                Plugin = "FM26.VisualDiagnostics", Version = "0.2.12", SchemaVersion = 1,
                Session = DiagnosticsPlugin.Session, RequestId = request?.RequestId,
                Action = request?.Action, TimestampUtc = DateTime.UtcNow, Accepted = false, Error = exception.Message,
            });
        }
        finally { File.Delete(path); }
    }

    [HideFromIl2Cpp]
    private void ProcessGpuRequest(GameObject root)
    {
        var path = Path.Combine(DiagnosticsPlugin.Output, "gpu-request.json");
        if (File.Exists(path))
        {
            string requestId = null;
            try
            {
                if (new FileInfo(path).Length > 4096) throw new InvalidOperationException("Oversized GPU request.");
                using var document = JsonDocument.Parse(File.ReadAllText(path));
                var request = document.RootElement;
                requestId = request.GetProperty("RequestId").GetString();
                if (request.GetProperty("Session").GetString() != DiagnosticsPlugin.Session
                    || request.GetProperty("Action").GetString() != "capture-once")
                    throw new InvalidOperationException("GPU request action or session mismatch.");
                if (SemanticBlocked) throw new InvalidOperationException("GPU probe refused during semantic capture.");
                _gpuCapture.Arm(requestId);
                WriteJson("gpu-request-result.json", new {Session = DiagnosticsPlugin.Session, RequestId = requestId,
                    TimestampUtc = DateTime.UtcNow, Accepted = true, Scope = "one GPU copy; no visual replacement or simulation writes"});
            }
            catch (Exception exception)
            {
                WriteJson("gpu-request-result.json", new {Session = DiagnosticsPlugin.Session, RequestId = requestId,
                    TimestampUtc = DateTime.UtcNow, Accepted = false, Error = exception.Message});
            }
            finally { File.Delete(path); }
        }
        if (!SemanticBlocked) _gpuCapture.TryBegin(root, _players, WriteJson);
    }

    [HideFromIl2Cpp]
    private void ProcessCandidateCommand(GameObject root)
    {
        var path = Path.Combine(DiagnosticsPlugin.Output, "candidate-overlay-command.json");
        if (!File.Exists(path)) return;
        string requestId = null;
        string action = null;
        try
        {
            if (new FileInfo(path).Length > 4096) throw new InvalidOperationException("Oversized candidate command.");
            using var document = JsonDocument.Parse(File.ReadAllText(path));
            var data = document.RootElement;
            requestId = data.GetProperty("RequestId").GetString();
            if (string.IsNullOrWhiteSpace(requestId) || requestId.Length > 64
                || requestId.Any(c => !((c >= 'a' && c <= 'z') || (c >= 'A' && c <= 'Z')
                    || (c >= '0' && c <= '9') || c == '-' || c == '_')))
                throw new InvalidOperationException("Invalid literal request ID.");
            if (data.GetProperty("Session").GetString() != DiagnosticsPlugin.Session)
                throw new InvalidOperationException("Candidate command belongs to another session.");
            action = data.GetProperty("Action").GetString();
            var hasCadence = data.TryGetProperty("SubmissionIntervalFrames", out var cadence);
            var submissionIntervalFrames = CandidatePosePolicy.SubmissionIntervalFrames;
            if (action == "start-candidate")
            {
                if (SemanticBlocked) throw new InvalidOperationException("Candidate refused during semantic capture.");
                // GetInt32 rejects strings, booleans, null, noninteger numbers and overflow.
                if (hasCadence) submissionIntervalFrames = cadence.GetInt32();
                if (!CandidatePosePolicy.IsSubmissionInterval(submissionIntervalFrames))
                    throw new InvalidOperationException("Submission interval must be exactly 6 or 12 frames.");
                // Validate before allocating an overlay or consuming its one-attempt guard.
                if (_candidateOverlay != null || _prototype != null)
                    throw new InvalidOperationException("One candidate attempt per session; no overlap with body replacement.");
                var actorId = data.GetProperty("ActorInstanceId").GetInt32();
                var matches = _players.Where(p => p.ActorInstanceId == actorId).ToArray();
                if (actorId == 0 || root == null || matches.Length != 1 || !matches[0].Eligible)
                    throw new InvalidOperationException("Target is not exactly one current eligible actor.");
                _candidateOverlay = new CandidatePoseOverlay();
                _candidateOverlay.Start(root, matches[0], requestId, WriteJson, submissionIntervalFrames);
                var state = JsonSerializer.SerializeToElement(_candidateOverlay.Describe());
                if (!state.GetProperty("Enabled").GetBoolean())
                    throw new InvalidOperationException($"Candidate start rejected: {state.GetProperty("Error")}");
            }
            else if (action == "stop-candidate")
            {
                if (hasCadence) throw new InvalidOperationException("Stop must not carry submission cadence.");
                if (data.GetProperty("ActorInstanceId").ValueKind != JsonValueKind.Null)
                    throw new InvalidOperationException("Stop must not carry an actor ID.");
                _candidateOverlay?.Stop("requested-stop");
            }
            else throw new InvalidOperationException("Unsupported candidate action.");
            WriteJson("candidate-overlay-request-result.json", new
            { Plugin = "FM26.VisualDiagnostics", Version = "0.2.12", Session = DiagnosticsPlugin.Session,
                RequestId = requestId, Action = action, TimestampUtc = DateTime.UtcNow, Accepted = true,
                SubmissionIntervalFrames = action == "start-candidate" ? (int?)submissionIntervalFrames : null,
                Scope = "bounded experimental owned geometry/copies; original stays visible; no simulation changes" });
        }
        catch (Exception error)
        {
            WriteJson("candidate-overlay-request-result.json", new
            { Plugin = "FM26.VisualDiagnostics", Version = "0.2.12", Session = DiagnosticsPlugin.Session,
                RequestId = requestId, Action = action, TimestampUtc = DateTime.UtcNow, Accepted = false, Error = error.ToString() });
            DiagnosticsPlugin.Logger.LogError($"[VISDIAG] CANDIDATE_COMMAND_REJECTED: {error}");
        }
        finally { File.Delete(path); }
    }

    [HideFromIl2Cpp]
    private void ProcessVisualCommand()
    {
        var path = Path.Combine(DiagnosticsPlugin.Output, "command.json");
        if (!File.Exists(path)) return;
        string requestId = "";
        try
        {
            using var command = JsonDocument.Parse(File.ReadAllText(path));
            File.Delete(path);
            var data = command.RootElement;
            requestId = data.GetProperty("RequestId").GetString();
            if (data.GetProperty("Session").GetString() != DiagnosticsPlugin.Session)
                throw new InvalidOperationException("Command belongs to a different game session.");
            var action = data.GetProperty("Action").GetString();
            if (action == "restore")
            {
                _prototype?.Restore("requested-restore");
            }
            else if (action == "apply")
            {
                if (SemanticBlocked) throw new InvalidOperationException("Body replacement refused during semantic capture.");
                if (_candidateOverlay != null) throw new InvalidOperationException("Body replacement disabled in candidate-overlay session.");
                var requested = data.GetProperty("ActorInstanceId").GetInt32();
                var selected = VisualPolicy.SelectTarget(requested, _players.Where(player => player.Eligible).Select(player => player.ActorInstanceId).ToArray());
                if (selected == 0) throw new InvalidOperationException("Requested target is not exactly one eligible active player.");
                _prototype?.Restore("replaced-by-new-prototype");
                _prototype = new VisualPrototype(_players.Single(player => player.ActorInstanceId == selected));
            }
            else throw new InvalidOperationException("Unsupported visual command.");
            WriteJson("command-result.json", new { RequestId = requestId, TimestampUtc = DateTime.UtcNow, Success = true, Action = action });
            DiagnosticsPlugin.Logger.LogInfo($"[VISDIAG] VISUAL_COMMAND_OK: {action}; request {requestId}");
        }
        catch (Exception exception)
        {
            if (File.Exists(path)) File.Delete(path);
            WriteJson("command-result.json", new { RequestId = requestId, TimestampUtc = DateTime.UtcNow, Success = false, Error = exception.ToString() });
            DiagnosticsPlugin.Logger.LogError($"[VISDIAG] VISUAL_COMMAND_FAILED: {exception}");
        }
    }

    [HideFromIl2Cpp]
    private void CaptureMatch(GameObject root)
    {
        var meshes = new List<object>();
        var meshErrors = new List<string>();
        var renderers = root.GetComponentsInChildren<SkinnedMeshRenderer>(true);
        foreach (var renderer in renderers)
        {
            if (meshes.Count >= 200) break;
            try
            {
                var mesh = renderer.sharedMesh;
                var bones = new List<object>();
                foreach (var bone in renderer.bones)
                {
                    if (bones.Count >= 256) break;
                    if (bone == null) continue;
                    bones.Add(new { Name = bone.name, Path = ObjectPath(bone), LocalPosition = Vector(bone.localPosition),
                                    LocalRotation = Rotation(bone.localRotation), LocalScale = Vector(bone.localScale) });
                }
                var materials = new List<object>();
                // sharedMaterials reads existing resources without creating material instances.
                foreach (var material in renderer.sharedMaterials)
                {
                    if (material == null) continue;
                    materials.Add(new { Name = material.name, Shader = material.shader == null ? null : material.shader.name });
                }
                meshes.Add(new
                {
                    ObjectPath = ObjectPath(renderer.transform), Name = renderer.name,
                    Active = renderer.gameObject.activeInHierarchy, Enabled = renderer.enabled,
                    Position = Vector(renderer.transform.position), LocalScale = Vector(renderer.transform.localScale),
                    Mesh = mesh == null ? null : new { Name = mesh.name, VertexCount = mesh.vertexCount, SubMeshCount = mesh.subMeshCount },
                    RootBone = renderer.rootBone == null ? null : ObjectPath(renderer.rootBone),
                    BoneCount = renderer.bones.Length, BonesTruncated = renderer.bones.Length > 256,
                    Bones = bones, Materials = materials,
                });
            }
            catch (Exception exception) { meshErrors.Add($"{renderer.name}: {exception.Message}"); }
        }
        var animators = new List<object>();
        foreach (var animator in root.GetComponentsInChildren<Animator>(true))
        {
            if (animators.Count >= 100) break;
            var avatar = animator.avatar;
            var controller = animator.runtimeAnimatorController;
            animators.Add(new { ObjectPath = ObjectPath(animator.transform), Enabled = animator.enabled,
                                Avatar = avatar == null ? null : avatar.name,
                                Controller = controller == null ? null : controller.name });
        }
        var fileName = $"match-{DiagnosticsPlugin.Session}-{_snapshots + 1:D2}.json";
        WriteJson(fileName, new
        {
            Plugin = "FM26.VisualDiagnostics", TimestampUtc = DateTime.UtcNow,
            ActiveScene = SceneManager.GetActiveScene().name, Root = ObjectPath(root.transform),
            RendererCount = renderers.Length, RenderersTruncated = renderers.Length > 200,
            Renderers = meshes, AnimatorCount = root.GetComponentsInChildren<Animator>(true).Length,
            Animators = animators, Errors = meshErrors,
            NativeAnimationBuffers = NativePoseAudit.Capture(root, _players),
        });
        DiagnosticsPlugin.Logger.LogInfo($"[VISDIAG] MATCH_SNAPSHOT: {fileName}; {meshes.Count} meshes inspected.");
    }

    [HideFromIl2Cpp]
    private void DumpCandidateTypes()
    {
        var results = new List<object>();
        var candidates = new HashSet<string> { "PlayerVisuals3D", "AnimatorCharacterVisuals3D", "CharacterSkeleton", "MatchCharacterFactory", "PlayerVisualsSetup" };
        foreach (var assemblyName in new[] { "FM.Match", "FM.Graphics", "SI.Anim", "SIShared.Skinning" })
        {
            try
            {
                var assembly = Assembly.Load(assemblyName);
                foreach (var type in assembly.GetTypes().Where(type => candidates.Contains(type.Name)))
                {
                    results.Add(new { Assembly = assemblyName, Type = type.FullName,
                        Properties = type.GetProperties(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly)
                            .Select(property => new { property.Name, Type = property.PropertyType.FullName }).ToArray(),
                        Methods = type.GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly)
                            .Where(method => !method.IsSpecialName).Select(method => method.ToString()).Take(100).ToArray() });
                }
            }
            catch (Exception exception) { results.Add(new { Assembly = assemblyName, Error = exception.Message }); }
        }
        WriteJson($"candidate-types-{DiagnosticsPlugin.Session}.json", results);
    }

    [HideFromIl2Cpp]
    private static string ObjectPath(Transform transform)
    {
        var names = new List<string>();
        Transform current = transform;
        while (current != null && names.Count < 64)
        {
            names.Add(current.name);
            current = current.parent;
        }
        names.Reverse();
        return string.Join("/", names);
    }

    [HideFromIl2Cpp]
    private static object Vector(Vector3 value) => new { X = value.x, Y = value.y, Z = value.z };

    [HideFromIl2Cpp]
    private static object Rotation(Quaternion value) => new { X = value.x, Y = value.y, Z = value.z, W = value.w };

    [HideFromIl2Cpp]
    private static void WriteJson(string name, object value)
    {
        var target = Path.Combine(DiagnosticsPlugin.Output, name);
        var temporary = target + ".tmp";
        File.WriteAllText(temporary, JsonSerializer.Serialize(value, new JsonSerializerOptions { WriteIndented = true }));
        File.Move(temporary, target, true);
    }
}
