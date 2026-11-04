# SPDX-License-Identifier: MIT
"""H171: private pack format v2 (FIFA17 locomotion set from the H170 pipeline). Synthetic data only, except the
skip-if-absent real build test that writes into a temporary folder OUTSIDE the repository."""
import hashlib
import importlib.util
import json
import struct
import tempfile
import unittest
from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]
spec = importlib.util.spec_from_file_location("fifa_pack", ROOT / "tools" / "fifa_pack.py")
fifa_pack = importlib.util.module_from_spec(spec)
spec.loader.exec_module(fifa_pack)

PIECES = (("mesh-1-body_2_0_0_mesh.json", 732, 1140), ("mesh-2-jersey_2_0_0_0_0_mesh.json", 268, 417), ("mesh-3-shorts_2_0_0_mesh.json", 220, 338),
          ("mesh-4-sock_2_0_0_mesh.json", 214, 308), ("mesh-5-shoes_2_0_mesh.json", 140, 196), ("mesh-6-headlod_2_0_mesh.json", 294, 512))
# name -> (tier, direction, frames, cycles) for the plugin-shaped synthetic v2 pack (keys = frames - 1)
V2 = {"idle": ("idle", "none", 3, None), "walk": ("walk", "forward", 9, 2), "jog": ("jog", "forward", 9, 2), "run": ("run", "forward", 9, 2),
      "sprint": ("sprint", "forward", 9, 2), "strafe_right_walk": ("walk", "strafe-right", 9, 2), "strafe_right_jog": ("jog", "strafe-right", 9, 2),
      "strafe_left_jog": ("jog", "strafe-left", 9, 1), "back_walk": ("walk", "backward", 9, 2), "back_jog": ("jog", "backward", 9, 2)}
SPEEDS = {"idle": 0.0, "walk": 1.898, "jog": 3.2, "run": 4.72, "sprint": 7.948, "strafe_right_walk": 1.353, "strafe_right_jog": 3.435,
          "strafe_left_jog": 3.624, "back_walk": 1.106, "back_jog": 3.129}
_PAYLOAD = {}


def payload(frames):
    if frames not in _PAYLOAD:
        n = frames * 1868 * 3
        _PAYLOAD[frames] = struct.pack(f"<{n}f", *[((i % 97) - 48) * 0.01 for i in range(n)])
    return _PAYLOAD[frames]


def meshes():
    out, offset = [], 0
    for file, vertices, triangles in PIECES:
        out.append({"file": file, "vertex_offset": offset, "vertex_count": vertices, "triangle_count": triangles})
        offset += vertices
    return out


def write_plugin_pack_v2(folder, mutate=None):
    """Plugin-shaped v2 pack (1868 vertices, 2911 triangles, the ten declared locomotion clips), synthetic coordinates."""
    folder.mkdir(parents=True)
    indices = []
    for mesh in meshes():
        indices.extend(mesh["vertex_offset"] + (i * 7) % mesh["vertex_count"] for i in range(mesh["triangle_count"] * 3))
    tri = struct.pack(f"<{2911 * 3}H", *indices)
    (folder / "triangles.u16le.bin").write_bytes(tri)
    clips = {}
    for name, (tier, direction, frames, cycles) in V2.items():
        data = payload(frames)
        file = f"clip-{name}.f32le.bin"
        (folder / file).write_bytes(data)
        keys = frames - 1
        clips[name] = {"file": file, "frames": frames, "rest_frame": 0, "first_key_frame": 1, "keys": keys, "bytes": len(data),
                       "sha256": hashlib.sha256(data).hexdigest(), "loop": True, "frame_range": [0, keys - 1], "tier": tier,
                       "direction": direction, "native_speed_mps": SPEEDS[name], "keys_per_cycle": keys / cycles if cycles else None,
                       "cycles_in_loop": cycles, "cycle_origin_key": 1 if cycles else None, "asset_name": name + "_asset", "asset_key": "1"}
    header = {"schema": "fm26.fifa-pack.v2", "label": fifa_pack.LABEL_V2, "vertex_count": 1868, "triangle_count": 2911, "keys_per_second": 30,
              "axis_mapping": {"matrix": [[-1, 0, 0], [0, 1, 0], [0, 0, 1]], "determinant": -1, "flip_winding": True},
              "rest_height": {"extent": 1.84, "head_to_feet_anchor_proxy": 1.65},
              "triangles": {"file": "triangles.u16le.bin", "count": 2911, "bytes": len(tri), "sha256": hashlib.sha256(tri).hexdigest()},
              "clips": clips, "meshes": meshes()}
    if mutate:
        mutate(header, folder)
    (folder / "fifa-pack.json").write_text(json.dumps(header), encoding="utf-8")
    return folder


class VerifyV2Tests(unittest.TestCase):
    def check(self, mutate=None):
        with tempfile.TemporaryDirectory() as tmp:
            return fifa_pack.verify_pack(write_plugin_pack_v2(Path(tmp) / "pack", mutate=mutate))

    def test_plugin_shaped_v2_pack_passes(self):
        result = self.check()
        self.assertTrue(result["ok"], result["errors"])

    def test_each_v2_clip_constraint_is_enforced(self):
        def clip(name, **kw):
            return lambda h, f: h["clips"][name].update(kw)
        cases = {
            "missing sprint": lambda h, f: h["clips"].pop("sprint"),
            "extra one-shot": lambda h, f: h["clips"].update(turn_135=dict(h["clips"]["walk"])),
            "v1 clip names under v2": lambda h, f: h.update(clips={"jog_F2": h["clips"]["jog"], "walk_F2": h["clips"]["walk"]}),
            "loop false": clip("jog", loop=False),
            "loop not bool": clip("jog", loop=1),
            "tier swapped": clip("run", tier="jog"),
            "direction swapped": clip("strafe_right_jog", direction="strafe-left"),
            "idle with speed": clip("idle", native_speed_mps=0.5),
            "speed too low": clip("walk", native_speed_mps=0.1),
            "speed too high": clip("sprint", native_speed_mps=13.0),
            "speed nan": clip("walk", native_speed_mps=float("nan")),
            "speed bool": clip("walk", native_speed_mps=True),
            "kpc missing": clip("walk", keys_per_cycle=None),
            "kpc != keys/cycles": clip("walk", keys_per_cycle=4.5),
            "cycles zero": clip("walk", cycles_in_loop=0, keys_per_cycle=None),
            "cycles float": clip("walk", cycles_in_loop=2.0),
            "kpc below 4": clip("strafe_left_jog", cycles_in_loop=4, keys_per_cycle=2.0),
            "idle kpc": clip("idle", keys_per_cycle=2.0),
            "origin out of range": clip("walk", cycle_origin_key=8),
            "origin negative": clip("walk", cycle_origin_key=-1),
            "origin missing": clip("walk", cycle_origin_key=None),
            "keys mismatch": clip("walk", keys=7),
            "range mismatch": clip("walk", frame_range=[0, 8]),
            "range shape": clip("walk", frame_range=[0]),
            "frames bytes": clip("jog", frames=10),
        }
        for label, mutate in cases.items():
            with self.subTest(label):
                result = self.check(mutate=mutate)
                self.assertFalse(result["ok"], label)

    def test_v2_total_payload_bound_shared_with_plugin(self):
        self.assertEqual(fifa_pack.MAX_PACK_BYTES_V2, 96 * 1024 * 1024)
        def big(h, f):  # test-side correction (cycle 02): one 4000-frame clip (89.7 MB) stays under 96 MiB; two exceed it
            h["clips"]["idle"].update(frames=4000, bytes=4000 * 1868 * 12)
            h["clips"]["walk"].update(frames=4000, bytes=4000 * 1868 * 12)
        result = self.check(mutate=big)
        self.assertIn("pack-bytes", result["errors"])
class BuildV2Tests(unittest.TestCase):
    """build_pack_v2 from an H170-shaped synthetic input (frames-index + proposal + measurements), tiny 6-vertex mesh."""

    def write_input(self, root, mutate_index=None, mutate_proposal=None, mutate_measure=None):
        tspec = importlib.util.spec_from_file_location("t_fifa_pack", ROOT / "tests" / "test_fifa_pack.py")
        t = importlib.util.module_from_spec(tspec)
        tspec.loader.exec_module(t)
        src, mesh_dir = t.write_fixture(root / "src")
        base = json.loads((src / "frames-index.json").read_text())
        rest = struct.unpack("<18d", (src / base["clips"]["jog_F2"]["file"]).read_bytes()[:18 * 8])
        index = {"label": "SYNTHETIC_H170", "dtype": "float64 little-endian", "total_vertices": 6, "meshes": base["meshes"],
                 "frame_layout": "frame 0 = 'rest', then the selected key range a..b inclusive", "clips": {}}
        proposal, rows = {"clips": []}, []
        for k, (name, source) in enumerate(fifa_pack.V2_SOURCES.items()):
            keys = 12 if name != "idle" else 5
            values = list(rest) + [v + 0.001 * f for f in range(1, keys + 1) for v in rest]
            data = struct.pack(f"<{len(values)}d", *values)
            (src / f"frames-{source}-f64le.bin").write_bytes(data)
            speed = 0.0 if name == "idle" else 0.05 + 0.01 * k
            index["clips"][source] = {"file": f"frames-{source}-f64le.bin", "bytes": len(data), "sha256": hashlib.sha256(data).hexdigest(),
                                      "shape": [keys + 1, 6, 3], "asset_key": str(1000 + k), "name": source + "_Cycle", "category": "x",
                                      "range": [0, keys - 1], "loop": True, "native_speed_tgt_units_per_key": speed}
            proposal["clips"].append({"id": source, "source": {"asset_key": str(1000 + k), "name": source + "_Cycle"}, "frame_range": [0, keys - 1],
                                      "loop": True, "native_speed_mps": round(speed * 30, 3), "direction_deg": None if name == "idle" else -90.0})
            rows.append({"asset_key": str(1000 + k), "status": "MEASURED",
                         "src_contact_sequence": [[0, 1, "L"], [2, 4, "R"], [5, 8, "L"], [9, 11, "R"]]})
        for target, mutate in ((index, mutate_index), (proposal, mutate_proposal), (rows, mutate_measure)):
            if mutate:
                mutate(target)
        (src / "frames-index.json").write_text(json.dumps(index))
        (root / "proposal.json").write_text(json.dumps(proposal))
        (root / "measurements.jsonl").write_text("\n".join(json.dumps(r) for r in rows))
        return src, mesh_dir, root / "proposal.json", root / "measurements.jsonl"

    def test_build_v2_header_declares_timing_role_and_provenance(self):
        with tempfile.TemporaryDirectory() as tmp:
            src, mesh_dir, proposal, measure = self.write_input(Path(tmp))
            out = Path(tmp) / "pack"
            header = fifa_pack.build_pack_v2(src, mesh_dir, proposal, measure, out, repo_root=ROOT, skeleton=src / "skeleton.json")
            self.assertEqual(header["schema"], "fm26.fifa-pack.v2")
            self.assertIn("PRIVATE_NOT_REDISTRIBUTABLE", header["label"])
            self.assertEqual(sorted(header["clips"]), sorted(fifa_pack.V2_CLIPS))
            self.assertEqual(header["keys_per_second"], 30)
            self.assertIn("ASSUMED", header["keys_per_second_basis"])
            self.assertEqual(header["feet_to_metres"], 0.30479)
            self.assertIn("deferred", header["one_shots"])
            walk = header["clips"]["walk"]
            self.assertEqual((walk["frames"], walk["keys"], walk["frame_range"], walk["loop"]), (13, 12, [0, 11], True))
            self.assertEqual((walk["tier"], walk["direction"], walk["cycles_in_loop"], walk["keys_per_cycle"]), ("walk", "forward", 2, 6.0))
            self.assertAlmostEqual(walk["native_speed_mps"], (0.05 + 0.01) * 30)
            self.assertEqual(walk["cycle_origin_key"], 5, "first left-stance interval of >= 4 keys")
            self.assertEqual((walk["asset_key"], walk["source_id"]), ("1001", "walk_A"))
            self.assertEqual(walk["direction_deg_source_left_positive"], -90.0)
            idle = header["clips"]["idle"]
            self.assertEqual((idle["native_speed_mps"], idle["keys_per_cycle"], idle["cycles_in_loop"], idle["cycle_origin_key"]), (0.0, None, None, None))
            self.assertEqual(header["clips"]["strafe_right_walk"]["cycles_in_loop"], 3)
            data = (out / walk["file"]).read_bytes()
            self.assertEqual((len(data), hashlib.sha256(data).hexdigest()), (walk["bytes"], walk["sha256"]))
            result = fifa_pack.verify_pack(out)  # the 6-vertex synthetic pack is refused like the plugin would, integrity intact
            self.assertIn("vertex_count", result["errors"])
            self.assertFalse([e for e in result["errors"] if e.startswith(("sha256:", "size:", "unreadable", "triangle-range", "cycle", "role", "loop", "native"))],
                             result["errors"])

    def test_build_v2_proxy_is_head_joint_to_feet_joint_midpoint_distance(self):
        # H222 (user decision): mirror FM's measure |Character_Head - mid(Character_LeftFoot, Character_RightFoot)| (3D,
        # FifaOverlayActor.Begin) on the render skeleton rest joints: Head (0,1.66,0.04), feet mid (0,0.11,-0.03).
        with tempfile.TemporaryDirectory() as tmp:
            src, mesh_dir, proposal, measure = self.write_input(Path(tmp))
            header = fifa_pack.build_pack_v2(src, mesh_dir, proposal, measure, Path(tmp) / "pack", repo_root=ROOT, skeleton=src / "skeleton.json")
            self.assertAlmostEqual(header["rest_height"]["head_to_feet_anchor_proxy"], (1.55 ** 2 + 0.07 ** 2) ** 0.5, places=12)

    def test_build_v2_header_declares_the_joint_to_joint_proxy_rule(self):
        with tempfile.TemporaryDirectory() as tmp:
            src, mesh_dir, proposal, measure = self.write_input(Path(tmp))
            header = fifa_pack.build_pack_v2(src, mesh_dir, proposal, measure, Path(tmp) / "pack", repo_root=ROOT, skeleton=src / "skeleton.json")
            self.assertEqual(header["rest_height"].get("head_to_feet_anchor_proxy_rule"), "joint-to-joint-rest")
            self.assertIn("joint", header["rest_height"]["proxy_rule"])

    def test_build_v2_header_records_the_skeleton_sha256(self):
        with tempfile.TemporaryDirectory() as tmp:
            src, mesh_dir, proposal, measure = self.write_input(Path(tmp))
            header = fifa_pack.build_pack_v2(src, mesh_dir, proposal, measure, Path(tmp) / "pack", repo_root=ROOT, skeleton=src / "skeleton.json")
            self.assertEqual(header["sources"].get("skeleton_sha256"), hashlib.sha256((src / "skeleton.json").read_bytes()).hexdigest())

    def test_build_v2_refuses_inconsistent_inputs(self):
        def idx(name, **kw):
            return lambda d: d["clips"][fifa_pack.V2_SOURCES[name]].update(kw)
        def prop(name, **kw):
            return lambda p: next(c for c in p["clips"] if c["id"] == fifa_pack.V2_SOURCES[name]).update(kw)
        cases = {
            "sha mismatch": (idx("jog", sha256="0" * 64), None, None),
            "loop false": (idx("jog", loop=False), None, None),
            "range vs shape": (idx("jog", range=[0, 10]), None, None),
            "proposal speed disagrees": (None, prop("run", native_speed_mps=9.0), None),
            "proposal asset disagrees": (None, prop("run", source={"asset_key": "77"}), None),
            "no left stance >= 4 keys": (None, None, lambda rows: rows[1].update(src_contact_sequence=[[0, 2, "L"], [3, 9, "R"]])),
            "fewer than 4 keys per declared cycle": (idx("strafe_right_walk", shape=[12, 6, 3], range=[0, 10]), None, None),
        }
        for label, (mi, mp, mm) in cases.items():
            with self.subTest(label), tempfile.TemporaryDirectory() as tmp:
                src, mesh_dir, proposal, measure = self.write_input(Path(tmp), mi, mp, mm)
                with self.assertRaises(ValueError):
                    fifa_pack.build_pack_v2(src, mesh_dir, proposal, measure, Path(tmp) / "pack", repo_root=ROOT, skeleton=src / "skeleton.json")

    def test_build_v2_refuses_output_inside_repo(self):
        with tempfile.TemporaryDirectory() as tmp:
            src, mesh_dir, proposal, measure = self.write_input(Path(tmp))
            inside = ROOT / "dist" / "pack-v2-should-not-exist"
            with self.assertRaises(ValueError):
                fifa_pack.build_pack_v2(src, mesh_dir, proposal, measure, inside, repo_root=ROOT, skeleton=src / "skeleton.json")
            self.assertFalse(inside.exists())
class DeployV2RefusalTests(unittest.TestCase):
    """deploy.py content refusal covers pack v2 markers (schema alone, label alone) and v2 payload size+sha."""

    def setUp(self):
        dspec = importlib.util.spec_from_file_location("deploy", ROOT / "tools" / "deploy.py")
        self.deploy = importlib.util.module_from_spec(dspec)
        dspec.loader.exec_module(self.deploy)

    def test_v2_schema_marker_alone_refused(self):
        with tempfile.TemporaryDirectory() as tmp:
            stage = Path(tmp) / "stage"
            stage.mkdir()
            (stage / "a.cfg").write_bytes(b'{"schema": "fm26.fifa-pack.v2"}')
            with self.assertRaises(ValueError):
                self.deploy.refuse_private_pack(stage, pack_dirs=[])

    def test_v2_renamed_header_and_payload_refused(self):
        with tempfile.TemporaryDirectory() as tmp:
            pack = write_plugin_pack_v2(Path(tmp) / "packs" / "fifa17-player-v2")
            stage = Path(tmp) / "stage"
            stage.mkdir()
            for original, renamed in (("fifa-pack.json", "notes.txt"), ("clip-sprint.f32le.bin", "sprint.dat")):
                (stage / renamed).write_bytes((pack / original).read_bytes())
                with self.assertRaises(ValueError, msg=renamed):
                    self.deploy.refuse_private_pack(stage, pack_dirs=[pack])
                (stage / renamed).unlink()
            self.deploy.refuse_private_pack(stage, pack_dirs=[pack])


class CliV2Tests(unittest.TestCase):
    def test_default_v2_dir_is_localappdata_sibling_of_v1(self):
        import os
        with tempfile.TemporaryDirectory() as tmp:
            old = os.environ.get("LOCALAPPDATA")
            os.environ["LOCALAPPDATA"] = tmp
            try:
                self.assertEqual(fifa_pack.default_pack_dir(2), Path(tmp) / "fm26-visual-mod" / "packs" / "fifa17-player-v2")
                self.assertEqual(fifa_pack.default_pack_dir(), Path(tmp) / "fm26-visual-mod" / "packs" / "fifa17-player-v1")
            finally:
                if old is None:
                    del os.environ["LOCALAPPDATA"]
                else:
                    os.environ["LOCALAPPDATA"] = old

    def test_build_v2_cli_writes_outside_repo_and_verify_reads_it(self):
        with tempfile.TemporaryDirectory() as tmp:
            src, mesh_dir, proposal, measure = BuildV2Tests().write_input(Path(tmp))
            out = Path(tmp) / "pack"
            code = fifa_pack.main(["build-v2", "--frames", str(src), "--meshes", str(mesh_dir), "--proposal", str(proposal),
                                   "--measurements", str(measure), "--skeleton", str(src / "skeleton.json"), "--out", str(out)])
            self.assertEqual(code, 0)
            self.assertEqual(json.loads((out / "fifa-pack.json").read_text())["schema"], "fm26.fifa-pack.v2")
            self.assertEqual(fifa_pack.main(["verify", "--pack", str(out)]), 1, "6-vertex synthetic pack refused like the plugin")


REAL_FRAMES_V2 = ROOT / "local/spike-171-pack-v2-locomotion-h170-regen"
REAL_H170 = ROOT / "local/spike-170-locomotion-clip-set"
REAL_MESHES = ROOT / "local/spike-140-full-player-mesh-acquisition"
REAL_SKELETON = ROOT / "local/spike-112-experimental-body-skin/data.json"
HAS_REAL_V2 = REAL_SKELETON.is_file() and all((p / n).is_file() for p, n in ((REAL_FRAMES_V2, "frames-index.json"), (REAL_H170, "pack-v2-proposal.json"),
                                                  (REAL_H170, "measurements.jsonl"), (REAL_MESHES, "mesh-5-shoes_2_0_mesh.json")))


class RealV2BuildTests(unittest.TestCase):
    @unittest.skipUnless(HAS_REAL_V2, "private H170/H140 data absent")
    def test_real_v2_build_verifies_and_stays_under_the_memory_bound(self):
        with tempfile.TemporaryDirectory() as tmp:
            out = Path(tmp) / "pack"
            header = fifa_pack.build_pack_v2(REAL_FRAMES_V2, REAL_MESHES, REAL_H170 / "pack-v2-proposal.json", REAL_H170 / "measurements.jsonl", out,
                                            skeleton=REAL_SKELETON)
            result = fifa_pack.verify_pack(out)
            self.assertTrue(result["ok"], result["errors"])
            self.assertEqual(header["axis_mapping"]["matrix"], [[-1, 0, 0], [0, 1, 0], [0, 0, 1]])
            self.assertLessEqual(sum(c["bytes"] for c in header["clips"].values()), fifa_pack.MAX_PACK_BYTES_V2)
            self.assertEqual(header["clips"]["walk"]["asset_name"], "F16_01st1_Cycle_Fwd_Walk_LCad_01")
            self.assertEqual(header["clips"]["sprint"]["keys_per_cycle"], 14.0)

    @unittest.skipUnless(HAS_REAL_V2, "private H170/H140 data absent")
    def test_real_v2_proxy_is_joint_to_joint_and_axes_unchanged(self):
        # H222 golden: H221 joints.py measured |Head - mid(LeftFoot, RightFoot)| = 1.5586257 on the 438-joint rest skeleton
        # (installed vertex proxy was 1.5265728); axis matrix, winding and extent must stay the installed values.
        with tempfile.TemporaryDirectory() as tmp:
            header = fifa_pack.build_pack_v2(REAL_FRAMES_V2, REAL_MESHES, REAL_H170 / "pack-v2-proposal.json", REAL_H170 / "measurements.jsonl",
                                             Path(tmp) / "pack", skeleton=REAL_SKELETON)
            axis = header["axis_mapping"]
            self.assertEqual((axis["matrix"], axis["determinant"], axis["flip_winding"]), ([[-1, 0, 0], [0, 1, 0], [0, 0, 1]], -1, True))
            self.assertEqual(header["rest_height"]["extent"], 1.8399084857691184)
            self.assertAlmostEqual(header["rest_height"]["head_to_feet_anchor_proxy"], 1.5586257181414778, places=9)


if __name__ == "__main__":
    unittest.main()
