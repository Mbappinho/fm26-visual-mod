# SPDX-License-Identifier: MIT
"""Build/verify a LOCAL PRIVATE FIFA player pack for the FM26 stage-1 overlay (MIT tool, EA data never bundled).

The pack is built only from the user's own extraction outputs (H152 frames + H140 meshes) into a folder
OUTSIDE the repository (default %LOCALAPPDATA%/fm26-visual-mod/packs/fifa17-player-v1). It contains:
  fifa-pack.json            header (schema, shapes, sha256, axis mapping, rest heights, provenance)
  clip-<name>.f32le.bin     float32 LE [frames, vertices, 3] in SOURCE (FIFA) coordinates, frame 0 = rest
  triangles.u16le.bin       uint16 LE global indices (per-mesh vertex offsets applied), source winding
float64 -> float32 is the only numeric change (max abs rounding recorded). No smoothing/interpolation/repair.
"""
import argparse
import array
import math
import hashlib
import json
import os
from pathlib import Path
import struct
import sys

SCHEMA = "fm26.fifa-pack.v1"
LABEL = "FIFA17_LOW_LOD_PLAYER_LOCAL_PACK_PRIVATE_NOT_REDISTRIBUTABLE"
DEFAULT_CLIPS = ("jog_F2", "walk_F2")
KEYS_PER_SECOND = 30
HEADER = "fifa-pack.json"
TRIANGLES = "triangles.u16le.bin"
ANCHOR_BONES = ("LeftFoot", "RightFoot", "LeftToeBase", "RightToeBase", "Head")
DOMINANCE = 0.9  # a derived axis must be >=90% along one source axis, otherwise refuse (no guessing)
REPO_ROOT = Path(__file__).resolve().parents[1]
# H156: plugin pack contract (src/FifaOverlayPolicy.cs), cross-checked by tests/test_fifa_cross_contract.py.
PLUGIN_VERTEX_COUNT = 1868
PLUGIN_TRIANGLE_COUNT = 2911
REQUIRED_CLIPS = DEFAULT_CLIPS
MAX_CLIP_FRAMES = 4096
MAX_HEADER_BYTES = 65536
MAX_ABS_VERTEX = 100.0
PROXY_RANGE = (0.5, 5.0)
# H160: per-piece colouring reads the header "meshes" (already written by build in concatenation order; pack format v1
# unchanged). Plugin PieceOrder tokens, cross-checked by tests/test_fifa_cross_contract.py.
PIECE_TOKENS = ("body", "jersey", "shorts", "sock", "shoes", "headlod")
# H171: pack format v2 = the H170 locomotion set (same mesh/axis/rest/triangle/piece contract as v1, ten loop clips).
# Plugin constants in src/FifaOverlayPolicy.cs (SchemaV2, V2Clips), cross-checked by tests/test_fifa_cross_contract.py.
SCHEMA_V2 = "fm26.fifa-pack.v2"
LABEL_V2 = "FIFA17_LOW_LOD_PLAYER_LOCOMOTION_V2_LOCAL_PACK_PRIVATE_NOT_REDISTRIBUTABLE"
V2_CLIPS = ("idle", "walk", "jog", "run", "sprint", "strafe_right_walk", "strafe_right_jog", "strafe_left_jog", "back_walk", "back_jog")
# declared (tier, direction) of each v2 clip; the plugin selector maps (tier, direction) to these names.
V2_ROLES = {"idle": ("idle", "none"), "walk": ("walk", "forward"), "jog": ("jog", "forward"), "run": ("run", "forward"),
            "sprint": ("sprint", "forward"), "strafe_right_walk": ("walk", "strafe-right"), "strafe_right_jog": ("jog", "strafe-right"),
            "strafe_left_jog": ("jog", "strafe-left"), "back_walk": ("walk", "backward"), "back_jog": ("jog", "backward")}
V2_NATIVE_SPEED_RANGE = (0.3, 12.0)   # m/s, moving clips; idle declares exactly 0
V2_MIN_KEYS_PER_CYCLE = 4
MAX_PACK_BYTES_V2 = 96 * 1024 * 1024  # all clip payloads together (plugin keeps them in memory as float32)


def _finite_number(value):
    return isinstance(value, (int, float)) and not isinstance(value, bool) and math.isfinite(value)


def _v1_clip_errors(clips, errors, need):
    if need(isinstance(clips, dict) and sorted(clips) == sorted(REQUIRED_CLIPS), "clip-set"):
        for name in REQUIRED_CLIPS:
            _clip_layout_errors(name, clips[name], need)


def _clip_layout_errors(name, clip, need):
    clip = clip if isinstance(clip, dict) else {}
    frames = clip.get("frames")
    need(_exact_int(frames) and 2 <= frames <= MAX_CLIP_FRAMES, f"frames:{name}")
    need(clip.get("rest_frame") == 0 and clip.get("first_key_frame") == 1 and _exact_int(clip.get("rest_frame"))
         and _exact_int(clip.get("first_key_frame")), f"layout:{name}")
    need(_exact_int(frames) and _exact_int(clip.get("bytes")) and clip.get("bytes") == frames * PLUGIN_VERTEX_COUNT * 12, f"bytes:{name}")
    need(_plain_file(clip.get("file")) and _sha(clip.get("sha256")), f"file-sha:{name}")
    return clip


def _v2_clip_errors(clips, errors, need):
    """The plugin's ValidateV2Clip rules (FifaOverlayPolicy.cs): loop locomotion clips with declared role and timing."""
    if not need(isinstance(clips, dict) and sorted(clips) == sorted(V2_CLIPS), "clip-set"):
        return
    total = 0
    for name in V2_CLIPS:
        clip = _clip_layout_errors(name, clips[name], need)
        frames, keys = clip.get("frames"), clip.get("keys")
        if _exact_int(clip.get("bytes")):
            total += clip["bytes"]
        need(_exact_int(frames) and _exact_int(keys) and keys == frames - 1, f"keys:{name}")
        rng = clip.get("frame_range")
        need(isinstance(rng, list) and len(rng) == 2 and all(_exact_int(x) for x in rng) and rng[0] >= 0 and _exact_int(keys)
             and rng[1] - rng[0] + 1 == keys, f"frame-range:{name}")
        need(clip.get("loop") is True, f"loop:{name}")
        tier, direction = V2_ROLES[name]
        need(clip.get("tier") == tier and clip.get("direction") == direction, f"role:{name}")
        speed, kpc, cycles, origin = clip.get("native_speed_mps"), clip.get("keys_per_cycle"), clip.get("cycles_in_loop"), clip.get("cycle_origin_key")
        if tier == "idle":
            need(_finite_number(speed) and speed == 0, f"native-speed:{name}")
            need(kpc is None and cycles is None and origin is None, f"cycle:{name}")
            continue
        need(_finite_number(speed) and V2_NATIVE_SPEED_RANGE[0] <= speed <= V2_NATIVE_SPEED_RANGE[1], f"native-speed:{name}")
        need(_exact_int(cycles) and cycles >= 1 and _finite_number(kpc) and _exact_int(keys) and abs(kpc - keys / cycles) <= 1e-9
             and V2_MIN_KEYS_PER_CYCLE <= kpc <= keys, f"cycle:{name}")
        need(_exact_int(origin) and _exact_int(keys) and 0 <= origin < keys, f"cycle-origin:{name}")
    need(total <= MAX_PACK_BYTES_V2, "pack-bytes")


def sha256_bytes(data):
    return hashlib.sha256(data).hexdigest()


def default_pack_dir(version=1):
    base = os.environ.get("LOCALAPPDATA")
    if not base:
        raise ValueError("LOCALAPPDATA is not set; pass --out explicitly (outside the repository)")
    if version not in (1, 2):
        raise ValueError("Unknown pack format version")
    return Path(base) / "fm26-visual-mod" / "packs" / f"fifa17-player-v{version}"


def require_outside_repo(out, repo_root):
    out, repo_root = Path(out).resolve(), Path(repo_root).resolve()
    if out == repo_root or repo_root in out.parents:
        raise ValueError(f"Refusing to write a private FIFA pack inside the repository: {out}")


def read_frame(path, vertices, frame):
    size = vertices * 3 * 8
    with open(path, "rb") as stream:
        stream.seek(frame * size)
        data = stream.read(size)
    if len(data) != size:
        raise ValueError(f"Short frame read: {path} frame {frame}")
    values = struct.unpack(f"<{vertices * 3}d", data)
    return [values[i:i + 3] for i in range(0, len(values), 3)]


def _centroid(points):
    if not points:
        raise ValueError("Empty anchor group")
    return [sum(p[i] for p in points) / len(points) for i in range(3)]


def load_skeleton(path):
    """H222: render skeleton rest pose ({"names": [...], "model": [4x4 row-major, translation in column 3]})."""
    raw = Path(path).read_bytes()
    data = json.loads(raw)
    return {"names": list(data["names"]), "joints": [[m[r][3] for r in range(3)] for m in data["model"]],
            "sha256": sha256_bytes(raw)}


def anchor_groups(index, mesh_dir, rest, names):
    """Rest vertices grouped by their dominant raw bone lane for the named anchor bones (raw weights, no renormalisation);
    names = the render skeleton BoneNames, indexed by the raw global bone index."""
    groups = {name: [] for name in ANCHOR_BONES}
    for mesh in index["meshes"]:
        data = json.loads((Path(mesh_dir) / mesh["file"]).read_text(encoding="utf-8"))
        used = data["influence"]["tests"]["direct_global_438"]
        if sorted(names[i] for i in used["bones_used"]) != sorted(used["bone_names_used"]):
            raise ValueError(f"Skeleton bone names disagree with the bone names declared by {mesh['file']}")
        for local, (bones, weights) in enumerate(zip(data["bone_indices_raw"], data["bone_weights_raw"])):
            lane = max(range(len(weights)), key=lambda i: (weights[i], -i))
            name = names[bones[lane]]  # H222: the skeleton names each global bone index (no zip of sorted lists)
            if name in groups:
                groups[name].append(list(rest[mesh["vertex_offset"] + local]))
    return groups


PROXY_RULE_ID = "joint-to-joint-rest"
PROXY_RULE = ("3D distance |Head joint - mid(LeftFoot joint, RightFoot joint)| on the render skeleton rest pose (ModelPose "
              "translations), mirroring FM's |Character_Head - mid(Character_LeftFoot, Character_RightFoot)|")


def joint_head_to_feet(skeleton):
    """H222: FM's scale measure |Character_Head - mid(Character_LeftFoot, Character_RightFoot)| (3D distance,
    FifaOverlayActor.Begin) mirrored on the render skeleton rest joints (signed-permutation axes keep the distance)."""
    joint = {name: skeleton["joints"][i] for i, name in enumerate(skeleton["names"])}
    feet = [(a + b) / 2 for a, b in zip(joint["LeftFoot"], joint["RightFoot"])]
    return sum((h - f) ** 2 for h, f in zip(joint["Head"], feet)) ** 0.5


def _dominant(vector, label):
    norm = sum(c * c for c in vector) ** 0.5
    if norm <= 1e-9:
        raise ValueError(f"Axis '{label}' is degenerate")
    axis = max(range(3), key=lambda i: abs(vector[i]))
    if abs(vector[axis]) / norm < DOMINANCE:
        raise ValueError(f"Axis '{label}' is not aligned with one source axis ({abs(vector[axis]) / norm:.3f})")
    return axis, 1 if vector[axis] > 0 else -1


def derive_axis_mapping(groups):
    """Unity (left-handed: +X right, +Y up, +Z forward) = M @ source, M a signed permutation derived from data."""
    for name in ANCHOR_BONES:
        if not groups.get(name):
            raise ValueError(f"Missing anchor vertices for {name}")
    c = {k: _centroid(v) for k, v in groups.items()}
    feet = _centroid([c["LeftFoot"], c["RightFoot"]])
    toes = _centroid([c["LeftToeBase"], c["RightToeBase"]])
    up = [c["Head"][i] - feet[i] for i in range(3)]
    right = [c["RightFoot"][i] - c["LeftFoot"][i] for i in range(3)]
    forward = [toes[i] - feet[i] for i in range(3)]
    axes = {"right": _dominant(right, "right"), "up": _dominant(up, "up"), "forward": _dominant(forward, "forward")}
    if len({a for a, _ in axes.values()}) != 3:
        raise ValueError("Derived right/up/forward axes are not distinct")
    matrix = [[0, 0, 0] for _ in range(3)]
    for row, key in enumerate(("right", "up", "forward")):
        axis, sign = axes[key]
        matrix[row][axis] = sign
    m = matrix
    det = (m[0][0] * (m[1][1] * m[2][2] - m[1][2] * m[2][1]) - m[0][1] * (m[1][0] * m[2][2] - m[1][2] * m[2][0])
           + m[0][2] * (m[1][0] * m[2][1] - m[1][1] * m[2][0]))
    return {"matrix": matrix, "determinant": det, "flip_winding": det < 0,
            "rule": "up=Head-feet, right=RightFoot-LeftFoot, forward=toes-feet (rest dominant-lane centroids); "
                    "target Unity +X right/+Y up/+Z forward; det<0 => mirror, triangle winding flipped at load",
            "source_vectors": {"up": up, "right": right, "forward": forward},
            "dominance_threshold": DOMINANCE}


def _load_index(frames_dir):
    index = json.loads((Path(frames_dir) / "frames-index.json").read_text(encoding="utf-8"))
    if index.get("dtype") not in (None, "float64 little-endian"):
        raise ValueError("Unexpected frames dtype")
    return index


def build_pack(frames_dir, mesh_dir, out, clips=DEFAULT_CLIPS, repo_root=REPO_ROOT, *, skeleton):
    frames_dir, mesh_dir, out = Path(frames_dir), Path(mesh_dir), Path(out)
    require_outside_repo(out, repo_root)
    if out.exists() and any(out.iterdir()):
        raise FileExistsError(f"Output folder is not empty: {out}")
    index = _load_index(frames_dir)
    vertices = int(index["total_vertices"])
    offsets, triangles, sources = 0, [], {}
    for mesh in index["meshes"]:
        if mesh["vertex_offset"] != offsets:
            raise ValueError("Mesh vertex offsets are not contiguous")
        raw = (mesh_dir / mesh["file"]).read_bytes()
        sources[mesh["file"]] = {"bytes": len(raw), "sha256": sha256_bytes(raw)}
        data = json.loads(raw)
        if len(data["positions"]) != mesh["vertex_count"] or len(data["triangles"]) != mesh["triangle_count"]:
            raise ValueError(f"Mesh shape mismatch: {mesh['file']}")
        for tri in data["triangles"]:
            if len(tri) != 3 or any(not 0 <= i < mesh["vertex_count"] for i in tri):
                raise ValueError(f"Triangle index out of range: {mesh['file']}")
            triangles.extend(offsets + i for i in tri)
        offsets += mesh["vertex_count"]
    if offsets != vertices or vertices > 65535:
        raise ValueError("Vertex count mismatch or exceeds uint16 index range")
    payloads, clip_headers, max_round, rest_reference = {}, {}, 0.0, None
    for name in clips:
        clip = index["clips"].get(name)
        if clip is None:
            raise ValueError(f"Clip not found: {name}")
        raw = (frames_dir / clip["file"]).read_bytes()
        if len(raw) != clip["bytes"] or sha256_bytes(raw) != clip["sha256"]:
            raise ValueError(f"Input hash/size mismatch for {name}")
        frames = clip["shape"][0]
        if clip["shape"] != [frames, vertices, 3] or len(raw) != frames * vertices * 3 * 8 or frames < 2:
            raise ValueError(f"Shape mismatch for {name}")
        frame_ids = clip.get("frame_ids")  # H156: explicit; an absent list is no longer assumed to start with rest
        if not isinstance(frame_ids, list) or len(frame_ids) != frames or frame_ids[0] != "rest":
            raise ValueError(f"Frame 0 of {name} is not the rest frame")
        values = struct.unpack(f"<{frames * vertices * 3}d", raw)
        rest_bytes = raw[:vertices * 3 * 8]
        if rest_reference is None:
            rest_reference = rest_bytes
        elif rest_bytes != rest_reference:  # H156: the rest LBS frame 0 must be byte-identical across clips
            raise ValueError(f"Frame 0 (rest) of {name} differs from {clips[0]}")
        if any(v != v or v in (float("inf"), float("-inf")) for v in values):
            raise ValueError(f"Nonfinite input in {name}")
        packed = struct.pack(f"<{len(values)}f", *values)
        rounded = struct.unpack(f"<{len(values)}f", packed)
        max_round = max(max_round, max(abs(a - b) for a, b in zip(values, rounded)))
        file = f"clip-{name}.f32le.bin"
        payloads[file] = packed
        clip_headers[name] = {"file": file, "frames": frames, "rest_frame": 0, "first_key_frame": 1,
                              "keys": frames - 1, "bytes": len(packed), "sha256": sha256_bytes(packed),
                              "asset_name": clip.get("asset_name"), "source_file": clip["file"],
                              "source_sha256": clip["sha256"]}
    tri_bytes = struct.pack(f"<{len(triangles)}H", *triangles)
    first = index["clips"][clips[0]]
    rest = read_frame(frames_dir / first["file"], vertices, 0)
    skeleton = load_skeleton(skeleton)
    groups = anchor_groups(index, mesh_dir, rest, skeleton["names"])
    mapping = derive_axis_mapping(groups)
    up_axis = mapping["matrix"][1]
    up_index = max(range(3), key=lambda i: abs(up_axis[i]))
    sign = up_axis[up_index]
    heights = [sign * p[up_index] for p in rest]
    header = {
        "schema": SCHEMA, "label": LABEL,
        "redistribution": "Built locally from the user's own FIFA17 installation outputs. Never commit, bundle, upload or ship.",
        "vertex_count": vertices, "triangle_count": len(triangles) // 3, "keys_per_second": KEYS_PER_SECOND,
        "coordinates": "source FIFA rest/animated vertices; apply axis_mapping.matrix at load (Unity = M @ source)",
        "axis_mapping": mapping,
        "rest_height": {"extent": max(heights) - min(heights),
                        "head_to_feet_anchor_proxy": joint_head_to_feet(skeleton),
                        "units": "FIFA render units as stored (no conversion)",
                        "proxy_rule": PROXY_RULE, "head_to_feet_anchor_proxy_rule": PROXY_RULE_ID},
        "float32_max_abs_rounding": max_round,
        "triangles": {"file": TRIANGLES, "count": len(triangles) // 3, "bytes": len(tri_bytes),
                      "sha256": sha256_bytes(tri_bytes), "winding": "source", "index_format": "uint16 LE"},
        "clips": clip_headers,
        "meshes": [{"file": m["file"], "vertex_offset": m["vertex_offset"], "vertex_count": m["vertex_count"],
                    "triangle_count": m["triangle_count"]} for m in index["meshes"]],
        "sources": {"frames_index_label": index.get("label"), "meshes": sources, "skeleton_sha256": skeleton["sha256"]},
    }
    out.mkdir(parents=True, exist_ok=True)
    for file, data in payloads.items():
        (out / file).write_bytes(data)
    (out / TRIANGLES).write_bytes(tri_bytes)
    (out / HEADER).write_text(json.dumps(header, indent=2), encoding="utf-8")
    return header


def _exact_int(value):
    return isinstance(value, int) and not isinstance(value, bool)


# ---- H171: pack v2 builder (H170 pipeline outputs: frames-index.json + pack-v2-proposal.json + measurements.jsonl) ----
V2_SOURCES = {"idle": "idle_A", "walk": "walk_A", "jog": "jog_A", "run": "run_A", "sprint": "sprint_A",
              "strafe_right_walk": "strafeR_walk", "strafe_right_jog": "strafeR_jog", "strafe_left_jog": "strafeL_jog",
              "back_walk": "back_walk", "back_jog": "back_jog"}
# Whole gait cycles in each loop (keys_per_cycle = keys / cycles). H170 proposal values except two declared H171 corrections
# from the source-rig FK contact onsets in measurements.jsonl: strafeR_walk R onsets 10/29/48 (3 cycles in 56 keys, the
# proposal's 22.4 = 2.5 cycles cannot loop), strafeR_jog R onsets 5/17/29 (3 cycles in 34 keys, proposal 8.5 = 4).
V2_CYCLES = {"walk": 2, "jog": 2, "run": 2, "sprint": 2, "strafe_right_walk": 3, "strafe_right_jog": 3, "strafe_left_jog": 1,
             "back_walk": 2, "back_jog": 2}
FEET_TO_METRES = 0.30479
ORIGIN_MIN_KEYS = 4
NATIVE_SPEED_BASIS = ("H170 frames-index native_speed_tgt_units_per_key (AITrajectory authored root track, source feet x 0.30479 = "
                      "target/source hip-height ratio) x 30 keys/s ASSUMED; in-place clip, no root motion stored")
KEYS_PER_SECOND_BASIS = "ASSUMED: controller fps field (30); uniform keys (KeyTimeSize=0); not a certified native FIFA clock"
CYCLE_ORIGIN_RULE = ("cycle_origin_key = start key of the first source-rig FK LEFT-stance interval lasting >= 4 keys "
                     "(H170 measurements src_contact_sequence; heuristic, not a certified foot contact)")


def _cycle_origin(row, keys, name):
    for start, end, foot in row.get("src_contact_sequence") or []:
        if foot == "L" and end - start + 1 >= ORIGIN_MIN_KEYS and 0 <= start < keys:
            return start
    raise ValueError(f"No left-stance interval of >= {ORIGIN_MIN_KEYS} keys for {name}")


def build_pack_v2(frames_dir, mesh_dir, proposal_path, measurements_path, out, repo_root=REPO_ROOT, *, skeleton):
    """Pack v2 from the H170 pipeline outputs (decode -> rotation-only retarget + H137 arms -> FK skinned vertex frames,
    float64). Same mesh/axis/rest/triangle/piece derivation as v1; float64 -> float32 is the only numeric change."""
    frames_dir, mesh_dir, out = Path(frames_dir), Path(mesh_dir), Path(out)
    require_outside_repo(out, repo_root)
    if out.exists() and any(out.iterdir()):
        raise FileExistsError(f"Output folder is not empty: {out}")
    index = _load_index(frames_dir)
    if "frame 0 = 'rest'" not in str(index.get("frame_layout", "")):
        raise ValueError("frames-index does not declare frame 0 = rest")
    proposal = {c["id"]: c for c in json.loads(Path(proposal_path).read_text(encoding="utf-8"))["clips"]}
    rows = {}
    for line in Path(measurements_path).read_text(encoding="utf-8").splitlines():
        if line.strip():
            row = json.loads(line)
            rows.setdefault(row["asset_key"], row)
    vertices = int(index["total_vertices"])
    offsets, triangles, sources = 0, [], {}
    for mesh in index["meshes"]:
        if mesh["vertex_offset"] != offsets:
            raise ValueError("Mesh vertex offsets are not contiguous")
        raw = (mesh_dir / mesh["file"]).read_bytes()
        sources[mesh["file"]] = {"bytes": len(raw), "sha256": sha256_bytes(raw)}
        data = json.loads(raw)
        if len(data["positions"]) != mesh["vertex_count"] or len(data["triangles"]) != mesh["triangle_count"]:
            raise ValueError(f"Mesh shape mismatch: {mesh['file']}")
        for tri in data["triangles"]:
            if len(tri) != 3 or any(not 0 <= i < mesh["vertex_count"] for i in tri):
                raise ValueError(f"Triangle index out of range: {mesh['file']}")
            triangles.extend(offsets + i for i in tri)
        offsets += mesh["vertex_count"]
    if offsets != vertices or vertices > 65535:
        raise ValueError("Vertex count mismatch or exceeds uint16 index range")
    payloads, headers, max_round, rest_reference, first_file = {}, {}, 0.0, None, None
    for name in V2_CLIPS:
        source = V2_SOURCES[name]
        clip, prop = index["clips"].get(source), proposal.get(source)
        if clip is None or prop is None:
            raise ValueError(f"Clip {source} missing from the frames index or the proposal")
        raw = (frames_dir / clip["file"]).read_bytes()
        if len(raw) != clip["bytes"] or sha256_bytes(raw) != clip["sha256"]:
            raise ValueError(f"Input hash/size mismatch for {source}")
        frames = clip["shape"][0]
        keys = frames - 1
        rng = clip["range"]
        if clip["shape"] != [frames, vertices, 3] or len(raw) != frames * vertices * 24 or keys < 1 or rng[1] - rng[0] + 1 != keys:
            raise ValueError(f"Shape/range mismatch for {source}")
        if clip.get("loop") is not True or prop.get("loop") is not True or prop.get("frame_range") != rng:
            raise ValueError(f"{source} is not the declared loop/range of the proposal")
        if str(prop["source"].get("asset_key")) != str(clip["asset_key"]):
            raise ValueError(f"Proposal asset key disagrees for {source}")
        speed = float(clip["native_speed_tgt_units_per_key"]) * KEYS_PER_SECOND
        if not math.isfinite(speed) or abs(speed - float(prop["native_speed_mps"])) > 6e-4:
            raise ValueError(f"Proposal native speed disagrees for {source}")
        tier, direction = V2_ROLES[name]
        cycles = V2_CYCLES.get(name)
        kpc = keys / cycles if cycles else None
        if tier == "idle":
            speed, origin = 0.0 if speed == 0 else speed, None
            if speed != 0:
                raise ValueError("Idle clip must have zero native speed")
        else:
            if kpc < V2_MIN_KEYS_PER_CYCLE or not V2_NATIVE_SPEED_RANGE[0] <= speed <= V2_NATIVE_SPEED_RANGE[1]:
                raise ValueError(f"{source}: keys per cycle or native speed out of the declared domain")
            row = rows.get(str(clip["asset_key"]))
            if row is None:
                raise ValueError(f"No H170 measurement row for {source}")
            origin = _cycle_origin(row, keys, source)
        rest_bytes = raw[:vertices * 24]
        if rest_reference is None:
            rest_reference, first_file = rest_bytes, clip["file"]
        elif rest_bytes != rest_reference:
            raise ValueError(f"Frame 0 (rest) of {source} differs from {first_file}")
        values = struct.unpack(f"<{frames * vertices * 3}d", raw)
        if any(not math.isfinite(v) for v in values):
            raise ValueError(f"Nonfinite input in {source}")
        packed = struct.pack(f"<{len(values)}f", *values)
        rounded = struct.unpack(f"<{len(values)}f", packed)
        max_round = max(max_round, max(abs(a - b) for a, b in zip(values, rounded)))
        file = f"clip-{name}.f32le.bin"
        payloads[file] = packed
        headers[name] = {"file": file, "frames": frames, "rest_frame": 0, "first_key_frame": 1, "keys": keys, "bytes": len(packed),
                         "sha256": sha256_bytes(packed), "loop": True, "frame_range": list(rng), "tier": tier, "direction": direction,
                         "native_speed_mps": speed, "keys_per_cycle": kpc, "cycles_in_loop": cycles, "cycle_origin_key": origin,
                         "direction_deg_source_left_positive": prop.get("direction_deg"), "source_id": source,
                         "asset_name": clip.get("name"), "asset_key": str(clip["asset_key"]), "source_file": clip["file"],
                         "source_sha256": clip["sha256"], "loop_wrap_max_abs": clip.get("loop_wrap")}
    if sum(len(p) for p in payloads.values()) > MAX_PACK_BYTES_V2:
        raise ValueError("Pack v2 payload exceeds the declared plugin memory bound")
    tri_bytes = struct.pack(f"<{len(triangles)}H", *triangles)
    rest = read_frame(frames_dir / first_file, vertices, 0)
    skeleton = load_skeleton(skeleton)
    groups = anchor_groups(index, mesh_dir, rest, skeleton["names"])
    mapping = derive_axis_mapping(groups)
    up_axis = mapping["matrix"][1]
    up_index = max(range(3), key=lambda i: abs(up_axis[i]))
    sign = up_axis[up_index]
    heights = [sign * p[up_index] for p in rest]
    header = {
        "schema": SCHEMA_V2, "label": LABEL_V2,
        "redistribution": "Built locally from the user's own FIFA17 installation outputs. Never commit, bundle, upload or ship.",
        "vertex_count": vertices, "triangle_count": len(triangles) // 3, "keys_per_second": KEYS_PER_SECOND,
        "keys_per_second_basis": KEYS_PER_SECOND_BASIS, "native_speed_basis": NATIVE_SPEED_BASIS, "feet_to_metres": FEET_TO_METRES,
        "cycle_origin_rule": CYCLE_ORIGIN_RULE,
        "one_shots": "deferred: turn_135 / start_walk / stop_jog (H170 one-shots) not included; plugin one-shot triggering is a later step",
        "retarget": "H170 pipeline: VBR decode (F2) -> rotation-only rest-relative retarget + H137 per-limb arm alignment -> FK -> LBS 4 raw lanes/255, in place",
        "coordinates": "source FIFA rest/animated vertices; apply axis_mapping.matrix at load (Unity = M @ source)",
        "axis_mapping": mapping,
        "rest_height": {"extent": max(heights) - min(heights), "head_to_feet_anchor_proxy": joint_head_to_feet(skeleton),
                        "units": "FIFA render units as stored (no conversion)",
                        "proxy_rule": PROXY_RULE, "head_to_feet_anchor_proxy_rule": PROXY_RULE_ID},
        "float32_max_abs_rounding": max_round,
        "triangles": {"file": TRIANGLES, "count": len(triangles) // 3, "bytes": len(tri_bytes), "sha256": sha256_bytes(tri_bytes),
                      "winding": "source", "index_format": "uint16 LE"},
        "clips": headers,
        "meshes": [{"file": m["file"], "vertex_offset": m["vertex_offset"], "vertex_count": m["vertex_count"],
                    "triangle_count": m["triangle_count"]} for m in index["meshes"]],
        "sources": {"frames_index_label": index.get("label"), "meshes": sources,
                    "proposal_sha256": sha256_bytes(Path(proposal_path).read_bytes()),
                    "measurements_sha256": sha256_bytes(Path(measurements_path).read_bytes()), "skeleton_sha256": skeleton["sha256"]},
    }
    raw_header = json.dumps(header, indent=2).encode("utf-8")
    if len(raw_header) > MAX_HEADER_BYTES:
        raise ValueError("Pack v2 header exceeds the plugin header bound")
    out.mkdir(parents=True, exist_ok=True)
    for file, data in payloads.items():
        (out / file).write_bytes(data)
    (out / TRIANGLES).write_bytes(tri_bytes)
    (out / HEADER).write_bytes(raw_header)
    return header


CLEAN_PIPELINE = ("fm26vm build-pack: extract-spans -> read-bank (gd) -> read-meshes (meshset) -> vbr + retarget, in memory; no local/ input")


# ---- B8 (H228): pack v2 from the in-memory clean-room outputs (fm26vm build-pack); nothing under local/ is read ----
def build_pack_v2_files(recipe, meshes, frames):
    """recipe: loaded fm26vm recipe; meshes: meshset.read_meshes output; frames: retarget.build_frames output
    ({frames-<source_id>-f64le.bin: bytes}). Returns {pack file name: bytes} (same derivation as build_pack_v2)."""
    pieces, ranges = meshes["pieces"], meshes["ranges"]
    vertices = sum(p["vertex_count"] for p in pieces)
    triangles = [r["vertex_offset"] + i for p, r in zip(pieces, ranges) for tri in p["triangles"] for i in tri]
    clips = {c["id"]: c for c in recipe["clips"]}
    files, headers, max_round, rest = {}, {}, 0.0, None
    for name in V2_CLIPS:
        rc = clips[name]
        source_file = f"frames-{rc['source_id']}-f64le.bin"
        raw = frames.get(source_file)
        if raw is None:
            raise ValueError(f"{source_file}: missing from the clean-room frames")
        values = struct.unpack(f"<{len(raw) // 8}d", raw)
        n = vertices * 3
        count = len(values) // n
        if rest is None:
            rest, rest_raw, first = values[:n], raw[:n * 8], rc["source_id"]
        elif raw[:n * 8] != rest_raw:
            raise ValueError(f"frame 0 (rest) of {rc['source_id']} differs from {first}")
        packed = struct.pack(f"<{len(values)}f", *values)
        rounded = struct.unpack(f"<{len(values)}f", packed)
        max_round = max(max_round, max(abs(a - b) for a, b in zip(values, rounded)))
        file, keys, cycles = f"clip-{name}.f32le.bin", count - 1, rc["cycles_in_loop"]
        tier, direction = V2_ROLES[name]
        files[file] = packed
        headers[name] = {"file": file, "frames": count, "rest_frame": 0, "first_key_frame": 1, "keys": keys, "bytes": len(packed),
                         "sha256": sha256_bytes(packed), "loop": True, "frame_range": list(rc["frame_range"]), "tier": tier,
                         "direction": direction, "native_speed_mps": rc["native_speed_mps"],
                         "keys_per_cycle": keys / cycles if cycles else None, "cycles_in_loop": cycles,
                         "cycle_origin_key": rc["cycle_origin_key"],
                         "direction_deg_source_left_positive": rc["direction_deg_source_left_positive"],
                         "source_id": rc["source_id"], "asset_name": rc["asset_name"], "asset_key": rc["asset_key"],
                         "source_file": source_file, "source_sha256": sha256_bytes(raw),
                         # H170 loop_wrap: max |first key - last key| over all vertex coordinates (frame 1 vs last frame)
                         "loop_wrap_max_abs": max(abs(a - b) for a, b in zip(values[n:2 * n], values[(count - 1) * n:]))}
    tri_bytes = struct.pack(f"<{len(triangles)}H", *triangles)
    files[TRIANGLES] = tri_bytes
    points = [rest[i:i + 3] for i in range(0, len(rest), 3)]
    sk = meshes["skeleton"]
    groups = {bone: [] for bone in ANCHOR_BONES}
    for p, r in zip(pieces, ranges):
        for local, (bones, weights) in enumerate(zip(p["bone_indices_raw"], p["bone_weights_raw"])):
            lane = max(range(len(weights)), key=lambda i: (weights[i], -i))
            if sk["names"][bones[lane]] in groups:
                groups[sk["names"][bones[lane]]].append(list(points[r["vertex_offset"] + local]))
    mapping = derive_axis_mapping(groups)
    up_axis = mapping["matrix"][1]
    up_index = max(range(3), key=lambda i: abs(up_axis[i]))
    heights = [up_axis[up_index] * p[up_index] for p in points]
    skeleton = {"names": sk["names"], "joints": [[m[r][3] for r in range(3)] for m in sk["model"]]}
    header = {
        "schema": SCHEMA_V2, "label": LABEL_V2,
        "redistribution": "Built locally from the user's own FIFA17 installation outputs. Never commit, bundle, upload or ship.",
        "vertex_count": vertices, "triangle_count": len(triangles) // 3, "keys_per_second": KEYS_PER_SECOND,
        "keys_per_second_basis": KEYS_PER_SECOND_BASIS, "native_speed_basis": NATIVE_SPEED_BASIS, "feet_to_metres": FEET_TO_METRES,
        "cycle_origin_rule": CYCLE_ORIGIN_RULE,
        "one_shots": "deferred: turn_135 / start_walk / stop_jog (H170 one-shots) not included; plugin one-shot triggering is a later step",
        "retarget": "H170 pipeline: VBR decode (F2) -> rotation-only rest-relative retarget + H137 per-limb arm alignment -> FK -> LBS 4 raw lanes/255, in place",
        "coordinates": "source FIFA rest/animated vertices; apply axis_mapping.matrix at load (Unity = M @ source)",
        "axis_mapping": mapping,
        "rest_height": {"extent": max(heights) - min(heights), "head_to_feet_anchor_proxy": joint_head_to_feet(skeleton),
                        "units": "FIFA render units as stored (no conversion)",
                        "proxy_rule": PROXY_RULE, "head_to_feet_anchor_proxy_rule": PROXY_RULE_ID},
        "float32_max_abs_rounding": max_round,
        "triangles": {"file": TRIANGLES, "count": len(triangles) // 3, "bytes": len(tri_bytes), "sha256": sha256_bytes(tri_bytes),
                      "winding": "source", "index_format": "uint16 LE"},
        "clips": headers,
        "meshes": [{"file": p["file"], "vertex_offset": r["vertex_offset"], "vertex_count": r["vertex_count"],
                    "triangle_count": r["triangle_count"]} for p, r in zip(pieces, ranges)],
        "sources": {"pipeline": CLEAN_PIPELINE, "recipe_schema": recipe["schema"],
                    "spans_decoded_sha256": {s["id"]: s["decoded_sha256"] for s in recipe["spans"]}}}
    files[HEADER] = json.dumps(header, indent=2).encode("utf-8")
    return files


def _plain_file(name):
    return (isinstance(name, str) and 0 < len(name) <= 64 and not name.startswith(".") and ".." not in name
            and all(c.isascii() and (c.isalnum() or c in "-_.") for c in name))


def _sha(value):
    return isinstance(value, str) and len(value) == 64 and all(c in "0123456789abcdef" for c in value)


def _no_duplicates(pairs):
    keys = [k for k, _ in pairs]
    if len(keys) != len(set(keys)):
        raise ValueError("duplicate JSON key in pack header")
    return dict(pairs)


def plugin_header_errors(header):
    """The plugin's ValidateHeader rules (FifaOverlayPolicy.cs), as a list of error labels (empty = accepted)."""
    errors = []
    def need(condition, label):
        if not condition:
            errors.append(label)
        return condition
    v2 = header.get("schema") == SCHEMA_V2
    need(header.get("schema") in (SCHEMA, SCHEMA_V2), "schema")
    need(header.get("vertex_count") == PLUGIN_VERTEX_COUNT and _exact_int(header.get("vertex_count")), "vertex_count")
    need(header.get("triangle_count") == PLUGIN_TRIANGLE_COUNT and _exact_int(header.get("triangle_count")), "triangle_count")
    need(header.get("keys_per_second") == KEYS_PER_SECOND and _exact_int(header.get("keys_per_second")), "keys_per_second")
    axis = header.get("axis_mapping") or {}
    m = axis.get("matrix")
    if need(isinstance(m, list) and len(m) == 3 and all(isinstance(r, list) and len(r) == 3 and all(_exact_int(c) and -1 <= c <= 1 for c in r) for r in m),
            "axis-matrix-3x3-signed"):
        need(all(sum(1 for j in range(3) if m[i][j]) == 1 and sum(1 for j in range(3) if m[j][i]) == 1 for i in range(3)), "axis-matrix-permutation")
        det = (m[0][0] * (m[1][1] * m[2][2] - m[1][2] * m[2][1]) - m[0][1] * (m[1][0] * m[2][2] - m[1][2] * m[2][0])
               + m[0][2] * (m[1][0] * m[2][1] - m[1][1] * m[2][0]))
        need(_exact_int(axis.get("determinant")) and axis.get("determinant") == det, "axis-determinant")
        need(isinstance(axis.get("flip_winding"), bool) and axis.get("flip_winding") == (det < 0), "axis-flip-winding")
    heights = header.get("rest_height") or {}
    proxy, extent = heights.get("head_to_feet_anchor_proxy"), heights.get("extent")
    need(all(isinstance(x, (int, float)) and not isinstance(x, bool) and math.isfinite(x) for x in (proxy, extent))
         and PROXY_RANGE[0] <= proxy <= PROXY_RANGE[1] and extent >= proxy, "rest-height")
    tri = header.get("triangles") or {}
    need(tri.get("count") == PLUGIN_TRIANGLE_COUNT and tri.get("bytes") == PLUGIN_TRIANGLE_COUNT * 6 and _exact_int(tri.get("bytes")), "triangles-shape")
    need(_plain_file(tri.get("file")) and _sha(tri.get("sha256")), "triangles-file-sha")
    (_v2_clip_errors if v2 else _v1_clip_errors)(header.get("clips"), errors, need)
    errors.extend(piece_errors(header))
    return errors


def piece_errors(header):
    """H160: the plugin's ValidatePieces rules: exactly six pieces in builder order (file "mesh-<k>-<token>_..."),
    contiguous non-empty vertex ranges from 0, exact sums 1868 / 2911."""
    meshes = header.get("meshes")
    if not isinstance(meshes, list) or len(meshes) != len(PIECE_TOKENS):
        return ["pieces-count"]
    vertex = triangle = 0
    for k, (mesh, token) in enumerate(zip(meshes, PIECE_TOKENS)):
        if not isinstance(mesh, dict) or not _plain_file(mesh.get("file")) or not mesh["file"].startswith(f"mesh-{k + 1}-{token}_"):
            return [f"pieces-name:{k + 1}"]
        offset, count, tris = mesh.get("vertex_offset"), mesh.get("vertex_count"), mesh.get("triangle_count")
        if not all(_exact_int(x) for x in (offset, count, tris)) or offset != vertex or count < 1 or tris < 1:
            return [f"pieces-range:{k + 1}"]
        vertex += count
        triangle += tris
    if vertex != PLUGIN_VERTEX_COUNT or triangle != PLUGIN_TRIANGLE_COUNT:
        return ["pieces-cover"]
    return []


def verify_pack(folder):
    """Integrity (sizes, sha256, triangle range) AND the plugin's acceptance contract (H156: same constraints as
    FifaOverlayPolicy.ValidateHeader/DecodeTriangles/DecodeFrames: exact shape, clip set, layout, signed-permutation
    matrix, finite |v| <= 100, header size, duplicate keys). Reports, never repairs."""
    folder = Path(folder)
    errors = []
    try:
        raw = (folder / HEADER).read_bytes()
        if len(raw) > MAX_HEADER_BYTES:
            errors.append("header-size")
        header = json.loads(raw.decode("utf-8"), object_pairs_hook=_no_duplicates)
        errors.extend(plugin_header_errors(header))
        vertices = header["vertex_count"]
        entries = [(header["triangles"]["file"], header["triangles"]["bytes"], header["triangles"]["sha256"],
                    header["triangles"]["count"] * 3 * 2)]
        for name, clip in header["clips"].items():
            entries.append((clip["file"], clip["bytes"], clip["sha256"], clip["frames"] * vertices * 3 * 4))
        for file, size, digest, expected in entries:
            if not _plain_file(file):
                errors.append(f"file-name:{file}")
                continue
            data = (folder / file).read_bytes()
            if len(data) != size or size != expected:
                errors.append(f"size:{file}")
            if sha256_bytes(data) != digest:
                errors.append(f"sha256:{file}")
            if file != header["triangles"]["file"] and len(data) % 4 == 0:
                values = array.array("f")
                values.frombytes(data)
                if sys.byteorder != "little":
                    values.byteswap()
                if any(not math.isfinite(v) or abs(v) > MAX_ABS_VERTEX for v in values):
                    errors.append(f"vertex-domain:{file}")
        tri = (folder / header["triangles"]["file"]).read_bytes()
        indices = struct.unpack(f"<{len(tri) // 2}H", tri)
        if any(i >= vertices for i in indices):
            errors.append("triangle-range")
        if not piece_errors(header) and len(indices) == PLUGIN_TRIANGLE_COUNT * 3:  # H160: same rule as FifaOverlayPolicy.SplitPieces
            start = 0
            for mesh in header["meshes"]:
                low, high = mesh["vertex_offset"], mesh["vertex_offset"] + mesh["vertex_count"]
                if any(not low <= i < high for i in indices[start * 3:(start + mesh["triangle_count"]) * 3]):
                    errors.append("piece-triangle-range")
                    break
                start += mesh["triangle_count"]
    except Exception as error:  # report, never repair
        errors.append(f"unreadable:{error}")
    return {"ok": not errors, "errors": errors, "folder": str(folder)}


def main(argv=None):
    parser = argparse.ArgumentParser(description=__doc__.splitlines()[0], epilog=(
        "Low-level maintainer CLI: every input path is required. Users build the pack with "
        "'python -m tools.fm26vm build-pack' (or setup)."))
    sub = parser.add_subparsers(dest="action", required=True)
    build = sub.add_parser("build")
    build.add_argument("--frames", required=True)
    build.add_argument("--meshes", required=True)
    build.add_argument("--skeleton", required=True)
    build.add_argument("--out")
    verify = sub.add_parser("verify")
    verify.add_argument("--pack")
    build2 = sub.add_parser("build-v2")  # H171: locomotion set from the H170 pipeline outputs
    build2.add_argument("--frames", required=True)
    build2.add_argument("--meshes", required=True)
    build2.add_argument("--proposal", required=True)
    build2.add_argument("--measurements", required=True)
    build2.add_argument("--skeleton", required=True)
    build2.add_argument("--out")
    args = parser.parse_args(argv)
    if args.action in ("build", "build-v2"):
        if args.action == "build":
            out = Path(args.out) if args.out else default_pack_dir()
            header = build_pack(args.frames, args.meshes, out, skeleton=args.skeleton)
        else:
            out = Path(args.out) if args.out else default_pack_dir(2)
            header = build_pack_v2(args.frames, args.meshes, args.proposal, args.measurements, out, skeleton=args.skeleton)
        files = sorted(p for p in out.iterdir() if p.is_file())
        print(json.dumps({"pack": str(out), "files": {p.name: {"bytes": p.stat().st_size,
                                                             "sha256": sha256_bytes(p.read_bytes())} for p in files},
                          "total_bytes": sum(p.stat().st_size for p in files),
                          "axis_matrix": header["axis_mapping"]["matrix"],
                          "flip_winding": header["axis_mapping"]["flip_winding"],
                          "rest_height": header["rest_height"],
                          "float32_max_abs_rounding": header["float32_max_abs_rounding"]}, indent=2))
        return 0
    result = verify_pack(Path(args.pack) if args.pack else default_pack_dir())
    print(json.dumps(result, indent=2))
    return 0 if result["ok"] else 1


if __name__ == "__main__":
    sys.exit(main())
