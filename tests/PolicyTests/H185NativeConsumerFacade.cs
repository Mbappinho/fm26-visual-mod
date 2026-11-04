#nullable disable
// SYNTHETIC CLR consumer facade only; no claim of IL2CPP runtime equivalence.
namespace UnityEngine.SceneManagement
{
    public struct Scene { public int handle; public string name; }
    public static class SceneManager { public static Scene Current; public static Scene GetActiveScene() => Current; }
}
namespace UnityEngine
{
    public class GameObject
    {
        public int Id; public bool activeInHierarchy = true;
        public SceneManagement.Scene scene;
        public Transform transform;
        public FM.Match.MatchPlaybackController Controller;
        public static GameObject PrimaryRoot, FallbackRoot;
        public static GameObject Find(string path) => path == "MatchPlaybackController/MatchComponents/Match3DBuilder" ? PrimaryRoot : path == "Match3DBuilder" ? FallbackRoot : null;
        public Action OnGetInstanceID;
        public int GetInstanceID() { OnGetInstanceID?.Invoke(); return Id; }
        public T GetComponentInParent<T>() where T : class => Controller as T;
    }
    public class Transform
    {
        public int Id; public Transform Parent; public GameObject gameObject;
        public int GetInstanceID() => Id;
        public bool IsChildOf(Transform parent) => this == parent || Parent != null && Parent.IsChildOf(parent);
    }
    public class Renderer
    {
        public int Id; public GameObject gameObject; public Transform transform;
        public bool enabled = true; public bool forceRenderingOff;
        public int GetInstanceID() => Id;
    }
}
namespace FM.Match
{
    public class MatchPlaybackController
    {
        public int Id; public UnityEngine.GameObject gameObject;
        public object m_match = new object();
        public int GetInstanceID() => Id;
    }
    public class Visuals { public bool IsLoading; public bool IsSubstitute; public bool IsMedic; }
    public class Player
    {
        public int ID; public int TeamID; public int ShirtNumber;
        public bool IsActive = true; public bool IsGoalkeeper;
        public UnityEngine.GameObject gameObject; public UnityEngine.Transform transform;
        public Visuals m_visuals3D = new();
    }
}
namespace FM26.VisualDiagnostics
{
    internal sealed partial class PlayerBinding
    {
        public FM.Match.Player Player; public int ActorInstanceId;
        public List<UnityEngine.Renderer> Renderers = new();
    }
}
