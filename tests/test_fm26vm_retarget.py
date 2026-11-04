# SPDX-License-Identifier: MIT
"""fm26vm B7: retarget + skin tools/fm26vm/retarget.py and CLI build-frames.

Moved math (H135/H137/H141/H152/H170) is pinned by characterization hashes computed by
local/spike-224-b7-retarget/gen_char.py with the ORIGINAL sealed-path h137.py/h141.py on SYNTHETIC rigs
(tests/fixtures/fm26vm/b7_synth.py). The real-data test runs only when FM26VM_SPANS points at an `extract-spans`
folder; it requires all 10 frame files to equal the recipe golden (no game bytes are stored in the repository)."""
import hashlib
import importlib.util
import io
import json
import os
import sys
import tempfile
import unittest
from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]
if str(ROOT) not in sys.path:
    sys.path.insert(0, str(ROOT))

from tools.fm26vm import retarget as R  # noqa: E402

_spec = importlib.util.spec_from_file_location("fm26vm_b7_synth", ROOT / "tests" / "fixtures" / "fm26vm" / "b7_synth.py")
S = importlib.util.module_from_spec(_spec)
_spec.loader.exec_module(S)


def sha(b):
    return hashlib.sha256(b).hexdigest()


def canon_sha(obj):
    return sha(json.dumps(obj, sort_keys=True, separators=(",", ":")).encode("ascii"))


class Case(unittest.TestCase):
    def refused(self, fn, needle):
        try:
            fn()
        except R.RetargetError as e:
            self.assertIn(needle, str(e))
            return
        except Exception as e:  # a crash is never a refusal
            self.fail("unexpected %r" % e)
        self.fail("not refused")

    def ctx(self):
        rig, sk = S.source_rig(), S.target_skeleton()
        return rig, sk, R.context(rig, sk)


class T_c02_retarget_characterization(Case):
    def test_context(self):
        import struct
        _rig, _sk, I = self.ctx()
        self.assertEqual(sha(struct.pack("<9d", *[x for r in I["C"] for x in r])),
                         "2cd8b3f45b3c7efe3e5be9fe9e8baf953771af2b9bfdc323e7bf59b022fdc671")
        self.assertEqual(sorted(I["Q"]), [3, 4, 5, 6, 8, 9, 10, 11])
        self.assertEqual([n for n, _s, _t in I["matched"]], [j["name"] for j in S.source_rig()["joints"]][:13])

    def test_rotations_and_frames(self):
        rig, _sk, I = self.ctx()
        dec, dofs = S.decoded_clip(rig)
        qs = R.clip_rotations(rig, dec, dofs)
        self.assertEqual(canon_sha(qs), "354b3ee48c4e6b80f549b6cf175e6360ac76e4774affcebc95aa22e67c1fdb07")
        got = {r: sha(R.clip_frames(I, S.pieces(), qs, r)) for r in ((0, 4), (1, 3))}
        self.assertEqual(got, {(0, 4): "d5e6bf721fb0cbee17d5e8627a4841fbcc8fd236a258d3f5dfd1e7cd5a1c524a",
                               (1, 3): "44d6500515f054353386385ab45f3a6555e43e46be1427c876026fb963f14680"})

    def test_moved_guards(self):
        rig, sk = S.source_rig(), S.target_skeleton()
        bad = dict(sk, parents=[0 if n == "LeftArm" else p for n, p in zip(sk["names"], sk["parents"])])
        self.refused(lambda: R.context(rig, bad), "parent mismatch LeftArm")
        dup = dict(sk, names=sk["names"][:-1] + ["Hips"])
        self.refused(lambda: R.context(rig, dup), "duplicate joint names")
        dec, dofs = S.decoded_clip(rig)
        self.refused(lambda: R.clip_rotations(rig, dec, dofs[:-1]), "DofIds 5 != decoded channels 6")


class T_s01_frame_range(Case):
    def test_it(self):
        rig, _sk, I = self.ctx()
        dec, dofs = S.decoded_clip(rig)
        qs = R.clip_rotations(rig, dec, dofs)
        self.refused(lambda: R.clip_frames(I, S.pieces(), qs, (2, 5)), "frame_range (2, 5) outside 5 keys")


def synth_inputs(golden_ok=True):
    rig, sk = S.source_rig(), S.target_skeleton()
    dec, dofs = S.decoded_clip(rig)
    clip = {"id": "c", "asset_key": "5", "fields": {"NumKeys": 5}, "Data": b"", "FrameBlockSizes": [],
            "ConstantPalette": [], "DofIds": dofs}
    rc = {"id": "c", "source_id": "c_A", "asset_key": "5", "frame_range": [1, 3]}
    raw = R.clip_frames(R.context(rig, sk), S.pieces(), R.clip_rotations(rig, dec, dofs), (1, 3))
    golden = {"frames-c_A-f64le.bin": {"bytes": len(raw), "sha256": sha(raw) if golden_ok else "0" * 64}}
    calls = []

    def decode(fields, data, fbs, pal, norm):
        calls.append(norm)
        return dec
    return ({"clips": [rc], "golden": {"frames_f64le": golden}}, {"rig": rig, "clips": [clip]},
            {"skeleton": sk, "pieces": S.pieces()}, decode, calls, raw)


class T_s02_build_frames(Case):
    def test_it(self):
        self.assertTrue(callable(getattr(R, "build_frames", None)), "retarget.build_frames missing")
        rec, bank, meshes, decode, calls, raw = synth_inputs()
        out = R.build_frames(rec, bank, meshes, decode)
        self.assertEqual((list(out), out["frames-c_A-f64le.bin"] == raw, calls), (["frames-c_A-f64le.bin"], True, ["F2"]))


class T_s03_golden_mismatch(Case):
    def test_it(self):
        rec, bank, meshes, decode, _calls, _raw = synth_inputs(golden_ok=False)
        self.refused(lambda: R.build_frames(rec, bank, meshes, decode), "frames-c_A-f64le.bin: 1152 bytes sha256")


class T_s04_golden_missing(Case):
    def test_it(self):
        rec, bank, meshes, decode, _calls, _raw = synth_inputs()
        rec["golden"]["frames_f64le"] = {}
        self.refused(lambda: R.build_frames(rec, bank, meshes, decode), "no entry for frames-c_A-f64le.bin")


class T_s05_asset_pairing(Case):
    def test_it(self):
        rec, bank, meshes, decode, _calls, _raw = synth_inputs()
        bank["clips"][0]["asset_key"] = "6"
        self.refused(lambda: R.build_frames(rec, bank, meshes, decode), "bank clip 6 does not match recipe clip 5")


class CliCase(Case):
    def run_cli(self, argv, build, env):
        from tools.fm26vm import cli
        buf = io.StringIO()

        def extract(ns, o):
            Path(ns.out).mkdir(parents=True)
            (Path(ns.out) / "gameplay-antstate-bank.bin").write_bytes(b"x" * 1000)
            return 0
        try:
            code = cli.main(["build-frames"] + argv, registry=lambda *a: None, fm_running=lambda: False, env=env,
                            out=buf, frames_pipeline=(extract, build))
        except BaseException as e:  # argparse exit or crash: a test failure, never an error
            self.fail("cli raised %r" % e)
        return code, buf.getvalue()

    def work_root(self, tmp):
        return Path(tmp) / "fm26-visual-mod" / "work"


class T_s06_cli_build_frames(CliCase):
    def test_it(self):
        with tempfile.TemporaryDirectory() as tmp:
            dest = Path(tmp) / "frames"
            code, text = self.run_cli(["--out", str(dest)], lambda spans, rec: {"frames-a-f64le.bin": b"xyz"},
                                      {"LOCALAPPDATA": tmp})
            self.assertEqual(code, 0, text)
            rep = json.loads(text)
            self.assertEqual(((dest / "frames-a-f64le.bin").read_bytes(), rep["files"]["frames-a-f64le.bin"]["sha256"]),
                             (b"xyz", sha(b"xyz")))
            self.assertGreaterEqual(rep["peak_work_bytes"], 1000)
            self.assertTrue(Path(rep["work"]).is_relative_to(self.work_root(tmp)))


class T_s07_cli_work_deleted(CliCase):
    def test_it(self):
        with tempfile.TemporaryDirectory() as tmp:
            code, text = self.run_cli(["--out", str(Path(tmp) / "f")], lambda spans, rec: {}, {"LOCALAPPDATA": tmp})
            self.assertEqual((code, list(self.work_root(tmp).iterdir())), (0, []), text)


class T_s08_cli_keep_work(CliCase):
    def test_it(self):
        with tempfile.TemporaryDirectory() as tmp:
            code, text = self.run_cli(["--out", str(Path(tmp) / "f"), "--keep-work"], lambda spans, rec: {},
                                      {"LOCALAPPDATA": tmp})
            self.assertEqual(code, 0, text)
            rep = json.loads(text)
            self.assertEqual((rep["work_kept"], (Path(rep["work"]) / "spans" / "gameplay-antstate-bank.bin").is_file()),
                             (True, True))


class T_s09_cli_error(CliCase):
    def test_it(self):
        def build(spans, rec):
            raise R.RetargetError("frames-a-f64le.bin: differs from the recipe golden")
        with tempfile.TemporaryDirectory() as tmp:
            dest = Path(tmp) / "f"
            code, text = self.run_cli(["--out", str(dest)], build, {"LOCALAPPDATA": tmp})
            self.assertEqual((code, text.startswith("error: frames-a"), dest.exists(), list(self.work_root(tmp).iterdir())),
                             (1, True, False, []))


class T_c03_real_frames_equal_golden(Case):
    def test_it(self):
        spans = os.environ.get("FM26VM_SPANS")
        if not spans:
            self.skipTest("FM26VM_SPANS not set (real decoded spans are never stored in the repository)")
        from tools.fm26vm import cli, recipe
        rec = recipe.load(recipe.DEFAULT_RECIPE)
        out = cli._frames_from_spans(Path(spans), rec)
        self.assertEqual(sorted(out), sorted(rec["golden"]["frames_f64le"]))
        self.assertTrue(all(sha(b) == rec["golden"]["frames_f64le"][n]["sha256"] for n, b in out.items()))
