using SIShared.Skinning;
using UnityEngine;

namespace FM26.VisualDiagnostics;

internal static class NativePoseAudit
{
    public static object Capture(GameObject root, List<PlayerBinding> players)
    {
        var controller = root.GetComponentInParent<FM.Match.MatchPlaybackController>();
        var results = new List<object>();
        if (controller == null || controller._MatchComponents_k__BackingField == null)
            return new { State = "controller-unavailable", MetadataOnly = true, Owners = results };
        foreach (var component in controller._MatchComponents_k__BackingField)
        {
            var owner = component == null ? null : component.TryCast<FM.Match.Animation.Match3DAnimation>();
            if (owner == null) continue;
            try
            {
                var initialized = owner._IsInitialised_k__BackingField;
                var shuttingDown = owner.m_isShuttingDown || owner.m_pluginShutDown;
                var state = owner.m_animationState;
                if (!initialized || shuttingDown || state == null)
                {
                    results.Add(new { OwnerInstanceId = owner.GetInstanceID(), Initialized = initialized,
                        ShuttingDown = shuttingDown, State = "owner-not-ready", MetadataOnly = true });
                    continue;
                }
                // Only array descriptors and the stable identity table are inspected here.
                // No pose payload is read, copied, disposed, or interpreted until publication is proven.
                var bones = state._BoneTransforms_k__BackingField;
                var worlds = state.m_worldTransforms;
                var scales = state._BoneScales_k__BackingField;
                var heights = state.m_heightScales;
                var ids = owner.m_entitiesSIAnimCharactersIDs;
                var idLength = ids.IsCreated ? ids.Length : 0;
                var boneLength = bones.IsCreated ? bones.Length : 0;
                var worldLength = worlds.IsCreated ? worlds.Length : 0;
                var usable = PoseOwnerPolicy.IsUsable(initialized, shuttingDown,
                    bones.IsCreated && worlds.IsCreated && ids.IsCreated,
                    state._EntityCount_k__BackingField, state._BoneCount_k__BackingField, worldLength, boneLength);
                var ownerService = owner.m_sianimCharacters;
                var ownerWrapper = ownerService == null ? null : ownerService.TryCast<FM.GamePlugin.Bindings.MarshallingWrappers.SI_Anim_ICharacters_NativeWrapper>();
                // Read the wrapper's identity field only; never dereference or call its native instance.
                var ownerNativeInstance = ownerWrapper == null ? 0L : ownerWrapper.instance.ToInt64();
                var mappings = new List<object>();
                foreach (var binding in players.Where(player => player.Eligible))
                {
                    var index = binding.Player.EntityIndex;
                    var visuals = binding.Player.m_visuals3D;
                    var characterId = (ushort)visuals.m_characterID;
                    var tableId = ids.IsCreated && index >= 0 && index < idLength ? (ushort)ids[index] : ushort.MaxValue;
                    var characterService = visuals.m_characters;
                    var characterWrapper = characterService == null ? null : characterService.TryCast<FM.GamePlugin.Bindings.MarshallingWrappers.SI_Anim_ICharacters_NativeWrapper>();
                    var characterNativeInstance = characterWrapper == null ? 0L : characterWrapper.instance.ToInt64();
                    var sameManagedService = characterService != null && ownerService != null
                        && characterService.Pointer == ownerService.Pointer;
                    var sameNativeService = PoseOwnerPolicy.HasMatchingNativeService(characterWrapper != null,
                        ownerWrapper != null, characterNativeInstance, ownerNativeInstance);
                    var sameService = sameManagedService || sameNativeService;
                    var characterReady = visuals.m_initialised && !visuals.m_isDestroying && !visuals.m_pluginShutDown;
                    mappings.Add(new { binding.ActorInstanceId, binding.PlayerId, binding.Name,
                        EntityIndex = index, CharacterID = characterId, TableCharacterID = tableId,
                        CharacterInitialized = characterReady, SameCharacterService = sameService,
                        SameManagedCharacterService = sameManagedService, SameNativeCharacterService = sameNativeService,
                        CharacterServicePointer = characterService == null ? "0x0" : $"0x{characterService.Pointer.ToInt64():x}",
                        OwnerServicePointer = ownerService == null ? "0x0" : $"0x{ownerService.Pointer.ToInt64():x}",
                        CharacterKnownNativeWrapper = characterWrapper != null, OwnerKnownNativeWrapper = ownerWrapper != null,
                        CharacterNativeInstance = $"0x{characterNativeInstance:x}", OwnerNativeInstance = $"0x{ownerNativeInstance:x}",
                        ServiceIdentityBasis = sameManagedService ? "same-managed-object" : sameNativeService ? "same-native-wrapper-instance" : "unverified",
                        Verified = usable && characterReady && PoseOwnerPolicy.HasMatchingIdentity(index, idLength, characterId, tableId, sameService) });
                }
                results.Add(new
                {
                    OwnerInstanceId = owner.GetInstanceID(), OwnerPath = PlayerBinding.HierarchyPath(owner.transform),
                    StateInstanceId = state.GetInstanceID(), StatePath = PlayerBinding.HierarchyPath(state.transform),
                    Initialized = initialized, ShuttingDown = shuttingDown, MetadataUsable = usable,
                    EntityCount = state._EntityCount_k__BackingField, BoneCount = state._BoneCount_k__BackingField,
                    BoneFloat4Count = boneLength, WorldTransformCount = worldLength,
                    BoneScaleCount = scales.IsCreated ? scales.Length : 0,
                    HeightScaleCount = heights.IsCreated ? heights.Length : 0,
                    IdentityTableLength = idLength, Mappings = mappings,
                    MetadataOnly = true, PosePayloadRead = false, LayoutVerified = false,
                    PublicationSynchronizationVerified = false,
                });
            }
            catch (Exception exception) { results.Add(new { Error = exception.ToString(), MetadataOnly = true }); }
        }
        return new { State = results.Count == 0 ? "owner-not-found" : "owner-metadata", MetadataOnly = true, Owners = results };
    }
}
