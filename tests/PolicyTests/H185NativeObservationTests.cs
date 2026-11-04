#nullable disable
using FM26.VisualDiagnostics;
using UnityEngine;
using UnityEngine.SceneManagement;
using FM.Match;

internal static class H185NativeObservationTests
{
    private static readonly List<(string Name, Action Test)> Cases = new() { ("players-truncated", PlayersTruncated), ("identity-current", IdentityCurrentPositive), ("renderers-truncated", RenderersTruncated), ("renderers-ready", RenderersReadyPositive), ("renderer-scene", RendererScene), ("controller-scene", ControllerScene), ("player-scene", PlayerScene), ("projection", Projection), ("unavailable", Unavailable), ("root-scene", RootScene), ("player-id", PlayerId), ("player-root", PlayerRoot), ("renderer-owner", RendererOwner) };
    private static void RenderersReadyPositive()
    {
        var f = new Fixture();
        Check(f.Capture().RenderersReady, "complete untruncated renderer sets must map to RenderersReady");
    }
    private static void RenderersTruncated()
    {
        var f = new Fixture();
        var b = f.Players[3];
        for (int i = 0; i < 128; i++)
        {
            var extra = Object(20000 + i, b.Player.transform);
            b.Renderers.Add(new Renderer { Id = 30000 + i, gameObject = extra, transform = extra.transform });
        }
        Check(!f.Capture().RenderersReady, "selected player with more than 128 renderers must not be RenderersReady (copied prefix truncated)");
    }
    private static void IdentityCurrentPositive()
    {
        var f = new Fixture();
        Check(f.Capture().IdentitySnapshotCurrent, "complete untruncated copied identity must map to IdentitySnapshotCurrent");
    }
    private static void PlayersTruncated()
    {
        var f = new Fixture();
        for (int i = 0; i < 43; i++)
        {
            var go = Object(40000 + i, f.Root.transform);
            var p = new Player { ID = 50000 + i, TeamID = 0, ShirtNumber = 0, IsActive = false, gameObject = go, transform = go.transform };
            f.Players.Add(new PlayerBinding { Player = p, ActorInstanceId = go.Id, PlayerId = p.ID, TeamId = 0 });
        }
        var o = f.Capture();
        Check(o.Identity != null, "players-truncated precondition: first 64 bindings still yield an identity");
        Check(!o.IdentitySnapshotCurrent, "more than 64 bindings must not be IdentitySnapshotCurrent (copied prefix truncated)");
    }
    private static void RendererScene()
    {
        var f = new Fixture();
        foreach (var b in f.Players)
        {
            var extra = Object(9000 + b.ActorInstanceId, b.Player.transform);
            b.Renderers.Add(new Renderer { Id = 9500 + b.ActorInstanceId, gameObject = extra, transform = extra.transform });
            foreach (var renderer in b.Renderers)
            {
                Check(f.Capture().Identity != null, "renderer scene positive control");
                renderer.gameObject.scene = new Scene { handle = 88, name = "MatchPlayback" };
                Check(f.Capture().Identity == null, "foreign renderer scene must discard identity");
                renderer.gameObject.scene = SceneManager.Current;
                Check(f.Capture().Identity != null, "renderer scene fresh recovery");
            }
        }
    }
    private static void ControllerScene()
    {
        var f = new Fixture(); Check(f.Capture().Identity != null, "controller scene positive control");
        f.Root.Controller.gameObject.scene = new Scene { handle = 88, name = "MatchPlayback" };
        Check(f.Capture().Identity == null, "foreign controller scene must discard identity");
        f.Root.Controller.gameObject.scene = SceneManager.Current;
        Check(f.Capture().Identity != null, "controller scene fresh recovery");
    }
    private static void PlayerScene()
    {
        var f = new Fixture();
        foreach (var b in f.Players)
        {
            Check(f.Capture().Identity != null, "player scene positive control");
            b.Player.gameObject.scene = new Scene { handle = 88, name = "MatchPlayback" };
            Check(f.Capture().Identity == null, "foreign player scene must discard identity");
            b.Player.gameObject.scene = SceneManager.Current;
            Check(f.Capture().Identity != null, "player scene fresh recovery");
        }
    }
    private static void RendererOwner()
    {
        var f = new Fixture();
        foreach (var b in f.Players)
        {
            var extra = Object(9000 + b.ActorInstanceId, b.Player.transform);
            b.Renderers.Add(new Renderer { Id = 9500 + b.ActorInstanceId, gameObject = extra, transform = extra.transform });
            foreach (var renderer in b.Renderers)
            {
                Check(f.Capture().Identity != null, "renderer ownership positive control");
                renderer.transform.Parent = f.Players.First(other => other != b).Player.transform;
                Check(f.Capture().Identity == null, "renderer owned by another player must discard identity");
                renderer.transform.Parent = b.Player.transform;
                Check(f.Capture().Identity != null, "renderer ownership checked fresh on recovery");
            }
        }
    }
    private static void PlayerRoot()
    {
        var f = new Fixture(); var foreign = Object(999, null);
        foreach (var b in f.Players)
        {
            Check(f.Capture().Identity != null, "player root positive control");
            b.Player.transform.Parent = foreign.transform;
            Check(f.Capture().Identity == null, "player outside actual input root must discard identity");
            b.Player.transform.Parent = f.Root.transform;
            Check(f.Capture().Identity != null, "player ownership checked fresh on recovery");
        }
    }
    private static void PlayerId()
    {
        var f = new Fixture(); Check(f.Capture().Identity != null, "player ID positive control");
        f.Players[4].Player.ID += 5000;
        Check(f.Capture().Identity == null, "changed native PlayerId must refuse stale same binding");
        f.Players[4].PlayerId = f.Players[4].Player.ID;
        Check(f.Capture().Identity.Actors.Single(a => a.ActorInstanceId == 204).PlayerId == 6004, "fresh binding recovers changed PlayerId");
    }
    private static void RootScene()
    {
        var f = new Fixture(); Check(f.Capture().Identity != null, "root scene positive control");
        f.Root.scene = new Scene { handle = 88, name = "MatchPlayback" };
        Check(f.Capture().Identity == null, "foreign-scene root must discard identity");
        f.Root.scene = SceneManager.Current;
        Check(f.Capture().Identity != null, "root scene fresh recovery");
    }
    private static void Unavailable()
    {
        foreach (string missing in new[] { "root", "players", "controller", "player", "visuals", "transform", "renderer" })
        {
            var f = new Fixture();
            switch (missing)
            {
                case "root": f.Root = null; break;
                case "players": f.Players = null; break;
                case "controller": f.Root.Controller = null; break;
                case "player": f.Players[0].Player = null; break;
                case "visuals": f.Players[0].Player.m_visuals3D = null; break;
                case "transform": f.Players[0].Player.transform = null; break;
                case "renderer": f.Players[0].Renderers[0] = null; break;
            }
            FifaAutoPollObservation result = null; Exception error = null;
            try { result = f.Capture(); } catch (Exception ex) { error = ex; }
            Check(error == null && result != null && result.Identity == null && !result.IdentitySnapshotCurrent, "unavailable native consumer must return closed observation: " + missing);
            var c = new FifaAutoPollCoordinator("synthetic-session");
            Check(c.Poll(result) == FifaAutoPollDecision.NotReady, "unavailable cannot propose");
        }
    }
    private static void Projection()
    {
        var f = new Fixture();
        var o = f.Capture();
        Check(o.Identity != null, "projection must copy actual consumer identity");
        Check(o.PluginSession == "synthetic-session" && o.SceneName == "MatchPlayback" && o.Enabled && o.RootPresent && o.RootActive, "host/session/root projection");
        Check(o.Identity.SceneHandle == 17 && o.Identity.RootInstanceId == 100 && o.Identity.ControllerInstanceId == 101, "root/controller identity projection");
        Check(FifaAutoCandidate.IsValid(o.Players) && FifaAutoIdentity.IsComplete(o.Identity) && FifaAutoIdentity.Matches(o.Identity, o), "actual resolver and eligibility binding");
        var ids = FifaActorSelection.ResolveFifa(new FifaOverlayRequest { AllEligible = true }, o.Players);
        Check(ids.SequenceEqual(o.Identity.Actors.Select(a => a.ActorInstanceId)), "identity uses exact selected resolver IDs");
        foreach (var b in f.Players)
        {
            var a = o.Identity.Actors.Single(a => a.ActorInstanceId == b.Player.gameObject.Id);
            Check(a.PlayerId == b.Player.ID && a.TeamId == b.Player.TeamID && a.Goalkeeper == b.Player.IsGoalkeeper && a.TransformInstanceId == b.Player.transform.Id && a.RendererInstanceIds.SequenceEqual(b.Renderers.Select(r => r.Id)), "same-binding native projection");
        }
        Check(!o.Players[0].Eligible && o.Players[0].FifaEligible && o.Players[1].Eligible, "fresh native role includes keepers despite stale binding scalars");
        f.Players[1].Player.TeamID = 0; f.Players[1].Player.ID += 2000; f.Players[1].PlayerId = f.Players[1].Player.ID;
        f.Players[1].Renderers[0].Id += 3000;
        var changed = f.Capture();
        Check(changed.Identity.Actors[1].PlayerId == 3001 && changed.Identity.Actors[1].RendererInstanceIds[0] == 3701 && o.Identity.Actors[1].PlayerId == 1001, "fresh capture and immutable earlier identity");
        Check(!o.PackVerified && o.RenderersReady && o.IdentitySnapshotCurrent && o.ManualCommandPending && o.RequestReportExists, "unresolved file evidence stays closed; complete copied identity is current");
        var c = new FifaAutoPollCoordinator("synthetic-session");
        Check(c.Poll(o) == FifaAutoPollDecision.NotReady && c.Poll(o) == FifaAutoPollDecision.NotReady, "adapter cannot authorize proposal");
        Check(FifaAutoNativeObservation.Capture(f.Root, f.Players, "synthetic-session", false, true, false).OtherModesBlocked && FifaAutoNativeObservation.Capture(f.Root, f.Players, "synthetic-session", false, false, true).OtherModesBlocked, "both host exclusions copied");
    }
    public static void Run(string name = null)
    {
        int ran = 0;
        foreach (var c in Cases) { if (name != null && name != c.Name) continue; c.Test(); ran++; Console.WriteLine("H185 PASS: " + c.Name); }
        if (name != null && ran != 1) throw new Exception("Unknown H185 case " + name);
    }
    private static void Check(bool value, string message) { if (!value) throw new Exception("FAIL: H185 " + message); }
    // Deliberately synthetic consumers with stale eligibility scalars in the original binding.
    private sealed class Fixture
    {
        public GameObject Root;
        public List<PlayerBinding> Players = new();
        public Fixture()
        {
            SceneManager.Current = new Scene { handle = 17, name = "MatchPlayback" };
            Root = Object(100, null);
            GameObject.PrimaryRoot = Root; GameObject.FallbackRoot = null;
            Root.Controller = new MatchPlaybackController { Id = 101, gameObject = Object(102, null) };
            for (int i = 0; i < 22; i++)
            {
                var go = Object(200 + i, Root.transform);
                var p = new Player { ID = 1000 + i, TeamID = i / 11, ShirtNumber = i % 11 + 1,
                    IsGoalkeeper = i % 11 == 0, gameObject = go, transform = go.transform };
                var b = new PlayerBinding { Player = p, ActorInstanceId = go.Id, PlayerId = p.ID,
                    TeamId = 9, ShirtNumber = 0, Active = false };
                foreach (string key in PlayerBinding.RequiredAnchors) b.Anchors.Add(key, new object());
                var rendererGo = Object(500 + i, go.transform);
                b.Renderers.Add(new Renderer { Id = 700 + i, gameObject = rendererGo, transform = rendererGo.transform });
                Players.Add(b);
            }
        }
        public FifaAutoPollObservation Capture() => FifaAutoNativeObservation.Capture(Root, Players, "synthetic-session", true, false, false);
    }
    private static GameObject Object(int id, Transform parent)
    {
        var go = new GameObject { Id = id, scene = SceneManager.Current };
        go.transform = new Transform { Id = id + 10000, Parent = parent, gameObject = go }; return go;
    }
}
