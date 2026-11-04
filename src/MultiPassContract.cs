using System.Text.Json;
namespace FM26.VisualDiagnostics;

internal sealed class MultiPassRequest
{
    private readonly int[] _ids;
    public string Session { get; }
    public string RequestId { get; }
    public string Action { get; }
    public int[] ActorInstanceIds => (int[])_ids.Clone();
    public SemanticEventRequest ReaderRequest { get; }
    public MultiPassRequest(string session,string requestId,string action,int[] ids)
    {
        Session=session;RequestId=requestId;Action=action;_ids=(int[])ids.Clone();
        if(action=="start-multi-pass") ReaderRequest=MultiPassContract.ActorRequest(this,_ids[0],requestId+"-reader");
    }
}

internal static class MultiPassContract
{
    public const int MaximumSeconds=12,MaximumCommandBytes=4096,MaximumCommands=16,MaximumMetadataBytes=196608;
    public const int MaximumActorBytes=16777216,MaximumGroupBytes=131072,MaximumResultBytes=16384,MaximumDerivationBytes=8192;
    public static object Caps => new {Workers=3,MaximumSeconds,MaximumCommandBytes,MaximumCommands,MaximumMetadataBytes,
        MaximumActorBytes,MaximumGroupBytes,MaximumResultBytes,MaximumDerivationBytes,MaximumNativeBytes=8388608,
        ExpectedGroupHandles=252,MaximumGroupHandles=384,MaximumGroupTimelineRows=6144,MaximumGroupObservations=600,MaximumArchiveBytes=58880000};
    public static MultiPassRequest Parse(JsonElement data,string session)
    {
        if(data.ValueKind!=JsonValueKind.Object)throw new InvalidOperationException("multi-object");
        string action=data.GetProperty("Action").GetString();
        bool start=action=="start-multi-pass";
        if(!start && action!="stop-multi-pass")throw new InvalidOperationException("multi-action");
        var fields=new HashSet<string>((start ? "SchemaVersion,Session,RequestId,Action,ActorInstanceIds,MaximumSeconds" : "SchemaVersion,Session,RequestId,Action").Split(','),StringComparer.Ordinal);
        foreach(var p in data.EnumerateObject()) if(!fields.Remove(p.Name))throw new InvalidOperationException("multi-unknown-or-duplicate-field");
        if(fields.Count!=0 || data.GetProperty("SchemaVersion").GetInt32()!=1 || data.GetProperty("Session").ValueKind!=JsonValueKind.String
            || string.IsNullOrEmpty(session) || session.Length>128 || data.GetProperty("Session").GetString()!=session)
            throw new InvalidOperationException("multi-schema-or-session");
        string id=data.GetProperty("RequestId").GetString();
        if(string.IsNullOrEmpty(id)||id.Length>48||id.Any(c=>!((c>='a'&&c<='z')||(c>='A'&&c<='Z')||(c>='0'&&c<='9')||c=='_'||c=='-')))
            throw new InvalidOperationException("multi-request-id");
        int[] ids=Array.Empty<int>();
        if(start)
        {
            var values=data.GetProperty("ActorInstanceIds");
            if(values.ValueKind!=JsonValueKind.Array || values.GetArrayLength()!=3 || data.GetProperty("MaximumSeconds").GetInt32()!=12)
                throw new InvalidOperationException("multi-three-and-twelve");
            ids=values.EnumerateArray().Select(v=>v.GetInt32()).ToArray();
            if(ids.Any(v=>v==0)||ids.Distinct().Count()!=3)throw new InvalidOperationException("multi-actor-identities");
        }
        return new MultiPassRequest(session,id,action,ids);
    }
    public static SemanticEventRequest ActorRequest(MultiPassRequest group,int actor,string requestId) =>
        SemanticEventPolicy.Parse(JsonSerializer.SerializeToElement(new {Session=group.Session,RequestId=requestId,Action="start-events",ActorInstanceId=actor,MaximumSeconds=12,OriginalPassDemo=true,AnticipatoryPassDemo=true}),group.Session);
    public static byte[] Read(string path)
    {
        using var file=new FileStream(path,FileMode.Open,FileAccess.Read,FileShare.Read);
        if(file.Length>MaximumCommandBytes)throw new InvalidOperationException("multi-command-bytes");
        var buffer=new byte[MaximumCommandBytes+1];int total=0,read;
        while(total<buffer.Length && (read=file.Read(buffer,total,buffer.Length-total))>0)total+=read;
        if(total>MaximumCommandBytes)throw new InvalidOperationException("multi-command-growth");
        return buffer.Take(total).ToArray();
    }
    public static void Write(string target,object value,int maximum)
    {
        WriteRaw(target,JsonSerializer.SerializeToUtf8Bytes(value),maximum);
    }
    public static void WriteRaw(string target,byte[] bytes,int maximum)
    {
        if(bytes.Length>maximum)throw new InvalidOperationException("multi-output-bytes");
        string temporary=target+"."+Guid.NewGuid().ToString("N")+".tmp";
        try
        {
            using(var file=new FileStream(temporary,FileMode.CreateNew,FileAccess.Write,FileShare.None)){file.Write(bytes);file.Flush(true);}
            File.Move(temporary,target,false);
            // Actual target readback, not an inference from writer completion.
            byte[] published=File.ReadAllBytes(target);
            if(!bytes.SequenceEqual(published))throw new IOException("multi-publication-readback");
        }
        finally{if(File.Exists(temporary))File.Delete(temporary);}
    }
    public static string Error(Exception e)=>OriginalError(e.Message);
    public static string OriginalError(string error)=>error==null?null:error.Substring(0,Math.Min(error.Length,4096));
}
