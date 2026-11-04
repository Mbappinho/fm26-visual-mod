using System.Diagnostics;
using System.Text.Json;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.SceneManagement;
using NVector3 = System.Numerics.Vector3;
using Object = UnityEngine.Object;

namespace FM26.VisualDiagnostics;

// Only owned geometry and ordinary actor-root Transform reads. No native pose/bone input.
internal sealed class OriginalPassDemo
{
    private const int MaximumOutputBytes = 16 * 1024 * 1024;
    private readonly List<(Object Value, int Id, string Kind, bool Direct)> _handles = new();
    private readonly List<Transform> _segments = new();
    private readonly List<object> _timeline = new();
    private readonly List<string> _cleanupErrors = new();
    private SemanticEventRequest _request;
    private SemanticEventObserver _reader;
    private PlayerBinding _actor;
    private GameObject _root, _ownedRoot;
    private Transform _actorTransform;
    private Camera _camera;
    private Shader _shader;
    private Mesh _mesh;
    private Material _material;
    private Vector3 _offset;
    private int _scene, _rootId, _actorTransformId, _actorParentId, _cameraId, _thread, _lastFrame = -1;
    private int _startedFrame, _stoppedFrame, _cleanupFrame, _timelineBytes;
    private long _started, _stopped, _cleanupQpc;
    private long? _triggerQpc;
    private object _trigger;
    private readonly OriginalPassSchedule _schedule = new();
    private AnticipatoryPassSchedule _anticipation;
    private string _reason, _error;
    private bool _active, _cleanupRequested, _finalized, _created, _completed;
    private bool _allocationUncertain;
    private bool _cleanupVerified;
    private int _poseUpdates;
    public bool OverlapActive => _active || (_cleanupRequested && !_cleanupVerified);

    // Preflight performs no allocations or FM getters beyond existing ordinary visual path.
    public void Prepare(SemanticEventRequest request, GameObject root, PlayerBinding actor)
    {
        if(!request.OriginalPassDemo || actor==null || !actor.Eligible || actor.PlayerId<=0 || actor.Player==null || root==null)
            throw new InvalidOperationException("Owned demo requires a positive cached PlayerId and exact eligible actor.");
        if(File.Exists(Path.Combine(DiagnosticsPlugin.Output,$"semantic-pass-{request.RequestId}.json")))
            throw new InvalidOperationException("Request-bound demo report already exists.");
        _request=request; _root=root; _actor=actor; _thread=Environment.CurrentManagedThreadId;
        _scene=SceneManager.GetActiveScene().handle; _rootId=root.GetInstanceID();
        _actorTransform=actor.Player.transform; _actorTransformId=_actorTransform.GetInstanceID();
        _actorParentId=_actorTransform.parent==null ? 0 : _actorTransform.parent.GetInstanceID();
        if(actor.Renderers.Count>256) throw new InvalidOperationException("Original renderer bound exceeded.");
        foreach(var original in actor.Renderers)
        {
            if(original==null) throw new InvalidOperationException("Original renderer unavailable.");
            var materials=original.sharedMaterials;
            if(materials.Length>16) throw new InvalidOperationException("Original material slots exceed bound.");
            foreach(var m in materials)
            {
                if(m==null || m.shader==null) continue;
                var shader=m.shader;
                if(shader.name=="Universal Render Pipeline/Unlit") _shader=shader;
                else if(_shader==null && shader.name=="Universal Render Pipeline/Lit") _shader=shader;
            }
        }
        if(_shader==null) throw new InvalidOperationException("No loaded URP shader reference.");
        var cameras=Camera.allCameras;
        if(cameras==null || cameras.Length>32) throw new InvalidOperationException("Camera inventory unavailable/unbounded.");
        var usable=cameras.Where(CameraUsable).ToArray();
        var main=Camera.main;
        _camera=CameraUsable(main) ? main : usable.Length==1 ? usable[0] : null;
        if(_camera==null) throw new InvalidOperationException("No unambiguous usable game camera.");
        _cameraId=_camera.GetInstanceID();
        var right=_camera.transform.right;
        RequireFinite(right);
        if(Math.Abs(right.sqrMagnitude-1f)>.001f) throw new InvalidOperationException("Invalid start camera-right basis.");
        _offset=right*1.5f; // Fixed world offset, never recomputed from later camera movement.
        RequireVisualContext();
    }

    public void Start(SemanticEventObserver reader)
    {
        _reader=reader; _started=reader.StartedQpcTicks; _startedFrame=reader.StartedFrame;
        _active=true;
        reader.SnapshotCopied=Snapshot;
        reader.Stopped=Stop;
        try
        {
            if(_request.AnticipatoryPassDemo)
                _anticipation=new AnticipatoryPassSchedule(_request,_actor.PlayerId,reader.CopiedKnownActors,Stopwatch.Frequency,_started);
            reader.RequireDemoContext(); RequireVisualContext(); CreateOwned(); Tick();
        }
        catch(Exception e) { var cause=OriginalPassPolicy.ClassifyFailure(e,"owned-start-failure"); Stop(cause.Reason,cause.Error); throw; }
    }

    private bool CameraUsable(Camera camera) => camera!=null && camera.enabled && camera.gameObject.activeInHierarchy
        && camera.cameraType==CameraType.Game && camera.targetTexture==null && (camera.cullingMask&1)!=0
        && camera.gameObject.scene.handle==_scene;

    private void RequireVisualContext()
    {
        if(Environment.CurrentManagedThreadId!=_thread || _root==null || !_root.activeInHierarchy || _root.GetInstanceID()!=_rootId
            || _root.scene.handle!=_scene || SceneManager.GetActiveScene().handle!=_scene || SceneManager.GetActiveScene().name!="MatchPlayback"
            || _actorTransform==null || _actorTransform.GetInstanceID()!=_actorTransformId
            || _actorTransform!=_actor.Player.transform || !_actorTransform.IsChildOf(_root.transform)
            || (_actorTransform.parent==null ? 0 : _actorTransform.parent.GetInstanceID())!=_actorParentId
            || !CameraUsable(_camera) || _camera.GetInstanceID()!=_cameraId || _shader==null)
            throw new InvalidOperationException("Owned demo root/actor/camera/scene identity changed.");
        foreach(var h in _handles)
            if(h.Value==null || h.Value.GetInstanceID()!=h.Id) throw new InvalidOperationException("Owned handle lifetime changed.");
        if(_ownedRoot!=null && (_ownedRoot.transform.parent!=null || _ownedRoot.scene.handle!=_scene || _ownedRoot.layer!=0))
            throw new InvalidOperationException("Owned independent root hierarchy/scene/layer changed.");
        foreach(var segment in _segments)
            if(segment==null || segment.parent!=_ownedRoot.transform || segment.gameObject.layer!=0
                || segment.gameObject.scene.handle!=_scene) throw new InvalidOperationException("Owned segment hierarchy changed.");
    }

    private void Snapshot(JsonElement snapshot,long detectionQpc)
    {
        if(!_active) return;
        try
        {
            int frame=Time.frameCount; // Cache the existing callback getter, never add a second Unity read.
            string anticipationBudget=OriginalPassPolicy.UpdateDecision(detectionQpc,_started,Stopwatch.Frequency,_request.MaximumSeconds,
                frame,_lastFrame,true,_timeline.Count);
            if(_anticipation!=null)
            {
                string anticipationDecision=_anticipation.Observe(snapshot,detectionQpc,frame);
                _trigger=_anticipation.Trigger; // FIRST SELECTION snapshot, not actual clip start.
                if(anticipationBudget=="stop-time-budget") Stop("stop-time-budget");
                else if(anticipationDecision=="aborted") Stop("anticipation-abstained");
                return;
            }
            if(anticipationBudget=="stop-time-budget") { Stop("stop-time-budget");return; }
            // OriginalPassPolicy.Trigger is applied by the tested one-shot managed schedule.
            var candidate=_schedule.Detect(snapshot,_actor.PlayerId,detectionQpc);
            if(!candidate.HasValue) return;
            _triggerQpc=detectionQpc;
            _trigger=new {Snapshot=snapshot.Clone(),Event=candidate.Value,DetectionQpcTicks=detectionQpc};
        }
        catch(Exception e) { Stop("owned-trigger-failure",e.ToString()); }
    }

    public void Tick()
    {
        CleanupTick();
        if(!_active) return;
        try
        {
            long now=Stopwatch.GetTimestamp(); int frame=Time.frameCount;
            string decision=OriginalPassPolicy.UpdateDecision(now,_started,Stopwatch.Frequency,_request.MaximumSeconds,frame,_lastFrame,true,_timeline.Count);
            if(decision=="wait")
            {
                if(_anticipation?.Check(now,frame)=="aborted") Stop("anticipation-abstained");
                return;
            }
            if(decision!="update") { Stop(decision);return; }
            _reader.RequireDemoContext(); RequireVisualContext();
            if(!OriginalPassPolicy.OriginalFlagsUnchanged(JsonSerializer.SerializeToElement(_reader.OriginalRendererFlags())))
                throw new InvalidOperationException("Original renderer flags changed or unavailable; no restoration writes.");
            if(_anticipation?.Check(now,frame)=="aborted") {Stop("anticipation-abstained");return;}
            // No actor-root read is performed after the shared observer's deadline.
            _reader.RequireDemoContext();
            RequireWriteDeadline();Vector3 position=_actorTransform.position;
            RequireWriteDeadline();Quaternion rotation=_actorTransform.rotation;
            RequireFinite(position); RequireFinite(rotation);
            _reader.RequireDemoContext();
            now=Stopwatch.GetTimestamp();
            if(_anticipation!=null)
            {
                // Same-frame/context/root/camera/deadline/renderer gates already passed. No native access here.
                RequireWriteDeadline();
                if(_anticipation.TryStart(now,frame,_lastFrame)=="aborted") {Stop("anticipation-abstained");return;}
                _triggerQpc=_anticipation.TriggerQpcTicks;
            }
            double? raw=_triggerQpc.HasValue ? OriginalPassPolicy.Phase(now,_triggerQpc.Value,Stopwatch.Frequency) : null;
            double phase=Math.Clamp(raw??0,0,1);
            var local=OriginalPassPolicy.Pose(phase);
            Vector3 ownedPosition=position+_offset; RequireFinite(ownedPosition);
            var world=local.Select(v=>ownedPosition+rotation*ToUnity(v)).ToArray();
            foreach(var v in world) RequireFinite(v);
            RequireWriteDeadline();_ownedRoot.transform.position=ownedPosition;
            RequireWriteDeadline();_ownedRoot.transform.rotation=rotation;
            for(int i=0;i<_segments.Count;i++)
            {
                var a=ToUnity(local[OriginalPassPolicy.Edges[i*2]]);var b=ToUnity(local[OriginalPassPolicy.Edges[i*2+1]]);var delta=b-a;
                var t=_segments[i];RequireWriteDeadline();t.localPosition=(a+b)*.5f;
                RequireWriteDeadline();t.localRotation=Quaternion.FromToRotation(Vector3.up,delta);
                float radius=i==0 ? .16f : i==2 || i==3 ? .105f : i>=18 ? .06f : .045f;
                RequireWriteDeadline();t.localScale=new Vector3(radius,delta.magnitude,radius);
            }
            RequireWriteDeadline();_ownedRoot.SetActive(true);
            _poseUpdates++;
            RequireWriteDeadline();
            var lengths=Enumerable.Range(0,_segments.Count).Select(i=>NVector3.Distance(local[OriginalPassPolicy.Edges[i*2]],local[OriginalPassPolicy.Edges[i*2+1]])).ToArray();
            var row=new {Sequence=_timeline.Count,FrameCount=frame,QpcTicks=now,PhaseRaw=raw,Phase=phase,
                ActorRootPosition=Vector(position),ActorRootRotation=Rotation(rotation),OwnedRootPosition=Vector(ownedPosition),OwnedRootRotation=Rotation(rotation),
                LocalJoints=local.Select(v=>Vector(ToUnity(v))).ToArray(),WorldJoints=world.Select(Vector).ToArray(),SegmentLengths=lengths};
            int bytes=JsonSerializer.SerializeToUtf8Bytes(row).Length;
            if((long)_timelineBytes+bytes>MaximumOutputBytes-1048576) {Stop("stop-output-budget");return;}
            _timeline.Add(row);_timelineBytes+=bytes;_lastFrame=frame;
            if(raw>=1) {_completed=true;Stop("gesture-completed");}
        }
        catch(Exception e) {var cause=OriginalPassPolicy.ClassifyFailure(e,"owned-update-failure");Stop(cause.Reason,cause.Error);}
    }

    private void RequireWriteDeadline()
    {
        // Managed-only gate, no repeated publisher or actor reads.
        OriginalPassPolicy.RequireWriteWindow(_reader.Enabled,Stopwatch.GetTimestamp(),_started,Stopwatch.Frequency,_request.MaximumSeconds);
    }

    private void Retain(Object value,string kind,bool direct)
    {
        // Capacity is reserved BEFORE each allocation below. Keep the wrapper even if identity lookup fails.
        int index=_handles.Count;_handles.Add((value,0,kind,direct));
        int id=value.GetInstanceID();_handles[index]=(value,id,kind,direct);
    }
    private void Reserve(int count)
    { if(_handles.Count+count>128) throw new InvalidOperationException("Owned handle budget exceeded."); }
    private void CreateOwned()
    {
        _allocationUncertain=true; // Any incomplete creation is conservatively not fully certifiable.
        Reserve(2);_ownedRoot=new GameObject("FM26Test_ORIGINAL_PASS_"+_request.RequestId);Retain(_ownedRoot,"root-game-object",true);
        Retain(_ownedRoot.transform,"root-transform",false);_ownedRoot.SetActive(false);_ownedRoot.layer=0;
        Reserve(1);_mesh=new Mesh();Retain(_mesh,"mesh",true);_mesh.name="FM26Test_OriginalPassPrism";
        var prism=PrismGeometry.Create(8);_mesh.vertices=prism.Vertices.Select(ToUnity).ToArray();_mesh.triangles=prism.Indices;
        _mesh.RecalculateNormals();_mesh.RecalculateBounds();
        Reserve(1);_material=new Material(_shader);Retain(_material,"material",true);_material.name="FM26Test_OriginalPassOwnedCyan";
        if(_material.HasProperty("_BaseColor")) _material.SetColor("_BaseColor",new Color(.02f,1f,.8f,1f));
        if(_material.HasProperty("_Color")) _material.SetColor("_Color",new Color(.02f,1f,.8f,1f));
        for(int i=0;i<OriginalPassPolicy.Edges.Length/2;i++)
        {
            Reserve(4);var part=new GameObject("FM26Test_OriginalPassSegment_"+i);Retain(part,"segment-game-object",true);
            var transform=part.transform;Retain(transform,"segment-transform",false);transform.SetParent(_ownedRoot.transform,false);part.layer=0;
            var filter=part.AddComponent<MeshFilter>();Retain(filter,"mesh-filter",false);filter.sharedMesh=_mesh;
            var ownedRenderer=part.AddComponent<MeshRenderer>();Retain(ownedRenderer,"mesh-renderer",false);ownedRenderer.sharedMaterial=_material;
            ownedRenderer.shadowCastingMode=ShadowCastingMode.Off;ownedRenderer.receiveShadows=false;
            _segments.Add(transform);
        }
        _created=true;
        _allocationUncertain=false;
    }

    public void Stop(string reason,string error=null)
    {
        if(_cleanupRequested) return;
        _active=false;_cleanupRequested=true;_reason=reason;
        // Latch one bounded literal error for both reports, including the existing failure-reason fallback.
        _error=OriginalPassPolicy.CanonicalError(error??(_reader?.Enabled==true && reason!="gesture-completed" && reason!="stop-time-budget" && !AnticipatoryPassPolicy.IsAbstention(_request.AnticipatoryPassDemo,reason) ? reason : null));
        _stopped=Stopwatch.GetTimestamp();_stoppedFrame=Time.frameCount;
        _cleanupFrame=_stoppedFrame;_cleanupQpc=_stopped;
        try {if(_ownedRoot!=null)_ownedRoot.SetActive(false);} catch(Exception e){_cleanupErrors.Add(e.Message);}
        foreach(var h in _handles)
        {
            if(!h.Direct)continue;
            try {if(h.Value!=null)Object.Destroy(h.Value);} catch(Exception e){_cleanupErrors.Add(e.Message);}
        }
        // Reentrant reader stop cannot overwrite the latched original demo reason.
        if(_reader?.Enabled==true)
        {
            if(reason=="gesture-completed" || AnticipatoryPassPolicy.IsAbstention(_request.AnticipatoryPassDemo,reason)) _reader.Stop("requested-stop",_error);
            else if(reason=="stop-time-budget") _reader.Stop(reason,_error);
            else _reader.Stop("owned-demo-failure",_error);
        }
    }

    public void CleanupTick()
    {
        if(!_cleanupRequested || _finalized) return;
        int frame=Time.frameCount;long now=Stopwatch.GetTimestamp();
        if(frame<=_cleanupFrame) return;
        var cleanup=ReadCleanup(frame,now,false);
        if(cleanup.Verified || (long)frame-_cleanupFrame>=120 || (decimal)now-_cleanupQpc >= (decimal)Stopwatch.Frequency*5)
            Publish(cleanup);
    }

    public void Shutdown()
    {
        Stop("diagnostics-destroyed");
        // No later-frame witness can be promised after host destruction. Finalize explicitly unconfirmed.
        if(!_finalized) Publish(ReadCleanup(Time.frameCount,Stopwatch.GetTimestamp(),true));
    }

    private sealed class CleanupResult
    {
        public int RequestedFrame {get;set;} public long RequestedQpcTicks {get;set;}
        public int VerificationFrame {get;set;} public long VerificationQpcTicks {get;set;}
        public bool Verified {get;set;} public int AliveCount {get;set;} public int UnknownCount {get;set;}
        public bool AllocationUncertain {get;set;}
        public object[] Handles {get;set;} public string[] Errors {get;set;}
    }
    private CleanupResult ReadCleanup(int frame,long now,bool forceUnconfirmed)
    {
        int alive=0,unknown=0;
        var handles=_handles.Select(h=>
        {
            string status;
            try {status=h.Id==0 ? "unknown" : h.Value==null ? "dead" : "alive";}
            catch {status="unknown";}
            if(status=="alive")alive++;
            if(status=="unknown")unknown++;
            return (object)new {InstanceId=h.Id,Kind=h.Kind,Status=status};
        }).ToArray();
        return new CleanupResult {RequestedFrame=_cleanupFrame,RequestedQpcTicks=_cleanupQpc,VerificationFrame=frame,VerificationQpcTicks=now,
            // OriginalPassPolicy.CleanupVerified additionally requires a strictly later Unity frame.
            Verified=!forceUnconfirmed && OriginalPassPolicy.CanCertifyCleanup(_cleanupFrame,frame,alive,unknown,_allocationUncertain),AliveCount=alive,UnknownCount=unknown,AllocationUncertain=_allocationUncertain,
            Handles=handles,Errors=_cleanupErrors.ToArray()};
    }
    private void Publish(CleanupResult cleanup)
    {
        _cleanupVerified=cleanup.Verified;
        _finalized=true; // A publication attempt is unique, never overwrite a request-bound final.
        try
        {
            var report=new
            {
                SchemaVersion=_request.AnticipatoryPassDemo ? 2 : 1,Plugin="FM26.VisualDiagnostics",Version="0.2.12",_request.Session,_request.RequestId,_request.ActorInstanceId,OriginalPassDemo=true,
                AnticipatoryPassDemo=_request.AnticipatoryPassDemo,TimingMode=_request.AnticipatoryPassDemo ? AnticipatoryPassPolicy.Rule : null,
                Anticipation=_request.AnticipatoryPassDemo ? _anticipation?.Report ?? AnticipatoryPassPolicy.EmptyReport : null,
                SelectedActor=_reader?.SelectedActor,Budget=new {MaximumSeconds=_request.MaximumSeconds,MaximumTimelineRows=2048,MaximumOwnedHandles=128,MaximumOutputBytes},
                StartedQpcTicks=_started,StartedFrame=_startedFrame,StoppedQpcTicks=_stopped,StoppedFrame=_stoppedFrame,StopReason=_reason,Error=_error,QpcFrequency=Stopwatch.Frequency,
                TriggerRule=_request.AnticipatoryPassDemo ? AnticipatoryPassPolicy.Rule : OriginalPassPolicy.TriggerRule,FootChoice="illustrative-right",Trigger=_trigger,TriggerQpcTicks=_triggerQpc,ClipDurationSeconds=OriginalPassPolicy.ClipDurationSeconds,
                FixedWorldOffset=Vector(_offset),DesignHeight=1.8,JointNames=OriginalPassPolicy.JointNames,Edges=OriginalPassPolicy.Edges,
                OwnedOriginalAnimation=_created && _triggerQpc.HasValue,GestureCompleted=_completed,ManagedPoseUpdates=_poseUpdates,
                RendererWitnessVerified=false,PixelVisibilityVerified=false,NativeActionCommandVerified = false,PerformerRoleVerified=false,EventMembershipVerified=false,
                NativeFootChoiceVerified=false,ContactTimingVerified=false,NativePublicationVerified=false,PoseFidelityVerified=false,VisualReplacementActivated=false,
                SimulationModified=false,ProtectedProducerAccessed=false,NativePosePayloadAccessed=false,
                Context=new {RootInstanceId=_rootId,ActorTransformInstanceId=_actorTransformId,ActorParentInstanceId=_actorParentId,SceneHandle=_scene,CameraInstanceId=_cameraId},ObserverContext=_reader?.ContextIdentity,
                OriginalRendererFlags=_reader?.OriginalRendererFlags(),Timeline=_timeline,Cleanup=cleanup,
            };
            byte[] bytes=JsonSerializer.SerializeToUtf8Bytes(report);
            if(bytes.Length>MaximumOutputBytes) throw new InvalidOperationException("Demo final exceeds output bound.");
            var target=Path.Combine(DiagnosticsPlugin.Output,$"semantic-pass-{_request.RequestId}.json");
            var temporary=target+"."+Guid.NewGuid().ToString("N")+".tmp";
            try
            {
                using(var file=new FileStream(temporary,FileMode.CreateNew,FileAccess.Write,FileShare.None)){file.Write(bytes);file.Flush(true);}
                File.Move(temporary, target, false);
            }
            finally {if(File.Exists(temporary))File.Delete(temporary);}
        }
        catch(Exception e){DiagnosticsPlugin.Logger.LogError($"[VISDIAG] ORIGINAL_PASS_FINAL_REPORT_FAILED: {e}");}
    }
    private static void RequireFinite(Vector3 v)
    {if(!float.IsFinite(v.x)||!float.IsFinite(v.y)||!float.IsFinite(v.z)||v.sqrMagnitude>100000000)throw new InvalidOperationException("Nonfinite/unbounded visual position.");}
    private static void RequireFinite(Quaternion q)
    {if(!float.IsFinite(q.x)||!float.IsFinite(q.y)||!float.IsFinite(q.z)||!float.IsFinite(q.w)||Math.Abs(q.x*q.x+q.y*q.y+q.z*q.z+q.w*q.w-1)>.001f)throw new InvalidOperationException("Invalid visual root quaternion; not repaired.");}
    private static Vector3 ToUnity(NVector3 v)=>new(v.X,v.Y,v.Z);
    private static object Vector(Vector3 v)=>new {X=v.x,Y=v.y,Z=v.z};
    private static object Rotation(Quaternion q)=>new {X=q.x,Y=q.y,Z=q.z,W=q.w};
}
