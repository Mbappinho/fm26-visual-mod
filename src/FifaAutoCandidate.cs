#nullable disable
namespace FM26.VisualDiagnostics;

// H182c: offline inventory shape only, NOT permission to start an overlay.
// Input is the same copied CLR tuple inventory as ResolveFifa; no Unity/native reads.
// No host caller exists. Null/duplicate/unsupported-team semantics will be inherited from ResolveFifa.
public static class FifaAutoCandidate
{
    public static bool IsValid(IReadOnlyList<(int ActorInstanceId, int TeamId, bool Eligible, bool FifaEligible, bool Goalkeeper)> players)
    {
        // Existing all-eligible resolution owns eligibility, unsupported-team, duplicate-id and manual-cap policy.
        // At-most-one goalkeeper per team is already enforced by the resolver; this helper adds completeness only.
        // It deliberately does not change the manual resolver's supported 1..24 actor contract.
        try
        {
            var ids = FifaActorSelection.ResolveFifa(new FifaOverlayRequest { AllEligible = true }, players);
            var selected = players.Where(p => ids.Contains(p.ActorInstanceId)).ToArray();
            return ids.Length == 22 && selected.Count(p => p.TeamId == 0) == 11 && selected.Count(p => p.TeamId == 1) == 11
                && selected.Any(p => p.TeamId == 0 && p.Goalkeeper) && selected.Any(p => p.TeamId == 1 && p.Goalkeeper);
        }
        catch (ArgumentException) { return false; }
        catch (InvalidOperationException) { return false; }
    }
}
