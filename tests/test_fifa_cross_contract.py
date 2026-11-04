"""H156: tools/fifa_pack.py verify must enforce the same pack constraints as the plugin (src/FifaOverlayPolicy.cs).

Cross-check: plugin constants are parsed from the C# source and compared with the Python constants, and a plugin-shaped
synthetic pack is mutated one constraint at a time; each mutation must fail verify with a plugin-contract error while the
unmodified pack passes. Synthetic data only (no EA data)."""
import hashlib
import importlib.util
import json
import math
import re
import struct
import tempfile
import unittest
from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]
spec = importlib.util.spec_from_file_location("fifa_pack", ROOT / "tools" / "fifa_pack.py")
fifa_pack = importlib.util.module_from_spec(spec)
spec.loader.exec_module(fifa_pack)
POLICY = (ROOT / "src" / "FifaOverlayPolicy.cs").read_text(encoding="utf-8")


def cs_const(name):
    match = re.search(rf"public const \w+ {name} = ([^;]+);", POLICY)
    if not match:
        raise AssertionError(f"C# constant {name} not found")
    return match.group(1).strip().strip('"')


# H160: the real pack's six-piece layout (names/counts only, no EA data); triangles stay inside their piece's vertex range.
PIECES = (("mesh-1-body_2_0_0_mesh.json", 732, 1140), ("mesh-2-jersey_2_0_0_0_0_mesh.json", 268, 417), ("mesh-3-shorts_2_0_0_mesh.json", 220, 338),
          ("mesh-4-sock_2_0_0_mesh.json", 214, 308), ("mesh-5-shoes_2_0_mesh.json", 140, 196), ("mesh-6-headlod_2_0_mesh.json", 294, 512))


def piece_meshes():
    out, offset = [], 0
    for file, vertices, triangles in PIECES:
        out.append({"file": file, "vertex_offset": offset, "vertex_count": vertices, "triangle_count": triangles})
        offset += vertices
    return out


def write_plugin_pack(folder, frames=(3, 4), mutate=None, values=None):
    """A pack with the plugin's exact shape (1868 vertices, 2911 triangles), synthetic coordinates."""
    v, t = fifa_pack.PLUGIN_VERTEX_COUNT, fifa_pack.PLUGIN_TRIANGLE_COUNT
    folder.mkdir(parents=True)
    indices = []
    for mesh in piece_meshes():
        indices.extend(mesh["vertex_offset"] + (i * 7) % mesh["vertex_count"] for i in range(mesh["triangle_count"] * 3))
    tri = struct.pack(f"<{t * 3}H", *indices)
    (folder / "triangles.u16le.bin").write_bytes(tri)
    clips = {}
    for name, count in zip(fifa_pack.REQUIRED_CLIPS, frames):
        data = values(name, count) if values else struct.pack(f"<{count * v * 3}f", *[((i % 97) - 48) * 0.01 for i in range(count * v * 3)])
        file = f"clip-{name}.f32le.bin"
        (folder / file).write_bytes(data)
        clips[name] = {"file": file, "frames": count, "rest_frame": 0, "first_key_frame": 1, "keys": count - 1,
                       "bytes": len(data), "sha256": hashlib.sha256(data).hexdigest()}
    header = {"schema": fifa_pack.SCHEMA, "label": fifa_pack.LABEL, "vertex_count": v, "triangle_count": t, "keys_per_second": 30,
              "axis_mapping": {"matrix": [[-1, 0, 0], [0, 1, 0], [0, 0, 1]], "determinant": -1, "flip_winding": True},
              "rest_height": {"extent": 1.84, "head_to_feet_anchor_proxy": 1.65},
              "triangles": {"file": "triangles.u16le.bin", "count": t, "bytes": len(tri), "sha256": hashlib.sha256(tri).hexdigest()},
              "clips": clips, "meshes": piece_meshes()}
    if mutate:
        mutate(header, folder)
    (folder / "fifa-pack.json").write_text(json.dumps(header), encoding="utf-8")
    return folder


class SharedConstantsTests(unittest.TestCase):
    def test_python_constants_equal_plugin_constants(self):
        self.assertEqual(cs_const("Schema"), fifa_pack.SCHEMA)
        self.assertEqual(int(cs_const("VertexCount")), fifa_pack.PLUGIN_VERTEX_COUNT)
        self.assertEqual(int(cs_const("TriangleCount")), fifa_pack.PLUGIN_TRIANGLE_COUNT)
        self.assertEqual(int(cs_const("KeysPerSecond")), fifa_pack.KEYS_PER_SECOND)
        self.assertEqual(int(cs_const("MaximumClipFrames")), fifa_pack.MAX_CLIP_FRAMES)
        self.assertEqual(int(cs_const("MaximumHeaderBytes")), fifa_pack.MAX_HEADER_BYTES)
        self.assertEqual(float(cs_const("MaximumAbsVertex")), fifa_pack.MAX_ABS_VERTEX)
        self.assertEqual(int(cs_const("RestFrame")), 0)
        clips = re.search(r"RequiredClips = \{([^}]*)\}", POLICY).group(1)
        self.assertEqual(tuple(re.findall(r'"([^"]+)"', clips)), tuple(fifa_pack.REQUIRED_CLIPS))
        self.assertIn("proxy < 0.5 || proxy > 5", POLICY)
        # H160: piece tokens/order shared by the plugin (PieceOrder) and verify (PIECE_TOKENS).
        order = re.search(r"PieceOrder =\s*\{([^;]*)\};", POLICY).group(1)
        self.assertEqual(tuple(re.findall(r'\("([^"]+)", "[^"]+"\)', order)), tuple(fifa_pack.PIECE_TOKENS))
        self.assertEqual(fifa_pack.PROXY_RANGE, (0.5, 5.0))

    def test_h171_v2_constants_equal_plugin_constants(self):
        self.assertEqual(cs_const("SchemaV2"), fifa_pack.SCHEMA_V2)
        self.assertEqual(int(cs_const("MaximumPackBytesV2")), fifa_pack.MAX_PACK_BYTES_V2)
        self.assertEqual(float(cs_const("V2MinimumNativeSpeed")), fifa_pack.V2_NATIVE_SPEED_RANGE[0])
        self.assertEqual(float(cs_const("V2MaximumNativeSpeed")), fifa_pack.V2_NATIVE_SPEED_RANGE[1])
        self.assertEqual(int(cs_const("V2MinimumKeysPerCycle")), fifa_pack.V2_MIN_KEYS_PER_CYCLE)
        table = re.search(r"V2Clips =\s*\{([^;]*)\};", POLICY).group(1)
        roles = re.findall(r'\("([^"]+)", "([^"]+)", "([^"]+)"\)', table)
        self.assertEqual(tuple(r[0] for r in roles), fifa_pack.V2_CLIPS, "same clip set and order")
        self.assertEqual({r[0]: (r[1], r[2]) for r in roles}, fifa_pack.V2_ROLES, "same declared tier/direction per clip")
        self.assertIn('"fifa17-player-v2"', POLICY, "plugin default v2 folder name = fifa_pack.default_pack_dir(2)")


class VerifyMatchesPluginTests(unittest.TestCase):
    def check(self, mutate=None, values=None, frames=(3, 4)):
        with tempfile.TemporaryDirectory() as tmp:
            return fifa_pack.verify_pack(write_plugin_pack(Path(tmp) / "pack", frames=frames, mutate=mutate, values=values))

    def test_plugin_shaped_pack_passes(self):
        result = self.check()
        self.assertTrue(result["ok"], result["errors"])

    def test_each_plugin_constraint_is_enforced(self):
        def clip(name, **kw):
            return lambda h, f: h["clips"][name].update(kw)
        cases = {
            "schema": lambda h, f: h.update(schema="fm26.fifa-pack.v0"),
            "vertex_count": lambda h, f: h.update(vertex_count=6),
            "triangle_count": lambda h, f: h.update(triangle_count=3),
            "keys_per_second": lambda h, f: h.update(keys_per_second=60),
            "float vertex_count": lambda h, f: h.update(vertex_count=1868.0),
            "non-permutation": lambda h, f: h["axis_mapping"].update(matrix=[[-1, 0, 0], [0, 1, 0], [0, 1, 0]]),
            "scaled matrix": lambda h, f: h["axis_mapping"].update(matrix=[[-0.5, 0, 0], [0, 1, 0], [0, 0, 1]]),
            "determinant": lambda h, f: h["axis_mapping"].update(determinant=1),
            "flip": lambda h, f: h["axis_mapping"].update(flip_winding=False),
            "proxy": lambda h, f: h["rest_height"].update(head_to_feet_anchor_proxy=0.1),
            "extent<proxy": lambda h, f: h["rest_height"].update(extent=1.0),
            "rest_frame": clip("jog_F2", rest_frame=1),
            "first_key_frame": clip("walk_F2", first_key_frame=2),
            "frames bytes": clip("jog_F2", frames=4),
            "file traversal": clip("jog_F2", file="..\\clip.bin"),
            "sha uppercase": lambda h, f: h["clips"]["jog_F2"].update(sha256=h["clips"]["jog_F2"]["sha256"].upper()),
            "missing walk": lambda h, f: h["clips"].pop("walk_F2"),
            "extra clip": lambda h, f: h["clips"].update(walk_F3=dict(h["clips"]["walk_F2"])),
            "triangle bytes": lambda h, f: h["triangles"].update(bytes=6),
        }
        for label, mutate in cases.items():
            with self.subTest(label):
                result = self.check(mutate=mutate)
                self.assertFalse(result["ok"], label)

    def test_frame_bound_and_values(self):
        v = fifa_pack.PLUGIN_VERTEX_COUNT
        def bad(value):
            return lambda name, count: struct.pack(f"<{count * v * 3}f", *([0.0] * 7 + [value] + [0.0] * (count * v * 3 - 8)))
        for value in (math.nan, math.inf, 100.5, -1e30):
            with self.subTest(value=value):
                self.assertFalse(self.check(values=bad(value))["ok"])
        self.assertTrue(self.check(values=bad(100.0))["ok"], "|v| == 100 accepted like the plugin")
        self.assertFalse(self.check(frames=(1, 4))["ok"], "rest-only clip")

    def test_triangle_index_range_and_header_size(self):
        def big_index(h, f):
            data = bytearray((f / "triangles.u16le.bin").read_bytes())
            data[0:2] = struct.pack("<H", fifa_pack.PLUGIN_VERTEX_COUNT)
            (f / "triangles.u16le.bin").write_bytes(bytes(data))
            h["triangles"]["sha256"] = hashlib.sha256(bytes(data)).hexdigest()
        self.assertFalse(self.check(mutate=big_index)["ok"])
        self.assertFalse(self.check(mutate=lambda h, f: h.update(padding="x" * fifa_pack.MAX_HEADER_BYTES))["ok"])

    def test_h160_piece_ranges_enforced_like_the_plugin(self):
        def crossing(h, f):
            data = bytearray((f / "triangles.u16le.bin").read_bytes())
            data[1140 * 6:1140 * 6 + 2] = struct.pack("<H", 0)  # first jersey triangle -> body vertex 0
            (f / "triangles.u16le.bin").write_bytes(bytes(data))
            h["triangles"]["sha256"] = hashlib.sha256(bytes(data)).hexdigest()
        result = self.check(mutate=crossing)
        self.assertFalse(result["ok"])
        self.assertIn("piece-triangle-range", result["errors"])
        cases = {
            "meshes missing": lambda h, f: h.pop("meshes"),
            "five pieces": lambda h, f: h["meshes"].pop(),
            "order swapped": lambda h, f: h.update(meshes=[h["meshes"][1], h["meshes"][0]] + h["meshes"][2:]),
            "gap in vertex offsets": lambda h, f: h["meshes"][1].update(vertex_offset=733),
            "triangle sum": lambda h, f: h["meshes"][0].update(triangle_count=1141),
            "float count": lambda h, f: h["meshes"][0].update(vertex_count=732.0),
            "bad file name": lambda h, f: h["meshes"][0].update(file="../mesh-1-body_x.json"),
            "empty piece": lambda h, f: h["meshes"][2].update(triangle_count=0),
        }
        for label, mutate in cases.items():
            with self.subTest(label):
                result = self.check(mutate=mutate)
                self.assertFalse(result["ok"], label)
                self.assertTrue(any(e.startswith("piece") for e in result["errors"]), (label, result["errors"]))

    def test_duplicate_header_keys_refused(self):
        with tempfile.TemporaryDirectory() as tmp:
            folder = write_plugin_pack(Path(tmp) / "pack")
            text = (folder / "fifa-pack.json").read_text(encoding="utf-8")
            (folder / "fifa-pack.json").write_text(text.replace('"keys_per_second": 30', '"keys_per_second": 30, "keys_per_second": 30'), encoding="utf-8")
            self.assertFalse(fifa_pack.verify_pack(folder)["ok"])


if __name__ == "__main__":
    unittest.main()
