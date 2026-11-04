# SPDX-License-Identifier: MIT
"""H229: H227 follow-ups (REM1-REM5, T1, T2) on tools/deploy.py and tools/fm26vm. Temporary fake FM trees only;
never the real game, never the network (openers are injected). A crash is never a refusal: unexpected exceptions are
captured as outcomes so every RED fails on an assertion.
"""
import contextlib
import importlib.util
import io
import json
import os
import subprocess
import sys
import tempfile
import unittest
import zipfile
from pathlib import Path
from types import SimpleNamespace
from unittest import mock

ROOT = Path(__file__).resolve().parents[1]
if str(ROOT) not in sys.path:
    sys.path.insert(0, str(ROOT))

from tools.fm26vm import cli, detect, recipe, tools_acquire as acq  # noqa: E402
from tests.test_fm26vm_recipe import fake_tree  # noqa: E402
from tests.test_fm26vm_acquire import EXE, make_zip, pins_for  # noqa: E402

spec = importlib.util.spec_from_file_location("deploy_h229", ROOT / "tools" / "deploy.py")
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


def core_dll(version):
    return b"MZ" + b"\0" * 62 + "ProductVersion\0\0".encode("utf-16-le") + version.encode("utf-16-le") + b"\0\0" * 5


def outcome(fn, *args, **kwargs):
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
    except Exception as e:  # noqa: BLE001
        code = f"{type(e).__name__}: {e}"
    return code, out.getvalue()


def run_cli(argv, **kw):
    out = io.StringIO()
    kw.setdefault("registry", lambda *a: None)
    kw.setdefault("fm_running", lambda: False)
    try:
        code = cli.main(argv, out=out, **kw)
    except SystemExit as e:
        code = f"exit {e.code}"
    except Exception as e:  # noqa: BLE001
        code = f"raised {e!r}"
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
        self.plugin = write(self.tmp / "build" / "FM26.VisualDiagnostics.dll", b"plugin v1")
        self.state = self.tmp / "s.json"

    def tearDown(self):
        self._tmp.cleanup()

    def stage(self):
        stage = self.tmp / "stage"
        write(stage / "BepInEx" / "core" / "BepInEx.Core.dll", core_dll("6.0.0-be.788+5b766a3"))
        write(stage / "winhttp.dll", b"proxy")
        return stage

    def plugin_only(self):
        write(self.game / "BepInEx" / "core" / "BepInEx.Core.dll", core_dll("6.0.0-be.788+5b766a3"))
        write(self.game / "winhttp.dll", b"user proxy")
        deploy.install_plugin_only(self.plugin, self.game, self.state)


class REM3MissingTrackedAutoJson(Tmp):
    def test_it(self):
        self.plugin_only()
        deploy.set_auto(self.game, self.state, True)
        (self.game / AUTO_REL).unlink()
        error = outcome(deploy.uninstall, self.game, self.state, self.tmp / "backups")
        data = json.loads(self.state.read_text(encoding="utf-8"))
        self.assertEqual((repr(error), data["status"], data.get("already_removed")), ("None", "uninstalled", [AUTO_REL]))
        self.assertFalse((self.game / PLUGIN_DIR).exists())
        self.assertTrue((Path(data["backup"]) / PLUGIN_REL).is_file())


class REM2BackupsBesideState(Tmp):
    def test_it(self):
        deploy.install(self.stage(), self.game, self.state)
        code, out = run_main(["uninstall", "--game", str(self.game), "--state", str(self.state)], project=self.project,
                             env=self.env, stopped=lambda: None)
        backup = Path(json.loads(out)["backup"]).parent if code == 0 else None
        self.assertEqual((code, backup, (self.project / "local").exists()), (0, (self.tmp / "backups").resolve(), False))


class REM1TasklistTimeout(Tmp):
    def test_it(self):
        calls = []

        def run(*a, **k):
            calls.append(k)
            return SimpleNamespace(stdout=b"")
        detect.fm_running_checker(run)()
        self.assertGreater(calls[0].get("timeout") or 0, 0)


class REM1TasklistHung(Tmp):
    def test_it(self):
        def hung(*a, **k):
            raise subprocess.TimeoutExpired(a[0], k.get("timeout"))
        error = outcome(detect.fm_running_checker(hung))
        self.assertEqual((type(error).__name__, "Could not check" in str(error)), ("DetectError", True))


class REM1DeployUsesDetect(Tmp):
    def test_it(self):
        seen = []

        def fake_run(cmd, **k):
            seen.append(cmd[0])
            return SimpleNamespace(stdout='"fm.exe","1234","Console","1","2 000 Ko"\r\n', returncode=0)
        with mock.patch.object(deploy.os, "name", "nt"), mock.patch.object(deploy.subprocess, "run", fake_run), \
                mock.patch.object(deploy.subprocess, "CREATE_NO_WINDOW", 0x08000000, create=True):
            error = outcome(deploy.require_game_stopped)
        self.assertEqual((seen, type(error).__name__, "Close Football Manager" in str(error)), (["tasklist"], "RuntimeError", True))


class REM4DownloadErrors(Tmp):
    def test_it(self):
        import urllib.error

        class Broken:
            def __enter__(self):
                return self

            def __exit__(self, *a):
                return False

            def read(self, n):
                raise TimeoutError("read timed out")
        bad = []
        cases = [urllib.error.URLError("no route"), urllib.error.HTTPError("https://x", 403, "Forbidden", {}, None),
                 TimeoutError("timed out"), "midstream"]
        for i, exc in enumerate(cases):
            lad = self.tmp / f"lad{i}"
            z = make_zip(self.tmp / f"in{i}" / "fake.zip", {"z/zstd.exe": EXE})

            def opener(url, exc=exc):
                if exc == "midstream":
                    return Broken()
                raise exc
            code, out = run_cli(["fetch-tools", "--only", "zstd"], env={"LOCALAPPDATA": str(lad)}, opener=opener, pins=pins_for(z))
            left = [p.name for p in (lad / "fm26-visual-mod" / "tools" / "downloads").glob("*")] if lad.exists() else []
            if (code, out.startswith("error: "), left) != (1, True, []):
                bad.append((i, code, out[:100], left))
        self.assertEqual(bad, [])


class REM4CorruptMarker(Tmp):
    def test_it(self):
        lad = self.tmp / "lad"
        z = make_zip(self.tmp / "in" / "fake.zip", {"z/zstd.exe": EXE})
        write(lad / "fm26-visual-mod" / "tools" / "zstd-1.5.7.fm26vm-tool.json", b"{not json")
        code, out = run_cli(["fetch-tools", "--only", "zstd", "--zstd", str(z)], env={"LOCALAPPDATA": str(lad)}, pins=pins_for(z))
        self.assertEqual((code, out.startswith("error: "), "marker" in out), (1, True, True), out)


class REM5FingerprintPath(Tmp):
    def test_it(self):
        _root, rec = fake_tree(self.tmp / "g")
        bad = []
        for value in ["../../outside.txt", "Data/../../x", "Data\\..\\..\\x", "/abs.txt", "\\\\srv\\x", "C:/x", "", 5]:
            obj = json.loads(json.dumps(rec))
            obj["fingerprints"][0]["path"] = value
            error = outcome(recipe.validate, obj)
            if not (isinstance(error, recipe.RecipeError) and "fingerprints[0]" in str(error)):
                bad.append((value, repr(error)))
        self.assertEqual(bad, [])


class T1DownloadCapStreaming(Tmp):
    def test_it(self):
        reads = []

        class Endless:
            def __enter__(self):
                return self

            def __exit__(self, *a):
                return False

            def read(self, n):
                reads.append(n)
                if len(reads) > 3:
                    raise AssertionError("read past the cap")
                return b"x" * n
        folder = self.tmp / "dl"
        with mock.patch.object(acq, "MAX_DOWNLOAD_BYTES", 1 << 20):
            error = outcome(acq._download, {"url": "https://example.invalid/z.zip", "file": "z.zip"}, folder, lambda url: Endless())
        self.assertEqual((type(error).__name__, len(reads), sorted(p.name for p in folder.iterdir())), ("AcquireError", 2, []))


class T2TraversalMessage(Tmp):
    def test_it(self):
        bad = []
        for i, name in enumerate(["../evil.txt", "a/../../evil.txt", "a\\..\\evil.txt"]):
            z = self.tmp / f"z{i}.zip"
            with zipfile.ZipFile(z, "w") as archive:
                archive.writestr("ok/a.txt", b"x")
                archive.writestr(name, b"payload")
            error = outcome(acq.safe_extract, z, self.tmp / f"d{i}")
            if not (isinstance(error, acq.AcquireError) and "parent traversal" in str(error)) or (self.tmp / f"d{i}").exists():
                bad.append((name, repr(error)))
        self.assertEqual(bad, [])
