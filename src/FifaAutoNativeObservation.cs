#nullable disable
using FM.Match;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace FM26.VisualDiagnostics;

// H185a: unused shared-source consumer adapter. Capture is NOT runtime authorization.
// Readiness remains closed until the deferred native and file/config gates are certified.
internal static class FifaAutoNativeObservation
{
    internal static FifaAutoPollObservation Capture(GameObject root, List<PlayerBinding> players,
        string session, bool enabled, bool otherModesBlocked, bool candidateOrPrototypeSession)
    {
        try { return Copy(root, players, session, enabled, otherModesBlocked, candidateOrPrototypeSession); }
        catch (Exception) { return new FifaAutoPollObservation(); }
    }

    private static FifaAutoPollObservation Copy(GameObject root, List<PlayerBinding> players,
        string session, bool enabled, bool otherModesBlocked, bool candidateOrPrototypeSession)
    {
        var scene = SceneManager.GetActiveScene();
        if (root.scene.handle != scene.handle) return new FifaAutoPollObservation();
        var controller = root.GetComponentInParent<MatchPlaybackController>();
        if (controller.gameObject.scene.handle != scene.handle) return new FifaAutoPollObservation();
        // Fixed copied prefixes bound work; truncation does NOT certify a complete roster/renderer set.
        var rows = players.Take(64).Select(binding =>
        {
            var p = binding.Player;
            if (p.gameObject.scene.handle != scene.handle) throw new InvalidOperationException("Player in foreign scene.");
            var visuals = p.m_visuals3D;
            var fresh = new PlayerBinding
            {
                PlayerId = p.ID, TeamId = p.TeamID, ShirtNumber = p.ShirtNumber,
                Active = p.IsActive && p.gameObject.activeInHierarchy,
                Substitute = visuals.IsSubstitute, Goalkeeper = p.IsGoalkeeper, Medic = visuals.IsMedic,
                Anchors = binding.Anchors,
            };
            int actor = p.gameObject.GetInstanceID();
            bool legacy = VisualPolicy.HasValidShirtNumber(fresh.ShirtNumber) && VisualPolicy.IsEligible(fresh.Active,
                fresh.Substitute, fresh.Goalkeeper, fresh.Medic, fresh.PlayerId,
                PlayerBinding.RequiredAnchors.Count(fresh.Anchors.ContainsKey));
            return (Binding: binding, Fresh: fresh, Actor: actor, Legacy: legacy, Fifa: fresh.FifaEligible);
        }).ToArray();
        if (rows.Any(row => row.Fresh.PlayerId != row.Binding.PlayerId)) return new FifaAutoPollObservation();
        var inventory = rows.Select(r => (r.Actor, r.Fresh.TeamId, r.Legacy, r.Fifa, r.Fresh.Goalkeeper)).ToArray();
        var selected = FifaActorSelection.ResolveFifa(new FifaOverlayRequest { AllEligible = true }, inventory);
        var identities = selected.Select(id =>
        {
            var row = rows.Single(r => r.Actor == id);
            var transform = row.Binding.Player.transform;
            if (!transform.IsChildOf(root.transform)) throw new InvalidOperationException("Player outside input root.");
            var rendererIds = row.Binding.Renderers.Take(128).Select(renderer =>
            {
                if (renderer.gameObject.scene.handle != scene.handle) throw new InvalidOperationException("Renderer in foreign scene.");
                if (!renderer.transform.IsChildOf(transform)) throw new InvalidOperationException("Renderer outside selected player.");
                return renderer.GetInstanceID();
            }).ToArray();
            return new FifaAutoActorIdentity(id, row.Fresh.PlayerId, row.Fresh.TeamId, row.Fresh.Goalkeeper,
                transform.GetInstanceID(), rendererIds);
        }).ToArray();
        return new FifaAutoPollObservation
        {
            Enabled = enabled, PluginSession = session, SceneName = scene.name,
            RootPresent = true, RootActive = root.activeInHierarchy,
            // Copied renderer prefixes are bounded to 128; a truncated set is not ready.
            // Copied binding prefixes are bounded to 64; a truncated inventory is not current.
            IdentitySnapshotCurrent = players.Count <= 64,
            RenderersReady = selected.All(id => rows.Single(r => r.Actor == id).Binding.Renderers.Count <= 128),
            // H209: FM view not hiding the selected players: EVERY selected actor keeps at least one of its copied (<= 128)
            // renderers active in hierarchy. All renderers inactive = the fm-view-changed shape (FM keeps 2 of 39 inactive normally).
            RenderersVisible = selected.All(id => rows.Single(r => r.Actor == id).Binding.Renderers.Take(128).Any(renderer => renderer.gameObject.activeInHierarchy)),
            OtherModesBlocked = otherModesBlocked || candidateOrPrototypeSession,
            Players = inventory,
            Identity = new FifaAutoCandidateIdentity(session, scene.name, scene.handle, root.GetInstanceID(),
                controller.GetInstanceID(), identities),
            // Never assert pack/manual/report/render readiness or complete native ownership here.
        };
    }
}
