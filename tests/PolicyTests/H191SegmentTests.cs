#nullable disable
using FM26.VisualDiagnostics;

// H191: executable tests of overlay segments (synthetic copied inputs only).
internal static class H191SegmentTests
{
    private static readonly List<(string Name, Action Test)> Cases = new() {  ("restart-after-clean-end", RestartAfterCleanEnd),  ("no-restart-after-failure", NoRestartAfterFailure),  ("no-restart-unverified", NoRestartUnverified),  ("no-restart-unpublished", NoRestartUnpublished),  ("segment-cap", SegmentCap),  ("roster-change-detected", RosterChangeDetected),  ("roster-change-two-polls", RosterChangeTwoPolls),  ("roster-incomplete-ignored", RosterIncompleteIgnored), };

    public static void Run(string name = null)
    {
        foreach (var (caseName, test) in Cases)
        {
            if (name != null && name != caseName) continue;
            test();
            Console.WriteLine($"H191 PASS: {caseName}");
        }
        if (name != null && !Cases.Any(c => c.Name == name)) throw new Exception($"H191 unknown case {name}");
    }

    private static void RestartAfterCleanEnd()
    {
        Check(FifaOverlaySegments.CanStart(false, false, false, false, null, 0), "first segment of a session may start");
        Check(FifaOverlaySegments.CanStart(true, true, true, true, null, 1), "a new segment may start after a clean, verified and published end");
    }

    private static void NoRestartAfterFailure()
    {
        Check(!FifaOverlaySegments.CanStart(true, true, true, true, "Implausible horizontal speed.", 1), "a failed segment must never be restarted in the same session");
    }

    private static void NoRestartUnverified()
    {
        Check(!FifaOverlaySegments.CanStart(true, true, false, true, null, 1), "an unverified cleanup must block any new segment");
    }

    private static void NoRestartUnpublished()
    {
        Check(!FifaOverlaySegments.CanStart(true, true, true, false, null, 1), "an unpublished report must block any new segment");
    }

    private static void SegmentCap()
    {
        Check(FifaOverlaySegments.CanStart(true, true, true, true, null, FifaOverlaySegments.MaximumSegments - 1), "below the cap a clean segment may restart");
        Check(!FifaOverlaySegments.CanStart(true, true, true, true, null, FifaOverlaySegments.MaximumSegments), "at most MaximumSegments segments per session");
    }

    // SYNTHETIC complete inventory: 22 starters (11/11, one keeper each) plus optional extra entries.
    private static List<(int, int, bool, bool, bool)> Roster(int firstId, params (int, int, bool, bool, bool)[] extra)
    {
        var players = new List<(int, int, bool, bool, bool)>();
        for (int i = 0; i < 22; i++) { bool keeper = i % 11 == 0; players.Add((firstId + i, i / 11, !keeper, true, keeper)); }
        players.AddRange(extra);
        return players;
    }

    private static int[] Ids(List<(int, int, bool, bool, bool)> players, int count) => players.Take(count).Select(p => p.Item1).ToArray();

    private static void RosterChangeDetected()
    {
        var selected = Ids(Roster(100), 22);
        var substituted = Roster(100); substituted[5] = (900, 0, true, true, false); // outfield substitute entered for actor 105
        var watch = new FifaRosterWatch();
        watch.Observe(selected, substituted);
        Check(watch.Observe(selected, substituted), "a complete roster differing from the running selection must be reported as a roster change");
    }

    private static void RosterChangeTwoPolls()
    {
        var selected = Ids(Roster(100), 22);
        var substituted = Roster(100); substituted[5] = (900, 0, true, true, false);
        var watch = new FifaRosterWatch();
        Check(!watch.Observe(selected, substituted), "a roster change seen on one poll only must not be reported yet");
        Check(watch.Observe(selected, substituted), "a roster change seen on two consecutive polls must be reported");
        var flicker = new FifaRosterWatch();
        flicker.Observe(selected, substituted);
        flicker.Observe(selected, Roster(100));
        Check(!flicker.Observe(selected, substituted), "an unchanged poll in between must restart the confirmation");
    }

    private static void RosterIncompleteIgnored()
    {
        var selected = Ids(Roster(100), 22);
        var empty = new List<(int, int, bool, bool, bool)>();          // camera cut: every player inactive
        var redCard = Roster(100); redCard.RemoveAt(7);                  // 21 players
        foreach (var (inventory, what) in new[] { (empty, "an empty inventory (cut/replay)"), (redCard, "an incomplete roster (red card, injury)") })
        {
            var watch = new FifaRosterWatch();
            bool reported = false; Exception error = null;
            try { for (int i = 0; i < 3; i++) reported |= watch.Observe(selected, inventory); }
            catch (Exception exception) { error = exception; }
            Check(error == null && !reported, what + " must not be a roster change and must not throw");
        }
    }

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new Exception("FAIL: H191 " + message);
    }
}
