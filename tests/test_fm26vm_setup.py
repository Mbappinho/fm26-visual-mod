# SPDX-License-Identifier: MIT
"""fm26vm B10 (H229): `setup | uninstall | status` orchestration on temporary fake FM/FIFA trees. Injected detector,
fm-running probe, opener (never the network), pack builder/check and release pin; never the real game. The opt-in
RealSetup case (FM26VM_REAL_SETUP=1) builds the real pack from the user's FIFA 17 into a temporary LOCALAPPDATA and
deploys into a FAKE FM folder. A crash is never a refusal: unexpected exceptions become assertion failures.
"""
import hashlib
import io
import json
import os
import shutil
import sys
import tempfile
import unittest
from pathlib import Path
from unittest import mock

ROOT = Path(__file__).resolve().parents[1]
if str(ROOT) not in sys.path:
    sys.path.insert(0, str(ROOT))

from tools.fm26vm import cli  # noqa: E402
from tests.test_fm26vm_recipe import fake_tree  # noqa: E402
from tests.test_fm26vm_acquire import EXE, FakeOpener, make_zip, pins_for  # noqa: E402

PLUGIN_REL = "BepInEx/plugins/FM26.VisualDiagnostics/FM26.VisualDiagnostics.dll"
AUTO_REL = "BepInEx/visual-diagnostics/fifa-overlay-auto.json"
STEPS = ["detect", "verify-fifa17", "tools", "pack", "deploy", "auto-on", "verify"]
REVIEWED_PLUGIN = "0bd5ebf491a592b738b4acd5c442338c8ca152b6f9ff993b2b9de335aa9198b4"  # 0.10.7 reproducible, path-free, IL-identical to H241-reviewed 26a81240...a1c (H246)


def sha(data):
    return hashlib.sha256(data).hexdigest()


def write(path, data):
    path = Path(path)
    path.parent.mkdir(parents=True, exist_ok=True)
    path.write_bytes(data)
    return path


def core_dll(version):
    return b"MZ" + b"\0" * 62 + "ProductVersion\0\0".encode("utf-16-le") + version.encode("utf-16-le") + b"\0\0" * 5


def tree(folder):
    folder = Path(folder)
    return {p.relative_to(folder).as_posix(): sha(p.read_bytes()) for p in sorted(folder.rglob("*")) if p.is_file()} \
        if folder.exists() else None


def read_json(path):
    return json.loads(Path(path).read_text(encoding="utf-8")) if Path(path).is_file() else {}


class Tmp(unittest.TestCase):
    def setUp(self):
        self._tmp = tempfile.TemporaryDirectory()
        self.tmp = Path(self._tmp.name)
        self.appdata = self.tmp / "appdata"
        self.env = {"LOCALAPPDATA": str(self.appdata)}
        patch = mock.patch.dict(os.environ, self.env)  # deploy's private-pack scan reads os.environ: never the real packs
        patch.start()
        self.addCleanup(patch.stop)
        self.user = self.appdata / "fm26-visual-mod"
        self.pack = self.user / "packs" / "fifa17-player-v2"
        self.dstate = self.user / "install-state.json"
        self.game = self.tmp / "steam" / "Football Manager 26"
        for name, data in (("fm.exe", b"fm exe"), ("GameAssembly.dll", b"ga"), ("UnityPlayer.dll", b"up")):
            write(self.game / name, data)
        self.fifa, rec = fake_tree(self.tmp / "ea")
        write(self.fifa / "FIFA17.exe", b"fifa exe")
        self.recipe = write(self.tmp / "recipe.json", json.dumps(rec).encode())
        self.zstd = make_zip(self.tmp / "zips" / "zstd.zip", {"z/zstd.exe": EXE})
        core = core_dll("6.0.0-be.788+5b766a3")
        self.bepinex = make_zip(self.tmp / "zips" / "bepinex.zip", {"BepInEx/core/BepInEx.Core.dll": core,
                                                                    "winhttp.dll": b"proxy", "doorstop_config.ini": b"[General]"})
        self.pins = {"zstd": pins_for(self.zstd)["zstd"],
                     "bepinex": {"version": "6.0.0-be.788", "url": None, "file": "bepinex.zip",
                                 "sha256": sha(self.bepinex.read_bytes()), "folder": "bepinex-6.0.0-be.788",
                                 "checks": {"BepInEx/core/BepInEx.Core.dll": sha(core)}}}
        self.plugin = write(self.tmp / "release" / "FM26.VisualDiagnostics.dll", b"release plugin bytes")
        self.release_sha = sha(self.plugin.read_bytes())
        self.opener = FakeOpener(self.zstd.read_bytes())
        self.running, self.fail_build, self.builds = False, False, []

        def builder(ctx, fifa, dest, recipe_path):
            self.builds.append(str(dest))
            if self.fail_build:
                raise RuntimeError("build-pack failed: injected failure")
            write(Path(dest) / "fifa-pack.json", b"fake pack")
            write(Path(dest) / "clip-walk.f32le.bin", b"\0" * 12)

        def check(pack, rec):
            header = Path(pack) / "fifa-pack.json"
            if not header.is_file():
                return False, "missing"
            return (True, "golden") if header.read_bytes() == b"fake pack" else (False, "differs")
        self.hooks = {"project": self.tmp / "project", "release": {"sha256": self.release_sha},
                      "pack_builder": builder, "pack_check": check}

    def tearDown(self):
        self._tmp.cleanup()

    def run_cli(self, argv):
        out = io.StringIO()
        try:
            code = cli.main(argv, out=out, env=self.env, registry=lambda *a: None, fm_running=lambda: self.running,
                            fifa_candidates=(), pins=self.pins, opener=self.opener, hooks=self.hooks)
        except SystemExit as e:
            code = f"exit {e.code}"
        except Exception as e:  # noqa: BLE001
            code = f"raised {e!r}"
        return code, out.getvalue()

    def setup_cli(self, *extra, zstd=True, bepinex=True):
        argv = ["setup", "--fm", str(self.game), "--fifa17", str(self.fifa), "--recipe", str(self.recipe),
                "--plugin", str(self.plugin)]
        argv += ["--zstd", str(self.zstd)] if zstd else []
        argv += ["--bepinex", str(self.bepinex)] if bepinex else []
        return self.run_cli(argv + list(extra))

    def status_cli(self):
        return self.run_cli(["status", "--fm", str(self.game), "--fifa17", str(self.fifa), "--recipe", str(self.recipe)])


class ReleasePin(unittest.TestCase):
    def test_it(self):
        data = read_json(ROOT / "tools" / "fm26vm" / "release.json")
        self.assertEqual(data.get("plugin", {}).get("file"), "FM26.VisualDiagnostics.dll")
        self.assertEqual(data.get("plugin", {}).get("sha256"), REVIEWED_PLUGIN)
        self.assertEqual(data.get("plugin", {}).get("version"), "0.10.7")


class StatusFresh(Tmp):
    def test_it(self):
        code, out = self.status_cli()
        got = json.loads(out) if code == 0 else {}
        self.assertEqual((code, got.get("fm"), got.get("fifa17"), (got.get("pack") or {}).get("exists"), got.get("deployment"),
                          (got.get("plugin") or {}).get("sha256"), (got.get("auto") or {}).get("enabled"),
                          (got.get("bepinex") or {}).get("mode"), got.get("setup")),
                         (0, str(self.game), str(self.fifa), False, None, None, False, "absent", None), out)
        self.assertFalse(self.appdata.exists())


class SetupFull(Tmp):
    def test_it(self):
        code, out = self.setup_cli()
        d, st = read_json(self.dstate), read_json(self.user / "state.json")
        self.assertEqual((code, d.get("status"), d.get("mode", "full"), sorted(st.get("steps", {}))),
                         (0, "installed", "full", sorted(STEPS)), out)
        self.assertEqual(sha((self.game / PLUGIN_REL).read_bytes()), self.release_sha)
        self.assertEqual(((self.game / AUTO_REL).is_file(), (self.game / "winhttp.dll").read_bytes(),
                          (self.pack / "fifa-pack.json").read_bytes(), (self.game / "BepInEx" / "interop").exists()),
                         (True, b"proxy", b"fake pack", False))


class SetupFmRunning(Tmp):
    def test_it(self):
        self.running = True
        code, out = self.setup_cli()
        self.assertEqual((code, out.startswith("error:"), "running" in out, (self.game / "BepInEx").exists(), self.pack.exists()),
                         (1, True, True, False, False), out)


class SetupPluginPin(Tmp):
    def test_it(self):
        write(self.plugin, b"tampered plugin")
        code, out = self.setup_cli()
        self.assertEqual((code, "release pin" in out, (self.game / "BepInEx").exists()), (1, True, False), out)


class SetupNoDownload(Tmp):
    def test_it(self):
        code, out = self.setup_cli(zstd=False, bepinex=False)
        self.assertEqual((code, "--download" in out, self.opener.calls), (1, True, []), out)


class SetupDownloadAllowed(Tmp):
    def test_it(self):
        code, out = self.setup_cli("--download", zstd=False)
        self.assertEqual((code, self.opener.calls), (0, [self.pins["zstd"]["url"]]), out)


class SetupPluginOnly(Tmp):
    def test_it(self):
        write(self.game / "BepInEx" / "core" / "BepInEx.Core.dll", core_dll("6.0.0-be.788+5b766a3"))
        write(self.game / "winhttp.dll", b"user proxy")
        code, out = self.setup_cli(bepinex=False)
        self.assertEqual((code, read_json(self.dstate).get("mode"), (self.game / "winhttp.dll").read_bytes(),
                          sha((self.game / PLUGIN_REL).read_bytes()) if (self.game / PLUGIN_REL).is_file() else None),
                         (0, "plugin-only", b"user proxy", self.release_sha), out)


class SetupPluginOnlyWrongVersion(Tmp):
    def test_it(self):
        write(self.game / "BepInEx" / "core" / "BepInEx.Core.dll", core_dll("6.0.0-be.700"))
        code, out = self.setup_cli(bepinex=False)
        self.assertEqual((code, "not supported" in out, (self.game / PLUGIN_REL).exists(), self.dstate.exists()),
                         (1, True, False, False), out)


class SetupIdempotent(Tmp):
    def test_it(self):
        code, out = self.setup_cli()
        self.assertEqual(code, 0, out)

        before = (tree(self.game), tree(self.pack))
        code, out = self.setup_cli()
        actions = {s["step"]: s["action"] for s in json.loads(out)["steps"]} if code == 0 else out
        self.assertEqual(actions, {"detect": "checked", "verify-fifa17": "skip", "tools": "ready", "pack": "skip",
                                   "deploy": "skip", "auto-on": "skip", "verify": "verified"})
        self.assertEqual(((tree(self.game), tree(self.pack)), len(self.builds)), (before, 1))


class SetupPackRefuse(Tmp):
    def test_it(self):
        write(self.pack / "fifa-pack.json", b"someone else's pack")
        code, out = self.setup_cli()
        self.assertEqual((code, "--rebuild-pack" in out, (self.pack / "fifa-pack.json").read_bytes(), self.builds),
                         (1, True, b"someone else's pack", []), out)


class SetupRebuildPack(Tmp):
    def test_it(self):
        write(self.pack / "fifa-pack.json", b"old pack")
        code, out = self.setup_cli("--rebuild-pack")
        moved = sorted((self.user / "pack-backups").glob("pack-*/fifa17-player-v2/fifa-pack.json"))
        self.assertEqual((code, [m.read_bytes() for m in moved], (self.pack / "fifa-pack.json").read_bytes()),
                         (0, [b"old pack"], b"fake pack"), out)


class SetupResume(Tmp):
    def test_it(self):
        self.fail_build = True
        code, out = self.setup_cli()
        st = read_json(self.user / "state.json")
        self.assertEqual((code, (st.get("failed") or {}).get("step"), sorted(st.get("steps", {}))),
                         (1, "pack", ["detect", "tools", "verify-fifa17"]), out)
        self.fail_build = False
        code, out = self.setup_cli()
        st = read_json(self.user / "state.json")
        self.assertEqual((code, "failed" in st, sorted(st.get("steps", {}))), (0, False, sorted(STEPS)), out)


class SetupDryRun(Tmp):
    def test_it(self):
        before = tree(self.game)
        code, out = self.setup_cli("--dry-run")
        got = json.loads(out) if out.startswith("{") else {}
        self.assertEqual((code, got.get("writes"), [s["step"] for s in got.get("steps", [])], [s["action"] for s in got.get("steps", [])][3:6]),
                         (0, False, STEPS, ["build", "install (full)", "enable"]), out)
        self.assertEqual((self.appdata.exists(), tree(self.game)), (False, before))


class Uninstall(Tmp):
    def test_it(self):
        code, out = self.setup_cli()
        self.assertEqual(code, 0, out)

        code, out = self.run_cli(["uninstall"])
        files = sorted(p.relative_to(self.game).as_posix() for p in self.game.rglob("*") if p.is_file())
        self.assertEqual((code, files, read_json(self.dstate).get("status"), self.pack.is_dir()),
                         (0, ["GameAssembly.dll", "UnityPlayer.dll", "fm.exe"], "uninstalled", True), out)


class UninstallPurgePack(Tmp):
    def test_it(self):
        code, out = self.setup_cli()
        self.assertEqual(code, 0, out)

        code, out = self.run_cli(["uninstall", "--purge-pack"])
        self.assertEqual((code, self.pack.exists()), (0, False), out)


class SetupAfterUninstall(Tmp):
    def test_it(self):
        code, out = self.setup_cli()
        self.assertEqual(code, 0, out)

        self.assertEqual(self.run_cli(["uninstall"])[0], 0)
        code, out = self.setup_cli()
        self.assertEqual((code, read_json(self.dstate).get("status"), len(list(self.user.glob("install-state.uninstalled-*.json")))),
                         (0, "installed", 1), out)


class SetupLegacyState(Tmp):
    def test_it(self):
        write(self.tmp / "project" / "local" / "install-state.json", json.dumps({"game": str(self.game), "status": "installed"}).encode())
        code, out = self.setup_cli()
        self.assertEqual((code, "migrate-state" in out, (self.game / "BepInEx").exists()), (1, True, False), out)


class StatusInstalled(Tmp):
    def test_it(self):
        code, out = self.setup_cli()
        self.assertEqual(code, 0, out)

        code, out = self.status_cli()
        got = json.loads(out)
        self.assertEqual((got["deployment"]["status"], got["deployment"]["mode"], got["plugin"]["matches_release"], got["auto"],
                          got["bepinex"]["mode"], got["pack"]["ok"], got["setup"]["steps_done"]),
                         ("installed", "full", True, {"enabled": True, "tracked": True}, "full", True, sorted(STEPS)))


@unittest.skipUnless(os.environ.get("FM26VM_REAL_SETUP") == "1", "opt-in real setup (FM26VM_REAL_SETUP=1)")
class RealSetup(Tmp):
    def test_it(self):
        real = Path(os.environ.get("FM26VM_REAL_LOCALAPPDATA") or os.environ.get("USERPROFILE", "/x") + "/AppData/Local")
        zstd_dir = real / "fm26-visual-mod" / "tools"
        bepinex = ROOT / ".tools" / "downloads" / "bepinex-il2cpp-be788.zip"
        plugin = ROOT / "local" / "spike-246-repro-release" / "release-assets" / "FM26.VisualDiagnostics.dll"
        fifa = Path("C:/Program Files/FIFA 17")
        if not all(p.exists() for p in (zstd_dir / "zstd-1.5.7", bepinex, plugin, fifa)):
            self.skipTest("real inputs absent")
        shutil.copytree(zstd_dir / "zstd-1.5.7", self.user / "tools" / "zstd-1.5.7")
        shutil.copy2(zstd_dir / "zstd-1.5.7.fm26vm-tool.json", self.user / "tools")
        out = io.StringIO()
        argv = ["setup", "--fm", str(self.game), "--fifa17", str(fifa), "--bepinex", str(bepinex), "--plugin", str(plugin)]
        code = cli.main(argv, out=out, env=self.env, registry=lambda *a: None, fm_running=lambda: False, fifa_candidates=(),
                        opener=lambda url: self.fail("network"), hooks={"project": self.tmp / "project"})
        print(out.getvalue())
        self.assertEqual(code, 0, out.getvalue())
        steps = {s["step"]: s for s in json.loads(out.getvalue())["steps"]}
        self.assertEqual((steps["pack"]["kind"], steps["deploy"]["mode"], steps["verify"]["plugin_sha256"]),
                         ("golden", "full", REVIEWED_PLUGIN))
        self.assertFalse((self.game / "BepInEx" / "interop").exists())
        out = io.StringIO()
        code = cli.main(["uninstall"], out=out, env=self.env, registry=lambda *a: None, fm_running=lambda: False,
                        fifa_candidates=(), hooks={"project": self.tmp / "project"})
        print(out.getvalue())
        files = sorted(p.relative_to(self.game).as_posix() for p in self.game.rglob("*") if p.is_file())
        self.assertEqual((code, files), (0, ["GameAssembly.dll", "UnityPlayer.dll", "fm.exe"]), out.getvalue())


class H230StatusStatePath(Tmp):  # H230 31-u-status-state-path
    def test_it(self):
        code, out = self.status_cli()
        got = json.loads(out) if code == 0 else {}
        self.assertEqual(got.get("state_path"), str(self.user / "state.json"), out)


class H230StatusFmNotFound(Tmp):  # H230 33-u-status-fm-not-found
    def test_it(self):
        code, out = self.run_cli(["status", "--fm", str(self.tmp / "nowhere"), "--fifa17", str(self.fifa), "--recipe", str(self.recipe)])
        got = json.loads(out) if code == 0 else {}
        self.assertTrue(str(got.get("fm")).startswith("not found: "), out)


class H230StatusFifaNotFound(Tmp):  # H230 35-u-status-fifa17-not-found
    def test_it(self):
        code, out = self.run_cli(["status", "--fm", str(self.game), "--fifa17", str(self.tmp / "nowhere"), "--recipe", str(self.recipe)])
        got = json.loads(out) if code == 0 else {}
        self.assertTrue(str(got.get("fifa17")).startswith("not found: "), out)


class H230StatusPackKind(Tmp):  # H230 38-u-status-pack-kind
    def test_it(self):
        code, out = self.status_cli()
        got = json.loads(out) if code == 0 else {}
        self.assertEqual((got.get("pack") or {}).get("kind"), "missing", out)


class H230StatusPackPath(Tmp):  # H230 39-u-status-pack-path
    def test_it(self):
        code, out = self.status_cli()
        got = json.loads(out) if code == 0 else {}
        self.assertEqual((got.get("pack") or {}).get("path"), str(self.pack), out)


class H230StatusPackUnchecked(Tmp):  # H230 40-u-status-pack-unchecked
    def test_it(self):
        code, out = self.run_cli(["status", "--fm", str(self.game), "--fifa17", str(self.fifa), "--recipe", str(self.tmp / "no-recipe.json")])
        got = json.loads(out) if code == 0 else {}
        self.assertEqual((code, str((got.get("pack") or {}).get("kind")).startswith("unchecked: ")), (0, True), out)


class H230StatusDeploymentGame(Tmp):  # H230 43-u-status-deployment-game
    def test_it(self):
        code, out = self.setup_cli()
        self.assertEqual(code, 0, out)
        code, out = self.status_cli()
        got = json.loads(out) if code == 0 else {}
        self.assertEqual(Path(str((got.get("deployment") or {}).get("game"))).resolve(), self.game.resolve(), out)


class H230StatusDeploymentState(Tmp):  # H230 44-u-status-deployment-state
    def test_it(self):
        code, out = self.setup_cli()
        self.assertEqual(code, 0, out)
        code, out = self.status_cli()
        got = json.loads(out) if code == 0 else {}
        self.assertEqual((got.get("deployment") or {}).get("state"), str(self.dstate), out)


class H230StatusLegacyFlag(Tmp):  # H230 45-u-status-deployment-legacy-flag
    def test_it(self):
        write(self.tmp / "project" / "local" / "install-state.json", json.dumps({"game": str(self.game), "status": "installed", "mode": "full"}).encode())
        code, out = self.status_cli()
        got = json.loads(out) if code == 0 else {}
        self.assertEqual((got.get("deployment") or {}).get("legacy"), True, out)


class H230StatusGameFromDeployment(Tmp):  # H230 47-u-status-game-from-deployment
    def test_it(self):
        code, out = self.setup_cli()
        self.assertEqual(code, 0, out)
        code, out = self.run_cli(["status", "--fm", str(self.tmp / "nowhere"), "--fifa17", str(self.fifa), "--recipe", str(self.recipe)])
        got = json.loads(out) if code == 0 else {}
        self.assertEqual((got.get("plugin") or {}).get("matches_release"), True, out)


class H230StatusReleaseSha(Tmp):  # H230 50-u-status-plugin-release-sha
    def test_it(self):
        code, out = self.status_cli()
        got = json.loads(out) if code == 0 else {}
        self.assertEqual((got.get("plugin") or {}).get("release_sha256"), self.release_sha, out)


class H230StatusPluginPath(Tmp):  # H230 51-u-status-plugin-path
    def test_it(self):
        code, out = self.status_cli()
        got = json.loads(out) if code == 0 else {}
        self.assertEqual((got.get("plugin") or {}).get("path"), str(self.game / PLUGIN_REL), out)


class H230StatusBepInExVersion(Tmp):  # H230 54-u-status-bepinex-version
    def test_it(self):
        write(self.game / "BepInEx" / "core" / "BepInEx.Core.dll", core_dll("6.0.0-be.788+5b766a3"))
        code, out = self.status_cli()
        got = json.loads(out) if code == 0 else {}
        self.assertEqual((got.get("bepinex") or {}).get("version"), "6.0.0-be.788+5b766a3", out)


class H230StatusBepInExPresent(Tmp):  # H230 55-u-status-bepinex-present
    def test_it(self):
        write(self.game / "BepInEx" / "core" / "BepInEx.Core.dll", core_dll("6.0.0-be.788+5b766a3"))
        code, out = self.status_cli()
        got = json.loads(out) if code == 0 else {}
        self.assertEqual((got.get("bepinex") or {}).get("present"), True, out)


class H230StatusBepInExUntracked(Tmp):  # H230 58-u-status-bepinex-untracked
    def test_it(self):
        write(self.game / "BepInEx" / "core" / "BepInEx.Core.dll", core_dll("6.0.0-be.788+5b766a3"))
        code, out = self.status_cli()
        got = json.loads(out) if code == 0 else {}
        self.assertEqual((got.get("bepinex") or {}).get("mode"), "untracked", out)


class H230StatusSetupFailed(Tmp):  # H230 60-u-status-setup-failed
    def test_it(self):
        self.fail_build = True
        self.setup_cli()
        code, out = self.status_cli()
        got = json.loads(out) if code == 0 else {}
        self.assertEqual(((got.get("setup") or {}).get("failed") or {}).get("step"), "pack", out)


class H230DetectRecord(Tmp):  # H230 62-u-detect-record
    def test_it(self):
        code, out = self.setup_cli()
        d = read_json(self.user / "state.json").get("steps", {}).get("detect", {})
        self.assertEqual((code, [Path(d[k]).resolve() if k in d else None for k in ("fm", "fifa17")]),
                         (0, [self.game.resolve(), self.fifa.resolve()]), out)


class H230VerifyFifa17Refuses(Tmp):  # H230 63-u-verify-fifa17-checks
    def test_it(self):
        write(self.fifa / "Update" / "Patch" / "package.mft", b"MFT")
        code, out = self.setup_cli()
        st = read_json(self.user / "state.json")
        failed = (st.get("failed") or {}).get("step")
        self.assertEqual((code, failed, (self.game / "BepInEx").exists(), self.builds), (1, "verify-fifa17", False, []), out)


class H230PackPostBuildCheck(Tmp):  # H230 67-u-pack-postbuild-check
    def test_it(self):
        self.hooks["pack_builder"] = lambda ctx, fifa, dest, rp: write(Path(dest) / "fifa-pack.json", b"bad build")
        code, out = self.setup_cli()
        st = read_json(self.user / "state.json")
        failed = (st.get("failed") or {}).get("step")
        self.assertEqual((code, failed, (self.game / "BepInEx").exists()), (1, "pack", False), out)


class H230VerifyOriginals(Tmp):  # H230 72-u-verify-originals
    def test_it(self):
        from tools import deploy
        real = deploy.set_auto

        def set_auto(game, state, enabled):
            result = real(game, state, enabled)
            write(self.game / "GameAssembly.dll", b"changed")
            return result
        with mock.patch.object(deploy, "set_auto", set_auto):
            code, out = self.setup_cli()
        st = read_json(self.user / "state.json")
        failed = (st.get("failed") or {}).get("step")
        self.assertEqual((code, failed, 'original FM files differ' in out), (1, "verify", True), out)


class H230VerifyMissing(Tmp):  # H230 73-u-verify-missing
    def test_it(self):
        from tools import deploy
        real = deploy.set_auto

        def set_auto(game, state, enabled):
            result = real(game, state, enabled)
            (self.game / "winhttp.dll").unlink()
            return result
        with mock.patch.object(deploy, "set_auto", set_auto):
            code, out = self.setup_cli()
        st = read_json(self.user / "state.json")
        failed = (st.get("failed") or {}).get("step")
        self.assertEqual((code, failed, 'deployed file(s) missing' in out), (1, "verify", True), out)


class H230VerifyPlugin(Tmp):  # H230 74-u-verify-plugin
    def test_it(self):
        from tools import deploy
        real = deploy.set_auto

        def set_auto(game, state, enabled):
            result = real(game, state, enabled)
            write(self.game / PLUGIN_REL, b"other plugin")
            return result
        with mock.patch.object(deploy, "set_auto", set_auto):
            code, out = self.setup_cli()
        st = read_json(self.user / "state.json")
        failed = (st.get("failed") or {}).get("step")
        self.assertEqual((code, failed, '!= release pin' in out), (1, "verify", True), out)


class H230VerifyAuto(Tmp):  # H230 75-u-verify-auto
    def test_it(self):
        from tools import deploy
        real = deploy.set_auto

        def set_auto(game, state, enabled):
            result = real(game, state, enabled)
            (self.game / AUTO_REL).unlink()
            return result
        with mock.patch.object(deploy, "set_auto", set_auto):
            code, out = self.setup_cli()
        st = read_json(self.user / "state.json")
        failed = (st.get("failed") or {}).get("step")
        self.assertEqual((code, failed, 'auto activation file missing' in out), (1, "verify", True), out)


class H230VerifyPack(Tmp):  # H230 76-u-verify-pack
    def test_it(self):
        from tools import deploy
        real = deploy.set_auto

        def set_auto(game, state, enabled):
            result = real(game, state, enabled)
            write(self.pack / "fifa-pack.json", b"changed pack")
            return result
        with mock.patch.object(deploy, "set_auto", set_auto):
            code, out = self.setup_cli()
        st = read_json(self.user / "state.json")
        failed = (st.get("failed") or {}).get("step")
        self.assertEqual((code, failed, 'pack check failed' in out), (1, "verify", True), out)


class H230IdemModeFromState(Tmp):  # H230 78-u-idem-deploy-mode-from-state
    def test_it(self):
        code, out = self.setup_cli()
        self.assertEqual(code, 0, out)
        code, out = self.setup_cli()
        steps = {s["step"]: s for s in json.loads(out)["steps"]} if code == 0 else {}
        self.assertEqual((code, (steps.get("tools") or {}).get("mode")), (0, "full"), out)


class H230IdemNoZstd(Tmp):  # H230 79-u-idem-zstd-only-if-pack-needed
    def test_it(self):
        code, out = self.setup_cli()
        self.assertEqual(code, 0, out)
        code, out = self.setup_cli()
        steps = {s["step"]: s for s in json.loads(out)["steps"]} if code == 0 else {}
        self.assertEqual((code, (steps.get("tools") or {}).get("tools")), (0, {}), out)


class H230RebuildNeedsZstd(Tmp):  # H230 80-u-idem-zstd-for-rebuild
    def test_it(self):
        code, out = self.setup_cli()
        self.assertEqual(code, 0, out)
        code, out = self.setup_cli("--rebuild-pack")
        steps = {s["step"]: s for s in json.loads(out)["steps"]} if code == 0 else {}
        self.assertEqual((code, sorted((steps.get("tools") or {}).get("tools", {}))), (0, ["zstd"]), out)


class H230DeployOtherFolder(Tmp):  # H230 82-u-idem-deploy-other-folder
    def test_it(self):
        write(self.dstate, json.dumps({"game": str(self.tmp / "other FM"), "status": "installed", "mode": "full"}).encode())
        code, out = self.setup_cli()
        st = read_json(self.user / "state.json")
        failed = (st.get("failed") or {}).get("step")
        self.assertEqual((code, failed, "another FM folder" in out), (1, "deploy", True), out)


class H230DeployPluginMismatch(Tmp):  # H230 84-u-idem-deploy-plugin-mismatch
    def test_it(self):
        code, out = self.setup_cli()
        self.assertEqual(code, 0, out)
        write(self.game / PLUGIN_REL, b"other plugin")
        code, out = self.setup_cli()
        st = read_json(self.user / "state.json")
        failed = (st.get("failed") or {}).get("step")
        self.assertEqual((code, failed, "update-plugin" in out), (1, "deploy", True), out)


class H230AutoAdoptIdentical(Tmp):  # H230 86-s-auto-adopt-identical
    def test_it(self):
        write(self.game / "BepInEx" / "core" / "BepInEx.Core.dll", core_dll("6.0.0-be.788+5b766a3"))
        write(self.game / "winhttp.dll", b"user proxy")
        write(self.game / AUTO_REL, b'{"Enabled":true}')
        code, out = self.setup_cli(bepinex=False)
        step = read_json(self.user / "state.json").get("steps", {}).get("auto-on", {})
        tracked = read_json(self.dstate).get("auto_json") or {}
        self.assertEqual((code, step.get("action"), step.get("sha256"), tracked.get("sha256"), tracked.get("adopted"),
                          (self.game / AUTO_REL).read_bytes()),
                         (0, "adopted", sha(b'{"Enabled":true}'), sha(b'{"Enabled":true}'), True, b'{"Enabled":true}'), out)


class H230AutoRefuseOther(Tmp):  # H230 87-c-auto-refuse-other
    def test_it(self):
        write(self.game / "BepInEx" / "core" / "BepInEx.Core.dll", core_dll("6.0.0-be.788+5b766a3"))
        write(self.game / "winhttp.dll", b"user proxy")
        write(self.game / AUTO_REL, b'{"Enabled": true}')
        code, out = self.setup_cli(bepinex=False)
        st = read_json(self.user / "state.json")
        self.assertEqual((code, (st.get("failed") or {}).get("step"), "untracked" in out, (self.game / AUTO_REL).read_bytes(),
                          "auto_json" in read_json(self.dstate)), (1, "auto-on", True, b'{"Enabled": true}', False), out)


class H230DryRunLegacyFull(Tmp):  # H230 88-s-dry-run-legacy-full-wording
    def test_it(self):
        write(self.game / "BepInEx" / "core" / "BepInEx.Core.dll", core_dll("6.0.0-be.788+5b766a3"))
        write(self.tmp / "project" / "local" / "install-state.json", json.dumps({"game": str(self.game), "status": "installed", "mode": "full"}).encode())
        code, out = self.setup_cli("--dry-run", bepinex=False)
        steps = {s["step"]: s for s in json.loads(out)["steps"]} if out.startswith("{") else {}
        self.assertEqual(((steps.get("tools") or {}).get("mode"), (steps.get("deploy") or {}).get("action")),
                         ("full (legacy state, migrate first)", "blocked"), out)


class H230FmStartedBeforeDeploy(Tmp):  # H230 89-s-fm-recheck-before-deploy
    def test_it(self):
        inner = self.hooks["pack_builder"]

        def builder(ctx, fifa, dest, recipe_path):
            inner(ctx, fifa, dest, recipe_path)
            self.running = True  # FM started while the pack was being built
        self.hooks["pack_builder"] = builder
        code, out = self.setup_cli()
        st = read_json(self.user / "state.json")
        self.assertEqual((code, (st.get("failed") or {}).get("step"), "running" in out, (self.game / "BepInEx").exists()),
                         (1, "deploy", True, False), out)


class H239UninstallFmRunning(Tmp):  # H239 02 O10
    def test_it(self):
        code, out = self.setup_cli()
        self.assertEqual(code, 0, out)
        self.running = True
        before = tree(self.game)
        code, out = self.run_cli(["uninstall"])
        self.assertEqual((code, "running" in out, read_json(self.dstate).get("status"), tree(self.game)),
                         (1, True, "installed", before), out)


class H239UninstallFmStartedLate(Tmp):  # H239 05 O10
    def test_it(self):
        code, out = self.setup_cli()
        self.assertEqual(code, 0, out)
        calls = []

        def running():
            calls.append(1)
            return len(calls) > 1  # closed at the orchestrator check, started before deploy.uninstall
        out = io.StringIO()
        try:
            code = cli.main(["uninstall"], out=out, env=self.env, registry=lambda *a: None, fm_running=running,
                            fifa_candidates=(), pins=self.pins, opener=self.opener, hooks=self.hooks)
        except Exception as e:  # noqa: BLE001
            code = f"raised {e!r}"
        self.assertEqual((code, "running" in out.getvalue(), read_json(self.dstate).get("status"),
                          (self.game / "winhttp.dll").is_file()), (1, True, "installed", True), out.getvalue())


class H239AutoAdoptWithoutState(Tmp):  # H239 06 N5
    def test_it(self):
        from tools.fm26vm import orchestrate
        write(self.game / AUTO_REL, b'{"Enabled":true}')
        ctx = orchestrate.Ctx(env=self.env, registry=lambda *a: None, project=self.tmp / "project",
                              release={"sha256": self.release_sha})
        try:
            orchestrate._step_auto(ctx, None, {"steps": {}}, {"fm": self.game}, False)
            got = "returned"
        except Exception as e:  # noqa: BLE001
            got = type(e).__name__
        self.assertEqual((got, self.dstate.exists(), (self.game / AUTO_REL).read_bytes()),
                         ("SetupError", False, b'{"Enabled":true}'))


class H239DryRunWrongBepInEx(Tmp):  # H239 07 N2
    def test_it(self):
        write(self.game / "BepInEx" / "core" / "BepInEx.Core.dll", core_dll("6.0.0-be.700"))
        before = tree(self.game)
        code, out = self.setup_cli("--dry-run", bepinex=False)
        steps = {s["step"]: s for s in json.loads(out)["steps"]} if out.startswith("{") else {}
        step = steps.get("deploy") or {}
        self.assertEqual((code, step.get("action"), "not supported" in step.get("detail", ""), tree(self.game)),
                         (1, "blocked", True, before), out)


class H239DryRunUntrackedPluginFolder(Tmp):  # H239 08 N2
    def test_it(self):
        write(self.game / "BepInEx" / "core" / "BepInEx.Core.dll", core_dll("6.0.0-be.788+5b766a3"))
        write(self.game / PLUGIN_REL, b"someone's plugin")
        before = tree(self.game)
        code, out = self.setup_cli("--dry-run", bepinex=False)
        steps = {s["step"]: s for s in json.loads(out)["steps"]} if out.startswith("{") else {}
        step = steps.get("deploy") or {}
        self.assertEqual((code, step.get("action"), "overwrite" in step.get("detail", ""), tree(self.game)),
                         (1, "blocked", True, before), out)


class H239UninstallPluginOnlyAutoFolder(Tmp):  # H239 13 M1
    def test_it(self):
        write(self.game / "BepInEx" / "core" / "BepInEx.Core.dll", core_dll("6.0.0-be.788+5b766a3"))
        write(self.game / "winhttp.dll", b"user proxy")
        code, out = self.setup_cli(bepinex=False)
        self.assertEqual(code, 0, out)
        code, out = self.run_cli(["uninstall"])
        self.assertEqual((code, (self.game / AUTO_REL).parent.exists(),
                          (self.game / "BepInEx" / "core" / "BepInEx.Core.dll").is_file()), (0, False, True), out)
