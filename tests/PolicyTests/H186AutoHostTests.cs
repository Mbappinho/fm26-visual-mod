#nullable disable
using FM26.VisualDiagnostics;

// H186: executable tests of the pure auto host step. Inputs are SYNTHETIC copied observations,
// not native evidence. Cases are added one behavior per TDD cycle.
internal static class H186AutoHostTests
{
    private static readonly List<(string Name, Action Test)> Cases = new() { ("no-capture-after-submit", NoCaptureAfterSubmit), ("match-not-loaded", MatchNotLoaded), ("version-090", Version090), ("step-invalid-config", StepInvalidConfig), ("step-disabled", StepDisabled), ("step-enabled", StepEnabled), ("one-attempt", OneAttempt), ("command-content", CommandContent), ("request-id", RequestIdForSession), ("host-proposes", HostProposes), ("prepare-pack", PreparePack), ("prepare-manual", PrepareManual), ("prepare-report", PrepareReport), ("wait-segment-slot", WaitSegmentSlot), ("next-segment", NextSegment),  ("rotate-only-own-segment", RotateOnlyOwnSegment),  ("segment-request-id", SegmentRequestId),  ("report-of-next-segment", ReportOfNextSegment), };

    public static void Run(string name = null)
    {
        foreach (var (caseName, test) in Cases)
        {
            if (name != null && name != caseName) continue;
            test();
            Console.WriteLine($"H186 PASS: {caseName}");
        }
        if (name != null && !Cases.Any(c => c.Name == name)) throw new Exception($"H186 unknown case {name}");
    }

    private static void PreparePack()
    {
        Check(FifaAutoHost.Prepare(new FifaAutoPollObservation(), true, true, true).PackVerified, "present pack header must map to PackVerified");
        Check(!FifaAutoHost.Prepare(new FifaAutoPollObservation(), false, true, true).PackVerified, "absent pack header must keep PackVerified false");
    }

    private static void PrepareManual()
    {
        Check(!FifaAutoHost.Prepare(new FifaAutoPollObservation(), true, false, true).ManualCommandPending, "no command file must clear ManualCommandPending");
        Check(FifaAutoHost.Prepare(new FifaAutoPollObservation(), true, true, true).ManualCommandPending, "existing command file must keep ManualCommandPending");
    }

    private static void PrepareReport()
    {
        Check(!FifaAutoHost.Prepare(new FifaAutoPollObservation(), true, false, false).RequestReportExists, "absent auto report must clear RequestReportExists");
        Check(FifaAutoHost.Prepare(new FifaAutoPollObservation(), true, false, true).RequestReportExists, "existing auto report must keep RequestReportExists");
    }

    // SYNTHETIC ready observation: complete 22 (11/11, one keeper each), matching copied identity, all file facts clear.
    private static FifaAutoPollObservation Ready(string session)
    {
        var players = new List<(int, int, bool, bool, bool)>();
        var actors = new List<FifaAutoActorIdentity>();
        for (int i = 0; i < 22; i++)
        {
            bool keeper = i % 11 == 0;
            players.Add((200 + i, i / 11, !keeper, true, keeper));
            actors.Add(new FifaAutoActorIdentity(200 + i, 1000 + i, i / 11, keeper, 10200 + i, new[] { 700 + i }));
        }
        var o = new FifaAutoPollObservation
        {
            Enabled = true, PluginSession = session, SceneName = "MatchPlayback", RootPresent = true, RootActive = true,
            RenderersReady = true, IdentitySnapshotCurrent = true, RenderersVisible = true, Players = players, // H209: players visible
            Identity = new FifaAutoCandidateIdentity(session, "MatchPlayback", 17, 100, 101, actors.ToArray()),
        };
        return FifaAutoHost.Prepare(o, true, false, false);
    }

    private static void HostProposes()
    {
        var host = new FifaAutoHost("synthetic-session");
        Check(host.Poll(Ready("synthetic-session")) == null, "first ready poll must not submit");
        Check(host.Poll(Ready("synthetic-session")) != null, "second consecutive ready poll must produce a command");
    }

    private static void RequestIdForSession()
    {
        Check(FifaAutoHost.RequestIdFor("20261026-195947") == "auto-20261026-195947", "auto RequestId must be \"auto-\" + plugin session");
    }

    private static void CommandContent()
    {
        var host = new FifaAutoHost("20261026-195947");
        host.Poll(Ready("20261026-195947"));
        var bytes = host.Poll(Ready("20261026-195947"));
        FifaOverlayRequest request = null;
        try
        {
            using var document = System.Text.Json.JsonDocument.Parse(bytes);
            request = FifaOverlayPolicy.ParseRequest(document.RootElement, "20261026-195947");
        }
        catch (Exception) { }
        Check(request != null, "auto command must be accepted by the standard command parser");
        Check(request.Action == FifaOverlayPolicy.StartAction && request.RequestId == "auto-20261026-195947", "auto command must be a standard start with the session-bound RequestId");
        Check(request.AllEligible && request.MaximumSeconds == FifaOverlayPolicy.MaximumSecondsCap && request.HideNativePlayer && request.PackDirectory == null,
            "auto command must request all eligible actors, the maximum duration, native hide and the default pack");
        Check(bytes.Length <= 4096, "auto command must fit the 4096-byte command bound");
    }

    private static void OneAttempt()
    {
        var host = new FifaAutoHost("synthetic-session");
        host.Poll(Ready("synthetic-session"));
        Check(host.Poll(Ready("synthetic-session")) != null, "one-attempt precondition: first proposal submits");
        for (int i = 0; i < 4; i++) Check(host.Poll(Ready("synthetic-session")) == null, "later ready polls in the same session must never submit again");
    }

    private static void StepEnabled()
    {
        var host = new FifaAutoHost("synthetic-session");
        var config = System.Text.Encoding.UTF8.GetBytes("{\"Enabled\":true}");
        Check(host.Step(config, true, true, null, () => Ready("synthetic-session"), true, false, false) == null, "first enabled step must not submit");
        Check(host.Step(config, true, true, null, () => Ready("synthetic-session"), true, false, false) != null, "second enabled ready step must produce the command");
    }

    private static void StepDisabled()
    {
        foreach (var config in new[] { null, System.Text.Encoding.UTF8.GetBytes("{}"), System.Text.Encoding.UTF8.GetBytes("{\"Enabled\":false}") })
        {
            var host = new FifaAutoHost("synthetic-session");
            int captures = 0;
            for (int i = 0; i < 3; i++)
                Check(host.Step(config, true, true, null, () => { captures++; return Ready("synthetic-session"); }, true, false, false) == null, "absent or disabled config must never submit");
            Check(captures == 0, "absent or disabled config must not read native state");
        }
    }

    private static void StepInvalidConfig()
    {
        foreach (var text in new[] { "", "{", "[]", "{\"Enabled\":1}", "{\"Enabled\":true,\"X\":1}", "{\"Enabled\":true,\"Enabled\":true}", "{\"Enabled\":true}" + new string(' ', 4096) })
        {
            var host = new FifaAutoHost("synthetic-session");
            int captures = 0; byte[] result = null; Exception error = null;
            try
            {
                for (int i = 0; i < 3; i++)
                    result ??= host.Step(System.Text.Encoding.UTF8.GetBytes(text), true, true, null, () => { captures++; return Ready("synthetic-session"); }, true, false, false);
            }
            catch (Exception exception) { error = exception; }
            Check(error == null && result == null && captures == 0, "invalid config must be treated as disabled without throwing or reading native state: " + text.Length);
        }
    }

    private static void Version090()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !File.Exists(Path.Combine(dir.FullName, "FM26.VisualDiagnostics.csproj"))) dir = dir.Parent;
        Check(dir != null && File.ReadAllText(Path.Combine(dir.FullName, "FM26.VisualDiagnostics.csproj")).Contains("<Version>0.10.7</Version>", StringComparison.Ordinal), "assembly version must be 0.10.7 (H238 supersedes; H234 supersedes; H211 supersedes; H209 hidden-view AUTO gate)");
    }

    private static void MatchNotLoaded()
    {
        var host = new FifaAutoHost("synthetic-session");
        var config = System.Text.Encoding.UTF8.GetBytes("{\"Enabled\":true}");
        int captures = 0;
        for (int i = 0; i < 3; i++)
            Check(host.Step(config, false, true, null, () => { captures++; return Ready("synthetic-session"); }, true, false, false) == null, "no command before the match is loaded");
        Check(captures == 0, "enabled config must not read native FM match state before the match is loaded (boot crash)");
    }

    private static void NoCaptureAfterSubmit()
    {
        var host = new FifaAutoHost("synthetic-session");
        var config = System.Text.Encoding.UTF8.GetBytes("{\"Enabled\":true}");
        int captures = 0;
        Func<FifaAutoPollObservation> capture = () => { captures++; return Ready("synthetic-session"); };
        host.Step(config, true, true, null, capture, true, false, false);
        Check(host.Step(config, true, true, null, capture, true, false, false) != null, "precondition: second step submits");
        int before = captures;
        // H193: while the submitted segment runs (no restart possible) later steps must not read native state.
        for (int i = 0; i < 5; i++) host.Step(config, true, false, "auto-synthetic-session", capture, true, false, false);
        Check(captures == before, "after the single attempt is used, later steps must not read native state");
    }

    private static void WaitSegmentSlot()
    {
        var host = new FifaAutoHost("synthetic-session");
        var config = System.Text.Encoding.UTF8.GetBytes("{\"Enabled\":true}");
        for (int i = 0; i < 3; i++)
            Check(host.Step(config, true, false, null, () => Ready("synthetic-session"), true, false, false) == null, "no command while an overlay segment is running or cannot be restarted");
    }

    private static void NextSegment()
    {
        var host = new FifaAutoHost("synthetic-session");
        var config = System.Text.Encoding.UTF8.GetBytes("{\"Enabled\":true}");
        host.Step(config, true, true, null, () => Ready("synthetic-session"), true, false, false);
        Check(host.Step(config, true, true, null, () => Ready("synthetic-session"), true, false, false) != null, "precondition: first segment submitted");
        // the submitted segment ran (current overlay = our request) and ended cleanly: a later segment may follow
        host.Step(config, true, true, "auto-synthetic-session", () => Ready("synthetic-session"), true, false, false);
        Check(host.Step(config, true, true, "auto-synthetic-session", () => Ready("synthetic-session"), true, false, false) != null,
            "after our segment ended cleanly a new segment must be submitted (two stable polls)");
    }

    private static void RotateOnlyOwnSegment()
    {
        var config = System.Text.Encoding.UTF8.GetBytes("{\"Enabled\":true}");
        foreach (var last in new[] { null, "live-001" })
        {
            var host = new FifaAutoHost("synthetic-session");
            host.Step(config, true, true, null, () => Ready("synthetic-session"), true, false, false);
            Check(host.Step(config, true, true, null, () => Ready("synthetic-session"), true, false, false) != null, "precondition: first segment submitted");
            for (int i = 0; i < 4; i++)
                Check(host.Step(config, true, true, last, () => Ready("synthetic-session"), true, false, false) == null,
                    "no new segment unless the current overlay is the one we submitted (rejected or foreign command): " + (last ?? "none"));
        }
    }

    private static void SegmentRequestId()
    {
        var host = new FifaAutoHost("20261026-195947");
        var config = System.Text.Encoding.UTF8.GetBytes("{\"Enabled\":true}");
        host.Step(config, true, true, null, () => Ready("20261026-195947"), true, false, false);
        host.Step(config, true, true, null, () => Ready("20261026-195947"), true, false, false);
        Check(host.CurrentRequestId == "auto-20261026-195947", "first segment keeps the 0.9.x RequestId");
        host.Step(config, true, true, "auto-20261026-195947", () => Ready("20261026-195947"), true, false, false);
        var bytes = host.Step(config, true, true, "auto-20261026-195947", () => Ready("20261026-195947"), true, false, false);
        FifaOverlayRequest request = null;
        try
        {
            using var document = System.Text.Json.JsonDocument.Parse(bytes);
            request = FifaOverlayPolicy.ParseRequest(document.RootElement, "20261026-195947");
        }
        catch (Exception) { }
        Check(request != null && request.RequestId == "auto-20261026-195947-2" && host.CurrentRequestId == "auto-20261026-195947-2",
            "the second segment must use its own RequestId (auto-<session>-2) so its report never collides");
    }

    private static void ReportOfNextSegment()
    {
        var host = new FifaAutoHost("synthetic-session");
        var config = System.Text.Encoding.UTF8.GetBytes("{\"Enabled\":true}");
        Func<string, bool> reports = id => id == "auto-synthetic-session"; // only the first segment's report exists
        host.Step(config, true, true, null, () => Ready("synthetic-session"), true, false, id => false);
        Check(host.Step(config, true, true, null, () => Ready("synthetic-session"), true, false, id => false) != null, "precondition: first segment submitted");
        host.Step(config, true, true, "auto-synthetic-session", () => Ready("synthetic-session"), true, false, reports);
        Check(host.Step(config, true, true, "auto-synthetic-session", () => Ready("synthetic-session"), true, false, reports) != null,
            "the finished segment's own report must not block the next segment (report checked for the next RequestId)");
    }

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new Exception("FAIL: H186 " + message);
    }
}
