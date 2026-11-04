namespace FM26.VisualDiagnostics;

public static class VisualPolicy
{
    public static bool IsEligible(bool active, bool substitute, bool goalkeeper, bool medic, int playerId, int anchors)
        => active && !substitute && !goalkeeper && !medic && playerId > 0 && anchors >= 15;

    public static bool HasValidShirtNumber(int shirtNumber) => shirtNumber > 0;

    public static int SelectTarget(int requestedId, int[] eligibleIds)
        => requestedId != 0 && eligibleIds.Count(id => id == requestedId) == 1 ? requestedId : 0;
}
