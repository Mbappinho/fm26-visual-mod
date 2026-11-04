namespace FM26.VisualDiagnostics;

public static class BoneMappingPolicy
{
    public const int MaximumBones = 256;
    public static void ValidateLength(int length)
    {
        if (length < 0 || length > MaximumBones) throw new ArgumentException("Unbounded bone table.");
    }

    public static int[] FindIndices(int?[] ids, int? identity)
    {
        ValidateLength(ids.Length);
        if (!identity.HasValue) return Array.Empty<int>();
        return Enumerable.Range(0, ids.Length).Where(index => ids[index] == identity).ToArray();
    }
}
