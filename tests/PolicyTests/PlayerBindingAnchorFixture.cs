namespace FM26.VisualDiagnostics;
// Test-only other half: stands in only for the Unity Transform-valued dictionary. The production property reads keys only.
internal sealed partial class PlayerBinding
{
    public Dictionary<string, object> Anchors = new();
}
