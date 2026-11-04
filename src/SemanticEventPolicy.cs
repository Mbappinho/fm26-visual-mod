using System.Text.Json;

namespace FM26.VisualDiagnostics;

public sealed class SemanticEventRequest
{
    public string Action { get; }
    public string Session { get; }
    public string RequestId { get; }
    public int ActorInstanceId { get; }
    public int MaximumSeconds { get; }
    public bool OriginalPassDemo { get; }
    public bool AnticipatoryPassDemo { get; }
    public SemanticEventRequest(string action, string session, string requestId, int actorInstanceId, int maximumSeconds, bool originalPassDemo = false, bool anticipatoryPassDemo = false)
    { Action = action; Session = session; RequestId = requestId; ActorInstanceId = actorInstanceId; MaximumSeconds = maximumSeconds; OriginalPassDemo = originalPassDemo; AnticipatoryPassDemo = anticipatoryPassDemo; }
}

public static class SemanticEventPolicy
{
    public sealed class EventWindow
    {
        public int Start { get; }
        public int Count { get; }
        public EventWindow(int start, int count) { Start = start; Count = count; }
    }

    public static bool SceneAssociation(int accepted, int root, int actor, int controller, int publisher) =>
        root == accepted && actor == accepted && controller == accepted && publisher == accepted;

    public static bool CanCaptureFrame(int frame, int lastFrame) => frame >= 0 && frame > lastFrame;

    public static EventWindow Window(int rawFirst, int sourceCount)
    {
        if (sourceCount < 0 || sourceCount > 8192) throw new InvalidOperationException("Source count outside bounds.");
        int start = (int)Math.Clamp((long)rawFirst - 1, 0, Math.Max(0, sourceCount - 1));
        return new EventWindow(start, Math.Min(32, sourceCount - start));
    }

    public static string Decide(long now, long start, long last, long frequency, int seconds, int count)
    {
        if (frequency <= 0 || seconds < 1 || seconds > 20 || count < 0 || now < start)
            throw new InvalidOperationException("Invalid monotonic policy inputs.");
        // Decimal avoids signed subtraction/multiplication overflow.
        if ((decimal)now - start >= (decimal)frequency * seconds) return "stop-time-budget";
        if (count >= 200) return "stop-snapshot-budget";
        if ((decimal)now - last < (decimal)frequency / 10) return "wait";
        return "capture";
    }

    public static SemanticEventRequest Parse(JsonElement data, string session)
    {
        if(data.ValueKind!=JsonValueKind.Object)throw new InvalidOperationException("Command must be an object.");
        var keys=new HashSet<string>(StringComparer.Ordinal);
        foreach(var p in data.EnumerateObject())
            if(!keys.Add(p.Name))throw new InvalidOperationException("Duplicate command field.");
        var action=data.GetProperty("Action").GetString();
        bool demo=data.TryGetProperty("OriginalPassDemo",out var flag);
        if(demo && (action!="start-events" || flag.ValueKind!=JsonValueKind.True))
            throw new InvalidOperationException("OriginalPassDemo must be literal true on start only.");
        if(demo) keys.Remove("OriginalPassDemo");
        bool anticipation=data.TryGetProperty("AnticipatoryPassDemo",out var futureFlag);
        if(anticipation && (action!="start-events" || !demo || futureFlag.ValueKind!=JsonValueKind.True))
            throw new InvalidOperationException("AnticipatoryPassDemo requires literal true with OriginalPassDemo on start only.");
        if(anticipation) keys.Remove("AnticipatoryPassDemo");
        var expected=action=="start-events"
            ? new[]{"Action","Session","RequestId","ActorInstanceId","MaximumSeconds"}
            : action=="stop-events" ? new[]{"Action","Session","RequestId"}
            : throw new InvalidOperationException("Unsupported semantic action.");
        if(!keys.SetEquals(expected))throw new InvalidOperationException("Unexpected/missing semantic command fields.");
        if(data.GetProperty("Session").GetString()!=session)throw new InvalidOperationException("Stale semantic session.");
        var id=data.GetProperty("RequestId").GetString();
        if(string.IsNullOrEmpty(id)||id.Length>64||id.Any(c=>!((c>='a'&&c<='z')||(c>='A'&&c<='Z')||(c>='0'&&c<='9')||c=='_'||c=='-')))
            throw new InvalidOperationException("Invalid literal semantic request ID.");
        var actor=action=="start-events" ? data.GetProperty("ActorInstanceId").GetInt32() : 0;
        var seconds=action=="start-events" ? data.GetProperty("MaximumSeconds").GetInt32() : 0;
        if(action=="start-events"&&(actor==0||seconds<1||seconds>20))throw new InvalidOperationException("Invalid semantic actor/time budget.");
        return new SemanticEventRequest(action,session,id,actor,seconds,demo,anticipation);
    }
}
