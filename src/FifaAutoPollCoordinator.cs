#nullable disable
namespace FM26.VisualDiagnostics;

// Copied adapter inputs only. Named fields are not proof that their native reads are wired.
public sealed class FifaAutoPollObservation
{
    public bool Enabled;
    public string PluginSession;
    public string SceneName;
    public bool RootPresent;
    public bool RootActive;
    public bool OtherModesBlocked;
    // Conservative defaults: readiness requires positive evidence; unknown pending/report blocks.
    // H184b carrier-only declaration; fields are inert until their individual behavioral cycles.
    public bool PackVerified;
    public bool RenderersReady;
    public bool ManualCommandPending = true;
    public bool RequestReportExists = true;
    public bool IdentitySnapshotCurrent;
    // H209 (0.10.4): every selected actor has a renderer active in hierarchy (FM view not hiding the players); copied by
    // FifaAutoNativeObservation, required by Poll. Conservative default false.
    public bool RenderersVisible;
    // Compatibility-only input: ignored by Poll; canonical copied identity controls stability.
    public string CandidateFingerprint;
    public FifaAutoCandidateIdentity Identity;
    public IReadOnlyList<(int ActorInstanceId, int TeamId, bool Eligible, bool FifaEligible, bool Goalkeeper)> Players;
}

// PartialProposal deliberately excludes downstream readiness and NEVER authorizes execution.
public enum FifaAutoPollDecision { NotReady, PartialProposal }

// Unused host preparation: no native calls, I/O or runtime integration.
// Future host owns ONE instance per immutable plugin session, not per poll/config/candidate.
public sealed class FifaAutoPollCoordinator
{
    private readonly string _session;
    private readonly FifaAutoActivationStability _stability = new();
    internal readonly FifaAutoActivationAttempt Attempt;

    public FifaAutoPollCoordinator(string pluginSession)
    {
        _session = pluginSession;
        Attempt = new FifaAutoActivationAttempt(pluginSession);
    }

    // H211 (0.10.5): one-shot seed = the selection the roster watch already confirmed on two consecutive polls (null = none).
    public void Seed(IReadOnlyList<int> confirmed) { _seed = confirmed; }
    private IReadOnlyList<int> _seed;

    // Only a partial proposal: all deferred downstream gates still forbid reservation/submission.
    public FifaAutoPollDecision Poll(FifaAutoPollObservation observation)
    {
        bool ready = FifaAutoActivationDecision.IsOptInReady(observation.Enabled)
            && string.Equals(_session, observation.PluginSession, System.StringComparison.Ordinal)
            && string.Equals(observation.SceneName, "MatchPlayback", System.StringComparison.Ordinal)
            && observation.RootPresent
            && observation.RootActive
            && !observation.OtherModesBlocked
            && observation.PackVerified
            && observation.RenderersReady
            && !observation.ManualCommandPending
            && !observation.RequestReportExists
            && observation.IdentitySnapshotCurrent
            && observation.RenderersVisible // H209: never propose while FM's presentation hides the players (live-014)
            && FifaAutoCandidate.IsValid(observation.Players)
            && FifaAutoIdentity.IsComplete(observation.Identity)
            && FifaAutoIdentity.Matches(observation.Identity, observation);
        bool stable = _stability.Observe(ready, ready ? FifaAutoIdentity.CanonicalToken(observation.Identity) : null);
        bool seeded = ready && _seed != null
            && FifaActorSelection.ResolveFifa(new FifaOverlayRequest { AllEligible = true }, observation.Players).OrderBy(id => id).SequenceEqual(_seed.OrderBy(id => id));
        _seed = null; // one-shot: only the first poll after the seeded rotation may use it
        return stable || seeded ? FifaAutoPollDecision.PartialProposal : FifaAutoPollDecision.NotReady;
    }
}
