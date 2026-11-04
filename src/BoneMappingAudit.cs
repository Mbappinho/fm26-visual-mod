using FM.Graphics.CharacterBuilder.AppearanceController;
using Il2CppInterop.Runtime.InteropTypes.Arrays;
using SIShared.Skinning;
using UnityEngine;

namespace FM26.VisualDiagnostics;

// Read instantiated reference tables and mesh metadata only; never a pose-buffer decoder.
internal sealed class BoneMappingAudit
{
    private readonly Dictionary<int, object> _nodes = new();
    private readonly List<object> _errors = new();

    public object Capture(GameObject root, List<PlayerBinding> players)
    {
        var target = players.FirstOrDefault(player => player.Eligible);
        if (root == null || target == null) throw new InvalidOperationException("No eligible live target.");
        var visual = target.Player.m_visuals3D;
        if (visual == null || !visual.m_initialised || visual.m_isDestroying || visual.m_pluginShutDown)
            throw new InvalidOperationException("Target visual lifecycle is not ready.");
        var mappings = new Dictionary<int, CharacterBonesMapping>();
        var parts = new List<object>();
        var sources = new List<GameObject> {target.Player.gameObject};
        var controller = visual.m_playerAppearanceController;
        if (controller != null && controller.m_partInstances != null)
        {
            if (controller.m_partInstances.Count > 64) throw new InvalidOperationException("Unbounded part inventory.");
            foreach (var part in controller.m_partInstances)
            {
                var instance = part.Value;
                parts.Add(new {Flags = (int)part.Key, FlagName = part.Key.ToString(),
                    InstanceId = instance == null ? (int?)null : instance.GetInstanceID(),
                    Path = instance == null ? null : PlayerBinding.HierarchyPath(instance.transform)});
                if (instance != null) sources.Add(instance);
            }
        }
        foreach (var source in sources)
            foreach (var mapping in source.GetComponentsInChildren<CharacterBonesMapping>(true))
                if (mapping != null) mappings.TryAdd(mapping.GetInstanceID(), mapping);
        if (mappings.Count > 16) throw new InvalidOperationException("Unbounded mapping component inventory.");
        var tables = new List<object>();
        foreach (var mapping in mappings.Values)
        {
            try
            {
                tables.Add(new {ComponentInstanceId = mapping.GetInstanceID(), Path = PlayerBinding.HierarchyPath(mapping.transform),
                    Tables = new[] {CaptureTable("standard", mapping.m_boneMapping), CaptureTable("full-face", mapping.m_boneFullFaceMapping)}});
            }
            catch (Exception error) { _errors.Add(new {Scope = "mapping", InstanceId = mapping.GetInstanceID(), Error = error.ToString()}); }
        }
        foreach (var anchor in target.Anchors.Values) AddNodeAndAncestors(anchor);
        AddNodeAndAncestors(target.Player.transform);
        if (visual.m_rootTransform != null) AddNodeAndAncestors(visual.m_rootTransform);
        var meshes = new List<object>();
        if (target.Renderers.Count > 96) throw new InvalidOperationException("Unbounded renderer inventory.");
        foreach (var renderer in target.Renderers)
        {
            try { meshes.Add(CaptureRenderer(renderer)); }
            catch (Exception error) { _errors.Add(new {Scope = "renderer", InstanceId = renderer.GetInstanceID(), Error = error.ToString()}); }
        }
        var skeleton = visual.m_skeleton;
        var handles = visual.m_transformStreamHandles;
        var appearance = visual.m_appearanceController;
        var animators = target.Player.GetComponentsInChildren<Animator>(true);
        return new
        {
            Plugin = "FM26.VisualDiagnostics", Version = "0.2.12", Session = DiagnosticsPlugin.Session,
            TimestampUtc = DateTime.UtcNow, Frame = Time.frameCount, State = "reference-inventory",
            MetadataOnly = true, NativeCpuPosePayloadRead = false, NewGpuReadbackRequested = false,
            GpuOrderVerified = false, PoseDecodingVerified = false, VisualReplacementActivated = false,
            Actor = new {target.ActorInstanceId, target.PlayerId, target.Name, target.ShirtNumber,
                EntityIndex = target.Player.EntityIndex, CharacterID = (ushort)visual.m_characterID,
                ActorPath = PlayerBinding.HierarchyPath(target.Player.transform),
                RootTransformInstanceId = visual.m_rootTransform == null ? (int?)null : visual.m_rootTransform.GetInstanceID()},
            Skeleton = skeleton == null ? null : new {skeleton.Height, skeleton.Weight, skeleton.IsFemale, skeleton.IsManager,
                skeleton.IsFullFace, LOD = skeleton.LOD.ToString()},
            StaticBoneCounts = new {Standard = SI.Anim.CharacterSkeleton.StandardBoneCount, FullFace = SI.Anim.CharacterSkeleton.FullFaceBoneCount},
            AnimatorHandleDescriptor = new {Created = handles.IsCreated, Length = handles.IsCreated ? handles.Length : 0},
            AppearanceReferences = new {PlayerAppearancePresent = controller != null,
                AnimatorAppearanceInstanceId = appearance == null ? (int?)null : appearance.GetInstanceID(),
                ManagerAppearancePointer = appearance == null || appearance.m_managerAppearanceObject is null ? null
                    : $"0x{appearance.m_managerAppearanceObject.Pointer.ToInt64():x}"},
            Parts = parts, MappingComponents = tables, Nodes = _nodes.Values.ToArray(), Meshes = meshes,
            RequiredAnchors = PlayerBinding.RequiredAnchors.Select(name => new {Name = name,
                TransformInstanceId = target.Anchors.TryGetValue(name, out var anchor) && anchor != null ? (int?)anchor.GetInstanceID() : null}).ToArray(),
            Animators = animators.Select(animator => new {InstanceId = animator.GetInstanceID(), Path = PlayerBinding.HierarchyPath(animator.transform),
                animator.enabled, animator.isActiveAndEnabled, CullingMode = animator.cullingMode.ToString()}).ToArray(),
            Errors = _errors,
        };
    }

    private object CaptureTable(string name, Il2CppReferenceArray<Transform> transforms)
    {
        if (transforms is null) return new {Name = name, Missing = true, Count = 0, Entries = Array.Empty<object>()};
        BoneMappingPolicy.ValidateLength(transforms.Length);
        var values = new Transform[transforms.Length];
        var ids = new int?[transforms.Length];
        for (var index = 0; index < values.Length; index++)
        {
            values[index] = transforms[index];
            ids[index] = values[index] == null ? null : values[index].GetInstanceID();
        }
        var entries = new List<object>();
        for (var index = 0; index < values.Length; index++)
        {
            var transform = values[index];
            if (transform != null) AddNodeAndAncestors(transform);
            var parentId = transform == null || transform.parent == null ? (int?)null : transform.parent.GetInstanceID();
            entries.Add(new {Index = index, TransformInstanceId = ids[index], Name = transform == null ? null : transform.name,
                ParentInstanceId = parentId, ParentMappedIndices = BoneMappingPolicy.FindIndices(ids, parentId),
                SameTransformIndices = BoneMappingPolicy.FindIndices(ids, ids[index])});
        }
        return new {Name = name, Missing = false, Count = values.Length, Entries = entries};
    }

    private void AddNodeAndAncestors(Transform transform)
    {
        for (var depth = 0; transform != null && depth < 64; depth++, transform = transform.parent)
        {
            var id = transform.GetInstanceID();
            if (_nodes.ContainsKey(id)) continue;
            if (_nodes.Count >= 2048) throw new InvalidOperationException("Unbounded reference hierarchy.");
            var parent = transform.parent;
            _nodes.Add(id, new {TransformInstanceId = id, Name = transform.name, Path = PlayerBinding.HierarchyPath(transform),
                ParentInstanceId = parent == null ? (int?)null : parent.GetInstanceID(), SiblingIndex = transform.GetSiblingIndex(),
                Active = transform.gameObject.activeInHierarchy, LocalPosition = Vector(transform.localPosition),
                LocalRotation = Rotation(transform.localRotation), LocalScale = Vector(transform.localScale),
                WorldPosition = Vector(transform.position), WorldRotation = Rotation(transform.rotation), LossyScale = Vector(transform.lossyScale),
                LocalToWorldMatrix = Matrix(transform.localToWorldMatrix), WorldToLocalMatrix = Matrix(transform.worldToLocalMatrix),
                EvidenceKind = "current-reference-transform-not-a-bindpose-or-animated-pose"});
        }
    }

    private object CaptureRenderer(Renderer renderer)
    {
        var skinned = renderer.TryCast<SkinnedMeshRenderer>();
        var filter = skinned == null ? renderer.GetComponent<MeshFilter>() : null;
        var mesh = skinned == null ? filter?.sharedMesh : skinned.sharedMesh;
        var bones = skinned == null ? null : skinned.bones;
        var boneIds = new List<int?>();
        if (bones is not null)
        {
            BoneMappingPolicy.ValidateLength(bones.Length);
            foreach (var bone in bones)
            {
                boneIds.Add(bone == null ? null : bone.GetInstanceID());
                if (bone != null) AddNodeAndAncestors(bone);
            }
        }
        var rootBone = skinned == null ? null : skinned.rootBone;
        if (rootBone != null) AddNodeAndAncestors(rootBone);
        var binds = new List<float[]>();
        var attributes = new List<object>();
        string meshError = null;
        if (mesh != null)
        {
            try
            {
                var descriptors = mesh.GetVertexAttributes();
                if (descriptors.Length > 32) throw new InvalidOperationException("Unbounded vertex attributes.");
                foreach (var attribute in descriptors)
                    attributes.Add(new {Attribute = attribute.attribute.ToString(), Format = attribute.format.ToString(),
                        Dimension = attribute.dimension, Stream = attribute.stream});
                if (mesh.isReadable)
                {
                    var bindposes = mesh.bindposes;
                    BoneMappingPolicy.ValidateLength(bindposes.Length);
                    foreach (var bindpose in bindposes) binds.Add(Matrix(bindpose));
                }
            }
            catch (Exception error) { meshError = error.ToString(); }
        }
        var materials = new List<object>();
        var shared = renderer.sharedMaterials;
        if (shared.Length > 16) throw new InvalidOperationException("Unbounded material slots.");
        for (var slot = 0; slot < shared.Length; slot++)
        {
            var material = shared[slot];
            if (material == null) { materials.Add(new {Slot = slot, Missing = true}); continue; }
            var block = new MaterialPropertyBlock();
            renderer.GetPropertyBlock(block, slot); // Copies into our owned block; no setter on the renderer.
            var fields = new List<object>();
            foreach (var property in new[] {CustomAnimationState.s_transformIndexID, CustomAnimationState.s_numCharactersID,
                CustomAnimationState.s_boneCountID, CustomAnimationState.s_hasScalesID})
                fields.Add(new {PropertyId = property, MaterialHas = material.HasProperty(property), BlockHas = block.HasProperty(property),
                    MaterialInt = material.HasProperty(property) ? (int?)material.GetInt(property) : null,
                    BlockInt = block.HasProperty(property) ? (int?)block.GetInt(property) : null});
            var textures = new List<object>();
            foreach (var property in new[] {CustomAnimationState.s_transQuatTextureID, CustomAnimationState.s_boneScalesTextureID})
            {
                var texture = material.HasProperty(property) ? material.GetTexture(property) : null;
                var blockTexture = block.HasProperty(property) ? block.GetTexture(property) : null;
                textures.Add(new {PropertyId = property, MaterialTextureId = texture == null ? (int?)null : texture.GetInstanceID(),
                    BlockTextureId = blockTexture == null ? (int?)null : blockTexture.GetInstanceID()});
            }
            materials.Add(new {Slot = slot, InstanceId = material.GetInstanceID(), material.name,
                Shader = material.shader == null ? null : material.shader.name, Fields = fields, Textures = textures});
        }
        return new {RendererInstanceId = renderer.GetInstanceID(), Path = PlayerBinding.HierarchyPath(renderer.transform), renderer.enabled,
            Type = skinned == null ? "MeshRenderer-or-other" : "SkinnedMeshRenderer", BoneTransformIds = boneIds,
            RootBoneId = rootBone == null ? (int?)null : rootBone.GetInstanceID(),
            Mesh = mesh == null ? null : new {InstanceId = mesh.GetInstanceID(), mesh.name, mesh.vertexCount,
                Readable = mesh.isReadable, BindposesObserved = mesh.isReadable && meshError == null, Bindposes = binds,
                Attributes = attributes, Error = meshError}, Materials = materials};
    }

    private static object Vector(Vector3 value) => new {X = value.x, Y = value.y, Z = value.z};
    private static object Rotation(Quaternion value) => new {X = value.x, Y = value.y, Z = value.z, W = value.w};
    private static float[] Matrix(Matrix4x4 value) => new[] {value.m00,value.m01,value.m02,value.m03,
        value.m10,value.m11,value.m12,value.m13,value.m20,value.m21,value.m22,value.m23,value.m30,value.m31,value.m32,value.m33};
}
