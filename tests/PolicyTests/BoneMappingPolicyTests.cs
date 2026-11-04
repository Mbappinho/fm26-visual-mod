using System.Reflection;

internal static class BoneMappingPolicyTests
{
    public static void Run()
    {
        var type = Assembly.GetExecutingAssembly().GetType("FM26.VisualDiagnostics.BoneMappingPolicy");
        if (type == null) throw new Exception("FAIL: ordered bone-reference policy is missing.");
        var find = type.GetMethod("FindIndices")!;
        var ids = new int?[] {null, -42, -99, -42, 17};
        var matches = (int[])find.Invoke(null, new object[] {ids, (int?)-42})!;
        if (!matches.SequenceEqual(new[] {1,3})) throw new Exception("Duplicate bone references were compacted or normalized.");
        var missing = (int[])find.Invoke(null, new object[] {ids, (int?)300})!;
        if (missing.Length != 0) throw new Exception("Missing parent must not be invented.");
        var nullMatches = (int[])find.Invoke(null, new object?[] {ids, null})!;
        if (nullMatches.Length != 0) throw new Exception("Null entries are holes, not bone identities.");
        var bound = type.GetMethod("ValidateLength")!;
        bound.Invoke(null, new object[] {126});
        foreach (var length in new[] {-1, 257})
        {
            try { bound.Invoke(null, new object[] {length}); throw new Exception("Unbounded bone table accepted."); }
            catch (TargetInvocationException error) when (error.InnerException is ArgumentException) { }
        }
        Console.WriteLine("PASS: bone table bounds, literal negative IDs, null holes and duplicate/absent parent indices.");
    }
}
