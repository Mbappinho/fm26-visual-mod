#nullable disable
using FM26.VisualDiagnostics;

// H156: executable lifecycle state machine of the FIFA overlay (pure CLR, extracted from FifaOverlay.cs).
internal static class FifaOverlayLifecycleTests
{
    private static int _assertions;
    private static void Check(bool okay, string label) { _assertions++; if (!okay) throw new Exception("FAIL: " + label); }
    private static void Reject(Action action, string label)
    {
        _assertions++;
        try { action(); } catch (InvalidOperationException) { return; }
        throw new Exception("FAIL: accepted " + label);
    }

    public static void Run()
    {
        StartRefusal(); StopThenVerifiedCleanup(); TimeoutStaysBlocked(); FailedPublishStaysBlocked(); ReportNoOverwrite();
        Console.WriteLine($"PASS: {_assertions} FIFA overlay lifecycle assertions (pure CLR state machine).");
    }

    // B1: an existing request-bound report refuses BEFORE any publish obligation exists.
    private static void StartRefusal()
    {
        var l = new FifaOverlayLifecycle();
        Reject(() => l.Arm(reportExists: true), "arm with existing request-bound report");
        Check(!l.Armed && !l.PublishObligation && !l.Blocked, "refused start creates no publish obligation and no block");
        Check(!l.RequestStop("start-failure", "x", 5, 50), "stop after refused arm is a no-op");
        Check(!l.ShouldPublish(9, 90, 10, true), "never publishes for a refused start");
    }

    internal static FifaOverlayLifecycle Running()
    {
        var l = new FifaOverlayLifecycle();
        l.Arm(false); l.Activate();
        return l;
    }

    // Stop: first cause wins; blocked through cleanup; strictly later frame; verified + written publication releases.
    private static void StopThenVerifiedCleanup()
    {
        var l = Running();
        Check(l.Active && l.Blocked && l.PublishObligation, "active overlay blocks other modes");
        Check(l.RequestStop("requested-stop", null, 10, 100), "first stop accepted");
        Check(!l.RequestStop("stop-time-budget", "late", 11, 110) && l.Reason == "requested-stop" && l.Error == null, "first stop cause wins");
        Check(!l.Active && l.Blocked, "cleanup pending stays blocked");
        Check(!l.ShouldPublish(10, 101, 10, true), "same-frame verification never publishes");
        Check(l.ShouldPublish(11, 105, 10, true), "later-frame verified cleanup publishes");
        l.RecordPublication(verified: true, written: true);
        Check(!l.Blocked && l.Published && l.CleanupVerified, "verified cleanup + written report releases the exclusion");
        Check(!l.ShouldPublish(12, 106, 10, true), "single publication attempt");
    }

    // B2: timeout publishes UNCONFIRMED and keeps blocking competitors for the rest of the session.
    private static void TimeoutStaysBlocked()
    {
        var l = Running();
        l.RequestStop("stop-time-budget", null, 10, 100);
        Check(!l.ShouldPublish(129, 149, 10, false), "unverified cleanup waits below 120 frames / 5 s");
        Check(l.ShouldPublish(130, 120, 10, false), "120-frame timeout publishes unverified");
        Check(Running().Stopped(10, 100).ShouldPublish(11, 150, 10, false), "5 s timeout publishes unverified");
        l.RecordPublication(verified: false, written: true);
        Check(l.Blocked && l.Published && !l.CleanupVerified, "timeout (cleanup unverified) keeps other modes blocked");
        Check(!l.ShouldPublish(500, 9000, 10, true), "no later re-certification after the single publication");
        Check(l.Blocked, "still blocked for the rest of the session");
    }

    // B2: a failed publication keeps the mode blocked even when cleanup itself was verified.
    private static void FailedPublishStaysBlocked()
    {
        var l = Running().Stopped(10, 100);
        l.RecordPublication(verified: true, written: false);
        Check(l.Blocked && !l.Published && l.PublicationFailed, "failed publication keeps other modes blocked");
        var forced = Running().Stopped(10, 100);
        forced.RecordPublication(verified: false, written: false);
        Check(forced.Blocked, "shutdown-style forced unconfirmed + failed write stays blocked");
    }

    private static FifaOverlayLifecycle Stopped(this FifaOverlayLifecycle l, int frame, long qpc)
    {
        l.RequestStop("requested-stop", null, frame, qpc);
        return l;
    }

    // B1: final report published with CreateNew + non-overwriting move; an existing report (any session) is never replaced.
    private static void ReportNoOverwrite()
    {
        var dir = Path.Combine(Path.GetTempPath(), "fifa-h156-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var target = Path.Combine(dir, "fifa-overlay-fifa-001.json");
            FifaOverlayReport.WriteNew(target, new byte[] { 1, 2, 3 }, 16);
            Check(File.ReadAllBytes(target).SequenceEqual(new byte[] { 1, 2, 3 }), "new report written");
            Reject(() => FifaOverlayReport.WriteNew(target, new byte[] { 9 }, 16), "overwrite of an existing request-bound report");
            Check(File.ReadAllBytes(target).SequenceEqual(new byte[] { 1, 2, 3 }), "existing report bytes unchanged");
            Reject(() => FifaOverlayReport.WriteNew(Path.Combine(dir, "big.json"), new byte[17], 16), "report over byte cap");
            Check(!File.Exists(Path.Combine(dir, "big.json")), "oversized report not written");
            Check(Directory.GetFiles(dir).Length == 1, "no temporary leftovers");
            Check(FifaOverlayReport.FileName("fifa-001") == "fifa-overlay-fifa-001.json", "request-bound name");
        }
        finally { Directory.Delete(dir, true); }
    }
}
