using System.Diagnostics;
using System.Text.Json;
using UnityEngine;
namespace FM26.VisualDiagnostics;

internal sealed class MultiPassCoordinator
{
    private readonly MultiPassRequest _request;
    private readonly SemanticEventObserver _reader;
    private readonly MultiPassWorker[] _workers={new(),new(),new()};
    private bool _stopping,_groupAttempted,_prepared,_readerPublished,_terminal;
    private string _reason,_error;
    private readonly List<object> _failures=new();
    private bool _failureOverflow;
    private object _fanoutFailure;
    private void RecordFailure(string reason,string error)
    {
        PublicationFailed=true;
        string bounded=MultiPassContract.OriginalError(error)??reason;
        if(bounded.Length>256)bounded=bounded.Substring(0,256);
        if(_failures.Count<4)_failures.Add(new{Reason=reason,Error=bounded});
        else _failureOverflow=true;
    }
    public bool ReaderAccepted {get;private set;}
    public bool ResultPublished {get;set;}
    public bool PublicationFailed {get;private set;}
    public bool Blocked => ReaderAccepted && (!_groupAttempted || PublicationFailed || _workers.Any(w=>w.OverlapActive));
    public int WorkersCreated => _workers.Count(w=>w.Created);
    public string RequestId=>_request.RequestId;
    public static object Certifications=>new{CachedActorIdentityCorroborated=false,RendererWitnessVerified=false,PixelVisibilityVerified=false,
        NativeActionCommandVerified=false,PerformerRoleVerified=false,EventMembershipVerified=false,NativeFootChoiceVerified=false,
        ContactTimingVerified=false,NativePublicationVerified=false,PoseFidelityVerified=false,RootCoherenceVerified=false,
        TransformReadbackVerified=false,VisualReplacementActivated=false,SimulationModified=false,ProtectedProducerAccessed=false,NativePosePayloadAccessed=false};
    public MultiPassCoordinator(MultiPassRequest request,SemanticEventObserver reader){_request=request;_reader=reader;}
    public void Prepare(GameObject root,List<PlayerBinding> players)
    {
        if(_reader.SnapshotCopied!=null || _reader.Stopped!=null || _reader.AdditionalContext!=null || _reader.FinalPublished!=null)
            throw new InvalidOperationException("Multi reader callbacks already owned.");
        var cache=players.Take(64).ToArray();var ids=_request.ActorInstanceIds;
        var selected=new PlayerBinding[3];
        for(int slot=0;slot<3;slot++)
        {
            var matches=players.Where(p=>p.ActorInstanceId==ids[slot]).ToArray();
            if(matches.Length!=1 || !cache.Contains(matches[0]) || !matches[0].Eligible || matches[0].PlayerId<=0 || matches[0].ShirtNumber<=0
                || cache.Count(p=>p.PlayerId==matches[0].PlayerId)!=1)
                throw new InvalidOperationException("Multi target must be unique positive current eligible cached actor.");
            selected[slot]=matches[0];
        }
        if(selected.Select(p=>p.PlayerId).Distinct().Count()!=3)throw new InvalidOperationException("Multi aliased players.");
        for(int slot=0;slot<3;slot++)_workers[slot].Prepare(_request,slot,root,selected[slot],Stop,CheckAliases);
        var metadata=new{SelectedActors=_workers.Select(w=>w.SelectedActor).ToArray(),KnownActors=cache.Select(p=>new{p.ActorInstanceId,p.PlayerId,p.Name,p.TeamId,p.ShirtNumber}).ToArray(),OriginalRendererFlags=_workers.Select(w=>w.Flags()).ToArray()};
        if(JsonSerializer.SerializeToUtf8Bytes(metadata).Length>MultiPassContract.MaximumMetadataBytes)throw new InvalidOperationException("Multi aggregate metadata reserve.");
        foreach(string name in RequiredFiles())if(File.Exists(Path.Combine(DiagnosticsPlugin.Output,name)) || Directory.Exists(Path.Combine(DiagnosticsPlugin.Output,name)))throw new InvalidOperationException("Multi request-bound target already exists.");
        _prepared=true;
    }
    private IEnumerable<string> RequiredFiles()
    {
        yield return $"multi-pass-command-{RequestId}.json";yield return $"multi-pass-result-{RequestId}.json";
        yield return $"multi-pass-reader-{RequestId}.json";yield return $"multi-pass-group-{RequestId}.json";
        yield return $"semantic-events-{RequestId}-reader.json";
        for(int i=0;i<3;i++)yield return $"multi-pass-actor-{RequestId}-{i}.json";
    }
    public void Begin(GameObject root,List<PlayerBinding> players,bool overlap)
    {
        if(!_prepared)throw new InvalidOperationException("Multi preflight missing.");
        _reader.AdditionalContext=RequireAll;
        try
        {
            _reader.Start(_request.ReaderRequest,root,players,overlap);
            ReaderAccepted=true;
            // One coordinator owns callbacks; no worker subscribes or stops reader.
            _reader.SnapshotCopied=Snapshot;_reader.Stopped=ReaderStopped;_reader.FinalPublished=()=>_readerPublished=true;
            foreach(var worker in _workers)worker.BindReader(_reader);
            foreach(var worker in _workers)worker.Initialize(_reader);
            foreach(var worker in _workers){if(_stopping)break;worker.Allocate();}
        }
        catch(Exception e)
        {
            if(ReaderAccepted)Stop(e is TimeoutException?"stop-time-budget":"owned-start-failure",e is TimeoutException?null:MultiPassContract.Error(e));
            else _reader.AdditionalContext=null;
            throw;
        }
    }
    private void RequireAll()
    {
        foreach(var worker in _workers){worker.RequireNativeContext();worker.RequireTargetVisualContext();worker.RequireFlags();}
    }
    private void CheckAliases()
    {
        var native=new HashSet<int>();
        foreach(var worker in _workers)foreach(int id in worker.NativeIds)if(id!=0)native.Add(id);
        var owned=new HashSet<int>();
        foreach(var worker in _workers)foreach(int id in worker.OwnedIds)
            if(id!=0 && (!owned.Add(id)||native.Contains(id)))throw new InvalidOperationException("Multi owned/native or cross-worker alias.");
    }
    private void Snapshot(JsonElement snapshot,long qpc)
    {
        if(_stopping)return;
        int slot=0;
        try
        {
            for(;slot<_workers.Length;slot++){if(_stopping)break;_workers[slot].Snapshot(snapshot,qpc);}
            if(_workers.All(w=>!w.Active))Stop("all-actors-terminal");
        }
        catch(Exception e)
        {
            // Returned calls are not observations. Only an interrupted worker call
            // witnesses the bounded attempted/returned prefix of this actual copy.
            if(!_stopping && e is not TimeoutException && slot<_workers.Length)
                _fanoutFailure=new{SnapshotSequence=snapshot.GetProperty("Sequence").GetInt32(),CallbackQpcTicks=qpc,AttemptedSlot=slot,ReturnedSlots=Enumerable.Range(0,slot).ToArray()};
            Stop(e is TimeoutException?"stop-time-budget":"owned-trigger-failure",e is TimeoutException?null:MultiPassContract.Error(e));
        }
    }
    private void ReaderStopped(string reason,string error)
    {
        bool clean=(reason=="stop-time-budget" || reason=="requested-stop") && error==null;
        Stop(reason,clean?null:error??"Shared reader failed without an error payload.");
    }
    public void Stop(string reason,string error=null)
    {
        if(_terminal)return;
        if(_stopping){if(error!=null)RecordFailure(reason,error);return;}
        _stopping=true;_reason=reason;_error=MultiPassContract.OriginalError(error);
        foreach(var worker in _workers)
        {
            try{worker.Stop(reason,error);}
            catch(Exception e){RecordFailure("multi-coordinator-failure",MultiPassContract.Error(e));}
        }
        if(_reader.Enabled)
        {
            bool clean=_error==null && (reason=="all-actors-terminal" || reason=="requested-stop" || reason=="stop-time-budget");
            _reader.Stop(clean?(reason=="stop-time-budget"?reason:"requested-stop"):"owned-demo-failure",clean?null:_error??reason);
        }
    }
    public void Tick()
    {
        if(!ReaderAccepted || _terminal)return;
        try
        {
            foreach(var worker in _workers)worker.CleanupTick();
            if(!_stopping)
            {
                if(!_reader.Enabled){Stop("reader-inactive","Shared reader inactive without stop callback.");}
                else foreach(var worker in _workers){if(_stopping)break;worker.Tick();}
                if(!_stopping && _workers.All(w=>!w.Active))Stop("all-actors-terminal");
            }
            if(_stopping && _workers.All(w=>w.Finalized))Publish();
        }
        catch(Exception e){RecordFailure("multi-coordinator-failure",MultiPassContract.Error(e));Stop(e is TimeoutException?"stop-time-budget":"multi-coordinator-failure",e is TimeoutException?null:MultiPassContract.Error(e));}
    }
    private bool Readable(string name,int cap)
    {
        try
        {
            string path=Path.Combine(DiagnosticsPlugin.Output,name);
            using var file=new FileStream(path,FileMode.Open,FileAccess.Read,FileShare.Read);
            if(file.Length>cap)return false;
            using var doc=JsonDocument.Parse(file);
            return doc.RootElement.GetProperty("Session").GetString()==_request.Session;
        }catch{return false;}
    }
    private void Publish()
    {
        if(_groupAttempted)return;
        _groupAttempted=true;
        bool native=_readerPublished && Readable($"semantic-events-{RequestId}-reader.json",8388608);
        bool[] actors=Enumerable.Range(0,3).Select(i=>_workers[i].Published&&Readable($"multi-pass-actor-{RequestId}-{i}.json",MultiPassContract.MaximumActorBytes)).ToArray();
        if(!native || actors.Any(v=>!v) || !ResultPublished)RecordFailure("required-publication-failure","Required multi publication missing or unreadable.");
        try
        {
            MultiPassContract.Write(Path.Combine(DiagnosticsPlugin.Output,$"multi-pass-group-{RequestId}.json"),new{
                SchemaVersion=1,_request.Session,_request.RequestId,ActorInstanceIds=_request.ActorInstanceIds,ReaderRequestId=_reader.RequestId,ReaderAccepted,ResultPublished,
                ReaderPublished=native,ActorPublished=actors,FanoutFailure=_fanoutFailure,StopReason=_reason,Error=_error,PublicationFailed,Failures=_failures,FailureOverflow=_failureOverflow,State="PARTIAL",Actors=_workers.Select(w=>w.Summary).ToArray(),Caps=MultiPassContract.Caps,Certifications},MultiPassContract.MaximumGroupBytes);
        }
        catch(Exception e){PublicationFailed=true;DiagnosticsPlugin.Logger.LogError("[VISDIAG] MULTI_GROUP_PUBLICATION_FAILED: "+MultiPassContract.Error(e));}
        // A successful final and finished resource obligations close this group only.
        _terminal=!PublicationFailed && !_reader.Enabled && _workers.All(w=>w.Finalized && !w.OverlapActive);
        // Cleared only after native reader has stopped. Never remove legacy callbacks.
        _reader.AdditionalContext=null;_reader.SnapshotCopied=null;_reader.Stopped=null;_reader.FinalPublished=null;
    }
    public void Shutdown()
    {
        if(!ReaderAccepted || _terminal)return;
        Stop("diagnostics-destroyed","Host destroyed; later-frame cleanup unconfirmed.");
        foreach(var worker in _workers)worker.Shutdown();
        PublicationFailed=true;Publish();
    }
}
