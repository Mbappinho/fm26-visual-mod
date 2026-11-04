import unittest
from pathlib import Path


class CandidateOverlayIntegrationTests(unittest.TestCase):
    def test_overlay_lifetime_callbacks_are_wired_without_mutating_original_renderers(self):
        root = Path(__file__).parents[1]
        plugin = (root / "src/DiagnosticsPlugin.cs").read_text()
        self.assertIn("_candidateOverlay?.Tick();", plugin, "No every-frame candidate polling/cleanup")
        self.assertLess(plugin.index("_candidateOverlay?.Tick();"), plugin.index("if (now < _nextPoll"))
        self.assertIn("_candidateOverlay?.LateTick();", plugin)
        self.assertIn('_candidateOverlay?.Stop("diagnostics-destroyed")', plugin)
        self.assertIn("candidate-overlay-command.json", plugin)
        self.assertIn("candidate-overlay-request-result.json", plugin)
        source = (root / "src/CandidatePoseOverlay.cs").read_text()
        for forbidden in ("GetTransforms(", "GetRawTextureData", "m_heightScales[", "m_worldTransforms[",
                          "WaitForCompletion(", "CreatePrimitive(", "SetLocalTransformsEnabled("):
            self.assertTrue(forbidden not in source, f"Forbidden overlay operation: {forbidden}")
        self.assertIn("view is null", source)
        self.assertIn("_displayCamera.cameraType != CameraType.Game", source)
        self.assertIn("_displayCamera.targetTexture != null", source)
        self.assertIn("(_displayCamera.cullingMask & 1) == 0", source)
        self.assertIn("if (firstRender)", source)
        self.assertIn("GhostDisplayPolicy.ApplyDisplayOffset", source)
        self.assertIn("DisplayOffsetIsPoseCorrection = false", source)
        self.assertIn("UnshiftedWorldCandidate", source)
        self.assertIn("CurrentOriginalRootScreen", source)
        self.assertIn("ObserveCleanup", source)
        self.assertIn("_cleanupRequestedFrame", source)
        self.assertIn("NativeGpuMappingVerified = false", source)
        self.assertIn("OriginalRendererWrites = 0", source)

    def test_explicit_cadence_is_validated_before_attempt_and_frozen_for_every_phase(self):
        root = Path(__file__).parents[1]
        plugin = (root / "src/DiagnosticsPlugin.cs").read_text()
        command = plugin.split("private void ProcessCandidateCommand(GameObject root)", 1)[1].split("private void ProcessVisualCommand()", 1)[0]
        self.assertIn('data.TryGetProperty("SubmissionIntervalFrames", out var cadence)', command)
        self.assertIn("cadence.GetInt32()", command)
        self.assertIn("CandidatePosePolicy.IsSubmissionInterval(submissionIntervalFrames)", command)
        self.assertLess(command.index("cadence.GetInt32()"), command.index("new CandidatePoseOverlay()"))
        self.assertLess(command.index("CandidatePosePolicy.IsSubmissionInterval"), command.index("if (_candidateOverlay != null"))
        self.assertIn('if (hasCadence) throw new InvalidOperationException("Stop must not carry submission cadence.");', command)
        self.assertIn("_candidateOverlay.Start(root, matches[0], requestId, WriteJson, submissionIntervalFrames)", command)
        self.assertIn('SubmissionIntervalFrames = action == "start-candidate" ? (int?)submissionIntervalFrames : null', command)
        source = (root / "src/CandidatePoseOverlay.cs").read_text()
        start = source.split("public void Start(", 1)[1].split("public void Tick()", 1)[0]
        self.assertIn("int submissionIntervalFrames = CandidatePosePolicy.SubmissionIntervalFrames", start)
        self.assertLess(start.index("new CandidatePoseBudget(submissionIntervalFrames)"), start.index("_used = true"))
        self.assertEqual(source.count("_budget = new CandidatePoseBudget(submissionIntervalFrames)"), 1)
        self.assertEqual(source.count("CandidatePosePolicy.DecideForInterval("), 3)
        self.assertEqual(source.count("_clock.Elapsed.TotalSeconds, _budget.SubmissionIntervalFrames"), 3)
        self.assertIn("SubmissionIntervalFrames = _budget.SubmissionIntervalFrames", source)

    def test_first_failure_guards_keep_order_and_short_circuit_dependencies(self):
        source = (Path(__file__).parents[1] / "src/CandidatePoseOverlay.cs").read_text()
        self.assertIn("ContextFailure = _contextGuard.Failure", source)
        self.assertIn("private readonly CandidateContextGuard _contextGuard = new();", source)
        reject = source.split("private bool RejectContext(", 1)[1].split("private bool IdentityMatches(", 1)[0]
        self.assertIn("Time.frameCount, Stopwatch.GetTimestamp(), Stopwatch.Frequency", reject)
        self.assertNotIn("Publish", reject)
        self.assertNotIn("_actor.", reject, "Failure facts must not re-read a destroyed actor")
        context = source.split("private bool ContextMatches(string stage)", 1)[1].split("private void CaptureOriginalsAndIndexSource()", 1)[0]
        identity = source.split("private bool IdentityMatches(string stage)", 1)[1].split("private bool ContextMatches(string stage)", 1)[0]
        originals = source.split("private bool OriginalsMatch(string stage)", 1)[1].split("private static int MeshId", 1)[0]
        # Existing predicates retain their literal expressions and left-to-right evaluation.
        ordered = {
            "context": (context, [
                ("_session != DiagnosticsPlugin.Session", "session-changed"),
                ('scene.name != "MatchPlayback"', "active-scene-name-changed"),
                ("scene.handle != _sceneHandle", "active-scene-handle-changed"),
                ("_matchRoot == null", "match-root-unavailable"),
                ("_matchRoot.GetInstanceID() != _matchRootId", "match-root-identity-changed"),
                ("!_matchRoot.activeInHierarchy", "match-root-inactive"),
                ("_matchRoot.scene.handle != _sceneHandle", "match-root-scene-changed"),
                ("_controller == null", "controller-unavailable"),
                ("_controller.GetInstanceID() != _controllerId", "controller-identity-changed"),
                ("_matchRoot.GetComponentInParent<MatchPlaybackController>() == null", "root-controller-unavailable"),
                ("_matchRoot.GetComponentInParent<MatchPlaybackController>().Pointer != _controller.Pointer", "root-controller-pointer-changed"),
                ("_owner == null", "owner-unavailable"),
                ("_owner.GetInstanceID() != _ownerId", "owner-identity-changed"),
                ("!_owner._IsInitialised_k__BackingField", "owner-not-initialized"),
                ("_owner.m_isShuttingDown", "owner-shutting-down"),
                ("_owner.m_pluginShutDown", "owner-plugin-shut-down"),
                ("_animationState == null", "animation-state-unavailable"),
                ("_animationState.GetInstanceID() != _stateId", "animation-state-identity-changed"),
                ("_owner.m_animationState == null", "owner-animation-state-unavailable"),
                ("_owner.m_animationState.Pointer != _animationState.Pointer", "owner-animation-state-pointer-changed"),
                ("_animationState._EntityCount_k__BackingField != _entities", "entity-count-changed"),
                ("_animationState._BoneCount_k__BackingField != _bones", "bone-count-changed"),
                ("_texture == null", "texture-unavailable"),
                ("_texture.GetInstanceID() != _textureId", "texture-identity-changed"),
                ("_animationState.m_boneTransforms == null", "state-texture-unavailable"),
                ("_animationState.m_boneTransforms.Pointer != _texture.Pointer", "state-texture-pointer-changed"),
                ("_texture.width != _width", "texture-width-changed"),
                ("_texture.height != _height", "texture-height-changed"),
                ("SystemInfo.graphicsDeviceType.ToString() != _backend", "graphics-backend-changed"),
                ("_ownedRoot == null", "owned-root-unavailable"),
                ("_ownedRoot.transform.parent != null", "owned-root-parent-changed"),
                ("_ownedRoot.scene.handle != _sceneHandle", "owned-root-scene-changed"),
                ("_ownedHandles.Any(h => h.Value == null)", "owned-handle-unavailable"),
                ("currentOwner == null", "unique-current-owner-unavailable"),
                ("currentOwner.Pointer != _owner.Pointer", "current-owner-pointer-changed"),
                ("!IdentityMatches(stage)", None),
                ("_actor.gameObject.scene.handle != _sceneHandle", "actor-scene-changed"),
                ("!_actor.transform.IsChildOf(_matchRoot.transform)", "actor-outside-match-root"),
                ("roi.ByteCount != _roi.ByteCount", "roi-byte-count-changed"),
                ("roi.Y != _roi.Y", "roi-row-changed"),
                ("!OriginalsMatch(stage)", None),
                ("_indexSource == null", "index-source-unavailable"),
                ("_indexSource.sharedMesh == null", "index-source-mesh-unavailable"),
                ("_indexSource.sharedMesh.GetInstanceID() != _sourceMeshId", "index-source-mesh-changed"),
                ("Id(_indexSource.rootBone) != _sourceRootBoneId", "index-source-root-bone-changed"),
                ("!CandidatePosePolicy.SameBoneOrder(_boneNames, _boneReferences, names, references)", "mesh-bone-order-changed"),
                ("!_boneParents.SequenceEqual(parents)", "mesh-bone-parents-changed"),
            ]),
            "identity": (identity, [
                ("_actor == null", "actor-unavailable"),
                ("_actor.Pointer != _actorPointer", "actor-pointer-changed"),
                ("_actor.gameObject.GetInstanceID() != _actorId", "actor-instance-changed"),
                ("_actor.ID != _playerId", "player-id-changed"),
                ("_target == null", "target-binding-unavailable"),
                ("_target.Player == null", "target-player-unavailable"),
                ("_target.Player.Pointer != _actorPointer", "target-player-pointer-changed"),
                ("_target.ActorInstanceId != _actorId", "target-actor-id-changed"),
                ("_target.PlayerId != _playerId", "target-player-id-changed"),
                ("_actor.EntityIndex != _entityIndex", "actor-entity-index-changed"),
                ("!_actor.IsActive", "actor-inactive"),
                ("!_actor.gameObject.activeInHierarchy", "actor-hierarchy-inactive"),
                ("_actor.IsGoalkeeper", "actor-is-goalkeeper"),
                ("_actor.ShirtNumber <= 0", "actor-shirt-number-invalid"),
                ("_actor.transform.GetInstanceID() != _actorTransformId", "actor-transform-changed"),
                ("Id(_actor.transform.parent) != _actorParentId", "actor-parent-changed"),
                ("visual == null", "visual-unavailable"),
                ("visual.Pointer != _visualPointer", "visual-pointer-changed"),
                ("!visual.gameObject.activeInHierarchy", "visual-hierarchy-inactive"),
                ("visual.IsSubstitute", "visual-is-substitute"),
                ("visual.IsMedic", "visual-is-medic"),
                ("visual.IsLoading", "visual-loading"),
                ("!visual.m_initialised", "visual-not-initialized"),
                ("visual.m_isDestroying", "visual-destroying"),
                ("visual.m_pluginShutDown", "visual-plugin-shut-down"),
                ("(ushort)visual.m_characterID != _characterId", "visual-character-id-changed"),
                ("visual.m_skeleton == null", "skeleton-unavailable"),
                ("visual.m_skeleton.Pointer != _skeletonPointer", "skeleton-pointer-changed"),
                ("visual.m_skeleton.LOD.ToString() != _lod", "skeleton-lod-changed"),
                ("!ids.IsCreated", "identity-table-not-created"),
                ("ids.Length != _entities", "identity-table-length-changed"),
                ("_entityIndex < 0", "entity-index-negative"),
                ("_entityIndex >= ids.Length", "entity-index-out-of-range"),
                ("left == null", "visual-native-wrapper-unavailable"),
                ("right == null", "owner-native-wrapper-unavailable"),
                ("left.instance.ToInt64() != _nativeService", "native-service-token-changed"),
            ]),
            "originals": (originals, [
                ("current.Length != _originals.Count", "original-renderer-count-changed"),
                ("renderer == null", "original-renderer-unavailable"),
                ("!ids.Contains(item.Id)", "original-renderer-set-changed"),
                ("renderer.enabled != item.Enabled", "original-renderer-enabled-changed"),
                ("renderer.gameObject.activeInHierarchy != item.Active", "original-renderer-active-changed"),
                ("MeshId(renderer) != item.MeshId", "original-renderer-mesh-changed"),
                ("!item.MaterialIds.SequenceEqual(materialIds)", "original-renderer-materials-changed"),
                ("!item.ShaderIds.SequenceEqual(shaderIds)", "original-renderer-shaders-changed"),
            ]),
        }
        for label, (body, predicates) in ordered.items():
            cursor = 0
            for condition, code in predicates:
                with self.subTest(guard=label, predicate=condition):
                    marker = f"if ({condition})"
                    where = body.index(marker, cursor)
                    next_line = body[where:].split("\n", 2)
                    if code is not None:
                        self.assertIn(f'RejectContext("{code}", stage', "\n".join(next_line[:2]))
                    cursor = where + len(marker)
        self.assertIn("if (_geometryAttempted)", context)
        self.assertIn("_originalFlagsUnchanged = false", originals)
        self.assertLess(identity.index("!ids.IsCreated"), identity.index("ids[_entityIndex]"))
        self.assertLess(identity.index("_entityIndex >= ids.Length"), identity.index("ids[_entityIndex]"))
        self.assertLess(context.index("!IdentityMatches(stage)"), context.index("_actor.gameObject.scene.handle"))
        self.assertIn('ContextMatches("start")', source)
        self.assertEqual(source.count('ContextMatches("update")'), 4)
        self.assertIn('ContextMatches("late")', source)
        self.assertNotIn('ContextMatches()', source)
        for code in ("display-camera-unavailable", "display-camera-disabled", "display-camera-inactive",
                     "display-camera-not-game", "display-camera-target-texture", "display-camera-layer-excluded"):
            self.assertIn(f'RejectContext("{code}", "late")', source)
        stop = source.split("public void Stop(string reason)", 1)[1].split("public object Describe()", 1)[0]
        self.assertNotIn("_contextGuard =", stop)
        self.assertNotIn('Stop("scene-changed")', source)

    def test_start_context_rejection_and_version_are_explicit(self):
        root = Path(__file__).parents[1]
        source = (root / "src/CandidatePoseOverlay.cs").read_text()
        start = source.split("public void Start(", 1)[1].split("public void Tick()", 1)[0]
        for code in ("active-scene-not-match-playback", "start-match-root-outside-active-scene",
                     "start-actor-outside-active-scene", "actor-outside-match-root",
                     "controller-unavailable", "unique-current-owner-unavailable", "visual-unavailable",
                     "skeleton-unavailable", "visual-native-wrapper-unavailable", "owner-native-wrapper-unavailable",
                     "texture-unavailable"):
            self.assertIn(f'RejectStartContext("{code}")', start)
        camera = source.split("private Camera SelectDisplayCamera()", 1)[1].split("private void Track(", 1)[0]
        for code in ("display-camera-inventory-unavailable", "display-camera-inventory-unbounded",
                     "display-camera-no-usable-candidate", "display-camera-candidates-ambiguous"):
            self.assertIn(f'RejectContext("{code}", "start"', camera)
        self.assertLess(camera.index("cameras is null"), camera.index("cameras.Length > 32"))
        self.assertIn('eligible.Length == 1', camera)
        self.assertNotIn("Publish", camera)
        for path in root.joinpath("src").glob("*.cs"):
            self.assertNotIn('"0.2.8"', path.read_text(), str(path))
        self.assertIn('<Version>0.10.7</Version>', (root / "FM26.VisualDiagnostics.csproj").read_text())  # H166 version bump; H168: 0.6.1
        self.assertIn('Version = "0.2.12"', source)

    def test_successful_late_render_retains_bounded_timing_rows_without_per_frame_io(self):
        # Source-shape regression only: not gameplay, publication or fidelity proof.
        source = (Path(__file__).parents[1] / "src/CandidatePoseOverlay.cs").read_text()
        late = source.split("public void LateTick()", 1)[1].split("private Match3DAnimation FindOwner()", 1)[0]
        publish = source.split("private void Publish()", 1)[1].split("private void SafePublish()", 1)[0]
        self.assertIn("private const int MaximumTimelineRows = 2048;", source)
        self.assertIn("private readonly List<object> _timeline = new();", source)
        self.assertIn("if (_timeline.Count < MaximumTimelineRows)", late)
        self.assertIn("else _droppedTimelineRows++;", late)
        self.assertIn("_timeline.Add(new", late)
        self.assertLess(late.index("_ownedRoot.SetActive(true);"), late.index("_timeline.Add(new"))
        self.assertLess(late.index("_lastRenderRoot = RootMetadata"), late.index("_timeline.Add(new"))
        self.assertLess(late.index("_timeline.Add(new"), late.index("if (firstRender)"))
        self.assertIn("var renderedSample = _samples[^1];", late)
        for field in (
            "Frame = Time.frameCount", "QpcTicks = Stopwatch.GetTimestamp()",
            "QpcFrequency = Stopwatch.Frequency", "TimestampUtc = DateTime.UtcNow",
            "SampleSequence = renderedSample.Sequence", "SubmissionFrame = renderedSample.SubmissionFrame",
            "CompletionFrame = renderedSample.CompletionFrame",
            "HeldAgeFrames = Time.frameCount - renderedSample.CompletionFrame",
            "OriginalRoot = _lastRenderRoot", "CameraInstanceId = _displayCameraId",
            "Position = Position(_displayCamera.transform.position)",
            "Rotation = Rotation(_displayCamera.transform.rotation)",
            "ScreenWidth = Screen.width", "ScreenHeight = Screen.height",
            "DisplayOffset = new {X=_displayOffset.X,Y=_displayOffset.Y,Z=_displayOffset.Z}",
            "UnshiftedCandidateScreen = unshifted.Select(p => Position(_displayCamera.WorldToScreenPoint(p))).ToArray()",
            "OwnedDisplayScreen = world.Select(p => Position(_displayCamera.WorldToScreenPoint(p))).ToArray()",
            "NativePublicationVerified = false", "PoseFidelityVerified = false",
        ):
            self.assertIn(field, late)
        self.assertIn("WorldToCameraMatrix = MatrixElements(_displayCamera.worldToCameraMatrix)", late)
        self.assertIn("ProjectionMatrix = MatrixElements(_displayCamera.projectionMatrix)", late)
        self.assertIn("QpcEndTicks = Stopwatch.GetTimestamp()", late)
        self.assertIn("QpcIsHighResolution = Stopwatch.IsHighResolution", publish)
        self.assertEqual(late.count("Publish();"), 1, "Keep publication inside existing first-render branch")
        for forbidden in ("_write", "File.", "Directory.", "Take(", "_timeline.Clear", "_timeline.Remove"):
            self.assertNotIn(forbidden, late)
        self.assertIn('_write?.Invoke("candidate-overlay-timeline.json", new', publish)
        for field in ("MaximumTimelineRows", "RetainedRowCount = _timeline.Count",
                      "DroppedRowCount = _droppedTimelineRows", "Rows = _timeline.ToArray()",
                      "QpcFrequency = Stopwatch.Frequency", "NativePublicationVerified = false",
                      "PoseFidelityVerified = false", "DisplayOffsetIsPoseCorrection = false"):
            self.assertIn(field, publish)
        stop = source.split("public void Stop(string reason)", 1)[1].split("public object Describe()", 1)[0]
        self.assertGreaterEqual(stop.count("SafePublish();"), 2, "Stop and later cleanup both export retained evidence")
        self.assertNotIn("_timeline.Clear", source)


if __name__ == "__main__": unittest.main()
