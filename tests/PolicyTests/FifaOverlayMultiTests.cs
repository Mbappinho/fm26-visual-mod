#nullable disable
using System.Text.Json;
using FM26.VisualDiagnostics;

// H166 (0.6.0): FIFA overlay on ALL eligible players (or an explicit set). Pure CLR parts, executable.
internal static class FifaOverlayMultiTests
{
    private static int _assertions;
    private static void Check(bool okay, string label) { _assertions++; if (!okay) throw new Exception("FAIL: " + label); }
    private static void Reject(Action action, string label)
    {
        _assertions++;
        try { action(); } catch (ArgumentException) { return; } catch (InvalidOperationException) { return; }
        throw new Exception("FAIL: accepted " + label);
    }
    private static JsonElement Json(string text) => JsonDocument.Parse(text).RootElement.Clone();
    private static string Start(string selector) =>
        "{\"Session\":\"S1\",\"RequestId\":\"fifa-all-1\",\"Action\":\"start-fifa-overlay\"," + selector + ",\"MaximumSeconds\":900,\"HideNativePlayer\":true}";

    public static void Run()
    {
        Selector(); Selection(); Palettes(); HandleBudget(); Scope(); GlobalSuspension(); FrameCost(); VertexCopy(); ReportSize(); SessionScope(); DestroyedByFm(); FlagsUnchanged(); H169Followups(); /*RUN*/
        Console.WriteLine($"PASS: {_assertions} FIFA overlay multi-actor assertions (pure CLR).");
    }

    // M1: command selector. Legacy single form unchanged; ActorInstanceIds = "all-eligible" or 1..24 unique nonzero ints; exclusive.
    private static void Selector()
    {
        var legacy = FifaOverlayPolicy.ParseRequest(Json(Start("\"ActorInstanceId\":-42")), "S1");
        Check(legacy.ActorInstanceId == -42 && !legacy.Multi && !legacy.AllEligible && legacy.ActorInstanceIds == null, "legacy single-actor form unchanged (N=1 path)");
        var all = FifaOverlayPolicy.ParseRequest(Json(Start("\"ActorInstanceIds\":\"all-eligible\"")), "S1");
        Check(all.Multi && all.AllEligible && all.ActorInstanceIds == null && all.ActorInstanceId == 0 && all.HideNativePlayer && all.MaximumSeconds == 900, "all-eligible literal");
        var list = FifaOverlayPolicy.ParseRequest(Json(Start("\"ActorInstanceIds\":[-42,17,-9001]")), "S1");
        Check(list.Multi && !list.AllEligible && list.ActorInstanceIds.SequenceEqual(new[] { -42, 17, -9001 }) && list.ActorInstanceId == 0, "explicit list kept in order");
        var max = "[" + string.Join(",", Enumerable.Range(1, 24)) + "]";
        Check(FifaOverlayPolicy.ParseRequest(Json(Start("\"ActorInstanceIds\":" + max)), "S1").ActorInstanceIds.Length == 24, "24 actors accepted");
        Check(FifaOverlayPolicy.ParseRequest(Json(Start("\"ActorInstanceIds\":[5]")), "S1").ActorInstanceIds.Length == 1, "one-element list accepted (multi path with N=1)");
        foreach (var bad in new[]
        {
            "\"ActorInstanceIds\":[]", "\"ActorInstanceIds\":[" + string.Join(",", Enumerable.Range(1, 25)) + "]", "\"ActorInstanceIds\":[0]", "\"ActorInstanceIds\":[5,5]",
            "\"ActorInstanceIds\":[5,\"6\"]", "\"ActorInstanceIds\":[5.5]", "\"ActorInstanceIds\":[2147483648]", "\"ActorInstanceIds\":[true]", "\"ActorInstanceIds\":[null]",
            "\"ActorInstanceIds\":[[5]]", "\"ActorInstanceIds\":\"all\"", "\"ActorInstanceIds\":\"ALL-ELIGIBLE\"", "\"ActorInstanceIds\":\"all-eligible \"", "\"ActorInstanceIds\":null",
            "\"ActorInstanceIds\":5", "\"ActorInstanceIds\":{}", "\"ActorInstanceIds\":true",
            "\"ActorInstanceId\":-42,\"ActorInstanceIds\":\"all-eligible\"", "\"ActorInstanceId\":-42,\"ActorInstanceIds\":[-42]",
            "\"ActorInstanceIds\":\"all-eligible\",\"ActorInstanceIds\":\"all-eligible\"", "\"ActorInstanceIds\":[1],\"ActorInstanceIds\":[2]",
            "\"ActorInstanceIds\":\"all-eligible\",\"ActorInstanceIdz\":1",
        })
            Reject(() => FifaOverlayPolicy.ParseRequest(Json(Start(bad)), "S1"), "selector " + bad);
        Reject(() => FifaOverlayPolicy.ParseRequest(Json("{\"Session\":\"S1\",\"RequestId\":\"fifa-all-1\",\"Action\":\"start-fifa-overlay\",\"MaximumSeconds\":900}"), "S1"), "no selector");
        Reject(() => FifaOverlayPolicy.ParseRequest(Json("{\"Session\":\"S1\",\"RequestId\":\"x\",\"Action\":\"stop-fifa-overlay\",\"ActorInstanceIds\":\"all-eligible\"}"), "S1"), "stop with selector");
        Check(FifaOverlayPolicy.MaximumActors == 24 && FifaOverlayPolicy.AllEligibleSelector == "all-eligible", "declared selector constants");
    }
    private static (int ActorInstanceId, int TeamId, bool Eligible)[] Inventory() =>
        Enumerable.Range(0, 20).Select(i => (-1000 - i, i % 2, true))
            .Concat(new[] { (-5904, 0, false), (-5905, 1, false), (-5906, 0, false) }) // referee/goalkeepers: not eligible
            .ToArray();

    // M2: actor set resolution against the CURRENT inventory (same eligibility the legacy path uses).
    private static void Selection()
    {
        var inv = Inventory();
        var legacy = FifaOverlayPolicy.ParseRequest(Json(Start("\"ActorInstanceId\":-1003")), "S1");
        Check(FifaActorSelection.Resolve(legacy, inv).SequenceEqual(new[] { -1003 }), "legacy: the one requested eligible actor");
        Reject(() => FifaActorSelection.Resolve(FifaOverlayPolicy.ParseRequest(Json(Start("\"ActorInstanceId\":-5904")), "S1"), inv), "legacy ineligible");
        Reject(() => FifaActorSelection.Resolve(FifaOverlayPolicy.ParseRequest(Json(Start("\"ActorInstanceId\":77")), "S1"), inv), "legacy absent");
        var all = FifaActorSelection.Resolve(FifaOverlayPolicy.ParseRequest(Json(Start("\"ActorInstanceIds\":\"all-eligible\"")), "S1"), inv);
        Check(all.Length == 20 && all.SequenceEqual(Enumerable.Range(0, 20).Select(i => -1000 - i)), "all-eligible = the 20 eligible actors in inventory order");
        var listed = FifaActorSelection.Resolve(FifaOverlayPolicy.ParseRequest(Json(Start("\"ActorInstanceIds\":[-1007,-1000]")), "S1"), inv);
        Check(listed.SequenceEqual(new[] { -1007, -1000 }), "explicit list in request order");
        Reject(() => FifaActorSelection.Resolve(FifaOverlayPolicy.ParseRequest(Json(Start("\"ActorInstanceIds\":[-1007,-5904]")), "S1"), inv), "explicit with an ineligible actor");
        Reject(() => FifaActorSelection.Resolve(FifaOverlayPolicy.ParseRequest(Json(Start("\"ActorInstanceIds\":[-1007,12345]")), "S1"), inv), "explicit with an absent actor");
        var dup = inv.Concat(new[] { (-1003, 1, true) }).ToArray();
        Reject(() => FifaActorSelection.Resolve(legacy, dup), "duplicate inventory id (legacy)");
        Reject(() => FifaActorSelection.Resolve(FifaOverlayPolicy.ParseRequest(Json(Start("\"ActorInstanceIds\":\"all-eligible\"")), "S1"), dup), "duplicate inventory id (all)");
        var none = inv.Select(p => (p.ActorInstanceId, p.TeamId, false)).ToArray();
        Reject(() => FifaActorSelection.Resolve(FifaOverlayPolicy.ParseRequest(Json(Start("\"ActorInstanceIds\":\"all-eligible\"")), "S1"), none), "all-eligible with zero eligible");
        var many = Enumerable.Range(0, 25).Select(i => (-2000 - i, i % 2, true)).ToArray();
        Reject(() => FifaActorSelection.Resolve(FifaOverlayPolicy.ParseRequest(Json(Start("\"ActorInstanceIds\":\"all-eligible\"")), "S1"), many), "more than 24 eligible refused (no silent truncation)");
        var oddTeam = inv.Select(p => p.ActorInstanceId == -1004 ? (p.ActorInstanceId, 2, true) : p).ToArray();
        var oddTeamHost = oddTeam.Select(p => (p.Item1, p.Item2, p.Item3, p.Item3 && FifaTeamPalettes.Declared(p.Item2), false)).ToArray(); // H178b: host-shaped tuples (PlayerBinding.FifaEligible requires TeamId 0/1)
        Reject(() => FifaActorSelection.ResolveFifa(FifaOverlayPolicy.ParseRequest(Json(Start("\"ActorInstanceIds\":\"all-eligible\"")), "S1"), oddTeamHost), "multi: eligible actor without a declared team palette");
        Check(FifaActorSelection.Resolve(FifaOverlayPolicy.ParseRequest(Json(Start("\"ActorInstanceId\":-1004")), "S1"), oddTeam).Single() == -1004, "legacy path keeps the fixed palette, team not required");
        Reject(() => FifaActorSelection.Resolve(null, inv), "null request");
        Reject(() => FifaActorSelection.Resolve(legacy, null), "null inventory");
    }
    // M3: fixed declared team palettes (TeamId 0 / 1); skin/head/shoes common; legacy single form keeps the H160 palette.
    private static void Palettes()
    {
        var roles = FifaOverlayPolicy.PieceOrder.Select(p => p.Role).ToArray();
        foreach (var team in new[] { 0, 1 })
        {
            var palette = FifaTeamPalettes.For(team);
            Check(roles.All(r => palette.ContainsKey(r) && palette[r].Length == 4 && palette[r].All(c => float.IsFinite(c) && c >= 0 && c <= 1) && palette[r][3] == 1f), $"team {team}: one opaque RGBA per piece role");
            foreach (var common in new[] { "skin", "head", "shoes" })
                Check(palette[common].SequenceEqual(FifaOverlayPolicy.Palette[common]), $"team {team}: {common} common with the legacy palette");
        }
        Check(FifaTeamPalettes.For(0)["shirt"].SequenceEqual(new[] { 0.10f, 0.30f, 0.78f, 1f }) && FifaTeamPalettes.For(0)["shorts"].SequenceEqual(new[] { 0.95f, 0.95f, 0.95f, 1f }), "team 0: blue shirt / white shorts");
        Check(FifaTeamPalettes.For(1)["shirt"].SequenceEqual(new[] { 0.80f, 0.10f, 0.10f, 1f }) && FifaTeamPalettes.For(1)["shorts"].SequenceEqual(new[] { 0.05f, 0.05f, 0.05f, 1f }), "team 1: red shirt / black shorts");
        Check(!FifaTeamPalettes.For(0)["shirt"].SequenceEqual(FifaTeamPalettes.For(1)["shirt"]) && !FifaTeamPalettes.For(0)["socks"].SequenceEqual(FifaTeamPalettes.For(1)["socks"]), "teams distinguishable by shirt and socks");
        foreach (var bad in new[] { -1, 2, int.MinValue }) Reject(() => FifaTeamPalettes.For(bad), "undeclared team " + bad);
        Check(FifaTeamPalettes.Resolve(false, 1) == FifaOverlayPolicy.Palette, "legacy single form: H160 palette whatever the team");
        Check(FifaTeamPalettes.Resolve(true, 1) == FifaTeamPalettes.For(1), "multi: palette by TeamId");
        Check(FifaTeamPalettes.Teams.SequenceEqual(new[] { 0, 1 }), "declared teams");
    }
    // M4: owned handle budget declared and checked before allocation: per actor root GameObject + root Transform + Mesh
    // + MeshFilter + MeshRenderer; materials shared per team (6 pieces x teams used).
    private static void HandleBudget()
    {
        Check(FifaHandleBudget.PerActor == 5, "5 owned handles per actor");
        Check(FifaHandleBudget.Required(1, 6, 1) == 11 && FifaHandleBudget.Required(1, 6, 1) <= 16, "legacy N=1 needs the same 11 handles as 0.5.1 (old cap 16)");
        Check(FifaHandleBudget.Required(20, 6, 2) == 112, "live-005 inventory: 20 actors, both teams");
        Check(FifaHandleBudget.Maximum == 24 * 5 + 6 * 4 && FifaHandleBudget.Required(24, 6, 4) == FifaHandleBudget.Maximum, "declared maximum = 24 actors x 5 + 6 pieces x 4 team+role palettes"); // H178b RED (redo of H178a cycle 22)
        FifaHandleBudget.Require(24, 6, 2); FifaHandleBudget.Require(1, 6, 1);
        foreach (var (a, p, t) in new[] { (0, 6, 1), (25, 6, 2), (24, 7, 2), (24, 6, 5), (1, 6, 0), (-1, 6, 1), (1, 0, 1) })
            Reject(() => FifaHandleBudget.Require(a, p, t), $"budget ({a},{p},{t})");
        Check(FifaHandleBudget.TeamsUsed(new[] { 1, 1, 1 }) == 1 && FifaHandleBudget.TeamsUsed(new[] { 0, 1, 0 }) == 2, "materials only for the teams actually selected");
    }
    private static readonly (bool, bool, bool?, bool?)[] AllInactive = { (true, true, true, false), (true, true, true, false) };
    private static readonly (bool, bool, bool?, bool?)[] OneActive = { (true, true, true, false), (true, true, true, true) };

    // M5: per-actor scope. Active-path refusal: own renderers all inactive (structure intact) => suspend THIS actor; multi +
    // hard identity loss (destroyed / other Transform / PlayerId / shirt / substitute / medic) => end THIS actor only
    // (actor-left); anything else (our exception, readback mismatch, structure change, partial) => stop the WHOLE session.
    private static void Scope()
    {
        foreach (var multi in new[] { false, true })
        {
            var s = FifaActorScope.OnActiveFailure(multi, null, false, true, true, AllInactive, false, "ctx");
            Check(s.Kind == FifaScopeKind.SuspendActor && s.Reason == FifaOverlayPolicy.ViewChangedReason && s.Error == null, $"view change suspends the actor (multi={multi})");
            s = FifaActorScope.OnActiveFailure(multi, null, true, false, true, OneActive, false, "late");
            Check(s.Kind == FifaScopeKind.StopSession && s.Reason == "stop-time-budget" && s.Error == null, "deadline => session stop-time-budget");
            s = FifaActorScope.OnActiveFailure(multi, null, false, false, true, AllInactive, false, "Root teleport");
            Check(s.Kind == FifaScopeKind.StopSession && s.Reason == "owned-update-failure" && s.Error == "Root teleport", "own exception => whole session (conservative)");
            s = FifaActorScope.OnActiveFailure(multi, null, false, true, false, AllInactive, true, "ctx");
            Check(s.Kind == FifaScopeKind.StopSession && s.Reason == "owned-update-failure", "structure changed => whole session even if the actor also left");
            s = FifaActorScope.OnActiveFailure(multi, null, false, true, true, OneActive, false, "ctx");
            Check(s.Kind == FifaScopeKind.StopSession && s.Reason == "owned-update-failure" && s.Error == "ctx", "partial own renderers + refusal => whole session");
        }
        var left = FifaActorScope.OnActiveFailure(true, null, false, true, true, OneActive, true, "ctx");
        Check(left.Kind == FifaScopeKind.EndActor && left.Reason == FifaActorScope.ActorLeftReason && left.Error == null, "multi: identity loss ends only that actor (substitution / red card)");
        left = FifaActorScope.OnActiveFailure(false, null, false, true, true, OneActive, true, "ctx");
        Check(left.Kind == FifaScopeKind.StopSession && left.Reason == "owned-update-failure" && left.Error == "ctx", "legacy N=1: identity loss keeps the 0.5.1 failure stop");
        left = FifaActorScope.OnActiveFailure(true, null, false, false, true, OneActive, true, "Readback");
        Check(left.Kind == FifaScopeKind.StopSession, "multi: a non-guard failure is never relabelled actor-left");
        left = FifaActorScope.OnActiveFailure(true, null, false, true, true, AllInactive, true, "ctx");
        Check(left.Kind == FifaScopeKind.SuspendActor, "own renderers inactive takes precedence (resolved while suspended)");

        // Suspended decisions (pure FifaViewSuspension.Decide) mapped to a scope.
        Check(FifaActorScope.OnSuspended(true, null, FifaStructureState.Intact, (FifaSuspendedDecision.Stay, null, null)).Kind == FifaScopeKind.Stay, "stay");
        Check(FifaActorScope.OnSuspended(true, null, FifaStructureState.Intact, (FifaSuspendedDecision.Resume, null, null)).Kind == FifaScopeKind.Resume, "resume");
        var m = FifaActorScope.OnSuspended(true, null, FifaStructureState.Intact, (FifaSuspendedDecision.StopMatchEnded, FifaViewSuspension.MatchEndedReason, null));
        Check(m.Kind == FifaScopeKind.EndActor && m.Reason == FifaActorScope.ActorLeftReason, "multi: actor gone while suspended, structure intact => that actor only");
        m = FifaActorScope.OnSuspended(true, null, FifaStructureState.Ended, (FifaSuspendedDecision.StopMatchEnded, FifaViewSuspension.MatchEndedReason, null));
        Check(m.Kind == FifaScopeKind.StopSession && m.Reason == FifaViewSuspension.MatchEndedReason, "structure ended => session fm-match-ended");
        m = FifaActorScope.OnSuspended(false, null, FifaStructureState.Intact, (FifaSuspendedDecision.StopMatchEnded, FifaViewSuspension.MatchEndedReason, null));
        Check(m.Kind == FifaScopeKind.StopSession && m.Reason == FifaViewSuspension.MatchEndedReason, "legacy N=1 unchanged: fm-match-ended session stop");
        m = FifaActorScope.OnSuspended(true, null, FifaStructureState.Intact, (FifaSuspendedDecision.StopSuspendLimit, FifaViewSuspension.LimitReason, null));
        Check(m.Kind == FifaScopeKind.EndActor && m.Reason == FifaViewSuspension.LimitReason, "multi: 120 s suspended => that actor only (e.g. deactivated substitute)");
        m = FifaActorScope.OnSuspended(false, null, FifaStructureState.Intact, (FifaSuspendedDecision.StopSuspendLimit, FifaViewSuspension.LimitReason, null));
        Check(m.Kind == FifaScopeKind.StopSession && m.Reason == FifaViewSuspension.LimitReason, "legacy: suspend-limit session stop");
        foreach (var multi in new[] { false, true })
        {
            m = FifaActorScope.OnSuspended(multi, null, FifaStructureState.Intact, (FifaSuspendedDecision.StopHideReset, FifaViewSuspension.HideResetReason, null));
            Check(m.Kind == FifaScopeKind.StopSession && m.Reason == FifaViewSuspension.HideResetReason, "FM reset of our flag => whole session (conservative)");
            m = FifaActorScope.OnSuspended(multi, null, FifaStructureState.Intact, (FifaSuspendedDecision.StopFailure, FifaViewSuspension.FailureReason, "partial"));
            Check(m.Kind == FifaScopeKind.StopSession && m.Reason == FifaViewSuspension.FailureReason && m.Error == "partial", "failure while suspended => whole session");
        }
        Check(FifaActorScope.OnSuspendLimitCount(true, null).Kind == FifaScopeKind.EndActor && FifaActorScope.OnSuspendLimitCount(false, null).Kind == FifaScopeKind.StopSession, "per-actor suspension count bound: actor (multi) / session (legacy)"); // H171 evolution: + session refusal argument
        Check(FifaActorScope.AllEnded(true, 0) == FifaActorScope.AllActorsEndedReason && FifaActorScope.AllEnded(true, 3) == null && FifaActorScope.AllEnded(false, 0) == null, "multi session ends normally once no actor remains");
    }
    // H171 (H169 suggestions 1, 2, 4 + survivor H169-14). Feasible pairs only: Structure()==Ended always comes with a RequireSession
    // refusal on the same frame (session predicates include the structure predicates).
    private static void H169Followups()
    {
        const string refused = "FIFA overlay root/scene/actor context changed.";
        var ended = (FifaSuspendedDecision.StopMatchEnded, FifaViewSuspension.MatchEndedReason, (string)null);
        var m = FifaActorScope.OnSuspended(true, refused, FifaStructureState.Ended, ended);
        Check(m.Kind == FifaScopeKind.StopSession && m.Reason == FifaViewSuspension.MatchEndedReason && m.Error == null, "multi: real match end while suspended (structure ended + its session refusal) => fm-match-ended");
        m = FifaActorScope.OnSuspended(true, refused, FifaStructureState.Intact, ended);
        Check(m.Kind == FifaScopeKind.StopSession && m.Reason == FifaViewSuspension.FailureReason && m.Error == refused, "session refusal with the structure intact stays a session failure (not masked)");
        m = FifaActorScope.OnSuspended(true, refused, FifaStructureState.Unreadable, ended);
        Check(m.Kind == FifaScopeKind.StopSession && m.Reason == FifaViewSuspension.FailureReason && m.Error == refused, "session refusal with an unreadable structure stays a session failure");
        m = FifaActorScope.OnSuspended(false, refused, FifaStructureState.Ended, ended);
        Check(m.Kind == FifaScopeKind.StopSession && m.Reason == FifaViewSuspension.MatchEndedReason, "legacy N=1 unchanged");

        var limit = FifaActorScope.OnSuspendLimitCount(true, refused);
        Check(limit.Kind == FifaScopeKind.StopSession && limit.Reason == FifaViewSuspension.FailureReason && limit.Error == refused, "multi suspension-count bound under a session refusal => whole-session failure (never EndActor / all-actors-ended)");
        limit = FifaActorScope.OnSuspendLimitCount(true, null);
        Check(limit.Kind == FifaScopeKind.EndActor && limit.Reason == FifaViewSuspension.LimitReason && limit.Error == null, "multi, session intact => that actor only (control)");
        limit = FifaActorScope.OnSuspendLimitCount(false, refused);
        Check(limit.Kind == FifaScopeKind.StopSession && limit.Reason == FifaViewSuspension.LimitReason && limit.Error == null, "legacy N=1 suspend-limit unchanged");

        var inactive = new (bool, bool, bool?, bool?, bool?, bool?)[] { (true, true, true, false, null, null) };
        var d = FifaViewSuspension.Decide(FifaStructureState.Intact, (bool?)null, inactive, 1.0);
        Check(d.Decision == FifaSuspendedDecision.StopFailure && d.Reason == FifaViewSuspension.FailureReason, "actor presence unreadable while suspended => failure (as HardIdentityLost/Structure), never an end");
        d = FifaViewSuspension.Decide(FifaStructureState.Intact, (bool?)false, inactive, 1.0);
        Check(d.Decision == FifaSuspendedDecision.StopMatchEnded, "confirmed absence => end decision (control)");
        d = FifaViewSuspension.Decide(FifaStructureState.Intact, (bool?)true, inactive, 1.0);
        Check(d.Decision == FifaSuspendedDecision.Stay, "present + all inactive => stay (control)");
        d = FifaViewSuspension.Decide(FifaStructureState.Unreadable, (bool?)null, inactive, 1.0);
        Check(d.Decision == FifaSuspendedDecision.StopFailure, "unreadable structure first");

        var l = new FifaNativeHideLedger();
        l.Record(-7, false, true, true); l.BeginHide(-7); l.ConfirmHide(-7, true); l.BeginRestore(-7); l.RecordRestoreReadback(-7, false);
        Reject(() => l.RendererDestroyed(-7, true), "destroyed-by-fm refused after a restore readback (H169-14)");
        Check(l.Entries[0].Status == "restored" && !l.Entries[0].DestroyedByFm, "readback status kept");
    }

    // H168 (S1): a SESSION refusal (RequireSession) is never an individual departure: it is never actor-left/EndActor (so it
    // can never add up to all-actors-ended). It keeps the 0.5.1 classification (exact fm-view-changed => suspend, otherwise a
    // whole-session failure stop). Only a confirmed departure with an INTACT session ends one actor.
    private static void SessionScope()
    {
        const string refused = "FIFA overlay root/scene/actor context changed.";
        foreach (var multi in new[] { false, true })
        {
            var s = FifaActorScope.OnActiveFailure(multi, refused, false, true, true, OneActive, true, refused);
            Check(s.Kind == FifaScopeKind.StopSession && s.Reason == "owned-update-failure" && s.Error == refused, $"session refusal + identity loss => whole-session failure, never actor-left (multi={multi})");
            s = FifaActorScope.OnActiveFailure(multi, refused, false, true, false, OneActive, true, refused);
            Check(s.Kind == FifaScopeKind.StopSession && s.Reason == "owned-update-failure", "session refusal + structure changed => whole-session failure");
            s = FifaActorScope.OnActiveFailure(multi, refused, false, true, true, AllInactive, false, refused);
            Check(s.Kind == FifaScopeKind.SuspendActor && s.Reason == FifaOverlayPolicy.ViewChangedReason, "session refusal on the exact view-change condition keeps the 0.5.1 suspension");
            s = FifaActorScope.OnActiveFailure(multi, refused, true, false, true, OneActive, true, "late");
            Check(s.Kind == FifaScopeKind.StopSession && s.Reason == "stop-time-budget", "deadline still first");
        }
        var left = FifaActorScope.OnActiveFailure(true, null, false, true, true, OneActive, true, "ctx");
        Check(left.Kind == FifaScopeKind.EndActor && left.Reason == FifaActorScope.ActorLeftReason, "intact session + confirmed departure => actor-left (control)");
        foreach (var d in new[] { (FifaSuspendedDecision.StopMatchEnded, FifaViewSuspension.MatchEndedReason, (string)null), (FifaSuspendedDecision.StopSuspendLimit, FifaViewSuspension.LimitReason, (string)null) })
        {
            var m = FifaActorScope.OnSuspended(true, refused, FifaStructureState.Intact, d);
            Check(m.Kind == FifaScopeKind.StopSession && m.Reason == FifaViewSuspension.FailureReason && m.Error == refused, $"multi suspended {d.Item1} under a session refusal => whole-session failure (never EndActor)");
            m = FifaActorScope.OnSuspended(true, null, FifaStructureState.Intact, d);
            Check(m.Kind == FifaScopeKind.EndActor, $"multi suspended {d.Item1} with an intact session => that actor only (control)");
        }
        var legacy = FifaActorScope.OnSuspended(false, refused, FifaStructureState.Intact, (FifaSuspendedDecision.StopMatchEnded, FifaViewSuspension.MatchEndedReason, null));
        Check(legacy.Kind == FifaScopeKind.StopSession && legacy.Reason == FifaViewSuspension.MatchEndedReason && legacy.Error == null, "legacy N=1 unchanged (0.5.1 decided without the session part)");
        legacy = FifaActorScope.OnSuspended(false, refused, FifaStructureState.Intact, (FifaSuspendedDecision.StopSuspendLimit, FifaViewSuspension.LimitReason, null));
        Check(legacy.Kind == FifaScopeKind.StopSession && legacy.Reason == FifaViewSuspension.LimitReason, "legacy N=1 suspend-limit unchanged");
        foreach (var multi in new[] { false, true })
        {
            Check(FifaActorScope.OnSuspended(multi, refused, FifaStructureState.Intact, (FifaSuspendedDecision.Stay, null, null)).Kind == FifaScopeKind.Stay, "stay unchanged (0.5.1 stay frames never read the session)");
            Check(FifaActorScope.OnSuspended(multi, refused, FifaStructureState.Intact, (FifaSuspendedDecision.Resume, null, null)).Kind == FifaScopeKind.Resume, "resume is routed to Resume, which throws the session refusal first (L1)");
        }
    }
    // H168 (S2): HideNativePlayer + substitution / red card: FM destroys the departed actor's renderers. With the session intact
    // and the departure confirmed (actor-left only), a destroyed hidden renderer is recorded "destroyed-by-fm" (nothing left to
    // restore, not a failure); a destroyed renderer while the actor is still present stays a failure; every live renderer keeps
    // the restore write + strictly-later-frame readback.
    private static void DestroyedByFm()
    {
        var l = new FifaNativeHideLedger();
        foreach (var id in new[] { -1, -2, -3 }) l.Record(id, false, true, true); // capture completes before the first write
        foreach (var id in new[] { -1, -2, -3 }) { l.BeginHide(id); l.ConfirmHide(id, true); }
        Reject(() => l.RendererDestroyed(-1, true), "destroyed before the restore intent is recorded");
        l.BeginRestore(-1); l.RendererDestroyed(-1, true);
        Check(l.Entries[0].DestroyedByFm && l.Entries[0].Status == "destroyed-by-fm", "departed actor: destroyed hidden renderer => destroyed-by-fm");
        Reject(() => l.RendererDestroyed(-1, true), "destroyed-by-fm recorded once");
        Reject(() => l.RecordRestoreReadback(-1, null), "no readback of a renderer FM destroyed");
        Reject(() => l.RendererDestroyed(-99, true), "unrecorded renderer");
        l.BeginRestore(-2); l.BeginRestore(-3);
        l.RecordRestoreReadback(-2, false); l.RecordRestoreReadback(-3, false);
        Check(l.RestorationVerified, "destroyed-by-fm + live renderers read back at their before value => restoration verified");
        Check(l.Entries.Select(e => e.Status).SequenceEqual(new[] { "destroyed-by-fm", "restored", "restored" }), "statuses");
        l.RecordRestoreReadback(-3, null);
        Check(!l.RestorationVerified, "a LIVE renderer's missing later-frame readback still fails");

        var present = new FifaNativeHideLedger();
        present.Record(-5, false, true, true); present.BeginHide(-5); present.ConfirmHide(-5, true); present.BeginRestore(-5);
        Reject(() => present.RendererDestroyed(-5, false), "renderer destroyed while the actor is still present => failure");
        Check(!present.Entries[0].DestroyedByFm && present.Entries[0].Status == "restore-unverified" && !present.RestorationVerified, "still a failure (restore-unverified)");
        var untouched = new FifaNativeHideLedger();
        untouched.Record(-6, true, true, true);
        Reject(() => untouched.RendererDestroyed(-6, true), "never-written renderer has nothing to classify");
    }
    // H168 (optional): typed per-frame flag check, same verdict as the 0.5.1 JSON check (OriginalPassPolicy.OriginalFlagsUnchanged
    // over SerializeToElement), without the per-actor per-frame JSON serialisation.
    private static void FlagsUnchanged()
    {
        static FifaRendererFlagReport F(string status) => new() { RendererInstanceId = -1, BeforeEnabled = true, BeforeActiveInHierarchy = true, AfterEnabled = true, AfterActiveInHierarchy = true, Status = status };
        var cases = new[]
        {
            new FifaRendererFlagReport[0], new[] { F("unchanged") }, new[] { F("unchanged"), F("unchanged"), F("unchanged") }, new[] { F("changed") },
            new[] { F("unchanged"), F("dead") }, new[] { F("unavailable"), F("unchanged") }, new[] { F(null) }, new[] { F("Unchanged") }, new[] { F("unchanged ") },
        };
        foreach (var c in cases)
            Check(FifaOverlayPolicy.FlagsUnchanged(c) == OriginalPassPolicy.OriginalFlagsUnchanged(JsonSerializer.SerializeToElement(c)),
                "typed flag check == JSON flag check for [" + string.Join(",", c.Select(f => f.Status ?? "null")) + "]");
        Check(FifaOverlayPolicy.FlagsUnchanged(new[] { F("unchanged") }) && !FifaOverlayPolicy.FlagsUnchanged(new[] { F("unchanged"), F("changed") }), "verdicts");
        Reject(() => FifaOverlayPolicy.FlagsUnchanged(null), "null flags");
        Check(!FifaOverlayPolicy.FlagsUnchanged(new FifaRendererFlagReport[] { null }) && !FifaOverlayPolicy.FlagsUnchanged(new[] { F("unchanged"), null }), "H168 mutant F2: a missing element is never unchanged (the JSON check would throw: refusal either way)");
    }
    // M6: global view-change record = every RUNNING actor suspended at once (one FM cut); per-actor records stay per actor.
    private static void GlobalSuspension()
    {
        var g = new FifaGlobalSuspension();
        g.Observe(10, 1000, 1000, 20, 19, 100.0); Check(!g.Suspended && g.Count == 0, "19/20 suspended is a partial (per-actor) suspension only");
        g.Observe(11, 1010, 1000, 20, 20, 100.0); Check(g.Suspended && g.Count == 1 && g.Records[0].StartFrame == 11 && g.Records[0].MatchTimeBefore == 100.0, "all running actors suspended => global record opens");
        g.Observe(12, 1020, 1000, 20, 20, null); Check(g.Suspended && g.Count == 1, "still global");
        g.Observe(13, 1230, 1000, 20, 3, 117.5);
        Check(!g.Suspended && g.Records[0].End == "resumed" && g.Records[0].EndFrame == 13 && Math.Abs(g.Records[0].RealSeconds.Value - 0.22) < 1e-9 && g.Records[0].MatchTimeAfter == 117.5, "first resume closes the global record");
        g.Observe(14, 1240, 1000, 19, 19, 117.6); Check(g.Suspended && g.Count == 2, "an actor that ended no longer counts as running");
        g.CloseOnStop(15, 1250, 1000, "stop-time-budget"); Check(!g.Suspended && g.Records[1].End == "stop-time-budget", "stop closes an open global record");
        var z = new FifaGlobalSuspension(); z.Observe(1, 1, 1000, 0, 0, null); Check(!z.Suspended && z.Count == 0, "no running actor is not a view change");
        Reject(() => z.Observe(1, 1, 1000, 2, 3, null), "more suspended than running");
        Reject(() => z.Observe(1, 1, 1000, -1, 0, null), "negative running");
        var b = new FifaGlobalSuspension();
        for (int i = 0; i < FifaViewSuspension.MaximumSuspensions + 5; i++) { b.Observe(2 * i, 2 * i, 1000, 2, 2, null); b.Observe(2 * i + 1, 2 * i + 1, 1000, 2, 0, 1.0); }
        Check(b.Count == FifaViewSuspension.MaximumSuspensions && b.Dropped == 5, "bounded global list with an exact dropped count");
    }
    // M7: per-frame overlay work cost (managed Stopwatch around one host Tick) -> p50/p95/max stats and a declared guard:
    // more than 8 ms on 60 CONSECUTIVE frames => normal stop "perf-budget". No per-frame allocation (fixed histogram).
    private static void FrameCost()
    {
        Check(FifaFrameCost.BudgetMilliseconds == 8.0 && FifaFrameCost.ConsecutiveFrames == 60 && FifaFrameCost.Reason == "perf-budget", "declared guard");
        var c = new FifaFrameCost();
        for (int i = 1; i <= 100; i++) Check(!c.Add(i * 0.05), "cheap frame " + i); // 0.05 .. 5.00 ms
        var s = c.Stats();
        Check(s.Frames == 100 && Math.Abs(s.P50Milliseconds.Value - 2.50) < 0.011 && Math.Abs(s.P95Milliseconds.Value - 4.75) < 0.011 && s.MaxMilliseconds == 5.0, $"percentiles p50 {s.P50Milliseconds} p95 {s.P95Milliseconds}");
        Check(s.OverBudgetFrames == 0 && s.MaxConsecutiveOverBudget == 0, "no over-budget frame");
        for (int i = 0; i < 59; i++) Check(!c.Add(9.0), "over budget below the consecutive bound");
        Check(!c.Add(1.0), "a cheap frame resets the run");
        for (int i = 0; i < 59; i++) Check(!c.Add(9.0), "second run");
        Check(c.Add(8.0001), "60th consecutive over-budget frame trips the guard");
        Check(!new FifaFrameCost().Add(8.0), "exactly 8 ms is within budget");
        // H168 (H167 mutant 18): the budget boundary is strict (> 8 ms) over the whole 60-frame run.
        var edge = new FifaFrameCost(); bool tripped = false;
        for (int i = 0; i < FifaFrameCost.ConsecutiveFrames * 2; i++) tripped |= edge.Add(FifaFrameCost.BudgetMilliseconds);
        Check(!tripped && edge.Stats().OverBudgetFrames == 0 && edge.Stats().MaxConsecutiveOverBudget == 0, "120 frames of exactly 8 ms never count as over budget");
        var over = new FifaFrameCost(); int trip = -1;
        for (int i = 0; i < FifaFrameCost.ConsecutiveFrames; i++) if (over.Add(BitConverter.Int64BitsToDouble(BitConverter.DoubleToInt64Bits(FifaFrameCost.BudgetMilliseconds) + 1)) && trip < 0) trip = i;
        Check(trip == FifaFrameCost.ConsecutiveFrames - 1 && over.Stats().OverBudgetFrames == 60, "the next double above 8 ms trips on the 60th frame");
        s = c.Stats();
        Check(s.OverBudgetFrames == 119 && s.MaxConsecutiveOverBudget == 60 && s.MaxMilliseconds == 9.0, "over-budget bookkeeping");
        var big = new FifaFrameCost(); big.Add(250.5); Check(big.Stats().MaxMilliseconds == 250.5 && big.Stats().P95Milliseconds == 250.5, "beyond-histogram frame reported exactly through Max");
        foreach (var bad in new[] { double.NaN, double.PositiveInfinity, -0.001 }) Reject(() => new FifaFrameCost().Add(bad), "invalid cost " + bad);
        Check(new FifaFrameCost().Stats().Frames == 0 && new FifaFrameCost().Stats().P50Milliseconds == null, "no frame => null percentiles");
        var z = new FifaFrameCost(); z.Add(0.5);
        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < 10000; i++) z.Add(0.3 + (i % 50) * 0.01);
        Check(GC.GetAllocatedBytesForCurrentThread() - before == 0, "Add allocates nothing per frame");
    }
    // M8: per-actor vertex upload buffer. The pack frames are pre-baked vertex positions (no skinning math): one frame is a
    // contiguous float triple run, copied with ONE block copy into a reused per-actor buffer (in the plugin: a reused
    // Il2CppStructArray<Vector3> span, so Mesh.vertices gets no per-frame managed->IL2CPP array conversion). Pure benchmark.
    private static void VertexCopy()
    {
        const int n = FifaOverlayPolicy.VertexCount, frames = 40;
        var rng = new Random(166); var src = new float[frames * n * 3];
        for (int i = 0; i < src.Length; i++) src[i] = (float)(rng.NextDouble() * 4 - 2);
        var dst = new System.Numerics.Vector3[n];
        foreach (var f in new[] { 0, 1, 17, frames - 1 })
        {
            FifaVertexCopy.CopyFrame<System.Numerics.Vector3>(src, f, n, dst);
            bool same = true; int o = f * n * 3;
            for (int i = 0; i < n; i++) same &= dst[i] == new System.Numerics.Vector3(src[o + i * 3], src[o + i * 3 + 1], src[o + i * 3 + 2]);
            Check(same, "block copy == per-element copy, frame " + f);
        }
        Reject(() => FifaVertexCopy.CopyFrame<System.Numerics.Vector3>(src, frames, n, dst), "frame past the clip");
        Reject(() => FifaVertexCopy.CopyFrame<System.Numerics.Vector3>(src, -1, n, dst), "negative frame");
        Reject(() => FifaVertexCopy.CopyFrame<System.Numerics.Vector3>(src, 0, n, new System.Numerics.Vector3[n - 1]), "short destination");
        Reject(() => FifaVertexCopy.CopyFrame<System.Numerics.Vector3>(src, 0, n, new System.Numerics.Vector3[n + 1]), "H168 (H167 mutant 19): larger destination (exact length required)");
        Reject(() => FifaVertexCopy.CopyFrame<System.Numerics.Vector2>(src, 0, n, new System.Numerics.Vector2[n]), "element is not 3 floats");
        Reject(() => FifaVertexCopy.CopyFrame<System.Numerics.Vector3>(null, 0, n, dst), "null source");

        // Benchmark (managed math only; IL2CPP upload/RecalculateNormals cost NOT measured here). 20 actors per frame.
        const int actors = 20, rounds = 600;
        var buffers = Enumerable.Range(0, actors).Select(_ => new System.Numerics.Vector3[n]).ToArray();
        for (int w = 0; w < 50; w++) for (int a = 0; a < actors; a++) FifaVertexCopy.CopyFrame<System.Numerics.Vector3>(src, (w + a) % frames, n, buffers[a]);
        var sw = System.Diagnostics.Stopwatch.StartNew(); long alloc0 = GC.GetAllocatedBytesForCurrentThread();
        for (int r = 0; r < rounds; r++) for (int a = 0; a < actors; a++) FifaVertexCopy.CopyFrame<System.Numerics.Vector3>(src, (r + a) % frames, n, buffers[a]);
        double copyMs = sw.Elapsed.TotalMilliseconds / rounds; long copyAlloc = GC.GetAllocatedBytesForCurrentThread() - alloc0;
        // 0.5.1 path: per-element Vector3 construction into a reused managed array + the implicit array conversion of
        // Mesh.vertices = Vector3[] (new IL2CPP array + CopyTo every upload), emulated here by a fresh managed array + copy.
        sw.Restart(); alloc0 = GC.GetAllocatedBytesForCurrentThread();
        for (int r = 0; r < rounds; r++) for (int a = 0; a < actors; a++)
        {
            int o = ((r + a) % frames) * n * 3; var v = buffers[a];
            for (int i = 0; i < n; i++) v[i] = new System.Numerics.Vector3(src[o + i * 3], src[o + i * 3 + 1], src[o + i * 3 + 2]);
            var converted = new System.Numerics.Vector3[n]; v.CopyTo(converted, 0);
        }
        double oldMs = sw.Elapsed.TotalMilliseconds / rounds; long oldAlloc = GC.GetAllocatedBytesForCurrentThread() - alloc0;
        Console.WriteLine($"PROBE: vertex fill, {actors} actors x {n} vertices per frame: block copy {copyMs:F4} ms/frame ({copyAlloc / rounds} B/frame); 0.5.1-style loop+conversion {oldMs:F4} ms/frame ({oldAlloc / rounds} B/frame).");
        Check(copyAlloc == 0, "block copy into reused buffers allocates nothing per frame");
        Check(oldAlloc >= (long)rounds * actors * n * 12, "the emulated 0.5.1 conversion allocates one vertex array per upload (what is removed)");
        Check(copyMs < FifaFrameCost.BudgetMilliseconds / 4, "managed vertex fill of 20 actors uses < 1/4 of the per-frame budget");
    }
    // M9: bounded multi report. Per-actor sections (timeline 64 head + 64 tail rows, <=200 suspensions, <=64 renderers),
    // global Suspensions/Discontinuities; worst case 3600 s x 24 actors stays below the 8 MiB writer cap.
    private static FifaMotionTraceRow WorstTraceRow() => new() { Frame = int.MaxValue, MatchTime = -5399.123456789012, Dt = 0.029166666666666667,
        RawSpeed = 14.999999999999998, MoveX = -0.00012345678901234567, MoveZ = -0.00012345678901234567, FaceX = -0.7071067690849304, FaceZ = -0.7071067690849304,
        Rebaselines = 500, Flags = 7, FilteredSpeed = 14.999999999999998, RawAngle = -179.99999999999997, FilteredAngle = -179.99999999999997,
        Tier = 4, Direction = 4, Target = 9, Source = 8, Pending = 7, Phase = 167.99999999999997, SourcePhase = 167.99999999999997, Weight = .9999999999999999, Rate = 1.4999999999999998 };
    private static FifaLocomotionStats WorstLocomotion() => new() { Updates = long.MaxValue,
        ClipFrames = FifaOverlayPolicy.V2Clips.ToDictionary(x => x.Name, x => long.MaxValue), TierFrames = FifaLocomotion.TierNames.ToDictionary(x => x, x => long.MaxValue),
        DirectionFrames = FifaLocomotion.DirectionNames.ToDictionary(x => x, x => long.MaxValue), TierFades = long.MaxValue, DirectionFades = long.MaxValue, MidFadeSnaps = long.MaxValue,
        Rebaselines = long.MaxValue, RebaselineSnaps = long.MaxValue, BlendFrames = long.MaxValue, FallbackFrames = long.MaxValue, MinimumRate = .6000000000000001, MaximumRate = 1.4999999999999998,
        RateClampedLow = long.MaxValue, RateClampedHigh = long.MaxValue, PendingApplications = long.MaxValue, DwellHistogram = Enumerable.Repeat(long.MaxValue, 6).ToArray(),
        SwitchesPerMatchSecond = .9999999999999999, CurrentClip = "strafe_right_walk", CurrentTier = "sprint", CurrentDirection = "strafe-right" };
    private static void ReportSize()
    {
        Check(FifaReportBudget.CleanupText(new string('é', 4096)).Length == 256 && FifaReportBudget.CleanupText(null) == null, "cleanup diagnostic text bounded at 256 UTF-16 chars");
        Check(FifaReportBudget.NameText(new string('é', 4096)).Length == 64 && FifaReportBudget.NameText("ok") == "ok", "display names bounded without changing identities");
        ReportSizeBody();
    }
    private static void ReportSizeBody()
    {
        Check(FifaReportBudget.ActorHeadRows == 8 && FifaReportBudget.ActorTailRows == 8 && FifaReportBudget.MaximumRenderersPerActor == 64, "declared per-actor caps");
        Check(FifaReportBudget.HeadRows(false) == FifaTimelineSampler.DefaultHeadRows && FifaReportBudget.TailRows(false) == FifaTimelineSampler.DefaultTailRows, "legacy N=1 timeline unchanged (1024+1024)");
        Check(FifaReportBudget.HeadRows(true) == 8 && FifaReportBudget.TailRows(true) == 8, "multi timeline per actor");
        Check(FifaReportBudget.RendererBound(false) == FifaNativeHideLedger.MaximumRenderers && FifaReportBudget.RendererBound(true) == 64, "renderer bound: legacy 256, multi 64 (live: 39)");
        var actors = new List<FifaActorReport>();
        for (int a = 0; a < FifaOverlayPolicy.MaximumActors; a++)
        {
            var t = new FifaTimelineSampler(FifaReportBudget.HeadRows(true), FifaReportBudget.TailRows(true));
            for (int i = 0; i < 3600 * 240 / 10; i++) t.Add(new FifaTimelineRow { Kind = "discontinuity", FrameCount = int.MaxValue - i, QpcTicks = long.MaxValue - i, WallDt = -0.016666666666666666,
                MatchTimeSeconds = -5399.123456789012, MatchDt = -0.029166666666666667, MatchSpeedMetersPerSecond = 14.999999999999998, MatchSpeedWindowSeconds = 0.25000000000000006,
                FrameDisplacementMeters = 2.9999999999999996, Paused = true, PublishedPlaybackSpeed = -1.75f, Clip = "strafe_right_walk", BlendClip = "strafe_right_walk", BlendPackFrame = int.MaxValue, BlendWeight = .9999999999999999, Tier = "sprint", Direction = "strafe-right", Fallback = true, Rate = 2.9999999999999996, PhaseKeys = 31.999999999999996,
                PackFrame = int.MaxValue, YawDegrees = -179.99999999999997, RootPosition = new FifaRowVector { X = -12345.679f, Y = -0.00012345679f, Z = -12345.679f },
                RootForward = new FifaRowVector { X = -0.70710677f, Y = -0.00012345679f, Z = -0.70710677f } });
            var s = new FifaViewSuspension();
            for (int i = 0; i < FifaViewSuspension.MaximumSuspensions; i++) { s.TryBegin(int.MaxValue - i, long.MaxValue - i, 5399.123456789012); s.Resume(int.MaxValue - i, long.MaxValue - i, 3, -5399.123456789012); }
            var ledger = new FifaNativeHideLedger();
            for (int i = 0; i < FifaReportBudget.MaximumRenderersPerActor; i++) ledger.Record(int.MinValue + i, false, true, true);
            foreach (var e in ledger.Entries.ToArray()) { ledger.BeginHide(e.RendererInstanceId); ledger.ConfirmHide(e.RendererInstanceId, true); }
            foreach (var e in ledger.RestorePlan) { ledger.BeginRestore(e.RendererInstanceId); ledger.RecordRestoreReadback(e.RendererInstanceId, false); }
            var obs = new FifaFrameObservation(); obs.Begin(int.MaxValue, long.MaxValue, 5399.123456789012, new System.Numerics.Vector3(-12345.679f, -0.00012345679f, -12345.679f));
            obs.Stage = "pose-write"; obs.SetWallDt(0.016666666666666666); obs.SetPosition(new System.Numerics.Vector3(12345.679f, 0.00012345679f, 12345.679f)); obs.SetClock(-5399.123456789012);
            actors.Add(new FifaActorReport
            {
                ActorInstanceId = int.MinValue + a, PlayerId = int.MinValue, Name = new string('\u00e9', 64), TeamId = 1, Role = FifaActorRole.Of(true), ShirtNumber = int.MinValue, State = "ended", // H178a: longest role
                EndReason = FifaActorScope.ActorLeftReason, EndError = new string('\u00e9', 4096), EndFrame = int.MaxValue, Shown = true, PoseUpdates = long.MaxValue, MeshUploads = long.MaxValue,
                ClipUpdates = new Dictionary<string, long> { ["stand"] = long.MaxValue, ["walk_F2"] = long.MaxValue, ["jog_F2"] = long.MaxValue },
                FmHeadToFeetAnchorDistance = 1.4999999999999998, UniformScale = 0.9803921568627451, SuspensionCount = s.Count, Suspensions = s.Records,
                TimelineRowsTotal = t.Total, TimelineRowsDropped = t.Dropped, Timeline = t.Rows, FailureDetail = obs.Snapshot(),
                OriginalRendererFlags = Enumerable.Range(0, FifaReportBudget.MaximumRenderersPerActor).Select(i => new FifaRendererFlagReport { RendererInstanceId = int.MinValue + i,
                    BeforeEnabled = true, BeforeActiveInHierarchy = true, AfterEnabled = true, AfterActiveInHierarchy = false, AfterForceRenderingOff = false, Status = "unchanged" }).ToArray(),
                NativePlayerHidden = true, HiddenRenderers = ledger.Entries,
                Locomotion = WorstLocomotion(),
                MotionTrace = a < FifaMotionTrace.MaximumActors ? new FifaMotionTraceReport { TotalRows = long.MaxValue, DroppedRows = long.MaxValue,
                    Rows = Enumerable.Repeat(WorstTraceRow(), FifaMotionTrace.Capacity).ToArray() } : null,
                Cleanup = new FifaActorCleanup { RequestedFrame = int.MaxValue, VerificationFrame = int.MaxValue, VerificationQpcTicks = long.MaxValue, Verified = false, AliveCount = int.MaxValue,
                    UnknownCount = int.MaxValue, NativeRestorationVerified = false, Errors = Enumerable.Repeat(FifaReportBudget.CleanupText(new string('\u00e9', 4096)), 16).ToArray() },
            });
        }
        var disc = new FifaTimeDiscontinuities();
        for (int i = 0; i < FifaTimeDiscontinuities.MaximumDiscontinuities; i++) disc.TryRecord(int.MaxValue - i, long.MaxValue - i, 5399.123456789012, -5399.123456789012, 12345.678901234567);
        var g = new FifaGlobalSuspension();
        for (int i = 0; i < FifaViewSuspension.MaximumSuspensions; i++) { g.Observe(int.MaxValue - 1, long.MaxValue - 1, 3, 24, 24, 5399.123456789012); g.Observe(int.MaxValue, long.MaxValue, 3, 24, 0, -5399.123456789012); }
        var cost = new FifaFrameCost(); cost.Add(123.456789);
        var handles = Enumerable.Range(0, FifaHandleBudget.Maximum).Select(i => new { InstanceId = int.MinValue + i, Kind = "material-shorts-team-1", Status = "unknown" }).ToArray();
        var body = new { Actors = actors, Suspensions = g.Records, Discontinuities = disc.Records, FrameCost = cost.Stats(), Handles = handles, Header = new string('x', 96 * 1024), Error = new string('\u00e9', 4096), Cleanup = new { Errors = Enumerable.Repeat(FifaReportBudget.CleanupText(new string('\u00e9', 4096)), 128).ToArray() } };
        var serialized = JsonSerializer.SerializeToUtf8Bytes(body, new JsonSerializerOptions { WriteIndented = true });
        long bytes = serialized.LongLength;
        var stressOut = Environment.GetEnvironmentVariable("H178_STRESS_OUT"); // H178a: retain the exact stress body for the component-wise bound
        if (!string.IsNullOrEmpty(stressOut)) File.WriteAllBytes(stressOut, serialized);
        Console.WriteLine($"PROBE: worst-case 3600 s x 24 actors FIFA overlay report body {bytes} bytes (cap {FifaOverlayReport.MaximumBytes}).");
        Check(bytes < FifaOverlayReport.MaximumBytes * 9 / 10, "worst-case multi report below 90% of the 8 MiB cap");
        Check(actors[0].Timeline.Count == 16 && actors[0].TimelineRowsDropped == 3600 * 24 - 16, "per-actor timeline bounded with an exact dropped count");
    }
}
