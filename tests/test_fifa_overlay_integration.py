"""H154 stage 1: source wiring of the opt-in FIFA overlay mode (source-bound checks; runtime behaviour is NOT certified here)."""
import sys
import unittest
from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]
sys.path.insert(0, str(Path(__file__).resolve().parent))
from multi_pass_legacy_projection import FIFA_SEAMS, remove_fifa_seams  # noqa: E402

BEFORE = ROOT / "local/spike-154-fm-overlay-stage1/before/src/DiagnosticsPlugin.cs"


class FifaOverlayWiringTests(unittest.TestCase):
    def setUp(self):
        self.plugin = (ROOT / "src/DiagnosticsPlugin.cs").read_text(encoding="utf-8")
        for name in ("FifaOverlay.cs", "FifaOverlayHost.cs"):
            self.assertTrue((ROOT / "src" / name).exists(), f"src/{name} missing")
        self.overlay = (ROOT / "src/FifaOverlay.cs").read_text(encoding="utf-8")
        self.host = (ROOT / "src/FifaOverlayHost.cs").read_text(encoding="utf-8")

    @unittest.skipUnless(BEFORE.exists(), "pre-H154 caller snapshot absent")
    def test_plugin_changes_are_only_declared_additive_seams(self):
        before = BEFORE.read_text(encoding="utf-8")
        self.assertEqual(remove_fifa_seams(self.plugin), before)
        for anchor, addition in FIFA_SEAMS:
            for label, mutated in (("missing", self.plugin.replace(addition, "", 1)),
                                   ("duplicate", self.plugin.replace(addition, addition + addition, 1))):
                with self.assertRaises(ValueError, msg=label + addition):
                    remove_fifa_seams(mutated)
        moved = self.plugin.replace(FIFA_SEAMS[3][1], "", 1).replace(FIFA_SEAMS[4][0], FIFA_SEAMS[4][0] + FIFA_SEAMS[3][1], 1)
        with self.assertRaises(ValueError):
            remove_fifa_seams(moved)
        with self.assertRaises(ValueError):
            remove_fifa_seams(self.plugin.replace("_candidateOverlay != null || _prototype != null, WriteJson", "false, WriteJson"))
        # Frozen legacy callbacks: no FIFA reference inside Update/LateUpdate/OnDestroy bodies.
        for callback in ("public void LateUpdate()", "public void OnDestroy()"):
            start = self.plugin.index(callback)
            body = self.plugin[start:self.plugin.index("\n    }\n", start)]
            self.assertNotIn("Fifa", body)
        update = self.plugin[self.plugin.index("public void Update()"):self.plugin.index("var now = DateTime.UtcNow;")]
        self.assertNotIn("Fifa", update)

    def test_default_off_command_file_mode(self):
        h = self.host
        self.assertIn('"fifa-overlay-command.json"', h)
        self.assertIn('"fifa-overlay-request-result.json"', h)
        self.assertIn("FifaOverlayPolicy.ParseRequest(", h)
        self.assertEqual(h.count("new FifaOverlay()"), 1)
        command = h[h.index("private static void Command("):]
        self.assertIn("new FifaOverlay()", command)
        # H191: one attempt per session became one CLEAN segment at a time (pure FifaOverlaySegments.CanStart, PolicyTests).
        self.assertLess(command.index("if (!FifaOverlaySegments.CanStart(_overlay != null,"), command.index("new FifaOverlay()"), "segment gate before any overlay")
        self.assertLess(command.index("_segments++;"), command.index("_overlay.Start("))
        self.assertLess(command.index("if (otherModesBlocked)"), command.index("new FifaOverlay()"))
        self.assertLess(command.index("if (candidateOrPrototypeSession)"), command.index("new FifaOverlay()"))
        self.assertIn("matches.Length != 1 || !matches[0].Eligible", command)
        self.assertIn("4096", command, "bounded command size")
        self.assertIn("finally { File.Delete(path); }", command)

    def test_lifecycle_wiring(self):
        h, p = self.host, self.plugin
        self.assertIn("public void Update() => FifaOverlayHost.CleanupTick();", h)
        self.assertIn("public void LateUpdate() => FifaOverlayHost.Tick();", h)
        self.assertIn("public void OnDestroy() => FifaOverlayHost.Shutdown();", h)
        self.assertIn("AddComponent<FifaOverlayBehaviour>();", p)
        self.assertIn('FifaOverlayHost.Stop("match-root-changed");', p)
        self.assertIn('FifaOverlayHost.Stop("diagnostic-error-limit");', p)
        self.assertIn("|| _multiPass.Blocked || FifaOverlayHost.Blocked;", p, "other modes blocked during overlay/cleanup")
        self.assertIn("public static bool Blocked => _overlay?.OverlapActive ?? false;", h)

    def test_overlay_safety_patterns(self):
        o = self.overlay
        compact = o.replace(" ", "")
        for forbidden in ("ComputeShader", "GetTransforms(", "GetRawTextureData", "m_worldTransforms", "CreatePrimitive(",
                          ".enabled =", ".enabled=", "Process.Start"):
            self.assertNotIn(forbidden, o, f"forbidden in overlay: {forbidden}")
        # H160 evolution: forceRenderingOff is WRITTEN only by the opt-in HideNative/RestoreNative pair (see test_h160_*).
        self.assertEqual(compact.count(".forceRenderingOff="), 2, "exactly one hide write and one restore write site")
        for line in o.splitlines():
            if "SetActive(" in line:
                self.assertIn("_ownedRoot.SetActive(", line, "only the owned root may change activity")
        self.assertIn("FifaOverlayPolicy.ValidateHeader(", o)
        self.assertIn("FifaOverlayPolicy.RequirePayload(", o)
        self.assertLess(o.index("FifaOverlayPolicy.RequirePayload("), o.index("new GameObject("), "pack integrity before allocation")
        self.assertLess(o.index("LoadPack(request.PackDirectory"), o.index("CreateOwned();"))
        for allocation in ("_ownedRoot=newGameObject(", "_mesh=newMesh()", "varmaterial=newMaterial("):  # H160 evolution: one material per piece
            self.assertIn("RequireWindow();" + allocation, compact)
        self.assertIn("FifaOverlayPolicy.FlagsUnchanged(Flags())", o)  # H168 evolution: typed check, same verdict as OriginalPassPolicy.OriginalFlagsUnchanged (PolicyTests)
        self.assertIn("FifaOverlayPolicy.CanCertifyCleanup(", o)
        self.assertIn("if(frame<=_lifecycle.CleanupFrame)return;", compact, "later-frame cleanup verification")  # H156 evolution: field moved into FifaOverlayLifecycle
        self.assertIn("Object.Destroy(", o)
        self.assertIn("NativeFifaTiming=false", compact)
        self.assertIn("NativePlayerHidden=_hide.Hidden", compact)  # H160 evolution: literal false while never hidden
        self.assertIn("FifaOverlayPolicy.Heuristics", o)

    def test_h156_report_refused_before_publish_obligation_and_never_overwritten(self):
        h, o = self.host, self.overlay
        command = h[h.index("private static void Command("):]
        guard = command.index("File.Exists(Path.Combine(DiagnosticsPlugin.Output, FifaOverlayReport.FileName(request.RequestId)))")
        self.assertLess(guard, command.index("new FifaOverlay()"), "B1: refuse before the attempt/publish obligation exists")
        start = o[o.index("public void Start("):o.index("private string ReportPath")]
        self.assertIn("_lifecycle.Arm(File.Exists(ReportPath));", start)
        self.assertLess(start.index("_lifecycle.Arm("), start.index("_thread ="), "arm (report check) is the first action")
        self.assertIn("if (_lifecycle.Armed) Stop(\"start-failure\", error.Message);", start, "no publication for a refused arm")
        self.assertIn("FifaOverlayReport.WriteNew(ReportPath,", o)
        self.assertNotIn('_write($"fifa-overlay-', o, "final report must not use the overwriting WriteJson")
        self.assertIn("_request.Session", o[o.index("private void Publish("):], "report binds the request session")

    def test_h156_exclusion_held_until_verified_published_cleanup(self):
        o = self.overlay
        self.assertIn("public bool OverlapActive => _lifecycle.Blocked;", o)
        self.assertIn("_lifecycle.ShouldPublish(", o)
        self.assertIn("_lifecycle.RecordPublication(cleanup.Verified, written);", o)
        self.assertNotIn("_finalized", o, "no separate finalized flag may release the exclusion")

    def test_h156_declared_stand_pose_and_hysteresis_wiring(self):
        o = self.overlay
        self.assertIn("FifaOverlayPolicy.SelectClip(_motion.Speed, _clip)", o, "hysteresis uses the previously selected clip")  # H158 evolution: speed now from the match-time tracker
        self.assertIn('private string _clip = "stand",', o)  # H166 evolution: per-actor field declaration list
        self.assertIn('FillVertices("stand", FifaOverlayPolicy.StandFrame);', o, "initial pose is the declared stand pose")
        self.assertIn('_s.Frames(clip == "stand" ? _s.StandClip : clip)', o)  # H166 evolution: shared pack frames via the session; H171: stand clip per pack format
        self.assertIn('internal string StandClip => _pack.FormatVersion == 2 ? "idle" : FifaOverlayPolicy.StandClip;', o)  # H171: v1 stand unchanged (walk_F2 key 0)
        self.assertNotIn('"rest"', o, "T-pose rest frame is never displayed")
        self.assertIn("FifaOverlayPolicy.JogEnterAtMetersPerSecond", o, "report logs the hysteresis thresholds")
        self.assertIn("FifaOverlayPolicy.JogLeaveBelowMetersPerSecond", o)

    def test_h158_speed_and_phase_in_match_time(self):
        o = self.overlay
        start = o[o.index("public void Start("):o.index("private string ReportPath")]
        self.assertIn("_publisher = _controller.m_match;", start, "same publisher field the semantic observer already reads")
        self.assertLess(start.index("_publisher = _controller.m_match;"), start.index("CreateOwned();"))
        # H166 evolution: ONE start clock read shared by every actor's FifaMatchMotion, before any owned allocation.
        self.assertIn("double startClock = ReadMatchTime();", start, "match clock read before any owned allocation")
        self.assertLess(start.index("double startClock = ReadMatchTime();"), start.index("a.Begin(_pack.RestProxyHeight, startClock);"))
        self.assertLess(start.index("a.Begin(_pack.RestProxyHeight, startClock);"), start.index("CreateOwned();"))
        self.assertIn("_motion = new FifaMatchMotion(startClock,", self.am("public void Begin("))
        ctx = (ROOT / "src/FifaOverlaySession.cs").read_text(encoding="utf-8")  # H206 evolution: predicates in the shared SessionRefusal
        self.assertIn("if (_publisher == null) return", ctx)
        self.assertIn("if (_controller.m_match != _publisher) return", ctx, "publisher identity bound to the controller")
        read = o[o.index("private double ReadMatchTime()"):]
        read = read[:read.index("\n    }\n")]
        self.assertIn("_publisher.m_matchFrame", read)
        self.assertIn("._CurrentTime_k__BackingField", read)
        self.assertIn("if (frame == null)", read, "absent published frame fails closed")
        tick = self.method("public void Tick()")
        pose = self.am("public void WritePose(")  # H166 evolution: per-actor motion/pose methods
        self.assertIn("_motion.Observe(matchTime, FifaOverlay.ToN(_position));", self.am("public void Observe("))
        self.assertIn("if (!_motion.Paused && !discontinuity)", pose, "pause: clip/phase held, no stop")  # H164 evolution: also held on a discontinuity frame
        self.assertIn("FifaOverlayPolicy.SelectClip(_motion.Speed, _clip)", pose, "hysteresis uses the previous clip, speed in match time")
        self.assertIn("FifaOverlayPolicy.AdvancePhase(_phase, _motion.MatchDt, rate, Keys(clip))", pose, "animation advances in match time")
        self.assertIn("if (dt < 0 || dt > FifaOverlayPolicy.MaximumStepSeconds)", tick, "wall stall/backwards bound kept for every clip")
        self.assertNotIn("_samples", o, "the wall-time averaged-speed window is gone")
        for forbidden in ("timeScale", "deltaTime", "m_matchFrame =", "_k__BackingField =", "_k__BackingField="):
            self.assertNotIn(forbidden, o, f"forbidden in overlay: {forbidden}")

    def test_h158_report_declares_time_source_and_match_rows(self):
        o = self.overlay
        policy = (ROOT / "src/FifaOverlayPolicy.cs").read_text(encoding="utf-8")
        publish = o[o.index("private void Publish("):o.index("public object Describe()")]
        self.assertIn("PlaybackSpeedSource = FifaOverlayPolicy.PlaybackSpeedSource,", publish)
        self.assertIn('public const string PlaybackSpeedSource = "fm-published-match-time:', policy)
        self.assertIn("MatchFrame.CurrentTime", policy[policy.index("public const string PlaybackSpeedSource"):][:400])
        for threshold in ("FifaOverlayPolicy.MaximumFrameDisplacementMeters", "FifaOverlayPolicy.MaximumPlausibleSpeed",
                          "FifaOverlayPolicy.MaximumMatchStepSeconds", "FifaOverlayPolicy.MaximumStepSeconds"):
            self.assertIn(threshold, publish, "report logs the declared stop bounds")
        tick = self.am("public void WritePose(")  # H166 evolution: rows written per actor
        row = tick[tick.index("_timeline.Add("):]
        row = row[:row.index(";")]
        for field in ("WallDt = dt", "MatchTimeSeconds = matchTime", "MatchDt = _motion.MatchDt", "MatchSpeedMetersPerSecond = _motion.Speed",
                      "MatchSpeedWindowSeconds = _motion.WindowSeconds", "FrameDisplacementMeters = _motion.FrameDisplacement",
                      "Paused = _motion.Paused", "PublishedPlaybackSpeed = speed.Value", "PublishedPlaybackSpeedNonFinite = speed.NonFinite"):  # H160 evolution
            self.assertIn(field, row)
        self.assertNotIn("SpeedMetersPerSecond = _motion", row.replace("MatchSpeedMetersPerSecond", ""), "no ambiguous wall-named speed")
        heuristics = policy[policy.index("public static readonly string[] Heuristics"):]
        heuristics = heuristics[:heuristics.index("};")]
        self.assertIn("MATCH seconds", heuristics)
        self.assertIn("> max(3 m, 15 m/s x MATCH dt)", heuristics)  # H234: per-frame bound scaled with match dt (was "> 3 m")

    def method(self, signature):
        o = self.overlay
        body = o[o.index(signature):]
        return body[:body.index("\n    }\n")]

    def am(self, signature):  # H166 evolution: method of the per-actor class FifaOverlayActor
        o = self.overlay
        start = o.index("internal sealed class FifaOverlayActor")
        body = o[o.index(signature, start):]
        return body[:body.index("\n    }\n")]

    def test_h160_hide_is_opt_in_reversible_and_force_rendering_off_only(self):
        o = self.overlay
        compact = o.replace(" ", "")
        hide, restore = self.method("private void HideNative()"), self.method("private void RestoreNative(")  # H168 evolution: RestoreNative(bool actorDeparted = false)
        self.assertIn(".forceRenderingOff = true;", hide)
        self.assertIn(".forceRenderingOff = entry.BeforeForceRenderingOff;", restore, "restore writes the recorded before value")
        for line in o.splitlines():
            if ".forceRenderingOff =" in line:
                self.assertTrue(line in hide or line in restore, line)
        self.assertLess(hide.index("_hide.BeginHide("), hide.index(".forceRenderingOff = true;"), "write intent recorded before the native write")
        self.assertLess(hide.index("RequireWindow();"), hide.index(".forceRenderingOff = true;"), "deadline checked before each hide write")
        self.assertIn("_hide.ConfirmHide(", hide, "hide read back")
        self.assertIn("_hide.NeedsHide(", hide, "already-off renderers untouched")
        self.assertLess(restore.index("_hide.BeginRestore("), restore.index(".forceRenderingOff = entry.BeforeForceRenderingOff;"))
        self.assertNotIn("RequireWindow", restore, "restoration is a cleanup write, allowed after the deadline")
        start = self.method("public void Start(")
        capture = self.am("public void Capture(")  # H166 evolution: per-actor capture called from Start
        self.assertIn("if (request.HideNativePlayer) _hide.Record(", capture, "before-state captured only when hiding is requested")
        self.assertLess(start.index("a.Capture("), start.index("CreateOwned();"), "exact before-state captured before any write")
        tick = self.am("public void WritePose(")
        shown = tick.index("_shown = true;")
        self.assertIn("if (_s.Request.HideNativePlayer && !_hide.Hidden) HideNative();", tick)
        self.assertLess(shown, tick.index("HideNative();"), "native hidden only after the FIFA objects are created and shown")
        session_stop = self.method("public void Stop(")
        self.assertLess(session_stop.index("_lifecycle.RequestStop("), session_stop.index("a.StopSession("), "every stop path (first cause latched) restores every actor")
        stop = self.am("public void StopSession(")
        self.assertEqual(stop.count("\n        RestoreNative();"), 1, "uncommented, unconditional restore statement in Stop")
        self.assertLess(stop.index("RestoreNative();"), stop.index("Object.Destroy("), "restore before destroying owned objects")
        self.assertLess(stop.index("RestoreNative();"), stop.index("_ownedRoot.SetActive(false)"), "native back before the overlay disappears")
        cleanup = self.method("private CleanupResult ReadCleanup(")
        self.assertIn("a.ReadRestoration();", cleanup, "every actor's restoration read back on the verification frame")
        self.assertIn("_hide.RecordRestoreReadback(", self.am("public void ReadRestoration("))
        verified = cleanup[cleanup.index("Verified = "):]
        verified = verified[:verified.index("NativeRestorationVerified")]
        self.assertIn("&& restored,", verified, "cleanup is certified only with verified restoration of every actor")
        self.assertIn("restored &= a.RestorationVerified;", cleanup)
        publish = self.method("private void Publish(")
        self.assertIn("HiddenRenderers = single?.HiddenRenderers", publish)
        self.assertIn("NativePlayerHidden = _hide.Hidden, HiddenRenderers = _hide.Entries, Cleanup = _endCleanup", self.overlay)
        self.assertIn("HideNativePlayer = _request.HideNativePlayer", publish)
        flags = self.am("public FifaRendererFlagReport[] Flags()")
        self.assertIn("FifaOverlayPolicy.RendererFlagStatus(", flags)
        self.assertIn("_s.Request.HideNativePlayer ?", flags, "forceRenderingOff read only in hide runs (default path reads unchanged)")
        self.assertNotIn("SetActive(", hide + restore, "never touches GameObject active state")
        self.assertIn("HideNativePlayer", self.host[self.host.index("Scope ="):][:300], "request result scope reflects the opt-in")

    def test_h160_fm_view_change_is_a_normal_end_only_after_a_guard_refusal(self):
        o = self.overlay
        self.assertIn("internal sealed class FifaGuardException : InvalidOperationException", o)
        ctx, flags, session = self.am("public void RequireContext(string where)"), self.am("public void RequireFlags()"), (lambda s: s[:s.index("\n    }\n")])((lambda t: t[t.index("internal void RequireSession()"):])((ROOT / "src/FifaOverlaySession.cs").read_text(encoding="utf-8")))  # H209: moved
        for body in (ctx, flags, session):
            self.assertIn("throw new FifaGuardException(", body)
        tick = self.method("public void Tick()")
        catch = tick[tick.index("catch (Exception error)"):]
        # H166 evolution: per-actor refusal classified by the pure FifaActorScope (which calls ClassifyStop with that actor's renderers).
        self.assertIn("FifaActorScope.OnActiveFailure(_request.Multi, session?.Message, error is TimeoutException, error is FifaGuardException, StructureIntact(), a.ObserveRenderers(),", catch)  # H168 evolution: session refusal passed
        outer = tick[tick.rindex("catch (Exception error)"):]
        self.assertIn("FifaOverlayPolicy.ClassifyStop(error is TimeoutException, error is FifaGuardException, StructureIntact(), current?.ObserveRenderers(), error.Message)", outer)
        self.assertIn("Stop(stop.Reason, stop.Error);", outer, "view change goes through the same Stop/cleanup/report path")
        self.assertNotIn('"owned-update-failure"', tick, "classification only in the pure policy")
        multi = (ROOT / "src/FifaOverlayMulti.cs").read_text(encoding="utf-8")
        self.assertIn("var stop = FifaOverlayPolicy.ClassifyStop(timeout, guardRefusal, structureIntact, renderers, error);", multi)
        observe = self.am("public (bool, bool, bool?, bool?)[] ObserveRenderers()")
        for write in (".enabled =", "SetActive(", ".forceRenderingOff ="):
            self.assertNotIn(write, observe, "observation only reads")
        self.assertIn("catch { return null; }", observe, "dead/unreadable renderer observed as null (not a view change)")
        # H164 evolution: tri-state structure; only Intact counts as intact (unreadable/thread mismatch is not a view change).
        self.assertIn("private bool StructureIntact() => Structure() == FifaStructureState.Intact;", self.overlay)
        structure = self.method("private FifaStructureState Structure()")
        for check in ("_root.GetInstanceID() == _rootId", "SceneManager.GetActiveScene().handle == _scene", "_controller.GetInstanceID() == _controllerId", "_controller.m_match == _publisher"):
            self.assertIn(check, structure)
        self.assertIn("catch { return FifaStructureState.Unreadable; }", structure, "unreadable structure is not a view change")
        self.assertIn("if (Environment.CurrentManagedThreadId != _thread) return FifaStructureState.Unreadable;", structure, "thread mismatch is a failure, not a match end")

    def test_h160_one_submesh_and_material_per_piece_fixed_palette(self):
        o = self.overlay
        compact = o.replace(" ", "")
        load = self.method("private void LoadPack(")
        self.assertIn("_pieces = FifaOverlayPolicy.SplitPieces(_triangles, _pack.Pieces);", load, "piece ranges validated with the pack, before any allocation")
        create = self.am("public void CreateOwned()")  # H166 evolution: per-actor mesh, shared per-team materials
        shared = self.method("private void CreateSharedMaterials()")
        self.assertNotIn("_mesh.triangles =", create, "single-submesh upload replaced")
        self.assertIn("_s.RequireWindow(); _mesh.subMeshCount = pieces.Length;", create)
        self.assertIn("_s.RequireWindow(); _mesh.SetTriangles(pieces[k], k);", create)
        self.assertLess(create.index("_mesh.subMeshCount"), create.index("_mesh.SetTriangles("))
        self.assertIn("Reserve(1); RequireWindow(); var material = new Material(_shader);", shared, "budget + deadline before each material allocation")
        self.assertIn("var rgba = palette[_pack.Pieces[k].Role];", shared, "fixed declared palette (legacy) / team palette (multi)")
        multi = (ROOT / "src/FifaOverlayMulti.cs").read_text(encoding="utf-8")
        self.assertIn("multi ? For(teamId) : FifaOverlayPolicy.Palette", multi, "legacy N=1 keeps the H160 palette")
        self.assertIn("RequireWindow(); renderer.sharedMaterials = _s.Materials(FifaTeamPalettes.Key(_s.Request.Multi, _actor.TeamId, _actor.Goalkeeper));", create)  # H178a evolution
        self.assertLess(shared.index("Retain(material,"), shared.index("material.name ="), "wrapper retained before any write")
        for forbidden in ("Shader.Find", "new Shader", "LoadAsset", "AssetBundle", "Resources.Load"):
            self.assertNotIn(forbidden, o, f"no new shader assets: {forbidden}")
        self.assertIn("FifaHandleBudget.Require(_actors.Count, _pack.Pieces.Count, _teams.Length);", o)  # H166 evolution: declared budget
        publish = self.method("private void Publish(")
        self.assertIn("Pieces = _pack?.Pieces", publish)
        self.assertIn("Palette = FifaOverlayPolicy.Palette", publish)

    def test_h160_playback_speed_guarded_and_deadline_before_clock_read(self):
        tick = self.am("public void WritePose(")  # H166 evolution
        self.assertIn("var speed = FifaOverlayPolicy.ReportablePlaybackSpeed(_s.ReadPublishedPlaybackSpeed());", tick, "nonfinite literal -> null + flag, report never lost")
        self.assertLess(tick.index("var speed = "), tick.index("_timeline.Add("))
        # T4 (H159 surviving mutant): the monotonic deadline is checked immediately before the native match-clock read.
        clock = self.method("internal double FrameClock(")  # H168 evolution: internal (called by the actor resume)
        self.assertIn("RequireWindow(); _frameClock = ReadMatchTime();", clock)
        self.assertEqual(self.method("public void Tick()").count("ReadMatchTime()"), 0, "the per-frame clock is read only through FrameClock (once per frame, guarded)")
        self.assertEqual(self.overlay.count("ReadMatchTime()"), 3, "definition + Start (before allocation) + FrameClock")
        policy = (ROOT / "src/FifaOverlayPolicy.cs").read_text(encoding="utf-8")
        heuristics = policy[policy.index("public static readonly string[] Heuristics"):]
        self.assertIn("HideNativePlayer", heuristics[:heuristics.index("};")], "declared pose heuristic mentions the opt-in hide")

    def test_version_bumped_patch_h164(self):  # H162 evolution: 0.4.0 -> 0.5.0; H164: 0.5.0 -> 0.5.1; H166: 0.5.1 -> 0.6.0; H168: 0.6.1
        csproj = (ROOT / "FM26.VisualDiagnostics.csproj").read_text(encoding="utf-8")
        self.assertIn("<Version>0.10.7</Version>", csproj)  # H234: 0.10.6; H238: 0.10.7; H211: 0.10.5; H209: 0.10.4; H206: 0.10.3; H171: 0.7.1; H178a: 0.8.0; H186: 0.9.0; H186b: 0.10.0; H200: 0.10.1; H203: 0.10.2
        self.assertEqual(self.host.count('Version = "0.10.7"'), 2)
        self.assertEqual(self.overlay.count('Version = "0.10.7"'), 2)
        self.assertNotIn('"0.5.1"', self.host + self.overlay)
        self.assertNotIn('"0.5.0"', self.host + self.overlay)
        self.assertNotIn('"0.4.0"', self.host + self.overlay)
        self.assertNotIn('"0.3.2"', self.host + self.overlay)

    def test_h162_view_change_suspends_instead_of_stopping(self):
        tick = self.method("public void Tick()")
        catch = tick[tick.index("catch (Exception error)"):]
        # H166 evolution: the exact fm-view-changed condition (FifaActorScope SuspendActor) suspends THIS actor, only once created.
        suspend_line = "if (scope.Kind == FifaScopeKind.SuspendActor && a.Created)"
        self.assertIn(suspend_line, catch, "exact H160 fm-view-changed condition suspends (only with created owned objects)")
        self.assertIn("if (a.Suspend(frame, now)) continue;", catch)
        self.assertIn("scope = FifaActorScope.OnSuspendLimitCount(_request.Multi, session?.Message);", catch, "count bound -> suspend-limit (session in legacy N=1)")
        self.assertLess(catch.index(suspend_line), catch.index("Stop("), "anything else still stops")
        self.assertIn("if (Suspended) throw", (ROOT / "src/FifaOverlaySuspension.cs").read_text(encoding="utf-8"), "TryBegin refuses a second suspension")
        suspend = self.am("public bool Suspend(")
        self.assertIn("_suspension.TryBegin(frame, now, _lastMatchTime)", suspend)
        self.assertIn("RequireWindow(); _ownedRoot.SetActive(false); _visible = false;", suspend, "deadline before the owned-root write; owned root only")
        for forbidden in (".forceRenderingOff", "RestoreNative", "ReadMatchTime", "_hide.", "_lifecycle."):
            self.assertNotIn(forbidden, suspend, f"suspension leaves natives/ledger/lifecycle untouched: {forbidden}")
        self.assertNotIn("_suspension.TryBegin", tick, "suspension entered only through the actor's Suspend()")

    def test_h162_suspended_frames_deadline_first_and_no_native_writes(self):
        tick = self.method("public void Tick()")
        # H166 evolution: suspended actors skip the active guards (branch) and are decided by the pure policy per actor.
        branch = "if (!a.Running || a.Suspended) continue;"
        self.assertIn(branch, tick)
        self.assertLess(tick.index("Stop(\"stop-time-budget\"); return; }"), tick.index(branch), "deadline checked unchanged before the suspended branch")
        self.assertLess(tick.index("if (frame == _lastFrame) return;"), tick.index(branch))
        self.assertLess(tick.index(branch), tick.index("a.Guard();"), "guards of the active path unchanged after the branch")
        self.assertIn('_obs.Stage = "guards"; RequireContext("tick"); RequireOwned(); RequireFlags();', self.am("public void Guard()"))  # H209: caller stage
        st = self.am("public (FifaSuspendedDecision Decision, string Reason, string Error) Decide(") + self.am("public void Resume(")
        self.assertIn("FifaViewSuspension.Decide(structure, ActorPresent(), ObserveSuspended(), (double)(now - _suspension.Records[^1].StartQpcTicks) / Stopwatch.Frequency)", st)  # H164 evolution: tri-state
        self.assertIn("a.Decide(Structure(), now)", tick)
        self.assertIn("if (scope.Kind == FifaScopeKind.Stay) continue;", tick, "a staying actor falls through to the frame end")
        stay_end = tick[tick.index("if (scope.Kind == FifaScopeKind.Stay) continue;"):]
        self.assertIn("_lastFrame = frame; _lastQpc = now;", stay_end, "H162 mutant P8: every processed (also fully suspended) frame refreshes the wall reference")
        for forbidden in (".forceRenderingOff =", ".enabled =", "SetActive(true)", "HideNative();", "_hide.Begin"):
            self.assertNotIn(forbidden, st, f"no native/hide write while suspended: {forbidden}")
        self.assertNotIn('"owned-update-failure"', st, "classification only in the pure policy")
        self.assertIn("_failing = a; Stop(scope.Reason, scope.Error);", tick)
        observe = self.am("private (bool, bool, bool?, bool?, bool?, bool?)[] ObserveSuspended()")
        self.assertIn("_s.Request.HideNativePlayer ?", observe, "forceRenderingOff read only in hide runs")
        self.assertIn("_hide.ExpectedForceRenderingOff(", observe)
        for write in (".enabled =", "SetActive(", ".forceRenderingOff ="):
            self.assertNotIn(write, observe)
        actor = self.am("private bool? ActorPresent()")  # H171 evolution (H169 suggestion 4): unreadable presence = null => failure
        self.assertIn("catch { return null; }", actor)
        self.assertIn("_actor.Player != null", actor)

    def test_h162_resume_guards_then_rebaseline_then_show_on_next_pose(self):
        st = self.am("public void Resume(")  # H168 (L1): 0.5.1 order restored — session, actor guards, THEN the clock read
        resume = st
        order = ["if (session != null) throw session;", "RequireContext(\"resume\"); RequireOwned(); RequireFlags();", "double matchTime = _s.FrameClock(frame);",
                 "RequireWindow(); var position = _actorTransform.position;", "_motion.Rebaseline(matchTime, FifaOverlay.ToN(position));",
                 "_suspension.Resume(frame, now, Stopwatch.Frequency, matchTime);"]
        for a, b in zip(order, order[1:]):
            self.assertIn(a, resume); self.assertIn(b, resume)
            self.assertLess(resume.index(a), resume.index(b), f"{a} before {b}")
        self.assertEqual(st.count("ReadMatchTime()"), 0)
        self.assertEqual(st.count("FrameClock("), 1, "one guarded clock read, after every guard")
        self.assertIn("if (scope.Kind == FifaScopeKind.Resume) { a.Resume(frame, now, session); _settle.MarkResume(now); continue; }", self.method("public void Tick()"),
                      "the frame's session refusal is handed to the resume; the clock is read inside, after the guards")
        tick = self.am("public void WritePose(")
        self.assertIn("if (!_visible) { RequireWindow(); _ownedRoot.SetActive(true); _visible = true; _shown = true; }", tick,
                      "owned root re-shown only after the next full guarded pose update")
        self.assertIn("_lastMatchTime = matchTime;", tick)
        self.assertNotIn("_motion = new FifaMatchMotion(", tick + st, "re-baseline goes through the tested pure method")

    def test_h162_stop_while_suspended_and_bounded_report(self):
        session_stop = self.method("public void Stop(")
        self.assertIn("_globalSuspension.CloseOnStop(_stoppedFrame, _stopped, Stopwatch.Frequency, reason);", session_stop)
        self.assertLess(session_stop.index("_lifecycle.RequestStop("), session_stop.index("a.StopSession(reason, _stoppedFrame, _stopped);"))
        stop = self.am("public void StopSession(")  # H166 evolution: per-actor close + restore
        self.assertIn("_suspension.CloseOnStop(frame, now, Stopwatch.Frequency, reason);", stop)
        self.assertLess(stop.index("_suspension.CloseOnStop("), stop.index("RestoreNative();"))
        self.assertIn("if (_overlay == null || !_overlay.Active || _overlay.RequestId != request.RequestId)", self.host, "stop command unchanged: Active stays true while suspended")
        publish = self.method("private void Publish(")
        for field in ("Suspensions = single != null ? single.SuspensionRecords : _globalSuspension.Records", "SuspensionCount = single != null ? single.SuspensionCount : _globalSuspension.Count",
                      "MaximumSuspensions = FifaViewSuspension.MaximumSuspensions",
                      "MaximumSuspendedSeconds = FifaViewSuspension.MaximumSuspendedSeconds", "Timeline = single?.TimelineRows", "TimelineRowsDropped = single?.TimelineDropped",
                      "TimelineHeadRows = FifaReportBudget.HeadRows(_request.Multi)", "TimelineTailRows = FifaReportBudget.TailRows(_request.Multi)"):
            self.assertIn(field, publish)  # H166 evolution: legacy N=1 shape from the single actor; multi = global lists + Actors[]
        self.assertIn("new FifaOverlayActor(this, actor, FifaReportBudget.HeadRows(request.Multi), FifaReportBudget.TailRows(request.Multi), _actors.Count < FifaMotionTrace.MaximumActors)", self.overlay)
        self.assertIn("_timeline = new FifaTimelineSampler(headRows, tailRows);", self.overlay)
        self.assertIn("_timeline.Add(new FifaTimelineRow {", self.overlay)
        self.assertNotIn("_rowsDropped", self.overlay)
        self.assertNotIn("MaximumRows", self.overlay)
        describe = self.method("public object Describe()")
        self.assertIn("Suspended = _actors.Any(a => a.Running && a.Suspended)", describe)  # H166 evolution
        self.assertIn("_actors[0].SuspensionCount : _globalSuspension.Count", describe)
        policy = (ROOT / "src/FifaOverlayPolicy.cs").read_text(encoding="utf-8")
        heuristics = policy[policy.index("public static readonly string[] Heuristics"):]
        heuristics = heuristics[:heuristics.index("};")]
        self.assertIn("suspend", heuristics, "declared suspend/resume heuristic")


    # ---- H164 (0.5.1): FM clock discontinuities without a cut (goal replay / dead-time skip), exact stop row, H163 gaps ----
    def test_h164_discontinuity_classified_after_full_guards_and_clock_read(self):
        tick = self.method("public void Tick()")  # H166 evolution: ONE clock => one classification/record per frame, all active actors re-baselined
        order = ["a.Guard();", "double matchTime = FrameClock(frame);",
                 "var step = FifaTimeDiscontinuities.Classify(_clock.Value, matchTime);",
                 "bool settling = _settle.InWindow(now, Stopwatch.Frequency);",
                 "bool discontinuity = FifaTimeDiscontinuities.IsDiscontinuity(step, active.Max(a => a.DisplacementNow()), settling);",
                 "if (discontinuity)", "foreach (var a in active) a.Rebaseline(matchTime);", "else foreach (var a in active) { current = a; a.Observe(matchTime); }",
                 "a.WritePose(frame, now, dt, matchTime, discontinuity);"]
        for a, b in zip(order, order[1:]):
            self.assertIn(a, tick); self.assertIn(b, tick)
            self.assertLess(tick.index(a), tick.index(b), f"{a} before {b}")
        self.assertEqual(tick.count("a.Observe("), 1)
        self.assertEqual(tick.count("a.Rebaseline("), 1, "re-baseline only on a classified discontinuity frame")
        self.assertIn("_clock = matchTime;", tick)
        rec = "if (!_discontinuities.TryRecord(frame, now, _clock.Value, matchTime, active.Max(a => a.DisplacementNow()), settling)) { Stop(FifaTimeDiscontinuities.LimitReason); return; }"
        self.assertIn(rec, tick, "bounded record; limit => normal discontinuity-limit stop")
        self.assertLess(tick.index(rec), tick.index("a.Rebaseline("), "recorded (before/after) before the baseline moves")
        self.assertIn("_motion.Rebaseline(matchTime, FifaOverlay.ToN(_position));", self.am("public void Rebaseline("))
        self.assertIn("if (!_motion.Paused && !discontinuity)", self.am("public void WritePose("), "clip/phase held on the discontinuity frame")
        self.assertNotIn("FifaClockStep.NonFinite", tick, "nonfinite never accepted (Observe/ReadMatchTime still throw)")
        # H200: a frozen clock with an over-bound root move is decided by the pure IsDiscontinuity (executable host case
        # h200-paused-reposition-rebaselines); the host never tests Paused itself.
        self.assertNotIn("Paused", tick[tick.index("bool discontinuity"):tick.index("if (discontinuity)")])
        st = self.am("public void Resume(") + self.am("public (FifaSuspendedDecision Decision, string Reason, string Error) Decide(")
        self.assertNotIn("_discontinuities", st, "suspension resume keeps its own re-baseline")

    def test_h164_discontinuity_report_and_row(self):
        tick = self.am("public void WritePose(")  # H166 evolution
        self.assertIn("if ((_poseUpdates - 1) % RowEvery == 0 || discontinuity)", tick, "discontinuity frame always sampled")
        self.assertIn('Kind = discontinuity ? "discontinuity" : "sample"', tick)
        publish = self.method("private void Publish(")
        for field in ("Discontinuities = _discontinuities.Records", "DiscontinuityCount = _discontinuities.Count",
                      "MaximumDiscontinuities = FifaTimeDiscontinuities.MaximumDiscontinuities", "FailureDetail = _failureDetail"):
            self.assertIn(field, publish)
        self.assertIn("Discontinuities = _discontinuities.Count", self.method("public object Describe()"))
        policy = (ROOT / "src/FifaOverlayPolicy.cs").read_text(encoding="utf-8")
        heuristics = policy[policy.index("public static readonly string[] Heuristics"):]
        heuristics = heuristics[:heuristics.index("};")]
        self.assertIn("discontinuity", heuristics)
        self.assertIn("backwards", heuristics)
        self.assertIn("discontinuity-limit", heuristics)

    def test_h164_stop_frame_observation_and_final_row(self):
        tick = self.method("public void Tick()")  # H166 evolution: one observation per actor, stages set by host and actor
        begin = "_obs.Begin(frame, now, _lastMatchTime, _motion == null ? null : _motion.LastPosition);"
        self.assertIn(begin, self.am("public void BeginFrame("))
        self.assertLess(tick.index("if (frame == _lastFrame) return;"), tick.index("a.BeginFrame(frame, now);"))
        self.assertLess(tick.index("a.BeginFrame(frame, now);"), tick.index("if (!a.Running || a.Suspended) continue;"))
        guard = self.am("public void Guard()")
        self.assertLess(guard.index("_obs.SetPosition(FifaOverlay.ToN(_position));"), guard.index("FifaOverlay.RequireFinite(_position);"), "raw position captured before its finiteness check")
        self.assertIn("if (wallDt.HasValue) _obs.SetWallDt(wallDt.Value);", self.am("public void SetStage("))
        self.assertLess(tick.index('a.SetStage("wall-clock", dt);'), tick.index("if (dt < 0 || dt > FifaOverlayPolicy.MaximumStepSeconds)"))
        self.assertIn("_obs.SetClock(matchTime)", self.am("public void SetClock("))
        self.assertLess(tick.index("a.SetClock(matchTime);"), tick.index("var step = FifaTimeDiscontinuities.Classify("))
        actor = self.overlay[self.overlay.index("internal sealed class FifaOverlayActor"):]
        for stage in ('_obs.Stage = "guards";', '_obs.Stage = "actor-transform";', '_obs.Stage = "motion";', '_obs.Stage = "pose-write";'):
            self.assertIn(stage, actor)
        for stage in ('a.SetStage("wall-clock", dt);', 'a.SetStage("match-clock", null);'):
            self.assertIn(stage, tick)
        session_stop = self.method("public void Stop(")
        self.assertIn("_failureDetail = _lifecycle.Error != null && failing != null ? failing.FailureSnapshot(_stoppedFrame) : null;", session_stop)
        self.assertIn("_obs.Frame == stoppedFrame ? _obs.Snapshot() : null", self.am("public FifaFailureDetail FailureSnapshot("))
        stop = self.am("public void StopSession(")
        final = stop.index("AddStopRow(frame, now);")
        self.assertLess(session_stop.index("_lifecycle.RequestStop("), session_stop.index("a.StopSession("), "stop row after the first cause is latched")
        self.assertLess(final, stop.index("RestoreNative();"))
        row = self.am("private void AddStopRow(")
        self.assertIn('Kind = "stop"', row)
        self.assertIn("FrameCount = frame, QpcTicks = now", row)
        self.assertIn("var o = _obs.Frame == frame ? _obs.Snapshot() : null;", row, "values only from the exact stop frame")
        self.assertIn("MatchTimeSeconds = o?.MatchTime", row)
        self.assertIn("catch (Exception e) { Error(e); }", row, "a stop row failure is surfaced (cleanup unverified), never silent")
        self.assertIn("_s.CleanupError(e);", self.am("private void Error("))
        self.assertNotIn("RequireWindow", row)
        for native in (".position", "ReadMatchTime", "ReadPublishedPlaybackSpeed", ".forward"):
            self.assertNotIn(native, row, "stop row adds no native read")

    def test_h164_h163_suspended_tick_has_no_owned_pose_or_visibility_write(self):  # mutants R5 / R9
        st = self.am("public (FifaSuspendedDecision Decision, string Reason, string Error) Decide(") + self.am("public void Resume(")  # H166 evolution
        for forbidden in ("_visible = true", "_visible=true", "SetPositionAndRotation", "_mesh.", "SetActive(", "_shown = true", "HideNative", "_ownedRoot."):
            self.assertNotIn(forbidden, st, f"no owned pose/visibility write while suspended or on resume: {forbidden}")

    def test_h164_h163_observe_suspended_reads_force_off_only_in_hide_runs(self):  # mutant R6
        observe = self.am("private (bool, bool, bool?, bool?, bool?, bool?)[] ObserveSuspended()")  # H166 evolution: _s.Request
        self.assertIn("bool? forceOff = _s.Request.HideNativePlayer ? (renderer == null ? null : Read(() => renderer.forceRenderingOff)) : null;", observe)
        self.assertIn("bool? expected = _s.Request.HideNativePlayer ? _hide.ExpectedForceRenderingOff(o.Id) : null;", observe)
        self.assertEqual(observe.count("forceRenderingOff"), 1, "a single guarded forceRenderingOff read")


    # ---- H166 (0.6.0): overlay on ALL eligible players (or an explicit list); N=1 legacy form is the same code path ----
    def actor_method(self, signature):
        o = self.overlay
        start = o.index("internal sealed class FifaOverlayActor")
        body = o[o.index(signature, start):]
        return body[:body.index("\n    }\n")]

    def test_h166_host_resolves_the_actor_set_before_the_attempt(self):
        command = self.host[self.host.index("private static void Command("):]
        resolve = "FifaActorSelection.ResolveFifa(request, players.Select(p => (p.ActorInstanceId, p.TeamId, p.Eligible, p.FifaEligible, p.Goalkeeper)).ToArray())"  # H178a evolution: FIFA eligibility + role input
        self.assertIn(resolve, command)
        self.assertLess(command.index(resolve), command.index("new FifaOverlay()"), "selection refused before the single attempt is consumed")
        self.assertIn("_overlay.Start(request, root, selected, write);", command)
        self.assertIn("matches.Length != 1 || !matches[0].Eligible", command, "legacy single-actor check kept")
        result = command[command.index("Accepted = true"):][:900]
        for field in ("ActorInstanceIds =", "AllEligible =", "SelectedActorInstanceIds ="):
            self.assertIn(field, command[command.index('write("fifa-overlay-request-result.json"'):][:1500])

    def test_h166_version_060(self):
        csproj = (ROOT / "FM26.VisualDiagnostics.csproj").read_text(encoding="utf-8")
        self.assertIn("<Version>0.10.7</Version>", csproj)  # H234: 0.10.6; H238: 0.10.7; H211: 0.10.5; H209: 0.10.4; H206: 0.10.3; H171: 0.7.1; H178a: 0.8.0; H186: 0.9.0; H186b: 0.10.0; H200: 0.10.1; H203: 0.10.2
        self.assertEqual(self.host.count('Version = "0.10.7"'), 2)
        self.assertEqual(self.overlay.count('Version = "0.10.7"'), 2)
        self.assertNotIn('"0.5.1"', self.host + self.overlay)
        self.assertNotIn('"0.6.0"', self.host + self.overlay)  # H168

    def test_h166_handle_budget_declared_before_any_allocation(self):
        o = self.overlay
        start = self.method("public void Start(")
        req = "FifaHandleBudget.Require(_actors.Count, _pack.Pieces.Count, _teams.Length);"
        self.assertIn(req, start)
        self.assertLess(start.index("LoadPack("), start.index(req))
        self.assertLess(start.index(req), start.index("CreateOwned();"))
        self.assertIn("_handleBudget = FifaHandleBudget.Required(_actors.Count, _pack.Pieces.Count, _teams.Length);", start)
        self.assertIn("if (_handles.Count + count > _handleBudget)", o)
        self.assertNotIn("MaximumHandles = 16", o)

    def test_h166_shared_team_materials_and_per_actor_meshes(self):
        o = self.overlay
        shared = self.method("private void CreateSharedMaterials()")
        self.assertIn("FifaTeamPalettes.ForKey(_request.Multi, team)", shared)  # H178a evolution: palette by team+role key
        self.assertIn("Reserve(1); RequireWindow(); var material = new Material(_shader);", shared)
        self.assertEqual(o.count("new Material("), 1, "materials only in the shared per-team creation")
        create = self.actor_method("public void CreateOwned()")
        self.assertIn("RequireWindow(); renderer.sharedMaterials = _s.Materials(FifaTeamPalettes.Key(_s.Request.Multi, _actor.TeamId, _actor.Goalkeeper));", create)  # H178a evolution: team+role key
        self.assertIn("_s.Reserve(1); _s.RequireWindow(); _mesh = new Mesh();", create)
        self.assertEqual(o.count("new Mesh()"), 1)
        self.assertEqual(o.count("new GameObject("), 1)

    def test_h166_vertex_upload_reuses_one_il2cpp_buffer_per_actor(self):
        o = self.overlay
        self.assertEqual(o.count("new Il2CppStructArray<Vector3>("), 1)
        fill = self.actor_method("private void FillVertices(")
        self.assertIn("FifaVertexCopy.CopyFrame<Vector3>(", fill)
        self.assertIn("_vertices.AsSpan()", fill)
        self.assertIn("_mesh.vertices = _vertices;", o)
        self.assertNotIn("new Vector3[", o, "no managed Vector3[] (implicit per-upload IL2CPP array conversion) any more")

    def test_h166_per_actor_scope_routing(self):
        tick = self.method("public void Tick()")
        self.assertIn("FifaActorScope.OnActiveFailure(_request.Multi, session?.Message, error is TimeoutException, error is FifaGuardException, StructureIntact(), a.ObserveRenderers(), _request.Multi && a.HardIdentityLost(), error.Message)", tick)  # H168 evolution
        self.assertIn("FifaActorScope.OnSuspended(_request.Multi, session?.Message, Structure(), ", tick)  # H168 evolution
        self.assertIn("_globalSuspension.Observe(", tick)
        self.assertIn("FifaActorScope.AllEnded(_request.Multi, ", tick)
        end = self.actor_method("public void End(")
        self.assertLess(end.index("RestoreNative(reason == FifaActorScope.ActorLeftReason);"), end.index("Object.Destroy("), "per-actor restore before its owned objects go")  # H168 evolution
        verify = self.actor_method("public bool? VerifyEnded(")
        self.assertIn("if (frame <= _endFrame) return null;", verify, "strictly later frame")
        self.assertIn("ReadRestoration();", verify)  # GREEN-time test correction: readback through the shared ReadRestoration

    def test_h166_one_global_clock_read_and_discontinuity_per_frame(self):
        tick = self.method("public void Tick()")
        clock = self.method("internal double FrameClock(")  # H168 evolution: internal (called by the actor resume)
        self.assertIn("RequireWindow(); _frameClock = ReadMatchTime();", clock)
        self.assertEqual(self.overlay.count("_discontinuities.TryRecord("), 1)
        self.assertIn("var step = FifaTimeDiscontinuities.Classify(_clock.Value, matchTime);", tick)
        self.assertIn("foreach (var a in active) a.Rebaseline(matchTime);", tick)

    def test_h166_frame_cost_guard_and_stats(self):
        tick = self.method("public void Tick()")
        self.assertIn("if (_frameCost.Add(", tick)
        self.assertIn("Stop(FifaFrameCost.Reason);", tick)
        publish = self.method("private void Publish(")
        self.assertIn("FrameCost = _frameCost.Stats()", publish)
        self.assertIn("Actors = ", publish)
        self.assertIn("FifaReportBudget.HeadRows(", self.overlay)

    # ---- H168 (0.6.1): H167 BLOCK fix (resume order L1), multi classification (S1/S2), H167 survivors ----
    def suspended_loop(self):
        tick = self.method("public void Tick()")
        start = tick.index("if (!a.Running || !a.Suspended || active.Contains(a)) continue;")
        return tick[start:tick.index("double dt = (double)(now - _lastQpc)", start)]

    def test_h168_l1_session_refusal_first_on_both_paths_then_actor_guards_then_clock(self):
        tick = self.method("public void Tick()")
        session = "try { RequireSession(); } catch (Exception e) { session = e; }"
        active = "try { if (session != null) throw session; a.Guard(); active.Add(a); }"
        self.assertEqual(tick.count(session), 1)
        self.assertIn(active, tick, "H167 mutant 1: the active path applies the frame's session refusal before the actor guards")
        self.assertLess(tick.index(session), tick.index(active))
        self.assertLess(tick.index(active), tick.index("double matchTime = FrameClock(frame);"), "active path: guards before the clock read")
        loop = self.suspended_loop()  # the SAME loop serves the legacy N=1 and the multi path (no mode gate around the resume)
        self.assertEqual(loop.count("a.Resume("), 1)
        self.assertIn("if (scope.Kind == FifaScopeKind.Resume) { a.Resume(frame, now, session); _settle.MarkResume(now); continue; }", loop)
        self.assertNotIn("FrameClock(", loop, "no clock read evaluated before the resume's guards")
        self.assertNotIn("_request.Multi ?", loop[loop.index("FifaScopeKind.Stay) continue;"):loop.index("a.Resume(")])
        self.assertEqual(self.overlay.count("throw session;"), 2, "session refusal applied on the active path AND on resume")
        resume = self.am("public void Resume(")
        self.assertIn("public void Resume(int frame, long now, Exception session)", resume)
        body = [line.strip() for line in resume.split("{", 1)[1].splitlines() if line.strip() and not line.strip().startswith("//")]
        self.assertEqual(body[0], "if (session != null) throw session;", "session refusal throws before anything else on resume")
        self.assertEqual(body[1], "RequireContext(\"resume\"); RequireOwned(); RequireFlags();", "then the actor guards (0.5.1 order; H209 caller stage)")
        self.assertEqual(body[2], "double matchTime = _s.FrameClock(frame);", "then the frame's single guarded clock read")
        self.assertIn("internal double FrameClock(int frame)", self.overlay)
        self.assertNotIn("Resume = full active-path guards, then the frame's ONE guarded clock value", self.overlay, "stale :796 comment fixed")

    def test_h168_s1_session_refusal_never_scoped_to_one_actor(self):
        tick = self.method("public void Tick()")
        self.assertEqual(tick.count("session?.Message"), 3, "the frame's session refusal reaches all THREE scope decisions")  # H171 evolution: + OnSuspendLimitCount (H169 suggestion 2)
        self.assertIn("FifaActorScope.OnActiveFailure(_request.Multi, session?.Message, ", tick)
        self.assertIn("FifaActorScope.OnSuspended(_request.Multi, session?.Message, Structure(), a.Decide(Structure(), now));", tick)
        multi = (ROOT / "src/FifaOverlayMulti.cs").read_text(encoding="utf-8")
        self.assertIn("if (multi && sessionRefusal == null && guardRefusal && structureIntact && hardIdentityLost)", multi)
        self.assertEqual(multi.count("FifaScopeKind.EndActor, ActorLeftReason"), 2, "actor-left only from the two session-intact decisions")

    def test_h168_s2_fm_destroyed_renderer_only_after_a_confirmed_departure(self):
        restore = self.am("private void RestoreNative(")
        self.assertIn("private void RestoreNative(bool actorDeparted = false)", restore)
        lines = ["if (entry.RestoreWriteAttempted) continue;", "_hide.BeginRestore(entry.RendererInstanceId);",
                 "var renderer = _originals.First(o => o.Id == entry.RendererInstanceId).Renderer;",
                 "if (renderer == null) { _hide.RendererDestroyed(entry.RendererInstanceId, actorDeparted); continue; }",
                 "renderer.forceRenderingOff = entry.BeforeForceRenderingOff;"]
        for a, b in zip(lines, lines[1:]):
            self.assertIn(a, restore); self.assertIn(b, restore)
            self.assertLess(restore.index(a), restore.index(b), f"{a} before {b}")
        self.assertNotIn("died before restoration", restore, "the present-actor failure is raised by the pure ledger")
        end = self.am("public void End(")
        self.assertEqual(end.count("RestoreNative("), 1)
        self.assertIn("RestoreNative(reason == FifaActorScope.ActorLeftReason);", end, "only a confirmed departure (session intact) may find FM-destroyed renderers")
        stop = self.am("public void StopSession(")
        self.assertIn("RestoreNative(); // H160", stop, "session stop: actor still present => a destroyed renderer stays a failure")
        self.assertEqual(self.overlay.count("RestoreNative(true)"), 0)
        read = self.am("public void ReadRestoration()")
        self.assertIn("if (entry.DestroyedByFm) continue;", read, "nothing to read back on an FM-destroyed renderer")
        self.assertLess(read.index("if (entry.DestroyedByFm) continue;"), read.index("_hide.RecordRestoreReadback("))

    def test_h168_h167_survivors_host_wiring(self):  # H167 surviving mutants 3, 4, 9, 10, 11, 12 (2 = test_h168_s2 order)
        stop = self.method("public void Stop(")
        shared = "if (!h.Direct || h.Actor != 0) continue; // shared materials"
        self.assertIn(shared, stop, "mutant 3: the session destroys the shared (Actor 0) materials")
        self.assertIn("try { if (h.Value != null) Object.Destroy(h.Value); } catch (Exception e) { CleanupError(e); }", stop[stop.index(shared):])
        self.assertLess(stop.index("a.StopSession(reason, _stoppedFrame, _stopped);"), stop.index(shared), "after every actor's own cleanup")
        tick = self.method("public void Tick()")
        self.assertIn("else if (_frameClockFrame == frame) _clock = _frameClock; // resume-only frame", tick,
                      "mutant 4: a resume-only frame re-baselines the global clock (else a false discontinuity next frame)")
        self.assertLess(tick.index("_clock = matchTime;"), tick.index("else if (_frameClockFrame == frame) _clock = _frameClock;"))
        verify = "if (e.VerifyEnded(frame, now) == false) { _failing = e; Stop(FifaViewSuspension.FailureReason, \"Per-actor end cleanup not verified on a later frame.\"); return; }"
        self.assertIn(verify, tick, "mutant 10: an unverified per-actor end stops the whole session")
        self.assertLess(tick.index(verify), tick.index("a.BeginFrame(frame, now);"))
        ended = self.am("public bool? VerifyEnded(")
        self.assertIn("Verified = _endErrors.Count == 0 && FifaOverlayPolicy.CanCertifyCleanup(_endFrame, frame, alive, unknown, false) && _hide.RestorationVerified", ended,
                      "mutant 9: per-actor end errors forbid certification")
        self.assertIn("if (_ended && _endCleanup == null && _endErrors.Count < 16) _endErrors.Add(", self.am("private void Error("))
        identity = self.am("public bool HardIdentityLost()")
        self.assertIn("catch { return false; } // unreadable is not proof", identity, "mutant 11: unreadable identity is never a departure")
        self.assertEqual(identity.count("return true"), 0)
        clock = self.method("internal double FrameClock(")
        body = [line.strip() for line in clock.split("{", 1)[1].splitlines() if line.strip()]
        self.assertEqual(body[:4], ["if (_frameClockFrame == frame) return _frameClock;", "RequireWindow(); _frameClock = ReadMatchTime();",
                                    "_frameClockFrame = frame;", "return _frameClock;"], "mutant 12: at most one guarded clock read per frame")
        self.assertEqual(self.overlay.count("ReadMatchTime()"), 3, "definition + Start's baseline + the single cached per-frame call site")
        self.assertIn("double startClock = ReadMatchTime();", self.method("public void Start("))
        actor = self.overlay[self.overlay.index("internal sealed class FifaOverlayActor"):]
        self.assertNotIn("ReadMatchTime(", self.method("public void Tick()") + actor, "per-frame reads only through FrameClock")

    def test_h168_flag_guard_without_per_frame_json(self):
        flags = self.am("public void RequireFlags()")
        self.assertIn("if (_originals.Count == 0 || !FifaOverlayPolicy.FlagsUnchanged(Flags()))", flags)
        self.assertIn("throw new FifaGuardException(", flags, "still a guard refusal")
        self.assertNotIn("SerializeToElement", self.overlay, "no per-actor per-frame JSON serialisation")

if __name__ == "__main__":
    unittest.main()
