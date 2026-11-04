#nullable disable
using FM26.VisualDiagnostics;

// H238 (0.10.7): live-017 segment 5 stopped owned-update-failure "FIFA overlay root/scene/actor context changed." with
// FailureDetail.Stage "suspended" after a 27 s fm-view-changed suspension. 0.10.3 (H206) turns a session-context change of a
// RUNNING overlay into a normal fm-context-changed end; this suite pins the same classification on the suspended path.
internal static class H238SuspendedContextTests
{
    private static readonly List<(string Name, Action Test)> Cases = new() {
        ("no-refusal-keeps-resume", NoRefusalKeepsResume),
        ("thread-keeps-resume", ThreadKeepsResume),
        ("other-scopes-unchanged", OtherScopesUnchanged),
        ("version-0107", Version0107),
        ("resume-refusal-non-resume-unchanged", ResumeRefusalNonResumeUnchanged),
        ("resume-refusal-session-keeps-resume", ResumeRefusalSessionKeepsResume),
        // H238-CASES
    };
    private static void Check(bool okay, string label) { if (!okay) throw new Exception("FAIL: H238 " + label); }

    public static void Run(string name = null)
    {
        int ran = 0;
        foreach (var (caseName, test) in Cases)
        {
            if (name != null && name != caseName) continue;
            test(); ran++;
            Console.WriteLine($"H238 PASS: {caseName}");
        }
        if (name != null && ran != 1) throw new Exception($"H238 unknown case {name}");
    }
    private static readonly (FifaScopeKind Kind, string Reason, string Error) ResumeScope = (FifaScopeKind.Resume, null, null);
    private static void NoRefusalKeepsResume()
    {
        Check(FifaSessionTransition.ApplySuspended(ResumeScope, null) == ResumeScope, "without a session refusal on the frame a resume decision stays a resume (ordinary end of a highlight break)");
    }
    private static void ThreadKeepsResume()
    {
        // Same classification as 0.10.3 FifaSessionTransition.Apply: a thread mismatch is our own fault, never an FM transition
        // (the resume then throws the refusal and stops as owned-update-failure).
        Check(FifaSessionTransition.ApplySuspended(ResumeScope, "thread") == ResumeScope, "a thread mismatch is never fm-context-changed on the suspended path either");
    }
    private static void OtherScopesUnchanged()
    {
        Check(FifaSessionTransition.ApplySuspended((FifaScopeKind.Stay, null, null), "builder-mismatch") == (FifaScopeKind.Stay, null, null), "players still hidden: the suspension stays (0.10.3 running path also suspends a session refusal with hidden renderers)");
        var partial = (FifaScopeKind.StopSession, FifaViewSuspension.FailureReason, "Target renderers partially reactivated while suspended.");
        Check(FifaSessionTransition.ApplySuspended(partial, "builder-mismatch") == partial, "any other suspended-path failure stays a failure with its Error");
        var ended = (FifaScopeKind.StopSession, FifaViewSuspension.MatchEndedReason, (string)null);
        Check(FifaSessionTransition.ApplySuspended(ended, "root-null") == ended, "a structure end (fm-match-ended, already normal) keeps its reason");
    }
    private static void Version0107()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !File.Exists(Path.Combine(dir.FullName, "FM26.VisualDiagnostics.csproj"))) dir = dir.Parent;
        Check(dir != null && File.ReadAllText(Path.Combine(dir.FullName, "FM26.VisualDiagnostics.csproj")).Contains("<Version>0.10.7</Version>", StringComparison.Ordinal),
            "assembly version must be 0.10.7 (session-context change while suspended ends normally)");
    }
    private static void ResumeRefusalNonResumeUnchanged()
    {
        Check(FifaResumeRefusal.Apply((FifaScopeKind.Stay, null, null), true, null, "is-active") == (FifaScopeKind.Stay, null, null), "H240 players still hidden: an is-active actor stays suspended (only a resume decision is reclassified)");
        var partial = (FifaScopeKind.StopSession, FifaViewSuspension.FailureReason, "Target renderers partially reactivated while suspended.");
        Check(FifaResumeRefusal.Apply(partial, true, null, "is-active") == partial, "H240 any other suspended-path failure keeps its reason and Error");
        var limit = (FifaScopeKind.EndActor, FifaViewSuspension.LimitReason, (string)null);
        Check(FifaResumeRefusal.Apply(limit, true, null, "is-active") == limit, "H240 a per-actor suspend-limit end keeps its reason");
    }
    private static void ResumeRefusalSessionKeepsResume()
    {
        // Resume throws the frame's session refusal before any actor guard: a session predicate (thread here; any other is already
        // the H238 fm-context-changed end) is never an individual actor end, like the running path (sessionRefusal != null => no EndActor).
        Check(FifaResumeRefusal.Apply(ResumeScope, true, "thread", "is-active") == ResumeScope, "H240 under a session refusal the resume decision is never turned into an actor end");
    }
    // H238-HELPERS
}
