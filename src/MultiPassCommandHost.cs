using System.Text.Json;
using UnityEngine;
namespace FM26.VisualDiagnostics;

internal sealed class MultiPassCommandHost
{
    private MultiPassCoordinator _group;
    private int _commands;
    private bool _writerFailed;
    public bool Blocked=>_writerFailed || (_group?.Blocked ?? false);
    public void Tick()=>_group?.Tick();
    public void Shutdown()=>_group?.Shutdown();
    public void Process(GameObject root,List<PlayerBinding> players,SemanticEventObserver reader,bool blocked,bool overlap)
    {
        string path=Path.Combine(DiagnosticsPlugin.Output,"multi-pass-command.json");
        if(_commands>=MultiPassContract.MaximumCommands || !File.Exists(path))return;
        _commands++;
        MultiPassRequest request=null;bool startedHere=false;
        try
        {
            byte[] bytes=MultiPassContract.Read(path);
            using var doc=JsonDocument.Parse(bytes);request=MultiPassContract.Parse(doc.RootElement,DiagnosticsPlugin.Session);
            if(request.Action=="start-multi-pass")
            {
                if(blocked||overlap||Blocked||reader.Enabled||reader.Attempted)throw new InvalidOperationException("Multi observation, attempt or cleanup already active.");
                var group=new MultiPassCoordinator(request,reader);
                group.Prepare(root,players);
                // Own the coordinator and all worker slots before starting/allocating.
                _group=group;
                MultiPassContract.WriteRaw(Path.Combine(DiagnosticsPlugin.Output,$"multi-pass-command-{request.RequestId}.json"),bytes,4096);
                MultiPassContract.Write(Path.Combine(DiagnosticsPlugin.Output,$"multi-pass-reader-{request.RequestId}.json"),new{
                    SchemaVersion=1,request.Session,request.RequestId,ReaderRequest=request.ReaderRequest,Derivation="ordered-primary-reader-only"},MultiPassContract.MaximumDerivationBytes);
                group.Begin(root,players,overlap);startedHere=group.ReaderAccepted;
            }
            else
            {
                if(_group==null || _group.RequestId!=request.RequestId || !reader.Enabled)throw new InvalidOperationException("Multi stop must identify current accepted group.");
                _group.Stop("requested-stop");
            }
            WriteResult(request,true,null,_commands);
            if(request.Action=="start-multi-pass")_group.ResultPublished=true;
        }
        catch(Exception e)
        {
            bool consumed=_group?.ReaderAccepted??false;
            if(consumed && (startedHere||_group.RequestId==request?.RequestId))_group.Stop("acceptance-write-error",MultiPassContract.Error(e));
            try{WriteResult(request,false,MultiPassContract.Error(e),_commands);}catch(Exception writer){_writerFailed=true;DiagnosticsPlugin.Logger.LogError("[VISDIAG] MULTI_RESULT_FAILED: "+MultiPassContract.Error(writer));}
        }
        finally
        {
            try{File.Delete(path);}catch(Exception e){_writerFailed=true;_group?.Stop("command-delete-error",MultiPassContract.Error(e));}
        }
    }
    private void WriteResult(MultiPassRequest request,bool accepted,string error,int number)
    {
        string name=request==null?$"multi-pass-rejection-{number}.json":request.Action=="stop-multi-pass"?$"multi-pass-stop-result-{request.RequestId}.json":$"multi-pass-result-{request.RequestId}.json";
        // Rejected repeats cannot overwrite the real accepted request-bound result.
        string path=Path.Combine(DiagnosticsPlugin.Output,name);
        if(!accepted && (File.Exists(path)||Directory.Exists(path)))path=Path.Combine(DiagnosticsPlugin.Output,$"multi-pass-rejection-{number}.json");
        MultiPassContract.Write(path,new{SchemaVersion=1,Session=DiagnosticsPlugin.Session,RequestId=request?.RequestId,Action=request?.Action,Accepted=accepted,
            ReaderAccepted=_group?.ReaderAccepted??false,WorkersCreated=_group?.WorkersCreated??0,Error=error},MultiPassContract.MaximumResultBytes);
    }
}
