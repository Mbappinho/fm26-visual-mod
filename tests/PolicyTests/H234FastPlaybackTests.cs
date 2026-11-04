#nullable disable
using System.Numerics;
using FM26.VisualDiagnostics;

// H234 (0.10.6): live-015 frame 36214 - FM fast-forward published playback speed 30: WallDt 0.0388 s, MatchDt 1.1636 s, an
// ordinary 2.97 m/s jog moved the root 3.46 m in one update and the fixed 3 m per-frame teleport bound stopped the overlay
// (owned-update-failure, no restart for the rest of the match). Pure CLR tests of FifaMatchMotion with the live numbers.
internal static class H234FastPlaybackTests
{
    private static readonly List<(string Name, Action Test)> Cases = new() {
        ("normal-step-3p2m-teleport", NormalStep3p2mTeleport),
        ("paused-3p2m-teleport", Paused3p2mTeleport),
        ("fast-step-18m-teleport", FastStep18mTeleport),
        ("normal-step-2p9m-accepted", NormalStep2p9mAccepted),
        ("fast-step-live-jog-accepted", FastStepLiveJogAccepted),
        ("fast-step-17m-accepted", FastStep17mAccepted),
        ("heuristics-declare-scaled-bound", HeuristicsDeclareScaledBound),
        ("version-0106", Version0106),
        // H234-CASES
    };
    private static void Check(bool okay, string label) { if (!okay) throw new Exception("FAIL: H234 " + label); }

    public static void Run(string name = null)
    {
        int ran = 0;
        foreach (var (caseName, test) in Cases)
        {
            if (name != null && name != caseName) continue;
            test(); ran++;
            Console.WriteLine($"H234 PASS: {caseName}");
        }
        if (name != null && ran != 1) throw new Exception($"H234 unknown case {name}");
    }
    // live-015 segment numbers: 30x fast-forward step.
    private const double LiveMatchDt = 1.1636;
    // Ordinary running history at normal playback (1.75x, 60 fps: 0.0292 s match steps, 1.9 m/s) before the step under test.
    private static FifaMatchMotion Running(out double t, out Vector3 p)
    {
        var m = new FifaMatchMotion(900, Vector3.Zero); t = 900; p = Vector3.Zero;
        for (int i = 1; i <= 30; i++) { t = 900 + i * 0.0292; p = new Vector3(0, 0, (float)(i * 0.0292 * 1.9)); m.Observe(t, p); }
        return m;
    }
    private static string Error(Action action) { try { action(); return null; } catch (Exception e) { return e.GetType().Name + ": " + e.Message; } }
    private static bool IsTeleport(string error) => error != null && error.StartsWith("InvalidOperationException: Root teleport", StringComparison.Ordinal);
    private static void NormalStep3p2mTeleport()
    {
        var m = Running(out var t, out var p);
        string error = Error(() => m.Observe(t + 0.017, p + new Vector3(3.2f, 0, 0)));
        Check(IsTeleport(error), "a 3.2 m root jump in a 0.017 s match step (ordinary frame) is still a Root teleport: " + error);
    }
    private static void Paused3p2mTeleport()
    {
        var m = Running(out var t, out var p);
        string error = Error(() => m.Observe(t, p + new Vector3(3.2f, 0, 0)));
        Check(IsTeleport(error), "a 3.2 m root jump with a FROZEN match clock (dt 0) through Observe is still a Root teleport (3 m bound): " + error);
    }
    private static void FastStep18mTeleport()
    {
        var m = Running(out var t, out var p);
        string error = Error(() => m.Observe(t + LiveMatchDt, p + new Vector3(0, 0, 18f)));
        Check(IsTeleport(error), "18 m in a 1.1636 s match step (above 15 m/s x dt = 17.45 m) is still a Root teleport, not only a speed refusal: " + error);
    }
    private static void NormalStep2p9mAccepted()
    {
        var m = Running(out var t, out var p);
        string error = Error(() => m.Observe(t + 0.017, p + new Vector3(2.9f, 0, 0)));
        Check(error == null && Math.Abs(m.FrameDisplacement - 2.9) < 1e-5, "a 2.9 m root jump in a 0.017 s match step stays under the 3 m floor (accepted): " + error);
    }
    private static void FastStepLiveJogAccepted()
    {
        var m = Running(out var t, out var p);
        string error = Error(() => m.Observe(t + LiveMatchDt, p + new Vector3(0, 0, 3.58f)));
        Check(error == null, "live-015 30x fast-forward: 3.58 m in a 1.1636 s match step (3.08 m/s jog, under 15 m/s x dt) must not be a teleport: " + error);
        Check(Math.Abs(m.FrameDisplacement - 3.58) < 1e-5 && Math.Abs(m.MatchDt - LiveMatchDt) < 1e-9 && Math.Abs(m.Speed - 3.58 / LiveMatchDt) < 1e-4 && !m.Paused,
            "the fast step is observed normally (displacement, match dt, window speed)");
        string next = Error(() => m.Observe(t + 2 * LiveMatchDt, p + new Vector3(0, 0, 3.58f + 3.46f)));
        Check(next == null && Math.Abs(m.Speed - 3.46 / LiveMatchDt) < 1e-4, "the next 30x frame (3.46 m, 2.97 m/s) is accepted too: " + next);
    }
    private static void FastStep17mAccepted()
    {
        var m = Running(out var t, out var p);
        string error = Error(() => m.Observe(t + LiveMatchDt, p + new Vector3(0, 0, 17f)));
        Check(error == null && Math.Abs(m.FrameDisplacement - 17) < 1e-4, "17 m in a 1.1636 s match step stays under 15 m/s x dt = 17.45 m (bound uses the 15 m/s plausible speed): " + error);
    }
    private static void HeuristicsDeclareScaledBound()
    {
        var h = FifaOverlayPolicy.Heuristics;
        Check(h.Any(x => x.Contains("single-frame horizontal root jump > max(3 m, 15 m/s x MATCH dt)", StringComparison.Ordinal)),
            "the report heuristics declare the per-frame jump bound scaled with match dt");
        Check(!h.Any(x => x.Contains("> 3 m teleport", StringComparison.Ordinal) || x.Contains("root jump > 3 m,", StringComparison.Ordinal)),
            "no report heuristic still declares the fixed 3 m running-clock teleport bound");
    }
    private static void Version0106()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !File.Exists(Path.Combine(dir.FullName, "FM26.VisualDiagnostics.csproj"))) dir = dir.Parent;
        Check(dir != null && File.ReadAllText(Path.Combine(dir.FullName, "FM26.VisualDiagnostics.csproj")).Contains("<Version>0.10.7</Version>", StringComparison.Ordinal),
            "assembly version must be 0.10.7 (H238 supersedes; per-frame teleport bound scaled with match dt)");
    }
    // H234-HELPERS
}
