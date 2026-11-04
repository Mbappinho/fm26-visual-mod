using System.Reflection;

internal static class PoseOwnerPolicyTests
{
    public static void Run()
    {
        var type = Assembly.GetExecutingAssembly().GetType("FM26.VisualDiagnostics.PoseOwnerPolicy");
        if (type == null) throw new Exception("FAIL: fail-closed pose-owner metadata policy is missing.");
        var owner = type.GetMethod("IsUsable")!;
        var cases = new (bool Initialized, bool ShuttingDown, bool BuffersCreated, int Entities, int Bones, int Worlds, int Values, bool Expected)[]
        {
            (true, false, true, 41, 86, 41, 100, true),
            (false, false, true, 41, 86, 41, 100, false),
            (true, true, true, 41, 86, 41, 100, false),
            (true, false, false, 41, 86, 41, 100, false),
            (true, false, true, 0, 0, 0, 0, false),
            (true, false, true, 41, 86, 1, 100, false),
        };
        foreach (var item in cases)
            if ((bool)owner.Invoke(null, new object[] { item.Initialized, item.ShuttingDown, item.BuffersCreated, item.Entities, item.Bones, item.Worlds, item.Values })! != item.Expected)
                throw new Exception("Unsafe owner metadata accepted.");
        var mapping = type.GetMethod("HasMatchingIdentity")!;
        foreach (var item in new[]
        {
            (Index: 36, Length: 41, Character: (ushort)100, Table: (ushort)100, SameService: true, Expected: true),
            (Index: 41, Length: 41, Character: (ushort)100, Table: (ushort)100, SameService: true, Expected: false),
            (Index: -1, Length: 41, Character: (ushort)100, Table: (ushort)100, SameService: true, Expected: false),
            (Index: 36, Length: 41, Character: ushort.MaxValue, Table: ushort.MaxValue, SameService: true, Expected: false),
            (Index: 36, Length: 41, Character: (ushort)100, Table: (ushort)101, SameService: true, Expected: false),
            (Index: 36, Length: 41, Character: (ushort)100, Table: (ushort)100, SameService: false, Expected: false),
        })
            if ((bool)mapping.Invoke(null, new object[] { item.Index, item.Length, item.Character, item.Table, item.SameService })! != item.Expected)
                throw new Exception("Unverified entity/CharacterID mapping accepted.");
        var nativeIdentity = type.GetMethod("HasMatchingNativeService");
        if (nativeIdentity == null) throw new Exception("FAIL: native service identity policy is missing.");
        foreach (var item in new[]
        {
            (LeftKnown: true, RightKnown: true, Left: 123L, Right: 123L, Expected: true),
            (LeftKnown: true, RightKnown: true, Left: 123L, Right: 456L, Expected: false),
            (LeftKnown: true, RightKnown: true, Left: 0L, Right: 0L, Expected: false),
            (LeftKnown: false, RightKnown: true, Left: 123L, Right: 123L, Expected: false),
            (LeftKnown: true, RightKnown: false, Left: 123L, Right: 123L, Expected: false),
        })
            if ((bool)nativeIdentity.Invoke(null, new object[] { item.LeftKnown, item.RightKnown, item.Left, item.Right })! != item.Expected)
                throw new Exception("Unverified native service identity accepted.");
        Console.WriteLine("PASS: pose-owner lifecycle, array bounds, sentinel IDs and managed/native identity checks.");
    }
}
