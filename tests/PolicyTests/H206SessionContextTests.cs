#nullable disable
using FM26.VisualDiagnostics;
using UnityEngine;
using UnityEngine.SceneManagement;

// H206 (0.10.3): half-time FM context change (live-013). Session predicate latch + fm-context-changed classification.
// Executes the REAL shared partial src/FifaOverlaySession.cs against the synthetic facade (H185 + H206SessionFacade.cs).
internal static class H206SessionContextTests
{
    private static readonly List<(string Name, Action Test)> Cases = new() {
        ("order-characterization", OrderCharacterization),
        ("predicate-names", PredicateNames),
        ("latch-first-refusal", LatchFirstRefusal),
        ("exception-clears-frame-predicate", ExceptionClearsFramePredicate),
        ("session-refusal-is-fm-transition", SessionRefusalIsFmTransition),
        ("no-session-refusal-unchanged", NoSessionRefusalUnchanged),
        ("thread-stays-failure", ThreadStaysFailure),
        ("view-change-suspension-unchanged", ViewChangeSuspensionUnchanged),
        ("version-0103", Version0103),
        // H206-CASES
    };
    private static void Check(bool okay, string label) { if (!okay) throw new Exception("FAIL: H206 " + label); }

    public static void Run(string name = null)
    {
        var scene = SceneManager.Current; var primary = GameObject.PrimaryRoot; var fallback = GameObject.FallbackRoot;
        try
        {
            foreach (var (caseName, test) in Cases)
            {
                if (name != null && name != caseName) continue;
                test();
                Console.WriteLine($"H206 PASS: {caseName}");
            }
            if (name != null && !Cases.Any(c => c.Name == name)) throw new Exception($"H206 unknown case {name}");
        }
        finally { SceneManager.Current = scene; GameObject.PrimaryRoot = primary; GameObject.FallbackRoot = fallback; }
    }

    // Session fixture where every predicate holds, except the ones listed in `broken` (index in the literal order).
    internal sealed class Fixture
    {
        public FifaOverlay Overlay = new(); public GameObject Root; public FM.Match.MatchPlaybackController Controller; public FM.Match.Match Publisher = new(); public int RootIdCalls;
        public Fixture(params int[] broken)
        {
            Controller = new FM.Match.MatchPlaybackController { Id = 9 }; Controller.m_match = Publisher;
            Root = new GameObject { Id = 7, scene = new Scene { handle = 3, name = "MatchPlayback" }, Controller = Controller };
            Root.OnGetInstanceID = () => RootIdCalls++;
            SceneManager.Current = new Scene { handle = 3, name = "MatchPlayback" };
            GameObject.PrimaryRoot = Root; GameObject.FallbackRoot = null;
            int thread = Environment.CurrentManagedThreadId, rootId = 7, controllerId = 9; var root = Root; var controller = Controller; var publisher = Publisher;
            foreach (var b in broken)
                switch (b)
                {
                    case 0: thread++; break;
                    case 1: root = null; break;
                    case 2: Root.activeInHierarchy = false; break;
                    case 3: rootId = 8; break;
                    case 4: SceneManager.Current = new Scene { handle = 4, name = "MatchPlayback" }; Root.scene = new Scene { handle = 4 }; break;
                    case 5: SceneManager.Current = new Scene { handle = SceneManager.Current.handle, name = "Other" }; break;
                    case 6: Root.scene = new Scene { handle = 5 }; break;
                    case 7: GameObject.PrimaryRoot = new GameObject { Id = 70 }; break;
                    case 8: controller = null; break;
                    case 9: controllerId = 10; break;
                    case 10: Root.Controller = new FM.Match.MatchPlaybackController { Id = 9, m_match = Publisher }; break;
                    case 11: publisher = null; break;
                    case 12: Controller.m_match = new FM.Match.Match(); break;
                }
            Overlay.BindSessionForTest(thread, root, rootId, 3, controller, controllerId, publisher);
        }
    }
    private static void OrderCharacterization()
    {
        // Characterization of the extracted predicate sequence (literal 0.10.2 order, short-circuit, getter multiplicity).
        var ok = new Fixture();
        Check(ok.Overlay.ObserveSession() == null && ok.Overlay.FrameSessionPredicate == null, "every predicate holds => no refusal");
        Check(ok.RootIdCalls == 1, "root GetInstanceID evaluated exactly once on a passing evaluation (" + ok.RootIdCalls + ")");
        for (int i = 0; i < 13; i++)
        {
            var f = new Fixture(i);
            Check(f.Overlay.ObserveSession() != null, "predicate " + i + " alone refuses");
            Check(f.RootIdCalls == (i <= 2 ? 0 : 1), "short-circuit: root GetInstanceID read only after the root null/active checks, once (" + i + ": " + f.RootIdCalls + ")");
        }
        var thrown = new Fixture(); thrown.Root.OnGetInstanceID = () => throw new InvalidOperationException("native getter failed");
        string error = null; try { thrown.Overlay.ObserveSession(); } catch (InvalidOperationException e) { error = e.Message; }
        Check(error == "native getter failed", "a getter exception propagates (it is not a refusal result)");
    }
    private static readonly string[] Names = { "thread", "root-null", "root-inactive", "root-id", "scene-handle", "scene-name", "root-scene", "builder-mismatch", "controller-null", "controller-id", "controller-parent", "publisher-null", "publisher-changed" };
    private static void PredicateNames()
    {
        var wrong = new List<string>();
        for (int i = 0; i < Names.Length; i++)
        {
            var got = new Fixture(i).Overlay.ObserveSession();
            if (got != Names[i]) wrong.Add(Names[i] + "<-" + got);
            for (int j = i + 1; j < Names.Length; j++)
            {
                if (i == 4 && j == 6) continue; // fixture 4 moves the root scene too (scene-handle stays first anyway)
                var both = new Fixture(i, j).Overlay.ObserveSession();
                if (both != Names[i]) wrong.Add(Names[i] + "+" + Names[j] + "<-" + both);
            }
        }
        Check(wrong.Count == 0, "each refusing session predicate is named, the FIRST in literal order wins: " + string.Join(", ", wrong.Take(20)));
    }
    private static void LatchFirstRefusal()
    {
        var f = new Fixture(12);
        Check(f.Overlay.ObserveSession() == "publisher-changed", "setup: publisher replaced (live-013 hypothesis)");
        Check(f.Overlay.SessionPredicate == "publisher-changed", "the first refusing session predicate is latched (" + f.Overlay.SessionPredicate + ")");
        f.Root.activeInHierarchy = false;
        Check(f.Overlay.ObserveSession() == "root-inactive" && f.Overlay.SessionPredicate == "publisher-changed", "a later, earlier-ordered refusal never replaces the latch");
        f.Root.activeInHierarchy = true; f.Controller.m_match = f.Publisher;
        Check(f.Overlay.ObserveSession() == null && f.Overlay.SessionPredicate == "publisher-changed", "the latch is retained after the predicates hold again");
        var t = new Fixture(); t.Root.OnGetInstanceID = () => throw new InvalidOperationException("native getter failed");
        try { t.Overlay.ObserveSession(); } catch (InvalidOperationException) { }
        Check(t.Overlay.SessionPredicate == null, "a getter exception before any result is not a refused predicate");
    }
    private static void ExceptionClearsFramePredicate()
    {
        var f = new Fixture(5);
        Check(f.Overlay.ObserveSession() == "scene-name", "setup: a refusal on an earlier frame");
        SceneManager.Current = new Scene { handle = 3, name = "MatchPlayback" };
        f.Root.OnGetInstanceID = () => throw new InvalidOperationException("native getter failed");
        try { f.Overlay.ObserveSession(); } catch (InvalidOperationException) { }
        Check(f.Overlay.FrameSessionPredicate == null, "a getter exception leaves NO refusing predicate for this frame (never a stale one: " + f.Overlay.FrameSessionPredicate + ")");
    }
    private static void SessionRefusalIsFmTransition()
    {
        foreach (var p in new[] { "builder-mismatch", "publisher-changed" })
        {
            var s = FifaSessionTransition.Apply((FifaScopeKind.StopSession, "owned-update-failure", "FIFA overlay root/scene/actor context changed."), p);
            Check(s.Kind == FifaScopeKind.StopSession && s.Reason == "fm-context-changed" && s.Error == null, "a session refusal (" + p + ") of a running overlay is a NORMAL end fm-context-changed with Error null (" + s.Reason + ", " + s.Error + ")");
            Check(FifaOverlaySegments.CanStart(true, true, true, true, s.Error, 1), "so a verified, published predecessor lets the next segment start");
        }
    }
    private static void NoSessionRefusalUnchanged()
    {
        var actor = (FifaScopeKind.StopSession, "owned-update-failure", "Root teleport");
        Check(FifaSessionTransition.Apply(actor, null) == actor, "without a session refusal (actor-part refusal, motion failure) the decision is unchanged");
        var left = (FifaScopeKind.EndActor, FifaActorScope.ActorLeftReason, (string)null);
        Check(FifaSessionTransition.Apply(left, null) == left, "actor-left unchanged");
    }
    private static void ThreadStaysFailure()
    {
        var failure = (FifaScopeKind.StopSession, "owned-update-failure", "FIFA overlay root/scene/actor context changed.");
        Check(FifaSessionTransition.Apply(failure, "thread") == failure, "a thread mismatch is our own fault, never an FM transition: stays owned-update-failure with its Error");
    }
    private static void ViewChangeSuspensionUnchanged()
    {
        var renderers = new List<(bool, bool, bool?, bool?)> { (true, true, true, false), (true, true, true, false) };
        var cut = FifaActorScope.OnActiveFailure(true, "FIFA overlay root/scene/actor context changed.", false, true, true, renderers, false, "FIFA overlay root/scene/actor context changed.");
        Check(cut.Kind == FifaScopeKind.SuspendActor, "setup: session refusal + intact structure + all renderers hidden = H162 view-change suspension");
        Check(FifaSessionTransition.Apply(cut, "root-inactive") == cut, "the view-change suspension keeps precedence over fm-context-changed");
    }
    private static void Version0103()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !File.Exists(Path.Combine(dir.FullName, "FM26.VisualDiagnostics.csproj"))) dir = dir.Parent;
        Check(dir != null && File.ReadAllText(Path.Combine(dir.FullName, "FM26.VisualDiagnostics.csproj")).Contains("<Version>0.10.7</Version>", StringComparison.Ordinal), "assembly version must be 0.10.7 (H238 supersedes; H234 supersedes; H211 supersedes; H209 supersedes; half-time fm-context-changed candidate)");
    }
    // H206-METHODS
}
