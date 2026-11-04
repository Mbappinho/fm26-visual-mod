# SPDX-License-Identifier: MIT
"""Clean-room FIFA 17 GenericData projection reader (roadmap B5).

Written from the local prose contract CONTRACT-GD (H218, erratum E1 H219) and merged from two blind MIT
implementations (local/cr-gd-impl-a, local/cr-gd-impl-b; H219). Projects the 10 recipe clips (VBR scalars and
arrays, channel-to-DOF ids, controller) and the animation rig (joints, slot table, defaults) from the decoded
gameplay bank. Every refusal is a GdError carrying one contract rule id; no partial output is produced.
"""


import hashlib
import json
import mmap
import struct
from array import array


class GdError(RuntimeError):
    """A contract refusal: `rule` is the CONTRACT-GD rule id (R-...), `reason` a short explanation."""

    def __init__(self, rule, reason=""):
        super().__init__(f"{rule}: {reason}")
        self.rule, self.reason = rule, reason


MAX_LAYOUTS = 4096
MAX_FIELDS = 4096
MAX_STRING = 1024
MAX_ARRAY = 1048576
MAX_DEPTH = 8
MAX_JOINTS = 1024
MAX_DATA = 1000000
T_SLOT, Q_SLOT, S_SLOT = 2049856663, 14, 2049856454  # literal slot type ids (contract A9)
_S = {c: struct.Struct(">" + c) for c in "BHIQbhiqf"}
PRIM = {"Bool": (1, "B"), "UInt8": (1, "B"), "UInt16": (2, "H"), "Int32": (4, "i"),
        "UInt32": (4, "I"), "Int64": (8, "q"), "Float": (4, "f"), "DataRef": (8, "q")}
VBR_FLOATS = ["QuatMin", "TrajMin", "Vec3Min", "FloatMin", "QuatMax", "TrajMax", "Vec3Max", "FloatMax",
              "VectorOffsetScale", "FloatOffsetScale", "Dct"]
VBR_U16 = ["QuaternionCount", "Vector3Count", "FloatCount", "ConstQuaternionCount", "ConstVector3Count",
           "ConstFloatCount", "KeyTimeSize", "NumKeys", "ConstChanMapSize", "ConstPaletteSize", "VectorOffsetSize",
           "FloatOffsetSize", "Flags"]
CHANNEL_COUNTS = VBR_U16[:6]
I, A = False, True
# class -> (field, type, is_array, required)  (contract section 5)
SPEC = {
    "VbrAnimationAsset": [("__key", "Key", I, 1), ("__base", "DataRef", I, 1)]
    + [(n, "Float", I, 1) for n in VBR_FLOATS] + [(n, "UInt16", I, 1) for n in VBR_U16]
    + [("Data", "UInt8", A, 1), ("FrameBlockSizes", "UInt16", A, 1), ("ConstantPalette", "Float", A, 1),
       ("__name", "String", I, 0)],
    "AnimationAsset": [("__base", "DataRef", I, 1), ("ChannelToDofAsset", "Key", I, 1), ("DofSetList", "Key", I, 1),
                       ("CodecType", "UInt32", I, 0), ("AnimId", "UInt32", I, 0), ("TrimOffset", "Float", I, 0),
                       ("TrimmedDuration", "Float", I, 0), ("EndFrame", "UInt16", I, 0), ("Additive", "Bool", I, 0)],
    "ChannelToDofAsset": [("__key", "Key", I, 1), ("__base", "DataRef", I, 1), ("DofIds", "UInt16", A, 1)],
    "RigAsset": [("__key", "Key", I, 1), ("__base", "DataRef", I, 1), ("__name", "String", I, 1),
                 ("Skeleton", "Key", I, 1), ("DofIds", "UInt16", A, 1), ("DofSetIdIndices", "UInt16", A, 1),
                 ("RigDofSets", "Key", A, 1), ("DefaultVector3Values", "DefaultDofVector3", A, 1),
                 ("DefaultVector4Values", "DefaultDofVector4", A, 1)],
    "SkeletonAsset": [("__key", "Key", I, 1), ("__base", "DataRef", I, 1), ("__name", "String", I, 1),
                      ("Joints", "JointInfo", A, 1), ("TrajJointIndex", "Int32", I, 1)],
    "LayoutAsset": [("__key", "Key", I, 1), ("__base", "DataRef", I, 1), ("Slots", "LayoutEntry", A, 1)],
    "DeltaTrajLayoutAsset": [("__key", "Key", I, 1), ("__base", "DataRef", I, 1)],
    "ClipControllerAsset": [("__key", "Key", I, 1), ("__base", "DataRef", I, 1), ("__name", "String", I, 1),
                            ("Anim", "Key", I, 1), ("TickOffset", "Float", I, 1), ("FPS", "Float", I, 1),
                            ("TimeScale", "Float", I, 1), ("Distance", "Float", I, 1), ("Modes", "UInt8", I, 1)],
    "JointInfo": [("JointName", "String", I, 1), ("ParentIndex", "Int32", I, 1), ("JointType", "Int32", I, 1)],
    "LayoutEntry": [("Name", "String", I, 1), ("Type", "UInt32", I, 1)],
    "DefaultDofVector3": [("Value", "Vector3", I, 1), ("DofId", "UInt16", I, 1)],
    "DefaultDofVector4": [("Value", "Vector4", I, 1), ("DofId", "UInt16", I, 1)],
}


def _need(start, length, lo, hi, rule="R-BOUNDS", what=""):
    """Refuse unless [start, start + length) lies inside [lo, hi) (contract section 11)."""
    if length < 0 or start < lo or start + length > hi:
        raise GdError(rule, f"{what} [{start},+{length}) outside [{lo},{hi})")


def _rd(buf, fmt, pos):
    return _S[fmt].unpack_from(buf, pos)[0]


def _cstr(buf, pos, lo, hi, what):
    """NUL-terminated UTF-8 metadata string starting at pos, entirely inside [lo, hi) (R-REFL-STRING)."""
    end = buf.find(b"\0", pos, hi)
    if pos < lo or pos >= hi or end < 0:
        raise GdError("R-REFL-STRING", f"{what} outside its table or unterminated")
    try:
        return bytes(buf[pos:end]).decode("utf-8")
    except UnicodeDecodeError:
        raise GdError("R-REFL-STRING", f"{what} is not UTF-8")


# ---------------------------------------------------------------- framing (section 3)
def _marker(buf, pos, what):
    """A section header's eighth byte must be the big-endian marker b (R-ENDIAN)."""
    if buf[pos + 7] != 0x62:
        raise GdError("R-ENDIAN", f"{what} byte-order marker {bytes([buf[pos + 7]])!r}")


def frame(buf):
    """-> (REFL (start, end), DATA starts, DATA ends)."""
    n = len(buf)
    _need(0, 8, 0, n, what="envelope")
    if _rd(buf, "I", 0) != 3:
        raise GdError("R-PACKAGING", f"packaging word {_rd(buf, 'I', 0)}")
    st = 4 + _rd(buf, "I", 4)
    if st + 16 > n:
        raise GdError("R-STRM", f"no stream container header at {st}")
    if bytes(buf[st:st + 7]) != b"GD.STRM":
        raise GdError("R-STRM", f"tag at {st} is {bytes(buf[st:st + 7])!r}")
    _marker(buf, st, "GD.STRM")
    if st + _rd(buf, "I", st + 8) != n:
        raise GdError("R-STRM", f"stream ends at {st + _rd(buf, 'I', st + 8)}, input at {n}")
    refl, starts, ends = None, array("q"), array("q")
    pos = st + 16
    while pos < n:
        if pos + 16 > n:
            raise GdError("R-SECTION", f"truncated child header at {pos}")
        tag = bytes(buf[pos:pos + 7])
        if tag not in (b"GD.REFL", b"GD.DATA"):
            raise GdError("R-SECTION", f"child tag {tag!r} at {pos}")
        if tag == b"GD.REFL" and refl is not None:
            raise GdError("R-REFL", f"second REFL at {pos}")
        if tag == b"GD.DATA" and refl is None:
            raise GdError("R-REFL", f"first child at {pos} is not REFL")
        _marker(buf, pos, tag.decode("ascii"))
        size = _rd(buf, "I", pos + 8)
        if size < 16 or pos + size > n:
            raise GdError("R-SECTION", f"child size {size} at {pos}")
        if tag == b"GD.REFL":
            refl = (pos, pos + size)
        else:
            if _rd(buf, "I", pos + 12) > size:
                raise GdError("R-SECTION", f"DATA word > size at {pos}")
            if len(starts) >= MAX_DATA:
                raise GdError("R-BUDGET", f"more than {MAX_DATA} DATA sections")
            starts.append(pos)
            ends.append(pos + size)
        pos += size
    if refl is None:
        raise GdError("R-REFL", "no REFL section")
    return refl, starts, ends


# ---------------------------------------------------------------- REFL (section 4)
class Layout:
    __slots__ = ("off", "pos", "size", "align", "tab", "tab_end", "hash", "name", "nfields", "fields")


class Field:
    __slots__ = ("type_hash", "elem_size", "offset", "name", "count", "flags", "type_off", "skipped")


class Refl:
    def __init__(self, buf, start, end):
        self.buf, self.base, self.end = buf, start + 16, end
        base = self.base
        _need(base, 8, base, end, "R-REFL", "layout count")
        c = _rd(buf, "q", base)
        if c > MAX_LAYOUTS:
            raise GdError("R-BUDGET", f"layout count {c}")
        _need(base + 8, 8 * c, base, end, "R-REFL", "layout index")
        self.by_off, self.by_hash, self.by_name = {}, {}, {}
        for i in range(c):
            off = _rd(buf, "q", base + 8 + 8 * i)
            pos = base + off
            _need(pos, 32, base, end, "R-REFL", "layout record")
            L = Layout()
            L.off, L.pos, L.fields = off, pos, None
            mn, mx, L.size, L.align, st_off, st_len = (_rd(buf, "i", pos), _rd(buf, "i", pos + 4),
                                                       _rd(buf, "I", pos + 8), _rd(buf, "I", pos + 12),
                                                       _rd(buf, "I", pos + 16), _rd(buf, "I", pos + 20))
            L.hash = _rd(buf, "I", pos + 28)
            L.nfields = mx - mn + 1
            if L.nfields < 0 or L.nfields > MAX_FIELDS:
                raise GdError("R-REFL", f"field count {L.nfields}")
            if st_off != 32 + 32 * L.nfields:
                raise GdError("R-REFL", f"string table offset {st_off} != {32 + 32 * L.nfields}")
            L.tab, L.tab_end = pos + st_off, pos + st_off + st_len
            _need(L.tab, st_len, base, end, "R-REFL", "string table")
            if st_len < 1 or buf[L.tab] != 0:
                raise GdError("R-REFL-STRING", "string table does not start with NUL")
            L.name = _cstr(buf, L.tab + 1, L.tab, L.tab_end, "class name")
            if L.hash in self.by_hash:
                raise GdError("R-REFL-DUP", f"class hash {L.hash}")
            if L.name in self.by_name:
                raise GdError("R-REFL-DUP", f"class name {L.name!r}")
            self.by_hash[L.hash] = self.by_name[L.name] = L
            self.by_off.setdefault(off, L)

    def fields(self, L):
        if L.fields is None:
            out = []
            for i in range(L.nfields):
                p = L.pos + 32 + 32 * i
                f = Field()
                f.type_hash, f.elem_size, f.offset = _rd(self.buf, "I", p), _rd(self.buf, "I", p + 4), _rd(self.buf, "I", p + 8)
                f.count, f.flags, f.type_off = _rd(self.buf, "H", p + 16), _rd(self.buf, "H", p + 18), _rd(self.buf, "q", p + 24)
                f.skipped = f.elem_size == 0 or f.count == 0
                f.name = None if f.skipped else _cstr(self.buf, L.tab + _rd(self.buf, "I", p + 12), L.tab, L.tab_end,
                                                      f"field name of {L.name}")
                out.append(f)
            L.fields = out
        return L.fields

    def target(self, f):
        L = self.by_off.get(f.type_off)
        if L is None:
            raise GdError("R-REFL-TARGET", f"field {f.name} type offset {f.type_off} not in index")
        if L.hash != f.type_hash:
            raise GdError("R-REFL-TARGET", f"field {f.name} type hash {f.type_hash} != {L.hash}")
        return L


# ---------------------------------------------------------------- schema (section 5)
class TypeInfo:
    __slots__ = ("name", "kind", "width", "fmt", "stride", "plan", "ncomp")


class Plan:
    __slots__ = ("name", "hash", "size", "items")


class Schema:
    def __init__(self, refl):
        self.refl, self.types, self.plans = refl, {}, {}

    def layout(self, name):
        L = self.refl.by_name.get(name)
        if L is None:
            raise GdError("R-REFL", f"needed class {name} absent")
        return L

    def typeinfo(self, L, depth):
        if L.name in self.types:
            return self.types[L.name]
        t, recs = TypeInfo(), self.refl.fields(L)
        t.name = L.name
        if L.name not in PRIM and any(f.skipped for f in recs):
            raise GdError("R-REFL-SKIPPED", f"type {L.name} has a skipped record")
        if L.name in PRIM:
            if recs or L.size != PRIM[L.name][0]:
                raise GdError("R-TYPE", f"primitive {L.name} size {L.size} fields {len(recs)}")
            t.kind, (t.width, t.fmt) = "prim", PRIM[L.name]
            t.stride = t.width
        elif L.name == "Key":
            self._shape(L, recs, [("Data1", "Int64", 0)], 8)
            t.kind, t.width, t.fmt, t.stride = "key", 8, "q", 8
        elif L.name == "String":
            if L.size != 16:
                raise GdError("R-TYPE", f"String size {L.size}")
            t.kind, t.width, t.stride = "string", 16, 16
        elif L.name in ("Vector3", "Vector4"):
            t.kind, t.width, t.stride, t.ncomp = "vec", 16, 16, 3 if L.name == "Vector3" else 4
            self._shape(L, recs, [(c, "Float", 4 * i) for i, c in enumerate("xyzw"[:t.ncomp])], 16)
        else:
            t.kind, t.plan, t.width = "struct", self.plan(L.name, depth + 1), L.size
            t.stride = -(-L.size // L.align) * L.align if L.align else L.size
        self.types[L.name] = t
        return t

    def _shape(self, L, recs, want, size):
        got = [(f.name, self.refl.target(f).name, f.offset) for f in recs if f.flags == 0]
        if L.size != size or len(recs) != len(want) or got != want:
            raise GdError("R-TYPE", f"{L.name} layout shape {got!r} size {L.size}")

    def plan(self, cname, depth=0):
        if depth > MAX_DEPTH:
            raise GdError("R-DEPTH", f"struct nesting above {MAX_DEPTH} at {cname}")
        if cname in self.plans:
            return self.plans[cname]
        L = self.layout(cname)
        recs = self.refl.fields(L)
        p = Plan()
        p.name, p.hash, p.size, p.items = cname, L.hash, L.size, []
        if any(f.skipped for f in recs):
            raise GdError("R-REFL-SKIPPED", f"needed class {cname} has a skipped record")
        for fname, tname, is_arr, req in SPEC[cname]:
            hit = [f for f in recs if f.name == fname]
            if not hit and req:
                raise GdError("R-FIELD", f"{cname}.{fname} absent")
            if not hit:
                continue
            if len(hit) > 1:
                raise GdError("R-FIELD", f"{cname}.{fname} present {len(hit)} times")
            f = hit[0]
            if f.flags != int(is_arr):
                raise GdError("R-FLAGS", f"{cname}.{fname} flags {f.flags}")
            T = self.refl.target(f)
            if T.name != tname:
                raise GdError("R-FIELD", f"{cname}.{fname} type {T.name}, expected {tname}")
            ti = self.typeinfo(T, depth)
            width = 16 if is_arr else ti.width
            if f.offset + width > L.size:
                raise GdError("R-FIELD-BOUNDS", f"{cname}.{fname} at {f.offset}+{width} > size {L.size}")
            p.items.append((fname, f.offset, ti, is_arr))
        self.plans[cname] = p
        return p


# ---------------------------------------------------------------- roots and key lookup (sections 6, 7)
class Roots:
    def __init__(self, buf, starts, ends, refl):
        self.buf, self.starts, self.ends, self.refl = buf, starts, ends, refl
        self.cls, self.body, self.key_of = array("I"), array("q"), {}
        kcache = {}
        for i, (st, en) in enumerate(zip(starts, ends)):
            base = st + 16
            _need(base, 32, base, en, what="root header")
            tw = _rd(buf, "Q", base + 16)
            if tw >> 32:
                raise GdError("R-ROOT-TYPE", f"type word high half {tw >> 32} at {base}")
            L = refl.by_hash.get(tw)
            if L is None:
                raise GdError("R-ROOT-TYPE", f"unknown class hash {tw} at {base}")
            bo = _rd(buf, "H", base + 28)
            if bo < 32 or base + bo + L.size > en:
                raise GdError("R-ROOT-BODY", f"body offset {bo} size {L.size} at {base}")
            ko = kcache.get(tw)
            if ko is None:
                ko = kcache[tw] = self._key_offset(L)
            k = _rd(buf, "q", base + bo + ko)
            if k in self.key_of:
                raise GdError("R-KEY-DUP", f"key {k} in sections {self.key_of[k]} and {i}")
            self.key_of[k] = i
            self.cls.append(tw)
            self.body.append(base + bo)

    def _key_offset(self, L):
        hits = [f for f in self.refl.fields(L) if not f.skipped and f.name == "__key"]
        if len(hits) != 1 or hits[0].flags != 0 or self.refl.target(hits[0]).name != "Key":
            raise GdError("R-KEY", f"class {L.name} has no single Key-typed __key")
        return hits[0].offset

    def class_name(self, i):
        return self.refl.by_hash[self.cls[i]].name

    def resolve(self, key, expected):
        if key == 0:
            raise GdError("R-NULL-REF", f"null reference where {'/'.join(expected)} is required")
        i = self.key_of.get(key)
        if i is None:
            raise GdError("R-KEY-ABSENT", f"key {key}")
        if self.class_name(i) not in expected:
            raise GdError("R-CLASS", f"key {key} is {self.class_name(i)}, expected {'/'.join(expected)}")
        return i


# ---------------------------------------------------------------- typed projection (sections 8, 9.0)
class Obj(dict):
    __slots__ = ("section",)


class Reader:
    def __init__(self, buf, refl, schema, roots):
        self.buf, self.refl, self.schema, self.roots = buf, refl, schema, roots

    def string(self, at, sbase, send):
        buf = self.buf
        _need(at, 16, sbase, send, what="string header")
        n, pos = _rd(buf, "I", at + 4), _rd(buf, "q", at + 8)
        if n == 0:
            return ""
        if n > MAX_STRING:
            raise GdError("R-STRING", f"string count {n}")
        _need(sbase + pos, n, sbase, send, "R-STRING", "string span")
        raw = bytes(buf[sbase + pos:sbase + pos + n])
        if raw[-1] != 0:
            raise GdError("R-STRING", "missing final NUL")
        if 0 in raw[:-1]:
            raise GdError("R-STRING", "interior NUL")
        try:
            return raw[:-1].decode("utf-8")
        except UnicodeDecodeError:
            raise GdError("R-STRING", "not UTF-8")

    def inline(self, at, ti, sbase, send, depth):
        if ti.kind in ("prim", "key"):
            _need(at, ti.width, sbase, send, what=ti.name)
            v = _rd(self.buf, ti.fmt, at)
            return bool(v) if ti.name == "Bool" else v
        if ti.kind == "string":
            return self.string(at, sbase, send)
        if ti.kind == "vec":
            _need(at, 4 * ti.ncomp, sbase, send, what=ti.name)
            return list(struct.unpack_from(f">{ti.ncomp}f", self.buf, at))
        _need(at, ti.plan.size, sbase, send, what=ti.name)
        return self.project(at, ti.plan, sbase, send, depth + 1)

    def array(self, at, ti, sbase, send, depth):
        buf = self.buf
        _need(at, 16, sbase, send, what="array header")
        n, pos = _rd(buf, "I", at + 4), _rd(buf, "q", at + 8)
        if n > MAX_ARRAY:
            raise GdError("R-ARRAY", f"array count {n}")
        if n == 0:
            return b"" if ti.name == "UInt8" else []
        start = sbase + pos
        _need(start, n * ti.stride, sbase, send, "R-ARRAY", "array span")
        if ti.name == "UInt8":
            return bytes(buf[start:start + n])
        if ti.kind in ("prim", "key"):
            vals = list(struct.unpack_from(f">{n}{ti.fmt}", buf, start))
            return [bool(v) for v in vals] if ti.name == "Bool" else vals
        return [self.inline(start + i * ti.stride, ti, sbase, send, depth) for i in range(n)]

    def project(self, body, plan, sbase, send, depth=0):
        o = Obj()
        for name, off, ti, is_arr in plan.items:
            o[name] = (self.array if is_arr else self.inline)(body + off, ti, sbase, send, depth)
        return o

    def get(self, key, classes):
        """Resolve a key to a projected root object of one of `classes`."""
        return self.at(self.roots.resolve(key, classes))

    def at(self, i):
        st, en = self.roots.starts[i], self.roots.ends[i]
        o = self.project(self.roots.body[i], self.schema.plan(self.roots.class_name(i)), st + 16, en)
        o.section = (st, en)
        return o

    def base(self, owner, value, cname):
        """Base object reached through __base (section 8), inside the owner's section."""
        st, en = owner.section
        sbase = st + 16
        if value <= 0:
            raise GdError("R-BASE", f"__base {value}")
        hdr = sbase + value
        _need(hdr, 32, sbase, en, "R-BASE", "base header")
        L = self.refl.by_hash.get(_rd(self.buf, "Q", hdr + 16))
        if L is None or L.name != cname:
            raise GdError("R-CLASS", f"base is {L.name if L else None}, expected {cname}")
        bo = _rd(self.buf, "H", hdr + 28)
        if bo < 32 or hdr + bo + L.size > en:
            raise GdError("R-BASE", "base body outside section")
        o = self.project(hdr + bo, self.schema.plan(cname), sbase, en)
        o.section = owner.section
        if o["__base"] != 0:
            raise GdError("R-BASE-DEPTH", "nested base")
        return o


def _no_base(o, what):
    if o["__base"] != 0:
        raise GdError("R-BASE", f"{what} has __base {o['__base']}")


# ---------------------------------------------------------------- clips, controllers, rig (section 9)
def _clip(rd, rc):
    v = rd.get(int(rc["asset_key"]), ("VbrAnimationAsset",))
    b = rd.base(v, v["__base"], "AnimationAsset")
    if b["DofSetList"] == 0:
        raise GdError("R-NULL-REF", "AnimationAsset DofSetList is 0")
    c = rd.get(b["ChannelToDofAsset"], ("ChannelToDofAsset",))
    _no_base(c, "ChannelToDofAsset")
    chans = sum(v[n] for n in CHANNEL_COUNTS)
    if len(c["DofIds"]) != chans:
        raise GdError("R-CLIP-CHANNELS", f"DofIds {len(c['DofIds'])} != channels {chans}")
    if sum(v["FrameBlockSizes"]) > len(v["Data"]):
        raise GdError("R-CLIP-BLOCKS", f"block sizes {sum(v['FrameBlockSizes'])} > Data {len(v['Data'])}")
    fields = {n: v[n] for n in VBR_FLOATS + VBR_U16}
    fields["__base"] = v["__base"]
    return {"id": rc["id"], "asset_key": str(rc["asset_key"]), "section": list(v.section), "fields": fields,
            "Data": v["Data"], "FrameBlockSizes": v["FrameBlockSizes"], "ConstantPalette": v["ConstantPalette"],
            "channel_to_dof_key": str(b["ChannelToDofAsset"]), "hierarchy_key": str(b["DofSetList"]),
            "DofIds": c["DofIds"]}


def _controllers(rd, recipe, clips):
    """Section 9.2 (erratum E1: the controller's own __base is neither followed nor validated)."""
    plan = rd.schema.plan("ClipControllerAsset")
    off = [o for n, o, _, _ in plan.items if n == "Anim"][0]
    wanted = {int(rc["asset_key"]): [] for rc in recipe["clips"]}
    for i, h in enumerate(rd.roots.cls):
        if h == plan.hash:
            a = _rd(rd.buf, "q", rd.roots.body[i] + off)
            if a in wanted:
                wanted[a].append(i)
    for rc, item in zip(recipe["clips"], clips):
        hits = wanted[int(rc["asset_key"])]
        if len(hits) != 1:
            raise GdError("R-CTRL", f"{len(hits)} controllers with Anim {rc['asset_key']}")
        o = rd.at(hits[0])
        if o["__name"] != rc.get("asset_name"):
            raise GdError("R-CTRL-NAME", f"controller name {o['__name']!r} != recipe {rc.get('asset_name')!r}")
        if "controller_key" in rc and str(o["__key"]) != rc["controller_key"]:
            raise GdError("R-CTRL", f"controller key {o['__key']} != recipe {rc['controller_key']}")
        item["controller"] = {"key": str(o["__key"]), "name": o["__name"], "FPS": o["FPS"],
                              "TimeScale": o["TimeScale"], "Distance": o["Distance"]}


def _rig(rd, rig_key, rig_name):
    r = rd.get(rig_key, ("RigAsset",))
    _no_base(r, "RigAsset")
    if rig_name is not None and r["__name"] != rig_name:
        raise GdError("R-CLASS", f"rig {rig_key} is named {r['__name']!r}, expected {rig_name!r}")
    sk = rd.get(r["Skeleton"], ("SkeletonAsset",))
    _no_base(sk, "SkeletonAsset")
    joints = sk["Joints"]
    if not 1 <= len(joints) <= MAX_JOINTS:
        raise GdError("R-RIG-JOINTS", f"joint count {len(joints)}")
    seen = set()
    for i, j in enumerate(joints):
        nm, p = j["JointName"], j["ParentIndex"]
        if not nm:
            raise GdError("R-RIG-JOINTS", f"joint {i} name empty")
        if nm in seen:
            raise GdError("R-RIG-JOINTS", f"joint {i} name {nm!r} repeated")
        seen.add(nm)
        if (i == 0 and p != -1) or (i > 0 and not 0 <= p < i):
            raise GdError("R-RIG-JOINTS", f"joint {i} parent {p}")
    if not 0 <= sk["TrajJointIndex"] < len(joints):
        raise GdError("R-RIG-JOINTS", f"TrajJointIndex {sk['TrajJointIndex']}")
    dofs, starts, sets = r["DofIds"], r["DofSetIdIndices"], r["RigDofSets"]
    if len(starts) != len(sets):
        raise GdError("R-RIG-SETS", f"DofSetIdIndices {len(starts)} != RigDofSets {len(sets)}")
    slots = {}
    for i, (s, k) in enumerate(zip(starts, sets)):
        o = rd.get(k, ("LayoutAsset", "DeltaTrajLayoutAsset"))
        _no_base(o, "rig set")
        entries = o.get("Slots", [])
        if entries and s + len(entries) > len(dofs):
            raise GdError("R-RIG-WINDOW", f"set {i} start {s} + {len(entries)} > {len(dofs)}")
        for jx, e in enumerate(entries):
            d, val = dofs[s + jx], (e["Name"], e["Type"])
            prev = slots.setdefault(d, val)
            if prev != val:
                raise GdError("R-RIG-DOF-CONFLICT", f"DOF {d}: {slots[d]!r} vs {val!r}")
    v3, v4 = {}, {}
    for arr, tgt in ((r["DefaultVector3Values"], v3), (r["DefaultVector4Values"], v4)):
        for e in arr:
            if e["DofId"] in tgt:
                raise GdError("R-DEFAULT-DUP", f"DOF {e['DofId']} twice")
            tgt[e["DofId"]] = e["Value"]
    if set(v3) & set(v4):
        raise GdError("R-DEFAULT-DUP", f"DOF {min(set(v3) & set(v4))} in both default arrays")
    by_name = {}
    for d, (nm, _ty) in slots.items():
        by_name.setdefault(nm, set()).add(d)
    out_joints = []
    for j in joints:
        row = {"name": j["JointName"], "parent": j["ParentIndex"], "type": j["JointType"]}
        for c, want, src in (("t", T_SLOT, v3), ("q", Q_SLOT, v4), ("s", S_SLOT, v3)):
            ds = by_name.get(f"{j['JointName']}.{c}", set())
            if len(ds) != 1:
                raise GdError("R-RIG-BINDING", f"{j['JointName']}.{c} bound to {len(ds)} DOF ids")
            d = next(iter(ds))
            if slots[d][1] != want:
                raise GdError("R-RIG-SLOT-TYPE", f"{j['JointName']}.{c} slot type {slots[d][1]}")
            if d not in src:
                raise GdError("R-RIG-DEFAULT-MISSING", f"{j['JointName']}.{c} DOF {d}")
            row[c], row[c + "_dof"] = list(src[d]), d
        out_joints.append(row)
    defaults = {str(d): list(v) for d, v in v3.items()}
    defaults.update({str(d): list(v) for d, v in v4.items()})
    return {"rig_key": str(r["__key"]), "skeleton_key": str(sk["__key"]), "joints": out_joints,
            "slots": {str(d): {"name": nm, "type": ty} for d, (nm, ty) in sorted(slots.items())},
            "defaults": defaults}


# ---------------------------------------------------------------- public API
def _sha256(data):
    h, mv = hashlib.sha256(), memoryview(data)
    for i in range(0, len(data), 1 << 24):
        h.update(mv[i:i + (1 << 24)])
    return h.hexdigest()


def read_bank(data, recipe, rig_key, rig_name=None, golden=None):
    """Project clips (+ controllers) and rig from a decoded bank held in `data` (bytes or mmap)."""
    digest = _sha256(data)
    if golden is not None and (len(data) != golden["bytes"] or digest != golden["sha256"]):
        raise GdError("R-INPUT", f"input {len(data)}/{digest} differs from the recipe golden")
    (rs, re_), starts, ends = frame(data)
    refl = Refl(data, rs, re_)
    schema = Schema(refl)
    for cname in SPEC:
        schema.plan(cname)
    rd = Reader(data, refl, schema, Roots(data, starts, ends, refl))
    clips = [_clip(rd, rc) for rc in recipe["clips"]]
    _controllers(rd, recipe, clips)
    return {"bank": {"bytes": len(data), "sha256": digest}, "clips": clips, "rig": _rig(rd, rig_key, rig_name)}


CLIP_KEYS = ("id", "asset_key", "section", "fields", "FrameBlockSizes", "ConstantPalette", "channel_to_dof_key",
             "hierarchy_key", "DofIds", "controller")


def normative(result):
    """Contract section 10 JSON shape: Data replaced by its count and sha256."""
    clips = []
    for c in result["clips"]:
        o = {k: c[k] for k in CLIP_KEYS if k in c}
        o["Data"] = {"count": len(c["Data"]), "sha256": hashlib.sha256(c["Data"]).hexdigest()}
        clips.append(o)
    return {"bank": dict(result["bank"]), "clips": clips, "rig": dict(result["rig"])}


def canonical_json(result):
    """Canonical form of the normative output (sorted keys, no whitespace, ASCII); its sha256 is the recipe golden."""
    return json.dumps(normative(result), sort_keys=True, separators=(",", ":"), ensure_ascii=True)


def read_bank_file(path, recipe):
    """Read the bank file written by cas.extract_spans; rig key/name and input golden come from the recipe."""
    with open(path, "rb") as fh:
        if not fh.seek(0, 2):  # mmap cannot map an empty file: refuse it as the too-short envelope it is
            return read_bank(b"", recipe, int(recipe["rig"]["key"]), recipe["rig"]["name"],
                             recipe["golden"]["decoded_bank"])
        with mmap.mmap(fh.fileno(), 0, access=mmap.ACCESS_READ) as mm:
            return read_bank(mm, recipe, int(recipe["rig"]["key"]), recipe["rig"]["name"],
                             recipe["golden"]["decoded_bank"])
