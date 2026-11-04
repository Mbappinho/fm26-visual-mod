# SPDX-License-Identifier: MIT
"""Synthetic byte builders written from CONTRACT-EBX-MESH prose only (no game bytes).

Copied unchanged (apart from this docstring) from clean-room implementation B (local/cr-em-impl-b/tests/synth.py, MIT, H220) for the H223 integration tests of tools/fm26vm/meshset.py.
"""

import struct

MAGIC = 0x0FB2D1CE


def name_hash(name):
    v = 5381
    for b in name.encode("latin-1"):
        v = ((v * 33) ^ b) & 0xFFFFFFFF
    return v


def align16(x):
    return (x + 15) & ~15


def build_ebx(classes, fields, groups, arrays, strings, data, payload, exported=None, imports=0,
              extra_names=(), drop_names=()):
    """classes: (name, first, count, align, size); fields: (name, code, class_ref, data_offset);
    groups: (class_ref, count); arrays: (rel, count, class_ref). Returns (bytes, info)."""
    names = []
    for n in [c[0] for c in classes] + [f[0] for f in fields] + list(extra_names):
        if n not in names and n not in drop_names:
            names.append(n)
    table = b"".join(n.encode("latin-1") + b"\0" for n in names)
    meta = bytearray(64) + bytes(32 * imports) + table
    for n, code, cref, off in fields:
        meta += struct.pack("<IHHII", name_hash(n), code << 4, cref, off, 0)
    for n, first, cnt, al, size in classes:
        meta += struct.pack("<IiBBHHH", name_hash(n), first, cnt, al, 0, size, 0)
    for cref, cnt in groups:
        meta += struct.pack("<HH", cref, cnt)
    meta += bytes(align16(len(meta)) - len(meta))
    desc_pos = len(meta)
    for rel, cnt, cref in arrays:
        meta += struct.pack("<IIi", rel, cnt, cref)
    meta += bytes(align16(len(meta)) - len(meta))
    so = len(meta)
    strings = bytes(strings) + bytes(align16(so + len(strings)) - (so + len(strings)))
    d = so + len(strings)
    body = strings + bytes(data) + bytes(payload)
    struct.pack_into("<IIIIHHHHHHIII", meta, 0, MAGIC, so, len(body), imports, len(groups),
                     len(groups) if exported is None else exported, len(groups), len(classes), len(fields),
                     len(table), len(strings), len(arrays), len(data))
    meta[40:56] = bytes(range(16))
    info = {"strings_offset": so, "D": d, "B": d + len(data), "desc_pos": desc_pos}
    return bytes(meta) + body, info


def lt_bytes(m):
    """m: 3 rows x 4 cols (right, up, forward, trans columns) -> 64-byte LinearTransform."""
    out = b""
    for col in range(4):
        out += struct.pack("<3f", m[0][col], m[1][col], m[2][col]) + bytes(4)
    return out


SK_NAMES = ["Root", "Spine", "Head"]
SK_HASHES = [0x11111111, 0x22222222, 0x33333333]
SK_PARENTS = [-1, 0, 1]


def sk_mats(k):
    return [[[float(k * 100 + j * 10 + r * 4 + c) for c in range(4)] for r in range(3)] for j in range(3)]


def build_skeleton(names=SK_NAMES, parents=SK_PARENTS, root_name="SkeletonAsset", root_size=56,
                   lt_name="LinearTransform", lt_size=64, model_index=5, local_rel=64, local_prefix=None,
                   name_offsets=None, mats=None, root_align=4):
    j = len(names)
    strings = b""
    offs = []
    for n in names:
        offs.append(len(strings))
        strings += n.encode("latin-1") + b"\0"
    path_off = len(strings)
    strings += b"skel/path\0"
    if name_offsets is not None:
        offs = name_offsets
    mats = mats or [sk_mats(1), sk_mats(2), sk_mats(3)]
    classes = [("Asset", 0, 1, 4, 12), (root_name, 1, 7, root_align, root_size), ("array", 8, 1, 4, 4),
               ("array", 9, 1, 4, 4), ("array", 10, 1, 4, 4), ("array", 11, 1, 4, 4), (lt_name, 12, 4, 16, lt_size),
               ("Vec3", 16, 3, 16, 16)]
    fields = [("Name", 7, 0, 8), ("$", 0, 0, 8), ("BoneNames", 4, 2, 12), ("BoneNameHashes", 4, 3, 16),
              ("Hierarchy", 4, 4, 20), ("LocalPose", 4, 5, 24), ("ModelPose", 4, 5, 28),
              ("InverseModelPose", 4, 5, 32), ("member", 7, 0, 0), ("member", 16, 0, 0), ("member", 15, 0, 0),
              ("member", 2, 6, 0), ("right", 2, 7, 0), ("up", 2, 7, 16), ("forward", 2, 7, 32), ("trans", 2, 7, 48),
              ("x", 13, 0, 0), ("y", 13, 0, 4), ("z", 13, 0, 8)]
    data = bytearray(48)
    data[0:16] = bytes(range(100, 116))
    struct.pack_into("<I6I", data, 16, path_off, 1, 2, 3, 4, model_index, 6)
    pay = bytearray(4 + 4 * j)  # names at rel 4
    struct.pack_into("<I", pay, 0, j)
    struct.pack_into(f"<{j}I", pay, 4, *offs)
    arrays = [(0, 0, 2), (4, j, 2)]
    for vals, fmt, cref in ((SK_HASHES[:j], "I", 3), (parents, "i", 4)):
        rel = len(pay) + 4
        pay += struct.pack("<I", j) + struct.pack(f"<{j}{fmt}", *vals)
        arrays.append((rel, j, cref))
    pose_rel = []
    for k in range(3):
        rel = align16(len(pay) + 4)
        if k == 0:
            rel = local_rel if local_rel >= rel else rel
        pay += bytes(rel - len(pay))
        struct.pack_into("<I", pay, rel - 4, j)
        for e in range(j):
            pay += lt_bytes(mats[k][e])
        pose_rel.append(rel)
        arrays.append((rel, j, 5))
    if local_prefix is not None:
        struct.pack_into("<I", pay, pose_rel[0] - 4, local_prefix)
    pay += bytes(16)
    return build_ebx(classes, fields, [(1, 1)], arrays, strings, data, pay)


RES_PATH = "content/test/piece_mesh"
RES_POS = [(0.0, 0.0, 0.0), (1.0, 0.0, 0.5), (0.0, -1.0, 0.25)]
RES_BONES = [(0, 1, 2, 2), (1, 1, 0, 0), (2, 0, 1, 1)]
RES_WEIGHTS = [(255, 0, 0, 0), (128, 127, 0, 0), (0, 0, 200, 55)]
RES_ELEMS = [(1, 3, 0), (2, 23, 12), (4, 13, 20)]


def build_res(positions=RES_POS, bones=RES_BONES, weights=RES_WEIGHTS, indices=(0, 1, 2), index_width=2,
              trailing=b"", slack=b"", stride=32, elements=RES_ELEMS, tail=b"TAIL", path=RES_PATH,
              bbox=((-1.0, -1.0, -1.0), (1.0, 1.0, 1.0)), triangles=None, part_count=1, bone_count=3,
              palette=(0, 1, 2)):
    """Synthetic MeshSet RES per CONTRACT-EBX-MESH section 10 step 2. Returns (res, res_meta, info)."""
    n = len(positions)
    t = len(indices) // 3 if triangles is None else triangles
    meta = bytearray(1024)
    s = 336
    pos = 560
    def put_str(text):
        nonlocal pos
        at = pos
        raw = text.encode("utf-8") + b"\0"
        meta[pos:pos + len(raw)] = raw
        pos += len(raw)
        return at
    path_ptr = put_str(path)
    short_ptr = path_ptr + path.rfind("/") + 1
    mat_ptr = put_str("test_mat")
    pos = (pos + 3) & ~3
    sec_pal = pos
    struct.pack_into(f"<{len(palette)}H", meta, pos, *palette)
    pos += 2 * len(palette) + 2
    pos = (pos + 3) & ~3
    lod_pal = pos
    struct.pack_into(f"<{len(palette)}I", meta, pos, *palette)
    pos += 4 * len(palette)
    part_idx = pos
    struct.pack_into(f"<{part_count}H", meta, pos, *range(part_count))
    pos += 2 * part_count + 2
    pos = align16(pos)
    part_bbox = pos
    pos += 32 * part_count
    cat = pos
    meta[cat] = 0
    pos += 1
    inline_start = align16(pos)
    meta = meta[:inline_start]
    struct.pack_into("<8f", meta, 0, *bbox[0], 0.0, *bbox[1], 0.0)
    struct.pack_into("<6q", meta, 32, 160, 0, 0, 0, 0, 0)
    struct.pack_into("<qqII", meta, 88, path_ptr, short_ptr, 7, 1)
    meta[112:120] = bytes.fromhex("80ff7ff001000000")
    struct.pack_into("<IHHHHqq", meta, 120, 0, 1, 1, bone_count, part_count, part_idx, part_bbox)
    vb_used = n * stride
    vb = vb_used + len(slack)
    ib_raw = struct.pack(f"<{len(indices)}{'H' if index_width == 2 else 'I'}", *indices) + trailing
    ib = len(ib_raw)
    L = 160
    struct.pack_into("<IIiq", meta, L, 1, 0, 1, s)
    struct.pack_into("<iq", meta, L + 20, 1, cat)  # one category list with one zero byte
    for i in range(1, 5):
        struct.pack_into("<iq", meta, L + 20 + 12 * i, 0, 0)
    struct.pack_into("<IiII", meta, L + 80, 1, 33, ib, vb)
    struct.pack_into("<3q", meta, L + 116, mat_ptr, mat_ptr, mat_ptr)
    struct.pack_into("<Iq", meta, L + 152, len(palette), lod_pal)
    struct.pack_into("<q", meta, s + 8, mat_ptr)
    struct.pack_into("<IIIIBBBBIq", meta, s + 24, t, 0, 0, n, stride, 3, 4, 0, len(palette), sec_pal)
    for k in range(16):
        rec = (elements[k][0], elements[k][1], elements[k][2], 0) if k < len(elements) else (0, 0, 0, 0xFF)
        struct.pack_into("<4B", meta, s + 80 + 4 * k, *rec)
    struct.pack_into("<BB", meta, s + 144, stride, 0)
    struct.pack_into("<BB", meta, s + 160, len(elements), 1)
    vbuf = bytearray()
    for v in range(n):
        vert = bytearray(stride)
        struct.pack_into("<3f", vert, 0, *positions[v])
        struct.pack_into("<4H", vert, 12, *bones[v])
        struct.pack_into("<4B", vert, 20, *weights[v])
        vbuf += vert
    vbuf += slack
    geo = bytes(vbuf) + ib_raw
    pad = align16(len(geo)) - len(geo)
    geo += bytes(pad)
    res = bytes(meta) + geo + tail
    res_meta = struct.pack("<4I", inline_start, len(geo), len(tail), 0x00E000A0)
    info = {"L": L, "S": s, "inline_start": inline_start, "vb": vb, "ib": ib, "pad": pad, "ib_start": inline_start + vb,
            "mat_ptr": mat_ptr, "cat": cat}
    return res, res_meta, info


def build_mesh_ebx(rid, root_name="SkinnedMeshAsset", root_align=16, msr_code=23, msr_name="MeshSetResource"):
    classes = [("MeshAsset", 0, 1, 16, 48), (root_name, 1, 1, root_align, 144)]
    fields = [(msr_name, msr_code, 0, 16), ("$", 0, 0, 8)]
    data = bytearray(48)
    data[0:16] = bytes(range(200, 216))
    struct.pack_into("<Q", data, 32, rid)
    return build_ebx(classes, fields, [(1, 1)], [(0, 0, 0)], b"mesh/path\0", data, b"")
