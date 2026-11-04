#nullable disable
using FM26.VisualDiagnostics;

// H211 (0.10.5): after a roster-changed stop, the AUTO host waited one poll for the next-segment slot and two more stable
// polls (live-011/013/014: stop -> next start 4.06 s). The roster watch already confirmed the new complete selection on two
// consecutive polls; that confirmed selection seeds the next segment's coordinator so the FIRST post-cleanup poll whose fresh,
// otherwise ready observation resolves to the same selection proposes. Pure tests with synthetic copied inputs.
internal static class H211FastRestartTests
{
    private static readonly List<(string Name, Action Test)> Cases = new() {
        ("watch-confirms-selection", WatchConfirmsSelection),
        ("watch-differing-pair-no-seed", WatchDifferingPairNoSeed),
        ("coordinator-seed-first-poll", CoordinatorSeedFirstPoll),
        ("coordinator-seed-mismatch-two-polls", CoordinatorSeedMismatchTwoPolls),
        ("coordinator-seed-one-shot", CoordinatorSeedOneShot),
        ("version-0105", Version0105),
        ("seed-first-host-poll-only", SeedFirstHostPollOnly),
        ("watch-incomplete-no-stale-confirm", WatchIncompleteNoStaleConfirm),
        ("seed-cleared-on-disabled-rotation-poll", SeedClearedOnDisabledRotationPoll),
        ("coordinator-seed-report-collision", CoordinatorSeedReportCollision),
        ("coordinator-seed-identity-current", CoordinatorSeedIdentityCurrent),
        // H211-CASES
    };
    private static void Check(bool okay, string label) { if (!okay) throw new Exception("FAIL: H211 " + label); }

    public static void Run(string name = null)
    {
        int ran = 0;
        foreach (var (caseName, test) in Cases)
        {
            if (name != null && name != caseName) continue;
            test(); ran++;
            Console.WriteLine($"H211 PASS: {caseName}");
        }
        if (name != null && ran != 1) throw new Exception($"H211 unknown case {name}");
    }

    // Complete 22-actor inventory (ids 200..221, keepers at 200/211); `swaps` replaces actor ids (substitutions).
    private static List<(int ActorInstanceId, int TeamId, bool Eligible, bool FifaEligible, bool Goalkeeper)> Inventory(params (int Index, int Id)[] swaps)
    {
        var players = new List<(int, int, bool, bool, bool)>();
        for (int i = 0; i < 22; i++)
        {
            bool keeper = i % 11 == 0; int id = 200 + i;
            foreach (var s in swaps) if (s.Index == i) id = s.Id;
            players.Add((id, i / 11, !keeper, true, keeper));
        }
        return players;
    }
    private static int[] Selection(IReadOnlyList<(int ActorInstanceId, int TeamId, bool Eligible, bool FifaEligible, bool Goalkeeper)> inventory)
        => FifaActorSelection.ResolveFifa(new FifaOverlayRequest { AllEligible = true }, inventory);

    // Otherwise ready copied observation (H209 Ready shape) for an inventory, players visible unless stated.
    private static FifaAutoPollObservation Ready(IReadOnlyList<(int ActorInstanceId, int TeamId, bool Eligible, bool FifaEligible, bool Goalkeeper)> players, bool visible = true)
    {
        var actors = players.Select((p, i) => new FifaAutoActorIdentity(p.ActorInstanceId, 1000 + i, p.TeamId, p.Goalkeeper, 10200 + i, new[] { 700 + i })).ToArray();
        var o = new FifaAutoPollObservation
        {
            Enabled = true, PluginSession = "synthetic-session", SceneName = "MatchPlayback", RootPresent = true, RootActive = true,
            RenderersReady = true, IdentitySnapshotCurrent = true, RenderersVisible = visible, Players = players,
            Identity = new FifaAutoCandidateIdentity("synthetic-session", "MatchPlayback", 17, 100, 101, actors),
        };
        return FifaAutoHost.Prepare(o, true, false, false);
    }

    private static void WatchConfirmsSelection()
    {
        var w = new FifaRosterWatch(); var running = Selection(Inventory()); var substituted = Inventory((5, 900));
        Check(!w.Observe(running, substituted) && w.Confirmed == null, "one changed poll stops nothing and confirms nothing");
        Check(w.Observe(running, substituted), "two changed complete polls still stop (H191 contract)");
        Check(w.Confirmed != null && w.Confirmed.OrderBy(id => id).SequenceEqual(Selection(substituted).OrderBy(id => id)),
            "the stopping pair confirms the new complete selection");
    }
    private static void WatchDifferingPairNoSeed()
    {
        var w = new FifaRosterWatch(); var running = Selection(Inventory());
        w.Observe(running, Inventory((5, 900)));
        Check(w.Observe(running, Inventory((5, 901))), "two changed complete polls stop even when the new selections differ (H191 contract)");
        Check(w.Confirmed == null, "two differing changed selections confirm no seed");
    }
    private static void CoordinatorSeedFirstPoll()
    {
        var substituted = Inventory((5, 900));
        var c = new FifaAutoPollCoordinator("synthetic-session"); c.Seed(Selection(substituted));
        Check(c.Poll(Ready(substituted)) == FifaAutoPollDecision.PartialProposal,
            "the first ready visible poll matching the roster-confirmed selection proposes");
    }
    private static void CoordinatorSeedMismatchTwoPolls()
    {
        var c = new FifaAutoPollCoordinator("synthetic-session"); c.Seed(Selection(Inventory((5, 900))));
        var other = Inventory((5, 901));
        Check(c.Poll(Ready(other)) == FifaAutoPollDecision.NotReady,
            "a fresh observation resolving to another selection than the confirmed one never proposes on its first poll");
        Check(c.Poll(Ready(other)) == FifaAutoPollDecision.PartialProposal, "a seed mismatch falls back to the normal two-poll rule");
    }
    private static void CoordinatorSeedOneShot()
    {
        var substituted = Inventory((5, 900));
        var c = new FifaAutoPollCoordinator("synthetic-session"); c.Seed(Selection(substituted));
        Check(c.Poll(Ready(substituted, visible: false)) == FifaAutoPollDecision.NotReady, "a hidden first post-cleanup poll never proposes");
        Check(c.Poll(Ready(substituted)) == FifaAutoPollDecision.NotReady,
            "the seed is spent by the first post-cleanup poll: a later matching visible poll only starts the normal streak");
        Check(c.Poll(Ready(substituted)) == FifaAutoPollDecision.PartialProposal, "then the normal two-poll rule proposes");
    }
    private static void Version0105()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !File.Exists(Path.Combine(dir.FullName, "FM26.VisualDiagnostics.csproj"))) dir = dir.Parent;
        Check(dir != null && File.ReadAllText(Path.Combine(dir.FullName, "FM26.VisualDiagnostics.csproj")).Contains("<Version>0.10.7</Version>", StringComparison.Ordinal),
            "assembly version must be 0.10.7 (H238 supersedes; H234 supersedes; fast AUTO restart after a roster-changed stop)");
    }
    private static void SeedFirstHostPollOnly()
    {
        var config = System.Text.Encoding.UTF8.GetBytes("{\"Enabled\":true}");
        var running = Inventory(); var substituted = Inventory((5, 900));
        var host = new FifaAutoHost("synthetic-session");
        host.Step(config, true, true, null, () => Ready(running), true, false, false);
        Check(host.Step(config, true, true, null, () => Ready(running), true, false, false) != null, "setup: AUTO segment 1 submitted after two polls");
        string first = host.CurrentRequestId;
        host.SeedNextSegment(Selection(substituted)); // roster-changed stop of segment 1
        Check(host.Step(config, false, true, first, () => throw new Exception("no capture before matchLoaded"), true, false, false) == null,
            "setup: the rotation poll without a loaded match submits nothing");
        Check(host.Step(config, true, true, first, () => Ready(substituted), true, false, false) == null,
            "the seed is only valid for the first host poll after the rotation; a later matching poll needs the normal two polls");
        Check(host.Step(config, true, true, first, () => Ready(substituted), true, false, false) != null, "then the normal two-poll rule submits");
    }
    private static void WatchIncompleteNoStaleConfirm()
    {
        // H214 (H213 NB1): an incomplete inventory resets the stop count but not the previous changed selection.
        var w = new FifaRosterWatch(); var running = Selection(Inventory());
        var a = Inventory((5, 900)); var b = Inventory((5, 901));
        w.Observe(running, a);
        w.Observe(running, a.Take(21).ToList());
        w.Observe(running, a);
        Check(w.Observe(running, b), "setup: the second consecutive changed complete poll stops the segment");
        Check(w.Confirmed == null, "H214 A, incomplete, A, B never confirms the stale A (the stopping pair A,B differs)");
    }
    private static void SeedClearedOnDisabledRotationPoll()
    {
        // H214 (H213 NB3): the rotation poll skipped for a disabled or invalid configuration also consumes the seed.
        var config = System.Text.Encoding.UTF8.GetBytes("{\"Enabled\":true}");
        foreach (var skipped in new[] { "{\"Enabled\":false}", "not json" })
        {
            var running = Inventory(); var substituted = Inventory((5, 900));
            var host = new FifaAutoHost("synthetic-session");
            host.Step(config, true, true, null, () => Ready(running), true, false, false);
            Check(host.Step(config, true, true, null, () => Ready(running), true, false, false) != null, "setup: AUTO segment 1 submitted after two polls");
            string first = host.CurrentRequestId;
            host.SeedNextSegment(Selection(substituted)); // roster-changed stop of segment 1
            Check(host.Step(System.Text.Encoding.UTF8.GetBytes(skipped), true, true, first, () => throw new Exception("no capture while AUTO is disabled"), true, false, false) == null,
                "setup: the rotation poll with a disabled/invalid configuration submits nothing");
            Check(host.Step(config, true, true, first, () => Ready(substituted), true, false, false) == null,
                "H214 a rotation poll skipped for configuration " + skipped + " clears the seed; the next matching poll needs the normal two polls");
            Check(host.Step(config, true, true, first, () => Ready(substituted), true, false, false) != null, "then the normal two-poll rule submits");
        }
    }
    private static void CoordinatorSeedReportCollision()
    {
        // H214 (H213 NB2): the seeded first poll reuses the whole readiness, including the next AUTO report collision.
        var substituted = Inventory((5, 900));
        var c = new FifaAutoPollCoordinator("synthetic-session"); c.Seed(Selection(substituted));
        var o = Ready(substituted); o.RequestReportExists = true;
        Check(c.Poll(o) == FifaAutoPollDecision.NotReady, "H214 a seeded first poll never proposes while the next AUTO report already exists");
    }
    private static void CoordinatorSeedIdentityCurrent()
    {
        // H214 (H213 NB2): the seeded first poll also requires a current copied identity snapshot.
        var substituted = Inventory((5, 900));
        var c = new FifaAutoPollCoordinator("synthetic-session"); c.Seed(Selection(substituted));
        var o = Ready(substituted); o.IdentitySnapshotCurrent = false;
        Check(c.Poll(o) == FifaAutoPollDecision.NotReady, "H214 a seeded first poll never proposes on a stale identity snapshot");
    }
    // H211-METHODS
}
