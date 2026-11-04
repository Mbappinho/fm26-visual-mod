using UnityEngine;
using Object = UnityEngine.Object;

namespace FM26.VisualDiagnostics;

internal sealed class VisualPrototype
{
    private readonly PlayerBinding _binding;
    private readonly List<(Renderer Renderer, bool Enabled)> _originals = new();
    private readonly List<(Transform Transform, string Start, string End, float Radius)> _segments = new();
    private readonly Dictionary<string, Vector3> _initialLocalJoints = new();
    private readonly Mesh _mesh;
    private readonly Material _bodyMaterial;
    private readonly Material _accentMaterial;
    private readonly GameObject _modelRoot;
    private Transform _head;
    private float _maximumJointMotion;
    private int _updatedFrames;
    private int _hiddenCount;
    private int _retainedSkinnedCount;
    private bool _enabled;
    public bool SemanticOverlapActive => _enabled;
    private bool _originalTransformUnchanged;
    private string _restoreReason = "";

    public VisualPrototype(PlayerBinding binding)
    {
        _binding = binding;
        var positionBefore = binding.Player.transform.position;
        var rotationBefore = binding.Player.transform.rotation;
        Shader shader = null;
        foreach (var renderer in binding.Renderers)
        {
            foreach (var material in renderer.sharedMaterials)
            {
                if (material == null || material.shader == null) continue;
                var name = material.shader.name;
                if (name == "Universal Render Pipeline/Lit" || name == "Universal Render Pipeline/Unlit")
                {
                    shader = material.shader;
                    break;
                }
            }
            if (shader != null) break;
        }
        if (shader == null) throw new InvalidOperationException("No loaded URP shader reference exists on this actor; original renderers remain untouched.");
        _mesh = new Mesh { name = "FM26Test_OriginalPrism" };
        var data = PrismGeometry.Create(8);
        _mesh.vertices = data.Vertices.Select(vertex => new Vector3(vertex.X, vertex.Y, vertex.Z)).ToArray();
        _mesh.triangles = data.Indices;
        _mesh.RecalculateNormals();
        _mesh.RecalculateBounds();
        _bodyMaterial = new Material(shader) { name = "FM26Test_OrangeBody" };
        _accentMaterial = new Material(shader) { name = "FM26Test_CyanAccents" };
        _bodyMaterial.SetColor("_BaseColor", new Color(1f, 0.3f, 0.04f, 1f));
        _accentMaterial.SetColor("_BaseColor", new Color(0.03f, 0.9f, 1f, 1f));
        _modelRoot = new GameObject("FM26Test_OriginalArticulatedModel");
        _modelRoot.transform.SetParent(binding.Player.transform, false);
        try
        {
            AddSegment("Torso", "Character_Hips", "Character_Spine3", 0.18f, _bodyMaterial);
            AddSegment("LeftUpperArm", "Character_LeftArm", "Character_LeftForeArm", 0.06f, _bodyMaterial);
            AddSegment("LeftForearm", "Character_LeftForeArm", "Character_LeftHand", 0.05f, _accentMaterial);
            AddSegment("RightUpperArm", "Character_RightArm", "Character_RightForeArm", 0.06f, _bodyMaterial);
            AddSegment("RightForearm", "Character_RightForeArm", "Character_RightHand", 0.05f, _accentMaterial);
            AddSegment("LeftThigh", "Character_LeftUpLeg", "Character_LeftLeg", 0.085f, _bodyMaterial);
            AddSegment("LeftShin", "Character_LeftLeg", "Character_LeftFoot", 0.065f, _accentMaterial);
            AddSegment("RightThigh", "Character_RightUpLeg", "Character_RightLeg", 0.085f, _bodyMaterial);
            AddSegment("RightShin", "Character_RightLeg", "Character_RightFoot", 0.065f, _accentMaterial);
            AddSegment("Neck", "Character_Spine3", "Character_Head", 0.075f, _accentMaterial);
            _head = AddPart("Head", _accentMaterial).transform;
            foreach (var name in PlayerBinding.RequiredAnchors)
                _initialLocalJoints[name] = binding.Player.transform.InverseTransformPoint(binding.Anchors[name].position);
            foreach (var renderer in binding.Renderers)
            {
                // Retain original skinned face renderers so their Animator culling remains unmodified.
                // The opaque test head covers the retained face; only body MeshRenderers are hidden.
                if (renderer.TryCast<SkinnedMeshRenderer>() != null) { _retainedSkinnedCount++; continue; }
                if (renderer.TryCast<MeshRenderer>() == null
                    || !renderer.transform.IsChildOf(binding.Player.m_visuals3D.transform)
                    || renderer.gameObject.name == "BlobQuad") continue;
                _originals.Add((renderer, renderer.enabled));
                if (renderer.enabled) _hiddenCount++;
                renderer.enabled = false;
            }
            if (_hiddenCount == 0) throw new InvalidOperationException("No active body renderer was identified; refusing a non-replacement test.");
            _enabled = true;
            _originalTransformUnchanged = (binding.Player.transform.position - positionBefore).sqrMagnitude == 0
                && Quaternion.Angle(binding.Player.transform.rotation, rotationBefore) == 0;
            UpdatePose();
        }
        catch
        {
            Restore("apply-failed");
            throw;
        }
    }

    private GameObject AddPart(string name, Material material)
    {
        var part = new GameObject("FM26Test_" + name);
        part.transform.SetParent(_modelRoot.transform, false);
        part.AddComponent<MeshFilter>().sharedMesh = _mesh;
        part.AddComponent<MeshRenderer>().sharedMaterial = material;
        // No Collider, Rigidbody, Animator, or gameplay component is created.
        return part;
    }

    private void AddSegment(string name, string start, string end, float radius, Material material)
        => _segments.Add((AddPart(name, material).transform, start, end, radius));

    public void UpdatePose()
    {
        if (!_enabled) return;
        if (_binding.Player == null || !_binding.Player.gameObject.activeInHierarchy || !_binding.Player.IsActive
            || _binding.Player.m_visuals3D == null || !_binding.Player.m_visuals3D.gameObject.activeInHierarchy
            || _binding.Player.m_visuals3D.IsSubstitute)
        {
            Restore("target-left-active-match");
            return;
        }
        foreach (var item in _originals)
            if (item.Renderer != null) item.Renderer.enabled = false;
        foreach (var segment in _segments)
        {
            var start = _binding.Anchors[segment.Start].position;
            var end = _binding.Anchors[segment.End].position;
            var direction = end - start;
            segment.Transform.position = (start + end) * 0.5f;
            if (direction.sqrMagnitude > 0.000001f)
                segment.Transform.rotation = Quaternion.FromToRotation(Vector3.up, direction);
            segment.Transform.localScale = new Vector3(segment.Radius, MathF.Max(direction.magnitude, 0.01f), segment.Radius);
        }
        var head = _binding.Anchors["Character_Head"];
        _head.position = head.position + Vector3.up * 0.10f;
        _head.rotation = Quaternion.identity;
        _head.localScale = new Vector3(0.15f, 0.3f, 0.15f);
        foreach (var name in PlayerBinding.RequiredAnchors)
        {
            var relative = _binding.Player.transform.InverseTransformPoint(_binding.Anchors[name].position);
            _maximumJointMotion = MathF.Max(_maximumJointMotion, (relative - _initialLocalJoints[name]).magnitude);
        }
        _updatedFrames++;
    }

    public void Restore(string reason)
    {
        foreach (var item in _originals)
            if (item.Renderer != null) item.Renderer.enabled = item.Enabled;
        _enabled = false;
        _restoreReason = reason;
        if (_modelRoot != null) Object.Destroy(_modelRoot);
        if (_mesh != null) Object.Destroy(_mesh);
        if (_bodyMaterial != null) Object.Destroy(_bodyMaterial);
        if (_accentMaterial != null) Object.Destroy(_accentMaterial);
    }

    public object Describe()
    {
        var actorPosition = _binding.Player == null ? Vector3.zero : _binding.Player.transform.position;
        return new
        {
            Plugin = "FM26.VisualDiagnostics", Version = "0.2.12", Session = DiagnosticsPlugin.Session,
            TimestampUtc = DateTime.UtcNow, Enabled = _enabled, Model = "original-articulated-prism",
            _binding.ActorInstanceId, _binding.PlayerId, _binding.Name, _binding.ShirtNumber,
            ModifiedActorCount = _enabled ? 1 : 0,
            HiddenOriginalRendererCount = _hiddenCount,
            RetainedOriginalSkinnedRendererCount = _retainedSkinnedCount,
            OriginalActorTransformUnchangedOnApply = _originalTransformUnchanged,
            MaximumLocalJointMotion = _maximumJointMotion, UpdatedFrames = _updatedFrames,
            CreatedModelRendererCount = _segments.Count + 1,
            CurrentActorPosition = new { X = actorPosition.x, Y = actorPosition.y, Z = actorPosition.z },
            RestoreReason = _restoreReason,
            OriginalRendererFlagsRestored = !_enabled && _originals.All(item => item.Renderer == null || item.Renderer.enabled == item.Enabled),
            Limitations = "Original procedural body; original face retained under test head. No FIFA assets or retargeted animations.",
        };
    }
}
