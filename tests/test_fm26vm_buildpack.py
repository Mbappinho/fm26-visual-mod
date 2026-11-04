# SPDX-License-Identifier: MIT
"""fm26vm B8 (H228): fifa_pack.build_pack_v2_files fed in memory by the clean-room readers, the recipe pack goldens
and the `build-pack` CLI. Real tests run extract-spans + readers on the user's FIFA 17 into a temporary work folder
under %LOCALAPPDATA%/fm26-visual-mod/work (skipped when FIFA 17, the pinned zstd or the installed v2 pack is absent);
the installed pack is only read. CLI tests are synthetic (injected pipeline).

A crash is never a refusal: unexpected exceptions become test failures (assertion RED only).
"""
import argparse
import copy
import hashlib
import io
import json
import os
import shutil
import struct
import sys
import tempfile
import unittest
from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]
if str(ROOT) not in sys.path:
    sys.path.insert(0, str(ROOT))

from tools import fifa_pack  # noqa: E402
from tools.fm26vm import cli, detect, gd, meshset, recipe, retarget  # noqa: E402
from tests.test_fm26vm_recipe import fake_tree  # noqa: E402

INSTALLED = Path(os.environ.get("LOCALAPPDATA") or "/nonexistent") / "fm26-visual-mod" / "packs" / "fifa17-player-v2"
INSTALLED_HEADER_SHA256 = "6c6a7b74bf7e93451d157e649465b485b0f287461d4031269d277aa20f4522f8"  # installed 2026-11-03 19:24
_REAL = {}


def sha(data):
    return hashlib.sha256(data).hexdigest()


def real():
    """(recipe, meshes, frames, spans dir) from the real clean-room pipeline, computed once per process, or None."""
    if "v" not in _REAL:
        _REAL["v"] = None
        if not (INSTALLED / fifa_pack.HEADER).is_file():
            return None
        base = Path(os.environ["LOCALAPPDATA"]) / "fm26-visual-mod" / "work"
        base.mkdir(parents=True, exist_ok=True)
        work = Path(tempfile.mkdtemp(prefix="test-b8-", dir=base))
        _REAL["work"] = work
        reg = None
        if sys.platform == "win32":
            import winreg
            reg = detect.registry_reader(winreg)
        ns = argparse.Namespace(fifa17=None, recipe=str(recipe.DEFAULT_RECIPE), out=str(work / "spans"))
        if cli._extract_spans(ns, reg, detect.FIFA_DEFAULT_FOLDERS, None, io.StringIO(), None, ()) == 0:
            rec = recipe.load(recipe.DEFAULT_RECIPE)
            meshes = meshset.read_meshes_dir(work / "spans", rec)
            bank = gd.read_bank_file(work / "spans" / "gameplay-antstate-bank.bin", rec)
            _REAL["v"] = (rec, meshes, retarget.build_frames(rec, bank, meshes), work / "spans")
    return _REAL["v"]


def tearDownModule():
    if "work" in _REAL:
        shutil.rmtree(_REAL["work"], ignore_errors=True)


class RealCase(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        cls.inputs = real()
        if cls.inputs is None:
            raise unittest.SkipTest("FIFA 17, the pinned zstd or the installed v2 pack is absent")

    def files(self):
        r, m, f, _ = self.inputs
        if "files" not in _REAL:
            try:
                _REAL["files"] = fifa_pack.build_pack_v2_files(r, m, f)
            except Exception as e:  # noqa: BLE001
                self.fail(f"raised {e!r}")
        return _REAL["files"]

    def refusal(self, **kw):
        r, m, f, _ = self.inputs
        try:
            fifa_pack.build_pack_v2_files(kw.get("rec", r), kw.get("meshes", m), kw.get("frames", f))
            return "accepted"
        except ValueError as e:
            return str(e)
        except Exception as e:  # noqa: BLE001
            return f"raised {e!r}"


def validate(obj):
    try:
        recipe.validate(obj)
        return "ok"
    except recipe.RecipeError as e:
        return str(e)
    except Exception as e:  # noqa: BLE001
        return f"raised {e!r}"


class SchemaCase(unittest.TestCase):
    def setUp(self):
        self._tmp = tempfile.TemporaryDirectory()
        self.obj = fake_tree(self._tmp.name)[1]

    def tearDown(self):
        self._tmp.cleanup()


class B8RecipeDirectionField(SchemaCase):
    def test_it(self):
        self.obj["clips"][0]["direction_deg_source_left_positive"] = -2.65
        self.assertEqual(validate(self.obj), "ok")


class B8RecipeDirectionType(SchemaCase):
    def test_it(self):
        self.obj["clips"][0]["direction_deg_source_left_positive"] = "-2.65"
        self.assertIn("direction_deg_source_left_positive has the wrong type", validate(self.obj))


class B8RecipeInstalledHeaderGolden(SchemaCase):
    def test_it(self):
        self.obj["golden"]["pack_v2_installed_header"] = {"bytes": 14227, "sha256": "a" * 64}
        self.assertEqual(validate(self.obj), "ok")


class B8RecipeInstalledHeaderHex(SchemaCase):
    def test_it(self):
        self.obj["golden"]["pack_v2_installed_header"] = {"bytes": 14227, "sha256": "A" * 64}
        self.assertIn("golden.pack_v2_installed_header", validate(self.obj))


class B8PayloadsEqualInstalled(RealCase):
    def test_it(self):
        files = self.files()
        names = [n for n in sorted(os.listdir(INSTALLED)) if n != fifa_pack.HEADER]
        golden = recipe.load(recipe.DEFAULT_RECIPE)["golden"]["pack_v2_files"]
        self.assertEqual(len(names), 11)
        self.assertEqual({n: sha(files.get(n, b"")) for n in names}, {n: sha((INSTALLED / n).read_bytes()) for n in names})
        self.assertEqual({n: sha(files.get(n, b"")) for n in names}, {n: golden[n]["sha256"] for n in names})


class B8HeaderEqualsInstalledExceptSources(RealCase):
    def test_it(self):
        inst_raw = (INSTALLED / fifa_pack.HEADER).read_bytes()
        self.assertEqual(sha(inst_raw), INSTALLED_HEADER_SHA256)
        got = self.files().get(fifa_pack.HEADER, b"")
        try:
            new_sources = json.loads(got)["sources"]
        except Exception:  # noqa: BLE001
            new_sources = None
        expected = json.dumps({**json.loads(inst_raw), "sources": new_sources}, indent=2).encode("utf-8")
        self.assertEqual(got, expected)  # every header byte outside "sources" equals the installed pack header


class B8SourcesClean(RealCase):
    def test_it(self):
        rec = self.inputs[0]
        got = json.loads(self.files()[fifa_pack.HEADER])["sources"]
        self.assertEqual(got, {"pipeline": "fm26vm build-pack: extract-spans -> read-bank (gd) -> read-meshes (meshset) -> vbr + retarget, in memory; no local/ input",
                               "recipe_schema": "fm26vm.recipe.fifa17-v2",
                               "spans_decoded_sha256": {s["id"]: s["decoded_sha256"] for s in rec["spans"]}})


class B8RefusesMissingFrames(RealCase):
    def test_it(self):
        frames = {k: v for k, v in self.inputs[2].items() if k != "frames-walk_A-f64le.bin"}
        self.assertEqual(self.refusal(frames=frames), "frames-walk_A-f64le.bin: missing from the clean-room frames")


class B8RefusesRestMismatch(RealCase):
    def test_it(self):
        frames = dict(self.inputs[2])
        raw = bytearray(frames["frames-walk_A-f64le.bin"])
        raw[0:8] = struct.pack("<d", 0.123)
        frames["frames-walk_A-f64le.bin"] = bytes(raw)
        self.assertEqual(self.refusal(frames=frames), "frame 0 (rest) of walk_A differs from idle_A")



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


FILES = {"fifa-pack.json": b"{}", "clip-a.f32le.bin": bytes(8)}


class CliCase(unittest.TestCase):
    def setUp(self):
        self._tmp = tempfile.TemporaryDirectory()
        self.tmp = Path(self._tmp.name)
        self.env = {"LOCALAPPDATA": str(self.tmp / "la")}
        self.calls = []
        self.dest = self.tmp / "pack"

    def tearDown(self):
        self._tmp.cleanup()

    def pipeline(self, files=FILES, verify_ok=True, extract_code=0):
        def extract(ns, o):
            self.calls.append("extract")
            Path(ns.out).mkdir(parents=True, exist_ok=True)
            (Path(ns.out) / "span.bin").write_bytes(b"x")
            if extract_code:
                o.write("error: extract failed\n")
            return extract_code

        def build(spans, rec):
            self.calls.append("build")
            return dict(files)

        def verify(folder):
            self.calls.append(("verify", sorted(p.name for p in Path(folder).iterdir())))
            return {"ok": verify_ok, "errors": [] if verify_ok else ["schema"], "folder": str(folder)}
        return (extract, build, verify)

    def run_pack(self, *extra, **kw):
        kw.setdefault("fifa_candidates", ())
        return run_cli(["build-pack", "--out", str(self.dest), *extra], env=self.env,
                       pack_pipeline=kw.pop("pipeline", None) or self.pipeline(), **kw)

    def tree(self, path):
        return {p.name: p.read_bytes() for p in Path(path).iterdir()} if Path(path).is_dir() else None

    def work_entries(self):
        root = self.tmp / "la" / "fm26-visual-mod" / "work"
        return sorted(p.name for p in root.iterdir()) if root.is_dir() else []


class B8CliWritesPack(CliCase):
    def test_it(self):
        code, text = self.run_pack()
        self.assertEqual((code, self.tree(self.dest)), (0, FILES), text)


class B8CliRefusesNonEmptyOut(CliCase):
    def test_it(self):
        self.dest.mkdir()
        (self.dest / "keep.txt").write_bytes(b"user")
        code, text = self.run_pack()
        self.assertEqual((code, text.startswith("error:"), "not empty" in text, self.calls, self.tree(self.dest)),
                         (1, True, True, [], {"keep.txt": b"user"}), text)


class B8CliRefusesOutInFifa(CliCase):
    def test_it(self):
        fifa = self.tmp / "FIFA 17"
        fifa.mkdir()
        (fifa / "FIFA17.exe").write_bytes(b"MZ")
        (fifa / "Data").mkdir()
        self.dest = fifa / "pack"
        code, text = self.run_pack("--fifa17", str(fifa))
        self.assertEqual((code, "inside the FIFA 17 folder" in text, self.calls, self.dest.exists()), (1, True, [], False), text)


class B8CliExtractFailure(CliCase):
    def test_it(self):
        code, text = self.run_pack(pipeline=self.pipeline(extract_code=1))
        self.assertEqual((code, text, self.calls, self.dest.exists()), (1, "error: extract failed\n", ["extract"], False), text)


class B8CliVerifyGate(CliCase):
    def test_it(self):
        code, text = self.run_pack(pipeline=self.pipeline(verify_ok=False))
        self.assertEqual((code, text.startswith("error:"), "schema" in text, self.calls[-1], self.dest.exists()),
                         (1, True, True, ("verify", sorted(FILES)), False), text)


class B8CliBuilderError(CliCase):
    def test_it(self):
        def build(spans, rec):
            raise ValueError("frames-walk_A-f64le.bin: missing from the clean-room frames")
        extract, _, verify = self.pipeline()
        code, text = self.run_pack(pipeline=(extract, build, verify))
        self.assertEqual((code, text, self.dest.exists()), (1, "error: frames-walk_A-f64le.bin: missing from the clean-room frames\n", False), text)


class B8CliWorkDeleted(CliCase):
    def test_it(self):
        code, text = self.run_pack()
        self.assertEqual((code, self.work_entries()), (0, []), text)


class B8CliWorkDeletedOnFailure(CliCase):
    def test_it(self):
        code, text = self.run_pack(pipeline=self.pipeline(verify_ok=False))
        self.assertEqual((code, self.work_entries()), (1, []), text)


class B8CliKeepWork(CliCase):
    def test_it(self):
        code, text = self.run_pack("--keep-work")
        kept = self.work_entries()
        self.assertEqual((code, len(kept), json.loads(text)["work_kept"] if code == 0 else text), (0, 1, True), text)


class B8RecipePackGolden(RealCase):
    def test_it(self):
        rec = recipe.load(recipe.DEFAULT_RECIPE)
        files = self.files()
        self.assertEqual(rec["golden"]["pack_v2_files"], {n: {"bytes": len(b), "sha256": sha(b)} for n, b in files.items()})
        inst = (INSTALLED / fifa_pack.HEADER).read_bytes()
        self.assertEqual(rec["golden"].get("pack_v2_installed_header"), {"bytes": len(inst), "sha256": INSTALLED_HEADER_SHA256})


class B8PackFromSpansGoldenCheck(RealCase):
    def test_it(self):
        rec = copy.deepcopy(self.inputs[0])
        rec["golden"]["pack_v2_files"]["clip-walk.f32le.bin"]["sha256"] = "0" * 64
        try:
            cli._pack_from_spans(self.inputs[3], rec)
            got = "accepted"
        except ValueError as e:
            got = str(e)
        except Exception as e:  # noqa: BLE001
            got = f"raised {e!r}"
        self.assertEqual(got, "pack file(s) clip-walk.f32le.bin differ from the recipe golden pack_v2_files")


class B8PackFromSpansReal(RealCase):
    def test_it(self):
        try:
            files = cli._pack_from_spans(self.inputs[3], self.inputs[0])
        except Exception as e:  # noqa: BLE001
            self.fail(f"raised {e!r}")
        golden = recipe.load(recipe.DEFAULT_RECIPE)["golden"]["pack_v2_files"]
        self.assertEqual({n: sha(b) for n, b in files.items()}, {n: g["sha256"] for n, g in golden.items()})
