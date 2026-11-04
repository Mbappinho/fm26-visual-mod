#nullable disable
namespace FM26.VisualDiagnostics;

// H206 (0.10.3): at half time FM replaces the match presentation context (root / 3D builder / controller / publisher) while the
// overlay runs (live-013, segment auto-...-2: owned-update-failure, Stage guards). A SESSION predicate refusal of a running
// overlay is an FM presentation transition: a normal end that lets the next AUTO segment bind the new context at Start.
// Pure, PolicyTests-compiled.
public static class FifaSessionTransition
{
    public const string Reason = "fm-context-changed";

    // Active-path scope decision under this frame's refusing session predicate (null = no session refusal on this frame).
    public static (FifaScopeKind Kind, string Reason, string Error) Apply((FifaScopeKind Kind, string Reason, string Error) scope, string sessionPredicate)
        => sessionPredicate == null || sessionPredicate == "thread" || scope.Kind != FifaScopeKind.StopSession ? scope : (FifaScopeKind.StopSession, Reason, null);

    // H238 (0.10.7): suspended-path scope decision (FifaActorScope.OnSuspended result) under this frame's refusing session
    // predicate: a resume decision (players visible again) under a session refusal ends normally (the frame's Resume would
    // otherwise throw that refusal and stop as owned-update-failure).
    public static (FifaScopeKind Kind, string Reason, string Error) ApplySuspended((FifaScopeKind Kind, string Reason, string Error) scope, string sessionPredicate)
        => sessionPredicate != null && sessionPredicate != "thread" && scope.Kind == FifaScopeKind.Resume ? (FifaScopeKind.StopSession, Reason, null) : scope;
}

// H240 (0.10.7): live-017 segment 5 stopped owned-update-failure on the RESUME frame of an fm-view-changed suspension because
// the actor guard refused 'is-active' for one actor (Resume -> RequireContext("resume") -> outer catch). Resume-frame scope
// decision from that actor's guard predicate (null = all hold). Pure, PolicyTests-compiled.
public static class FifaResumeRefusal
{
    public static (FifaScopeKind Kind, string Reason, string Error) Apply((FifaScopeKind Kind, string Reason, string Error) scope, bool multi, string sessionPredicate, string actorPredicate)
        => multi && sessionPredicate == null && actorPredicate == "is-active" && scope.Kind == FifaScopeKind.Resume ? (FifaScopeKind.EndActor, FifaActorScope.ActorLeftReason, null) : scope;
}
