# SPDX-License-Identifier: MIT
"""Synthetic GenericData bank writer built from CONTRACT-GD section 13 prose (copied verbatim from the
MIT clean-room implementation B, local/cr-gd-impl-b/tests/synth.py, H218; catalogue appended in H219).

The bank is described by a mutable model (`Model`); a test applies ONE
mutation to the model (or to the produced bytes) and serialises it.
Layout offsets are chosen by this writer (they deliberately differ from
the real bank) so the reader cannot rely on hard-coded offsets.
"""
import copy
import struct

PRIM = {"Bool": 1, "UInt8": 1, "Int8": 1, "UInt16": 2, "Int32": 4,
        "UInt32": 4, "Int64": 8, "Float": 4, "DataRef": 8}
FMT = {"Bool": "B", "UInt8": "B", "Int8": "b", "UInt16": "H", "Int32": "i",
       "UInt32": "I", "Int64": "q", "Float": "f", "DataRef": "q", "Key": "q"}

T_SLOT, Q_SLOT, S_SLOT = 2049856663, 14, 2049856454


def _al(x, a):
    return (x + a - 1) // a * a


class Model:
    """Layouts + roots; serialise() turns it into bank bytes."""

    def __init__(self):
        self.endian = ">"
        self.skip = 20            # S; prefix bytes 8 .. 4+S
        self.layouts = []         # list of dict
        self.roots = []           # list of dict (class, fields, base?)
        self.extra_refl = False   # add a second REFL section
        self.data_patches = []    # (root index, callable(bytearray section))
        self.refl_patch = None    # callable(bytearray refl section, offsets)

    # ---- layouts -------------------------------------------------
    def layout(self, name):
        for l in self.layouts:
            if l["name"] == name:
                return l
        raise KeyError(name)

    def add_layout(self, name, h, size, align, fields, min_slot=0):
        self.layouts.append(dict(name=name, hash=h, size=size, align=align,
                                 fields=fields, min_slot=min_slot))

    def width(self, tname):
        if tname in PRIM:
            return PRIM[tname]
        return self.layout(tname)["size"]

    def stride(self, tname):
        if tname in PRIM:
            return PRIM[tname]
        l = self.layout(tname)
        return _al(l["size"], l["align"])

    # ---- serialisation ---------------------------------------------
    def build_refl(self):
        e = self.endian
        n = len(self.layouts)
        offs = []
        pos = 8 + 8 * n
        blobs = []
        for l in self.layouts:
            pos = _al(pos, 8)
            offs.append(pos)
            nf = len(l["fields"])
            tab = bytearray(b"\0") + l.get("emit_name", l["name"]).encode() + b"\0"
            name_offs = []
            for f in l["fields"]:
                name_offs.append(len(tab))
                tab += f.get("emit_name", f["name"]).encode() + b"\0"
            size = 32 + 32 * nf + len(tab)
            blobs.append((l, tab, name_offs))
            pos += size
        idx_of = {l["name"]: o for l, o in zip(self.layouts, offs)}
        out = bytearray(struct.pack(e + "q", n))
        for o in offs:
            out += struct.pack(e + "q", o)
        for (l, tab, name_offs), o in zip(blobs, offs):
            out += b"\0" * (o - len(out))
            nf = len(l["fields"])
            st_off = l.get("st_off", 32 + 32 * nf)
            st_len = l.get("st_len", len(tab))
            out += struct.pack(e + "iiIIIIBBxxI", l["min_slot"],
                               l["min_slot"] + nf - 1, l["size"], l["align"],
                               st_off, st_len, 0, 0, l["hash"])
            for f, no in zip(l["fields"], name_offs):
                t = self.layout(f["type"])
                arr = f.get("flags", 0) == 1
                es = f.get("elem_size", 16 if (arr or f["type"] == "String")
                           else self.width(f["type"]))
                out += struct.pack(e + "IIIIHHHhq",
                                   f.get("type_hash", t["hash"]), es,
                                   f["offset"], no, f.get("count", 1),
                                   f.get("flags", 0), t["align"], 0,
                                   f.get("type_off", idx_of[f["type"]]))
            out += tab
        return bytes(out)

    def build_root(self, r):
        """Return section payload (bytes after the 16-byte header)."""
        e = self.endian
        lay = self.layout(r["class"])
        sec = bytearray(32 + _al(lay["size"], 8))
        tail = []  # deferred payloads

        def alloc(data, align=16):
            pos = _al(len(sec), align)
            sec.extend(b"\0" * (pos - len(sec)))
            sec.extend(data)
            return pos

        def write_obj(body, layname, values):
            l = self.layout(layname)
            fmap = {f["name"]: f for f in l["fields"]}
            for name, val in values.items():
                f = fmap[name]
                at = body + f["offset"]
                write_field(at, f["type"], f.get("flags", 0), val)

        def write_inline(at, tname, val):
            if tname == "String":
                write_string(at, val)
            elif tname in FMT:
                struct.pack_into(e + FMT[tname], sec, at, val)
            elif tname in ("Vector3", "Vector4"):
                for i, v in enumerate(val):
                    struct.pack_into(e + "f", sec, at + 4 * i, v)
            else:
                write_obj(at, tname, val)

        def write_string(at, val):
            if isinstance(val, tuple):   # (raw bytes incl. NUL, count override)
                raw, cnt = val
            elif val == "":
                struct.pack_into(e + "IIq", sec, at, 0, 0, 0)
                return
            else:
                raw = val.encode() + b"\0"
                cnt = len(raw)
            pos = alloc(raw, 1)
            struct.pack_into(e + "IIq", sec, at, 0, cnt, pos)

        def write_field(at, tname, flags, val):
            if flags == 1:
                if isinstance(val, dict):   # explicit header override
                    struct.pack_into(e + "IIq", sec, at, val["cap"], val["n"], val["pos"])
                    return
                n = len(val)
                if n == 0:
                    struct.pack_into(e + "IIq", sec, at, 0, 0, 12345)
                    return
                st = self.stride(tname)
                if tname == "UInt8" and isinstance(val, (bytes, bytearray)):
                    pos = alloc(bytes(val), 16)
                else:
                    pos = alloc(b"\0" * (st * n), 16)
                    for i, v in enumerate(val):
                        write_inline(pos + i * st, tname, v)
                struct.pack_into(e + "IIq", sec, at, n, n, pos)
            else:
                write_inline(at, tname, val)

        struct.pack_into(e + "QQQIHH", sec, 0, 0x1111, 0x2222,
                         r.get("type_word", lay["hash"]), 7,
                         r.get("body_off", 32), 0)
        write_obj(32, r["class"], r["fields"])
        if "base" in r:
            b = r["base"]
            bl = self.layout(b["class"])
            hdr = alloc(b"\0" * (32 + _al(bl["size"], 8)), 8)
            struct.pack_into(e + "QQQIHH", sec, hdr, 0, 0, bl["hash"], 0, 32, 0)
            write_obj(hdr + 32, b["class"], b["fields"])
            if r.get("base_ptr") is None:
                fl = {f["name"]: f for f in lay["fields"]}["__base"]
                struct.pack_into(e + "q", sec, 32 + fl["offset"], hdr)
        sec.extend(b"\0" * (_al(len(sec), 16) - len(sec)))
        return sec

    def serialize(self):
        e = self.endian
        mk = b"b" if e == ">" else b"l"
        refl = bytearray(self.build_refl())
        if self.refl_patch:
            self.refl_patch(refl, self)
        sections = [(b"GD.REFL", refl, 99)]
        if self.extra_refl:
            sections.append((b"GD.REFL", refl, 99))
        for i, r in enumerate(self.roots):
            sec = self.build_root(r)
            for (ri, fn) in self.data_patches:
                if ri == i:
                    fn(sec, self)
            sections.append((b"GD.DATA", sec, r.get("word", 16)))
        body = bytearray()
        for tag, payload, word in sections:
            body += tag + mk + struct.pack(e + "II", 16 + len(payload), word) + payload
        out = bytearray(struct.pack(">II", 3, self.skip))   # envelope: big-endian (see REPORT D1)
        out += b"\xAB" * (self.skip - 4)
        out += b"GD.STRM" + mk + struct.pack(e + "II", 16 + len(body), 4242) + body
        return bytes(out)


# ------------------------------------------------------------------------
def _seq(fields, start=0):
    """Assign offsets sequentially with natural alignment: (name,type,flags)."""
    out, pos = [], start
    for name, t, flags in fields:
        if flags == 1 or t == "String":
            w, a = 16, 8
        elif t in PRIM:
            w = a = PRIM[t]
        elif t == "Key":
            w = a = 8
        elif t in ("Vector3", "Vector4"):
            w = a = 16
        else:
            w, a = None, 8
        pos = _al(pos, a)
        out.append(dict(name=name, type=t, flags=flags, offset=pos))
        pos += w if w else 0
    return out, pos


def _obj(m, name, h, fields, align=8):
    # Object classes: header fields first in a writer-chosen order.
    f, end = _seq([("__name", "String", 0), ("__base", "DataRef", 0),
                   ("__key", "Key", 0)] + fields)
    m.add_layout(name, h, end, align, f, min_slot=-3)


def positive_model():
    m = Model()
    hashes = iter(range(701, 800))
    for p in ["Bool", "UInt8", "Int8", "UInt16", "Int32", "UInt32", "Int64",
              "Float", "DataRef"]:
        m.add_layout(p, next(hashes), PRIM[p], PRIM[p], [])
    m.add_layout("String", 41, 16, 8, [dict(name="chars", type="Int8", flags=1, offset=0)])
    m.add_layout("Key", 42, 8, 8, [dict(name="Data1", type="Int64", flags=0, offset=0)])
    m.add_layout("Vector3", 43, 16, 16, [dict(name=c, type="Float", flags=0, offset=4 * i)
                                         for i, c in enumerate("xyz")])
    m.add_layout("Vector4", 44, 16, 16, [dict(name=c, type="Float", flags=0, offset=4 * i)
                                         for i, c in enumerate("xyzw")])
    m.add_layout("JointInfo", 51, 24, 8, [
        dict(name="JointName", type="String", flags=0, offset=0),
        dict(name="ParentIndex", type="Int32", flags=0, offset=16),
        dict(name="JointType", type="Int32", flags=0, offset=20)])
    m.add_layout("LayoutEntry", 52, 20, 8, [
        dict(name="Name", type="String", flags=0, offset=0),
        dict(name="Type", type="UInt32", flags=0, offset=16)])
    m.add_layout("DefaultDofVector3", 53, 18, 16, [
        dict(name="DofId", type="UInt16", flags=0, offset=16),
        dict(name="Value", type="Vector3", flags=0, offset=0)])
    m.add_layout("DefaultDofVector4", 54, 18, 16, [
        dict(name="DofId", type="UInt16", flags=0, offset=16),
        dict(name="Value", type="Vector4", flags=0, offset=0)])
    flt = ["QuatMin", "TrajMin", "Vec3Min", "FloatMin", "QuatMax", "TrajMax",
           "Vec3Max", "FloatMax", "VectorOffsetScale", "FloatOffsetScale", "Dct"]
    u16 = ["QuaternionCount", "Vector3Count", "FloatCount", "ConstQuaternionCount",
           "ConstVector3Count", "ConstFloatCount", "KeyTimeSize", "NumKeys",
           "ConstChanMapSize", "ConstPaletteSize", "VectorOffsetSize",
           "FloatOffsetSize", "Flags"]
    _obj(m, "VbrAnimationAsset", 61,
         [("Data", "UInt8", 1), ("FrameBlockSizes", "UInt16", 1),
          ("ConstantPalette", "Float", 1)] + [(n, "Float", 0) for n in flt]
         + [(n, "UInt16", 0) for n in u16] + [("Unlisted", "Int64", 0)])
    _obj(m, "AnimationAsset", 62,
         [("ChannelToDofAsset", "Key", 0), ("DofSetList", "Key", 0),
          ("CodecType", "UInt32", 0), ("AnimId", "UInt32", 0),
          ("TrimOffset", "Float", 0), ("TrimmedDuration", "Float", 0),
          ("EndFrame", "UInt16", 0), ("Additive", "Bool", 0)])
    _obj(m, "ChannelToDofAsset", 63, [("DofIds", "UInt16", 1)])
    _obj(m, "RigAsset", 64, [("DofIds", "UInt16", 1), ("DofSetIdIndices", "UInt16", 1),
                             ("RigDofSets", "Key", 1),
                             ("DefaultVector3Values", "DefaultDofVector3", 1),
                             ("DefaultVector4Values", "DefaultDofVector4", 1),
                             ("Skeleton", "Key", 0)])
    _obj(m, "SkeletonAsset", 65, [("Joints", "JointInfo", 1), ("TrajJointIndex", "Int32", 0)])
    _obj(m, "LayoutAsset", 66, [("Slots", "LayoutEntry", 1)])
    _obj(m, "DeltaTrajLayoutAsset", 67, [])
    _obj(m, "ClipControllerAsset", 68,
         [("Anim", "Key", 0), ("TickOffset", "Float", 0), ("FPS", "Float", 0),
          ("TimeScale", "Float", 0), ("Distance", "Float", 0), ("Modes", "UInt8", 0)])

    def root(cls, key, **f):
        d = {"__key": key, "__base": 0}
        d.update(f)
        r = {"class": cls, "fields": d}
        m.roots.append(r)
        return r

    root("SkeletonAsset", 1001, __name="skel_b",
         Joints=[{"JointName": "J0", "ParentIndex": -1, "JointType": 1},
                 {"JointName": "J1", "ParentIndex": 0, "JointType": 0}],
         TrajJointIndex=1)
    slots = [("J0.t", T_SLOT), ("J0.q", Q_SLOT), ("J0.s", S_SLOT),
             ("J1.t", T_SLOT), ("J1.s", S_SLOT), ("J1.q", Q_SLOT)]
    root("LayoutAsset", 2001, Slots=[{"Name": n, "Type": t} for n, t in slots])
    root("LayoutAsset", 2002, Slots=[])
    root("DeltaTrajLayoutAsset", 2003)
    root("RigAsset", 3001, __name="rig_b", Skeleton=1001,
         DofIds=[900, 901, 10, 11, 12, 13, 14, 15],
         DofSetIdIndices=[0, 2, 65535], RigDofSets=[2003, 2001, 2002],
         DefaultVector3Values=[
             {"DofId": 10, "Value": (0.1, 0.0, -2.5)},
             {"DofId": 12, "Value": (1.0, 1.0, 1.0)},
             {"DofId": 13, "Value": (0.0, 3.25, 0.2)},
             {"DofId": 14, "Value": (0.9999998, 1.0, 1.0000001)}],
         DefaultVector4Values=[
             {"DofId": 11, "Value": (0.0, 0.0, 0.0, 1.0)},
             {"DofId": 15, "Value": (0.7071069, 0.7071066, -3.9e-07, -7.3e-07)}])
    root("ChannelToDofAsset", 4001, DofIds=[11, 15, 13, 10])
    vbr = {n: 0.0 for n in flt}
    vbr.update(QuatMin=-0.7654324, QuatMax=1.0, Vec3Max=0.1, Dct=4.0)
    vbr.update({n: 0 for n in u16})
    vbr.update(QuaternionCount=2, Vector3Count=1, FloatCount=1, NumKeys=9,
               VectorOffsetSize=7, Flags=270)
    r = root("VbrAnimationAsset", 5001, __name="clip_b.chan",
             Data=bytes(range(40)), FrameBlockSizes=[10, 12],
             ConstantPalette=[0.5, -1.25], Unlisted=77, **vbr)
    r["base"] = {"class": "AnimationAsset", "fields": {
        "__key": 0, "__base": 0, "ChannelToDofAsset": 4001, "DofSetList": 6001,
        "CodecType": 1447186976, "AnimId": 3000000000, "TrimOffset": 0.0,
        "TrimmedDuration": 8.0, "EndFrame": 8, "Additive": 1}}
    root("ClipControllerAsset", 7001, __name="ClipB_Name", Anim=5001,
         TickOffset=0.0, FPS=30.0, TimeScale=0.5, Distance=2.5, Modes=3)
    root("ClipControllerAsset", 7002, __name="other", Anim=5999,
         TickOffset=0.0, FPS=30.0, TimeScale=1.0, Distance=0.0, Modes=1)
    return m


RECIPE = {"clips": [{"id": "c1", "asset_key": "5001", "asset_name": "ClipB_Name"}]}
RIG_KEY = 3001


def find_root(m, cls, key):
    for i, r in enumerate(m.roots):
        if r["class"] == cls and r["fields"]["__key"] == key:
            return i, r
    raise KeyError((cls, key))


def recipe():
    return copy.deepcopy(RECIPE)


# ------------------------------------------------------------------------
# H219 case catalogue: the 44 single-mutation refusal cases of CONTRACT-GD vectors.json (mutations from
# implementation B's tests/test_refusals.py) plus H219 extras for guards that no vector exercises.
# Each entry: name -> (kind, fn, budget overrides). kind m = model, b = bytes, r = recipe, g = golden, k = rig kw.
def sections(b):
    s = struct.unpack_from(">I", b, 4)[0]
    pos, out = 4 + s + 16, []
    while pos < len(b):
        size = struct.unpack_from(">I", b, pos + 8)[0]
        out.append((pos, bytes(b[pos:pos + 7]), size))
        pos += size
    return out


def data_section(b, i):
    return [x for x in sections(b) if x[1] == b"GD.DATA"][i]


def patch(b, at, fmt, val):
    b = bytearray(b)
    struct.pack_into(fmt, b, at, val)
    return bytes(b)


def _root(m, cls, key):
    return find_root(m, cls, key)[1]


def _field(m, cls, name):
    return [f for f in m.layout(cls)["fields"] if f["name"] == name][0]


def _refl_at(b):
    return sections(b)[0][0]


def _b_strm_short(b):
    st = 4 + struct.unpack_from(">I", b, 4)[0]
    return patch(b, st + 8, ">I", struct.unpack_from(">I", b, st + 8)[0] - 1)


def _b_strm_extend(b):
    st = 4 + struct.unpack_from(">I", b, 4)[0]
    return patch(b + bytes(8), st + 8, ">I", struct.unpack_from(">I", b, st + 8)[0] + 8)


def _b_data_tag(b):
    st = data_section(b, 2)[0]
    return bytes(b[:st]) + b"GD.XXXX" + bytes(b[st + 7:])


def _b_root_high(b):
    st = data_section(b, find_root(positive_model(), "LayoutAsset", 2002)[0])[0]
    return patch(b, st + 32, ">Q", struct.unpack_from(">Q", b, st + 32)[0] | (1 << 32))


def _b_root_type(b, h):
    st = data_section(b, find_root(positive_model(), "LayoutAsset", 2002)[0])[0]
    return patch(b, st + 32, ">Q", h)


def _b_body16(b):
    st = data_section(b, find_root(positive_model(), "LayoutAsset", 2002)[0])[0]
    return patch(b, st + 16 + 28, ">H", 16)


def _b_refl_layout0(b, rel, fmt, val):
    st = _refl_at(b)
    off0 = struct.unpack_from(">q", b, st + 16 + 8)[0]
    return patch(b, st + 16 + off0 + rel, fmt, val)


def _m_extra_root(cls, key, **f):
    def fn(m):
        d = {"__key": key, "__base": 0}
        d.update(f)
        m.roots.append({"class": cls, "fields": d})
    return fn


def _m_set(cls, key, path, value):
    def fn(m):
        obj = _root(m, cls, key)["fields"]
        for p in path[:-1]:
            obj = obj[p]
        obj[path[-1]] = value
    return fn


def _m_field_set(cls, name, attr, value):
    def fn(m):
        _field(m, cls, name)[attr] = value
    return fn


def _m_numkeys_oob(m):
    f = _field(m, "VbrAnimationAsset", "NumKeys")
    f["offset"] = m.layout("VbrAnimationAsset")["size"] - 1
    del _root(m, "VbrAnimationAsset", 5001)["fields"]["NumKeys"]


def _m_data_flags2(m):
    _field(m, "VbrAnimationAsset", "Data")["flags"] = 2
    del _root(m, "VbrAnimationAsset", 5001)["fields"]["Data"]


def _m_numkeys_dup(m):
    f = dict(_field(m, "VbrAnimationAsset", "NumKeys"))
    m.layout("VbrAnimationAsset")["fields"].append(f)


def _m_numkeys_type(m):
    _field(m, "VbrAnimationAsset", "NumKeys")["type"] = "Int32"
    del _root(m, "VbrAnimationAsset", 5001)["fields"]["NumKeys"]


def _m_base_ptr(value):
    def fn(m):
        r = _root(m, "VbrAnimationAsset", 5001)
        r["base_ptr"] = True
        r["fields"]["__base"] = value
    return fn


def _m_base_class(m):
    _root(m, "VbrAnimationAsset", 5001)["base"] = {"class": "DeltaTrajLayoutAsset",
                                                   "fields": {"__key": 0, "__base": 0}}


def _m_slot_removed(m):
    _root(m, "LayoutAsset", 2001)["fields"]["Slots"].pop()


def _m_default_missing(m):
    v = _root(m, "RigAsset", 3001)["fields"]["DefaultVector3Values"]
    v[:] = [x for x in v if x["DofId"] != 14]


def _m_default_dup(m):
    _root(m, "RigAsset", 3001)["fields"]["DefaultVector3Values"].append({"DofId": 10, "Value": (0.1, 0.0, -2.5)})


def _m_default_both(m):
    _root(m, "RigAsset", 3001)["fields"]["DefaultVector4Values"].append({"DofId": 10, "Value": (0.0, 0.0, 0.0, 1.0)})


def _m_conflict(m):
    _root(m, "RigAsset", 3001)["fields"]["DofIds"][3] = 10


def _m_two_refl(m):
    m.extra_refl = True


def _m_layout(name, attr, value):
    def fn(m):
        m.layout(name)[attr] = value
    return fn


def _m_key_second(m):
    m.layout("Key")["fields"].append(dict(name="Data2", type="Int64", flags=0, offset=0))


def _m_name_unterminated(m):
    l = m.layout("ClipControllerAsset")
    l["st_len"] = 1 + len(l["name"])


def _refl_patch(fn):
    def outer(m):
        m.refl_patch = fn
    return outer


def _rp_table_first_byte(refl, m):
    off0 = struct.unpack_from(">q", refl, 8)[0]
    n = len(m.layouts[0]["fields"])
    refl[off0 + 32 + 32 * n] = 0x41


def _rp_class_name_utf8(refl, m):
    off0 = struct.unpack_from(">q", refl, 8)[0]
    n = len(m.layouts[0]["fields"])
    refl[off0 + 32 + 32 * n + 1] = 0xFF


def _rp_count_huge(refl, m):
    struct.pack_into(">q", refl, 0, 4000)


def _rp_offset_huge(refl, m):
    struct.pack_into(">q", refl, 8, len(refl) + 64)


def _m_endian(m):
    m.endian = "<"


def _r_set(path, value):
    def fn(r):
        r["clips"][0][path] = value
    return fn


def _empty_stream():
    return struct.pack(">II", 3, 4) + b"GD.STRMb" + struct.pack(">II", 16, 0)


FLOAT_HASH = 708  # Float layout hash in positive_model (Bool=701 ... Float=708)

CASES = {
    # --- 44 vector refusal cases (names as in vectors.json synthetic.refusals) ---
    "packaging word 1": ("b", lambda b: patch(b, 0, ">I", 1)),
    "STRM end one byte short of EOF": ("b", _b_strm_short),
    "a DATA section tag changed to GD.XXXX": ("b", _b_data_tag),
    "a DATA size set to 15": ("b", lambda b: patch(b, data_section(b, 2)[0] + 8, ">I", 15)),
    "a DATA word set to size + 1": ("b", lambda b: patch(b, data_section(b, 2)[0] + 12, ">I", data_section(b, 2)[2] + 1)),
    "byte-order marker of the REFL set to l": ("b", lambda b: b[:_refl_at(b) + 7] + b"l" + b[_refl_at(b) + 8:]),
    "two REFL sections": ("m", _m_two_refl),
    "layout string-table offset not equal to 32 + 32N": ("m", lambda m: m.layout("JointInfo").__setitem__(
        "st_off", 32 + 32 * len(m.layout("JointInfo")["fields"]) + 4)),
    "class name without terminating NUL inside the table": ("m", _m_name_unterminated),
    "a needed class gets one field record with element size 0": ("m", _m_field_set("VbrAnimationAsset", "Unlisted", "elem_size", 0)),
    "field type hash differs from its target layout hash": ("m", _m_field_set("VbrAnimationAsset", "NumKeys", "type_hash", 9999)),
    "field type offset not in the layout index": ("m", _m_field_set("VbrAnimationAsset", "NumKeys", "type_off", 3)),
    "two layouts with the same class name": ("m", _m_layout("Int8", "emit_name", "Bool")),
    "Key layout with a second field": ("m", _m_key_second),
    "UInt16 layout size 4": ("m", _m_layout("UInt16", "size", 4)),
    "VBR field NumKeys renamed": ("m", _m_field_set("VbrAnimationAsset", "NumKeys", "emit_name", "NumKeyz")),
    "VBR field NumKeys offset beyond class size": ("m", _m_numkeys_oob),
    "Data field flags 2": ("m", _m_data_flags2),
    "root type word high half non-zero": ("b", _b_root_high),
    "root body offset 16": ("b", _b_body16),
    "two roots with the same key (one not requested)": ("m", _m_set("ClipControllerAsset", 7002, ["__key"], 2003)),
    "recipe clip key absent": ("r", _r_set("asset_key", "5002")),
    "clip key resolves to a LayoutAsset": ("r", _r_set("asset_key", "2001")),
    "AnimationAsset ChannelToDofAsset key 0": ("m", lambda m: _root(m, "VbrAnimationAsset", 5001)["base"]["fields"].__setitem__("ChannelToDofAsset", 0)),
    "__base pointing past section end": ("m", _m_base_ptr(10 ** 6)),
    "ChannelToDofAsset with non-zero __base": ("m", _m_set("ChannelToDofAsset", 4001, ["__base"], 32)),
    "AnimationAsset base with non-zero __base": ("m", lambda m: _root(m, "VbrAnimationAsset", 5001)["base"]["fields"].__setitem__("__base", 32)),
    "JointName without final NUL": ("m", _m_set("SkeletonAsset", 1001, ["Joints", 0, "JointName"], (b"J0x", 3))),
    "JointName with an interior NUL": ("m", _m_set("SkeletonAsset", 1001, ["Joints", 0, "JointName"], (b"J\x000\x00", 4))),
    "Data array count overruns the section": ("m", _m_set("VbrAnimationAsset", 5001, ["Data"], {"cap": 40, "n": 100000, "pos": 48})),
    "DofIds count one less than the channel sum": ("m", _m_set("ChannelToDofAsset", 4001, ["DofIds"], [11, 15, 13])),
    "FrameBlockSizes sum greater than Data count": ("m", _m_set("VbrAnimationAsset", 5001, ["FrameBlockSizes"], [30, 12])),
    "second controller with the same Anim": ("m", _m_set("ClipControllerAsset", 7002, ["Anim"], 5001)),
    "controller name differs from recipe": ("r", _r_set("asset_name", "Other")),
    "joint 1 parent 1": ("m", _m_set("SkeletonAsset", 1001, ["Joints", 1, "ParentIndex"], 1)),
    "duplicate joint name": ("m", _m_set("SkeletonAsset", 1001, ["Joints", 1, "JointName"], "J0")),
    "DofSetIdIndices one shorter than RigDofSets": ("m", _m_set("RigAsset", 3001, ["DofSetIdIndices"], [0, 2])),
    "LayoutAsset start such that start + slots > DofIds count": ("m", _m_set("RigAsset", 3001, ["DofSetIdIndices"], [0, 3, 65535])),
    "same DofId mapped by two slots with different names": ("m", _m_conflict),
    "a DofId twice in DefaultVector3Values": ("m", _m_default_dup),
    "slot J1.q removed": ("m", _m_slot_removed),
    "J0.t slot type 14": ("m", _m_set("LayoutAsset", 2001, ["Slots", 0, "Type"], 14)),
    "default for J1.s removed": ("m", _m_default_missing),
    "input sha256 differs (real-bank mode only)": ("g", None),
    # --- H219 extras (guards without a vector) ---
    "x: input shorter than the 8-byte envelope": ("raw", lambda: b"\x00\x00\x00\x03"),
    "x: skip length beyond the input": ("b", lambda b: patch(b, 4, ">I", len(b))),
    "x: stream tag GD.XXXX": ("b", lambda b: b[:24] + b"GD.XXXX" + b[31:]),
    "x: 8 trailing bytes inside the stream": ("b", _b_strm_extend),
    "x: first child is a DATA section": ("b", lambda b: b[:_refl_at(b)] + b"GD.DATA" + b[_refl_at(b) + 7:]),
    "x: stream without children": ("raw", _empty_stream),
    "x: DATA section budget 2": ("budget", ("MAX_DATA", 2)),
    "x: layout index does not fit": ("m", _refl_patch(_rp_count_huge)),
    "x: layout offset beyond REFL": ("m", _refl_patch(_rp_offset_huge)),
    "x: layout budget 2": ("budget", ("MAX_LAYOUTS", 2)),
    "x: field record budget 1": ("budget", ("MAX_FIELDS", 1)),
    "x: string table not starting with NUL": ("m", _refl_patch(_rp_table_first_byte)),
    "x: class name not UTF-8": ("m", _refl_patch(_rp_class_name_utf8)),
    "x: two layouts with the same class hash": ("m", _m_layout("Int8", "hash", 701)),
    "x: VBR field NumKeys present twice": ("m", _m_numkeys_dup),
    "x: VBR field NumKeys of type Int32": ("m", _m_numkeys_type),
    "x: root class hash unknown": ("b", lambda b: _b_root_type(b, 4242)),
    "x: root of a class without __key": ("b", lambda b: _b_root_type(b, FLOAT_HASH)),
    "x: AnimationAsset DofSetList key 0": ("m", lambda m: _root(m, "VbrAnimationAsset", 5001)["base"]["fields"].__setitem__("DofSetList", 0)),
    "x: clip base of class DeltaTrajLayoutAsset": ("m", _m_base_class),
    "x: clip __base 0": ("m", _m_base_ptr(0)),
    "x: RigAsset with non-zero __base": ("m", _m_set("RigAsset", 3001, ["__base"], 32)),
    "x: SkeletonAsset with non-zero __base": ("m", _m_set("SkeletonAsset", 1001, ["__base"], 32)),
    "x: LayoutAsset with non-zero __base": ("m", _m_set("LayoutAsset", 2001, ["__base"], 32)),
    "x: string budget 2": ("budget", ("MAX_STRING", 2)),
    "x: JointName count beyond the section": ("m", _m_set("SkeletonAsset", 1001, ["Joints", 0, "JointName"], (b"J0\x00", 1000))),
    "x: JointName not UTF-8": ("m", _m_set("SkeletonAsset", 1001, ["Joints", 0, "JointName"], (b"J\xff\x00", 3))),
    "x: array budget 10": ("budget", ("MAX_ARRAY", 10)),
    "x: struct depth budget 0": ("budget", ("MAX_DEPTH", 0)),
    "x: no controller with the clip Anim": ("m", _m_set("ClipControllerAsset", 7001, ["Anim"], 5999)),
    "x: empty joint name": ("m", _m_set("SkeletonAsset", 1001, ["Joints", 1, "JointName"], "")),
    "x: joint budget 1": ("budget", ("MAX_JOINTS", 1)),
    "x: TrajJointIndex 2": ("m", _m_set("SkeletonAsset", 1001, ["TrajJointIndex"], 2)),
    "x: DofId in both default arrays": ("m", _m_default_both),
    "x: rig name differs from the recipe": ("k", {"rig_name": "other_rig"}),
    "x: controller with a non-zero __base": ("m", _m_set("ClipControllerAsset", 7001, ["__base"], 32)),
    "x: controller key differs from the recipe": ("r", _r_set("controller_key", "7002")),
    "x: byte-swapped bank": ("m", _m_endian),
    "x: String layout size 8": ("m", _m_layout("String", "size", 8)),
    "x: Vector3 layout with a fourth field": ("m", lambda m: m.layout("Vector3")["fields"].append(
        dict(name="w", type="Float", flags=0, offset=12))),
}


def build(name):
    """-> (data, recipe, rig_key, kwargs, budget override or None) for one catalogue case."""
    kind, fn = CASES[name]
    m, rec, kw, budget = positive_model(), recipe(), {}, None
    if kind == "m":
        fn(m)
    if kind == "r":
        fn(rec)
    data = m.serialize()
    if kind == "b":
        data = fn(data)
    if kind == "raw":
        data = fn()
    if kind == "g":
        kw["golden"] = {"bytes": len(data), "sha256": "0" * 64}
    if kind == "k":
        kw.update(fn)
    if kind == "budget":
        budget = fn
    return data, rec, RIG_KEY, kw, budget
