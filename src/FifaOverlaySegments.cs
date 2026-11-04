#nullable disable
namespace FM26.VisualDiagnostics;

// H191: pure policies for sequential overlay SEGMENTS in one plugin session (restart after a clean end, roster change).
public static class FifaOverlaySegments
{
    public const int MaximumSegments = 32;

    // A replacement requires all independent terminal facts and shares the session-wide cap.
    public static bool CanStart(bool hasPrevious, bool previousFinalized, bool previousCleanupVerified, bool previousPublished,
        string previousError, int segmentsStarted)
        => segmentsStarted < MaximumSegments
            && (!hasPrevious || (previousFinalized && previousCleanupVerified && previousPublished && previousError == null));
}

// Two complete changed inventories stop an automatic segment; incomplete inventories reset the streak.
public sealed class FifaRosterWatch
{
    public bool Observe(IReadOnlyList<int> selected, IReadOnlyList<(int ActorInstanceId, int TeamId, bool Eligible, bool FifaEligible, bool Goalkeeper)> inventory)
    {
        // Only a COMPLETE valid candidate (22, 11/11, one keeper each) can replace the running selection.
        if (!FifaAutoCandidate.IsValid(inventory)) { _consecutive = 0; return false; }
        var current = FifaActorSelection.ResolveFifa(new FifaOverlayRequest { AllEligible = true }, inventory);
        bool changed = !current.OrderBy(id => id).SequenceEqual(selected.OrderBy(id => id));
        var sorted = current.OrderBy(id => id).ToArray();
        bool sameAsPrevious = _lastChanged != null && _lastChanged.SequenceEqual(sorted);
        _lastChanged = changed ? sorted : null;
        _consecutive = changed ? _consecutive + 1 : 0;
        if (_consecutive >= 2 && sameAsPrevious) Confirmed = current;
        return _consecutive >= 2;
    }

    // H211 (0.10.5): new complete AUTO selection confirmed by the stopping pair of polls (null = none); seeds the next segment.
    public int[] Confirmed { get; private set; }

    private int _consecutive;
    private int[] _lastChanged; // H211: sorted selection of the previous changed poll
}
