# SPDX-License-Identifier: MIT
"""H226a: tools/deploy.py hardening after the H225 review (temporary fake FM trees only; never the real game)."""
import contextlib
import importlib.util
import io
import json
import os
import shutil
import subprocess
import sys
import tempfile
import unittest
from pathlib import Path
from types import SimpleNamespace
from unittest import mock

ROOT = Path(__file__).resolve().parents[1]
MODULE = ROOT / "tools" / "deploy.py"
spec = importlib.util.spec_from_file_location("deploy_h226", MODULE)
deploy = importlib.util.module_from_spec(spec)
spec.loader.exec_module(deploy)

PLUGIN_REL = "BepInEx/plugins/FM26.VisualDiagnostics/FM26.VisualDiagnostics.dll"
PLUGIN_DIR = "BepInEx/plugins/FM26.VisualDiagnostics"
AUTO_REL = "BepInEx/visual-diagnostics/fifa-overlay-auto.json"


def write(path, data):
    path = Path(path)
    path.parent.mkdir(parents=True, exist_ok=True)
    path.write_bytes(data)
    return path


def version_blob(raw):
    """Fake BepInEx.Core.dll whose ProductVersion value is the given raw UTF-16LE bytes."""
    return (b"MZ" + b"\0" * 62 + "ProductVersion\0\0".encode("utf-16-le") + raw + b"\0\0" + b"\0" * 8)


def core_dll(version):
    return version_blob(version.encode("utf-16-le"))


def outcome(fn, *args, **kwargs):
    """Exception raised by fn (None if it returned): keeps REDs assertion-shaped."""
    try:
        fn(*args, **kwargs)
    except Exception as error:  # noqa: BLE001
        return error
    return None


def run_main(argv, **kw):
    out = io.StringIO()
    try:
        with contextlib.redirect_stdout(out):
            deploy.main(argv, **kw)
        code = 0
    except SystemExit as e:
        code = e.code
    except Exception as e:  # report the exception as the outcome
        code = f"{type(e).__name__}: {e}"
    return code, out.getvalue()


class Tmp(unittest.TestCase):
    def setUp(self):
        self._tmp = tempfile.TemporaryDirectory()
        self.tmp = Path(self._tmp.name)
        env = mock.patch.dict(os.environ, {"LOCALAPPDATA": str(self.tmp / "appdata")})  # never the real private packs
        env.start()
        self.addCleanup(env.stop)
        self.game = self.tmp / "game"
        write(self.game / "fm.exe", b"original executable")
        self.project = self.tmp / "project"
        self.project.mkdir()
        self.env = {"LOCALAPPDATA": str(self.tmp / "appdata")}
        self.user = self.tmp / "appdata" / "fm26-visual-mod"
        self.plugin = write(self.tmp / "build" / "FM26.VisualDiagnostics.dll", b"plugin v1")
        self.state = self.tmp / "s.json"

    def tearDown(self):
        self._tmp.cleanup()

    def stage(self):
        stage = self.tmp / "stage"
        write(stage / "BepInEx" / "core" / "BepInEx.Core.dll", core_dll("6.0.0-be.788+5b766a3"))
        write(stage / "winhttp.dll", b"proxy")
        return stage

    def existing_bepinex(self, version="6.0.0-be.788+5b766a3", blob=None):
        write(self.game / "BepInEx" / "core" / "BepInEx.Core.dll", blob if blob is not None else core_dll(version))
        write(self.game / "winhttp.dll", b"user proxy")
        write(self.game / "BepInEx" / "plugins" / "Other" / "Other.dll", b"someone else")

    def plugin_only(self):
        self.existing_bepinex()
        deploy.install_plugin_only(self.plugin, self.game, self.state)

    def tamper_state(self, change):
        data = json.loads(self.state.read_text(encoding="utf-8"))
        change(data)
        self.state.write_text(json.dumps(data), encoding="utf-8")


class B1PluginOnlyOverOwnFullInstallTests(Tmp):
    def test_install_plugin_over_our_full_install_is_refused_and_state_kept(self):
        # Reproduces H225 probes.json deploy.D1: full loader install, then `install --plugin` (BepInEx/ exists).
        deploy.install(self.stage(), self.game, self.state)
        before = self.state.read_bytes()
        empty = self.tmp / "empty-stage"
        empty.mkdir()
        code, out = run_main(["install", "--plugin", str(self.plugin), "--stage", str(empty), "--game", str(self.game),
                              "--state", str(self.state)], project=self.project, env=self.env, stopped=lambda: None)
        self.assertIn("FileExistsError", str(code), out)
        self.assertEqual(self.state.read_bytes(), before)
        self.assertFalse((self.game / PLUGIN_DIR).exists())
        deploy.uninstall(self.game, self.state, self.tmp / "backups")
        self.assertEqual(sorted(p.name for p in self.game.iterdir()), ["fm.exe"])


class N1PrivatePackTests(Tmp):
    def test_plugin_only_refuses_a_plugin_holding_private_pack_data(self):
        self.existing_bepinex()
        bad = write(self.tmp / "bad" / "FM26.VisualDiagnostics.dll", b"MZ" + b'"fm26.fifa-pack.v2"')
        error = outcome(deploy.install_plugin_only, bad, self.game, self.state)
        self.assertIsInstance(error, ValueError)
        self.assertIn("private FIFA pack", str(error))
        self.assertFalse((self.game / PLUGIN_DIR).exists() or self.state.exists())


class N1FilenameTests(Tmp):
    def test_plugin_only_refuses_an_unexpected_plugin_filename(self):
        self.existing_bepinex()
        other = write(self.tmp / "x" / "Something.dll", b"plugin v1")
        error = outcome(deploy.install_plugin_only, other, self.game, self.state)
        self.assertIsInstance(error, ValueError)
        self.assertIn("Unexpected plugin filename", str(error))
        self.assertFalse((self.game / PLUGIN_DIR).exists() or self.state.exists())


class N1HashTests(Tmp):
    def test_plugin_only_hash_mismatch_is_refused_without_leftovers(self):
        self.existing_bepinex()
        with mock.patch.object(deploy.shutil, "copy2", lambda source, target: Path(target).write_bytes(b"corrupt")):
            error = outcome(deploy.install_plugin_only, self.plugin, self.game, self.state)
        self.assertIsInstance(error, RuntimeError)
        self.assertIn("hash mismatch", str(error))
        self.assertFalse((self.game / PLUGIN_DIR).exists() or self.state.exists())


class N1FailedCopyTests(Tmp):
    def test_plugin_only_failed_copy_leaves_no_untracked_folder(self):
        self.existing_bepinex()

        def broken(source, target):
            Path(target).write_bytes(b"half")
            raise OSError("disk full")
        with mock.patch.object(deploy.shutil, "copy2", broken):
            error = outcome(deploy.install_plugin_only, self.plugin, self.game, self.state)
        self.assertIsInstance(error, OSError)
        self.assertFalse((self.game / PLUGIN_DIR).exists() or self.state.exists())
        self.assertTrue((self.game / "BepInEx/plugins/Other/Other.dll").is_file())
        deploy.install_plugin_only(self.plugin, self.game, self.state)  # a retry is not blocked
        self.assertEqual((self.game / PLUGIN_REL).read_bytes(), b"plugin v1")


class N2AutoOtherGameTests(Tmp):
    def test_auto_on_refuses_a_game_other_than_the_state_game(self):
        deploy.install(self.stage(), self.game, self.state)
        other = self.tmp / "other-game"
        write(other / "fm.exe", b"another install")
        error = outcome(deploy.set_auto, other, self.state, True)
        self.assertIsInstance(error, ValueError)
        self.assertFalse((other / AUTO_REL).exists())
        self.assertNotIn("auto_json", json.loads(self.state.read_text(encoding="utf-8")))


class N2AutoStatusTests(Tmp):
    def test_auto_on_refuses_a_state_that_is_not_installed(self):
        deploy.install(self.stage(), self.game, self.state)
        deploy.uninstall(self.game, self.state, self.tmp / "backups")
        error = outcome(deploy.set_auto, self.game, self.state, True)
        self.assertIsInstance(error, ValueError)
        self.assertFalse((self.game / "BepInEx").exists())


class N2AutoOffUntrackedTests(Tmp):
    def test_auto_off_refuses_an_untracked_auto_file(self):
        deploy.install(self.stage(), self.game, self.state)
        mine = write(self.game / AUTO_REL, b'{"Enabled":true,"Note":"hand-made"}')
        error = outcome(deploy.set_auto, self.game, self.state, False)
        self.assertIsInstance(error, ValueError)
        self.assertIn("untracked", str(error))
        self.assertEqual(mine.read_bytes(), b'{"Enabled":true,"Note":"hand-made"}')


class N2AutoOffModifiedTests(Tmp):
    def test_auto_off_refuses_an_auto_file_modified_since_auto_on(self):
        deploy.install(self.stage(), self.game, self.state)
        deploy.set_auto(self.game, self.state, True)
        edited = write(self.game / AUTO_REL, b'{"Enabled":true,"Tuned":1}')
        error = outcome(deploy.set_auto, self.game, self.state, False)
        self.assertIsInstance(error, ValueError)
        self.assertIn("modified", str(error))
        self.assertEqual(edited.read_bytes(), b'{"Enabled":true,"Tuned":1}')
        self.assertIn("auto_json", json.loads(self.state.read_text(encoding="utf-8")))


class N3TamperedAutoPathTests(Tmp):
    def test_plugin_only_uninstall_refuses_a_foreign_auto_json_path(self):
        # H225 probe D8: a tampered auto_json.path moved fm.exe out of the game.
        self.plugin_only()
        deploy.set_auto(self.game, self.state, True)
        self.tamper_state(lambda data: data["auto_json"].update(path="fm.exe"))
        error = outcome(deploy.uninstall, self.game, self.state, self.tmp / "backups")
        self.assertIsInstance(error, ValueError)
        self.assertEqual((self.game / "fm.exe").read_bytes(), b"original executable")
        self.assertTrue((self.game / PLUGIN_REL).is_file())


@unittest.skipUnless(os.name == "nt", "NTFS junction")
class N3ReparsePointTests(Tmp):
    def test_plugin_only_uninstall_refuses_an_owned_dir_redirected_outside(self):
        import _winapi
        self.plugin_only()
        outside = write(self.tmp / "outside" / "precious.txt", b"keep me").parent
        shutil.rmtree(self.game / PLUGIN_DIR)
        _winapi.CreateJunction(str(outside), str(self.game / PLUGIN_DIR))
        try:
            error = outcome(deploy.uninstall, self.game, self.state, self.tmp / "backups")
        finally:
            junction = self.game / PLUGIN_DIR
            if os.path.lexists(junction):
                os.rmdir(junction)  # removes the junction only, never its target
        self.assertIsInstance(error, ValueError)
        self.assertEqual((outside / "precious.txt").read_bytes(), b"keep me")
        self.assertEqual(json.loads(self.state.read_text(encoding="utf-8"))["status"], "installed")


class N3MissingPluginDirTests(Tmp):
    def test_plugin_only_uninstall_with_a_missing_plugin_dir_is_a_clear_refusal(self):
        # H225 probe D9: FileNotFoundError traceback, state left 'installed' after a partial move.
        self.plugin_only()
        deploy.set_auto(self.game, self.state, True)
        shutil.rmtree(self.game / PLUGIN_DIR)
        error = outcome(deploy.uninstall, self.game, self.state, self.tmp / "backups")
        self.assertIsInstance(error, ValueError)
        self.assertIn("missing", str(error))
        self.assertTrue((self.game / AUTO_REL).is_file())
        self.assertEqual(json.loads(self.state.read_text(encoding="utf-8"))["status"], "installed")


class N3PluginOnlyBaselineTests(Tmp):
    def test_plugin_only_uninstall_checks_the_baseline_originals(self):
        self.plugin_only()
        write(self.game / "fm.exe", b"patched by something else")
        error = outcome(deploy.uninstall, self.game, self.state, self.tmp / "backups")
        self.assertIsInstance(error, RuntimeError)
        data = json.loads(self.state.read_text(encoding="utf-8"))
        self.assertEqual((data["status"], data.get("originals_unchanged")), ("uninstalled", {"fm.exe": False}))
        self.assertFalse((self.game / PLUGIN_DIR).exists())


class N4InvalidUtf16Tests(Tmp):
    def test_invalid_utf16_product_version_is_the_intended_refusal(self):
        self.existing_bepinex(blob=version_blob("\ud800bad".encode("utf-16-le", "surrogatepass")))
        error = outcome(deploy.install_plugin_only, self.plugin, self.game, self.state)
        self.assertIsInstance(error, ValueError)
        self.assertIn("is not supported", str(error))
        self.assertFalse((self.game / PLUGIN_DIR).exists() or self.state.exists())


class N4BoundedReadTests(Tmp):
    def test_core_dll_larger_than_the_read_bound_has_no_version(self):
        self.existing_bepinex(blob=b"MZ" + b"\0" * 8192 + core_dll("6.0.0-be.788+5b766a3"))
        with mock.patch.object(deploy, "_MAX_CORE_BYTES", 4096, create=True):
            self.assertIsNone(deploy.bepinex_version(self.game))


class N5CliErrorTests(Tmp):
    def test_cli_refusal_prints_error_and_exits_1_without_traceback(self):
        notgame = self.tmp / "not-a-game"
        notgame.mkdir()
        env = dict(os.environ, LOCALAPPDATA=str(self.tmp / "appdata"), PYTHONDONTWRITEBYTECODE="1")
        result = subprocess.run([sys.executable, "-B", str(MODULE), "install", "--game", str(notgame), "--stage",
                                 str(self.stage()), "--state", str(self.state)], capture_output=True, text=True,
                                env=env, timeout=120)
        self.assertEqual(result.returncode, 1, result.stderr)
        self.assertTrue(result.stderr.startswith("error: "), result.stderr)
        self.assertNotIn("Traceback", result.stderr)
        self.assertFalse(self.state.exists())


class N5PowershellTimeoutTests(Tmp):
    def test_game_running_check_uses_a_subprocess_timeout(self):
        calls = []

        def fake_run(*args, **kwargs):
            calls.append(kwargs)
            return SimpleNamespace(stdout="0\n", returncode=0)
        with mock.patch.object(deploy.os, "name", "nt"), mock.patch.object(deploy.subprocess, "run", fake_run), \
                mock.patch.object(deploy.subprocess, "CREATE_NO_WINDOW", 0x08000000, create=True):
            deploy.require_game_stopped()
        self.assertEqual(len(calls), 1)
        self.assertGreater(calls[0].get("timeout") or 0, 0)


class N5PowershellTimeoutRefusalTests(Tmp):
    def test_a_hung_game_check_is_a_clear_refusal(self):
        def hung(*args, **kwargs):
            raise subprocess.TimeoutExpired(args[0], kwargs.get("timeout"))
        with mock.patch.object(deploy.os, "name", "nt"), mock.patch.object(deploy.subprocess, "run", hung), \
                mock.patch.object(deploy.subprocess, "CREATE_NO_WINDOW", 0x08000000, create=True):
            error = outcome(deploy.require_game_stopped)
        self.assertIsInstance(error, RuntimeError)
        self.assertIn("Could not check", str(error))


class N5MigrateActionTests(Tmp):
    def test_migrate_state_action_moves_the_legacy_state(self):
        write(self.project / "local" / "install-state.json", json.dumps({"game": "G", "status": "installed"}).encode())

        def no_detection():
            raise AssertionError("migrate-state must not need the game folder")
        code, out = run_main(["migrate-state"], project=self.project, env=self.env, stopped=lambda: None,
                             find_game=no_detection)
        self.assertEqual(code, 0, out)
        self.assertEqual(json.loads(out)["state"], str(self.user / "install-state.json"))
        self.assertTrue((self.project / "local" / "install-state.json.migrated").is_file())


class N5MigrateActionRefusalTests(Tmp):
    def test_migrate_state_action_never_overwrites_a_user_state(self):
        legacy = write(self.project / "local" / "install-state.json", b'{"game": "old"}')
        current = write(self.user / "install-state.json", b'{"game": "new"}')
        code, out = run_main(["migrate-state"], project=self.project, env=self.env, stopped=lambda: None,
                             find_game=lambda: str(self.game))
        self.assertIn("FileExistsError", str(code), out)
        self.assertEqual((legacy.read_bytes(), current.read_bytes()), (b'{"game": "old"}', b'{"game": "new"}'))


class N10VersionExactTests(Tmp):
    def test_a_version_merely_starting_with_be788_is_refused(self):
        for version in ("6.0.0-be.7881+5b766a3", "6.0.0-be.788-rc", "6.0.0-be.788.1"):
            shutil.rmtree(self.game / "BepInEx", ignore_errors=True)
            self.existing_bepinex(version)
            error = outcome(deploy.install_plugin_only, self.plugin, self.game, self.state)
            self.assertIsInstance(error, ValueError, version)
            self.assertIn(version, str(error))
            self.assertFalse((self.game / PLUGIN_DIR).exists() or self.state.exists())


class N10InstallRefuseOverwriteTests(Tmp):
    def test_full_install_never_overwrites_an_existing_root(self):
        # H225 probe D2.
        theirs = write(self.game / "winhttp.dll", b"other mod")
        error = outcome(deploy.install, self.stage(), self.game, self.state)
        self.assertIsInstance(error, FileExistsError)
        self.assertIn("winhttp.dll", str(error))
        self.assertEqual(theirs.read_bytes(), b"other mod")
        self.assertFalse((self.game / "BepInEx").exists() or self.state.exists())


class N10UpdateHashTests(Tmp):
    def test_update_plugin_refuses_a_modified_installed_plugin(self):
        # H225 probe D3.
        deploy.install(self.stage(), self.game, self.state)
        installed = deploy.add_plugin(self.plugin, self.game, self.state)
        installed.write_bytes(b"patched by the user")
        new = write(self.tmp / "v2" / "FM26.VisualDiagnostics.dll", b"plugin v2")
        error = outcome(deploy.update_plugin, new, self.game, self.state)
        self.assertIsInstance(error, ValueError)
        self.assertIn("owned deployment hash", str(error))
        self.assertEqual(installed.read_bytes(), b"patched by the user")


class N11LicenceHygieneTests(unittest.TestCase):
    FILES = ("tools/deploy.py", "tools/fifa_pack.py", "tests/test_deploy.py", "tests/test_deploy_h226.py",
             "tests/test_fm26vm_acquire.py", "tests/test_fm26vm_deploy.py", "tests/test_fm26vm_detect.py",
             "tests/test_fm26vm_recipe.py", "tests/test_fifa_pack.py", "tests/test_fifa_pack_v2.py")

    def test_spdx_headers_and_third_party_notices(self):
        missing = [name for name in self.FILES
                   if not (ROOT / name).read_text(encoding="utf-8").startswith("# SPDX-License-Identifier: MIT\n")]
        self.assertEqual(missing, [])
        notices = ROOT / "THIRD-PARTY-NOTICES.md"
        text = notices.read_text(encoding="utf-8") if notices.is_file() else ""
        for needle in ("zstd", "BSD-3-Clause", "GPL-2.0", "BepInEx", "LGPL-2.1", ".NET", "MIT", "not redistributed"):
            self.assertIn(needle, text)


class N4OversizedCoreTests(Tmp):
    def test_oversized_core_dll_is_refused_even_with_an_early_version(self):
        self.existing_bepinex(blob=core_dll("6.0.0-be.788+5b766a3") + b"\0" * 8192)
        with mock.patch.object(deploy, "_MAX_CORE_BYTES", 4096):
            self.assertIsNone(deploy.bepinex_version(self.game))
