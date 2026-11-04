#nullable disable
using FM26.VisualDiagnostics;

// H178a: goalkeeper redo from the 0.7.1 baseline. One case per behavior; each case is run alone (H178_CASE=name) for its
// RED/GREEN and all cases run in the full PolicyTests entry point. A failing assertion throws (nonzero exit).
internal static class H178Tests
{
    private static readonly List<(string Name, Action Body)> Cases = new()
    {
        ("char_budget_refuses_five_palettes", char_budget_refuses_five_palettes),
        ("char_playerbinding_requires_anchors", char_playerbinding_requires_anchors),
        ("char_playerbinding_requires_team", char_playerbinding_requires_team),
        ("fifa_eligible_includes_goalkeeper", fifa_eligible_includes_goalkeeper),
        ("fifa_eligible_requires_declared_team", fifa_eligible_requires_declared_team),
        ("char_fifa_eligible_keeps_legacy_requirements", char_fifa_eligible_keeps_legacy_requirements),
        ("char_legacy_eligibility_refuses_goalkeeper", char_legacy_eligibility_refuses_goalkeeper),
        ("char_legacy_single_refuses_goalkeeper", char_legacy_single_refuses_goalkeeper),
        ("explicit_list_may_include_keeper", explicit_list_may_include_keeper),
        ("all_eligible_selects_22_with_keepers", all_eligible_selects_22_with_keepers),
        ("two_keepers_in_team0_refused", two_keepers_in_team0_refused),
        ("two_keepers_in_team1_refused", two_keepers_in_team1_refused),
        ("char_cap_counts_keepers", char_cap_counts_keepers),
        ("role_keeper_staying_keeper_not_refused", role_keeper_staying_keeper_not_refused),
        ("role_keeper_becoming_outfield_refused", role_keeper_becoming_outfield_refused),
        ("role_change_ends_actor_as_left", role_change_ends_actor_as_left),
        ("report_role_goalkeeper", report_role_goalkeeper),
        ("report_role_outfield", report_role_outfield),
        ("keeper_palette_team0_declared", keeper_palette_team0_declared),
        ("keeper_palette_team1_declared", keeper_palette_team1_declared),
        ("char_legacy_palette_unaffected_by_keeper", char_legacy_palette_unaffected_by_keeper),
        ("handle_budget_admits_24_actors_four_palettes", handle_budget_admits_24_actors_four_palettes),
        ("version_080", version_080),
        ("all_eligible_refuses_undeclared_team", all_eligible_refuses_undeclared_team),
        ("explicit_refuses_undeclared_team", explicit_refuses_undeclared_team),
    };

    public static void Run(string only)
    {
        int ran = 0;
        foreach (var c in Cases)
        {
            if (!string.IsNullOrEmpty(only) && c.Name != only) continue;
            c.Body(); ran++;
            Console.WriteLine($"H178 PASS: {c.Name}");
        }
        if (!string.IsNullOrEmpty(only) && ran != 1) throw new Exception($"FAIL: unknown H178 case {only}");
        Console.WriteLine($"PASS: {ran} H178 goalkeeper cases.");
    }

    // 03: a goalkeeper meeting every other legacy requirement is FIFA-eligible (shirt>0, active, not substitute/medic, PlayerId>0, 15 anchors, team 0).
    private static void fifa_eligible_includes_goalkeeper()
    {
        Check(FifaActorSelection.FifaEligible(1, true, false, true, false, 101, 15, 0), "goalkeeper with all legacy requirements must be FIFA-eligible");
    }

    // 04: FIFA eligibility requires a declared team palette (TeamId 0/1); a goalkeeper of TeamId 2 or -1 is refused.
    private static void fifa_eligible_requires_declared_team()
    {
        Check(!FifaActorSelection.FifaEligible(1, true, false, true, false, 101, 15, 2), "TeamId 2 must not be FIFA-eligible");
        Check(!FifaActorSelection.FifaEligible(1, true, false, false, false, 101, 15, -1), "TeamId -1 must not be FIFA-eligible");
        Check(FifaActorSelection.FifaEligible(1, true, false, true, false, 101, 15, 1), "TeamId 1 goalkeeper stays FIFA-eligible");
    }

    // K1 (characterization, passes on arrival; protected by mutants): each other legacy requirement still refuses a goalkeeper.
    private static void char_fifa_eligible_keeps_legacy_requirements()
    {
        Check(!FifaActorSelection.FifaEligible(0, true, false, true, false, 101, 15, 0), "shirt 0 refused");
        Check(!FifaActorSelection.FifaEligible(1, false, false, true, false, 101, 15, 0), "inactive refused");
        Check(!FifaActorSelection.FifaEligible(1, true, true, true, false, 101, 15, 0), "substitute refused");
        Check(!FifaActorSelection.FifaEligible(1, true, false, true, true, 101, 15, 0), "medic refused");
        Check(!FifaActorSelection.FifaEligible(1, true, false, true, false, 0, 15, 0), "PlayerId 0 refused");
        Check(!FifaActorSelection.FifaEligible(1, true, false, true, false, 101, 14, 0), "14 anchors refused");
        Check(FifaActorSelection.FifaEligible(1, true, false, false, false, 101, 15, 0), "outfield with all requirements stays FIFA-eligible");
    }

    // K1 (characterization of 0.7.1): the legacy PlayerBinding.Eligible predicate (VisualPolicy) still refuses goalkeepers.
    private static void char_legacy_eligibility_refuses_goalkeeper()
    {
        Check(!VisualPolicy.IsEligible(true, false, true, false, 101, 15), "legacy eligibility must refuse a goalkeeper");
        Check(VisualPolicy.IsEligible(true, false, false, false, 101, 15), "legacy eligibility accepts the outfield control");
    }

    // ---- inventory helpers (test-only) ----
    // Tuples are (ActorInstanceId, TeamId, Eligible = legacy outfield flag, FifaEligible, Goalkeeper) as the host builds them.
    private static (int, int, bool, bool, bool) Outfield(int id, int team) => (id, team, true, true, false);
    private static (int, int, bool, bool, bool) Keeper(int id, int team) => (id, team, false, true, true);
    private static (int, int, bool, bool, bool) Excluded(int id, int team) => (id, team, false, false, false); // substitute / medic / inactive
    private static List<(int ActorInstanceId, int TeamId, bool Eligible, bool FifaEligible, bool Goalkeeper)> Match22()
    {
        var inv = new List<(int ActorInstanceId, int TeamId, bool Eligible, bool FifaEligible, bool Goalkeeper)>();
        inv.Add(Keeper(-100, 0)); for (int i = 1; i <= 10; i++) inv.Add(Outfield(-100 - i, 0));
        inv.Add(Excluded(-150, 0)); inv.Add(Excluded(-151, 0)); // team 0 substitutes
        inv.Add(Keeper(-200, 1)); for (int i = 1; i <= 10; i++) inv.Add(Outfield(-200 - i, 1));
        inv.Add(Excluded(-250, 1)); inv.Add(Excluded(-300, 0)); inv.Add(Excluded(-301, 1)); // substitute, medic, inactive
        return inv;
    }
    private static FifaOverlayRequest Legacy(int id) => new() { Action = FifaOverlayPolicy.StartAction, ActorInstanceId = id, RequestId = "h178", Session = "S1", MaximumSeconds = 60 };
    private static FifaOverlayRequest List(params int[] ids) => new() { Action = FifaOverlayPolicy.StartAction, ActorInstanceIds = ids, RequestId = "h178", Session = "S1", MaximumSeconds = 60 };
    private static FifaOverlayRequest AllEligible() => new() { Action = FifaOverlayPolicy.StartAction, AllEligible = true, RequestId = "h178", Session = "S1", MaximumSeconds = 60 };
    private static int[] Accepted(Func<int[]> resolve, string what)
    {
        try { return resolve(); }
        catch (Exception e) when (e is InvalidOperationException || e is ArgumentException) { throw new Exception($"FAIL: {what} was refused: {e.Message}"); }
    }
    private static void Refused(Func<int[]> resolve, string what)
    {
        int[] ids;
        try { ids = resolve(); }
        catch (Exception e) when (e is InvalidOperationException || e is ArgumentException) { return; }
        throw new Exception($"FAIL: {what} was accepted ({ids.Length} actors)");
    }

    // K2 (characterization of 0.7.1 through the skeleton ResolveFifa): the legacy N=1 form still refuses a goalkeeper, even FIFA-eligible.
    private static void char_legacy_single_refuses_goalkeeper()
    {
        var inv = Match22();
        Refused(() => FifaActorSelection.ResolveFifa(Legacy(-100), inv), "legacy N=1 goalkeeper");
        Check(Accepted(() => FifaActorSelection.ResolveFifa(Legacy(-101), inv), "legacy N=1 outfield").SequenceEqual(new[] { -101 }), "legacy N=1 outfield control");
    }

    // 06: an explicit multi id list may include a (FIFA-eligible) goalkeeper; request order kept.
    private static void explicit_list_may_include_keeper()
    {
        var ids = Accepted(() => FifaActorSelection.ResolveFifa(List(-101, -100), Match22()), "explicit list with the team-0 goalkeeper");
        Check(ids.SequenceEqual(new[] { -101, -100 }), "explicit list resolves to the requested ids in order");
    }

    // 07: all-eligible picks every FIFA-eligible actor in inventory order: 22 = 2 goalkeepers + 20 outfield; substitutes,
    // medic and inactive actors excluded.
    private static void all_eligible_selects_22_with_keepers()
    {
        var inv = Match22();
        var ids = Accepted(() => FifaActorSelection.ResolveFifa(AllEligible(), inv), "all-eligible on a 22-player match");
        Check(ids.Length == 22, $"all-eligible must select 22 actors (got {ids.Length})");
        Check(ids.Contains(-100) && ids.Contains(-200), "both goalkeepers selected");
        Check(ids.SequenceEqual(inv.Where(p => p.FifaEligible).Select(p => p.ActorInstanceId)), "inventory order, FIFA-eligible only");
    }

    // 08: a multi selection with two goalkeepers in team 0 is refused before the attempt (FM fields one keeper per team; two
    // keeper-flagged eligible actors in one team mean an inconsistent inventory).
    private static void two_keepers_in_team0_refused()
    {
        var inv = Match22(); inv.Add(Keeper(-160, 0));
        Refused(() => FifaActorSelection.ResolveFifa(AllEligible(), inv), "all-eligible with two team-0 goalkeepers");
        Refused(() => FifaActorSelection.ResolveFifa(List(-100, -160), inv), "explicit list with two team-0 goalkeepers");
    }

    // 09: same bound for team 1 (H177: a team-0-only check survived); one keeper in EACH team stays accepted.
    private static void two_keepers_in_team1_refused()
    {
        var inv = Match22(); inv.Add(Keeper(-260, 1));
        Refused(() => FifaActorSelection.ResolveFifa(AllEligible(), inv), "all-eligible with two team-1 goalkeepers");
        Refused(() => FifaActorSelection.ResolveFifa(List(-200, -260), inv), "explicit list with two team-1 goalkeepers");
        Check(Accepted(() => FifaActorSelection.ResolveFifa(List(-100, -200), inv), "one keeper per team").Length == 2, "one goalkeeper per team accepted");
    }

    // K3 (characterization): keepers count toward the 24-actor cap; 25 FIFA-eligible => refused (never truncated), 24 accepted.
    private static void char_cap_counts_keepers()
    {
        var inv = Match22(); inv.Add(Outfield(-120, 0)); inv.Add(Outfield(-220, 1));
        Check(Accepted(() => FifaActorSelection.ResolveFifa(AllEligible(), inv), "24 FIFA-eligible").Length == 24, "24 selected");
        inv.Add(Outfield(-221, 1));
        Refused(() => FifaActorSelection.ResolveFifa(AllEligible(), inv), "25 FIFA-eligible including 2 keepers");
    }

    // 11: a goalkeeper selected as goalkeeper that is still a goalkeeper passes the per-frame role check.
    private static void role_keeper_staying_keeper_not_refused()
    {
        Check(!FifaActorSelection.RoleRefused(true, true), "keeper still keeper must not be refused");
        Check(!FifaActorSelection.RoleRefused(false, false), "outfield still outfield must not be refused");
    }

    // 12: a goalkeeper whose FM role becomes outfield fails the per-frame role check (role = part of the start identity);
    // an outfield actor becoming goalkeeper stays refused (0.7.1).
    private static void role_keeper_becoming_outfield_refused()
    {
        Check(FifaActorSelection.RoleRefused(true, false), "keeper -> outfield must be refused");
        Check(FifaActorSelection.RoleRefused(false, true), "outfield -> keeper stays refused");
    }

    // 14: in multi, a guard refusal caused by a role change (structure intact, no session refusal) ends ONLY that actor as
    // actor-left (own restore + later-frame verification), like the other hard identity changes; outfield<->keeper both ways.
    private static void role_change_ends_actor_as_left()
    {
        var renderers = new List<(bool, bool, bool?, bool?)> { (true, true, true, true), (true, true, true, true) };
        foreach (var (selected, current) in new[] { (true, false), (false, true) })
        {
            var scope = FifaActorScope.OnActiveFailure(true, null, false, true, true, renderers, FifaActorScope.RoleIdentityLost(selected, current), "FIFA overlay root/scene/actor context changed.");
            Check(scope.Kind == FifaScopeKind.EndActor && scope.Reason == FifaActorScope.ActorLeftReason, $"role {selected}->{current} must end the actor as actor-left (got {scope.Kind}/{scope.Reason})");
        }
        Check(!FifaActorScope.RoleIdentityLost(true, true) && !FifaActorScope.RoleIdentityLost(false, false), "unchanged role is not an identity loss");
    }

    // 16: a goalkeeper actor's final-report section carries Role "goalkeeper".
    private static void report_role_goalkeeper()
    {
        var json = System.Text.Json.JsonSerializer.Serialize(new FifaActorReport { ActorInstanceId = -100, TeamId = 0, Role = FifaActorRole.Of(true) });
        Check(json.Contains("\"Role\":\"goalkeeper\""), "goalkeeper report section must carry Role goalkeeper: " + json.Substring(0, Math.Min(120, json.Length)));
    }

    // 17: an outfield actor's final-report section carries Role "outfield" (every multi actor is labelled).
    private static void report_role_outfield()
    {
        var json = System.Text.Json.JsonSerializer.Serialize(new FifaActorReport { ActorInstanceId = -101, TeamId = 0, Role = FifaActorRole.Of(false) });
        Check(json.Contains("\"Role\":\"outfield\""), "outfield report section must carry Role outfield");
    }

    private static bool Same(float[] a, float[] b) => a.Length == b.Length && a.Zip(b).All(x => x.First == x.Second);
    private static IReadOnlyDictionary<string, float[]> KeeperPalette(int team) => FifaTeamPalettes.ForKey(true, FifaTeamPalettes.Key(true, team, true));

    // 19: the team-0 goalkeeper uses its own declared palette: amber shirt and socks, near-black shorts (linear RGBA), FM skin/head/shoes.
    private static void keeper_palette_team0_declared()
    {
        var gk = KeeperPalette(0);
        Check(Same(gk["shirt"], new[] { 0.85f, 0.65f, 0.05f, 1f }), "team-0 keeper shirt = declared amber");
        Check(Same(gk["shorts"], new[] { 0.08f, 0.08f, 0.08f, 1f }) && Same(gk["socks"], new[] { 0.85f, 0.65f, 0.05f, 1f }), "team-0 keeper shorts/socks declared");
        foreach (var piece in new[] { "skin", "head", "shoes" }) Check(Same(gk[piece], FifaOverlayPolicy.Palette[piece]), $"keeper {piece} = shared declared colour");
        Check(!Same(gk["shirt"], FifaTeamPalettes.For(0)["shirt"]), "team-0 keeper shirt differs from team-0 outfield");
    }

    // 20: the team-1 goalkeeper uses its own declared palette (green shirt/socks, near-black shorts), distinct from the team-1
    // outfield palette and from the team-0 keeper; four distinct shirts in total.
    private static void keeper_palette_team1_declared()
    {
        var gk = KeeperPalette(1);
        Check(Same(gk["shirt"], new[] { 0.15f, 0.70f, 0.35f, 1f }), "team-1 keeper shirt = declared green");
        Check(Same(gk["shorts"], new[] { 0.08f, 0.08f, 0.08f, 1f }) && Same(gk["socks"], new[] { 0.15f, 0.70f, 0.35f, 1f }), "team-1 keeper shorts/socks declared");
        var shirts = new[] { FifaTeamPalettes.For(0)["shirt"], FifaTeamPalettes.For(1)["shirt"], KeeperPalette(0)["shirt"], gk["shirt"] };
        for (int i = 0; i < 4; i++) for (int j = i + 1; j < 4; j++) Check(!Same(shirts[i], shirts[j]), $"shirts {i}/{j} distinct");
    }

    // K4 (characterization; kills H177 survivor #16 'legacy palette unaffected by keeper'): the legacy N=1 form always gets the
    // fixed H160 palette object, whatever the TeamId or role; multi outfield actors keep their 0.7.1 team keys/palettes.
    private static void char_legacy_palette_unaffected_by_keeper()
    {
        foreach (var team in new[] { 0, 1, 7 })
            foreach (var gk in new[] { false, true })
            {
                Check(FifaTeamPalettes.Key(false, team, gk) == team, $"legacy key = TeamId ({team},{gk})");
                Check(ReferenceEquals(FifaTeamPalettes.ForKey(false, FifaTeamPalettes.Key(false, team, gk)), FifaOverlayPolicy.Palette), $"legacy palette = H160 palette ({team},{gk})");
            }
        Check(FifaTeamPalettes.Key(true, 0, false) == 0 && FifaTeamPalettes.Key(true, 1, false) == 1, "multi outfield keys = TeamId");
        Check(ReferenceEquals(FifaTeamPalettes.ForKey(true, 0), FifaTeamPalettes.For(0)) && ReferenceEquals(FifaTeamPalettes.ForKey(true, 1), FifaTeamPalettes.For(1)), "multi outfield palettes = 0.7.1 team palettes");
        Check(new[] { FifaTeamPalettes.Key(true, 0, false), FifaTeamPalettes.Key(true, 1, false), FifaTeamPalettes.Key(true, 0, true), FifaTeamPalettes.Key(true, 1, true) }.Distinct().Count() == 4, "four distinct team+role keys");
    }

    // 22 (H178b redo): the owned-handle budget admits 24 actors x 5 + 6 pieces x 4 team+role palettes = 144 handles (checked before allocation).
    private static void handle_budget_admits_24_actors_four_palettes()
    {
        try { FifaHandleBudget.Require(24, 6, 4); }
        catch (InvalidOperationException e) { throw new Exception("FAIL: 24 actors with 4 palettes refused: " + e.Message); }
        Check(FifaHandleBudget.Required(24, 6, 4) == 144, "24 x 5 + 6 x 4 = 144 handles");
    }

    // H178b: the refusal message of a selection that must be refused (an acceptance is a behavioral failure).
    private static string RefusalMessage(Func<int[]> resolve, string what)
    {
        int[] ids;
        try { ids = resolve(); }
        catch (InvalidOperationException e) { return e.Message; }
        throw new Exception($"FAIL: {what} was accepted ({ids.Length} actors)");
    }

    // 23 (H178b, H179 D-NEW-1, parent decision: keep the 0.7.1 contract): an otherwise eligible actor whose TeamId has no declared
    // palette (host tuple: Eligible=true, FifaEligible=false since PlayerBinding.FifaEligible requires TeamId 0/1) makes an
    // all-eligible request REFUSED with the 0.7.1 message, never silently excluded.
    private static void all_eligible_refuses_undeclared_team()
    {
        var inv = Match22(); inv.Add((-400, 2, true, false, false));
        var message = RefusalMessage(() => FifaActorSelection.ResolveFifa(AllEligible(), inv), "all-eligible with an eligible TeamId 2 actor");
        Check(message == "No declared team palette for TeamId 2.", $"0.7.1 refusal message expected, got: {message}");
    }

    // H178b characterization: same shared selected-actor validation as all-eligible; no new production change.
    private static void explicit_refuses_undeclared_team()
    {
        foreach (var team in new[] { -1, 2, 7 })
        {
            var inv = Match22(); inv.Add((-400, team, true, false, false));
            var message = RefusalMessage(() => FifaActorSelection.ResolveFifa(List(-101, -400), inv), "explicit eligible undeclared-team actor");
            Check(message == $"No declared team palette for TeamId {team}.", $"0.7.1 explicit refusal message expected, got: {message}");
        }
    }

    // H178b T1: executes the ACTUAL PlayerBinding.FifaEligible property, scalar fields, required names and dictionary-key count.
    private static void char_playerbinding_requires_team()
    {
        var binding = new PlayerBinding { ShirtNumber = 1, Active = true, PlayerId = 101, Goalkeeper = true };
        foreach (var name in PlayerBinding.RequiredAnchors) binding.Anchors.Add(name, new object());
        foreach (var team in new[] { -1, 2, 7 })
        {
            binding.TeamId = team;
            Check(!binding.FifaEligible, $"PlayerBinding must forward actual TeamId {team}");
        }
        foreach (var team in new[] { 0, 1 })
        {
            binding.TeamId = team;
            Check(binding.FifaEligible, $"declared team {team} goalkeeper control");
        }
    }

    // H178b T1: anchor COUNT is computed from real required dictionary keys, not hardcoded to 15 or dictionary.Count.
    private static void char_playerbinding_requires_anchors()
    {
        var binding = new PlayerBinding { ShirtNumber = 1, Active = true, PlayerId = 101, Goalkeeper = true, TeamId = 0 };
        Check(!binding.FifaEligible, "empty PlayerBinding anchors refused");
        foreach (var name in PlayerBinding.RequiredAnchors.Take(14)) binding.Anchors.Add(name, new object());
        binding.Anchors.Add("not-a-required-anchor", new object());
        Check(!binding.FifaEligible, "14 required anchors plus an unrelated key refused");
        binding.Anchors.Add(PlayerBinding.RequiredAnchors[14], new object());
        Check(binding.FifaEligible, "all 15 required keys accepted");
    }

    // H178b T2: isolate palette count (35 handles < 144): rejection cannot be masked by the total bound.
    private static void char_budget_refuses_five_palettes()
    {
        Check(FifaHandleBudget.Required(1, 6, 5) < FifaHandleBudget.Maximum, "palette-only bound fixture below total cap");
        bool refused = false;
        try { FifaHandleBudget.Require(1, 6, 5); }
        catch (InvalidOperationException) { refused = true; }
        Check(refused, "five palettes must be refused even below the total handle cap");
        FifaHandleBudget.Require(1, 6, 4); // feasible boundary control
    }

    // Final cycle only: assembly and FIFA host/overlay version bump 0.8.0.
    private static void version_080()
    {
        var root = AppContext.BaseDirectory; while (root != null && !File.Exists(Path.Combine(root, "FM26.VisualDiagnostics.csproj"))) root = Path.GetDirectoryName(root);
        Check(root != null && File.ReadAllText(Path.Combine(root, "FM26.VisualDiagnostics.csproj")).Contains("<Version>0.10.7</Version>", StringComparison.Ordinal), "assembly version 0.10.7 (H238; H234: 0.10.6; H211: 0.10.5)");
    }

    private static void Check(bool condition, string message) { if (!condition) throw new Exception("FAIL: " + message); }
}
