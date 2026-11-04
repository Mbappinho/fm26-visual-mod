#nullable disable
// SYNTHETIC CLR facade (H209) for the shared src/FifaOverlayActorContext.cs: the FifaOverlay accessors the actor guard reads
// (bodies copied from FifaOverlay.cs), the guard exception type and PlayerBinding.Eligible as a plain scalar.
// No claim of IL2CPP runtime equivalence (Unity null semantics, native getters).
namespace FM26.VisualDiagnostics
{
    internal sealed class FifaGuardException : InvalidOperationException { public FifaGuardException(string message) : base(message) { } }
    internal sealed partial class FifaOverlay
    {
        internal UnityEngine.Transform RootTransform => _root.transform;
        internal int Scene => _scene;
        internal FifaOverlayRequest Request { get; set; }
    }
    internal sealed partial class PlayerBinding { public bool Eligible; }
}
