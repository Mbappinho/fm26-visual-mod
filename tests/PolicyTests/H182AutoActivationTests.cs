using FM26.VisualDiagnostics;

internal static class H182AutoActivationTests
{
    private static void Check(bool condition, string message)
    {
        if (!condition) throw new Exception("FAIL: H182 " + message);
    }

    public static void Run(string? name = null)
    {
        if (name != null && name.StartsWith("stable-", StringComparison.Ordinal))
        {
            H182bAutoStabilityTests.Run(name);
            return;
        }
        if (name == null) H182bAutoStabilityTests.Run();
        if (name == null || name == "default")
        {
            Check(!FifaAutoActivationDecision.IsOptInReady(), "omitted opt-in is not ready");
            Check(!FifaAutoActivationDecision.IsOptInReady(false), "disabled opt-in is not ready");
            Console.WriteLine("H182 PASS: default and disabled opt-in characterization");
        }
        if (name == null || name == "enabled")
        {
            Check(FifaAutoActivationDecision.IsOptInReady(true), "explicit opt-in must produce opt-in readiness");
            Console.WriteLine("H182 PASS: explicit opt-in readiness (not start authorization)");
        }
        if (name != null && name != "default" && name != "enabled")
            throw new Exception("Unknown H182 case: " + name);
    }
}
