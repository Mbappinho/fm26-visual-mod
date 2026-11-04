#nullable disable
using FM26.VisualDiagnostics;

internal static partial class H184aAutoCoordinatorTests
{
    private static void IdentityStability()
    {
        // Feasible changed inventories: retain complete 22, 11/11 and one goalkeeper per team.
        var changes = IdentityChanges().Where(i => i.Session == "session-A" && i.SceneName == "MatchPlayback"
            && i.Actors.Count(a => a.TeamId == 0) == 11 && i.Actors.Count(a => a.TeamId == 1) == 11
            && i.Actors.Count(a => a.TeamId == 0 && a.Goalkeeper) == 1
            && i.Actors.Count(a => a.TeamId == 1 && a.Goalkeeper) == 1).ToList();
        var actors = Identity().Actors.ToArray();
        var teamSwap = actors.Select((a, n) => new FifaAutoActorIdentity(a.ActorInstanceId, a.PlayerId,
            n == 0 ? 1 : n == 11 ? 0 : a.TeamId, a.Goalkeeper, a.TransformInstanceId, a.RendererInstanceIds.ToArray())).ToArray();
        changes.Add(Identity(actors: teamSwap));
        changes.Add(Identity(actors: actors.Select((a, n) => new FifaAutoActorIdentity(a.ActorInstanceId, a.PlayerId,
            a.TeamId, n == 0 ? false : n == 1 ? true : a.Goalkeeper, a.TransformInstanceId, a.RendererInstanceIds.ToArray())).ToArray()));
        foreach (var identity in changes)
        {
            var o = Observation(); o.Identity = identity;
            o.Players = identity.Actors.Select(a => (a.ActorInstanceId, a.TeamId, true, true, a.Goalkeeper)).ToArray();
            Check(FifaAutoCandidate.IsValid(o.Players) && FifaAutoIdentity.Matches(identity, o), "changed fixture must remain complete/resolved/bound");
            foreach (int warmup in new[] { 1, 2 }) {
                var c = new FifaAutoPollCoordinator("session-A");
                for (int n = 0; n < warmup; n++) c.Poll(Observation());
                Check(c.Poll(o) == FifaAutoPollDecision.NotReady, "changed canonical identity resets stable polls despite unchanged loose token");
                Check(c.Poll(o) == FifaAutoPollDecision.PartialProposal, "changed complete identity requires exactly two polls");
                Check(c.Attempt.TryReserve("session-A", true), "identity transitions do not reserve lifetime attempt");
            }
        }
        var stable = Observation(); var coordinator = new FifaAutoPollCoordinator("session-A");
        Check(coordinator.Poll(stable) == FifaAutoPollDecision.NotReady, "initial canonical identity waits");
        stable.Identity = ReorderedIdentity(); stable.Players = stable.Players.Reverse().ToArray();
        stable.CandidateFingerprint = "untrusted-other-token";
        Check(coordinator.Poll(stable) == FifaAutoPollDecision.PartialProposal, "canonical reorder matures regardless of caller token");
        foreach (string token in new string[] { null, "", "A|B", "different" }) {
            stable.CandidateFingerprint = token;
            Check(coordinator.Poll(stable) == FifaAutoPollDecision.PartialProposal, "loose caller token no longer controls stability");
        }
        RefusesAndResets(o => o.Identity = Identity(session: "session-B"), "foreign identity session");
        RefusesAndResets(o => o.Identity = Identity(scene: "Frontend"), "foreign identity scene");
    }

    private static IEnumerable<FifaAutoCandidateIdentity> IdentityChanges()
    {
        yield return Identity(session: "session-B"); yield return Identity(scene: "Other");
        yield return Identity(handle: -8); yield return Identity(root: -11); yield return Identity(controller: -21);
        yield return WithActor(a => new(-101, a.PlayerId, a.TeamId, a.Goalkeeper, a.TransformInstanceId, a.RendererInstanceIds.ToArray()));
        yield return WithActor(a => new(a.ActorInstanceId, a.PlayerId + 1, a.TeamId, a.Goalkeeper, a.TransformInstanceId, a.RendererInstanceIds.ToArray()));
        yield return WithActor(a => new(a.ActorInstanceId, a.PlayerId, 1, a.Goalkeeper, a.TransformInstanceId, a.RendererInstanceIds.ToArray()));
        yield return WithActor(a => new(a.ActorInstanceId, a.PlayerId, a.TeamId, !a.Goalkeeper, a.TransformInstanceId, a.RendererInstanceIds.ToArray()));
        yield return WithActor(a => new(a.ActorInstanceId, a.PlayerId, a.TeamId, a.Goalkeeper, -999, a.RendererInstanceIds.ToArray()));
        yield return WithActor(a => new(a.ActorInstanceId, a.PlayerId, a.TeamId, a.Goalkeeper, a.TransformInstanceId, new[] { -111, 112 }));
        yield return WithActor(a => new(a.ActorInstanceId, a.PlayerId, a.TeamId, a.Goalkeeper, a.TransformInstanceId, a.RendererInstanceIds.Concat(new[] { -222 }).ToArray()));
    }
    private static FifaAutoCandidateIdentity ReorderedIdentity()
        => Identity(actors: Identity().Actors.Reverse().Select(a => new FifaAutoActorIdentity(a.ActorInstanceId,
            a.PlayerId, a.TeamId, a.Goalkeeper, a.TransformInstanceId, a.RendererInstanceIds.Reverse().ToArray())).ToArray());
    private static void IdentityCanonical()
    {
        var vector = new FifaAutoCandidateIdentity("A|β", "", -7, -10, -20,
            new[] { new FifaAutoActorIdentity(-1, 2, 0, true, -3, new[] { 9, -8 }) });
        Check(FifaAutoIdentity.CanonicalToken(vector) == "RkFJMQEEAAAAQXzOsgIAAAAAA/n///8E9v///wXs////BgEAAAAH/////wgCAAAACQAAAAAKAQv9////DAIAAAAN+P///w0JAAAA",
            "canonical bytes must match independent little-endian tagged UTF8 vector");
        string token = FifaAutoIdentity.CanonicalToken(Identity());
        foreach (var changed in IdentityChanges())
            Check(FifaAutoIdentity.CanonicalToken(changed) != token, "each canonical identity field is significant");
        Check(token == FifaAutoIdentity.CanonicalToken(ReorderedIdentity()), "actor and renderer reorder is canonical");
        var culture = System.Globalization.CultureInfo.CurrentCulture;
        try {
            foreach (string name in new[] { "fr-FR", "ar-SA", "tr-TR" }) {
                System.Globalization.CultureInfo.CurrentCulture = new(name);
                Check(token == FifaAutoIdentity.CanonicalToken(ReorderedIdentity()), "culture-independent canonical token");
            }
        } finally { System.Globalization.CultureInfo.CurrentCulture = culture; }
        Check(FifaAutoIdentity.CanonicalToken(Identity(session: null)) != FifaAutoIdentity.CanonicalToken(Identity(session: "")), "null distinct from empty");
        Check(FifaAutoIdentity.CanonicalToken(Identity(session: "a|b", scene: "c")) != FifaAutoIdentity.CanonicalToken(Identity(session: "a", scene: "b|c")), "length prefixes prevent delimiter ambiguity");
        Check(FifaAutoIdentity.CanonicalToken(Identity(session: "é")) != FifaAutoIdentity.CanonicalToken(Identity(session: "é")), "literal unicode not normalized");
    }

    private static void IdentityBinding()
    {
        var baseActors = Identity().Actors.ToArray();
        var invalid = new[] {
            Identity(session: "session-B"), Identity(scene: "Frontend"),
            Identity(actors: baseActors.Skip(1).ToArray()), Identity(actors: baseActors.Concat(new[] { baseActors[0] }).ToArray()),
            Identity(actors: baseActors.Select((a, i) => i == 1 ? baseActors[0] : a).ToArray()),
            WithActor(a => new(a.ActorInstanceId + 9999, a.PlayerId, a.TeamId, a.Goalkeeper, a.TransformInstanceId, a.RendererInstanceIds.ToArray())),
            WithActor(a => new(a.ActorInstanceId, a.PlayerId, 1, a.Goalkeeper, a.TransformInstanceId, a.RendererInstanceIds.ToArray())),
            WithActor(a => new(a.ActorInstanceId, a.PlayerId, a.TeamId, !a.Goalkeeper, a.TransformInstanceId, a.RendererInstanceIds.ToArray()))
        };
        foreach (var identity in invalid)
        {
            Check(FifaAutoIdentity.IsComplete(identity), "binding negative must have complete IDs");
            RefusesAndResets(o => o.Identity = identity, "identity must bind exact selected inventory");
        }
        var o = Observation(); var players = Complete(); players.Add((-888, 0, false, false, false)); o.Players = players;
        var selected = FifaActorSelection.ResolveFifa(new FifaOverlayRequest { AllEligible = true }, players);
        Check(selected.OrderBy(x => x).SequenceEqual(o.Identity.Actors.Select(a => a.ActorInstanceId).OrderBy(x => x)),
            "positive identity equals actual resolver, excludes unselected inventory");
        var c = new FifaAutoPollCoordinator("session-A");
        Check(c.Poll(o) == FifaAutoPollDecision.NotReady && c.Poll(o) == FifaAutoPollDecision.PartialProposal,
            "unselected inventory must not require unrelated identity");
        var first = players[0]; players[0] = (-101, first.TeamId, first.Eligible, first.FifaEligible, first.Goalkeeper);
        o.Identity = WithActor(a => new(-101, a.PlayerId, a.TeamId, a.Goalkeeper, a.TransformInstanceId, a.RendererInstanceIds.ToArray()));
        c = new("session-A");
        Check(c.Poll(o) == FifaAutoPollDecision.NotReady && c.Poll(o) == FifaAutoPollDecision.PartialProposal,
            "signed actor ID with independent positive PlayerId remains valid");
    }

    private static FifaAutoCandidateIdentity WithActor(Func<FifaAutoActorIdentity, FifaAutoActorIdentity> change)
    {
        var actors = Identity().Actors.ToArray(); actors[0] = change(actors[0]);
        return Identity(actors: actors);
    }
    private static void IdentityComplete()
    {
        Check(FifaAutoIdentity.IsComplete(Identity()), "signed native IDs are complete");
        var invalid = new List<FifaAutoCandidateIdentity> {
            null, Identity(session: null), Identity(session: ""), Identity(scene: null), Identity(scene: ""),
            Identity(handle: 0), Identity(root: 0), Identity(controller: 0),
            new("session-A", "MatchPlayback", -7, -10, -20, null),
            Identity(actors: Array.Empty<FifaAutoActorIdentity>()), WithActor(a => null),
            WithActor(a => new(0, a.PlayerId, a.TeamId, a.Goalkeeper, a.TransformInstanceId, a.RendererInstanceIds.ToArray())),
            WithActor(a => new(a.ActorInstanceId, 0, a.TeamId, a.Goalkeeper, a.TransformInstanceId, a.RendererInstanceIds.ToArray())),
            WithActor(a => new(a.ActorInstanceId, -1, a.TeamId, a.Goalkeeper, a.TransformInstanceId, a.RendererInstanceIds.ToArray())),
            WithActor(a => new(a.ActorInstanceId, a.PlayerId, a.TeamId, a.Goalkeeper, 0, a.RendererInstanceIds.ToArray())),
            WithActor(a => new(a.ActorInstanceId, a.PlayerId, a.TeamId, a.Goalkeeper, a.TransformInstanceId, null)),
            WithActor(a => new(a.ActorInstanceId, a.PlayerId, a.TeamId, a.Goalkeeper, a.TransformInstanceId, Array.Empty<int>())),
            WithActor(a => new(a.ActorInstanceId, a.PlayerId, a.TeamId, a.Goalkeeper, a.TransformInstanceId, new[] { -8, 0 }))
        };
        foreach (var identity in invalid)
            RefusesAndResets(o => o.Identity = identity, "incomplete identity evidence");
    }

    private static void IdentityCopy()
    {
        int[] renderers = { -8, 9 };
        var actor = new FifaAutoActorIdentity(-1, 2, 0, true, -3, renderers);
        var actors = new[] { actor };
        var identity = Identity(actors: actors);
        renderers[0] = 777; actors[0] = null;
        Check(identity.Actors[0] != null && identity.Actors[0].RendererInstanceIds[0] == -8,
            "identity constructor must deep copy caller arrays");
        Check(identity.Actors is not FifaAutoActorIdentity[] && actor.RendererInstanceIds is not int[],
            "identity must expose no mutable array aliases");
        try { ((IList<int>)actor.RendererInstanceIds)[0] = 88; throw new Exception("FAIL: mutable renderer view"); }
        catch (NotSupportedException) { }
        try { ((IList<FifaAutoActorIdentity>)identity.Actors)[0] = null; throw new Exception("FAIL: mutable actor view"); }
        catch (NotSupportedException) { }
        Check(identity.Actors[0].RendererInstanceIds[0] == -8, "nested copy remains intact");
    }

    // Entirely synthetic identities: never a claim of native evidence.
    private static FifaAutoCandidateIdentity Identity(
        string session = "session-A", string scene = "MatchPlayback", int handle = -7,
        int root = -10, int controller = -20, FifaAutoActorIdentity[] actors = null)
        => new(session, scene, handle, root, controller, actors ?? Complete().Select(p =>
            new FifaAutoActorIdentity(p.ActorInstanceId, p.ActorInstanceId + 1000, p.TeamId,
                p.Goalkeeper, -p.ActorInstanceId, new[] { -p.ActorInstanceId * 10, p.ActorInstanceId * 10 + 1 })).ToArray());
}
