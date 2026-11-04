# SPDX-License-Identifier: MIT
"""H154 stage 1: local private FIFA pack tool (never inside the repo/dist)."""
import hashlib
import importlib.util
import json
import os
from pathlib import Path
import struct
import tempfile
import unittest

ROOT = Path(__file__).resolve().parents[1]


def load(name):
    path = ROOT / "tools" / f"{name}.py"
    if not path.exists():
        return None
    spec = importlib.util.spec_from_file_location(name, path)
    module = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(module)
    return module


fifa_pack = load("fifa_pack")
deploy = load("deploy")
REAL_FRAMES = ROOT / "local/spike-152-vbr-animated-player"
REAL_MESHES = ROOT / "local/spike-140-full-player-mesh-acquisition"
REAL_SKELETON = ROOT / "local/spike-112-experimental-body-skin/data.json"
HAS_REAL = REAL_SKELETON.is_file() and (REAL_FRAMES / "frames-index.json").is_file() and (REAL_MESHES / "mesh-5-shoes_2_0_mesh.json").is_file()


def unity_from(matrix, point):
    return [sum(matrix[r][c] * point[c] for c in range(3)) for r in range(3)]


def centroid(points):
    return [sum(p[i] for p in points) / len(points) for i in range(3)]


def synthetic_groups(transform):
    """FIFA-like rest in a 'source' basis produced by transform(unity_point)."""
    base = {
        "LeftFoot": [[-0.13, 0.04, 0.0], [-0.12, 0.05, 0.02]],
        "RightFoot": [[0.13, 0.04, 0.0], [0.12, 0.05, 0.02]],
        "LeftToeBase": [[-0.14, 0.03, 0.18]],
        "RightToeBase": [[0.14, 0.03, 0.18]],
        "Head": [[0.0, 1.7, 0.08], [0.0, 1.75, 0.07]],
    }
    return {k: [transform(p) for p in v] for k, v in base.items()}


# H222: synthetic render skeleton, real index layout (Neck=7, Head=9, LeftFoot=332, LeftToeBase=333, RightFoot=373,
# RightToeBase=374); source axes like FIFA (left at +X, Y up). Joint translations = model column 3.
SKELETON_JOINTS = {"Neck": (7, (0.0, 1.57, 0.02)), "Head": (9, (0.0, 1.66, 0.04)), "LeftFoot": (332, (0.13, 0.11, -0.03)),
                   "LeftToeBase": (333, (0.14, 0.03, 0.12)), "RightFoot": (373, (-0.13, 0.11, -0.03)),
                   "RightToeBase": (374, (-0.14, 0.03, 0.12)), "LeftShoulder": (11, (0.15, 1.45, 0.0))}


def skeleton_names():
    names = [f"Joint{i}" for i in range(438)]
    for name, (i, _) in SKELETON_JOINTS.items():
        names[i] = name
    return names


def write_skeleton(path, names=None):
    model = [[[1.0, 0.0, 0.0, 0.0], [0.0, 1.0, 0.0, 0.0], [0.0, 0.0, 1.0, 0.0], [0.0, 0.0, 0.0, 1.0]] for _ in range(438)]
    for _, (i, t) in SKELETON_JOINTS.items():
        for r in range(3):
            model[i][r][3] = t[r]
    Path(path).write_text(json.dumps({"names": names or skeleton_names(), "model": model}))
    return Path(path)


def write_fixture(folder, vertices=4, frames=(3, 2), names=("jog_F2", "walk_F2")):
    """Two tiny meshes (shoes-like + head-like) plus frames-index, real file shapes."""
    folder.mkdir(parents=True, exist_ok=True)
    meshes_dir = folder / "meshes"
    meshes_dir.mkdir()
    # mesh A: 2 shoe vertices per foot (LeftFoot=332 +X, RightFoot=373 -X), toes, mesh B: head.
    mesh_a = {
        "positions": [[0.13, 0.04, 0.0], [-0.13, 0.04, 0.0], [0.14, 0.03, 0.18], [-0.14, 0.03, 0.18]],
        "triangles": [[0, 1, 2], [1, 3, 2]],
        "bone_indices_raw": [[332, 0, 0, 0], [373, 0, 0, 0], [333, 0, 0, 0], [374, 0, 0, 0]],
        "bone_weights_raw": [[255, 0, 0, 0]] * 4,
        "influence": {"tests": {"direct_global_438": {"bones_used": [332, 333, 373, 374],
                                                      "bone_names_used": ["LeftFoot", "LeftToeBase", "RightFoot", "RightToeBase"]}}},
    }
    mesh_b = {
        "positions": [[0.0, 1.7, 0.08], [0.0, 1.8, 0.07]],
        "triangles": [[0, 1, 0]],
        "bone_indices_raw": [[9, 0, 0, 0], [9, 0, 0, 0]],
        "bone_weights_raw": [[255, 0, 0, 0]] * 2,
        "influence": {"tests": {"direct_global_438": {"bones_used": [9], "bone_names_used": ["Head"]}}},
    }
    (meshes_dir / "mesh-a.json").write_text(json.dumps(mesh_a))
    (meshes_dir / "mesh-b.json").write_text(json.dumps(mesh_b))
    rest = mesh_a["positions"] + mesh_b["positions"]
    total = len(rest)
    clips = {}
    for name, count in zip(names, frames):
        values = []
        for f in range(count):
            for p in rest:
                values.extend([p[0], p[1] + 0.001 * f, p[2]])
        data = struct.pack(f"<{len(values)}d", *values)
        file = f"frames-{name}-f64le.bin"
        (folder / file).write_bytes(data)
        clips[name] = {"file": file, "bytes": len(data), "sha256": hashlib.sha256(data).hexdigest(),
                       "shape": [count, total, 3], "frame_ids": ["rest"] + [f"candidate:{i}" for i in range(count - 1)],
                       "asset_name": name + "_asset"}
    index = {"label": "SYNTHETIC", "dtype": "float64 little-endian", "total_vertices": total,
             "meshes": [{"file": "mesh-a.json", "vertex_offset": 0, "vertex_count": 4, "triangle_count": 2},
                        {"file": "mesh-b.json", "vertex_offset": 4, "vertex_count": 2, "triangle_count": 1}],
             "clips": clips}
    (folder / "frames-index.json").write_text(json.dumps(index))
    write_skeleton(folder / "skeleton.json")
    return folder, meshes_dir


class AxisMappingTests(unittest.TestCase):
    def setUp(self):
        self.assertIsNotNone(fifa_pack, "tools/fifa_pack.py missing")

    def check_unity_rest(self, matrix, groups):
        conv = {k: centroid([unity_from(matrix, p) for p in v]) for k, v in groups.items()}
        feet = centroid([conv["LeftFoot"], conv["RightFoot"]])
        self.assertLess(conv["LeftFoot"][0], conv["RightFoot"][0], "Unity left foot must be at -X")
        self.assertGreater(conv["Head"][1] - feet[1], 1.0, "head above feet along +Y")
        self.assertGreater(centroid([conv["LeftToeBase"], conv["RightToeBase"]])[2], feet[2], "toes forward along +Z")

    def test_fifa_convention_left_plus_x_is_mirrored(self):
        groups = synthetic_groups(lambda p: [-p[0], p[1], p[2]])  # FIFA: left foot at +X
        mapping = fifa_pack.derive_axis_mapping(groups)
        self.assertEqual(mapping["matrix"], [[-1, 0, 0], [0, 1, 0], [0, 0, 1]])
        self.assertEqual(mapping["determinant"], -1)
        self.assertTrue(mapping["flip_winding"])
        self.check_unity_rest(mapping["matrix"], groups)

    def test_other_basis_z_up_is_derived_not_guessed(self):
        groups = synthetic_groups(lambda p: [p[0], -p[2], p[1]])  # up=+Z, forward=-Y, right=+X
        mapping = fifa_pack.derive_axis_mapping(groups)
        self.assertEqual(mapping["determinant"], 1)
        self.assertFalse(mapping["flip_winding"])
        self.check_unity_rest(mapping["matrix"], groups)

    def test_ambiguous_axes_refused(self):
        groups = synthetic_groups(lambda p: [p[0], p[1], p[2]])
        groups["LeftToeBase"] = [[-0.13, 0.04, 0.0]]
        groups["RightToeBase"] = [[0.13, 0.04, 0.0]]  # toes not forward of feet
        with self.assertRaises(ValueError):
            fifa_pack.derive_axis_mapping(groups)
        missing = synthetic_groups(lambda p: p)
        del missing["Head"]
        with self.assertRaises(ValueError):
            fifa_pack.derive_axis_mapping(missing)

    @unittest.skipUnless(HAS_REAL, "private H152/H140 data absent")
    def test_real_rest_pose_mapping(self):
        index = json.loads((REAL_FRAMES / "frames-index.json").read_text())
        rest = fifa_pack.read_frame(REAL_FRAMES / index["clips"]["jog_F2"]["file"], index["total_vertices"], 0)
        groups = fifa_pack.anchor_groups(index, REAL_MESHES, rest, fifa_pack.load_skeleton(REAL_SKELETON)["names"])
        mapping = fifa_pack.derive_axis_mapping(groups)
        self.assertEqual(mapping["matrix"], [[-1, 0, 0], [0, 1, 0], [0, 0, 1]])
        self.assertTrue(mapping["flip_winding"])
        self.check_unity_rest(mapping["matrix"], groups)


class AnchorPairingTests(unittest.TestCase):
    """H222: a vertex bone index is named by the render skeleton (names[index]); the mesh's bones_used (sorted by index)
    and bone_names_used (sorted by name) are two independently sorted sets and must not be zipped (H221: headlod 7->'Head')."""

    def write_mesh(self, folder, used, used_names):
        mesh = {"bone_indices_raw": [[7, 0, 0, 0], [9, 0, 0, 0]], "bone_weights_raw": [[255, 0, 0, 0]] * 2,
                "influence": {"tests": {"direct_global_438": {"bones_used": used, "bone_names_used": used_names}}}}
        (folder / "m.json").write_text(json.dumps(mesh))
        return {"meshes": [{"file": "m.json", "vertex_offset": 0, "vertex_count": 2, "triangle_count": 0}]}

    def test_anchor_groups_names_each_bone_index_from_the_skeleton(self):
        with tempfile.TemporaryDirectory() as tmp:
            index = self.write_mesh(Path(tmp), [7, 9], ["Head", "Neck"])
            rest = [[0.0, 1.57, 0.0], [0.0, 1.67, 0.0]]
            groups = fifa_pack.anchor_groups(index, Path(tmp), rest, skeleton_names())
            self.assertEqual(groups["Head"], [[0.0, 1.67, 0.0]], "bone 9 is Head in the skeleton; bone 7 is Neck")

    def test_anchor_groups_refuses_a_skeleton_that_disagrees_with_the_mesh_bone_names(self):
        with tempfile.TemporaryDirectory() as tmp:
            index = self.write_mesh(Path(tmp), [7, 9], ["Head", "Neck"])
            names = skeleton_names()
            names[9] = "Spine"  # the mesh declares {Head, Neck} for its bones; this skeleton says {Neck, Spine}
            with self.assertRaises(ValueError):
                fifa_pack.anchor_groups(index, Path(tmp), [[0.0, 1.57, 0.0], [0.0, 1.67, 0.0]], names)


class PackBuildTests(unittest.TestCase):
    def setUp(self):
        self.assertIsNotNone(fifa_pack, "tools/fifa_pack.py missing")

    def test_build_and_verify_synthetic_pack(self):
        with tempfile.TemporaryDirectory() as tmp:
            src, meshes = write_fixture(Path(tmp) / "src")
            out = Path(tmp) / "pack"
            header = fifa_pack.build_pack(src, meshes, out, repo_root=ROOT, skeleton=src / "skeleton.json")
            self.assertEqual(header["schema"], "fm26.fifa-pack.v1")
            self.assertIn("PRIVATE_NOT_REDISTRIBUTABLE", header["label"])
            self.assertEqual(header["vertex_count"], 6)
            self.assertEqual(header["triangle_count"], 3)
            self.assertEqual(header["keys_per_second"], 30)
            self.assertEqual(header["axis_mapping"]["matrix"], [[-1, 0, 0], [0, 1, 0], [0, 0, 1]])
            tri = (out / header["triangles"]["file"]).read_bytes()
            self.assertEqual(len(tri), 3 * 3 * 2)
            # second mesh offset applied (4 + local)
            self.assertEqual(list(struct.unpack("<9H", tri)), [0, 1, 2, 1, 3, 2, 4, 5, 4])
            jog = header["clips"]["jog_F2"]
            self.assertEqual(jog["frames"], 3)
            self.assertEqual(jog["rest_frame"], 0)
            self.assertEqual(jog["first_key_frame"], 1)
            data = (out / jog["file"]).read_bytes()
            self.assertEqual(len(data), 3 * 6 * 3 * 4)
            self.assertEqual(hashlib.sha256(data).hexdigest(), jog["sha256"])
            self.assertAlmostEqual(struct.unpack_from("<f", data, 0)[0], 0.13, places=6)
            self.assertGreater(header["rest_height"]["extent"], 1.7)
            # H222: joint-to-joint rest proxy (was '> 1.6' for the vertex-centroid rule): |Head - mid(feet)| of SKELETON_JOINTS
            self.assertAlmostEqual(header["rest_height"]["head_to_feet_anchor_proxy"], (1.55 ** 2 + 0.07 ** 2) ** 0.5, places=12)
            self.assertLess(header["float32_max_abs_rounding"], 1e-6)
            # H156 evolution: verify applies the plugin contract, so this 6-vertex synthetic pack is refused (as the plugin
            # would refuse it) while its integrity (sizes/sha256/triangle range) is intact.
            result = fifa_pack.verify_pack(out)
            self.assertFalse(result["ok"])
            self.assertIn("vertex_count", result["errors"])
            self.assertFalse([e for e in result["errors"] if e.startswith(("sha256:", "size:", "unreadable", "triangle-range"))], result["errors"])
            with open(out / jog["file"], "r+b") as stream:
                stream.seek(5)
                stream.write(b"\xff")
            self.assertIn("sha256:" + jog["file"], fifa_pack.verify_pack(out)["errors"])

    def test_build_header_declares_the_joint_to_joint_proxy_rule(self):
        with tempfile.TemporaryDirectory() as tmp:
            src, meshes = write_fixture(Path(tmp) / "src")
            header = fifa_pack.build_pack(src, meshes, Path(tmp) / "pack", repo_root=ROOT, skeleton=src / "skeleton.json")
            self.assertEqual(header["rest_height"].get("head_to_feet_anchor_proxy_rule"), "joint-to-joint-rest")
            self.assertIn("joint", header["rest_height"]["proxy_rule"])

    def test_build_header_records_the_skeleton_sha256(self):
        with tempfile.TemporaryDirectory() as tmp:
            src, meshes = write_fixture(Path(tmp) / "src")
            header = fifa_pack.build_pack(src, meshes, Path(tmp) / "pack", repo_root=ROOT, skeleton=src / "skeleton.json")
            self.assertEqual(header["sources"].get("skeleton_sha256"), hashlib.sha256((src / "skeleton.json").read_bytes()).hexdigest())

    def test_build_requires_explicit_rest_frame_ids_and_identical_rest_frames(self):
        for label in ("missing-frame-ids", "rest-differs"):
            with self.subTest(label), tempfile.TemporaryDirectory() as tmp:
                src, meshes = write_fixture(Path(tmp) / "src")
                index = json.loads((src / "frames-index.json").read_text())
                clip = index["clips"]["walk_F2"]
                if label == "missing-frame-ids":
                    del clip["frame_ids"]
                else:
                    blob = src / clip["file"]
                    data = bytearray(blob.read_bytes())
                    data[0:8] = struct.pack("<d", 0.5)
                    blob.write_bytes(bytes(data))
                    clip["sha256"] = hashlib.sha256(bytes(data)).hexdigest()
                (src / "frames-index.json").write_text(json.dumps(index))
                with self.assertRaises(ValueError):
                    fifa_pack.build_pack(src, meshes, Path(tmp) / "pack", repo_root=ROOT, skeleton=src / "skeleton.json")

    def test_refuses_output_inside_repo_or_dist(self):
        with tempfile.TemporaryDirectory() as tmp:
            src, meshes = write_fixture(Path(tmp) / "src")
            for inside in (ROOT / "local" / "pack-should-not-exist", ROOT / "dist" / "pack"):
                with self.assertRaises(ValueError):
                    fifa_pack.build_pack(src, meshes, inside, repo_root=ROOT, skeleton=src / "skeleton.json")
                self.assertFalse(inside.exists())

    def test_refuses_input_hash_mismatch_and_bad_shape(self):
        with tempfile.TemporaryDirectory() as tmp:
            src, meshes = write_fixture(Path(tmp) / "src")
            blob = src / "frames-walk_F2-f64le.bin"
            blob.write_bytes(blob.read_bytes()[:-8] + b"\0" * 8)
            with self.assertRaises(ValueError):
                fifa_pack.build_pack(src, meshes, Path(tmp) / "pack", repo_root=ROOT, skeleton=src / "skeleton.json")
        with tempfile.TemporaryDirectory() as tmp:
            src, meshes = write_fixture(Path(tmp) / "src")
            index = json.loads((src / "frames-index.json").read_text())
            index["clips"]["jog_F2"]["shape"] = [3, 7, 3]
            (src / "frames-index.json").write_text(json.dumps(index))
            with self.assertRaises(ValueError):
                fifa_pack.build_pack(src, meshes, Path(tmp) / "pack", repo_root=ROOT, skeleton=src / "skeleton.json")

    def test_refuses_existing_nonempty_output(self):
        with tempfile.TemporaryDirectory() as tmp:
            src, meshes = write_fixture(Path(tmp) / "src")
            out = Path(tmp) / "pack"
            out.mkdir()
            (out / "other.txt").write_text("x")
            with self.assertRaises(FileExistsError):
                fifa_pack.build_pack(src, meshes, out, repo_root=ROOT, skeleton=src / "skeleton.json")

    def test_default_output_is_localappdata(self):
        with tempfile.TemporaryDirectory() as tmp:
            old = os.environ.get("LOCALAPPDATA")
            os.environ["LOCALAPPDATA"] = tmp
            try:
                self.assertEqual(fifa_pack.default_pack_dir(), Path(tmp) / "fm26-visual-mod" / "packs" / "fifa17-player-v1")
            finally:
                if old is None:
                    del os.environ["LOCALAPPDATA"]
                else:
                    os.environ["LOCALAPPDATA"] = old


class BundleRefusalTests(unittest.TestCase):
    def test_gitignore_excludes_pack_artifacts(self):
        text = (ROOT / ".gitignore").read_text()
        for pattern in ("fifa-pack.json", "*.f32le.bin", "*.u16le.bin"):
            self.assertIn(pattern, text)

    def test_deploy_install_refuses_stage_with_private_pack(self):
        self.assertTrue(hasattr(deploy, "refuse_private_pack"), "deploy pack refusal missing")
        with tempfile.TemporaryDirectory() as tmp:
            root = Path(tmp)
            game, stage = root / "game", root / "stage"
            game.mkdir()
            (game / "fm.exe").write_bytes(b"exe")
            plugin_dir = stage / "BepInEx" / "plugins" / "FM26.VisualDiagnostics"
            plugin_dir.mkdir(parents=True)
            (plugin_dir / "fifa-pack.json").write_text("{}")
            with self.assertRaises(ValueError):
                deploy.install(stage, game, root / "state.json")
            self.assertFalse((game / "BepInEx").exists())
            self.assertFalse((root / "state.json").exists())
            (plugin_dir / "fifa-pack.json").unlink()
            (plugin_dir / "clip-jog_F2.f32le.bin").write_bytes(b"\0" * 12)
            with self.assertRaises(ValueError):
                deploy.install(stage, game, root / "state.json")

    def test_deploy_plugin_refuses_non_dll_pack_source(self):
        with tempfile.TemporaryDirectory() as tmp:
            source = Path(tmp) / "fifa-pack.json"
            source.write_text("{}")
            with self.assertRaises(ValueError):
                deploy.refuse_private_pack(source)


class ContentRefusalTests(unittest.TestCase):
    """H156 S2: deploy refusal by CONTENT (header schema/label bytes, size+sha of local pack files), incl. archives."""

    def make_pack(self, root):
        src, meshes = write_fixture(root / "src")
        pack = root / "packs" / "fifa17-player-v1"
        fifa_pack.build_pack(src, meshes, pack, repo_root=ROOT, skeleton=src / "skeleton.json")
        return pack

    def test_renamed_header_refused_by_schema_content(self):
        with tempfile.TemporaryDirectory() as tmp:
            root = Path(tmp)
            pack = self.make_pack(root)
            for name in ("pack.json", "fifa-pack.json.txt", "notes.dat"):
                target = root / "stage" / name
                target.parent.mkdir(exist_ok=True)
                target.write_bytes((pack / "fifa-pack.json").read_bytes())
                with self.assertRaises(ValueError, msg=name):
                    deploy.refuse_private_pack(target, pack_dirs=[])
                target.unlink()
            # schema marker alone (no label) and label alone are both refused
            (root / "stage" / "a.cfg").write_bytes(b'{"schema": "fm26.fifa-pack.v1"}')
            with self.assertRaises(ValueError):
                deploy.refuse_private_pack(root / "stage", pack_dirs=[])
            (root / "stage" / "a.cfg").write_bytes(b"x" * 5000 + fifa_pack.LABEL.encode() + b"y")
            with self.assertRaises(ValueError):
                deploy.refuse_private_pack(root / "stage", pack_dirs=[])

    def test_renamed_payload_refused_by_size_and_sha_of_local_pack(self):
        with tempfile.TemporaryDirectory() as tmp:
            root = Path(tmp)
            pack = self.make_pack(root)
            stage = root / "stage"
            stage.mkdir()
            for original, renamed in (("clip-jog_F2.f32le.bin", "clip-jog_F2.bin"), ("clip-walk_F2.f32le.bin", "walk.f32le.bin.bak"),
                                      ("triangles.u16le.bin", "BepInEx.cache")):
                (stage / renamed).write_bytes((pack / original).read_bytes())
                with self.assertRaises(ValueError, msg=renamed):
                    deploy.refuse_private_pack(stage, pack_dirs=[pack])
                (stage / renamed).unlink()
            # same size, different bytes: not refused (no false positive on size alone)
            data = bytearray((pack / "clip-jog_F2.f32le.bin").read_bytes())
            data[0] ^= 1
            (stage / "other.bin").write_bytes(bytes(data))
            deploy.refuse_private_pack(stage, pack_dirs=[pack])

    def test_archives_containing_pack_content_refused(self):
        import zipfile
        with tempfile.TemporaryDirectory() as tmp:
            root = Path(tmp)
            pack = self.make_pack(root)
            stage = root / "stage"
            stage.mkdir()
            cases = {"name.zip": ("fifa-pack.json", b"{}"), "header.zip": ("readme.json", (pack / "fifa-pack.json").read_bytes()),
                     "payload.zip": ("data.bin", (pack / "clip-jog_F2.f32le.bin").read_bytes())}
            for archive, (member, data) in cases.items():
                with zipfile.ZipFile(stage / archive, "w") as z:
                    z.writestr(member, data)
                with self.assertRaises(ValueError, msg=archive):
                    deploy.refuse_private_pack(stage, pack_dirs=[pack])
                (stage / archive).unlink()
            inner = root / "inner.zip"
            with zipfile.ZipFile(inner, "w") as z:
                z.writestr("x.json", (pack / "fifa-pack.json").read_bytes())
            with zipfile.ZipFile(stage / "outer.zip", "w") as z:
                z.write(inner, "nested.zip")
            with self.assertRaises(ValueError):
                deploy.refuse_private_pack(stage, pack_dirs=[pack])
            (stage / "outer.zip").unlink()
            with zipfile.ZipFile(stage / "clean.zip", "w") as z:
                z.writestr("BepInEx/config/x.cfg", b"[General]\nEnabled=true\n")
            deploy.refuse_private_pack(stage, pack_dirs=[pack])

    def test_plugin_dll_and_ordinary_files_still_accepted(self):
        with tempfile.TemporaryDirectory() as tmp:
            stage = Path(tmp) / "stage"
            stage.mkdir()
            (stage / "winhttp.dll").write_bytes(os.urandom(4096))
            (stage / "doorstop_config.ini").write_text("[General]\nenabled=true\n")
            deploy.refuse_private_pack(stage, pack_dirs=[])
        dlls = [p for p in (ROOT / "bin" / "Release").rglob("FM26.VisualDiagnostics.dll")] if (ROOT / "bin" / "Release").exists() else []
        for dll in dlls[:1]:
            deploy.refuse_private_pack(dll, pack_dirs=[])  # the plugin keeps the schema only as UTF-16 metadata, not the JSON marker

    def test_default_pack_dirs_are_discovered_under_localappdata(self):
        with tempfile.TemporaryDirectory() as tmp:
            root = Path(tmp)
            old = os.environ.get("LOCALAPPDATA")
            os.environ["LOCALAPPDATA"] = str(root / "lad")
            try:
                pack = self.make_pack(root / "lad" / "fm26-visual-mod")
                self.assertIn(pack.resolve(), [p.resolve() for p in deploy.local_pack_dirs()])
                stage = root / "stage"
                stage.mkdir()
                (stage / "renamed.bin").write_bytes((pack / "triangles.u16le.bin").read_bytes())
                with self.assertRaises(ValueError):
                    deploy.refuse_private_pack(stage)
            finally:
                if old is None:
                    del os.environ["LOCALAPPDATA"]
                else:
                    os.environ["LOCALAPPDATA"] = old


if __name__ == "__main__":
    unittest.main()
