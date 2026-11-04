# SPDX-License-Identifier: MIT
"""fm26vm H226b hardening (independent review H225 N6-N10 + B7): CLI error lines, strict recipe schema, zip safety
caps, extract-spans cleanup, reader boundary pins, build-frames output guard. Synthetic inputs only.

A crash is never a refusal: helpers turn any unexpected exception into a test failure (assertion RED only).
"""
import hashlib
import io
import json
import struct
import sys
import tempfile
import unittest
import warnings
import zipfile
from pathlib import Path
from unittest import mock

ROOT = Path(__file__).resolve().parents[1]
if str(ROOT) not in sys.path:
    sys.path.insert(0, str(ROOT))

from tools.fm26vm import cas, cli, detect, gd, meshset, recipe, tools_acquire as acq, vdf  # noqa: E402
from tests.test_fm26vm_recipe import fake_tree  # noqa: E402


def run_cli(argv, **kw):
    out = io.StringIO()
    kw.setdefault("registry", lambda *a: None)
    kw.setdefault("fm_running", lambda: False)
    try:
        code = cli.main(argv, out=out, **kw)
    except SystemExit as e:
        code = f"exit {e.code}"
    except Exception as e:  # noqa: BLE001 - an uncaught crash is a failed expectation, not a test error
        code = f"raised {e!r}"
    return code, out.getvalue()


class Tmp(unittest.TestCase):
    def setUp(self):
        self._tmp = tempfile.TemporaryDirectory()
        self.tmp = Path(self._tmp.name)
        self.root, self.recipe = fake_tree(self.tmp / "g")

    def tearDown(self):
        self._tmp.cleanup()

    def write_recipe(self, obj, name="r.json"):
        p = self.tmp / name
        p.write_text(json.dumps(obj), encoding="utf-8")
        return p

    def recipe_refused(self, obj, needle=""):
        try:
            recipe.validate(obj)
        except recipe.RecipeError as e:
            self.assertIn(needle, str(e))
            return
        except Exception as e:  # noqa: BLE001
            self.fail(f"not a RecipeError: {e!r}")
        self.fail("accepted")

    def read_bank_cli(self, rpath, bank):
        return run_cli(["read-bank", "--bank", str(bank), "--recipe", str(rpath), "--out", str(self.tmp / "o.json")])


class N6RecipeMissing(Tmp):
    def test_it(self):
        code, text = self.read_bank_cli(self.tmp / "nope.json", self.tmp / "bank.bin")
        self.assertEqual((code, text.startswith("error:"), "nope.json" in text), (1, True, True), text)


class N6RecipeBadJson(Tmp):
    def test_it(self):
        rpath = self.tmp / "bad.json"
        rpath.write_text("{bad", encoding="utf-8")
        code, text = self.read_bank_cli(rpath, self.tmp / "bank.bin")
        self.assertEqual((code, text.startswith("error:"), "not valid JSON" in text), (1, True, True), text)


class N6FingerprintNotObject(Tmp):
    def test_it(self):
        self.recipe["fingerprints"] = [1]
        self.recipe_refused(self.recipe, "fingerprints[0]: must be a JSON object")


class N6BankMissing(Tmp):
    def test_it(self):
        self.recipe["rig"] = {"key": "3001", "name": "rig_b", "skeleton_key": "1001"}
        code, text = self.read_bank_cli(self.write_recipe(self.recipe), self.tmp / "missing.bin")
        self.assertEqual((code, text.startswith("error:"), "missing.bin" in text), (1, True, True), text)


class N6BankEmpty(Tmp):
    def test_it(self):
        self.recipe["rig"] = {"key": "3001", "name": "rig_b", "skeleton_key": "1001"}
        bank = self.tmp / "empty.bin"
        bank.write_bytes(b"")
        code, text = self.read_bank_cli(self.write_recipe(self.recipe), bank)
        self.assertEqual((code, text.startswith("error: R-")), (1, True), text)


class N6SuppliedZipMissing(Tmp):
    def test_it(self):
        code, text = run_cli(["fetch-tools", "--only", "zstd", "--offline", "--zstd", str(self.tmp / "missing.zip")],
                             env={"LOCALAPPDATA": str(self.tmp)})
        self.assertEqual((code, text.startswith("error:"), "missing.zip" in text), (1, True, True), text)


class N6VdfUnterminatedString(Tmp):
    def test_it(self):
        try:
            vdf.parse('"libraryfolders" { "0" { "path" "C:\\Ste')
        except vdf.VdfError as e:
            self.assertIn("unterminated string", str(e))
            return
        except Exception as e:  # noqa: BLE001
            self.fail(f"not a VdfError: {e!r}")
        self.fail("accepted")


class N6VdfTrailingBackslash(Tmp):
    def test_it(self):
        try:
            vdf.parse('"a" "b\\')
        except vdf.VdfError as e:
            self.assertIn("unterminated string", str(e))
            return
        except Exception as e:  # noqa: BLE001
            self.fail(f"not a VdfError: {e!r}")
        self.fail("accepted")


class N6DetectBrokenVdf(Tmp):
    def test_it(self):
        steam = self.tmp / "Steam"
        (steam / "steamapps").mkdir(parents=True)
        (steam / "steamapps" / "libraryfolders.vdf").write_text('"libraryfolders" { "0" { "path" "C:\\Ste', encoding="utf-8")
        code, text = run_cli(["detect", "--steam", str(steam), "--fifa17", str(self.root)])
        self.assertEqual((code, text.startswith("error:"), "libraryfolders.vdf" in text), (1, True, True), text)


class N7SpanIdCharset(Tmp):
    def test_it(self):
        bad = []
        for value in ["../../escape", "a/b", "a b", "", "x.bin", 5, "s\n"]:
            obj = json.loads(json.dumps(self.recipe))
            obj["spans"][0]["id"] = value
            try:
                recipe.validate(obj)
                bad.append((value, "accepted"))
            except recipe.RecipeError:
                pass
            except Exception as e:  # noqa: BLE001
                bad.append((value, repr(e)))
        self.assertEqual(bad, [])


class N7SpanPathParent(Tmp):
    def test_it(self):
        bad = []
        for value in ["../x.cas", "Data/../../x.cas", "Data\\..\\x.cas", "", 5]:
            obj = json.loads(json.dumps(self.recipe))
            obj["spans"][0]["path"] = value
            try:
                recipe.validate(obj)
                bad.append((value, "accepted"))
            except recipe.RecipeError:
                pass
            except Exception as e:  # noqa: BLE001
                bad.append((value, repr(e)))
        self.assertEqual(bad, [])


class N7SpanPathAbsolute(Tmp):
    def test_it(self):
        bad = []
        for value in ["/Data/x.cas", "\\Data\\x.cas", "\\\\server\\share\\x.cas"]:
            obj = json.loads(json.dumps(self.recipe))
            obj["spans"][0]["path"] = value
            try:
                recipe.validate(obj)
                bad.append((value, "accepted"))
            except recipe.RecipeError:
                pass
            except Exception as e:  # noqa: BLE001
                bad.append((value, repr(e)))
        self.assertEqual(bad, [])


class N7SpanPathDrive(Tmp):
    def test_it(self):
        bad = []
        for value in ["C:/Data/x.cas", "C:Data/x.cas", "Data/x.cas:ads"]:
            obj = json.loads(json.dumps(self.recipe))
            obj["spans"][0]["path"] = value
            try:
                recipe.validate(obj)
                bad.append((value, "accepted"))
            except recipe.RecipeError:
                pass
            except Exception as e:  # noqa: BLE001
                bad.append((value, repr(e)))
        self.assertEqual(bad, [])


class N7ClipFieldTypes(Tmp):
    def test_it(self):
        bad = []
        for field, value in [("id", "a/b"), ("source_id", 5), ("asset_key", "--5"), ("asset_name", None), ("frame_range", [0]),
         ("frame_range", [0, "58"]), ("loop", 1), ("tier", 5), ("direction", None), ("keys", "many"),
         ("keys_per_cycle", "x"), ("cycles_in_loop", -1), ("cycle_origin_key", 1.5), ("native_speed_mps", True),
         ("controller_key", "1x")]:
            obj = json.loads(json.dumps(self.recipe))
            obj["clips"][0][field] = value
            try:
                recipe.validate(obj)
                bad.append((field, value, "accepted"))
            except recipe.RecipeError as e:
                if field not in str(e):
                    bad.append((field, value, str(e)))
            except Exception as e:  # noqa: BLE001
                bad.append((field, value, repr(e)))
        self.assertEqual(bad, [])


class N7RigUnknownKey(Tmp):
    def test_it(self):
        self.recipe["rig"] = {"key": "3001", "name": "rig_b", "skeleton_key": "1001", "extra": 1}
        self.recipe_refused(self.recipe, "rig: unknown field(s) extra")


class N7RigMissingKey(Tmp):
    def test_it(self):
        self.recipe["rig"] = {"key": "3001", "skeleton_key": "1001"}
        self.recipe_refused(self.recipe, "rig: missing field(s) name")


class N7GoldenRequired(Tmp):
    def test_it(self):
        bad = []
        for field in ("decoded_bank", "meshes", "frames_f64le", "pack_v2_files", "pack_v2_build_json_sha256",
                      "gd_reader_sha256", "meshset_reader_sha256"):
            obj = json.loads(json.dumps(self.recipe))
            del obj["golden"][field]
            try:
                recipe.validate(obj)
                bad.append((field, "accepted"))
            except recipe.RecipeError as e:
                if field not in str(e):
                    bad.append((field, str(e)))
            except Exception as e:  # noqa: BLE001
                bad.append((field, repr(e)))
        self.assertEqual(bad, [])


class N7GoldenGdHex(Tmp):
    def test_it(self):
        self.recipe["golden"]["gd_reader_sha256"] = "Z" * 64
        self.recipe_refused(self.recipe, "gd_reader_sha256")


class N7GoldenUnknownKey(Tmp):
    def test_it(self):
        self.recipe["golden"]["extra"] = "0" * 64
        self.recipe_refused(self.recipe, "golden: unknown field(s) extra")


class N7CliOutputGoldenMissing(Tmp):
    def test_it(self):
        got = []
        for field, argv in (("gd_reader_sha256", ["read-bank", "--bank", str(self.tmp / "b.bin")]),
                            ("meshset_reader_sha256", ["read-meshes", "--spans", str(self.tmp)]),
                            ("frames_f64le", ["build-frames"])):
            obj = json.loads(json.dumps(self.recipe))
            del obj["golden"][field]
            rpath = self.write_recipe(obj)
            code, text = run_cli(argv + ["--recipe", str(rpath), "--out", str(self.tmp / "o")],
                                 frames_pipeline=(lambda ns, o: self.fail("extract ran"), lambda s, r: self.fail("build ran")),
                                 env={"LOCALAPPDATA": str(self.tmp)})
            got.append((field, code, text.startswith("error: golden: missing field(s) " + field)))
        self.assertEqual(got, [(f, 1, True) for f in ("gd_reader_sha256", "meshset_reader_sha256", "frames_f64le")])
        self.assertFalse((self.tmp / "o").exists())


class N7RidStrict(Tmp):
    def test_it(self):
        bad = []
        for value in ["--5", "-", "\u0661\u0662", "5 "]:
            obj = json.loads(json.dumps(self.recipe))
            obj["spans"][0]["rid"] = value
            try:
                recipe.validate(obj)
                bad.append((value, "accepted"))
            except recipe.RecipeError:
                pass
            except Exception as e:  # noqa: BLE001
                bad.append((value, repr(e)))
        self.assertEqual(bad, [])


class N7RigKeyStrict(Tmp):
    def test_it(self):
        bad = []
        for k in ("key", "skeleton_key"):
            obj = json.loads(json.dumps(self.recipe))
            obj["rig"] = {"key": "3001", "name": "rig_b", "skeleton_key": "1001"}
            obj["rig"][k] = "--5"
            try:
                recipe.validate(obj)
                bad.append((k, "accepted"))
            except recipe.RecipeError:
                pass
            except Exception as e:  # noqa: BLE001
                bad.append((k, repr(e)))
        self.assertEqual(bad, [])


class N8DuplicateMember(Tmp):
    def test_it(self):
        bad = []
        warnings.simplefilter("ignore")
        for i, name in enumerate(["ok/readme.txt", "OK/README.TXT"]):
            z = self.tmp / f"z{i}.zip"
            with zipfile.ZipFile(z, "w") as archive:
                archive.writestr("ok/readme.txt", b"x")
                archive.writestr(name, b"payload")
            dest = self.tmp / f"d{i}"
            try:
                acq.safe_extract(z, dest)
                bad.append((name, "accepted"))
            except acq.AcquireError:
                if dest.exists() and any(dest.rglob("*")):
                    bad.append((name, "refused but wrote files"))
            except Exception as e:  # noqa: BLE001
                bad.append((name, repr(e)))
        self.assertEqual(bad, [])


class N8ReservedNames(Tmp):
    def test_it(self):
        bad = []
        for i, name in enumerate(["CON", "dir/aux.txt", "nul", "Prn.log", "COM1", "x/com9.dll", "LPT1", "lpt9.txt"]):
            z = self.tmp / f"z{i}.zip"
            with zipfile.ZipFile(z, "w") as archive:
                archive.writestr("ok/readme.txt", b"x")
                archive.writestr(name, b"payload")
            dest = self.tmp / f"d{i}"
            try:
                acq.safe_extract(z, dest)
                bad.append((name, "accepted"))
            except acq.AcquireError:
                if dest.exists() and any(dest.rglob("*")):
                    bad.append((name, "refused but wrote files"))
            except Exception as e:  # noqa: BLE001
                bad.append((name, repr(e)))
        self.assertEqual(bad, [])


class N8TrailingDotSpace(Tmp):
    def test_it(self):
        bad = []
        for i, name in enumerate(["readme.", "dir./x.txt", "readme ", "dir /x.txt"]):
            z = self.tmp / f"z{i}.zip"
            with zipfile.ZipFile(z, "w") as archive:
                archive.writestr("ok/readme.txt", b"x")
                archive.writestr(name, b"payload")
            dest = self.tmp / f"d{i}"
            try:
                acq.safe_extract(z, dest)
                bad.append((name, "accepted"))
            except acq.AcquireError:
                if dest.exists() and any(dest.rglob("*")):
                    bad.append((name, "refused but wrote files"))
            except Exception as e:  # noqa: BLE001
                bad.append((name, repr(e)))
        self.assertEqual(bad, [])


class N8ExtractSizeCap(Tmp):
    def test_it(self):
        z = self.tmp / "big.zip"
        with zipfile.ZipFile(z, "w") as archive:
            archive.writestr("a.bin", b"x" * 600)
            archive.writestr("b.bin", b"x" * 600)
        dest = self.tmp / "d"
        with mock.patch.object(acq, "MAX_EXTRACT_BYTES", 1000, create=True):
            try:
                acq.safe_extract(z, dest)
                got = "accepted"
            except acq.AcquireError as e:
                got = "size" if "1200" in str(e) else str(e)
            except Exception as e:  # noqa: BLE001
                got = repr(e)
        self.assertEqual((got, dest.exists() and any(dest.rglob("*"))), ("size", False))


class N8DownloadSizeCap(Tmp):
    def test_it(self):
        from tests.test_fm26vm_acquire import FakeOpener, make_zip, pins_for, EXE
        z = make_zip(self.tmp / "in" / "fake.zip", {"z/zstd.exe": EXE})
        pins = pins_for(z)
        tools = self.tmp / "tools"
        with mock.patch.object(acq, "MAX_DOWNLOAD_BYTES", z.stat().st_size - 1):
            try:
                acq.acquire("zstd", tools, opener=FakeOpener(z.read_bytes()), pins=pins)
                got = "accepted"
            except acq.AcquireError as e:
                got = "cap" if "download" in str(e) else str(e)
            except Exception as e:  # noqa: BLE001
                got = repr(e)
        left = sorted(p.name for p in (tools / "downloads").iterdir()) if (tools / "downloads").exists() else []
        self.assertEqual((got, left, (tools / "zstd-1.5.7").exists()), ("cap", [], False))


from tests import test_fm26vm_cas as _cas_tests  # noqa: E402


class ExtractCase(_cas_tests.Tmp):
    """Reuses the CliExtractTests fake FIFA tree + fake pinned zstd (no test methods inherited)."""
    tree = _cas_tests.CliExtractTests.tree
    cli = _cas_tests.CliExtractTests.cli

    def run_failing_second_span(self, fail):
        root, rec = self.tree([(_cas_tests.b0(b"zz"), b"zz", "mesh-ebx"), (_cas_tests.b0(b"yy"), b"yy", "mesh-ebx")])
        real, calls = cas.extract_span, []

        def flaky(*a, **kw):
            calls.append(1)
            if len(calls) == 2:
                fail(a, kw)
            return real(*a, **kw)
        dest = self.tmp / "dest"
        with mock.patch.object(cas, "extract_span", flaky):
            code, text = self.cli(["extract-spans", "--fifa17", str(root), "--recipe", str(rec), "--out", str(dest)])
        left = sorted(p.name for p in dest.iterdir()) if dest.exists() else []
        return code, text, left


class N9OsErrorCleanup(ExtractCase):
    def test_it(self):
        def fail(a, kw):
            raise OSError(28, "No space left on device")
        code, text, left = self.run_failing_second_span(fail)
        self.assertEqual((code, text.startswith("error:"), "No space left" in text, left), (1, True, True, []), text)


class N9AnyExceptionCleanup(ExtractCase):
    def test_it(self):
        def fail(a, kw):
            raise RuntimeError("boom")
        code, text, left = self.run_failing_second_span(fail)
        self.assertEqual((code, left), ("raised RuntimeError('boom')", []), text)


class N9WorkNotEmpty(ExtractCase):
    def test_it(self):
        def fail(a, kw):
            (Path(a[6]) / "leftover.part").write_bytes(b"x")
            raise cas.CasError("final", "simulated refusal", "s1")
        code, text, left = self.run_failing_second_span(fail)
        self.assertEqual((code, "simulated refusal" in text, left), (1, True, []), text)


class N10GdNeedBoundary(Tmp):
    def test_it(self):
        import importlib.util
        spec = importlib.util.spec_from_file_location("h226b_gd_synth", ROOT / "tests" / "fixtures" / "fm26vm" / "gd_synth.py")
        S = importlib.util.module_from_spec(spec)
        spec.loader.exec_module(S)
        data = S.positive_model().serialize()[:7]  # one byte short of the 8-byte envelope: [0,8) must not fit in [0,7)
        try:
            gd.read_bank(data, S.recipe(), S.RIG_KEY)
            got = "accepted"
        except gd.GdError as e:
            got = e.rule
        except Exception as e:  # noqa: BLE001
            got = repr(e)
        self.assertEqual(got, "R-BOUNDS")


class N10GdCstrUnterminated(Tmp):
    def test_it(self):
        try:
            got = gd._cstr(b"\0ab\0", 1, 0, 3, "class name")  # NUL at 3 lies outside the table [0,3): unterminated
        except gd.GdError as e:
            got = e.rule
        except Exception as e:  # noqa: BLE001
            got = repr(e)
        self.assertEqual(got, "R-REFL-STRING")


class N10MeshArrayIndexBoundary(Tmp):
    def test_it(self):
        from tests.test_fm26vm_meshset import S as ES, patch, tables, u32, J
        b, _ = ES.build_skeleton()
        narrays = u32(b, 32)  # header array_count
        try:
            meshset.read_skeleton(patch(b, tables(b)["D"] + 20, "I", narrays), J)  # index == array_count: one past the end
            got = "accepted"
        except meshset.MeshError as e:
            got = e.rule
        except Exception as e:  # noqa: BLE001
            got = repr(e)
        self.assertEqual(got, "E-ARR-1")


class B7BuildFramesOutInFifa(Tmp):
    def test_it(self):
        (self.root / "FIFA17.exe").write_bytes(b"MZ")
        dest = self.root / "frames"
        calls = []
        code, text = run_cli(["build-frames", "--fifa17", str(self.root), "--out", str(dest)], env={"LOCALAPPDATA": str(self.tmp)},
                             frames_pipeline=(lambda ns, o: calls.append(ns) or 1, lambda s, r: {}))
        self.assertEqual((code, "inside the FIFA 17 folder" in text, calls, dest.exists()), (1, True, [], False), text)


class B7BuildFramesFifaMissing(Tmp):
    def test_it(self):
        calls = []

        def extract(ns, o):
            calls.append(ns.fifa17)
            o.write("error: FIFA 17 not found\n")
            return 1
        code, text = run_cli(["build-frames", "--out", str(self.tmp / "frames")], env={"LOCALAPPDATA": str(self.tmp)},
                             fifa_candidates=(), frames_pipeline=(extract, lambda s, r: {}))
        self.assertEqual((code, text.startswith("error: FIFA 17 not found"), calls), (1, True, [None]), text)
