#nullable disable
// SYNTHETIC CLR facade (H206) for the shared FifaOverlaySession.cs partial: FM Match type + a test-only partial that binds the
// session identities. No claim of IL2CPP runtime equivalence (Unity null semantics, native getters).
namespace FM.Match { public class Match { } }
namespace FM26.VisualDiagnostics
{
    internal sealed partial class FifaOverlay
    {
        internal void BindSessionForTest(int thread, UnityEngine.GameObject root, int rootId, int scene, FM.Match.MatchPlaybackController controller, int controllerId, FM.Match.Match publisher)
        { _thread = thread; _root = root; _rootId = rootId; _scene = scene; _controller = controller; _controllerId = controllerId; _publisher = publisher; }
    }
}
