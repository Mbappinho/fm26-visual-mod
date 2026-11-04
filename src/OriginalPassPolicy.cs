using System.Numerics;
using System.Text.Json;

namespace FM26.VisualDiagnostics;

public sealed class OriginalPassSchedule
{
    private int? _lastSlice;
    private float _lastTime;
    private long? _lastQpc;
    public long? TriggerQpcTicks { get; private set; }
    public JsonElement? Detect(JsonElement snapshot,int playerId,long detectionQpc)
    {
        int slice=snapshot.GetProperty("CurrentTimeSlice").GetInt32();
        float time=snapshot.GetProperty("CurrentTime").GetSingle();
        if(!float.IsFinite(time) || (_lastQpc.HasValue && detectionQpc<_lastQpc.Value)
            || (_lastSlice.HasValue && !OriginalPassPolicy.NativeClockForward(_lastSlice.Value,slice,_lastTime,time)))
            throw new InvalidOperationException("Backward raw narrative slice/time or detection QPC; not repaired.");
        _lastSlice=slice;_lastTime=time;_lastQpc=detectionQpc;
        if(TriggerQpcTicks.HasValue) return null;
        var candidate=OriginalPassPolicy.Trigger(snapshot,playerId);
        if(candidate.HasValue) TriggerQpcTicks=detectionQpc;
        return candidate;
    }
}

// Original dimensions and illustrative right-foot motion, never native measurements.
public static class OriginalPassPolicy
{
    public const double ClipDurationSeconds = 0.9;
    public const string TriggerRule = "HYPOTHESIS: exact whitelist; slot0 selected positive PlayerId; current-first marker in raw first..last and source bounds; raw slice age 0..2; once";
    public static JsonElement? Trigger(JsonElement snapshot, int selectedPlayerId)
    {
        if(selectedPlayerId <= 0 || snapshot.GetProperty("SourceListNull").GetBoolean()) return null;
        int first=snapshot.GetProperty("CurrentFirstEventIndex").GetInt32(), last=snapshot.GetProperty("CurrentLastEventIndex").GetInt32();
        int count=snapshot.GetProperty("SourceCount").GetInt32(), slice=snapshot.GetProperty("CurrentTimeSlice").GetInt32();
        if(count < 0 || count > 8192 || first < 0 || first >= count || last < first || last >= count) return null;
        JsonElement? found=null;
        foreach(var e in snapshot.GetProperty("Events").EnumerateArray())
        {
            if(e.GetProperty("SourceIndex").GetInt32()!=first) continue;
            // Duplicate marker entries are ambiguous, even when identical.
            if(found.HasValue) return null;
            found=e;
        }
        if(!found.HasValue) return null;
        var value=found.Value;
        var expected=value.GetProperty("CommentaryEventID").GetInt32() switch
        {8041=>"EventAttemptPassFloor",8282=>"EventPassShortForward",8310=>"EventPassMediumForward",8324=>"EventPassMediumForwardIntoPath",_=>null};
        var ids=value.GetProperty("PlayerUniqueIDs");
        long age=(long)slice-value.GetProperty("TimeSlice").GetInt32();
        if(expected==null || value.GetProperty("CommentaryEventName").GetString()!=expected || ids.ValueKind!=JsonValueKind.Array
            || ids.GetArrayLength()==0 || ids.GetArrayLength()>16 || ids[0].GetInt32()!=selectedPlayerId || age<0 || age>2) return null;
        return value.Clone();
    }
    public static double Phase(long now, long detection, long frequency)
    {
        if(frequency <= 0 || now < detection) throw new InvalidOperationException("Invalid owned clip clock.");
        return (double)(((decimal)now-detection)/frequency/(decimal)ClipDurationSeconds);
    }
    public static bool CleanupVerified(int requestedFrame, int observedFrame, int alive, int unknown) =>
        requestedFrame >= 0 && observedFrame > requestedFrame && alive == 0 && unknown == 0;
    public static bool CanCertifyCleanup(int requestedFrame,int observedFrame,int alive,int unknown,bool allocationUncertain) =>
        !allocationUncertain && CleanupVerified(requestedFrame,observedFrame,alive,unknown);
    public static bool OriginalFlagsUnchanged(JsonElement flags) => flags.ValueKind==JsonValueKind.Array
        && flags.EnumerateArray().All(f=>f.TryGetProperty("Status",out var s) && s.ValueKind==JsonValueKind.String && s.GetString()=="unchanged");
    public static bool NativeClockForward(int previousSlice, int slice, float previousTime, float time) =>
        float.IsFinite(previousTime) && float.IsFinite(time) && slice >= previousSlice && time >= previousTime;
    public static string UpdateDecision(long now,long start,long frequency,int seconds,int frame,int lastFrame,bool context,int rows)
    {
        if(frequency<=0 || seconds<1 || seconds>20 || now<start || frame<lastFrame || !context) return "context-changed";
        if((decimal)now-start >= (decimal)frequency*seconds) return "stop-time-budget";
        if(rows>=2048) return "stop-timeline-budget";
        return frame==lastFrame ? "wait" : "update";
    }
    public static void RequireWriteWindow(bool enabled,long now,long start,long frequency,int seconds)
    {
        if(!enabled) throw new InvalidOperationException("Shared observer inactive before owned write or actor-root read.");
        if((decimal)now-start >= (decimal)frequency*seconds)
            throw new TimeoutException("Shared observer deadline before owned write or actor-root read.");
    }
    #nullable disable
    public static (string Reason,string Error) ClassifyFailure(Exception error,string failureReason) =>
        error is TimeoutException ? ("stop-time-budget",null) : (failureReason,error.ToString());
    public static string CanonicalError(string error) => error==null ? null : error.Substring(0,Math.Min(error.Length,4096));
    #nullable restore
    public static readonly string[] JointNames = { "pelvis", "chest", "neck", "head", "leftShoulder", "leftElbow", "leftWrist", "rightShoulder", "rightElbow", "rightWrist", "leftHip", "leftKnee", "leftAnkle", "rightHip", "rightKnee", "rightAnkle", "rightToe", "leftToe", "headTop", "leftHand", "rightHand" };
    public static readonly int[] Edges = {0,1,1,2,2,3,3,18,1,4,4,5,5,6,1,7,7,8,8,9,0,10,10,11,11,12,12,17,0,13,13,14,14,15,15,16,6,19,9,20};
    public static Vector3[] Pose(double phase)
    {
        if(!double.IsFinite(phase) || phase < 0 || phase > 1) throw new ArgumentOutOfRangeException(nameof(phase));
        // Keyed angular motion preserves bone lengths; local forward is +Z.
        double hip = phase <= .25 ? -.65 * phase/.25 : phase <= .6 ? -.65 + 1.5*(phase-.25)/.35 : .85*(1-phase)/.4;
        double knee = phase <= .25 ? .8*phase/.25 : phase <= .6 ? .8*(.6-phase)/.35 : 0;
        var p = new Vector3[21];
        p[0]=new(0,.96f,0); p[1]=new(0,1.4f,0); p[2]=new(0,1.53f,0); p[3]=new(0,1.66f,0); p[18]=new(0,1.8f,0);
        p[4]=new(-.24f,1.4f,0);p[5]=p[4]+new Vector3(-.08f,-.27f,0);p[6]=p[5]+new Vector3(0,-.25f,.04f);
        p[7]=new(.24f,1.4f,0);p[8]=p[7]+new Vector3(.08f,-.27f,0);p[9]=p[8]+new Vector3(0,-.25f,.04f);
        p[19]=p[6]+new Vector3(0,-.11f,0);p[20]=p[9]+new Vector3(0,-.11f,0);
        p[10]=new(-.12f,.96f,0);p[11]=p[10]+new Vector3(0,-.43f,0);p[12]=p[11]+new Vector3(0,-.43f,0);p[17]=p[12]+new Vector3(0,0,.2f);
        p[13]=new(.12f,.96f,0);p[14]=p[13]+Leg(hip);p[15]=p[14]+Leg(hip-knee);p[16]=p[15]+new Vector3(0,0,.2f);
        return p;
    }
    private static Vector3 Leg(double angle) => new(0,-.43f*(float)Math.Cos(angle),.43f*(float)Math.Sin(angle));
}
