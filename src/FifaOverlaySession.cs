using FM.Match;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace FM26.VisualDiagnostics;

// H206 (0.10.3): session identities and the session predicates of the context guard, in a partial compiled by production AND
// PolicyTests (synthetic CLR facade, tests/PolicyTests/H206SessionFacade.cs). SessionRefusal keeps the literal 0.10.2 order of
// the former single || chain of RequireSession: one `if` per operand, so the same short-circuit and the same getter calls in
// the same order (a getter exception propagates before any result exists).
internal sealed partial class FifaOverlay
{
    private GameObject _root;
    private MatchPlaybackController _controller;
    // H158: match clock = published MatchFrame.CurrentTime of the controller's Match (same consumer field read as SemanticEventObserver).
    private Match _publisher;
    private int _thread, _scene, _rootId, _controllerId;
    private string _sessionRefusal, _firstSessionRefusal; // this evaluation's refusing predicate / first one of the overlay

    // Refusing session predicate of the LAST evaluation (null = every predicate held).
    internal string FrameSessionPredicate => _sessionRefusal;
    // First session predicate that refused during this overlay (report + state; retained through Stop and cleanup).
    internal string SessionPredicate => _firstSessionRefusal;

    // H209 (0.10.4): first ACTOR-part context predicate that refused during this overlay (any actor) and its caller stage
    // ("start" | "tick" | "resume"); retained through Stop and cleanup.
    private string _firstActorPredicate, _firstActorPredicateStage;
    internal string ActorPredicate => _firstActorPredicate;
    internal string ActorPredicateStage => _firstActorPredicateStage;
    internal void LatchActorPredicate(string predicate, string where)
    {
        if (_firstActorPredicate != null) return; // first refusal only
        _firstActorPredicate = predicate; _firstActorPredicateStage = where;
    }

    internal string ObserveSession()
    {
        _sessionRefusal = null; // a getter exception below must not leave the previous frame's predicate
        _sessionRefusal = SessionRefusal();
        _firstSessionRefusal ??= _sessionRefusal; // latch only the first refusal (a getter exception never reaches here)
        return _sessionRefusal;
    }

    // H166: session part of the 0.5.1 RequireContext (root/scene/controller/publisher), evaluated once per frame.
    // H206: the predicates are evaluated by ObserveSession/SessionRefusal (same literal order). H209: moved here unchanged from
    // FifaOverlay.cs so PolicyTests execute it (the refusal must go through ObserveSession, which latches it).
    internal void RequireSession()
    {
        if (ObserveSession() != null)
            throw new FifaGuardException("FIFA overlay root/scene/actor context changed.");
    }

    private string SessionRefusal()
    {
        if (Environment.CurrentManagedThreadId != _thread) return "thread";
        if (_root == null) return "root-null";
        if (!_root.activeInHierarchy) return "root-inactive";
        if (_root.GetInstanceID() != _rootId) return "root-id";
        if (SceneManager.GetActiveScene().handle != _scene) return "scene-handle";
        if (SceneManager.GetActiveScene().name != "MatchPlayback") return "scene-name";
        if (_root.scene.handle != _scene) return "root-scene";
        if ((GameObject.Find("MatchPlaybackController/MatchComponents/Match3DBuilder") ?? GameObject.Find("Match3DBuilder")) != _root) return "builder-mismatch";
        if (_controller == null) return "controller-null";
        if (_controller.GetInstanceID() != _controllerId) return "controller-id";
        if (_root.GetComponentInParent<MatchPlaybackController>() != _controller) return "controller-parent";
        if (_publisher == null) return "publisher-null";
        if (_controller.m_match != _publisher) return "publisher-changed";
        return null;
    }
}
