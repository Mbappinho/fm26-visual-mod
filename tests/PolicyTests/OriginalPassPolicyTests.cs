using System.Numerics;
using System.Reflection;
using FM26.VisualDiagnostics;

internal static class OriginalPassPolicyTests
{
    public static void Run()
    {
        var type = Assembly.GetExecutingAssembly().GetType("FM26.VisualDiagnostics.OriginalPassPolicy");
        if (type == null) throw new Exception("FAIL: original authored rig policy absent");
        var window = type.GetMethod("RequireWriteWindow");
        var classify = type.GetMethod("ClassifyFailure");
        if(window==null || classify==null) throw new Exception("FAIL: clean deadline / actual inactive failure stop policy absent");
        Exception Gate(bool enabled,long now)
        {
            try { window.Invoke(null,new object[]{enabled,now,1000L,1000L,1}); }
            catch(TargetInvocationException e) { return e.InnerException!; }
            throw new Exception("FAIL: expired/inactive write window accepted");
        }
        (string Reason,string? Error) Cause(Exception e) => ((string,string?))classify.Invoke(null,new object[]{e,"owned-update-failure"})!;
        var budget=Cause(Gate(true,2000));
        if(budget.Reason!="stop-time-budget" || budget.Error!=null) throw new Exception("FAIL: deadline must stop cleanly with exactly null error");
        window.Invoke(null,new object[]{true,1999L,1000L,1000L,1});
        foreach(var e in new[]{Gate(false,1999),Gate(false,2000),new InvalidOperationException("actual context changed")})
        {
            var failure=Cause(e);
            if(failure.Reason!="owned-update-failure" || string.IsNullOrEmpty(failure.Error)) throw new Exception("FAIL: inactive/context failure mislabeled clean budget");
        }
        var observerBudget=Cause(new TimeoutException("Semantic event deadline reached."));
        if(observerBudget!=budget) throw new Exception("FAIL: shared observer and write deadlines classified differently");
        string? evidence=Environment.GetEnvironmentVariable("ALIGNMENT_POLICY_OUTPUT");
        if(evidence!=null) File.WriteAllText(evidence,System.Text.Json.JsonSerializer.Serialize(new {Budget=new {budget.Reason,budget.Error},Inactive=new {Reason=Cause(Gate(false,2000)).Reason,Error=Cause(Gate(false,2000)).Error}}));
        Console.WriteLine("PASS: exact-deadline null error, shared timeout, inactive/context genuine failure");
        var canonical=type.GetMethod("CanonicalError");
        if(canonical==null) throw new Exception("FAIL: shared bounded literal error policy absent");
        string longError="SYNTHETIC actual owned exception "+new string('x',5000);
        if(longError.Length!=5033) throw new Exception("FAIL: long exception reproducer length");
        string? Canonical(string? error)=>(string?)canonical.Invoke(null,new object?[]{error});
        if(Canonical(longError)!=longError[..4096] || Canonical(null)!=null || Canonical("")!=""
            || Canonical(new string('y',4096))!=new string('y',4096) || Canonical("literal actual context failure")!="literal actual context failure")
            throw new Exception("FAIL: canonical error must preserve exact null/short or literal 4096 prefix");
        var longCause=Cause(new InvalidOperationException(longError));
        // Exception.ToString contains the type prefix; the direct Stop error reproducer is exactly 5033 characters.
        if(longCause.Reason!="owned-update-failure" || !longCause.Error!.Contains(longError,StringComparison.Ordinal))
            throw new Exception("FAIL: genuine exception cause was lost before bounded latching");
        if(evidence!=null) File.WriteAllText(evidence,System.Text.Json.JsonSerializer.Serialize(new {Budget=new {budget.Reason,budget.Error},Inactive=new {Reason=Cause(Gate(false,2000)).Reason,Error=Cause(Gate(false,2000)).Error},LongFailure=new {Reason=longCause.Reason,Error=Canonical(longError)}}));
        Console.WriteLine("PASS: canonical null/short/exact-boundary/5033-char literal prefix and retained failure cause");
        var pose = type.GetMethod("Pose")!;
        var rest = (Vector3[])pose.Invoke(null, new object[]{0d})!;
        var back = (Vector3[])pose.Invoke(null, new object[]{0.25d})!;
        var forward = (Vector3[])pose.Invoke(null, new object[]{0.6d})!;
        if(rest.Length != 21 || Vector3.Distance(back[15],forward[15]) < .2f)
            throw new Exception("FAIL: complete articulated right-foot pose required");
        if(Math.Abs(Vector3.Distance(rest[15],rest[18])-Vector3.Distance(back[15],back[18])) < .03f)
            throw new Exception("FAIL: rigid motion is not articulation");
        var edges = (int[])type.GetField("Edges")!.GetValue(null)!;
        foreach(var p in new[]{back,forward,(Vector3[])pose.Invoke(null,new object[]{1d})!})
            for(int i=0;i<edges.Length;i+=2)
                if(Math.Abs(Vector3.Distance(rest[edges[i]],rest[edges[i+1]])-Vector3.Distance(p[edges[i]],p[edges[i+1]])) > 1e-5)
                    throw new Exception("FAIL: authored limb length changed");
        Console.WriteLine("PASS: original complete rig articulation and invariant segment lengths");
        var phase = type.GetMethod("Phase");
        if(phase == null) throw new Exception("FAIL: bounded detection-QPC scheduler absent");
        if((double)phase.Invoke(null,new object[]{1900L,1000L,1000L})! != 1d ||
            Math.Abs((double)phase.Invoke(null,new object[]{1450L,1000L,1000L})!-.5d)>1e-9)
            throw new Exception("FAIL: detection clock 0.9s clip");
        foreach(var args in new[]{new object[]{999L,1000L,1000L},new object[]{1000L,1000L,0L}})
        { try { phase.Invoke(null,args); throw new Exception("FAIL: invalid clock accepted"); } catch(TargetInvocationException e) when(e.InnerException is InvalidOperationException){} }
        var cleanup = type.GetMethod("CleanupVerified");
        if(cleanup == null) throw new Exception("FAIL: later-frame retained-handle cleanup policy absent");
        foreach(var x in new[]{(10,10,0,0,false),(10,11,1,0,false),(10,11,0,1,false),(10,11,0,0,true)})
            if((bool)cleanup.Invoke(null,new object[]{x.Item1,x.Item2,x.Item3,x.Item4})! != x.Item5) throw new Exception("FAIL: false cleanup certification");
        Console.WriteLine("PASS: detection-QPC duration, clock rejection and strictly later-frame cleanup");
        var trigger = type.GetMethod("Trigger");
        if(trigger == null) throw new Exception("FAIL: exact managed snapshot trigger absent");
        foreach(var code in new[]{8041,8282,8310,8324})
        {
            string name = code switch {8041=>"EventAttemptPassFloor",8282=>"EventPassShortForward",8310=>"EventPassMediumForward",_=>"EventPassMediumForwardIntoPath"};
            foreach(var c in new[]{(2,2,100,99,42,code,name,true),(1,2,100,99,42,code,name,false),(2,2,100,97,42,code,name,false),(2,2,100,101,42,code,name,false),(2,2,100,99,41,code,name,false),(2,2,100,99,42,code,"Other",false),(2,2,int.MaxValue,int.MinValue,42,code,name,false)})
            {
                using var doc=System.Text.Json.JsonDocument.Parse(System.Text.Json.JsonSerializer.Serialize(new {CurrentFirstEventIndex=c.Item2,CurrentLastEventIndex=4,SourceCount=5,SourceListNull=false,CurrentTimeSlice=c.Item3,Events=new[]{new{SourceIndex=c.Item1,CommentaryEventID=c.Item6,CommentaryEventName=c.Item7,TimeSlice=c.Item4,PlayerUniqueIDs=new[]{c.Item5,-1,c.Item5}}}}));
                if((trigger.Invoke(null,new object[]{doc.RootElement,42}) != null) != c.Item8) throw new Exception("FAIL: exact trigger whitelist/marker/age/slot rule");
            }
        }
        Console.WriteLine("PASS: four exact pass codes/names and marker/slot/freshness negatives");
        string command="{\"Action\":\"start-events\",\"Session\":\"s\",\"RequestId\":\"literal\",\"ActorInstanceId\":-9,\"MaximumSeconds\":12";
        using(var doc=System.Text.Json.JsonDocument.Parse(command+",\"OriginalPassDemo\":true}"))
        {
            var request=SemanticEventPolicy.Parse(doc.RootElement,"s");
            var mode=request.GetType().GetProperty("OriginalPassDemo");
            if(mode==null || !(bool)mode.GetValue(request)!) throw new Exception("FAIL: explicit original-pass mode absent");
        }
        foreach(var value in new[]{"false","null","1","\"true\""})
        { using var doc=System.Text.Json.JsonDocument.Parse(command+",\"OriginalPassDemo\":"+value+"}");
          try{SemanticEventPolicy.Parse(doc.RootElement,"s");throw new Exception("FAIL: nonliteral demo mode accepted");}catch(InvalidOperationException){} }
        using(var doc=System.Text.Json.JsonDocument.Parse("{\"Action\":\"stop-events\",\"Session\":\"s\",\"RequestId\":\"literal\",\"OriginalPassDemo\":true}"))
        {try{SemanticEventPolicy.Parse(doc.RootElement,"s");throw new Exception("FAIL: stop carries mode");}catch(InvalidOperationException){}}
        var safety=type.GetMethod("UpdateDecision");
        if(safety==null) throw new Exception("FAIL: demo deadline/same-frame/context policy absent");
        string Decision(long now,int frame,int last,bool context,int rows)=>(string)safety.Invoke(null,new object[]{now,0L,1000L,20,frame,last,context,rows})!;
        if(Decision(20000,10,9,true,0)!="stop-time-budget" || Decision(1,10,9,false,0)!="context-changed"
            || Decision(1,10,10,true,0)!="wait" || Decision(1,9,10,true,0)!="context-changed"
            || Decision(1,10,9,true,2048)!="stop-timeline-budget" || Decision(1,10,9,true,0)!="update")
            throw new Exception("FAIL: demo safety gates");
        var nativeClock=type.GetMethod("NativeClockForward");
        if(nativeClock==null || (bool)nativeClock.Invoke(null,new object[]{10,9,1f,2f})!
            || (bool)nativeClock.Invoke(null,new object[]{9,10,2f,1f})!
            || !(bool)nativeClock.Invoke(null,new object[]{9,9,1f,1f})!) throw new Exception("FAIL: backwards native clock not rejected");
        var dir=new DirectoryInfo(AppContext.BaseDirectory);
        while(dir!=null && !File.Exists(Path.Combine(dir.FullName,"FM26.VisualDiagnostics.csproj"))) dir=dir.Parent;
        if(dir==null) throw new Exception("FAIL: source root");
        string sourcePath=Path.Combine(dir.FullName,"src","OriginalPassDemo.cs");
        if(!File.Exists(sourcePath)) throw new Exception("FAIL: owned demo render/cleanup adapter absent");
        string source=File.ReadAllText(sourcePath);
        if(!source.Contains("OriginalPassPolicy.RequireWriteWindow(_reader.Enabled,Stopwatch.GetTimestamp(),_started,Stopwatch.Frequency,_request.MaximumSeconds)",StringComparison.Ordinal)
            || !source.Contains("OriginalPassPolicy.ClassifyFailure(e,\"owned-update-failure\")",StringComparison.Ordinal)
            || !source.Contains("Stop(cause.Reason,cause.Error)",StringComparison.Ordinal)
            || source.Contains("catch(TimeoutException e)",StringComparison.Ordinal)) throw new Exception("FAIL: pure stop classification not wired to owned update/deadline");
        if(!source.Contains("_error=OriginalPassPolicy.CanonicalError(",StringComparison.Ordinal)
            || !source.Contains("_reader.Stop(reason,_error)",StringComparison.Ordinal)
            || !source.Contains("_reader.Stop(\"owned-demo-failure\",_error)",StringComparison.Ordinal)
            || source.Contains("_reader.Stop(reason,error)",StringComparison.Ordinal)
            || source.Contains("_reader.Stop(\"owned-demo-failure\",error??reason)",StringComparison.Ordinal))
            throw new Exception("FAIL: reader/final must share the once-latched canonical error");
        foreach(var token in new[]{"OriginalPassPolicy.Pose", "OriginalPassPolicy.Trigger", "OriginalPassPolicy.UpdateDecision", "OriginalPassPolicy.CleanupVerified", "PrismGeometry.Create", "CleanupTick()", "File.Move(temporary, target, false)", "NativeActionCommandVerified = false", "targetTexture", "CameraType.Game", "Retain(", "Object.Destroy(", "semantic-pass-"})
            if(!source.Contains(token,StringComparison.Ordinal)) throw new Exception("FAIL: owned demo wiring "+token);
        foreach(var token in new[]{"CreatePrimitive", "AddComponent<Animator>", "AddComponent<Collider>", "AddComponent<Rigidbody>", "m_skeleton", "m_boneTransforms", "GetService", "Anchors[", ".bones", "renderer.enabled ="})
            if(source.Contains(token,StringComparison.Ordinal)) throw new Exception("FAIL: forbidden demo source "+token);
        var observer=File.ReadAllText(Path.Combine(dir.FullName,"src","SemanticEventObserver.cs"));
        if(!observer.Contains("SnapshotCopied?.Invoke",StringComparison.Ordinal) || !observer.Contains("Stopped?.Invoke",StringComparison.Ordinal)) throw new Exception("FAIL: managed-only copied snapshot/lifecycle bridge missing");
        var plugin=File.ReadAllText(Path.Combine(dir.FullName,"src","DiagnosticsPlugin.cs"));
        if(!plugin.Contains("_originalPass?.CleanupTick();",StringComparison.Ordinal)
            || plugin.IndexOf("_originalPass?.CleanupTick();",StringComparison.Ordinal)>plugin.IndexOf("now < _nextPoll",StringComparison.Ordinal)
            || !plugin.Contains("_originalPass?.OverlapActive",StringComparison.Ordinal)) throw new Exception("FAIL: unconditional cleanup/overlap adapter");
        Console.WriteLine("PASS: owned adapter source scope, managed snapshot bridge, unconditional cleanup and reverse overlap wiring");
        var scheduleType=Assembly.GetExecutingAssembly().GetType("FM26.VisualDiagnostics.OriginalPassSchedule");
        if(scheduleType==null) throw new Exception("FAIL: one-shot native-clock-aware managed scheduler absent");
        object schedule=Activator.CreateInstance(scheduleType)!;
        var detect=scheduleType.GetMethod("Detect")!;
        object? Detect(string json,long now)=>(detect.Invoke(schedule,new object[]{System.Text.Json.JsonDocument.Parse(json).RootElement,42,now}));
        string snapshot=System.Text.Json.JsonSerializer.Serialize(new{CurrentTimeSlice=100,CurrentTime=25f,CurrentFirstEventIndex=2,CurrentLastEventIndex=4,SourceListNull=false,SourceCount=5,Events=new[]{new{SourceIndex=2,CommentaryEventID=8041,CommentaryEventName="EventAttemptPassFloor",TimeSlice=99,PlayerUniqueIDs=new[]{42,-1,42}}}});
        if(Detect(snapshot,1234)==null || Detect(snapshot,1235)!=null || (long)scheduleType.GetProperty("TriggerQpcTicks")!.GetValue(schedule)! !=1234L)
            throw new Exception("FAIL: managed scheduler rearmed or retimed");
        try{Detect(snapshot.Replace("\"CurrentTimeSlice\":100","\"CurrentTimeSlice\":99",StringComparison.Ordinal),1236);throw new Exception("FAIL: backward clock accepted after trigger");}
        catch(TargetInvocationException e) when(e.InnerException is InvalidOperationException){}
        Console.WriteLine("PASS: one trigger, detection QPC immutable, backwards clock aborts even after trigger");
        var certify=type.GetMethod("CanCertifyCleanup");
        if(certify==null)throw new Exception("FAIL: partial-allocation uncertainty cleanup guard absent");
        if((bool)certify.Invoke(null,new object[]{10,11,0,0,true})! || !(bool)certify.Invoke(null,new object[]{10,11,0,0,false})!)
            throw new Exception("FAIL: uncertain lost allocation certified dead");
        var flags=type.GetMethod("OriginalFlagsUnchanged");
        if(flags==null)throw new Exception("FAIL: original renderer flags guard absent");
        foreach(var s in new[]{"unchanged","changed","dead","unavailable"})
        {using var doc=System.Text.Json.JsonDocument.Parse("[{\"Status\":\""+s+"\"}]");
         if((bool)flags.Invoke(null,new object[]{doc.RootElement})! != (s=="unchanged"))throw new Exception("FAIL: original flag guard");}
        if(!observer.Contains("public object ContextIdentity",StringComparison.Ordinal)
            || !observer.Contains("PublisherInstanceId = _publisher.GetInstanceID()",StringComparison.Ordinal)
            || !source.Contains("ObserverContext=_reader?.ContextIdentity",StringComparison.Ordinal))
            throw new Exception("FAIL: accepted publisher/controller/root identity evidence missing");
        if(!source.Contains("private void RequireWriteDeadline()",StringComparison.Ordinal)
            || !source.Contains("RequireWriteDeadline();_ownedRoot.transform.position",StringComparison.Ordinal)
            || !source.Contains("RequireWriteDeadline();t.localPosition",StringComparison.Ordinal))
            throw new Exception("FAIL: per-owned-write monotonic deadline gate missing");
    }
}
