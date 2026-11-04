namespace FM26.VisualDiagnostics;

public static class DiagnosticsPolicy
{
    public static bool CanCapture(bool hasMatchRoot, int skinnedMeshCount)
        => hasMatchRoot && skinnedMeshCount > 0;
}
