using System.Text.Json;
using UnityEngine;

namespace FM26.VisualDiagnostics;

// H154 stage 1 host (H166: one session host for 1..24 actors): command-file processing (called from the existing 2 s poll through one declared additive seam)
// and per-frame ticks from a separate component, so the frozen legacy Update/LateUpdate/OnDestroy bodies stay unchanged.
internal static class FifaOverlayHost
{
    private static FifaOverlay _overlay; // retained until clean verified publication; maximum 32 sequential segments
    private static GameObject _overlayRoot; // accepted current root, only used by the active AUTO watch
    private static int[] _overlaySelected; // H166: actor set of the accepted start (request result echo)
    private static int _segments; // H191: overlays started in this plugin session
    public static bool Blocked => _overlay?.OverlapActive ?? false;
    public static bool Attempted => _overlay != null;

    public static void Stop(string reason) => _overlay?.Stop(reason);
    public static void CleanupTick() => _overlay?.CleanupTick();
    public static void Tick() => _overlay?.Tick();
    public static void Shutdown() => _overlay?.Shutdown();

    public static void Process(GameObject root, List<PlayerBinding> players, bool otherModesBlocked, bool candidateOrPrototypeSession, Action<string, object> write)
    {
        var path = Path.Combine(DiagnosticsPlugin.Output, "fifa-overlay-command.json");
        RosterCheck(root, players, candidateOrPrototypeSession);
        AutoStep(path, root, players, otherModesBlocked, candidateOrPrototypeSession);
        if (File.Exists(path)) Command(path, root, players, otherModesBlocked, candidateOrPrototypeSession, write);
        if (_overlay != null) write("fifa-overlay-state.json", _overlay.Describe());
    }

    // H186: opt-in automatic activation. Absent config file = no work and no native reads. All decisions live in the
    // PolicyTests-compiled FifaAutoHost.Step; this glue supplies copied file facts and writes the STANDARD command
    // without overwriting, so the shared Command path below re-checks every guard of a manual start.
    private static FifaAutoHost _auto; // one per plugin session, never recreated
    private static bool _autoErrorLogged;
    private static string _autoSubmittedRequestId; // only a successfully moved AUTO command establishes origin

    private static void AutoStep(string commandPath, GameObject root, List<PlayerBinding> players, bool otherModesBlocked, bool candidateOrPrototypeSession)
    {
        try
        {
            var configPath = Path.Combine(DiagnosticsPlugin.Output, "fifa-overlay-auto.json");
            if (!File.Exists(configPath)) return;
            _auto ??= new FifaAutoHost(DiagnosticsPlugin.Session);
            var packHeader = Path.Combine(FifaOverlayPolicy.ResolveDefaultPackDirectory(Environment.GetEnvironmentVariable("LOCALAPPDATA"), File.Exists), "fifa-pack.json");
            // Request-bound report facts must follow any rotation inside the pure auto step.
            // Native FM match types are only touched once the match root and player bindings exist (H186b: boot crash).
            bool matchLoaded = root != null && players != null && players.Count > 0;
            bool canStartSegment = _overlay == null || FifaOverlaySegments.CanStart(true, _overlay.Finalized, _overlay.CleanupVerified, _overlay.Published, _overlay.Error, _segments);
            var bytes = _auto.Step(ReadBounded(configPath), matchLoaded, canStartSegment, _overlay?.RequestId,
                () => FifaAutoNativeObservation.Capture(root, players, DiagnosticsPlugin.Session, true, otherModesBlocked, candidateOrPrototypeSession),
                File.Exists(packHeader), File.Exists(commandPath),
                requestId => File.Exists(Path.Combine(DiagnosticsPlugin.Output, FifaOverlayReport.FileName(requestId))));
            if (bytes == null) return;
            var temporary = commandPath + ".auto.tmp";
            File.WriteAllBytes(temporary, bytes);
            try
            {
                File.Move(temporary, commandPath); // never overwrites a pending manual command
                _autoSubmittedRequestId = _auto.CurrentRequestId;
            }
            finally { if (File.Exists(temporary)) File.Delete(temporary); }
            DiagnosticsPlugin.Logger.LogInfo($"[VISDIAG] FIFA_OVERLAY_AUTO_SUBMITTED: {_auto.CurrentRequestId}");
        }
        catch (Exception exception)
        {
            if (!_autoErrorLogged) DiagnosticsPlugin.Logger.LogError($"[VISDIAG] FIFA_OVERLAY_AUTO_FAILED: {exception.Message}");
            _autoErrorLogged = true;
        }
    }

    private static FifaRosterWatch _rosterWatch = new();

    // Process is already bounded to the existing two-second command poll, never per-frame.
    private static void RosterCheck(GameObject root, List<PlayerBinding> players, bool candidateOrPrototypeSession)
    {
        if (_overlay?.Active != true || _auto == null || _overlay.RequestId != _autoSubmittedRequestId
            || _overlay.RequestId != _auto.CurrentRequestId
            || root == null || root != _overlayRoot || players == null || players.Count == 0 || candidateOrPrototypeSession) return;
        try
        {
            if (_rosterWatch.Observe(_overlaySelected,
                PlayerBinding.Collect(root).Select(p => (p.ActorInstanceId, p.TeamId, p.Eligible, p.FifaEligible, p.Goalkeeper)).ToArray()))
            {
                _overlay.Stop("roster-changed");
                _auto.SeedNextSegment(_rosterWatch.Confirmed); // H211: the next segment may start on the first matching poll
            }
        }
        catch (Exception exception) { _overlay.Stop("roster-observation-failed", exception.Message); }
    }

    // Reads at most 4097 bytes; the strict parser refuses anything above 4096 (treated as disabled).
    private static byte[] ReadBounded(string path)
    {
        using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        var buffer = new byte[4097];
        int total = 0, read;
        while (total < buffer.Length && (read = file.Read(buffer, total, buffer.Length - total)) > 0) total += read;
        return buffer.Take(total).ToArray();
    }

    private static void Command(string path, GameObject root, List<PlayerBinding> players, bool otherModesBlocked, bool candidateOrPrototypeSession, Action<string, object> write)
    {
        FifaOverlayRequest request = null;
        try
        {
            byte[] bytes;
            using (var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                if (file.Length > 4096) throw new InvalidOperationException("Oversized FIFA overlay command.");
                var buffer = new byte[4097];
                int total = 0, read;
                while (total < buffer.Length && (read = file.Read(buffer, total, buffer.Length - total)) > 0) total += read;
                if (total > 4096) throw new InvalidOperationException("Growing FIFA overlay command exceeds bound.");
                bytes = buffer.Take(total).ToArray();
            }
            using (var document = JsonDocument.Parse(bytes)) request = FifaOverlayPolicy.ParseRequest(document.RootElement, DiagnosticsPlugin.Session);
            if (request.Action == FifaOverlayPolicy.StartAction)
            {
                if (!FifaOverlaySegments.CanStart(_overlay != null, _overlay?.Finalized ?? false, _overlay?.CleanupVerified ?? false, _overlay?.Published ?? false, _overlay?.Error, _segments))
                    throw new InvalidOperationException("Previous FIFA overlay segment is not clean, verified and published, or the segment cap is reached.");
                if (otherModesBlocked) throw new InvalidOperationException("FIFA overlay refused during another owned/semantic mode or its cleanup.");
                if (candidateOrPrototypeSession) throw new InvalidOperationException("FIFA overlay refused in a candidate/body-replacement session.");
                if (root == null || players == null) throw new InvalidOperationException("Target is not exactly one current eligible outfield actor.");
                if (!request.Multi)
                {
                    var matches = players.Where(p => p.ActorInstanceId == request.ActorInstanceId).ToArray();
                    if (matches.Length != 1 || !matches[0].Eligible) throw new InvalidOperationException("Target is not exactly one current eligible outfield actor.");
                }
                // Actor set resolved against the CURRENT inventory before the segment attempt is consumed (legacy = N=1).
                var ids = FifaActorSelection.ResolveFifa(request, players.Select(p => (p.ActorInstanceId, p.TeamId, p.Eligible, p.FifaEligible, p.Goalkeeper)).ToArray());
                var selected = ids.Select(id => players.Single(p => p.ActorInstanceId == id)).ToArray();
                // B1: request-bound final report from ANY earlier session refuses before the attempt and any publish obligation exist.
                if (File.Exists(Path.Combine(DiagnosticsPlugin.Output, FifaOverlayReport.FileName(request.RequestId))))
                    throw new InvalidOperationException("Request-bound FIFA overlay report already exists; choose a new RequestId.");
                _rosterWatch = new FifaRosterWatch();
                _overlayRoot = root;
                _overlaySelected = ids;
                _overlay = new FifaOverlay(); // consumes this segment even if Start fails (cleanup + report follow)
                _segments++;
                _overlay.Start(request, root, selected, write);
            }
            else
            {
                if (_overlay == null || !_overlay.Active || _overlay.RequestId != request.RequestId)
                    throw new InvalidOperationException("Stop must identify the current active FIFA overlay request.");
                _overlay.Stop("requested-stop");
            }
            write("fifa-overlay-request-result.json", new
            {
                Plugin = "FM26.VisualDiagnostics", Version = "0.10.7", SchemaVersion = 1, Session = DiagnosticsPlugin.Session,
                request.RequestId, request.Action, ActorInstanceId = request.Action == FifaOverlayPolicy.StartAction && !request.Multi ? (int?)request.ActorInstanceId : null,
                ActorInstanceIds = request.ActorInstanceIds, AllEligible = request.Action == FifaOverlayPolicy.StartAction ? (bool?)request.AllEligible : null,
                SelectedActorInstanceIds = request.Action == FifaOverlayPolicy.StartAction ? _overlaySelected : null,
                MaximumSeconds = request.Action == FifaOverlayPolicy.StartAction ? (int?)request.MaximumSeconds : null,
                TimestampUtc = DateTime.UtcNow, Accepted = true, Heuristics = FifaOverlayPolicy.Heuristics,
                HideNativePlayer = request.Action == FifaOverlayPolicy.StartAction ? (bool?)request.HideNativePlayer : null,
                Scope = "one owned FIFA-animated mesh per selected FM player root (legacy: one; ActorInstanceIds: all eligible or a list, team palettes); "
                    + "native players stay visible unless HideNativePlayer=true (then Renderer.forceRenderingOff on the selected players' renderers only, "
                    + "restored at each actor end and every stop); no simulation changes",
            });
        }
        catch (Exception exception)
        {
            write("fifa-overlay-request-result.json", new
            {
                Plugin = "FM26.VisualDiagnostics", Version = "0.10.7", SchemaVersion = 1, Session = DiagnosticsPlugin.Session,
                RequestId = request?.RequestId, Action = request?.Action, TimestampUtc = DateTime.UtcNow, Accepted = false, Error = exception.Message,
            });
            DiagnosticsPlugin.Logger.LogError($"[VISDIAG] FIFA_OVERLAY_COMMAND_REJECTED: {exception.Message}");
        }
        finally { File.Delete(path); }
    }
}

// Separate component: idle (null overlay) unless a start command was accepted.
public sealed class FifaOverlayBehaviour : MonoBehaviour
{
    public FifaOverlayBehaviour(IntPtr pointer) : base(pointer) { }
    public void Update() => FifaOverlayHost.CleanupTick(); // later-frame owned-handle verification after Stop
    public void LateUpdate() => FifaOverlayHost.Tick();     // pose after FM animation/root updates of this frame
    public void OnDestroy() => FifaOverlayHost.Shutdown();
}
