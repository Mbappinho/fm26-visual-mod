#nullable disable
using System.Text.Json;

namespace FM26.VisualDiagnostics;

// Owned CLR copies only. H012-A is a literal hypothesis, not a clock conversion.
public static class AnticipatoryPassPolicy
{
    public const string Rule = "H012-A";
    public static bool IsAbstention(bool enabled,string reason) => enabled && reason=="anticipation-abstained";
    public static object EmptyReport => new {Rule,Parameters,Selected=(object)null,Start=(object)null,Observations=Array.Empty<object>(),Abort=(object)null};
    public static object Parameters => new {MinimumFutureRawSlices=1,MaximumFutureRawSlices=8,FreshnessMilliseconds=150,
        MaximumPollDecisionGapMilliseconds=250,MaximumSliceJump=8,MaximumCurrentTimeJump=1.0,
        ClipDurationSeconds=OriginalPassPolicy.ClipDurationSeconds,MaximumObservations=200};
    internal static void Require(bool value,string reason)
    { if(!value) throw new InvalidOperationException(reason); }
    private static void Fields(JsonElement value,string fields,bool timestamp=false)
    {
        Require(value.ValueKind==JsonValueKind.Object,"invalid-object");
        var keys=new HashSet<string>(fields.Split(','),StringComparer.Ordinal);
        foreach(var property in value.EnumerateObject())
        {
            if(timestamp && property.Name=="TimestampUtc")
            { Require(property.Value.ValueKind==JsonValueKind.String && property.Value.GetString()!.Length<=64,"invalid-timestamp");timestamp=false;continue; }
            Require(keys.Remove(property.Name),"unknown-or-duplicate-field");
        }
        Require(keys.Count==0,"missing-field");
    }
    internal static long Nonnegative(JsonElement value)
    { long result=value.GetInt64();Require(result>=0,"invalid-integer");return result; }
    internal static int Frame(JsonElement value)
    { int result=value.GetInt32();Require(result>=0,"invalid-frame");return result; }
    internal static string Payload(JsonElement e) => string.Join("|",new[]{"CommentaryEventID","CommentaryEventName","TimeSlice","Duration","ContributorTimeSlice","Flags","PlayerUniqueIDs"}.Select(k=>e.GetProperty(k).GetRawText()));
    internal static void Validate(JsonElement s)
    {
        Fields(s,"Sequence,FrameCount,QpcTicks,QpcEndTicks,CurrentTimeSlice,CurrentTime,CurrentPlaybackMode,CurrentPlaybackSpeed,MatchPeriod,CurrentFirstEventIndex,CurrentLastEventIndex,SourceListNull,SourceCount,CaptureStartIndex,WindowTruncated,Events",true);
        Nonnegative(s.GetProperty("Sequence"));Frame(s.GetProperty("FrameCount"));
        Require(Nonnegative(s.GetProperty("QpcTicks"))<=Nonnegative(s.GetProperty("QpcEndTicks")),"read-reversal");
        foreach(var k in new[]{"CurrentTimeSlice","CurrentPlaybackMode","MatchPeriod"}) s.GetProperty(k).GetInt32();
        Require(float.IsFinite(s.GetProperty("CurrentTime").GetSingle()) && float.IsFinite(s.GetProperty("CurrentPlaybackSpeed").GetSingle()),"nonfinite-single");
        Require(s.GetProperty("CurrentPlaybackSpeed").GetSingle()>0,"nonpositive-speed");
        Require(s.GetProperty("SourceListNull").ValueKind==JsonValueKind.False,"source-null-or-invalid");
        int n=s.GetProperty("SourceCount").GetInt32(),first=s.GetProperty("CurrentFirstEventIndex").GetInt32(),last=s.GetProperty("CurrentLastEventIndex").GetInt32();
        Require(n>=1 && n<=8192 && first>=0 && first<=last && last<n,"source-bounds");
        int start=Math.Max(0,first-1),count=Math.Min(32,n-start);
        Require(s.GetProperty("CaptureStartIndex").GetInt32()==start && s.GetProperty("WindowTruncated").GetBoolean()==(count<n),"window-header");
        var events=s.GetProperty("Events");Require(events.ValueKind==JsonValueKind.Array && events.GetArrayLength()==count,"event-cap-or-type");
        var payloads=new HashSet<string>(StringComparer.Ordinal);
        int offset=0;
        foreach(var e in events.EnumerateArray())
        {
            Fields(e,"SourceIndex,CommentaryEventID,CommentaryEventName,TimeSlice,Duration,ContributorTimeSlice,Flags,PlayerUniqueIDs");
            Require(e.GetProperty("SourceIndex").GetInt32()==start+offset++,"window-indices");
            foreach(var k in new[]{"CommentaryEventID","TimeSlice","Duration","ContributorTimeSlice","Flags"}) e.GetProperty(k).GetInt32();
            var name=e.GetProperty("CommentaryEventName");Require(name.ValueKind==JsonValueKind.Null || name.ValueKind==JsonValueKind.String && name.GetString()!.Length<=256,"invalid-name");
            var ids=e.GetProperty("PlayerUniqueIDs");Require(ids.ValueKind==JsonValueKind.Null || ids.ValueKind==JsonValueKind.Array && ids.GetArrayLength()<=16,"invalid-participants");
            if(ids.ValueKind==JsonValueKind.Array) foreach(var id in ids.EnumerateArray()) id.GetInt32();
            Require(payloads.Add(Payload(e)),"ambiguous-records");
        }
    }
    public static string PassName(int code) => code switch
    {8041=>"EventAttemptPassFloor",8282=>"EventPassShortForward",8310=>"EventPassMediumForward",8324=>"EventPassMediumForwardIntoPath",_=>null};
}

public sealed class AnticipatoryPassSchedule
{
    private readonly int _playerId;
    private readonly long _frequency, _deadline;
    private long? _lastDecisionQpc, _pendingEnd;
    private int _lastDecisionFrame=-1, _pollCount;
    private JsonElement? _latest, _pending;
    public string AbortReason { get; private set; }
    private object _selected, _start, _abort;
    private readonly List<object> _observations=new(200);
    private long _callbackQpc;
    private int _callbackFrame;
    private long? _observingSequence;
    public object Trigger { get; private set; }
    public object Report => new {Rule=AnticipatoryPassPolicy.Rule,Parameters=AnticipatoryPassPolicy.Parameters,
        Selected=_selected,Start=_start,Observations=_observations.ToArray(),Abort=_abort};
    private string Refuse(string reason)
    {
        if(AbortReason==null)
        { AbortReason=reason;_abort=new {Reason=reason,QpcTicks=_callbackQpc,FrameCount=_callbackFrame,SnapshotSequence=_observingSequence}; }
        return "aborted";
    }
    public long? TriggerQpcTicks { get; private set; }
    public AnticipatoryPassSchedule(SemanticEventRequest request,int playerId,JsonElement knownActors,long frequency,long startedQpc)
    {
        AnticipatoryPassPolicy.Require(request!=null && request.Action=="start-events" && request.OriginalPassDemo && request.AnticipatoryPassDemo
            && !string.IsNullOrEmpty(request.Session) && request.Session.Length<=128 && !string.IsNullOrEmpty(request.RequestId)
            && request.RequestId.Length<=64 && request.RequestId.All(c=>(c>='a'&&c<='z')||(c>='A'&&c<='Z')||(c>='0'&&c<='9')||c=='_'||c=='-'),"invalid-request");
        AnticipatoryPassPolicy.Require(playerId>0 && knownActors.ValueKind==JsonValueKind.Array && knownActors.GetArrayLength()<=64,"invalid-cache");
        int matches=0;
        foreach(var actor in knownActors.EnumerateArray())
        {
            int id=actor.GetProperty("PlayerId").GetInt32(),instance=actor.GetProperty("ActorInstanceId").GetInt32();
            if(id!=playerId) continue;
            matches++;
            AnticipatoryPassPolicy.Require(instance!=0 && instance==request.ActorInstanceId,"cached-actor-mismatch");
        }
        AnticipatoryPassPolicy.Require(matches==1,"ambiguous-cached-player");
        AnticipatoryPassPolicy.Require(frequency>0 && startedQpc>=0 && request.MaximumSeconds>=1 && request.MaximumSeconds<=20
            && (decimal)startedQpc+frequency*(decimal)request.MaximumSeconds<=long.MaxValue,"invalid-clock-budget");
        _frequency=frequency;_deadline=(long)((decimal)startedQpc+frequency*(decimal)request.MaximumSeconds);
        _playerId=playerId;
    }
    public string Observe(JsonElement snapshot,long qpc,int frame)
    {
        _callbackQpc=qpc;_callbackFrame=frame;_observingSequence=null;
        if(snapshot.ValueKind==JsonValueKind.Object && snapshot.TryGetProperty("Sequence",out var seq) && seq.ValueKind==JsonValueKind.Number && seq.TryGetInt64(out long value)) _observingSequence=value;
        string decision=ObserveCore(snapshot,qpc,frame);
        if(_observations.Count<200)
            _observations.Add(new {SnapshotSequence=_observingSequence,CallbackQpcTicks=qpc,CallbackFrame=frame,Decision=decision,
                Reason=decision=="aborted" ? AbortReason : decision=="wait" && !_pending.HasValue ? "no-eligible-future" : null});
        _observingSequence=null;
        return decision;
    }
    private string ObserveCore(JsonElement snapshot,long qpc,int frame)
    {
        if(AbortReason!=null) return "aborted";
        try { AnticipatoryPassPolicy.Validate(snapshot); }
        catch(Exception e) when(e is InvalidOperationException or KeyNotFoundException or FormatException or OverflowException)
        { return Refuse(e is InvalidOperationException ? e.Message : "invalid-producer-scalar"); }
        string continuity=Continuity(snapshot);
        if(continuity!=null) return Refuse(continuity);
        if(_latest.HasValue && (snapshot.GetProperty("CurrentFirstEventIndex").GetInt32()<_latest.Value.GetProperty("CurrentFirstEventIndex").GetInt32()
            || snapshot.GetProperty("CurrentLastEventIndex").GetInt32()<_latest.Value.GetProperty("CurrentLastEventIndex").GetInt32()))
            return Refuse("current-event-index-rewind");
        bool newPoll=!_latest.HasValue || snapshot.GetProperty("Sequence").GetInt64()!=_latest.Value.GetProperty("Sequence").GetInt64();
        if(newPoll && ++_pollCount>200) return Refuse("snapshot-cap");
        _latest=snapshot.Clone();
        string clock=DecisionClock(qpc,frame);
        if(clock!=null) return Refuse(clock);
        int involved=0;bool otherPendingFuture=false;
        JsonElement? candidate=null;
        foreach(var e in snapshot.GetProperty("Events").EnumerateArray())
        {
            var ids=e.GetProperty("PlayerUniqueIDs");
            long delta=(long)e.GetProperty("TimeSlice").GetInt32()-snapshot.GetProperty("CurrentTimeSlice").GetInt32();
            if(delta<1 || delta>8 || ids.ValueKind!=JsonValueKind.Array || !ids.EnumerateArray().Any(id=>id.GetInt32()==_playerId)) continue;
            involved++;
            if(_pending.HasValue && e.GetProperty("SourceIndex").GetInt32()!=_pending.Value.GetProperty("SourceIndex").GetInt32()) otherPendingFuture=true;
            var expected=AnticipatoryPassPolicy.PassName(e.GetProperty("CommentaryEventID").GetInt32());
            if(e.GetProperty("SourceIndex").GetInt32()>snapshot.GetProperty("CurrentLastEventIndex").GetInt32()
                && ids[0].GetInt32()==_playerId && expected!=null && expected==e.GetProperty("CommentaryEventName").GetString()) candidate=e;
        }
        if(involved>1 || otherPendingFuture) return Refuse("competing-future-records");
        if(_pending.HasValue)
        {
            int index=_pending.Value.GetProperty("SourceIndex").GetInt32();
            var matches=snapshot.GetProperty("Events").EnumerateArray().Where(e=>e.GetProperty("SourceIndex").GetInt32()==index).ToArray();
            if(matches.Length!=1 || AnticipatoryPassPolicy.Payload(matches[0])!=AnticipatoryPassPolicy.Payload(_pending.Value)) return Refuse("pending-changed-or-disappeared");
            if(!TriggerQpcTicks.HasValue)
            {
                if(((decimal)qpc-_pendingEnd!.Value)*1000>150m*_frequency) return Refuse("pending-expired");
                if(!candidate.HasValue || candidate.Value.GetProperty("SourceIndex").GetInt32()!=index) return Refuse("pending-no-longer-future");
            }
        }
        if(TriggerQpcTicks.HasValue) return "started";
        if(candidate.HasValue && !_pending.HasValue)
        {
            _pending=candidate.Value.Clone();_pendingEnd=snapshot.GetProperty("QpcEndTicks").GetInt64();
            _selected=new {SnapshotSequence=snapshot.GetProperty("Sequence").GetInt64(),Event=_pending.Value,
                ObservedQpcTicks=qpc,ObservedFrame=frame,QpcEndTicks=_pendingEnd.Value};
            Trigger=new {Snapshot=snapshot.Clone(),Event=_pending.Value,DetectionQpcTicks=qpc};
            return "selected";
        }
        return "wait";
    }
    private string Continuity(JsonElement s)
    {
        long sequence=s.GetProperty("Sequence").GetInt64();
        if(!_latest.HasValue) return sequence==0 ? null : "first-sequence";
        var p=_latest.Value;long previousSequence=p.GetProperty("Sequence").GetInt64();
        bool newPoll=sequence!=previousSequence;
        if(!newPoll && s.GetRawText()!=p.GetRawText()) return "mutated-snapshot";
        if(newPoll && (decimal)sequence!=(decimal)previousSequence+1) return "sequence-gap-or-reversal";
        if(s.GetProperty("FrameCount").GetInt32()<p.GetProperty("FrameCount").GetInt32()
            || s.GetProperty("QpcTicks").GetInt64()<p.GetProperty("QpcTicks").GetInt64()
            || s.GetProperty("QpcEndTicks").GetInt64()<p.GetProperty("QpcEndTicks").GetInt64()) return "poll-reversal";
        if(newPoll && s.GetProperty("QpcTicks").GetInt64()<=p.GetProperty("QpcEndTicks").GetInt64()) return "overlapping-polls";
        if(((decimal)s.GetProperty("QpcEndTicks").GetInt64()-p.GetProperty("QpcEndTicks").GetInt64())*1000>250m*_frequency) return "poll-gap";
        long slice=(long)s.GetProperty("CurrentTimeSlice").GetInt32()-p.GetProperty("CurrentTimeSlice").GetInt32();
        double time=s.GetProperty("CurrentTime").GetDouble()-p.GetProperty("CurrentTime").GetDouble();
        if(slice<0 || slice>8 || time<0 || time>1) return "narrative-discontinuity";
        foreach(var k in new[]{"CurrentPlaybackSpeed","CurrentPlaybackMode","MatchPeriod"})
            if(s.GetProperty(k).GetDouble()!=p.GetProperty(k).GetDouble()) return "literal-context-change";
        return null;
    }
    private string DecisionClock(long qpc,int frame)
    {
        if(qpc<0 || frame<0 || _lastDecisionQpc.HasValue && (qpc<_lastDecisionQpc.Value || frame<_lastDecisionFrame)) return "decision-reversal";
        if(qpc>=_deadline) return "expired-deadline";
        if(_lastDecisionQpc.HasValue && ((decimal)qpc-_lastDecisionQpc.Value)*1000>250m*_frequency) return "decision-gap";
        if(_latest.HasValue)
        {
            var s=_latest.Value;
            if(qpc<s.GetProperty("QpcEndTicks").GetInt64() || frame<s.GetProperty("FrameCount").GetInt32()) return "decision-before-read";
            if(((decimal)qpc-s.GetProperty("QpcEndTicks").GetInt64())*1000>150m*_frequency) return "stale-read";
        }
        _lastDecisionQpc=qpc;_lastDecisionFrame=frame;
        return null;
    }
    // Constant work on cached scalar state; never enumerates or serializes a source snapshot.
    public string Check(long qpc,int frame)
    {
        _callbackQpc=qpc;_callbackFrame=frame;_observingSequence=null;
        if(AbortReason!=null) return "aborted";
        string clock=DecisionClock(qpc,frame);
        if(clock!=null) return Refuse(clock);
        if(!TriggerQpcTicks.HasValue && _pendingEnd.HasValue && ((decimal)qpc-_pendingEnd.Value)*1000>150m*_frequency)
            return Refuse("pending-expired");
        return TriggerQpcTicks.HasValue ? "started" : "wait";
    }
    public string TryStart(long qpc,int frame,int lastPoseFrame)
    {
        string cached=Check(qpc,frame);
        if(cached=="aborted" || cached=="started") return cached;
        if(frame<lastPoseFrame) return Refuse("decision-reversal");
        if(frame==lastPoseFrame || !_pending.HasValue) return "wait";
        if((decimal)qpc*10+9m*_frequency>=(decimal)_deadline*10) return Refuse("insufficient-deadline");
        TriggerQpcTicks=qpc;
        _start=new {SnapshotSequence=_latest!.Value.GetProperty("Sequence").GetInt64(),QpcTicks=qpc,FrameCount=frame};
        return "started";
    }
}
