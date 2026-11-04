# SPDX-License-Identifier: MIT
"""fm26vm B9: tools/deploy.py evolution (temporary fake FM trees only; never the real game)."""
import contextlib
import importlib.util
import io
import json
import tempfile
import unittest
from pathlib import Path
from unittest import mock

MODULE = Path(__file__).resolve().parents[1] / "tools" / "deploy.py"
spec = importlib.util.spec_from_file_location("deploy_b9", MODULE)
deploy = importlib.util.module_from_spec(spec)
spec.loader.exec_module(deploy)

PLUGIN_REL = "BepInEx/plugins/FM26.VisualDiagnostics/FM26.VisualDiagnostics.dll"
AUTO_REL = "BepInEx/visual-diagnostics/fifa-overlay-auto.json"


def write(path, data):
    path = Path(path)
    path.parent.mkdir(parents=True, exist_ok=True)
    path.write_bytes(data)
    return path


def core_dll(version):
    """Fake BepInEx.Core.dll carrying a VS_VERSION_INFO-like ProductVersion string (UTF-16LE)."""
    return (b"MZ" + b"\0" * 62 + "FileVersion\0\0".encode("utf-16-le") + "6.0.0.0\0".encode("utf-16-le")
            + "ProductVersion\0\0".encode("utf-16-le") + (version + "\0").encode("utf-16-le") + b"\0" * 8)


class Tmp(unittest.TestCase):
    def setUp(self):
        self._tmp = tempfile.TemporaryDirectory()
        self.tmp = Path(self._tmp.name)
        self.game = self.tmp / "game"
        write(self.game / "fm.exe", b"original executable")
        self.project = self.tmp / "project"
        self.project.mkdir()
        self.env = {"LOCALAPPDATA": str(self.tmp / "appdata")}
        self.user = self.tmp / "appdata" / "fm26-visual-mod"
        self.plugin = write(self.tmp / "build" / "FM26.VisualDiagnostics.dll", b"plugin v1")

    def tearDown(self):
        self._tmp.cleanup()

    def stage(self, *extra):
        stage = self.tmp / "stage"
        write(stage / "BepInEx" / "core" / "BepInEx.Core.dll", core_dll("6.0.0-be.788+5b766a3"))
        write(stage / "winhttp.dll", b"proxy")
        for rel in extra:
            write(stage / rel, b"x")
        return stage

    def existing_bepinex(self, version="6.0.0-be.788+5b766a3"):
        write(self.game / "BepInEx" / "core" / "BepInEx.Core.dll", core_dll(version))
        write(self.game / "winhttp.dll", b"user proxy")
        write(self.game / "BepInEx" / "plugins" / "Other" / "Other.dll", b"someone else")


class ResolveStateTests(Tmp):
    def test_without_legacy_state_uses_the_user_folder(self):
        self.assertEqual(deploy.resolve_state(self.project, self.env),
                         (self.user / "install-state.json", self.user / "backups"))


class ResolveLegacyTests(Tmp):
    def test_existing_legacy_state_is_kept(self):
        write(self.project / "local" / "install-state.json", b"{}")
        self.assertEqual(deploy.resolve_state(self.project, self.env),
                         (self.project / "local" / "install-state.json", self.project / "local" / "backups"))


class MigrateTests(Tmp):
    def test_explicit_migration_moves_state_to_the_user_folder(self):
        legacy = write(self.project / "local" / "install-state.json", json.dumps({"game": "G", "status": "installed"}).encode())
        moved = deploy.migrate_state(self.project, self.env)
        self.assertEqual(moved, self.user / "install-state.json")
        data = json.loads((self.user / "install-state.json").read_text(encoding="utf-8"))
        self.assertEqual((data["game"], data["status"], data["migrated_from"]), ("G", "installed", str(legacy)))
        self.assertFalse(legacy.exists())
        self.assertTrue((self.project / "local" / "install-state.json.migrated").is_file())
        self.assertEqual(deploy.resolve_state(self.project, self.env)[0], self.user / "install-state.json")


class MigrateRefuseTests(Tmp):
    def test_migration_never_overwrites_an_existing_user_state(self):
        legacy = write(self.project / "local" / "install-state.json", b'{"game": "old"}')
        current = write(self.user / "install-state.json", b'{"game": "new"}')
        try:
            deploy.migrate_state(self.project, self.env)
            refused = False
        except FileExistsError:
            refused = True
        self.assertTrue(refused)
        self.assertEqual((legacy.read_bytes(), current.read_bytes()), (b'{"game": "old"}', b'{"game": "new"}'))


def run_main(argv, **kw):
    out = io.StringIO()
    try:
        with contextlib.redirect_stdout(out):
            deploy.main(argv, **kw)
        code = 0
    except SystemExit as e:
        code = e.code
    except Exception as e:  # keep REDs assertion-shaped: report the exception as the outcome
        code = f"{type(e).__name__}: {e}"
    return code, out.getvalue()


class MainStateDefaultTests(Tmp):
    def test_state_defaults_to_resolve_state(self):
        deploy.install(self.stage(), self.game, self.user / "install-state.json")
        code, out = run_main(["verify", "--game", str(self.game)], project=self.project, env=self.env, stopped=lambda: None)
        self.assertEqual(code, 0, out)
        self.assertEqual(json.loads(out)["status"], "installed")


class MainBackupDefaultTests(Tmp):
    def test_uninstall_backups_follow_the_resolved_state_folder(self):
        deploy.install(self.stage(), self.game, self.user / "install-state.json")
        code, out = run_main(["uninstall", "--game", str(self.game)], project=self.project, env=self.env, stopped=lambda: None)
        self.assertEqual(code, 0, out)
        backup = Path(json.loads(out)["backup"])
        self.assertEqual(backup.parent, self.user / "backups")
        self.assertFalse((self.project / "local").exists())


class MainGameDetectTests(Tmp):
    def test_game_defaults_to_detection(self):
        nowhere = self.tmp / "not-a-game"  # pre-GREEN static default: refused install, real FM folder never reached
        with mock.patch.object(deploy, "DEFAULT_GAME", str(nowhere), create=True):
            code, out = run_main(["install", "--stage", str(self.stage()), "--state", str(self.tmp / "s.json")],
                                 project=self.project, env=self.env, stopped=lambda: None, find_game=lambda: str(self.game))
        self.assertEqual(code, 0, out)
        self.assertEqual((self.game / "winhttp.dll").read_bytes(), b"proxy")
        self.assertFalse(nowhere.exists())


class AutoEnableTests(Tmp):
    def test_enable_writes_and_tracks_the_auto_file(self):
        state = self.tmp / "s.json"
        deploy.install(self.stage(), self.game, state)
        deploy.set_auto(self.game, state, True)
        auto = self.game / AUTO_REL
        self.assertTrue(auto.is_file())
        self.assertEqual(json.loads(auto.read_text(encoding="utf-8")), {"Enabled": True})
        recorded = json.loads(state.read_text(encoding="utf-8"))["auto_json"]
        self.assertEqual(recorded, {"path": AUTO_REL, "sha256": deploy.digest(auto)})


class AutoDisableTests(Tmp):
    def test_disable_removes_the_file_and_the_record(self):
        state = self.tmp / "s.json"
        deploy.install(self.stage(), self.game, state)
        deploy.set_auto(self.game, state, True)
        deploy.set_auto(self.game, state, False)
        self.assertFalse((self.game / AUTO_REL).exists())
        self.assertNotIn("auto_json", json.loads(state.read_text(encoding="utf-8")))


class AutoUntrackedTests(Tmp):
    def test_enable_refuses_to_overwrite_an_untracked_auto_file(self):
        state = self.tmp / "s.json"
        deploy.install(self.stage(), self.game, state)
        mine = write(self.game / AUTO_REL, b'{"Enabled":true,"Note":"hand-made"}')
        try:
            deploy.set_auto(self.game, state, True)
            refused = False
        except FileExistsError:
            refused = True
        self.assertTrue(refused)
        self.assertEqual(mine.read_bytes(), b'{"Enabled":true,"Note":"hand-made"}')


class AutoCliTests(Tmp):
    def test_auto_on_and_auto_off_actions(self):
        state = self.tmp / "s.json"
        deploy.install(self.stage(), self.game, state)
        common = ["--game", str(self.game), "--state", str(state)]
        on, _ = run_main(["auto-on"] + common, project=self.project, env=self.env, stopped=lambda: None)
        present = (self.game / AUTO_REL).is_file()
        off, _ = run_main(["auto-off"] + common, project=self.project, env=self.env, stopped=lambda: None)
        self.assertEqual((on, present, off, (self.game / AUTO_REL).exists()), (0, True, 0, False))


class PublicInteropTests(Tmp):
    def test_public_install_refuses_a_stage_with_interop(self):
        stage = self.stage("BepInEx/interop/Assembly-CSharp.dll")
        try:
            deploy.install(stage, self.game, self.tmp / "s.json", public=True)
            error = None
        except ValueError as e:
            error = str(e)
        self.assertIsNotNone(error)
        self.assertIn("BepInEx/interop", error)
        self.assertFalse((self.game / "BepInEx").exists() or (self.tmp / "s.json").exists())


class PublicUnityLibsTests(Tmp):
    def test_public_install_refuses_a_stage_with_unity_libs(self):
        stage = self.stage("BepInEx/unity-libs/6000.0.52.zip")
        try:
            deploy.install(stage, self.game, self.tmp / "s.json", public=True)
            error = None
        except ValueError as e:
            error = str(e)
        self.assertIsNotNone(error)
        self.assertIn("BepInEx/unity-libs", error)
        self.assertFalse((self.game / "BepInEx").exists() or (self.tmp / "s.json").exists())


class MainPublicTests(Tmp):
    def test_install_public_flag_reaches_the_stage_check(self):
        stage = self.stage("BepInEx/interop/Assembly-CSharp.dll")
        code, out = run_main(["install", "--public", "--stage", str(stage), "--game", str(self.game),
                              "--state", str(self.tmp / "s.json")], project=self.project, env=self.env, stopped=lambda: None)
        self.assertIn("BepInEx/interop", str(code))
        self.assertFalse((self.game / "BepInEx").exists())


class VersionReadTests(Tmp):
    def test_reads_product_version_of_bepinex_core(self):
        self.existing_bepinex("6.0.0-be.788+5b766a3b7f6c164d4798924a93f3acf4db769d06")
        self.assertEqual(deploy.bepinex_version(self.game), "6.0.0-be.788+5b766a3b7f6c164d4798924a93f3acf4db769d06")


class VersionMissingTests(Tmp):
    def test_missing_core_dll_has_no_version(self):
        try:
            version = deploy.bepinex_version(self.game)
        except OSError as e:
            version = f"raised {type(e).__name__}"
        self.assertIsNone(version)


class VersionNoKeyTests(Tmp):
    def test_core_dll_without_product_version_has_no_version(self):
        write(self.game / "BepInEx" / "core" / "BepInEx.Core.dll", b"MZ" + "6.0.0-be.788\0".encode("utf-16-le"))
        self.assertIsNone(deploy.bepinex_version(self.game))


class PluginOnlyInstallTests(Tmp):
    def test_plugin_only_install_over_existing_be788(self):
        self.existing_bepinex()
        state = self.user / "install-state.json"
        deploy.install_plugin_only(self.plugin, self.game, state)
        installed = self.game / PLUGIN_REL
        self.assertEqual(installed.read_bytes() if installed.is_file() else None, b"plugin v1")
        self.assertEqual((self.game / "BepInEx/plugins/Other/Other.dll").read_bytes(), b"someone else")
        self.assertEqual((self.game / "winhttp.dll").read_bytes(), b"user proxy")
        data = json.loads(state.read_text(encoding="utf-8")) if state.is_file() else {}
        self.assertEqual((data.get("mode"), data.get("status"), data.get("owned_roots")), ("plugin-only", "installed", []))
        self.assertEqual(data["files"], [{"path": PLUGIN_REL, "sha256": deploy.digest(self.plugin)}])
        self.assertEqual(data["baseline_sha256"], {"fm.exe": deploy.digest(self.game / "fm.exe")})


class PluginOnlyVersionTests(Tmp):
    def test_other_bepinex_version_is_refused(self):
        self.existing_bepinex("6.0.0-be.700+abcdef0")
        state = self.user / "install-state.json"
        try:
            deploy.install_plugin_only(self.plugin, self.game, state)
            error = None
        except ValueError as e:
            error = str(e)
        self.assertIsNotNone(error)
        self.assertIn("6.0.0-be.700+abcdef0", error)
        self.assertIn("6.0.0-be.788", error)
        self.assertFalse((self.game / PLUGIN_REL).parent.exists() or state.exists())


class PluginOnlyNoBepInExTests(Tmp):
    def test_plugin_only_without_bepinex_is_refused(self):
        with self.assertRaises(ValueError) as ctx:
            deploy.install_plugin_only(self.plugin, self.game, self.tmp / "s.json")
        self.assertIn("version None", str(ctx.exception))
        self.assertFalse((self.game / "BepInEx").exists())


class PluginOnlyExistingTests(Tmp):
    def test_existing_plugin_folder_is_never_overwritten(self):
        self.existing_bepinex()
        theirs = write(self.game / PLUGIN_REL, b"user copy")
        try:
            deploy.install_plugin_only(self.plugin, self.game, self.tmp / "s.json")
            refused = False
        except FileExistsError:
            refused = True
        self.assertTrue(refused)
        self.assertEqual(theirs.read_bytes(), b"user copy")
        self.assertFalse((self.tmp / "s.json").exists())


class PluginOnlyUninstallTests(Tmp):
    def test_uninstall_moves_only_our_plugin_folder(self):
        self.existing_bepinex()
        state = self.tmp / "s.json"
        deploy.install_plugin_only(self.plugin, self.game, state)
        try:
            backup = deploy.uninstall(self.game, state, self.tmp / "backups")
        except Exception as e:  # pre-GREEN: 'Unsafe deployment manifest' for owned_roots == []
            backup = e
        self.assertIsInstance(backup, Path)
        self.assertFalse((self.game / PLUGIN_REL).parent.exists())
        self.assertEqual((backup / PLUGIN_REL).read_bytes(), b"plugin v1")
        self.assertEqual((self.game / "BepInEx/core/BepInEx.Core.dll").read_bytes(), core_dll("6.0.0-be.788+5b766a3"))
        self.assertEqual((self.game / "BepInEx/plugins/Other/Other.dll").read_bytes(), b"someone else")
        self.assertEqual((self.game / "winhttp.dll").read_bytes(), b"user proxy")
        self.assertEqual(json.loads(state.read_text(encoding="utf-8"))["status"], "uninstalled")


class PluginOnlyUninstallAutoTests(Tmp):
    def test_uninstall_also_removes_the_tracked_auto_file(self):
        self.existing_bepinex()
        state = self.tmp / "s.json"
        deploy.install_plugin_only(self.plugin, self.game, state)
        deploy.set_auto(self.game, state, True)
        backup = deploy.uninstall(self.game, state, self.tmp / "backups")
        self.assertFalse((self.game / AUTO_REL).exists())
        self.assertEqual(json.loads((backup / AUTO_REL).read_text(encoding="utf-8")), {"Enabled": True})


class PluginOnlyUnsafeManifestTests(Tmp):
    def test_plugin_only_state_cannot_move_foreign_paths(self):
        self.existing_bepinex()
        state = self.tmp / "s.json"
        deploy.install_plugin_only(self.plugin, self.game, state)
        accepted = []
        for foreign in ("BepInEx/core", "BepInEx/plugins", "BepInEx/plugins/Other", "fm_Data"):
            data = json.loads(state.read_text(encoding="utf-8"))
            data["owned_dirs"] = [foreign]
            bad = write(self.tmp / f"bad-{len(accepted)}-{foreign.replace('/', '_')}.json", json.dumps(data).encode())
            try:
                deploy.uninstall(self.game, bad, self.tmp / "backups")
                accepted.append(foreign)
            except (ValueError, OSError):
                pass
        self.assertEqual(accepted, [])
        self.assertTrue((self.game / "BepInEx/core/BepInEx.Core.dll").is_file())
        self.assertTrue((self.game / "BepInEx/plugins/Other/Other.dll").is_file())


class PluginOnlyUpdateTests(Tmp):
    def test_update_plugin_works_in_plugin_only_mode(self):
        self.existing_bepinex()
        state = self.tmp / "s.json"
        deploy.install_plugin_only(self.plugin, self.game, state)
        new = write(self.tmp / "v2" / "FM26.VisualDiagnostics.dll", b"plugin v2")
        backup = deploy.update_plugin(new, self.game, state)
        self.assertEqual(((self.game / PLUGIN_REL).read_bytes(), backup.read_bytes()), (b"plugin v2", b"plugin v1"))


class MainPluginOnlyTests(Tmp):
    def test_install_with_plugin_over_existing_bepinex_is_plugin_only(self):
        self.existing_bepinex()
        empty = self.tmp / "empty-stage"
        empty.mkdir()
        state = self.tmp / "s.json"
        code, out = run_main(["install", "--plugin", str(self.plugin), "--stage", str(empty), "--game", str(self.game),
                              "--state", str(state)], project=self.project, env=self.env, stopped=lambda: None)
        self.assertEqual(code, 0, out)
        self.assertEqual((self.game / PLUGIN_REL).read_bytes(), b"plugin v1")
        self.assertEqual(json.loads(state.read_text(encoding="utf-8"))["mode"], "plugin-only")


class AcquiredStageTests(Tmp):
    def test_acquired_bepinex_folder_is_a_valid_public_stage(self):
        import hashlib
        import sys
        import zipfile
        root = str(Path(__file__).resolve().parents[1])
        if root not in sys.path:
            sys.path.insert(0, root)
        from tools.fm26vm import tools_acquire
        official = self.tmp / "official.zip"
        with zipfile.ZipFile(official, "w") as z:
            z.writestr("BepInEx/core/BepInEx.Core.dll", core_dll("6.0.0-be.788+5b766a3"))
            z.writestr("winhttp.dll", b"proxy")
            z.writestr("doorstop_config.ini", b"[General]")
        pins = {"bepinex": {"version": "6.0.0-be.788", "url": "https://example.invalid/b.zip", "file": "b.zip",
                            "sha256": hashlib.sha256(official.read_bytes()).hexdigest(), "folder": "bep", "checks": {}}}
        stage = Path(tools_acquire.acquire("bepinex", self.tmp / "tools", supplied=official, pins=pins)["path"])
        try:
            deploy.install(stage, self.game, self.tmp / "s.json", public=True)
            error = None
        except ValueError as e:
            error = str(e)
        self.assertIsNone(error)
        self.assertEqual(deploy.bepinex_version(self.game), "6.0.0-be.788+5b766a3")
