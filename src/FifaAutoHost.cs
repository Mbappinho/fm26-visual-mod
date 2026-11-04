#nullable disable
namespace FM26.VisualDiagnostics;

// H186: pure automatic-activation host step, compiled into PolicyTests.
// The plugin glue supplies copied file facts; the produced bytes are a STANDARD start command
// that goes through the shared manual command path (session, segment slot, modes, resolver, B1, pack, renderers).
public sealed class FifaAutoHost
{
    private readonly string _session;
    private readonly FifaAutoPollCoordinator _coordinator;

    public FifaAutoHost(string pluginSession)
    {
        _session = pluginSession;
        _coordinator = new FifaAutoPollCoordinator(pluginSession);
    }

    // Copies host file facts into the observation; the standard Start still verifies the pack itself.
    public static FifaAutoPollObservation Prepare(FifaAutoPollObservation observation,
        bool packHeaderPresent, bool manualCommandPending, bool requestReportExists)
    {
        observation.PackVerified = packHeaderPresent;
        observation.ManualCommandPending = manualCommandPending;
        observation.RequestReportExists = requestReportExists;
        return observation;
    }

    // Deterministic, session-bound RequestId; the standard parser still validates its charset and length.
    public static string RequestIdFor(string session) => "auto-" + session;

    // One host poll: config bytes (null = absent file), lazy native capture and copied file facts.
    // matchLoaded=false (boot/menus): no native capture at all.
    // Only our own cleanly completed segment rotates the coordinator and request identity.
    public byte[] Step(byte[] configBytes, bool matchLoaded, bool canStartSegment, string lastOverlayRequestId, Func<FifaAutoPollObservation> capture,
        bool packHeaderPresent, bool manualCommandPending, bool requestReportExists)
    {
        if (_submitted && canStartSegment && lastOverlayRequestId == CurrentRequestId) NextSegment(); // previous segment ended cleanly: fresh stability and attempt
        if (_submitted || !matchLoaded || !canStartSegment || !Enabled(configBytes))
        {
            Coordinator.Seed(null); // H211: a seed is only valid for the first host poll after the rotation
            return null;
        }
        var bytes = Poll(Prepare(capture(), packHeaderPresent, manualCommandPending, requestReportExists));
        _submitted |= bytes != null; // the segment attempt is spent: no more native reads until its clean completion
        return bytes;
    }

    // Host overload: report existence is evaluated for the RequestId this step would submit (after any segment rotation).
    public byte[] Step(byte[] configBytes, bool matchLoaded, bool canStartSegment, string lastOverlayRequestId, Func<FifaAutoPollObservation> capture,
        bool packHeaderPresent, bool manualCommandPending, Func<string, bool> reportExistsFor)
    {
        if (_submitted && canStartSegment && lastOverlayRequestId == CurrentRequestId) NextSegment();
        return Step(configBytes, matchLoaded, canStartSegment, lastOverlayRequestId, capture, packHeaderPresent, manualCommandPending, reportExistsFor(CurrentRequestId));
    }

    // H211 (0.10.5): a roster-changed stop of the current AUTO segment hands over the roster watch's confirmed selection.
    public void SeedNextSegment(IReadOnlyList<int> confirmed) { _pendingSeed = confirmed; }
    private IReadOnlyList<int> _pendingSeed;

    private bool _submitted;
    private FifaAutoPollCoordinator _segmentCoordinator;

    private void NextSegment()
    {
        _segmentCoordinator = new FifaAutoPollCoordinator(_session);
        _segmentCoordinator.Seed(_pendingSeed);
        _pendingSeed = null; // one segment only
        _segment++;
        _submitted = false;
    }

    // Invalid configuration = disabled (fail closed); the strict parser's refusal never escapes into the plugin poll.
    private static bool Enabled(byte[] configBytes)
    {
        try { return FifaAutoActivationConfiguration.ParseEnabled(configBytes); }
        catch (Exception) { return false; }
    }

    public string CurrentRequestId => _segment == 1 ? RequestIdFor(_session) : RequestIdFor(_session) + "-" + _segment;
    private int _segment = 1;

    private FifaAutoPollCoordinator Coordinator => _segmentCoordinator ?? _coordinator;

    // Returns standard start-command bytes once the coordinator's two-poll proposal matures, else null.
    public byte[] Poll(FifaAutoPollObservation observation)
        => Coordinator.Attempt.TryReserve(_session, Coordinator.Poll(observation) == FifaAutoPollDecision.PartialProposal)
            ? Command(_session, CurrentRequestId) : null; // reservation consumed before the caller writes the file

    private static byte[] Command(string session, string requestId) => System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(new
    {
        Session = session, RequestId = requestId, Action = FifaOverlayPolicy.StartAction,
        ActorInstanceIds = "all-eligible", MaximumSeconds = FifaOverlayPolicy.MaximumSecondsCap, HideNativePlayer = true,
    });
}
