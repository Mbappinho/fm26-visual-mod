using System.Reflection;

internal static class GpuCapturePolicyTests
{
    public static void Run()
    {
        var type = Assembly.GetExecutingAssembly().GetType("FM26.VisualDiagnostics.GpuCapturePolicy");
        if (type == null) throw new Exception("FAIL: bounded GPU capture policy is missing.");
        var size = type.GetMethod("ExpectedByteCount")!;
        var valid = new object[] { 43, 126, 126, 86, true, true, true };
        if ((int)size.Invoke(null, valid)! != 173376) throw new Exception("Wrong RGBAFloat byte count.");
        foreach (var args in new object[][]
        {
            new object[] {0, 126, 126, 86, true, true, true},
            new object[] {43, 0, 126, 86, true, true, true},
            new object[] {43, 126, 125, 86, true, true, true},
            new object[] {43, 126, 126, 85, true, true, true},
            new object[] {43, 126, 126, 86, false, true, true},
            new object[] {43, 126, 126, 86, true, false, true},
            new object[] {43, 126, 126, 86, true, true, false},
            new object[] {int.MaxValue, 255, 255, int.MaxValue, true, true, true},
        })
        {
            try { size.Invoke(null, args); throw new Exception("Unsafe texture dimensions accepted."); }
            catch (TargetInvocationException error) when (error.InnerException is ArgumentException) { }
        }
        var canCopy = type.GetMethod("CanCopy")!;
        var cases = new (bool Done, bool Error, bool Context, int Frames, int Layers, int Bytes, int Expected, bool Result)[]
        {
            (true, false, true, 1, 1, 16, 16, true),
            (false, false, true, 1, 1, 16, 16, false),
            (true, true, true, 1, 1, 16, 16, false),
            (true, false, false, 1, 1, 16, 16, false),
            (true, false, true, -1, 1, 16, 16, false),
            (true, false, true, 120, 1, 16, 16, false),
            (true, false, true, 1, 2, 16, 16, false),
            (true, false, true, 1, 1, 12, 16, false),
            (true, false, true, 1, 1, 0, 0, false),
        };
        foreach (var item in cases)
            if ((bool)canCopy.Invoke(null, new object[] {item.Done, item.Error, item.Context, item.Frames, item.Layers, item.Bytes, item.Expected})! != item.Result)
                throw new Exception("Unsafe request completion accepted.");
        var copy = type.GetMethod("CopyOwned")!;
        var source = new float[] {1f, -0f, float.NaN, float.PositiveInfinity};
        var owned = (float[])copy.Invoke(null, new object[] {4, (Func<int,float>)(index => source[index])})!;
        source[0] = 99;
        if (owned[0] != 1 || !float.IsNaN(owned[2]) || !float.IsPositiveInfinity(owned[3]))
            throw new Exception("Snapshot does not own its copied data.");
        var encode = type.GetMethod("ToBytes")!;
        var bytes = (byte[])encode.Invoke(null, new object[] {owned})!;
        if (bytes.Length != 16 || BitConverter.ToSingle(bytes, 0) != 1f)
            throw new Exception("Float payload encoding is invalid.");
        Console.WriteLine("PASS: GPU capability/dimension bounds, completion/lifetime guards and owned-copy stability.");
    }
}
