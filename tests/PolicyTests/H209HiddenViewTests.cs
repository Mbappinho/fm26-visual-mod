#nullable disable
using FM26.VisualDiagnostics;
using UnityEngine;
using UnityEngine.SceneManagement;
using FM.Match;

// H209 (0.10.4), live-014: the AUTO host submitted segment 2 while FM's key-moments view hid every player renderer (a
// substitution during a highlight break); it failed on its first frame. AUTO start gate on visible players + actor-part
// context predicate latch. Executes the REAL shared sources against the synthetic H185/H206/H209 facades.
internal static class H209HiddenViewTests
{
    private static readonly List<(string Name, Action Test)> Cases = new() {
        ("copy-visible-positive", CopyVisiblePositive),
        ("copy-partial-inactive-visible", CopyPartialInactiveVisible),
        ("copy-hidden-actor-not-visible", CopyHiddenActorNotVisible),
        ("coordinator-requires-visible", CoordinatorRequiresVisible),
        ("actor-predicates-refuse", ActorPredicatesRefuse),
        ("actor-predicate-names", ActorPredicateNamesCase),
        ("actor-latch", ActorLatch),
        ("actor-latch-first-only", ActorLatchFirstOnly),
        ("failure-detail-predicate", FailureDetailPredicate),
        ("begin-clears-predicate", BeginClearsPredicate),
        ("require-session-latches", RequireSessionLatches),
        ("version-0104", Version0104),
        // H209-CASES
    };
    private static void Check(bool okay, string label) { if (!okay) throw new Exception("FAIL: H209 " + label); }

    public static void Run(string name = null)
    {
        var scene = SceneManager.Current; var primary = GameObject.PrimaryRoot; var fallback = GameObject.FallbackRoot;
        try
        {
            int ran = 0;
            foreach (var (caseName, test) in Cases)
            {
                if (name != null && name != caseName) continue;
                test(); ran++;
                Console.WriteLine($"H209 PASS: {caseName}");
            }
            if (name != null && ran != 1) throw new Exception($"H209 unknown case {name}");
        }
        finally { SceneManager.Current = scene; GameObject.PrimaryRoot = primary; GameObject.FallbackRoot = fallback; }
    }

    // H185-shaped producer fixture: 22 eligible players, `renderers` renderer objects each under the player transform.
    internal sealed class CaptureFixture
    {
        public GameObject Root; public List<PlayerBinding> Players = new();
        public CaptureFixture(int renderers)
        {
            SceneManager.Current = new Scene { handle = 17, name = "MatchPlayback" };
            Root = Obj(100, null); GameObject.PrimaryRoot = Root; GameObject.FallbackRoot = null;
            Root.Controller = new MatchPlaybackController { Id = 101, gameObject = Obj(102, null) };
            for (int i = 0; i < 22; i++)
            {
                var go = Obj(200 + i, Root.transform);
                var p = new Player { ID = 1000 + i, TeamID = i / 11, ShirtNumber = i % 11 + 1, IsGoalkeeper = i % 11 == 0, gameObject = go, transform = go.transform };
                var b = new PlayerBinding { Player = p, ActorInstanceId = go.Id, PlayerId = p.ID, TeamId = 9, ShirtNumber = 0, Active = false };
                foreach (string key in PlayerBinding.RequiredAnchors) b.Anchors.Add(key, new object());
                for (int k = 0; k < renderers; k++)
                {
                    var rendererGo = Obj(500 + 100 * i + k, go.transform);
                    b.Renderers.Add(new Renderer { Id = 30000 + 100 * i + k, gameObject = rendererGo, transform = rendererGo.transform });
                }
                Players.Add(b);
            }
        }
        public FifaAutoPollObservation Capture() => FifaAutoNativeObservation.Capture(Root, Players, "synthetic-session", true, false, false);
    }
    internal static GameObject Obj(int id, Transform parent)
    {
        var go = new GameObject { Id = id, scene = SceneManager.Current };
        go.transform = new Transform { Id = id + 100000, Parent = parent, gameObject = go }; return go;
    }
    // live-014 segment 1: players visible on the pitch (37 of 39 renderers active in hierarchy) => visible.
    private static void CopyVisiblePositive()
    {
        var f = new CaptureFixture(3);
        var o = f.Capture();
        Check(o.Identity != null && o.RenderersReady, "positive control: complete copied identity");
        Check(o.RenderersVisible, "selected players with renderers active in hierarchy must map to RenderersVisible");
    }
    // live-014 evidence: on a visible pitch FM keeps 2 of each player's 39 renderers inactive in hierarchy (37 active);
    // such a player is visible (only ALL renderers inactive = FM view hiding him, the fm-view-changed definition).
    private static void CopyPartialInactiveVisible()
    {
        var f = new CaptureFixture(39);
        foreach (var b in f.Players) { b.Renderers[0].gameObject.activeInHierarchy = false; b.Renderers[38].gameObject.activeInHierarchy = false; }
        var o = f.Capture();
        Check(o.Identity != null && o.RenderersReady, "positive control: complete copied identity");
        Check(o.RenderersVisible, "players with 2 of 39 renderers inactive (FM's normal visible state) must stay RenderersVisible");
    }
    // live-014 segment 2: FM's key-moments view hid EVERY renderer of every player (ActiveInHierarchy false, enabled true).
    // One selected actor whose renderers are all inactive in hierarchy (enabled unchanged) => not visible.
    private static void CopyHiddenActorNotVisible()
    {
        var f = new CaptureFixture(39);
        foreach (var b in f.Players) { b.Renderers[0].gameObject.activeInHierarchy = false; b.Renderers[38].gameObject.activeInHierarchy = false; }
        foreach (var b in f.Players)
        {
            Check(f.Capture().RenderersVisible, "positive control before hiding one actor");
            foreach (var r in b.Renderers) r.gameObject.activeInHierarchy = false; // enabled stays true (fm-view-changed shape)
            var o = f.Capture();
            Check(o.Identity != null && o.RenderersReady, "hidden view keeps a complete copied identity (precondition)");
            Check(!o.RenderersVisible, "one selected actor with all renderers inactive in hierarchy must clear RenderersVisible (actor " + b.ActorInstanceId + ")");
            foreach (var r in b.Renderers) r.gameObject.activeInHierarchy = true;
            b.Renderers[0].gameObject.activeInHierarchy = false; b.Renderers[38].gameObject.activeInHierarchy = false;
        }
    }
    // Otherwise ready copied observation (H186 Ready shape) with the H209 visibility input.
    private static FifaAutoPollObservation Ready(bool visible)
    {
        var players = new List<(int, int, bool, bool, bool)>(); var actors = new List<FifaAutoActorIdentity>();
        for (int i = 0; i < 22; i++)
        {
            bool keeper = i % 11 == 0;
            players.Add((200 + i, i / 11, !keeper, true, keeper));
            actors.Add(new FifaAutoActorIdentity(200 + i, 1000 + i, i / 11, keeper, 10200 + i, new[] { 700 + i }));
        }
        var o = new FifaAutoPollObservation
        {
            Enabled = true, PluginSession = "synthetic-session", SceneName = "MatchPlayback", RootPresent = true, RootActive = true,
            RenderersReady = true, IdentitySnapshotCurrent = true, RenderersVisible = visible, Players = players,
            Identity = new FifaAutoCandidateIdentity("synthetic-session", "MatchPlayback", 17, 100, 101, actors.ToArray()),
        };
        return FifaAutoHost.Prepare(o, true, false, false);
    }
    private static void CoordinatorRequiresVisible()
    {
        var c = new FifaAutoPollCoordinator("synthetic-session");
        Check(c.Poll(Ready(false)) == FifaAutoPollDecision.NotReady && c.Poll(Ready(false)) == FifaAutoPollDecision.NotReady,
            "two otherwise ready polls while FM's view hides the players must never propose");
        Check(c.Poll(Ready(true)) == FifaAutoPollDecision.NotReady, "first visible poll after a hidden view only starts the stable streak");
        Check(c.Poll(Ready(true)) == FifaAutoPollDecision.PartialProposal, "two stable polls with visible players propose");
        var d = new FifaAutoPollCoordinator("synthetic-session");
        Check(d.Poll(Ready(true)) == FifaAutoPollDecision.NotReady && d.Poll(Ready(false)) == FifaAutoPollDecision.NotReady
            && d.Poll(Ready(true)) == FifaAutoPollDecision.NotReady, "a hidden poll between visible polls resets the stable streak");
    }
    internal static readonly string[] ActorPredicateNames = { "player-null", "instance-id", "transform-null", "transform-changed", "transform-id",
        "not-child-of-root", "scene", "inactive", "is-active", "visuals-null", "visuals-loading", "substitute", "medic", "role", "player-id", "shirt", "admissible" };
    // One selected multi-mode goalkeeper (live-014 failing actor shape) where every actor predicate holds, except `Break(k)`
    // (k = index in the literal 0.10.3 order of the former || chain).
    internal sealed class ActorFixture
    {
        public FifaOverlay Overlay = new(); public GameObject Root; public PlayerBinding Actor; public Player P; public Transform T; public int Tid; public FifaFrameObservation Obs = new();
        public ActorFixture()
        {
            SceneManager.Current = new Scene { handle = 3, name = "MatchPlayback" };
            Root = Obj(7, null);
            Overlay.BindSessionForTest(Environment.CurrentManagedThreadId, Root, 7, 3, new MatchPlaybackController { Id = 9 }, 9, new FM.Match.Match());
            Overlay.Request = new FifaOverlayRequest { AllEligible = true };
            var go = Obj(200, Root.transform);
            P = new Player { ID = 1000, TeamID = 0, ShirtNumber = 1, IsGoalkeeper = true, gameObject = go, transform = go.transform };
            Actor = new PlayerBinding { Player = P, ActorInstanceId = 200, PlayerId = 1000, ShirtNumber = 1, TeamId = 0, Active = true, Goalkeeper = true, Eligible = false };
            foreach (string key in PlayerBinding.RequiredAnchors) Actor.Anchors.Add(key, new object());
            T = P.transform; Tid = T.Id;
        }
        public void Break(int k)
        {
            switch (k)
            {
                case 0: Actor.Player = null; break;
                case 1: Actor.ActorInstanceId = 201; break;
                case 2: T = null; break;
                case 3: P.transform = Obj(300, Root.transform).transform; break;
                case 4: Tid = T.Id + 1; break;
                case 5: T.Parent = null; break;
                case 6: P.gameObject.scene = new Scene { handle = 4, name = "MatchPlayback" }; break;
                case 7: P.gameObject.activeInHierarchy = false; break;
                case 8: P.IsActive = false; break;
                case 9: P.m_visuals3D = null; break;
                case 10: P.m_visuals3D.IsLoading = true; break;
                case 11: P.m_visuals3D.IsSubstitute = true; break;
                case 12: P.m_visuals3D.IsMedic = true; break;
                case 13: P.IsGoalkeeper = false; break;
                case 14: P.ID = 1001; break;
                case 15: P.ShirtNumber = 2; break;
                case 16: Actor.Medic = true; break; // binding scalar => not FIFA-eligible => not admissible (multi)
            }
        }
        public string Refusal() => FifaActorContext.Refusal(Overlay, Actor, T, Tid);
        public Exception Require(string where = "tick")
        {
            try { FifaActorContext.Require(Overlay, Obs, where, Actor, T, Tid); return null; } catch (Exception e) { return e; }
        }
    }
    // Characterization of the extracted literal chain: each operand alone refuses with the 0.10.3 guard exception.
    private static void ActorPredicatesRefuse()
    {
        Check(new ActorFixture().Require() == null, "positive control: every actor predicate holds");
        for (int k = 0; k < ActorPredicateNames.Length; k++)
        {
            var f = new ActorFixture(); f.Break(k);
            var e = f.Require();
            Check(e is FifaGuardException && e.Message == "FIFA overlay root/scene/actor context changed.", "actor predicate " + ActorPredicateNames[k] + " alone must refuse with the 0.10.3 guard exception");
        }
    }
    // Each operand of the former || chain has its own stable name; the FIRST refusing operand in literal order is reported.
    private static void ActorPredicateNamesCase()
    {
        Check(new ActorFixture().Refusal() == null, "positive control: no refusing actor predicate");
        for (int k = 0; k < ActorPredicateNames.Length; k++)
        {
            var f = new ActorFixture(); f.Break(k);
            var name = f.Refusal();
            Check(name == ActorPredicateNames[k], "actor predicate " + ActorPredicateNames[k] + " must be named, got " + (name ?? "null"));
        }
        for (int k = 0; k < ActorPredicateNames.Length; k++)
            for (int j = k + 1; j < ActorPredicateNames.Length; j++)
            {
                var f = new ActorFixture();
                try { f.Break(j); f.Break(k); } catch (NullReferenceException) { continue; } // fixture cannot express this pair
                Check(f.Refusal() == ActorPredicateNames[k], "literal order: " + ActorPredicateNames[k] + " before " + ActorPredicateNames[j]);
            }
    }
    // The refusing actor predicate and its caller stage are latched on the overlay (report + state), the guard still throws.
    private static void ActorLatch()
    {
        var f = new ActorFixture(); f.Break(8);
        var e = f.Require("tick");
        Check(e is FifaGuardException, "precondition: is-active refusal throws the guard exception");
        Check(f.Overlay.ActorPredicate == "is-active" && f.Overlay.ActorPredicateStage == "tick", "refusing actor predicate and caller stage must be latched, got " + (f.Overlay.ActorPredicate ?? "null") + "/" + (f.Overlay.ActorPredicateStage ?? "null"));
        var g = new ActorFixture(); g.Break(10);
        Check(g.Require("start") is FifaGuardException && g.Overlay.ActorPredicate == "visuals-loading" && g.Overlay.ActorPredicateStage == "start", "start-time refusal latched with stage start");
        var h = new ActorFixture();
        Check(h.Require("resume") == null && h.Overlay.ActorPredicate == null && h.Overlay.ActorPredicateStage == null, "no refusal => nothing latched");
    }
    // Only the FIRST actor refusal of the overlay is latched (a later refusal, other predicate/stage, never overwrites it).
    private static void ActorLatchFirstOnly()
    {
        var f = new ActorFixture(); f.Break(8);
        Check(f.Require("tick") is FifaGuardException && f.Overlay.ActorPredicate == "is-active", "precondition: first refusal latched");
        f.P.IsActive = true; f.Break(10);
        Check(f.Require("resume") is FifaGuardException, "precondition: second refusal throws");
        Check(f.Overlay.ActorPredicate == "is-active" && f.Overlay.ActorPredicateStage == "tick", "a later refusal must not overwrite the first latched actor predicate/stage, got " + f.Overlay.ActorPredicate + "/" + f.Overlay.ActorPredicateStage);
    }
    // live-014 frame 25402 shape: the failing actor's refusing predicate of the stop frame lands in FailureDetail.
    private static void FailureDetailPredicate()
    {
        var f = new ActorFixture(); f.Obs.Begin(25402, 1, 3816, null); f.Obs.Stage = "guards"; f.Break(8);
        Check(f.Require("tick") is FifaGuardException, "precondition: refusal throws");
        var d = f.Obs.Snapshot();
        Check(d != null && d.Frame == 25402 && d.Stage == "guards", "precondition: stop-frame snapshot");
        Check(d.ActorPredicate == "is-active", "FailureDetail must carry the failing actor's refusing predicate of the stop frame, got " + (d.ActorPredicate ?? "null"));
    }
    // A refusal of an earlier frame never leaks into a later frame's FailureDetail (frame-scoped like Stage).
    private static void BeginClearsPredicate()
    {
        var f = new ActorFixture(); f.Obs.Begin(100, 1, 3816, null); f.Break(8);
        Check(f.Require("tick") is FifaGuardException && f.Obs.Snapshot().ActorPredicate == "is-active", "precondition: frame 100 refusal recorded");
        f.Obs.Begin(101, 2, 3816, null);
        Check(f.Obs.Snapshot().ActorPredicate == null, "a new frame must clear the previous frame's refusing actor predicate");
    }
    // H207 survivor bypass-observe: RequireSession must refuse THROUGH ObserveSession, so the refusing session predicate is
    // latched (frame + first). Characterization of the moved production method (RED evidence = the mutant kill).
    private static void RequireSessionLatches()
    {
        var ok = new H206SessionContextTests.Fixture();
        ok.Overlay.RequireSession();
        Check(ok.Overlay.SessionPredicate == null, "positive control: every session predicate holds");
        var f = new H206SessionContextTests.Fixture(12);
        Exception e = null; try { f.Overlay.RequireSession(); } catch (Exception x) { e = x; }
        Check(e is FifaGuardException && e.Message == "FIFA overlay root/scene/actor context changed.", "session refusal throws the 0.10.3 guard exception");
        Check(f.Overlay.FrameSessionPredicate == "publisher-changed" && f.Overlay.SessionPredicate == "publisher-changed", "RequireSession refusal must be observed and latched (publisher-changed)");
    }
    private static void Version0104()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !File.Exists(Path.Combine(dir.FullName, "FM26.VisualDiagnostics.csproj"))) dir = dir.Parent;
        Check(dir != null && File.ReadAllText(Path.Combine(dir.FullName, "FM26.VisualDiagnostics.csproj")).Contains("<Version>0.10.7</Version>", StringComparison.Ordinal), "assembly version must be 0.10.7 (H238 supersedes; H234 supersedes; H211 supersedes; hidden-view AUTO gate + actor predicate latch)");
    }
    // H209-METHODS
}
