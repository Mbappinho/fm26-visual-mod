#nullable disable
using FM26.VisualDiagnostics;

internal static class H182dAutoAttemptTests
{
    private static readonly List<(string Name, Action Test)> Cases = new()
    {
        ("attempt-readiness", ReadinessPredicate),
        ("attempt-consumption", ConsumedImmediately),
        ("attempt-session", BoundSession),
        ("attempt-char-tokens", CharacterizeTokens),
        ("attempt-char-lifetime", CharacterizeLifetime),
        ("attempt-char-outcomes", CharacterizeCallerOutcomes),
    };
    public static void Run(string name = null)
    {
        int ran = 0;
        foreach (var c in Cases)
        {
            if (name != null && name != c.Name) continue;
            c.Test(); ran++; Console.WriteLine("H182d PASS: " + c.Name);
        }
        if (name != null && ran != 1) throw new Exception("Unknown H182d case " + name);
    }
    private static void Check(bool condition, string message)
    {
        if (!condition) throw new Exception("FAIL: H182d " + message);
    }
    // Post-cycle characterization only: no production changes accompany these tests.
    private static void CharacterizeTokens()
    {
        foreach (string session in new string[] { null, "", "Session-A", " session-A " })
        {
            var gate = new FifaAutoActivationAttempt(session);
            string mismatch = session == null ? "" : null;
            Check(!gate.TryReserve(mismatch, true), "distinct null/empty/text session tokens must not match");
            Check(!gate.TryReserve(session, false), "matching token while unready must not consume");
            // Equal contents need not be the same CLR string instance.
            string equal = session == null ? null : new string(session.ToCharArray());
            Check(gate.TryReserve(equal, true), "equal ordinal token must reserve");
            Check(!gate.TryReserve(equal, true), "opaque token reservation must be lifetime-bounded");
        }
        var ordinal = new FifaAutoActivationAttempt("Session-A");
        Check(!ordinal.TryReserve("session-A", true), "session equality must be case-sensitive");
        Check(!ordinal.TryReserve("Session-A ", true), "session equality must preserve whitespace");
        Check(ordinal.TryReserve("Session-A", true), "ordinal mismatches must not consume");
    }
    private static void CharacterizeLifetime()
    {
        var gate = new FifaAutoActivationAttempt("session-A");
        var stable = new FifaAutoActivationStability();
        Check(!stable.Observe(true, "inventory-A"), "first candidate observation is not stable");
        Check(!gate.TryReserve("session-A", false), "initial not-ready must not consume");
        Check(stable.Observe(true, "inventory-A"), "second observation is stable");
        Check(gate.TryReserve("session-A", true), "stable caller can reserve once");
        // Synthetic pause/replay/config-disable transition: only copied readiness enters the gate.
        Check(!gate.TryReserve("session-A", false), "not-ready after consumption must refuse");
        Check(!stable.Observe(false, "inventory-A"), "disabled stability resets separately");
        Check(!gate.TryReserve("session-B", true), "session changes cannot reopen consumed instance");
        Check(!stable.Observe(true, "inventory-B"), "new inventory starts separate stability window");
        Check(stable.Observe(true, "inventory-B"), "new inventory can regain stability");
        Check(!gate.TryReserve("session-A", true), "restored readiness and changed inventory cannot reopen attempt");
        for (int i = 0; i < 100; i++)
            Check(!gate.TryReserve("session-A", (i % 2) == 0), "repeated readiness transitions cannot reopen attempt");
    }
    private static void CharacterizeCallerOutcomes()
    {
        // Entirely synthetic caller outcomes, no actual submission or I/O.
        foreach (string outcome in new[] { "accepted", "refused", "exception" })
        {
            var gate = new FifaAutoActivationAttempt("session-A");
            Check(gate.TryReserve("session-A", true), "outcome fixture must reserve first");
            Check(!gate.TryReserve("session-A", true), "reservation is consumed before caller outcome");
            try
            {
                if (outcome == "exception") throw new InvalidOperationException("synthetic submission failure");
                bool accepted = outcome == "accepted";
                Check(accepted == (outcome == "accepted"), "synthetic caller outcome fixture");
            }
            catch (InvalidOperationException) { }
            Check(!gate.TryReserve("session-A", true), "caller outcome cannot reopen reservation");
        }
    }
    private static void BoundSession()
    {
        var gate = new FifaAutoActivationAttempt("session-A");
        Check(!gate.TryReserve("session-B", true), "different session must be refused without consuming");
        Check(gate.TryReserve("session-A", true), "matching session must still reserve after mismatch");
    }
    private static void ConsumedImmediately()
    {
        var gate = new FifaAutoActivationAttempt("session-A");
        Check(gate.TryReserve("session-A", true), "consumption setup reservation must succeed");
        // No submission or outcome has occurred between these calls.
        Check(!gate.TryReserve("session-A", true), "repeat ready reservation must be refused before any outcome");
    }
    private static void ReadinessPredicate()
    {
        var gate = new FifaAutoActivationAttempt("session-A");
        Check(!gate.TryReserve("session-A", false), "not-ready observation must remain refused");
        Check(gate.TryReserve("session-A", true), "first ready same-session reservation must succeed");
    }
}
