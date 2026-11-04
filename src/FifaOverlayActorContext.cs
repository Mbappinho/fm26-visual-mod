using UnityEngine;

namespace FM26.VisualDiagnostics;

// H209 (0.10.4): actor part of the context guard (former body of FifaOverlayActor.RequireContext), in a shared source compiled
// by production AND PolicyTests (synthetic CLR facade, tests/PolicyTests/H209Facade.cs). The parameters keep the former field
// names so the predicate chain is the literal 0.10.3 text: same order, same short-circuit, same getter calls.
internal static class FifaActorContext
{
    // Refusing predicate of the actor guard, null = all hold. One `if` per operand of the former || chain, in its literal order.
    internal static string Refusal(FifaOverlay _s, PlayerBinding _actor, Transform _actorTransform, int _actorTransformId)
    {
        var p = _actor.Player;
        if (p == null) return "player-null";
        if (p.gameObject.GetInstanceID() != _actor.ActorInstanceId) return "instance-id";
        if (_actorTransform == null) return "transform-null";
        if (p.transform != _actorTransform) return "transform-changed";
        if (_actorTransform.GetInstanceID() != _actorTransformId) return "transform-id";
        if (!_actorTransform.IsChildOf(_s.RootTransform)) return "not-child-of-root";
        if (p.gameObject.scene.handle != _s.Scene) return "scene";
        if (!p.gameObject.activeInHierarchy) return "inactive";
        if (!p.IsActive) return "is-active";
        if (p.m_visuals3D == null) return "visuals-null";
        if (p.m_visuals3D.IsLoading) return "visuals-loading";
        if (p.m_visuals3D.IsSubstitute) return "substitute";
        if (p.m_visuals3D.IsMedic) return "medic";
        if (FifaActorSelection.RoleRefused(_actor.Goalkeeper, p.IsGoalkeeper)) return "role";
        if (p.ID != _actor.PlayerId) return "player-id";
        if (p.ShirtNumber != _actor.ShirtNumber) return "shirt";
        if (!FifaActorSelection.Admissible(_s.Request.Multi, _actor.Eligible, _actor.FifaEligible)) return "admissible";
        return null;
    }

    // Actor guard: where = caller stage ("start" | "tick" | "resume"); obs = the actor's frame observation (FailureDetail source).
    internal static void Require(FifaOverlay s, FifaFrameObservation obs, string where, PlayerBinding actor, Transform actorTransform, int actorTransformId)
    {
        var refusal = Refusal(s, actor, actorTransform, actorTransformId); // a getter exception propagates before any latch
        if (refusal != null)
        {
            s.LatchActorPredicate(refusal, where);
            obs.ActorPredicate = refusal; // this frame's refusal of this actor (FailureDetail of a stop on this frame)
            throw new FifaGuardException("FIFA overlay root/scene/actor context changed.");
        }
    }
}
