# SPDX-License-Identifier: MIT
"""Clean-room FIFA 17 render skeleton + MeshSet reader (roadmap B6).

Written from the local prose contract CONTRACT-EBX-MESH (H220, erratum E1 H223) and merged from two blind MIT
implementations (local/cr-em-impl-a, local/cr-em-impl-b; H223). Reads the legacy EBX v2 render skeleton (438 joints),
the six mesh EBX (typed MeshSetResource link) and the six MeshSet RES payloads (positions, triangles, raw bone lanes,
palettes) decoded by `cas.extract_span`. Every refusal is a MeshError carrying one contract rule id; no partial output.
"""


import functools
import hashlib
import json
import math
import struct
from pathlib import Path


class MeshError(RuntimeError):
    """A contract refusal: `rule` is the CONTRACT-EBX-MESH rule id, `reason` a short explanation."""

    def __init__(self, rule, reason=""):
        super().__init__(f"{rule}: {reason}")
        self.rule, self.reason = rule, reason


MAGIC = 0x0FB2D1CE
INHERITED, STRUCT, ARRAY, CSTRING, INT32, UINT32, RESOURCEREF = 0, 2, 4, 7, 15, 16, 23
MAX_DEPTH = 8
MAX_NAME = 1024
EMPTY_STRING = 0xFFFFFFFF
POSES = ("LocalPose", "ModelPose", "InverseModelPose")
SIX = ("BoneNames", "BoneNameHashes", "Hierarchy") + POSES
FORMAT_SIZES = {3: 12, 6: 4, 8: 8, 13: 4, 23: 8}  # Float3, Half2, Half4, UByte4N, UShort4
MAX_T, MAX_N = 200_000, 65_535
BBOX_TOL = 0.001
SKELETON_KEYS = ("joint_count", "names", "name_hashes", "parents", "local", "model", "inverse")
PIECE_KEYS = ("piece", "file", "mesh", "short_name", "material_name", "vertex_count", "triangle_count",
              "positions", "triangles", "indices", "bone_indices_raw", "bone_weights_raw", "section_palette",
              "lod_palette", "bones_used", "bone_names_used", "index_policy", "index_posthoc_pad_bytes", "rid_u64",
              "res_meta_u32", "vertex_buffer_slack_bytes", "inline_padding", "stride", "bones_per_vertex_raw")


def _align16(x):
    return (x + 15) & ~15


def name_hash(raw):
    """Type-name hash of contract 1.2: start 5381, per byte multiply by 33 then XOR, keep 32 bits."""
    value = 5381
    for byte in raw:
        value = ((value * 33) ^ byte) & 0xFFFFFFFF
    return value


def _refuse_malformed(fn):
    """Inputs outside every named rule that would crash the narrow reader are refused as MALFORMED (erratum E1.1)."""
    @functools.wraps(fn)
    def wrapper(*args, **kwargs):
        try:
            return fn(*args, **kwargs)
        except (struct.error, IndexError, KeyError, ValueError, TypeError, OverflowError) as e:
            raise MeshError("MALFORMED", f"{type(e).__name__}: {e}") from None
    return wrapper


# ---- legacy EBX v2 container (contract section 1) ------------------------------------------------------------------
class _Ebx:
    """Header values and metadata tables of one EBX payload."""

    def u32(self, pos):
        return struct.unpack_from("<I", self.data, pos)[0]

    def own_fields(self, klass):
        return self.fields[klass["first"]:klass["first"] + klass["count"]]


def _parse_ebx(data):
    data = bytes(data)
    (magic, so, sdl, imports, ngroups, nexported, _unique, nclasses, nfields, tlen, slen, narrays,
     dlen) = struct.unpack_from("<IIIIHHHHHHIII", data, 0)
    if magic != MAGIC:
        raise MeshError("E-HDR-1", f"magic {magic:#010x}")
    if so + sdl != len(data):
        raise MeshError("E-HDR-2", f"strings_offset + strings_and_data_len = {so + sdl} != {len(data)}")
    if slen + dlen > sdl:
        raise MeshError("E-HDR-2", "strings_len + data_len exceeds strings_and_data_len")
    doc = _Ebx()
    doc.data, doc.exported = data, nexported
    doc.strings_offset, doc.strings_len = so, slen
    doc.data_start = so + slen
    doc.data_end = doc.data_start + dlen
    doc.array_base = doc.data_end
    tn = 64 + 32 * imports
    groups_at = tn + tlen + 16 * nfields + 16 * nclasses
    arrays_at = _align16(groups_at + 4 * ngroups)
    metadata_end = _align16(arrays_at + 12 * narrays)
    if metadata_end > so:
        raise MeshError("E-META-5", f"metadata tables end at {metadata_end} > strings_offset {so}")
    parts = data[tn:tn + tlen].split(b"\0")
    if parts[-1]:
        raise MeshError("E-META-1", "last type name is not NUL-terminated inside the table")
    names = {}
    for raw in parts[:-1]:
        names.setdefault(name_hash(raw), raw.decode("latin-1"))
    pos = tn + tlen
    doc.fields = []
    for k in range(nfields):
        h, raw_type, cref, offset, _second = struct.unpack_from("<IHHII", data, pos + 16 * k)
        if h not in names:
            raise MeshError("E-META-2", f"field {k} name hash {h:#010x} not in the type-name table")
        doc.fields.append({"name": names.get(h), "code": (raw_type >> 4) & 0x1F, "cref": cref, "offset": offset})
    pos += 16 * nfields
    doc.classes = []
    for k in range(nclasses):
        h, first, count, align, _raw, size, _size2 = struct.unpack_from("<IiBBHHH", data, pos + 16 * k)
        if h not in names:
            raise MeshError("E-META-3", f"class {k} name hash {h:#010x} not in the type-name table")
        if first < 0:
            raise MeshError("E-META-3", f"class {k} first field index {first} is negative")
        if first + count > nfields:
            raise MeshError("E-META-3", f"class {k} fields {first}+{count} exceed field_count {nfields}")
        if align not in (1, 2, 4, 8, 16):
            raise MeshError("E-META-3", f"class {k} alignment {align}")
        doc.classes.append({"name": names.get(h), "first": first, "count": count, "align": align, "size": size})
    doc.groups = []
    for k in range(ngroups):
        cref, count = struct.unpack_from("<HH", data, groups_at + 4 * k)
        if cref >= nclasses:
            raise MeshError("E-META-4", f"instance group {k} class {cref} >= class_count {nclasses}")
        doc.groups.append(cref)
    doc.arrays = [struct.unpack_from("<IIi", data, arrays_at + 12 * k) for k in range(narrays)]
    return doc


def _root(doc):
    """Root object = first object of the first instance group (contract 1.3); returns (class, field base)."""
    if doc.exported < 1:
        raise MeshError("E-INST-1", "the first instance group is not exported")
    klass = doc.classes[doc.groups[0]]
    if doc.data_start % klass["align"]:
        raise MeshError("E-INST-2", f"root start {doc.data_start} not aligned to {klass['align']}")
    if klass["align"] not in (4, 16):
        raise MeshError("E-INST-3", f"root class alignment {klass['align']}")
    return klass, doc.data_start + (8 if klass["align"] == 4 else 16)


def _find_field(doc, klass, name):
    hits = []

    def walk(k, depth):
        if depth > MAX_DEPTH:
            raise MeshError("E-INST-5", f"inheritance deeper than {MAX_DEPTH}")
        for f in doc.own_fields(k):
            if f["name"] == name:
                hits.append(f)
            if f["code"] == INHERITED:
                walk(doc.classes[f["cref"]], depth + 1)

    walk(klass, 0)
    if len(hits) != 1:
        raise MeshError("E-INST-6", f"field {name!r} found {len(hits)} times in the root chain")
    return hits[0]


def _field_pos(doc, base, field, width):
    pos = base + field["offset"]
    if pos + width > doc.data_end:
        raise MeshError("E-INST-4", f"field {field['name']} at {pos} outside the instance-data region")
    return pos


def _cstring(doc, offset):
    if offset == EMPTY_STRING:
        return ""
    if offset >= doc.strings_len:
        raise MeshError("E-ARR-3", f"string offset {offset} >= strings_len {doc.strings_len}")
    start = doc.strings_offset + offset
    nul = doc.data.find(b"\0", start, min(doc.strings_offset + doc.strings_len, start + MAX_NAME + 1))
    if nul < 0:
        raise MeshError("E-ARR-3", f"no NUL within the strings region / {MAX_NAME} bytes at offset {offset}")
    return doc.data[start:nul].decode("latin-1")


def _bind_array(doc, klass, base, name):
    """Array-typed root field -> (origin, count, stride, member field) after the E-ARR rules (contract 1.4)."""
    f = _find_field(doc, klass, name)
    index = doc.u32(_field_pos(doc, base, f, 4))
    if index >= len(doc.arrays):
        raise MeshError("E-ARR-1", f"{name}: array index {index} >= array_count {len(doc.arrays)}")
    rel, count, cref = doc.arrays[index]
    member = doc.own_fields(doc.classes[cref])[0]
    member_class = doc.classes[member["cref"]] if member["code"] == STRUCT else None
    stride, align = (member_class["size"], member_class["align"]) if member_class else (4, 4)
    origin = doc.array_base + rel
    if origin % align:
        raise MeshError("E-ARR-2", f"{name}: origin {origin} not aligned to {align}")
    if origin + count * stride > len(doc.data):
        raise MeshError("E-ARR-2", f"{name}: span [{origin}, {origin + count * stride}) outside the array payload")
    if doc.u32(origin - 4) != count:
        raise MeshError("E-ARR-2", f"{name}: count prefix {doc.u32(origin - 4)} != {count}")
    return {"origin": origin, "count": count, "stride": stride, "member_class": member_class}


# ---- render skeleton (contract sections 2 and 5) -------------------------------------------------------------------
@_refuse_malformed
def read_skeleton(data, expected_joints=438):
    """Decoded skeleton EBX payload -> {joint_count, names, name_hashes, parents, local, model, inverse}."""
    doc = _parse_ebx(data)
    klass, base = _root(doc)
    if klass["name"] != "SkeletonAsset":
        raise MeshError("SK-1", f"root class {klass['name']!r}")
    if klass["align"] != 4:
        raise MeshError("SK-1", f"root class alignment {klass['align']}")
    if klass["size"] != 56:
        raise MeshError("SK-1", f"root class size {klass['size']}")
    arrays = {name: _bind_array(doc, klass, base, name) for name in SIX}
    for name in POSES:
        mc = arrays[name]["member_class"]
        if mc is None or mc["name"] != "LinearTransform":
            raise MeshError("SK-4", f"{name} member class is not LinearTransform")
        if mc["size"] != 64:
            raise MeshError("SK-4", f"{name} member class size {mc['size']} != 64")
    j = arrays["BoneNames"]["count"]
    if any(a["count"] != j for a in arrays.values()):
        raise MeshError("SK-2", f"array counts {sorted({a['count'] for a in arrays.values()})} differ")
    if j != expected_joints:
        raise MeshError("SK-2", f"joint count {j} != {expected_joints}")
    spans = sorted((a["origin"], a["origin"] + a["count"] * a["stride"], n) for n, a in arrays.items())
    for (s0, e0, n0), (s1, _e1, n1) in zip(spans, spans[1:]):
        if s1 < e0:
            raise MeshError("SK-3", f"{n0} overlaps {n1}")
    d = doc.data
    out = {"joint_count": j,
           "names": [_cstring(doc, o) for o in struct.unpack_from(f"<{j}I", d, arrays["BoneNames"]["origin"])],
           "name_hashes": list(struct.unpack_from(f"<{j}I", d, arrays["BoneNameHashes"]["origin"])),
           "parents": list(struct.unpack_from(f"<{j}i", d, arrays["Hierarchy"]["origin"]))}
    for key, name in zip(("local", "model", "inverse"), POSES):
        mats = []
        for e in range(j):
            at = arrays[name]["origin"] + 64 * e
            lanes = [struct.unpack_from("<3f", d, at + 16 * c) for c in range(4)]  # right, up, forward, trans
            if not all(math.isfinite(v) for lane in lanes for v in lane):
                raise MeshError("SK-5", f"{name}[{e}] has a non-finite float")
            mats.append([[lanes[c][r] for c in range(4)] for r in range(3)] + [[0.0, 0.0, 0.0, 1.0]])
        out[key] = mats
    for i, p in enumerate(out["parents"]):
        if p != -1 and p >= i:
            raise MeshError("SK-6", f"joint {i} parent {p} does not precede it")
        if p < -1:
            raise MeshError("SK-6", f"joint {i} parent {p}")
    if len(set(out["names"])) != j:
        raise MeshError("SK-7", "bone names are not distinct")
    return out




# ---- mesh EBX: the typed MeshSetResource link (contract section 3) -------------------------------------------------
def rid_u64(rid):
    """Signed decimal catalogue literal (str or int) -> unsigned 64-bit value."""
    value = int(rid)
    return value + (1 << 64) if value < 0 else value


@_refuse_malformed
def read_mesh_link(data, expected_rid):
    """Decoded mesh EBX payload -> {rid_u64, absolute_offset} after checking the RID of the paired RES."""
    doc = _parse_ebx(data)
    klass, base = _root(doc)
    if klass["name"] != "SkinnedMeshAsset":
        raise MeshError("ME-1", f"root class {klass['name']!r}")
    if klass["align"] != 16:
        raise MeshError("ME-1", f"root class alignment {klass['align']}")
    f = _find_field(doc, klass, "MeshSetResource")
    if f["code"] != RESOURCEREF:
        raise MeshError("ME-2", f"MeshSetResource type code {f['code']}")
    pos = _field_pos(doc, base, f, 8)
    rid = struct.unpack_from("<Q", doc.data, pos)[0]
    if rid != rid_u64(expected_rid):
        raise MeshError("ME-3", f"RID {rid} != expected {rid_u64(expected_rid)}")
    return {"rid_u64": rid, "absolute_offset": pos}




# ---- MeshSet RES (contract sections 4 and 6) -----------------------------------------------------------------------
class _Meta:
    """Bounded reads inside the metadata part [0, inline_start) [RM-2]."""

    def __init__(self, data, end):
        self.data, self.end = data, end

    def need(self, pos, n, what):
        if pos < 0 or pos + n > self.end:
            raise MeshError("RM-2", f"{what} [{pos}, {pos + n}) outside the metadata part [0, {self.end})")
        return pos

    def unpack(self, fmt, pos, what):
        return struct.unpack_from("<" + fmt, self.data, self.need(pos, struct.calcsize("<" + fmt), what))

    def raw(self, pos, n, what):
        return self.data[self.need(pos, n, what):pos + n]

    def cstr(self, pos, what):
        nul = self.data.find(b"\0", self.need(pos, 1, what), self.end)
        if nul < 0:
            raise MeshError("RM-2", f"{what} at {pos} not NUL-terminated inside the metadata part")
        return self.data[pos:nul].decode("utf-8")


def _indices(ib, q, start, n, code):
    values = struct.unpack_from(f"<{q}{code}", ib, 0)[start:]
    return list(values) if all(v < n for v in values) else None


def _select_indices(ib, start, t, n):
    """Index model selection of contract 6.5 -> (policy, post-hoc pad bytes, indices)."""
    q = start + 3 * t
    if len(ib) == 4 * q:
        values = _indices(ib, q, start, n, "I")
        if values is not None:
            return "U32LE", 0, values
    values = _indices(ib, q, start, n, "H") if len(ib) >= 2 * q else None
    if len(ib) == 2 * q and values is not None:
        return "U16LE", 0, values
    extra = len(ib) - 2 * q
    if extra not in (1, 2, 3):
        raise MeshError("RS-16", f"no index model: extra length {extra} (RS-17 needs 1..3)")
    if len(ib) % 4:
        raise MeshError("RS-16", f"no index model: IB {len(ib)} is not a multiple of 4 (RS-17)")
    if any(ib[2 * q:]):
        raise MeshError("RS-16", "no index model: RS-17 trailing bytes are not zero")
    if values is None:
        raise MeshError("RS-16", "no index model accepted every index below N")
    return "POSTHOC_U16LE_ZERO_PAD_TO_4", extra, values


@_refuse_malformed
def read_res(res, res_meta, expected_path, joint_count=438):
    """Decoded MeshSet RES payload + its 16-byte ResMeta -> piece fields of contract section 7."""
    res = bytes(res)
    inline_start, inline_size, tail_size, opaque = struct.unpack("<4I", bytes(res_meta))
    if inline_start == 0:
        raise MeshError("RM-1", "ResMeta inline_start is 0")
    if inline_start + inline_size + tail_size != len(res):
        raise MeshError("RM-1", f"ResMeta {inline_start}+{inline_size}+{tail_size} != payload {len(res)}")
    m = _Meta(res, inline_start)
    bbox = m.unpack("8f", 0, "bounding box")
    if not all(math.isfinite(v) for v in bbox):
        raise MeshError("RS-1", "non-finite bounding box")
    lods = m.unpack("6q", 32, "LOD pointers")
    if any(lods[1:]):
        raise MeshError("RS-2", f"LOD pointers 2..6 are {lods[1:]}")
    path_ptr, short_ptr = m.unpack("2q", 88, "name pointers")
    mesh = m.cstr(path_ptr, "asset path")
    if mesh != expected_path:
        raise MeshError("RS-3", f"asset path {mesh!r} != {expected_path!r}")
    short_name = m.cstr(short_ptr, "short name")
    if short_name != expected_path.rsplit("/", 1)[-1]:
        raise MeshError("RS-3", f"short name {short_name!r} is not the last path component")
    mesh_type, = m.unpack("I", 108, "mesh type")
    lod_count, section_headers, bone_count, parts = m.unpack("4H", 124, "header counts")
    if mesh_type != 1:
        raise MeshError("RS-4", f"mesh type {mesh_type}")
    if lod_count != 1:
        raise MeshError("RS-4", f"LOD count {lod_count}")
    if section_headers != 1:
        raise MeshError("RS-4", f"section-header count {section_headers}")
    want_lod = _align16(148) if bone_count or parts else _align16(132)
    if lods[0] != want_lod:
        raise MeshError("RS-2", f"LOD record at {lods[0]}, expected {want_lod}")
    L = lods[0]
    lod_type, = m.unpack("I", L, "LOD type")
    section_count, S = m.unpack("iq", L + 8, "section pointer")
    if lod_type != 1:
        raise MeshError("RS-5", f"LOD type {lod_type}")
    if section_count != 1:
        raise MeshError("RS-5", f"section count {section_count}")
    if S != _align16(L + 164):
        raise MeshError("RS-5", f"section record at {S}, expected {_align16(L + 164)}")
    m.need(S, 224, "section record")
    for i in range(5):
        count, ptr = m.unpack("iq", L + 20 + 12 * i, f"category list {i}")
        if count not in (0, 1):
            raise MeshError("RS-6", f"category list {i} count {count}")
        if count and any(m.raw(ptr, count, f"category list {i}")):
            raise MeshError("RS-6", f"category list {i} byte is not zero")
    IB, VB = m.unpack("2I", L + 88, "buffer sizes")
    if any(m.raw(L + 96, 16, "chunk GUID")):
        raise MeshError("RS-7", "per-LOD chunk GUID is not zero (geometry in a separate chunk)")
    lod_palette_count, lod_palette_ptr = m.unpack("Iq", L + 152, "LOD palette")
    lod_palette = list(m.unpack(f"{lod_palette_count}I", lod_palette_ptr, "LOD palette"))
    material_ptr, = m.unpack("q", S + 8, "material pointer")
    material_name = m.cstr(material_ptr, "material name")
    T, start, voff, N, stride, prim, bpv, _u43, spal_count, spal_ptr = m.unpack("4I4BIq", S + 24, "section")
    if T == 0:
        raise MeshError("RS-8", "primitive count 0")
    if T > MAX_T:
        raise MeshError("RS-8", f"primitive count {T} > {MAX_T}")
    if N == 0:
        raise MeshError("RS-8", "vertex count 0")
    if N > MAX_N:
        raise MeshError("RS-8", f"vertex count {N} > {MAX_N} (u16 concatenation)")
    if prim != 3:
        raise MeshError("RS-8", f"primitive type {prim} is not a triangle list")
    records = [m.unpack("4B", S + 80 + 4 * k, "element record") for k in range(16)]
    streams = [m.unpack("2B", S + 144 + 2 * k, "stream record") for k in range(8)]
    active, active_streams = m.unpack("2B", S + 160, "element counts")
    if stride != streams[0][0]:
        raise MeshError("RS-9", f"section stride {stride} != stream 0 stride {streams[0][0]}")
    if active < 1:
        raise MeshError("RS-10", "no active vertex element")
    if active > 16:
        raise MeshError("RS-10", f"{active} active elements > 16")
    if active_streams != 1:
        raise MeshError("RS-10", f"{active_streams} active streams")
    if streams[0][1] != 0:
        raise MeshError("RS-10", f"stream 0 classification {streams[0][1]}")
    if any(s[0] for s in streams[1:]):
        raise MeshError("RS-10", "streams 1..7 have a non-zero stride")
    if any(r[0] for r in records[active:]):
        raise MeshError("RS-10", "an inactive element record has a non-zero usage")
    lanes, usages = [], {}
    for usage, fmt, off, stream in records[:active]:
        if usage == 0:
            raise MeshError("RS-10", "active element with usage 0")
        if stream != 0:
            raise MeshError("RS-10", f"element usage {usage} in stream {stream}")
        if fmt not in FORMAT_SIZES:
            raise MeshError("RS-11", f"element usage {usage} has unknown format {fmt}")
        size = FORMAT_SIZES.get(fmt, 0)
        if off + size > stride:
            raise MeshError("RS-10", f"element usage {usage} lanes [{off}, {off + size}) exceed stride {stride}")
        if any(off < e and s < off + size for s, e in lanes):
            raise MeshError("RS-10", f"element usage {usage} overlaps another element")
        if usage in usages:
            raise MeshError("RS-10", f"usage {usage} appears twice")
        lanes.append((off, off + size))
        usages.setdefault(usage, (fmt, off))
    if usages.get(1) != (3, 0):
        raise MeshError("RS-12", "position element is not Float3 at offset 0")
    if usages.get(2, (0,))[0] != 23:
        raise MeshError("RS-12", "bone-index element is missing or not UShort4")
    if usages.get(4, (0,))[0] != 13:
        raise MeshError("RS-12", "bone-weight element is missing or not UByte4N")
    section_palette = list(m.unpack(f"{spal_count}H", spal_ptr, "section palette"))
    if inline_size != _align16(VB + IB):
        raise MeshError("RS-13", f"inline_size {inline_size} != align16({VB} + {IB})")
    if any(res[inline_start + VB + IB:inline_start + inline_size]):
        raise MeshError("RS-13", "inline padding bytes are not zero")
    used = voff + N * stride
    slack = VB - used
    if slack < 0:
        raise MeshError("RS-14", f"vertices need {used} bytes > VB {VB}")
    if slack >= stride:
        raise MeshError("RS-14", f"vertex slack {slack} >= stride {stride}")
    if any(res[inline_start + used:inline_start + VB]):
        raise MeshError("RS-14", "vertex slack bytes are not zero")
    lo, hi = bbox[0:3], bbox[4:7]
    bi_off, bw_off = usages[2][1], usages[4][1]
    positions, bones, weights = [], [], []
    for v in range(N):
        at = inline_start + voff + v * stride
        xyz = struct.unpack_from("<3f", res, at)
        if not all(lo[a] - BBOX_TOL <= c <= hi[a] + BBOX_TOL for a, c in enumerate(xyz)):
            raise MeshError("RS-15", f"vertex {v} position {xyz} outside the bounding box")
        lanes4 = list(struct.unpack_from("<4H", res, at + bi_off))
        if any(b >= joint_count for b in lanes4):
            raise MeshError("RS-18", f"vertex {v} bone lanes {lanes4} reach the joint count {joint_count}")
        positions.append(list(xyz))
        bones.append(lanes4)
        weights.append(list(res[at + bw_off:at + bw_off + 4]))
    ib = res[inline_start + VB:inline_start + VB + IB]
    policy, pad_bytes, indices = _select_indices(ib, start, T, N)
    return {"mesh": mesh, "short_name": short_name, "material_name": material_name, "vertex_count": N,
            "triangle_count": T, "positions": positions, "triangles": [indices[i:i + 3] for i in range(0, 3 * T, 3)],
            "indices": indices, "bone_indices_raw": bones, "bone_weights_raw": weights,
            "section_palette": section_palette, "lod_palette": lod_palette,
            "bones_used": sorted({b for bs, ws in zip(bones, weights) for b, w in zip(bs, ws) if w}),
            "index_policy": policy, "index_posthoc_pad_bytes": pad_bytes,
            "res_meta_u32": [inline_start, inline_size, tail_size, opaque], "vertex_buffer_slack_bytes": slack,
            "inline_padding": inline_size - VB - IB, "stride": stride, "bones_per_vertex_raw": bpv}




# ---- whole read: skeleton + pieces + ranges (contract sections 7 and 8) --------------------------------------------


def read_meshes(skeleton, pieces, expected_joints=438):
    """skeleton: decoded skeleton EBX; pieces: [{ebx, res, res_meta, rid, asset}] in recipe order. All-or-nothing."""
    sk = read_skeleton(skeleton, expected_joints)
    out, ranges, flat = [], [], []
    v_off = t_off = 0
    for k, p in enumerate(pieces, 1):
        link = read_mesh_link(p["ebx"], p["rid"])
        piece = read_res(p["res"], p["res_meta"], p["asset"], sk["joint_count"])
        piece.update(piece=k, file=f"mesh-{k}-{piece['short_name']}.json", rid_u64=link["rid_u64"],
                     # A2 (kept as-is): an independent alphabetical list, NOT aligned with bones_used.
                     bone_names_used=sorted(sk["names"][b] for b in piece["bones_used"]))
        for i in piece["indices"]:
            if i + v_off > 0xFFFF:
                raise MeshError("CAT-1", f"piece {k}: global index {i + v_off} does not fit u16")
            flat.append(i + v_off)
        ranges.append({"piece": k, "vertex_offset": v_off, "vertex_count": piece["vertex_count"],
                       "triangle_offset": t_off, "triangle_count": piece["triangle_count"]})
        v_off += piece["vertex_count"]
        t_off += piece["triangle_count"]
        out.append(piece)
    blob = struct.pack(f"<{len(flat)}H", *flat)
    return {"skeleton": sk, "pieces": out, "ranges": ranges,
            "totals": {"vertices": v_off, "triangles": t_off, "concatenated_triangles_bytes": len(blob),
                       "concatenated_triangles_u16le_sha256": hashlib.sha256(blob).hexdigest()}}




def canonical_json(result):
    """Canonical form of the normative output (sorted keys, no whitespace, ASCII); its sha256 is the recipe golden."""
    normative = {"skeleton": {k: result["skeleton"][k] for k in SKELETON_KEYS},
                 "pieces": [{k: p[k] for k in PIECE_KEYS} for p in result["pieces"]],
                 "ranges": result["ranges"], "totals": result["totals"]}
    return json.dumps(normative, sort_keys=True, separators=(",", ":"), ensure_ascii=True)




def read_meshes_from_spans(spans, recipe, expected_joints=438):
    """spans: {span id: decoded bytes} (the files written by cas.extract_span); recipe: loaded recipe dict.

    ResMeta and RID come from the recipe mesh-res spans (`res_meta_hex`, `rid`; gap G1 closed by H223)."""
    skeletons = [s for s in recipe["spans"] if s["role"] == "skeleton-ebx"]
    if len(skeletons) != 1:
        raise MeshError("IN-3", f"recipe has {len(skeletons)} skeleton-ebx spans")

    def payload(span):
        if span["id"] not in spans:
            raise MeshError("IN-1", f"decoded span {span['id']} is missing")
        data = spans[span["id"]]
        if len(data) != span["decoded_size"]:
            raise MeshError("IN-1", f"{span['id']}: size {len(data)} != {span['decoded_size']}")
        if hashlib.sha256(data).hexdigest() != span["decoded_sha256"]:
            raise MeshError("IN-1", f"{span['id']}: sha256 differs from the recipe")
        return data

    pieces = []
    for e in (s for s in recipe["spans"] if s["role"] == "mesh-ebx"):
        res = [s for s in recipe["spans"] if s["role"] == "mesh-res" and s["asset"] == e["asset"]]
        if len(res) != 1:
            raise MeshError("IN-3", f"mesh {e['asset']} has {len(res)} mesh-res spans")
        if "res_meta_hex" not in res[0] or "rid" not in res[0]:
            raise MeshError("IN-2", f"recipe span {res[0]['id']} lacks res_meta_hex/rid (gap G1)")
        pieces.append({"ebx": payload(e), "res": payload(res[0]), "res_meta": bytes.fromhex(res[0]["res_meta_hex"]),
                       "rid": res[0]["rid"], "asset": e["asset"]})
    return read_meshes(payload(skeletons[0]), pieces, expected_joints)


def read_meshes_dir(spans_dir, recipe):
    """Read the recipe spans from the folder written by `extract-spans` (DIR/<span id>.bin)."""
    spans = {}
    for s in recipe["spans"]:
        if s["role"] in ("skeleton-ebx", "mesh-ebx", "mesh-res") and (Path(spans_dir) / f"{s['id']}.bin").is_file():
            spans[s["id"]] = (Path(spans_dir) / f"{s['id']}.bin").read_bytes()
    return read_meshes_from_spans(spans, recipe)
