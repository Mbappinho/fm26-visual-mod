namespace FM26.VisualDiagnostics;

// Offline sequential-call seam only. No host calls this API.
// Session tokens use ordinal equality; null and empty are valid, distinct tokens.
// Future host ownership: one instance per immutable plugin Session, never recreated
// on configuration reload, readiness changes or candidate/inventory changes.
public sealed class FifaAutoActivationAttempt
{
    private readonly string _session;
    private bool _consumed;

    public FifaAutoActivationAttempt(string session) => _session = session;

    // A successful reservation is consumed before returning to any caller I/O.
    // There is intentionally no outcome, unreserve or reset API.
    public bool TryReserve(string currentSession, bool ready)
    {
        if (!ready || _consumed) return false;
        if (!string.Equals(_session, currentSession, System.StringComparison.Ordinal)) return false;
        _consumed = true;
        return true;
    }
}
