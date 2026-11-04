using System.Reflection;

internal static class VisualPolicyTests
{
    public static void Run()
    {
        var type = Assembly.GetExecutingAssembly().GetType("FM26.VisualDiagnostics.VisualPolicy");
        if (type == null) throw new Exception("FAIL: visual target selection policy is missing.");
        var eligible = type.GetMethod("IsEligible")!;
        var cases = new (bool Active, bool Substitute, bool Goalkeeper, bool Medic, int PlayerId, int Anchors, bool Expected)[]
        {
            (true, false, false, false, 123, 15, true),
            (false, false, false, false, 123, 15, false),
            (true, true, false, false, 123, 15, false),
            (true, false, true, false, 123, 15, false),
            (true, false, false, true, 123, 15, false),
            (true, false, false, false, 0, 15, false),
            (true, false, false, false, 123, 0, false),
        };
        foreach (var item in cases)
        {
            var actual = (bool)eligible.Invoke(null, new object[] { item.Active, item.Substitute, item.Goalkeeper, item.Medic, item.PlayerId, item.Anchors })!;
            if (actual != item.Expected) throw new Exception($"Eligibility policy failed: {item}");
        }
        var select = type.GetMethod("SelectTarget")!;
        foreach (var item in new[]
        {
            (Requested: 101, Ids: new[] { 101, 102 }, Expected: 101),
            (Requested: 102, Ids: new[] { 101, 102 }, Expected: 102),
            (Requested: 103, Ids: new[] { 101, 102 }, Expected: 0),
            (Requested: 0, Ids: new[] { 101, 102 }, Expected: 0),
            (Requested: 101, Ids: new[] { 101, 101 }, Expected: 0),
            (Requested: -101, Ids: new[] { -101, -102 }, Expected: -101),
        })
        {
            var actual = (int)select.Invoke(null, new object[] { item.Requested, item.Ids })!;
            if (actual != item.Expected) throw new Exception("Target must resolve to exactly one eligible instance.");
        }
        var shirt = type.GetMethod("HasValidShirtNumber");
        if (shirt == null) throw new Exception("FAIL: officials without a positive shirt number must be excluded.");
        foreach (var item in new[] { (Number: -1, Expected: false), (Number: 0, Expected: false), (Number: 22, Expected: true) })
            if ((bool)shirt.Invoke(null, new object[] { item.Number })! != item.Expected) throw new Exception("Invalid shirt eligibility.");
        Console.WriteLine($"PASS: {cases.Length} eligibility cases, exact-instance selection and official exclusion (including negative Unity IDs).");
    }
}
