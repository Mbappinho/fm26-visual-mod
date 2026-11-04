using FM26.VisualDiagnostics;

internal static class H182bAutoStabilityTests
{
    private static void Check(bool condition, string message)
    {
        if (!condition) throw new Exception("FAIL: H182b " + message);
    }

    public static void Run(string? name = null)
    {
        if (name == null || name == "stable-first")
        {
            foreach (string? token in new string?[] { "A", "", null })
            {
                Check(!new FifaAutoActivationStability().Observe(true, token!), "first observation is not stable");
                Check(!new FifaAutoActivationStability().Observe(false, token!), "first disabled observation is not stable");
            }
            Console.WriteLine("H182b PASS: stable-first characterization");
        }
        if (name == null || name == "stable-repeat")
        {
            foreach (string? token in new string?[] { "A", "", null })
            {
                var gate = new FifaAutoActivationStability();
                Check(!gate.Observe(true, token!), "first repeated-token poll is not stable");
                string? equalToken = token == null ? null : new string(token.ToCharArray());
                Check(gate.Observe(true, equalToken!), "second identical poll must be stable");
                for (int i = 0; i < 8; i++)
                    Check(gate.Observe(true, token!), "further identical polls stay stable");
            }
            Console.WriteLine("H182b PASS: stable-repeat");
        }
        if (name == null || name == "stable-change")
        {
            foreach (var pair in new (string? first, string? second)[]
                     { ("A", "B"), ("A", "a"), (null, ""), ("", null), (" A", "A") })
            {
                var gate = new FifaAutoActivationStability();
                Check(!gate.Observe(true, pair.first!), "first before change is not stable");
                Check(!gate.Observe(true, pair.second!), "changed candidate must restart stability");
                Check(gate.Observe(true, pair.second!), "second changed-candidate poll is stable");
                Check(!gate.Observe(true, pair.first!), "change after readiness restarts stability");
                Check(gate.Observe(true, pair.first!), "returning candidate requires its own second poll");
            }
            Console.WriteLine("H182b PASS: stable-change");
        }
        if (name == null || name == "stable-disable")
        {
            foreach (string? token in new string?[] { "A", "", null })
            foreach (int priorPolls in new[] { 1, 2, 5 })
            {
                var gate = new FifaAutoActivationStability();
                for (int i = 0; i < priorPolls; i++) gate.Observe(true, token!);
                Check(!gate.Observe(false, token!), "disabled poll must clear stability");
                Check(!gate.Observe(false, token!), "repeated disabled poll is not stable");
                Check(!gate.Observe(true, token!), "first enabled poll after disable is not stable");
                Check(gate.Observe(true, token!), "second enabled poll after disable is stable");
                Check(gate.Observe(true, token!), "readiness is not a one-shot latch");
            }
            Console.WriteLine("H182b PASS: stable-disable");
        }
        if (name != null && name != "stable-first" && name != "stable-repeat" && name != "stable-change" && name != "stable-disable")
            throw new Exception("Unknown H182b case: " + name);
    }
}
