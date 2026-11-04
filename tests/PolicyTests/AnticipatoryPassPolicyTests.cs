using System.Text.Json;
using FM26.VisualDiagnostics;

internal static class AnticipatoryPassPolicyTests
{
    private static int _assertions;
    private static void Check(bool condition,string message)
    { _assertions++; if(!condition) throw new Exception("FAIL: anticipation "+message); }
    public static void Run()
    {
        CommandMode();
        TwoPhase();
        FutureUniqueness();
        ProducerValidation();
        CachedIdentity();
        ActualStartGates();
        IndexRewinds();
        Continuity();
        PendingIdentity();
        ProvenanceReport();
        CachedFrameCheck();
        AdapterWiring();
        InvalidSequenceAndRequest();
        FailureEnvelope();
        FullClipAndManagedProbe();
        MaximumWindowProbe();
        LiteralCurrentTimeJump();
        PostStartConflict();
        Console.WriteLine($"PASS: {_assertions} anticipatory policy assertions (pure CLR/source only).");
    }

    private static JsonElement Snapshot(int sequence=0,long end=100,int frame=10)
    {
        return JsonSerializer.SerializeToElement(new {Sequence=sequence,FrameCount=frame,QpcTicks=end-1,QpcEndTicks=end,
            CurrentTimeSlice=100,CurrentTime=25f,CurrentPlaybackMode=1,CurrentPlaybackSpeed=1f,MatchPeriod=1,
            CurrentFirstEventIndex=0,CurrentLastEventIndex=0,SourceListNull=false,SourceCount=2,CaptureStartIndex=0,WindowTruncated=false,
            Events=new[]{new{SourceIndex=0,CommentaryEventID=1,CommentaryEventName="Other",TimeSlice=100,Duration=0,ContributorTimeSlice=-1,Flags=0,PlayerUniqueIDs=new[]{-1}},
                new{SourceIndex=1,CommentaryEventID=8041,CommentaryEventName="EventAttemptPassFloor",TimeSlice=101,Duration=0,ContributorTimeSlice=-1,Flags=0,PlayerUniqueIDs=new[]{42,-1,42}}}});
    }
    private static object NewSchedule()
    {
        var type=typeof(OriginalPassPolicy).Assembly.GetType("FM26.VisualDiagnostics.AnticipatoryPassSchedule");
        Check(type!=null,"two-phase pure scheduler missing");
        var actors=JsonSerializer.SerializeToElement(new[]{new{PlayerId=42,ActorInstanceId=-9}});
        return Activator.CreateInstance(type!,new object[]{new SemanticEventRequest("start-events","s","r",-9,20,true,true),42,actors,1000L,0L})!;
    }
    private static string Call(object schedule,string method,params object[] args) => (string)schedule.GetType().GetMethod(method)!.Invoke(schedule,args)!;
    private static void TwoPhase()
    {
        var s=NewSchedule();
        Check(Call(s,"Observe",Snapshot(),110L,10)=="selected","selection only");
        Check(s.GetType().GetProperty("TriggerQpcTicks")!.GetValue(s)==null,"Observe must not start");
        Check(Call(s,"TryStart",120L,11,10)=="started","actual later eligible update starts");
        Check((long)s.GetType().GetProperty("TriggerQpcTicks")!.GetValue(s)! ==120L,"actual not backdated QPC");
        Check(OriginalPassPolicy.Phase(120,120,1000)==0,"first phase zero");
        Check(Call(s,"TryStart",130L,12,11)=="started","no rearm");
        Check((long)s.GetType().GetProperty("TriggerQpcTicks")!.GetValue(s)! ==120L,"start immutable");
    }


    private static JsonElement Change(JsonElement value,Action<System.Text.Json.Nodes.JsonObject> change)
    {
        var node=System.Text.Json.Nodes.JsonNode.Parse(value.GetRawText())!.AsObject();change(node);
        return JsonSerializer.SerializeToElement(node);
    }
    private static void FutureUniqueness()
    {
        var conflict=Change(Snapshot(),n=> {n["Events"]![0]!["TimeSlice"]=102;n["Events"]![0]!["PlayerUniqueIDs"]=new System.Text.Json.Nodes.JsonArray(42);});
        Check(Call(NewSchedule(),"Observe",conflict,110L,10)=="aborted","other actor-involving future action refuses");
        foreach(int delta in new[]{-1,0,9,int.MaxValue})
        {
            var s=Change(Snapshot(),n=>n["Events"]![1]!["TimeSlice"]=delta==int.MaxValue ? int.MaxValue : 100+delta);
            string decision=Call(NewSchedule(),"Observe",s,110L,10);
            Check(decision=="wait" || decision=="aborted","literal horizon excludes outside 1..8");
        }
        foreach(int delta in new[]{1,8})
            Check(Call(NewSchedule(),"Observe",Change(Snapshot(),n=>n["Events"]![1]!["TimeSlice"]=100+delta),110L,10)=="selected","raw horizon inclusive");
        foreach(var transform in new Action<System.Text.Json.Nodes.JsonObject>[] {
            n=>n["Events"]![1]!["PlayerUniqueIDs"]=null,
            n=>n["Events"]![1]!["PlayerUniqueIDs"]=new System.Text.Json.Nodes.JsonArray(),
            n=>n["Events"]![1]!["PlayerUniqueIDs"]=new System.Text.Json.Nodes.JsonArray(-1,42),
            n=>n["Events"]![1]!["CommentaryEventName"]="Other",
            n=>{n["Events"]![1]!["CommentaryEventID"]=2;n["Events"]![1]!["CommentaryEventName"]=null;}})
            Check(Call(NewSchedule(),"Observe",Change(Snapshot(),transform),110L,10)=="wait","no invented slot/name eligibility");
        foreach(var pair in new[]{(8041,"EventAttemptPassFloor"),(8282,"EventPassShortForward"),(8310,"EventPassMediumForward"),(8324,"EventPassMediumForwardIntoPath")})
            Check(Call(NewSchedule(),"Observe",Change(Snapshot(),n=>{n["Events"]![1]!["CommentaryEventID"]=pair.Item1;n["Events"]![1]!["CommentaryEventName"]=pair.Item2;}),110L,10)=="selected","four exact pairs");
    }


    private static void ProducerValidation()
    {
        foreach(var change in new Action<System.Text.Json.Nodes.JsonObject>[] {
            n=>n["FrameCount"]=2147483648L,n=>n["Sequence"]=-1,n=>n["QpcTicks"]=101,n=>n["QpcEndTicks"]=-1,
            n=>n["CurrentTime"]=3.5e38,n=>n["CurrentPlaybackSpeed"]=0,n=>n["CurrentPlaybackSpeed"]=-1,
            n=>n["CurrentFirstEventIndex"]=-1,n=>n["CurrentLastEventIndex"]=2,n=>n["SourceCount"]=8193,
            n=>n["SourceCount"]=null,n=>n["SourceListNull"]=true,n=>n["SourceCount"]=0,
            n=>n["CaptureStartIndex"]=1,n=>n["WindowTruncated"]=true,n=>n.Remove("MatchPeriod"),n=>n["Unknown"]=0,
            n=>n["Events"]!.AsArray().RemoveAt(1),n=>n["Events"]![1]!["SourceIndex"]=0,
            n=>n["Events"]![1]!["Duration"]=2147483648L,n=>n["Events"]![1]!["ContributorTimeSlice"]="-1",
            n=>n["Events"]![1]!["Flags"]=true,n=>n["Events"]![1]!["CommentaryEventName"]=new string('x',257),
            n=>n["Events"]![1]!["PlayerUniqueIDs"]=new System.Text.Json.Nodes.JsonArray(Enumerable.Range(0,17).Select(i=>(System.Text.Json.Nodes.JsonNode?)System.Text.Json.Nodes.JsonValue.Create(i)).ToArray()),
            n=>n["Events"]![1]!["PlayerUniqueIDs"]![1]=2147483648L,n=>n["Events"]![1]!.AsObject().Remove("Duration"),
            n=>n["Events"]![1]!["Unknown"]=0,n=>n["TimestampUtc"]=new string('x',65)})
        {
            var s=NewSchedule();Check(Call(s,"Observe",Change(Snapshot(),change),110L,10)=="aborted","producer invalidity fails closed");
            Check(Call(s,"Observe",Snapshot(1,200,11),210L,11)=="aborted","permanent invalidity/no rearm");
        }
        var dup=Change(Snapshot(),n=> {var x=n["Events"]![0]!.DeepClone();x["SourceIndex"]=1;n["Events"]![1]=x;});
        Check(Call(NewSchedule(),"Observe",dup,110L,10)=="aborted","duplicate payload ambiguous even distinct index");
        foreach(float speed in new[]{float.MaxValue,float.Epsilon})
            Check(Call(NewSchedule(),"Observe",Change(Snapshot(),n=>n["CurrentPlaybackSpeed"]=speed),110L,10)=="selected","legitimate float32 boundary");
        Check(Call(NewSchedule(),"Observe",Change(Snapshot(),n=>n["Events"]![0]!["PlayerUniqueIDs"]=null),110L,10)=="selected","null participant preserved");
        Check(Call(NewSchedule(),"Observe",Change(Snapshot(),n=>n["Events"]![0]!["PlayerUniqueIDs"]=new System.Text.Json.Nodes.JsonArray()),110L,10)=="selected","empty participant preserved");
    }


    private static void CachedIdentity()
    {
        var type=typeof(OriginalPassPolicy).Assembly.GetType("FM26.VisualDiagnostics.AnticipatoryPassSchedule")!;
        foreach(var actors in new object[]{new[]{new{PlayerId=41,ActorInstanceId=-9}},new[]{new{PlayerId=42,ActorInstanceId=0}},new[]{new{PlayerId=42,ActorInstanceId=-8}},new[]{new{PlayerId=42,ActorInstanceId=-9},new{PlayerId=42,ActorInstanceId=-10}}})
        {
            bool rejected=false;
            try{Activator.CreateInstance(type,new object[]{new SemanticEventRequest("start-events","s","r",-9,20,true,true),42,JsonSerializer.SerializeToElement(actors),1000L,0L});}
            catch(System.Reflection.TargetInvocationException e) when(e.InnerException is InvalidOperationException){rejected=true;}
            Check(rejected,"exact unique cached actor required");
        }
    }


    private static void ActualStartGates()
    {
        var s=NewSchedule();Call(s,"Observe",Snapshot(),110L,10);
        Check(Call(s,"TryStart",120L,10,10)=="wait","no same-frame bypass after previous pose");
        Check(s.GetType().GetProperty("TriggerQpcTicks")!.GetValue(s)==null,"same-frame remains pending");
        Check(Call(s,"TryStart",250L,11,10)=="started","TTL exact boundary 150ms");
        var expired=NewSchedule();Call(expired,"Observe",Snapshot(),110L,10);
        Check(Call(expired,"TryStart",251L,11,10)=="aborted","first copy expires no catchup");
        Check(Call(expired,"TryStart",252L,12,11)=="aborted","no rearm after TTL");
        foreach(long qpc in new[]{19100L,19101L})
        {
            var x=NewSchedule();Call(x,"Observe",Snapshot(0,qpc-10,10),qpc-5,10);
            Check(Call(x,"TryStart",qpc,11,10)=="aborted","full clip strictly fits deadline");
        }
        var fits=NewSchedule();Call(fits,"Observe",Snapshot(0,19089,10),19094L,10);
        Check(Call(fits,"TryStart",19099L,11,10)=="started","one tick before strict full clip deadline");
        foreach(var args in new[]{(109L,11),(120L,9),(20000L,11)})
        {var x=NewSchedule();Call(x,"Observe",Snapshot(),110L,10);Check(Call(x,"TryStart",args.Item1,args.Item2,10)=="aborted","decision reversal/deadline");}
    }


    private static JsonElement Wide(int first=2,int last=4,int sequence=0,long end=100,int frame=10)
    {
        int start=Math.Max(0,first-1);
        var events=Enumerable.Range(start,6-start).Select(i=>new {SourceIndex=i,CommentaryEventID=i==5 ? 8041 : 1,
            CommentaryEventName=i==5 ? "EventAttemptPassFloor" : "Other",TimeSlice=i==5 ? 101 : 90+i,Duration=i,ContributorTimeSlice=-1,Flags=0,PlayerUniqueIDs=i==5 ? new[]{42,-1,42} : new[]{-1}}).ToArray();
        return Change(Snapshot(sequence,end,frame),n=>{n["SourceCount"]=6;n["CurrentFirstEventIndex"]=first;n["CurrentLastEventIndex"]=last;n["CaptureStartIndex"]=start;n["WindowTruncated"]=start>0;n["Events"]=JsonSerializer.SerializeToNode(events);});
    }
    private static void IndexRewinds()
    {
        foreach(bool started in new[]{false,true}) foreach(bool sameFrame in new[]{false,true})
            foreach(var indices in new[]{(1,4),(2,3),(1,5),(3,3)})
        {
            var s=NewSchedule();Check(Call(s,"Observe",Wide(),110L,10)=="selected","wide selection");
            if(started) Check(Call(s,"TryStart",120L,10,9)=="started","started baseline");
            Check(Call(s,"Observe",Wide(indices.Item1,indices.Item2,1,200,sameFrame?10:11),210L,sameFrame?10:11)=="aborted","independent first/last rewind before shortcuts");
            Check((string)s.GetType().GetProperty("AbortReason")!.GetValue(s)! =="current-event-index-rewind","precise rewind reason");
            Check(Call(s,"Observe",Wide(2,4,2,300,12),310L,12)=="aborted","rewind permanent");
        }
    }


    private static void Continuity()
    {
        foreach(bool started in new[]{false,true}) foreach(var change in new Action<System.Text.Json.Nodes.JsonObject>[] {
            n=>n["Sequence"]=0,n=>n["Sequence"]=2,n=>n["FrameCount"]=9,n=>n["QpcTicks"]=99,n=>n["QpcEndTicks"]=99,
            n=>n["CurrentTimeSlice"]=99,n=>n["CurrentTimeSlice"]=109,n=>n["CurrentTime"]=24.9,n=>n["CurrentTime"]=26.01,
            n=>n["CurrentPlaybackSpeed"]=1.1,n=>n["CurrentPlaybackMode"]=2,n=>n["MatchPeriod"]=2})
        {
            var s=NewSchedule();Call(s,"Observe",Snapshot(),110L,10);
            if(started)Call(s,"TryStart",120L,10,9);
            Check(Call(s,"Observe",Change(Snapshot(1,200,10),change),210L,10)=="aborted","continuity before same-frame/started shortcuts");
        }
        var pollGap=NewSchedule();Call(pollGap,"Observe",Snapshot(),110L,10);
        Call(pollGap,"TryStart",200L,11,11); // decision continuity doesn't repair poll gap
        Check(Call(pollGap,"Observe",Snapshot(1,351,12),360L,12)=="aborted","251ms poll gap");
        foreach(var time in new[]{25f,26f})
        {var s=NewSchedule();Call(s,"Observe",Snapshot(),110L,10);
         Check(Call(s,"Observe",Change(Snapshot(1,200,11),n=>{n["CurrentTime"]=time;n["CurrentTimeSlice"]=100;}),210L,11)=="wait","inclusive continuity bounds/no retiming");}
    }


    private static void PendingIdentity()
    {
        foreach(bool started in new[]{false,true}) foreach(var change in new Action<System.Text.Json.Nodes.JsonObject>[] {
            n=>n["Events"]![1]!["Duration"]=1,n=>n["Events"]![1]!["ContributorTimeSlice"]=0,
            n=>n["Events"]![1]!["Flags"]=1,n=>n["Events"]![1]!["PlayerUniqueIDs"]![1]=-2,
            n=>n["Events"]![1]!["TimeSlice"]=102,n=>n["Events"]![1]!["CommentaryEventID"]=8282,
            n=>n["Events"]![1]!["CommentaryEventName"]="EventPassShortForward",
            n=>{n["SourceCount"]=1;n["Events"]!.AsArray().RemoveAt(1);}})
        {
            var s=NewSchedule();Call(s,"Observe",Snapshot(),110L,10);if(started)Call(s,"TryStart",120L,10,9);
            Check(Call(s,"Observe",Change(Snapshot(1,200,11),change),210L,11)=="aborted","full record mutation/disappearance before/after start");
        }
        var expired=NewSchedule();Call(expired,"Observe",Snapshot(),110L,10);
        Check(Call(expired,"Observe",Snapshot(1,260,11),270L,11)=="aborted","new poll never refreshes first-copy TTL");
        Check(Call(expired,"TryStart",271L,12,11)=="aborted","aborted Observe prevents start");
        var late=NewSchedule();Call(late,"Observe",Snapshot(),110L,10);
        Check(Call(late,"Observe",Change(Snapshot(1,200,11),n=>n["CurrentTimeSlice"]=101),210L,11)=="aborted","no late fallback to current marker");
        var startedPolicy=NewSchedule();Call(startedPolicy,"Observe",Snapshot(),110L,10);Call(startedPolicy,"TryStart",120L,10,9);
        Check(Call(startedPolicy,"Observe",Change(Snapshot(1,200,11),n=>n["CurrentTimeSlice"]=101),210L,11)=="started","future reaching current after actual start is not retiming");
    }


    private static JsonElement Report(object schedule) => JsonSerializer.SerializeToElement(schedule.GetType().GetProperty("Report")!.GetValue(schedule));
    private static void ProvenanceReport()
    {
        var s=NewSchedule();Check(s.GetType().GetProperty("Report")!=null,"bounded schema Report missing");
        Call(s,"Observe",Snapshot(),110L,10);
        Call(s,"Observe",Snapshot(1,200,11),210L,11);
        Call(s,"TryStart",220L,12,11);
        var r=Report(s);
        Check(r.GetProperty("Rule").GetString()=="H012-A","report rule");
        Check(r.GetProperty("Selected").GetProperty("SnapshotSequence").GetInt64()==0,"first copy provenance immutable");
        Check(r.GetProperty("Selected").GetProperty("QpcEndTicks").GetInt64()==100,"TTL original read-end");
        Check(r.GetProperty("Selected").GetProperty("ObservedQpcTicks").GetInt64()==110,"callback selected time");
        Check(r.GetProperty("Start").GetProperty("SnapshotSequence").GetInt64()==1,"latest copied sequence at start");
        Check(r.GetProperty("Start").GetProperty("QpcTicks").GetInt64()==220,"actual start separate");
        Check(r.GetProperty("Observations").GetArrayLength()==2 && r.GetProperty("Observations")[0].GetProperty("Decision").GetString()=="selected","one observation per copy");
        Call(s,"Observe",Change(Snapshot(2,300,13),n=>n["CurrentTimeSlice"]=99),310L,13);
        var abort=Report(s).GetProperty("Abort");
        Check(abort.GetProperty("QpcTicks").GetInt64()==310 && abort.GetProperty("SnapshotSequence").GetInt64()==2,"refusal copy provenance");
        Call(s,"TryStart",320L,14,13);Check(Report(s).GetProperty("Abort").GetRawText()==abort.GetRawText(),"first abort retained");
        var ttl=NewSchedule();Call(ttl,"Observe",Snapshot(),110L,10);Call(ttl,"TryStart",251L,11,10);
        Check(Report(ttl).GetProperty("Abort").GetProperty("SnapshotSequence").ValueKind==JsonValueKind.Null,"between-poll abort sequence null");
        var bounded=NewSchedule();
        for(int i=0;i<200;i++)
            Check(Call(bounded,"Observe",Change(Snapshot(i,i*50+100,10+i),n=>n["Events"]![1]!["PlayerUniqueIDs"]=null),(long)i*50+110,10+i)=="wait","bounded no-trigger observations");
        Check(Report(bounded).GetProperty("Observations").GetArrayLength()==200,"hard trace cap");
        Check(Call(bounded,"Observe",Snapshot(200,10100,210),10110L,210)=="aborted","snapshot hard cap");
        Check(Report(bounded).GetProperty("Observations").GetArrayLength()==200,"no overflow observation");
    }


    private static void CachedFrameCheck()
    {
        var s=NewSchedule();Check(s.GetType().GetMethod("Check")!=null,"O(1) nonstarting frame check missing");
        Call(s,"Observe",Snapshot(),110L,10);
        Check(Call(s,"Check",120L,10)=="wait","frame check cannot start before guards");
        Check(s.GetType().GetProperty("TriggerQpcTicks")!.GetValue(s)==null,"only TryStart starts");
        Check(Call(s,"Check",251L,10)=="aborted","expiry checked even same-frame before shortcut");
        var noRead=NewSchedule();Check(Call(noRead,"Check",100L,1)=="wait","no copied event means wait");
        Check(Call(noRead,"Check",351L,2)=="aborted","251ms decision gap with no snapshot");
    }


    private static string SourceRoot()
    {
        var dir=new DirectoryInfo(AppContext.BaseDirectory);
        while(dir!=null && !File.Exists(Path.Combine(dir.FullName,"FM26.VisualDiagnostics.csproj"))) dir=dir.Parent;
        return dir?.FullName ?? throw new Exception("source root");
    }
    private static void AdapterWiring()
    {
        string root=SourceRoot();
        string demo=File.ReadAllText(Path.Combine(root,"src/OriginalPassDemo.cs"));
        Check(demo.Contains("_request.AnticipatoryPassDemo ? 2 : 1",StringComparison.Ordinal),"distinct schema2 opt-in producer wiring missing");
        Check(demo.Contains("TimingMode=_request.AnticipatoryPassDemo ? AnticipatoryPassPolicy.Rule : null",StringComparison.Ordinal),"distinct timing mode");
        Check(demo.Contains("Anticipation=_request.AnticipatoryPassDemo",StringComparison.Ordinal),"bounded report");
        Check(demo.Contains("_anticipation.Observe(snapshot,detectionQpc,frame)",StringComparison.Ordinal),"pure Observe from cached callback frame");
        int snapshotBegin=demo.IndexOf("private void Snapshot(",StringComparison.Ordinal),snapshotEnd=demo.IndexOf("public void Tick()",snapshotBegin,StringComparison.Ordinal);
        Check(System.Text.RegularExpressions.Regex.Matches(demo[snapshotBegin..snapshotEnd],@"Time\.frameCount").Count==1,"no extra Unity frame getter");
        Check(demo.IndexOf("_anticipation.Observe(snapshot",snapshotBegin,StringComparison.Ordinal)<demo.IndexOf("if(anticipationBudget==",snapshotBegin,StringComparison.Ordinal),"every copied observation recorded before clean budget stop");
        Check(demo.Contains("_anticipation.TryStart(now,frame,_lastFrame)",StringComparison.Ordinal),"actual eligible start");
        int tick=demo.IndexOf("public void Tick()",StringComparison.Ordinal);
        int start=demo.IndexOf("_anticipation.TryStart",tick,StringComparison.Ordinal);
        int rootRead=demo.IndexOf("Quaternion rotation=_actorTransform.rotation",tick,StringComparison.Ordinal);
        Check(start>rootRead && start>demo.IndexOf("OriginalPassPolicy.OriginalFlagsUnchanged",tick,StringComparison.Ordinal),"actual start after guards/root checks");
        Check(demo.Contains("AnticipatoryPassPolicy.IsAbstention(_request.AnticipatoryPassDemo,reason)",StringComparison.Ordinal),"clean refusal isolated from actual failure");
        string observer=File.ReadAllText(Path.Combine(root,"src/SemanticEventObserver.cs"));
        string baseline=File.ReadAllText(Path.Combine(root,"local/spike-014/pre-anticipatory-0.2.12/files/src/SemanticEventObserver.cs"));
        var regex=new System.Text.RegularExpressions.Regex(@"ReadPublished\(\(\) => [^;\r\n]*?\)");
        var oldReads=regex.Matches(baseline).Select(x=>x.Value).ToArray();
        var newReads=regex.Matches(observer).Select(x=>x.Value).ToArray();
        Check(oldReads.Length==24 && oldReads.SequenceEqual(newReads),"exactly24 unchanged published read expressions");
        Check(observer.Contains("AnticipatoryPassDemo = _request.AnticipatoryPassDemo",StringComparison.Ordinal),"native bool echo");
        Check(observer.Contains("CopiedKnownActors",StringComparison.Ordinal),"existing cached CLR actor bridge");
        string plugin=File.ReadAllText(Path.Combine(root,"src/DiagnosticsPlugin.cs"));
        Check(plugin.Contains("AnticipatoryPassDemo = _semanticEvents.AnticipatoryPassDemo",StringComparison.Ordinal),"acceptance bool echo");
        foreach(var path in Directory.GetFiles(Path.Combine(root,"src"),"*.cs"))
            Check(!File.ReadAllText(path).Contains("\"0.2.11\"",StringComparison.Ordinal),"consistent0.2.12 metadata");
        Check(File.ReadAllText(Path.Combine(root,"FM26.VisualDiagnostics.csproj")).Contains("<Version>0.10.7</Version>",StringComparison.Ordinal),"assembly version") /* H166 version bump; H168: 0.6.1; H178a: 0.8.0; H186: 0.9.0; H186b: 0.10.0; H200: 0.10.1; H203: 0.10.2; H206: 0.10.3; H209: 0.10.4; H211: 0.10.5; H234: 0.10.6; H238: 0.10.7 */;
        var classify=typeof(AnticipatoryPassPolicy).GetMethod("IsAbstention");
        Check(classify!=null,"refusal classification pure helper");
        foreach(var c in new[]{(true,"anticipation-abstained",true),(false,"anticipation-abstained",false),(true,"owned-update-failure",false),(true,"stop-time-budget",false),(true,"context-or-read-error",false)})
            Check((bool)classify!.Invoke(null,new object[]{c.Item1,c.Item2})! == c.Item3,"actual failure/budget not predictor refusal");
    }


    private static void InvalidSequenceAndRequest()
    {
        foreach(var value in new object[]{true,"0",new {Unknown=0}})
            Check(Call(NewSchedule(),"Observe",Change(Snapshot(),n=>n["Sequence"]=JsonSerializer.SerializeToNode(value)),110L,10)=="aborted","invalid sequence type safely refuses");
        foreach(var request in new[]{new SemanticEventRequest("stop-events","s","r",-9,20,true,true),
            new SemanticEventRequest("start-events","","r",-9,20,true,true),new SemanticEventRequest("start-events",new string('s',129),"r",-9,20,true,true),
            new SemanticEventRequest("start-events","s","bad/id",-9,20,true,true),new SemanticEventRequest("start-events","s","r",-9,20,false,true),
            new SemanticEventRequest("start-events","s","r",-9,20,true,false)})
        {
            bool refused=false;
            try{new AnticipatoryPassSchedule(request,42,JsonSerializer.SerializeToElement(new[]{new{PlayerId=42,ActorInstanceId=-9}}),1000,0);}
            catch(InvalidOperationException){refused=true;}
            Check(refused,"schedule bound to exact opt-in valid request");
        }
    }


    private static void FailureEnvelope()
    {
        var property=typeof(AnticipatoryPassPolicy).GetProperty("EmptyReport");
        Check(property!=null,"schema2 envelope needed even preflight/constructor failure");
        var envelope=JsonSerializer.SerializeToElement(property!.GetValue(null));
        Check(envelope.GetProperty("Rule").GetString()=="H012-A" && envelope.GetProperty("Selected").ValueKind==JsonValueKind.Null
            && envelope.GetProperty("Start").ValueKind==JsonValueKind.Null && envelope.GetProperty("Abort").ValueKind==JsonValueKind.Null
            && envelope.GetProperty("Observations").GetArrayLength()==0,"actual error must not synthesize predictor abort");
        Check(File.ReadAllText(Path.Combine(SourceRoot(),"src/OriginalPassDemo.cs")).Contains("_anticipation?.Report ?? AnticipatoryPassPolicy.EmptyReport",StringComparison.Ordinal),"failure envelope source wiring");
    }


    private static void FullClipAndManagedProbe()
    {
        var s=(AnticipatoryPassSchedule)NewSchedule();s.Observe(Snapshot(),110,10);s.TryStart(120,11,10);
        for(int i=1;i<=10;i++)
        {
            long end=100+i*100;
            var copy=Change(Snapshot(i,end,11+i),n=>{n["CurrentTimeSlice"]=100+i;n["CurrentTime"]=25+i*.1f;n["CurrentFirstEventIndex"]=1;n["CurrentLastEventIndex"]=1;});
            Check(s.Observe(copy,end+10,11+i)=="started","valid copies retain actual one-shot clip across0.9s");
            Check(s.Check(end+15,11+i)=="started","per-frame cached continuity");
        }
        Check(s.TriggerQpcTicks==120 && OriginalPassPolicy.Phase(1020,120,1000)==1,"duration geometry clock unchanged");
        string? target=Environment.GetEnvironmentVariable("ANTICIPATION_POLICY_OUTPUT");
        if(target!=null) File.WriteAllText(target,JsonSerializer.Serialize(new {Evidence="SYNTHETIC pure CLR policy only; no FM/native/Unity execution",Anticipation=s.Report,s.Trigger,TriggerQpcTicks=s.TriggerQpcTicks}));
        long begin=System.Diagnostics.Stopwatch.GetTimestamp();
        const int calls=10000;
        for(int i=0;i<calls;i++) s.Check(1115,21);
        long endQpc=System.Diagnostics.Stopwatch.GetTimestamp();
        Console.WriteLine($"PROBE: {calls} cached managed Check calls took {endQpc-begin} Stopwatch ticks at {System.Diagnostics.Stopwatch.Frequency}Hz; not runtime latency certification.");
    }


    private static void MaximumWindowProbe()
    {
        var events=Enumerable.Range(49,32).Select(i=>new {SourceIndex=i,CommentaryEventID=i==80?8041:1,
            CommentaryEventName=i==80?"EventAttemptPassFloor":new string('x',256),TimeSlice=i==80?101:90,
            Duration=i,ContributorTimeSlice=int.MinValue,Flags=int.MaxValue,PlayerUniqueIDs=i==80?new[]{42,-1,42}:Enumerable.Repeat(-1,16).ToArray()}).ToArray();
        var snapshot=Change(Snapshot(),n=>{n["SourceCount"]=8192;n["CaptureStartIndex"]=49;n["CurrentFirstEventIndex"]=50;n["CurrentLastEventIndex"]=50;n["WindowTruncated"]=true;n["Events"]=JsonSerializer.SerializeToNode(events);});
        Check(Call(NewSchedule(),"Observe",snapshot,110L,10)=="selected","32 rows/source8192/16 literal participant ids/name256 extremes accepted");
        long begin=System.Diagnostics.Stopwatch.GetTimestamp();
        const int calls=200;
        var actors=JsonSerializer.SerializeToElement(new[]{new{PlayerId=42,ActorInstanceId=-9}});
        for(int i=0;i<calls;i++)
        {
            var policy=new AnticipatoryPassSchedule(new SemanticEventRequest("start-events","s","r",-9,20,true,true),42,actors,1000,0);
            policy.Observe(snapshot,110,10);
        }
        long end=System.Diagnostics.Stopwatch.GetTimestamp();
        Console.WriteLine($"PROBE: {calls} managed constructor+32-row Observe calls took {end-begin} Stopwatch ticks at {System.Diagnostics.Stopwatch.Frequency}Hz; includes allocations, not runtime CPU certification.");
    }


    private static void LiteralCurrentTimeJump()
    {
        var policy=NewSchedule();
        Call(policy,"Observe",Change(Snapshot(),n=>n["CurrentTime"]=0),110L,10);
        Check(Call(policy,"Observe",Change(Snapshot(1,200,11),n=>n["CurrentTime"]=1.00000004),210L,11)=="aborted","literal raw CurrentTime jump must not round into acceptance");
    }


    private static void PostStartConflict()
    {
        var policy=NewSchedule();Call(policy,"Observe",Snapshot(),110L,10);Call(policy,"TryStart",120L,10,9);
        var conflicting=Change(Snapshot(1,200,11),n=>{n["CurrentTimeSlice"]=101;n["CurrentFirstEventIndex"]=1;n["CurrentLastEventIndex"]=1;
            n["Events"]![0]!["TimeSlice"]=102;n["Events"]![0]!["PlayerUniqueIDs"]=new System.Text.Json.Nodes.JsonArray(42);});
        Check(Call(policy,"Observe",conflicting,210L,11)=="aborted","a different actor-involving future record remains a conflict after pending becomes current");
    }

    private static void CommandMode()
    {
        const string prefix="{\"Action\":\"start-events\",\"Session\":\"s\",\"RequestId\":\"r\",\"ActorInstanceId\":-9,\"MaximumSeconds\":20";
        SemanticEventRequest Parse(string text) => SemanticEventPolicy.Parse(JsonDocument.Parse(text).RootElement,"s");
        var request=Parse(prefix+",\"OriginalPassDemo\":true,\"AnticipatoryPassDemo\":true}");
        var mode=request.GetType().GetProperty("AnticipatoryPassDemo");
        Check(mode!=null && (bool)mode.GetValue(request)!,"literal opted-in mode");
        Check(!(bool)mode!.GetValue(Parse(prefix+"}"))!,"absent default false");
        Check(!(bool)mode.GetValue(Parse(prefix+",\"OriginalPassDemo\":true}"))!,"original-only remains distinct");
        foreach(var text in new[]{prefix+",\"AnticipatoryPassDemo\":true}",prefix+",\"OriginalPassDemo\":true,\"AnticipatoryPassDemo\":false}",prefix+",\"OriginalPassDemo\":true,\"AnticipatoryPassDemo\":null}",prefix+",\"OriginalPassDemo\":true,\"AnticipatoryPassDemo\":1}",prefix+",\"OriginalPassDemo\":true,\"AnticipatoryPassDemo\":\"true\"}","{\"Action\":\"stop-events\",\"Session\":\"s\",\"RequestId\":\"r\",\"AnticipatoryPassDemo\":true}"})
        {
            bool rejected=false;try{Parse(text);}catch(InvalidOperationException){rejected=true;}
            Check(rejected,"nonliteral/unpaired/stop mode rejected");
        }
    }
}
