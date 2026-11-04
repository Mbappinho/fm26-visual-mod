# SPDX-License-Identifier: MIT
"""fm26vm B2: public FIFA 17 recipe schema + streaming read-only verify-fifa17 (synthetic fake FIFA trees only)."""
import copy
import hashlib
import io
import json
import sys
import tempfile
import unittest
from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]
if str(ROOT) not in sys.path:
    sys.path.insert(0, str(ROOT))

from tools.fm26vm import cli, recipe  # noqa: E402

RECIPE_FILE = ROOT / "tools" / "fm26vm" / "recipe-fifa17-v2.json"
CAS = bytes(range(256)) * 40


def touch(path, data):
    path = Path(path)
    path.parent.mkdir(parents=True, exist_ok=True)
    path.write_bytes(data)
    return path


def fake_tree(tmp):
    """Fake FIFA 17 folder + matching recipe (2 fingerprints, 2 spans in one fake CAS)."""
    root = Path(tmp) / "FIFA 17"
    files = {"Info_Win64_retail.BuildSettings": b"changelist 1", "Update/Patch/package.mft": b"mft"}
    for name, data in files.items():
        touch(root / name, data)
    touch(root / "Data/Win32/superbundlelayout/p/cas_01.cas", CAS)
    spans = [(100, 300), (5000, 1000)]
    obj = {
        "schema": "fm26vm.recipe.fifa17-v2",
        "note": "synthetic",
        "supported_build": {"platform": "PC", "changelist": 1, "patch": "final",
                            "message": "unsupported FIFA 17 build: only PC changelist 3175939 + final patch"},
        "fingerprints": [{"path": n, "bytes": len(d), "sha256": hashlib.sha256(d).hexdigest()} for n, d in files.items()],
        "spans": [{"id": f"s{i}", "role": "mesh-res", "asset": "content/x", "path": "Data/Win32/superbundlelayout/p/cas_01.cas",
                   "offset": o, "size": s, "sha1": hashlib.sha1(CAS[o:o + s]).hexdigest(),
                   "decoded_size": s, "decoded_sha256": "0" * 64} for i, (o, s) in enumerate(spans)],
        "clips": [{"id": "walk", "source_id": "walk_A", "asset_key": "1", "asset_name": "x", "frame_range": [0, 58],
                   "loop": True, "tier": "walk", "direction": "forward", "keys": 59, "keys_per_cycle": 29.5,
                   "cycles_in_loop": 2, "cycle_origin_key": 28, "native_speed_mps": 1.9}],
        "golden": {"decoded_bank": {"bytes": 1, "sha256": "0" * 64}, "meshes": {}, "frames_f64le": {}, "pack_v2_files": {},
                   "pack_v2_build_json_sha256": "0" * 64, "gd_reader_sha256": "0" * 64,
                   "meshset_reader_sha256": "0" * 64},
    }
    return root, obj


class Tmp(unittest.TestCase):
    def setUp(self):
        self._tmp = tempfile.TemporaryDirectory()
        self.tmp = Path(self._tmp.name)
        self.root, self.recipe = fake_tree(self.tmp)

    def tearDown(self):
        self._tmp.cleanup()


class UnknownTopFieldTests(Tmp):
    def test_unknown_top_level_field_is_refused(self):
        bad = dict(self.recipe, payload_b64="AAAA")
        with self.assertRaises(recipe.RecipeError) as ctx:
            recipe.validate(bad)
        self.assertIn("payload_b64", str(ctx.exception))


class MissingTopFieldTests(Tmp):
    def test_missing_top_level_field_is_refused(self):
        bad = dict(self.recipe)
        del bad["spans"]
        with self.assertRaises(recipe.RecipeError) as ctx:
            recipe.validate(bad)
        self.assertIn("spans", str(ctx.exception))


class SpanFieldTests(Tmp):
    def test_span_with_unknown_field_is_refused(self):
        bad = copy.deepcopy(self.recipe)
        bad["spans"][1]["bytes_b64"] = "AAAA"
        with self.assertRaises(recipe.RecipeError) as ctx:
            recipe.validate(bad)
        self.assertIn("spans[1]", str(ctx.exception))


class ClipFieldTests(Tmp):
    def test_clip_missing_field_is_refused(self):
        bad = copy.deepcopy(self.recipe)
        del bad["clips"][0]["cycle_origin_key"]
        with self.assertRaises(recipe.RecipeError) as ctx:
            recipe.validate(bad)
        self.assertIn("clips[0]", str(ctx.exception))


class SpanSha1Tests(Tmp):
    def test_span_sha1_must_be_40_lowercase_hex(self):
        bad = copy.deepcopy(self.recipe)
        bad["spans"][0]["sha1"] = "F64AAF2C"
        with self.assertRaises(recipe.RecipeError) as ctx:
            recipe.validate(bad)
        self.assertIn("sha1", str(ctx.exception))


class ShippedRecipeFileTests(unittest.TestCase):
    def test_shipped_recipe_has_14_spans_and_10_clips(self):
        self.assertTrue(RECIPE_FILE.is_file(), f"{RECIPE_FILE} missing")
        obj = json.loads(RECIPE_FILE.read_text(encoding="utf-8"))
        self.assertEqual((len(obj["spans"]), len(obj["clips"])), (14, 10))
        self.assertEqual(obj["spans"][0]["sha1"], "f64aaf2c90ce5c479e280a993002c5d220341c55")


class LoadTests(unittest.TestCase):
    def test_load_validates_the_shipped_recipe(self):
        self.assertEqual(recipe.load(RECIPE_FILE).get("schema"), "fm26vm.recipe.fifa17-v2")


class VerifyOkTests(Tmp):
    def test_matching_tree_passes(self):
        report = recipe.verify_fifa17(self.root, self.recipe)
        self.assertEqual((report.get("ok"), report.get("spans"), report.get("fingerprints")), (True, 2, 2))


class VerifyFingerprintTests(Tmp):
    def test_other_build_settings_is_an_unsupported_build(self):
        touch(self.root / "Info_Win64_retail.BuildSettings", b"changelist 2")
        with self.assertRaises(recipe.VerifyError) as ctx:
            recipe.verify_fifa17(self.root, self.recipe)
        self.assertIn("unsupported FIFA 17 build: only PC changelist 3175939 + final patch", str(ctx.exception))
        self.assertIn("Info_Win64_retail.BuildSettings", str(ctx.exception))


class VerifySpanTests(Tmp):
    def test_changed_span_byte_is_an_unsupported_build(self):
        cas = bytearray(CAS)
        cas[5999] ^= 1
        touch(self.root / "Data/Win32/superbundlelayout/p/cas_01.cas", bytes(cas))
        with self.assertRaises(recipe.VerifyError) as ctx:
            recipe.verify_fifa17(self.root, self.recipe)
        self.assertIn("unsupported FIFA 17 build", str(ctx.exception))
        self.assertIn("s1", str(ctx.exception))


class VerifyBoundedReadTests(Tmp):
    def test_reads_only_fingerprints_and_span_ranges(self):
        counted = []

        class Counting(io.FileIO):
            def read(self, n=-1):
                if n is None or n < 0:
                    raise AssertionError("unbounded read")
                b = super().read(n)
                counted.append(len(b))
                return b

        def opener(path, mode="r"):
            assert mode == "rb", mode
            return Counting(path, "r")

        report = recipe.verify_fifa17(self.root, self.recipe, open_fn=opener)
        expected = sum(f["bytes"] for f in self.recipe["fingerprints"]) + sum(s["size"] for s in self.recipe["spans"])
        self.assertEqual((sum(counted), report.get("bytes_read")), (expected, expected))


class VerifyMissingFileTests(Tmp):
    def test_missing_cas_is_a_verify_error(self):
        (self.root / "Data/Win32/superbundlelayout/p/cas_01.cas").unlink()
        try:
            recipe.verify_fifa17(self.root, self.recipe)
        except recipe.VerifyError as e:
            self.assertIn("cas_01.cas", str(e))
            return
        except Exception as e:  # noqa: BLE001 - wrong exception type is the failure under test
            self.fail(f"expected VerifyError, got {e!r}")
        self.fail("expected VerifyError")


class VerifyShortFileTests(Tmp):
    def test_truncated_cas_is_an_unsupported_build(self):
        self.recipe["spans"][1]["size"] = 6000
        self.recipe["spans"][1]["sha1"] = hashlib.sha1(CAS[5000:]).hexdigest()
        with self.assertRaises(recipe.VerifyError) as ctx:
            recipe.verify_fifa17(self.root, self.recipe)
        self.assertIn("s1", str(ctx.exception))


class CliVerifyTests(Tmp):
    def test_verify_fifa17_exit_codes(self):
        rpath = touch(self.tmp / "recipe.json", json.dumps(self.recipe).encode())
        touch(self.root / "FIFA17.exe", b"exe")
        (self.root / "Data").mkdir(exist_ok=True)
        out = io.StringIO()
        try:
            code = cli.main(["verify-fifa17", "--fifa17", str(self.root), "--recipe", str(rpath)], out=out,
                            registry=lambda *a: None, fm_running=lambda: False, fifa_candidates=())
        except SystemExit as e:
            self.fail(f"argparse rejected verify-fifa17 ({e.code})")
        self.assertEqual(code, 0, out.getvalue())
        self.assertTrue(json.loads(out.getvalue())["ok"])
        touch(self.root / "Update/Patch/package.mft", b"other")
        out = io.StringIO()
        code = cli.main(["verify-fifa17", "--fifa17", str(self.root), "--recipe", str(rpath)], out=out,
                        registry=lambda *a: None, fm_running=lambda: False, fifa_candidates=())
        self.assertEqual(code, 1)
        self.assertIn("unsupported FIFA 17 build", out.getvalue())


class SpanOffsetTypeTests(Tmp):
    def test_span_offset_must_be_a_non_negative_int(self):
        accepted = []
        for bad in (-1, "100", 1.5, True, None):
            obj = copy.deepcopy(self.recipe)
            obj["spans"][1]["offset"] = bad
            try:
                recipe.validate(obj)
                accepted.append(bad)
            except recipe.RecipeError as e:
                self.assertIn("spans[1]", str(e))
        self.assertEqual(accepted, [])


class SpanSizeTypeTests(Tmp):
    def test_span_size_must_be_a_non_negative_int(self):
        accepted = []
        for bad in (-5, "300", 2.0, False, None):
            obj = copy.deepcopy(self.recipe)
            obj["spans"][0]["size"] = bad
            try:
                recipe.validate(obj)
                accepted.append(bad)
            except recipe.RecipeError as e:
                self.assertIn("spans[0]: size", str(e))
        self.assertEqual(accepted, [])


class FingerprintFieldTests(Tmp):
    def test_fingerprint_unknown_or_missing_field_is_refused(self):
        accepted = []
        extra = copy.deepcopy(self.recipe)
        extra["fingerprints"][0]["mtime"] = 1
        missing = copy.deepcopy(self.recipe)
        del missing["fingerprints"][1]["bytes"]
        for obj in (extra, missing):
            try:
                recipe.validate(obj)
                accepted.append(obj)
            except recipe.RecipeError as e:
                self.assertIn("fingerprints[", str(e))
        self.assertEqual(len(accepted), 0)


class FingerprintBytesTests(Tmp):
    def test_fingerprint_bytes_must_be_a_non_negative_int(self):
        accepted = []
        for bad in (-1, "12", 12.0, True):
            obj = copy.deepcopy(self.recipe)
            obj["fingerprints"][0]["bytes"] = bad
            try:
                recipe.validate(obj)
                accepted.append(bad)
            except recipe.RecipeError as e:
                self.assertIn("fingerprints[0]: bytes", str(e))
        self.assertEqual(accepted, [])


class FingerprintShaTests(Tmp):
    def test_fingerprint_sha256_must_be_64_lowercase_hex(self):
        accepted = []
        good = self.recipe["fingerprints"][1]["sha256"]
        for bad in (good.upper(), good[:-1], good + "0", "g" * 64, None):
            obj = copy.deepcopy(self.recipe)
            obj["fingerprints"][1]["sha256"] = bad
            try:
                recipe.validate(obj)
                accepted.append(bad)
            except recipe.RecipeError as e:
                self.assertIn("fingerprints[1]: sha256", str(e))
        self.assertEqual(accepted, [])


class SupportedBuildFieldTests(Tmp):
    def test_supported_build_unknown_or_missing_field_is_refused(self):
        accepted = []
        extra = copy.deepcopy(self.recipe)
        extra["supported_build"]["region"] = "EU"
        missing = copy.deepcopy(self.recipe)
        del missing["supported_build"]["changelist"]
        for obj in (extra, missing):
            try:
                recipe.validate(obj)
                accepted.append(obj)
            except recipe.RecipeError as e:
                self.assertIn("supported_build", str(e))
        self.assertEqual(len(accepted), 0)


class SupportedBuildMessageTests(Tmp):
    def test_supported_build_message_must_be_a_non_empty_string(self):
        accepted = []
        for bad in ("", "   ", None, 3):
            obj = copy.deepcopy(self.recipe)
            obj["supported_build"]["message"] = bad
            try:
                recipe.validate(obj)
                accepted.append(bad)
            except recipe.RecipeError as e:
                self.assertIn("supported_build: message", str(e))
        self.assertEqual(accepted, [])


class GoldenBankShaTests(Tmp):
    def test_golden_decoded_bank_sha256_must_be_64_lowercase_hex(self):
        accepted = []
        for bad in ("A" * 64, "0" * 63, "", None):
            obj = copy.deepcopy(self.recipe)
            obj["golden"]["decoded_bank"]["sha256"] = bad
            try:
                recipe.validate(obj)
                accepted.append(bad)
            except recipe.RecipeError as e:
                self.assertIn("golden.decoded_bank: sha256", str(e))
        self.assertEqual(accepted, [])


class GoldenFileMapShaTests(Tmp):
    def test_golden_file_map_sha256_must_be_64_lowercase_hex(self):
        accepted = []
        for group in ("meshes", "frames_f64le", "pack_v2_files"):
            obj = copy.deepcopy(self.recipe)
            obj["golden"][group]["x.bin"] = {"bytes": 4, "sha256": "F" * 64}
            try:
                recipe.validate(obj)
                accepted.append(group)
            except recipe.RecipeError as e:
                self.assertIn(f"golden.{group}[x.bin]: sha256", str(e))
        self.assertEqual(accepted, [])


class GoldenBuildJsonShaTests(Tmp):
    def test_golden_pack_build_json_sha256_must_be_64_lowercase_hex(self):
        accepted = []
        for bad in ("0" * 65, "z" * 64, 0):
            obj = copy.deepcopy(self.recipe)
            obj["golden"]["pack_v2_build_json_sha256"] = bad
            try:
                recipe.validate(obj)
                accepted.append(bad)
            except recipe.RecipeError as e:
                self.assertIn("golden.pack_v2_build_json_sha256", str(e))
        self.assertEqual(accepted, [])


class GoldenBytesTests(Tmp):
    def test_golden_bytes_must_be_non_negative_ints(self):
        accepted = []
        bank = copy.deepcopy(self.recipe)
        bank["golden"]["decoded_bank"]["bytes"] = -1
        mesh = copy.deepcopy(self.recipe)
        mesh["golden"]["meshes"]["m.json"] = {"bytes": "12", "sha256": "0" * 64}
        for label, obj in (("golden.decoded_bank: bytes", bank), ("golden.meshes[m.json]: bytes", mesh)):
            try:
                recipe.validate(obj)
                accepted.append(label)
            except recipe.RecipeError as e:
                self.assertIn(label, str(e))
        self.assertEqual(accepted, [])


class VerifyFingerprintSizeTests(Tmp):
    def test_longer_fingerprint_file_with_same_prefix_is_an_unsupported_build(self):
        touch(self.root / "Update/Patch/package.mft", b"mft" + b" extra patch line")
        try:
            recipe.verify_fifa17(self.root, self.recipe)
            refused = None
        except recipe.VerifyError as e:
            refused = str(e)
        self.assertIsNotNone(refused)
        self.assertIn("unsupported FIFA 17 build", refused)
        self.assertIn("package.mft", refused)


class SpanDecodedFieldTests(Tmp):
    def test_span_requires_decoded_size_and_sha256(self):
        for field in ("decoded_size", "decoded_sha256"):
            obj = copy.deepcopy(self.recipe)
            del obj["spans"][0][field]
            with self.assertRaises(recipe.RecipeError) as cm:
                recipe.validate(obj)
            self.assertIn(f"missing field(s) {field}", str(cm.exception))


class SpanDecodedSizeTypeTests(Tmp):
    def test_decoded_size_must_be_a_non_negative_int(self):
        accepted = []
        for bad in (-1, "100", 1.5, True, None):
            obj = copy.deepcopy(self.recipe)
            obj["spans"][1]["decoded_size"] = bad
            try:
                recipe.validate(obj)
                accepted.append(bad)
            except recipe.RecipeError as e:
                self.assertIn("spans[1]: decoded_size", str(e))
        self.assertEqual(accepted, [])


class SpanDecodedShaTests(Tmp):
    def test_decoded_sha256_must_be_64_lowercase_hex(self):
        accepted = []
        for bad in ("A" * 64, "0" * 63, 5, None):
            obj = copy.deepcopy(self.recipe)
            obj["spans"][1]["decoded_sha256"] = bad
            try:
                recipe.validate(obj)
                accepted.append(bad)
            except recipe.RecipeError as e:
                self.assertIn("spans[1]: decoded_sha256", str(e))
        self.assertEqual(accepted, [])


class ShippedDecodedTests(unittest.TestCase):
    def test_shipped_bank_decoded_matches_golden_and_ebx_sizes(self):
        obj = recipe.load(RECIPE_FILE)
        bank = [s for s in obj["spans"] if s["role"] == "bank"]
        self.assertEqual([(s["decoded_size"], s["decoded_sha256"]) for s in bank],
                         [(obj["golden"]["decoded_bank"]["bytes"], obj["golden"]["decoded_bank"]["sha256"])])
        self.assertEqual({s["id"]: s["decoded_size"] for s in obj["spans"]}["render-skeleton"], 100960)


class ShippedRecipeRigTests(unittest.TestCase):
    def test_shipped_recipe_pins_the_animation_rig(self):
        self.assertEqual(recipe.load(RECIPE_FILE).get("rig"), {'key': '2759552836368141229', 'name': 'player_rig_17', 'skeleton_key': '-718769907582999888'})


class RigKeyTests(Tmp):
    def test_rig_key_must_be_a_signed_decimal_string(self):
        self.recipe["rig"] = {"key": "abc", "name": "r", "skeleton_key": "1"}
        with self.assertRaises(recipe.RecipeError):
            recipe.validate(self.recipe)


class ShippedRecipeControllerTests(unittest.TestCase):
    def test_clips_carry_controller_keys(self):
        self.assertEqual([c.get("controller_key") for c in recipe.load(RECIPE_FILE)["clips"]], ['-6739202074407542914', '-4911106137681012630', '-5233529911975644661', '6784529989797722299', '-736720325307156584', '4726731564361812475', '-628932152154109355', '8667145861839071855', '-3615401277997266088', '-9099786055286612827'])


class ShippedRecipeGoldenGdTests(unittest.TestCase):
    def test_golden_reader_output_hash(self):
        self.assertEqual(recipe.load(RECIPE_FILE)["golden"].get("gd_reader_sha256"), '59794b1bbfb9bb3c20033ba79f0ea72d7b12fcac821b12bc2708e46d73912221')
