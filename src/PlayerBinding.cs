using FM.Match;
using UnityEngine;

namespace FM26.VisualDiagnostics;

internal sealed partial class PlayerBinding
{
    public Player Player;
    public int ActorInstanceId;
    public string Name;
    public Dictionary<string, Transform> Anchors = new();
    public List<Renderer> Renderers = new();
    public List<object> RendererDescriptions = new();
    public bool Eligible => VisualPolicy.HasValidShirtNumber(ShirtNumber) && VisualPolicy.IsEligible(Active, Substitute, Goalkeeper, Medic, PlayerId,
        RequiredAnchors.Count(name => Anchors.ContainsKey(name)));


    public static List<PlayerBinding> Collect(GameObject root)
    {
        var result = new List<PlayerBinding>();
        foreach (var player in root.GetComponentsInChildren<Player>(true))
        {
            var visuals = player.m_visuals3D;
            if (visuals == null || visuals.IsLoading) continue;
            var binding = new PlayerBinding
            {
                Player = player, ActorInstanceId = player.gameObject.GetInstanceID(),
                PlayerId = player.ID, Name = player.BillboardName ?? player.ShirtName ?? "",
                ShirtNumber = player.ShirtNumber, TeamId = player.TeamID,
                Active = player.IsActive && player.gameObject.activeInHierarchy,
                Substitute = visuals.IsSubstitute, Goalkeeper = player.IsGoalkeeper, Medic = visuals.IsMedic,
            };
            foreach (var skinned in player.GetComponentsInChildren<SkinnedMeshRenderer>(true))
            {
                foreach (var bone in skinned.bones)
                    if (bone != null && !binding.Anchors.ContainsKey(bone.name)) binding.Anchors.Add(bone.name, bone);
            }
            foreach (var renderer in player.GetComponentsInChildren<Renderer>(true))
            {
                if (renderer.gameObject.name.StartsWith("FM26Test_", StringComparison.Ordinal)) continue;
                binding.Renderers.Add(renderer);
                var skinned = renderer.TryCast<SkinnedMeshRenderer>();
                var meshRenderer = renderer.TryCast<MeshRenderer>();
                var filter = meshRenderer == null ? null : meshRenderer.GetComponent<MeshFilter>();
                var mesh = skinned != null ? skinned.sharedMesh : filter?.sharedMesh;
                var materials = new List<object>();
                foreach (var material in renderer.sharedMaterials)
                {
                    if (material == null) continue;
                    materials.Add(new
                    {
                        MaterialInstanceId = material.GetInstanceID(), material.name,
                        Shader = material.shader == null ? null : material.shader.name,
                        HasBaseColor = material.HasProperty("_BaseColor"), HasColor = material.HasProperty("_Color"),
                    });
                }
                binding.RendererDescriptions.Add(new
                {
                    RendererInstanceId = renderer.GetInstanceID(),
                    GameObjectInstanceId = renderer.gameObject.GetInstanceID(),
                    Type = skinned != null ? "SkinnedMeshRenderer" : meshRenderer != null ? "MeshRenderer" : "OtherRenderer",
                    Path = HierarchyPath(renderer.transform), Name = renderer.name,
                    renderer.enabled, ActiveInHierarchy = renderer.gameObject.activeInHierarchy,
                    VisibleToCamera = renderer.isVisible,
                    Mesh = mesh == null ? null : new { mesh.name, mesh.vertexCount, mesh.subMeshCount },
                    BoneCount = skinned == null ? 0 : skinned.bones.Length, Materials = materials,
                });
            }
            result.Add(binding);
        }
        return result;
    }

    public object Describe()
    {
        var position = Player.transform.position;
        return new
        {
            ActorInstanceId, PlayerId, Name, ShirtNumber, TeamId,
            EntityIndex = Player.EntityIndex, EntityKind = Player.EntityKind.ToString(),
            Active, Substitute, Goalkeeper, Medic, Eligible,
            WorldPosition = new { X = position.x, Y = position.y, Z = position.z },
            RendererCount = Renderers.Count,
            MeshRendererCount = Renderers.Count(renderer => renderer.TryCast<MeshRenderer>() != null),
            SkinnedMeshRendererCount = Renderers.Count(renderer => renderer.TryCast<SkinnedMeshRenderer>() != null),
            AnchorCount = RequiredAnchors.Count(name => Anchors.ContainsKey(name)),
            Anchors = RequiredAnchors.Where(name => Anchors.ContainsKey(name))
                .Select(name => new { Name = name, InstanceId = Anchors[name].GetInstanceID() }).ToArray(),
            Renderers = RendererDescriptions,
            AnimationCullingModes = Player.GetComponentsInChildren<Animator>(true)
                .Select(animator => new { InstanceId = animator.GetInstanceID(), Mode = animator.cullingMode.ToString(), animator.enabled }).ToArray(),
        };
    }

    public static string HierarchyPath(Transform transform)
    {
        var names = new List<string>();
        while (transform != null && names.Count < 64)
        {
            names.Add(transform.name + "#" + transform.gameObject.GetInstanceID());
            transform = transform.parent;
        }
        names.Reverse();
        return string.Join("/", names);
    }
}
