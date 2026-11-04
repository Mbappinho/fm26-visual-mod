namespace FM26.VisualDiagnostics;

// Offline decision seam only. Opt-in readiness is NOT permission to start an overlay.
// No host calls this API; session, inventory, pack and lifecycle gates are not implemented here.
public static class FifaAutoActivationDecision
{
    // Readiness covers explicit opt-in only; all actual activation gates remain outside this API.
    public static bool IsOptInReady(bool enabled = false) => enabled;
}

// Offline sequential-poll seam only; readiness never grants activation permission.
// Fingerprints are opaque ordinal tokens: null and empty are valid, distinct tokens.
// The caller must establish candidate validity separately. Not thread-safe.
public sealed class FifaAutoActivationStability
{
    private int _observations;
    private string _fingerprint = null!;

    public bool Observe(bool enabled, string candidateFingerprint)
    {
        if (!enabled)
        {
            _observations = 0;
            return false;
        }
        if (!string.Equals(_fingerprint, candidateFingerprint, System.StringComparison.Ordinal))
            _observations = 0;
        _fingerprint = candidateFingerprint;
        if (_observations < 2) _observations++;
        return _observations == 2;
    }
}
