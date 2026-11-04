#nullable disable
using FM26.VisualDiagnostics;
using Inventory = System.Collections.Generic.List<(int ActorInstanceId, int TeamId, bool Eligible, bool FifaEligible, bool Goalkeeper)>;

internal static partial class H184aAutoCoordinatorTests
{
    private static readonly List<(string Name, Action Test)> Cases = new()
    {
        ("identity-stability", IdentityStability),
        ("identity-canonical", IdentityCanonical),
        ("identity-binding", IdentityBinding),
        ("identity-complete", IdentityComplete),
        ("identity-copy", IdentityCopy),
        ("poll-ready", Ready),
        ("poll-session", Session),
        ("poll-scene", Scene),
        ("poll-root-present", RootPresent),
        ("poll-root-active", RootActive),
        ("poll-other-modes", OtherModes),
        ("poll-pack", GuardPackVerified),
        ("poll-renderers", GuardRenderersReady),
        ("poll-manual-priority", GuardManualCommandPending),
        ("poll-report-collision", GuardRequestReportExists),
        ("poll-identity-current", GuardIdentitySnapshotCurrent),
        ("poll-char-optin", CharacterizeOptIn),
        ("poll-char-candidate", CharacterizeCandidate),
        ("poll-char-fingerprint", CharacterizeFingerprint),
        ("poll-char-lifetime", CharacterizeLifetime),
    };
    public static void Run(string name = null)
    {
        int ran = 0;
        foreach (var c in Cases)
        {
            if (name != null && name != c.Name) continue;
            c.Test(); ran++; Console.WriteLine("H184a PASS: " + c.Name);
        }
        if (name != null && ran != 1) throw new Exception("Unknown H184a case " + name);
    }
    private static void Check(bool condition, string message)
    {
        if (!condition) throw new Exception("FAIL: H184a " + message);
    }
    private static Inventory Complete()
    {
        var inv = new Inventory();
        for (int team = 0; team < 2; team++)
            for (int i = 0; i < 11; i++)
            {
                var b = new PlayerBinding { PlayerId = 100 + team * 100 + i, ShirtNumber = i + 1,
                    TeamId = team, Active = true, Goalkeeper = i == 0 };
                foreach (var key in PlayerBinding.RequiredAnchors) b.Anchors.Add(key, new object());
                bool legacy = VisualPolicy.HasValidShirtNumber(b.ShirtNumber) && VisualPolicy.IsEligible(b.Active,
                    b.Substitute, b.Goalkeeper, b.Medic, b.PlayerId, PlayerBinding.RequiredAnchors.Count(b.Anchors.ContainsKey));
                inv.Add((b.PlayerId, team, legacy, b.FifaEligible, b.Goalkeeper));
            }
        return inv;
    }
    // Synthetic copied observation, NOT an executed Unity adapter or certified native identity.
    private static FifaAutoPollObservation Observation() => new()
    {
        Enabled = true, PluginSession = "session-A", SceneName = "MatchPlayback",
        RootPresent = true, RootActive = true, OtherModesBlocked = false,
        PackVerified = true, RenderersReady = true, ManualCommandPending = false,
        RequestReportExists = false, IdentitySnapshotCurrent = true, RenderersVisible = true, // H209: players visible
        CandidateFingerprint = "synthetic-candidate-A", Players = Complete(), Identity = Identity(),
    };
    private static void RefusesAndResets(Action<FifaAutoPollObservation> invalidate, string label)
    {
        foreach (int warmup in new[] { 0, 1, 2 })
        {
            var c = new FifaAutoPollCoordinator("session-A");
            for (int i = 0; i < warmup; i++) c.Poll(Observation());
            var invalid = Observation(); invalidate(invalid);
            Check(c.Poll(invalid) == FifaAutoPollDecision.NotReady, label + " must refuse after warmup " + warmup);
            Check(c.Poll(invalid) == FifaAutoPollDecision.NotReady, label + " must never mature while invalid");
            Check(c.Poll(Observation()) == FifaAutoPollDecision.NotReady, label + " must reset stable count");
            Check(c.Poll(Observation()) == FifaAutoPollDecision.PartialProposal, label + " recovery requires two valid polls");
            Check(c.Attempt.TryReserve("session-A", true), label + " refusal/recovery must not consume attempt");
        }
    }
    private static void Session()
    {
        foreach (string session in new string[] { "session-B", "session-a", "session-A ", null, "" })
            RefusesAndResets(o => o.PluginSession = session, "plugin session mismatch");
    }
    private static void Scene()
    {
        foreach (string scene in new string[] { "Frontend", "matchplayback", "MatchPlayback ", "[invalid]", null, "" })
            RefusesAndResets(o => o.SceneName = scene, "non-MatchPlayback scene");
    }
    // Independent copied flag: RootActive stays true to isolate presence, not a feasible native null dereference.
    private static void RootPresent() => RefusesAndResets(o => o.RootPresent = false, "missing root");
    private static void RootActive() => RefusesAndResets(o => o.RootActive = false, "inactive present root");
    private static void OtherModes() => RefusesAndResets(o => o.OtherModesBlocked = true, "other owned/semantic mode or cleanup");
    private static void GuardPackVerified()
    {
        // Assert conservative default too; fixture supplies every gate explicitly, never test-default bypass.
        Check(new FifaAutoPollObservation().PackVerified == false, "PackVerified conservative default");
        RefusesAndResets(o => o.PackVerified = false, "PackVerified downstream gate");
    }
    private static void GuardRenderersReady()
    {
        // Assert conservative default too; fixture supplies every gate explicitly, never test-default bypass.
        Check(new FifaAutoPollObservation().RenderersReady == false, "RenderersReady conservative default");
        RefusesAndResets(o => o.RenderersReady = false, "RenderersReady downstream gate");
    }
    private static void GuardManualCommandPending()
    {
        // Assert conservative default too; fixture supplies every gate explicitly, never test-default bypass.
        Check(new FifaAutoPollObservation().ManualCommandPending == true, "ManualCommandPending conservative default");
        RefusesAndResets(o => o.ManualCommandPending = true, "ManualCommandPending downstream gate");
    }
    private static void GuardRequestReportExists()
    {
        // Assert conservative default too; fixture supplies every gate explicitly, never test-default bypass.
        Check(new FifaAutoPollObservation().RequestReportExists == true, "RequestReportExists conservative default");
        RefusesAndResets(o => o.RequestReportExists = true, "RequestReportExists downstream gate");
    }
    private static void GuardIdentitySnapshotCurrent()
    {
        // Assert conservative default too; fixture supplies every gate explicitly, never test-default bypass.
        Check(new FifaAutoPollObservation().IdentitySnapshotCurrent == false, "IdentitySnapshotCurrent conservative default");
        RefusesAndResets(o => o.IdentitySnapshotCurrent = false, "IdentitySnapshotCurrent downstream gate");
    }
    // Post-cycle characterizations of inherited modules through the actual coordinator; no production edits.
    private static void CharacterizeOptIn()
    {
        RefusesAndResets(o => o.Enabled = new FifaAutoPollObservation().Enabled, "default disabled observation");
        foreach (byte[] bytes in new byte[][] { null, System.Text.Encoding.UTF8.GetBytes("{}"),
            System.Text.Encoding.UTF8.GetBytes("{\"Enabled\":false}") })
            RefusesAndResets(o => o.Enabled = FifaAutoActivationConfiguration.ParseEnabled(bytes), "inherited disabled configuration");
    }
    private static void CharacterizeCandidate()
    {
        RefusesAndResets(o => o.Players = null, "null copied inventory");
        RefusesAndResets(o => o.Players = new Inventory(), "empty copied inventory");
        RefusesAndResets(o => { var p = Complete(); p.RemoveAt(10); o.Players = p; }, "21 selected");
        RefusesAndResets(o => { var p = Complete(); p.Add((-999, 0, true, true, false)); o.Players = p; }, "23 selected");
        RefusesAndResets(o => { var p = Complete(); p.Add(p[0]); o.Players = p; }, "duplicate actor identity");
        RefusesAndResets(o => { var p = Complete(); var a = p[10]; p[10] = (a.ActorInstanceId, 1, true, true, false); o.Players = p; }, "10/12 balance");
        RefusesAndResets(o => { var p = Complete(); var a = p[0]; p[0] = (a.ActorInstanceId, 0, true, true, false); o.Players = p; }, "missing team-0 keeper");
        RefusesAndResets(o => { var p = Complete(); var a = p[1]; p[1] = (a.ActorInstanceId, 0, false, true, true); o.Players = p; }, "inherited second keeper refusal");
        RefusesAndResets(o => { var p = Complete(); p.Add((-999, 2, true, false, false)); o.Players = p; }, "legacy eligible unsupported team must not disappear");
        var manual = Complete(); manual.Add((-998, 0, true, true, false)); manual.Add((-999, 1, true, true, false));
        Check(FifaActorSelection.ResolveFifa(new FifaOverlayRequest { AllEligible = true }, manual).Length == 24,
            "manual resolver still supports 24; auto has not redefined it");
    }
    private static void CharacterizeFingerprint() => IdentityStability();
    private static void CharacterizeLifetime()
    {
        var c = new FifaAutoPollCoordinator("session-A"); var original = c.Attempt;
        c.Poll(Observation()); c.Poll(Observation());
        var disabled = Observation(); disabled.Enabled = false; c.Poll(disabled);
        var invalid = Observation(); invalid.Players = null; c.Poll(invalid);
        var changed = Observation(); changed.Identity = Identity(root: -11);
        c.Poll(changed); c.Poll(changed);
        Check(ReferenceEquals(original, c.Attempt), "config and candidate changes never recreate owned attempt");
        Check(!original.TryReserve("session-B", true), "owned attempt uses constructor session");
        Check(original.TryReserve("session-A", true), "all partial polling left attempt unconsumed");
        c.Poll(disabled); c.Poll(Observation()); c.Poll(Observation());
        Check(ReferenceEquals(original, c.Attempt) && !c.Attempt.TryReserve("session-A", true),
            "poll transitions do not reopen consumed attempt");
    }
    private static void Ready()
    {
        var c = new FifaAutoPollCoordinator("session-A"); var o = Observation();
        Check(FifaAutoCandidate.IsValid(o.Players), "producer-shaped fixture must be complete and eligible");
        Check(c.Poll(o) == FifaAutoPollDecision.NotReady, "first ready poll must wait");
        Check(c.Poll(o) == FifaAutoPollDecision.PartialProposal, "second ready poll must yield partial proposal");
        Check(c.Poll(o) == FifaAutoPollDecision.PartialProposal, "stable proposal remains a nonexecuting suggestion");
        Check(c.Attempt.TryReserve("session-A", true), "proposal must not consume the owned lifetime attempt");
        Check(!c.Attempt.TryReserve("session-A", true), "owned attempt retains inherited once-per-lifetime behavior");
    }
}
