using System.Reflection;
using System.Text.Json;

internal static class SemanticEventPolicyTests
{
    public static void Run()
    {
        var type = Assembly.GetExecutingAssembly().GetType("FM26.VisualDiagnostics.SemanticEventPolicy")
            ?? throw new Exception("FAIL: semantic consumer event policy missing.");
        var sceneAssociation = type.GetMethod("SceneAssociation")
            ?? throw new Exception("FAIL: semantic scene association policy missing.");
        bool Associated(int root, int actor, int controller, int publisher) =>
            (bool)sceneAssociation.Invoke(null, new object[]{7, root, actor, controller, publisher})!;
        if (!Associated(7,7,7,7) || Associated(8,7,7,7) || Associated(7,8,7,7)
            || Associated(7,7,8,7) || Associated(7,7,7,8))
            throw new Exception("FAIL: each semantic object must belong to accepted scene.");
        var parse = type.GetMethod("Parse")!;
        object Parse(string json) { using var d=JsonDocument.Parse(json);return parse.Invoke(null,new object[]{d.RootElement,"session"})!; }
        var valid=Parse("{\"Action\":\"start-events\",\"Session\":\"session\",\"RequestId\":\"id-1\",\"ActorInstanceId\":-27,\"MaximumSeconds\":20}");
        if ((int)valid.GetType().GetProperty("ActorInstanceId")!.GetValue(valid)! != -27)
            throw new Exception("FAIL: literal signed actor not preserved");
        foreach(var json in new[]{
            "{\"Action\":\"start-events\",\"Session\":\"session\",\"RequestId\":\"bad/id\",\"ActorInstanceId\":-27,\"MaximumSeconds\":20}",
            "{\"Action\":\"start-events\",\"Session\":\"session\",\"RequestId\":\"id\",\"ActorInstanceId\":-27,\"MaximumSeconds\":true}",
            "{\"Action\":\"start-events\",\"Session\":\"wrong\",\"RequestId\":\"id\",\"ActorInstanceId\":-27,\"MaximumSeconds\":20}",
            "{\"Action\":\"start-events\",\"Session\":\"session\",\"RequestId\":\"id\",\"ActorInstanceId\":0,\"MaximumSeconds\":20}",
            "{\"Action\":\"start-events\",\"Session\":\"session\",\"RequestId\":\"id\",\"ActorInstanceId\":-27,\"MaximumSeconds\":21}",
            "{\"Action\":\"start-events\",\"Session\":\"session\",\"RequestId\":\"id\",\"ActorInstanceId\":-27,\"MaximumSeconds\":0}",
            "{\"Action\":\"start-events\",\"Session\":\"session\",\"RequestId\":\"id\",\"ActorInstanceId\":-27,\"MaximumSeconds\":20,\"Unknown\":1}",
            "{\"Action\":\"stop-events\",\"Session\":\"session\",\"RequestId\":\"id\",\"ActorInstanceId\":-27}",
            "{\"Action\":\"stop-events\",\"Session\":\"session\",\"RequestId\":\"id\",\"MaximumSeconds\":20}",
            "{\"Action\":\"stop-events\",\"Session\":\"session\",\"RequestId\":\"id\",\"RequestId\":\"other\"}"})
        {
            bool rejected=false;
            try {Parse(json);}catch(TargetInvocationException){rejected=true;}
            if(!rejected)throw new Exception("FAIL: malformed semantic command accepted: "+json);
        }
        Parse("{\"Action\":\"stop-events\",\"Session\":\"session\",\"RequestId\":\"id\"}");
        var decide=type.GetMethod("Decide") ?? throw new Exception("FAIL: semantic monotonic budget policy missing.");
        string Decide(long now,long start,long last,int count) => (string)decide.Invoke(null,new object[]{now,start,last,1000L,20,count})!;
        if(Decide(20000,0,-100,0)!="stop-time-budget" || Decide(19999,0,19899,199)!="capture"
            || Decide(19999,0,19900,199)!="wait" || Decide(100,0,0,200)!="stop-snapshot-budget")
            throw new Exception("FAIL: semantic deadline/spacing/counter policy");
        var window=type.GetMethod("Window") ?? throw new Exception("FAIL: semantic source window policy missing.");
        var framePolicy=type.GetMethod("CanCaptureFrame") ?? throw new Exception("FAIL: semantic same-frame policy missing.");
        bool CanCaptureFrame(int frame,int lastFrame) => (bool)framePolicy.Invoke(null,new object[]{frame,lastFrame})!;
        if(Decide(100,0,0,1)!="capture" || CanCaptureFrame(11,11) || CanCaptureFrame(10,11)
            || !CanCaptureFrame(12,11) || !CanCaptureFrame(0,-1))
            throw new Exception("FAIL: spaced Update/LateUpdate must not capture the same Unity frame.");
        foreach(var c in new[]{(int.MinValue,50,0,32),(int.MaxValue,50,49,1),(-1,0,0,0),(10,50,9,32),(0,1,0,1)})
        {
            var w=window.Invoke(null,new object[]{c.Item1,c.Item2})!;
            if((int)w.GetType().GetProperty("Start")!.GetValue(w)! !=c.Item3 || (int)w.GetType().GetProperty("Count")!.GetValue(w)! !=c.Item4)
                throw new Exception("FAIL: technical event window overflow/bounds");
        }
        foreach(var count in new[]{-1,8193})
        {bool rejected=false;try{window.Invoke(null,new object[]{0,count});}catch(TargetInvocationException){rejected=true;}
         if(!rejected)throw new Exception("FAIL: source count outside cap accepted");}
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !File.Exists(Path.Combine(dir.FullName, "FM26.VisualDiagnostics.csproj"))) dir = dir.Parent;
        if (dir == null) throw new Exception("FAIL: source root not found");
        var observerPath = Path.Combine(dir.FullName,"src","SemanticEventObserver.cs");
        if (!File.Exists(observerPath)) throw new Exception("FAIL: bounded typed semantic observer missing");
        var source = File.ReadAllText(observerPath);
        var frameGuard=source.IndexOf("if (!SemanticEventPolicy.CanCaptureFrame(Time.frameCount, _lastFrame)) return;",StringComparison.Ordinal);
        if(frameGuard<0 || frameGuard>source.IndexOf("            Capture();",StringComparison.Ordinal)
            || !source.Contains("_lastFrame = capturedFrame",StringComparison.Ordinal)
            || !source.Contains("FrameCount = capturedFrame",StringComparison.Ordinal)
            || !source.Contains("_lastFrame = -1",StringComparison.Ordinal))
            throw new Exception("FAIL: explicit last-frame guard/retention missing.");
        foreach (var token in new[]{"GetComponentInParent<MatchPlaybackController>","m_matchFrame","_CommentaryEvents_k__BackingField","RequireBeforeDeadline();","SemanticEventPolicy.Window","SourceListNull","NativePublicationVerified = false","File.Move(temporary, target, false)","OriginalRendererFlags"})
            if (!source.Contains(token,StringComparison.Ordinal)) throw new Exception("FAIL: semantic source wiring missing "+token);
        if (!source.Contains("private T ReadPublished<T>(Func<T> read)",StringComparison.Ordinal)
            || !source.Contains("ReadPublished(() => _publisher.m_matchFrame)",StringComparison.Ordinal))
            throw new Exception("FAIL: each published native read must be deadline-guarded");
        if (!source.Contains("_acceptedSession = request.Session",StringComparison.Ordinal))
            throw new Exception("FAIL: accepted semantic attempt must be latched per process session");
        foreach (var guard in new[]{"PlayerBinding.RequiredAnchors", "anchor == null", "if ((decimal)end - _started"})
            if (!source.Contains(guard,StringComparison.Ordinal)) throw new Exception("FAIL: actor anchor lifetime / retained deadline guard missing "+guard);
        foreach (var scene in new[]{"_root.scene.handle", "_actor.Player.gameObject.scene.handle", "_controller.gameObject.scene.handle", "_publisher.gameObject.scene.handle", "SemanticEventPolicy.SceneAssociation(_sceneHandle"})
            if (!source.Contains(scene,StringComparison.Ordinal)) throw new Exception("FAIL: semantic scene guard wiring missing "+scene);
        var contextStart = source.IndexOf("private void RequireContext()",StringComparison.Ordinal);
        var sceneGuard = source.IndexOf("SemanticEventPolicy.SceneAssociation(_sceneHandle",contextStart,StringComparison.Ordinal);
        var publishedRead = source.IndexOf("private T ReadPublished",StringComparison.Ordinal);
        if (sceneGuard < contextStart || sceneGuard >= publishedRead
            || source.IndexOf("RequireContext(); // No commentary/frame reads",StringComparison.Ordinal) < 0)
            throw new Exception("FAIL: scene guard must precede event reads and run on context checks.");
        foreach (var forbidden in new[]{"GetService", "new GameObject", "renderer.enabled =", "il2cpp_runtime_invoke", "GetTimeSlice()"})
            if (source.Contains(forbidden,StringComparison.Ordinal)) throw new Exception("FAIL: forbidden semantic observer scope "+forbidden);
        if((string)decide.Invoke(null,new object[]{long.MaxValue,long.MinValue,long.MinValue,1000L,20,0})! != "stop-time-budget"
            || (string)decide.Invoke(null,new object[]{0L,0L,long.MinValue,long.MaxValue,20,0})! != "capture")
            throw new Exception("FAIL: policy long arithmetic overflow");
        var plugin = File.ReadAllText(Path.Combine(dir.FullName,"src","DiagnosticsPlugin.cs"));
        foreach (var token in new[]{"_semanticEvents.Tick(SemanticOverlap)","ProcessSemanticCommand(root)","_semanticEvents.Stop(\"diagnostics-destroyed\")", "_semanticEvents.Stop(\"diagnostic-error-limit\")", "semantic-events-command.json", "SemanticEventPolicy.Parse", "_semanticEvents.Enabled", "SchemaVersion = 1"})
            if (!plugin.Contains(token,StringComparison.Ordinal)) throw new Exception("FAIL: semantic lifecycle/command wiring missing "+token);
        if (plugin.IndexOf("_semanticEvents.Tick(SemanticOverlap)",StringComparison.Ordinal) > plugin.IndexOf("now < _nextPoll",StringComparison.Ordinal))
            throw new Exception("FAIL: semantic deadline callback behind slow poll/error-limit gate");
        foreach(var file in new[]{"GpuPoseCapture.cs","CandidatePoseOverlay.cs","VisualPrototype.cs"})
            if (!File.ReadAllText(Path.Combine(dir.FullName,"src",file)).Contains("SemanticOverlapActive",StringComparison.Ordinal))
                throw new Exception("FAIL: typed overlap state missing "+file);
        Console.WriteLine("PASS: semantic request, monotonic deadline/spacing and overflow-safe source window policies and source wiring.");
    }
}
