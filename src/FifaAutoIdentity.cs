#nullable disable
namespace FM26.VisualDiagnostics;

// Copied consumer identity only. No native handles or publisher identities invented.
public sealed class FifaAutoActorIdentity
{
    public int ActorInstanceId { get; }
    public int PlayerId { get; }
    public int TeamId { get; }
    public bool Goalkeeper { get; }
    public int TransformInstanceId { get; }
    public IReadOnlyList<int> RendererInstanceIds { get; }
    public FifaAutoActorIdentity(int actorInstanceId, int playerId, int teamId, bool goalkeeper,
        int transformInstanceId, int[] rendererInstanceIds)
    {
        ActorInstanceId = actorInstanceId; PlayerId = playerId; TeamId = teamId;
        Goalkeeper = goalkeeper; TransformInstanceId = transformInstanceId;
        RendererInstanceIds = rendererInstanceIds == null ? null : Array.AsReadOnly((int[])rendererInstanceIds.Clone());
    }
}

public sealed class FifaAutoCandidateIdentity
{
    public string Session { get; }
    public string SceneName { get; }
    public int SceneHandle { get; }
    public int RootInstanceId { get; }
    public int ControllerInstanceId { get; }
    public IReadOnlyList<FifaAutoActorIdentity> Actors { get; }
    public FifaAutoCandidateIdentity(string session, string sceneName, int sceneHandle,
        int rootInstanceId, int controllerInstanceId, FifaAutoActorIdentity[] actors)
    {
        Session = session; SceneName = sceneName; SceneHandle = sceneHandle;
        RootInstanceId = rootInstanceId; ControllerInstanceId = controllerInstanceId;
        // Actor carriers are sealed and already own their nested renderer copy.
        Actors = actors == null ? null : Array.AsReadOnly((FifaAutoActorIdentity[])actors.Clone());
    }
}

// Offline complete identity, selected-inventory binding and canonical token helpers.
// None of these certify native graph ownership or adapter freshness.
public static class FifaAutoIdentity
{
    public static bool IsComplete(FifaAutoCandidateIdentity identity)
        => identity != null && !string.IsNullOrEmpty(identity.Session) && !string.IsNullOrEmpty(identity.SceneName)
            && identity.SceneHandle != 0 && identity.RootInstanceId != 0 && identity.ControllerInstanceId != 0
            && identity.Actors != null && identity.Actors.Count > 0
            && identity.Actors.All(a => a != null && a.ActorInstanceId != 0 && a.PlayerId > 0
                && a.TransformInstanceId != 0 && a.RendererInstanceIds != null && a.RendererInstanceIds.Count > 0
                && a.RendererInstanceIds.All(id => id != 0));
    // Preconditions: complete identity and valid candidate, checked in Poll before calling.
    public static bool Matches(FifaAutoCandidateIdentity identity, FifaAutoPollObservation observation)
    {
        var selected = FifaActorSelection.ResolveFifa(new FifaOverlayRequest { AllEligible = true }, observation.Players);
        return string.Equals(identity.Session, observation.PluginSession, StringComparison.Ordinal)
            && string.Equals(identity.SceneName, observation.SceneName, StringComparison.Ordinal)
            && identity.Actors.Count == selected.Length
            && selected.All(id => identity.Actors.Count(a => a.ActorInstanceId == id) == 1)
            && identity.Actors.All(a => observation.Players.Any(p => p.ActorInstanceId == a.ActorInstanceId
                && p.TeamId == a.TeamId && p.Goalkeeper == a.Goalkeeper));
    }
    // Encoding precondition: nonnull identity/actor/renderer collections and unique actor IDs.
    // Poll calls only after complete evidence and exact selected-inventory binding.
    public static string CanonicalToken(FifaAutoCandidateIdentity identity)
    {
        using var bytes = new System.IO.MemoryStream();
        using var writer = new System.IO.BinaryWriter(bytes, System.Text.Encoding.UTF8, true);
        writer.Write(0x31494146); // FAI1, fixed little-endian Int32 format/version.
        WriteText(writer, 1, identity.Session); WriteText(writer, 2, identity.SceneName);
        WriteInt(writer, 3, identity.SceneHandle); WriteInt(writer, 4, identity.RootInstanceId);
        WriteInt(writer, 5, identity.ControllerInstanceId); WriteInt(writer, 6, identity.Actors.Count);
        foreach (var actor in identity.Actors.OrderBy(a => a.ActorInstanceId))
        {
            WriteInt(writer, 7, actor.ActorInstanceId); WriteInt(writer, 8, actor.PlayerId);
            WriteInt(writer, 9, actor.TeamId); writer.Write((byte)10); writer.Write(actor.Goalkeeper);
            WriteInt(writer, 11, actor.TransformInstanceId); WriteInt(writer, 12, actor.RendererInstanceIds.Count);
            foreach (int renderer in actor.RendererInstanceIds.OrderBy(id => id)) WriteInt(writer, 13, renderer);
        }
        writer.Flush();
        return Convert.ToBase64String(bytes.ToArray());
    }
    private static void WriteInt(System.IO.BinaryWriter writer, byte tag, int value)
    { writer.Write(tag); writer.Write(value); }
    private static void WriteText(System.IO.BinaryWriter writer, byte tag, string value)
    {
        writer.Write(tag);
        if (value == null) { writer.Write(-1); return; }
        byte[] utf8 = System.Text.Encoding.UTF8.GetBytes(value);
        writer.Write(utf8.Length); writer.Write(utf8);
    }
}
