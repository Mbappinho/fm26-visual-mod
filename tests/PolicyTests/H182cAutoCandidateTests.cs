#nullable disable
using FM26.VisualDiagnostics;
using Inventory = System.Collections.Generic.List<(int ActorInstanceId, int TeamId, bool Eligible, bool FifaEligible, bool Goalkeeper)>;

internal static class H182cAutoCandidateTests
{
    private static readonly List<(string Name, Action Test)> Cases = new()
    {
        ("candidate-accept", AcceptComplete),
        ("candidate-count", RequireTwentyTwo),
        ("candidate-balance", RequireBalancedTeams),
        ("candidate-keepers", RequireKeepers),
        ("candidate-char-unsupported", CharacterizeUnsupportedTeams),
        ("candidate-char-keeper-limit", CharacterizeKeeperLimit),
        ("candidate-char-inputs", CharacterizeInputRefusals),
        ("candidate-char-manual", CharacterizeManualContract),
    };
    public static void Run(string name = null)
    {
        int ran = 0;
        foreach (var c in Cases)
        {
            if (name != null && name != c.Name) continue;
            c.Test(); ran++; Console.WriteLine("H182c PASS: " + c.Name);
        }
        if (name != null && ran != 1) throw new Exception("Unknown H182c case " + name);
    }
    private static void Check(bool condition, string message)
    {
        if (!condition) throw new Exception("FAIL: H182c " + message);
    }
    // Producer-shaped copied scalars, using the actual PlayerBinding.FifaEligible property compiled from production.
    // Legacy Eligible uses the same pre-existing VisualPolicy calls as PlayerBinding.cs, not a new auto eligibility rule.
    private static (int ActorInstanceId, int TeamId, bool Eligible, bool FifaEligible, bool Goalkeeper) Actor(
        int id, int team, bool keeper = false, bool active = true, bool substitute = false, bool medic = false,
        int shirt = 1, bool missingAnchor = false)
    {
        var b = new PlayerBinding { PlayerId = -id, ShirtNumber = shirt, TeamId = team, Active = active,
            Substitute = substitute, Goalkeeper = keeper, Medic = medic };
        foreach (var key in PlayerBinding.RequiredAnchors) b.Anchors.Add(key, new object());
        if (missingAnchor) b.Anchors.Remove(PlayerBinding.RequiredAnchors[0]);
        bool legacy = VisualPolicy.HasValidShirtNumber(b.ShirtNumber) && VisualPolicy.IsEligible(b.Active, b.Substitute,
            b.Goalkeeper, b.Medic, b.PlayerId, PlayerBinding.RequiredAnchors.Count(b.Anchors.ContainsKey));
        return (id, team, legacy, b.FifaEligible, keeper);
    }
    private static Inventory Complete()
    {
        var inv = new Inventory();
        for (int team = 0; team < 2; team++)
            for (int i = 0; i < 11; i++) inv.Add(Actor(-100 - team * 100 - i, team, keeper: i == 0));
        inv.Add(Actor(-301, 0, substitute: true));
        inv.Add(Actor(-302, 1, medic: true));
        inv.Add(Actor(-303, 0, active: false));
        inv.Add(Actor(-304, 1, shirt: 0));
        inv.Add(Actor(-305, 0, missingAnchor: true));
        return inv;
    }
    // Post-cycle characterizations: no further production changes are permitted in this batch.
    private static void CharacterizeUnsupportedTeams()
    {
        foreach (int team in new[] { 2, -1 })
        {
            var inv = Complete(); var odd = Actor(-401, team);
            Check(odd.Eligible && !odd.FifaEligible, "unsupported-team fixture retains legacy eligibility");
            inv.Add(odd);
            Check(!FifaAutoCandidate.IsValid(inv), "otherwise Eligible unsupported-team actor must not disappear");
            inv[inv.Count - 1] = Actor(-401, team, active: false);
            Check(FifaAutoCandidate.IsValid(inv), "ineligible unsupported-team bystander remains excluded");
        }
    }
    private static void CharacterizeKeeperLimit()
    {
        foreach (int team in new[] { 0, 1 })
        {
            var inv = Complete(); int slot = team * 11 + 1;
            inv[slot] = Actor(inv[slot].ActorInstanceId, team, keeper: true);
            Check(!FifaAutoCandidate.IsValid(inv), "resolver already refuses two keepers within a balanced 22");
        }
    }
    private static void CharacterizeInputRefusals()
    {
        Check(!FifaAutoCandidate.IsValid(null), "resolver null input refusal is inherited");
        Check(!FifaAutoCandidate.IsValid(new Inventory()), "resolver empty input refusal is inherited");
        var duplicate = Complete(); duplicate.Add(duplicate[0]);
        Check(!FifaAutoCandidate.IsValid(duplicate), "resolver duplicate inventory id refusal is inherited");
    }
    private static void CharacterizeManualContract()
    {
        var inv = Complete(); inv.Add(Actor(-401, 0)); inv.Add(Actor(-402, 1));
        var ids = FifaActorSelection.ResolveFifa(new FifaOverlayRequest { AllEligible = true }, inv);
        Check(ids.Length == 24 && FifaOverlayPolicy.MaximumActors == 24, "manual all-eligible still accepts 24");
        Check(!FifaAutoCandidate.IsValid(inv), "auto shape remains distinct from manual cap");
        var one = FifaActorSelection.ResolveFifa(new FifaOverlayRequest { ActorInstanceIds = new[] { -101 } }, inv);
        Check(one.SequenceEqual(new[] { -101 }), "manual explicit single outfielder still supported");
    }
    private static void RequireKeepers()
    {
        var missingZero = Complete(); missingZero[0] = Actor(-100, 0);
        Check(!FifaAutoCandidate.IsValid(missingZero), "missing team-0 goalkeeper must be refused");
        var missingOne = Complete(); missingOne[11] = Actor(-200, 1);
        Check(!FifaAutoCandidate.IsValid(missingOne), "missing team-1 goalkeeper must be refused");
        Check(FifaAutoCandidate.IsValid(Complete()), "one goalkeeper per team control");
    }
    private static void RequireBalancedTeams()
    {
        var tenTwelve = Complete(); tenTwelve[10] = Actor(-110, 1);
        Check(!FifaAutoCandidate.IsValid(tenTwelve), "10/12 team split must be refused");
        var twelveTen = Complete(); twelveTen[21] = Actor(-210, 0);
        Check(!FifaAutoCandidate.IsValid(twelveTen), "12/10 team split must be refused");
        Check(FifaAutoCandidate.IsValid(Complete()), "11/11 team balance control");
    }
    private static void RequireTwentyTwo()
    {
        var shortSide = Complete(); shortSide.RemoveAt(10); // 21 selected; both keepers retained
        Check(!FifaAutoCandidate.IsValid(shortSide), "21 selected actors must be refused");
        var extra = Complete(); extra.Add(Actor(-401, 0)); // 23 selected; within the unchanged manual cap
        Check(!FifaAutoCandidate.IsValid(extra), "23 selected actors must be refused");
        Check(FifaAutoCandidate.IsValid(Complete()), "22-player count control");
    }
    private static void AcceptComplete()
    {
        var inv = Complete();
        Check(inv.Count(p => p.FifaEligible || p.Eligible) == 22, "fixture selects 22 via real eligibility");
        Check(FifaAutoCandidate.IsValid(inv), "complete 22-player inventory must be accepted");
    }
}
