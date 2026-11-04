# SPDX-License-Identifier: MIT
"""Retarget + skin the recipe clips onto the render skeleton (roadmap B7).

Project math (our own authorship) moved from the sealed H135/H137/H141/H149/H152/H170 scripts; no file under
local/ is read at runtime. Conventions: column vectors, 4x4 row-major nested lists whose columns are
right/up/forward/trans, W = parentW * local; raw quaternions x, y, z, w with the literal 1-2(...) polynomial, never
normalised. Variant = H137 rest-aligned arms (world delta with C from the Hips rest bases, shortest-arc Q on 8 arm
joints), rotations only, in place. Skinning = 4 raw lanes, weight/255, supplied InverseModelPose, no renormalisation.

Inputs come from the clean-room readers: gd.read_bank (rig joints/defaults, clip VBR fields), meshset.read_meshes
(render skeleton + 6 pieces) and vbr.decode (channels). Frame file layout (frames-<source_id>-f64le.bin): frame 0 =
rest skin, then the recipe frame_range a..b inclusive; float64 little-endian, vertices in piece order.
"""
import hashlib
import math
import struct

ARM_BONES = {"LeftShoulder": "LeftArm", "LeftArm": "LeftForeArm", "LeftForeArm": "LeftHand",
             "LeftHand": "LeftInHandMiddle", "RightShoulder": "RightArm", "RightArm": "RightForeArm",
             "RightForeArm": "RightHand", "RightHand": "RightInHandMiddle"}
NORMALISATION = "F2"
NBONES = 438


class RetargetError(ValueError):
    pass


# ---------------------------------------------------------------- linear algebra (H135)
def mul3(A, B):
    return [[A[r][0] * B[0][c] + A[r][1] * B[1][c] + A[r][2] * B[2][c] for c in range(3)] for r in range(3)]


def mul3v(A, v):
    return [A[r][0] * v[0] + A[r][1] * v[1] + A[r][2] * v[2] for r in range(3)]


def mul4(A, B):
    return [[A[r][0] * B[0][c] + A[r][1] * B[1][c] + A[r][2] * B[2][c] + A[r][3] * B[3][c] for c in range(4)]
            for r in range(4)]


def lin(M):
    return [M[0][:3], M[1][:3], M[2][:3]]


def compose(L, t):
    return [L[0] + [t[0]], L[1] + [t[1]], L[2] + [t[2]], [0.0, 0.0, 0.0, 1.0]]


def det3(M):
    return (M[0][0] * (M[1][1] * M[2][2] - M[1][2] * M[2][1]) - M[0][1] * (M[1][0] * M[2][2] - M[1][2] * M[2][0])
            + M[0][2] * (M[1][0] * M[2][1] - M[1][1] * M[2][0]))


def inv3(M):
    """Explicit adjugate / determinant inverse of the raw 3x3 (no orthonormalisation). Singular/non-finite -> stop."""
    d = det3(M)
    if not math.isfinite(d) or abs(d) < 1e-12:
        raise RetargetError("singular or non-finite 3x3 (det=%r)" % d)
    a, b, c = M[0]
    e, f, g = M[1]
    h, i, j = M[2]
    return [[(f * j - g * i) / d, (c * i - b * j) / d, (b * g - c * f) / d],
            [(g * h - e * j) / d, (a * j - c * h) / d, (c * e - a * g) / d],
            [(e * i - f * h) / d, (b * h - a * i) / d, (a * f - b * e) / d]]


def sub(a, b):
    return [a[0] - b[0], a[1] - b[1], a[2] - b[2]]


def norm(v):
    return math.sqrt(v[0] * v[0] + v[1] * v[1] + v[2] * v[2])


def quat_R(q):
    x, y, z, w = q
    return [[1 - 2 * (y * y + z * z), 2 * (x * y - z * w), 2 * (x * z + y * w)],
            [2 * (x * y + z * w), 1 - 2 * (x * x + z * z), 2 * (y * z - x * w)],
            [2 * (x * z - y * w), 2 * (y * z + x * w), 1 - 2 * (x * x + y * y)]]


# ---------------------------------------------------------------- poses (H135 / H137)
def source_local(t, q, s):
    """T * R(q) * diag(s)."""
    R = quat_R(q)
    return compose([[R[r][c] * s[c] for c in range(3)] for r in range(3)], t)


def fk(parents, locals_):
    W = []
    for j, p in enumerate(parents):
        W.append(locals_[j] if p < 0 else mul4(W[p], locals_[j]))
    return W


def D_src(Wk, Wrest):
    return mul3(lin(Wk), inv3(lin(Wrest)))


def D_src_aligned(Wk, Wrest, Q):
    """W_lin_src(j,k) * inverse3x3(Q * W_lin_src_rest(j))."""
    return mul3(lin(Wk), inv3(mul3(Q, lin(Wrest))))


def world_delta_locals(parents, rest_locals, rest_worlds, matched, D, C):
    """matched: target index -> source index; D: source index -> 3x3. Returns new target locals."""
    Ci = inv3(C)
    out, W = [], []
    for j, p in enumerate(parents):
        L = rest_locals[j]
        if j in matched:
            A = mul3(mul3(C, D[matched[j]]), Ci)
            if p < 0:
                L = compose(mul3(A, lin(rest_locals[j])), [rest_locals[j][r][3] for r in range(3)])
            else:
                Wj = mul3(A, lin(rest_worlds[j]))
                L = compose(mul3(inv3(lin(W[p])), Wj), [rest_locals[j][r][3] for r in range(3)])
        out.append(L)
        W.append(L if p < 0 else mul4(W[p], L))
    return out


def lbs(worlds, inverse, positions, indices, weights):
    used = sorted({b for row in indices for b in row})
    M = {b: mul4(worlds[b], inverse[b]) for b in used}
    out = []
    for p, idx, wt in zip(positions, indices, weights):
        acc = [0.0, 0.0, 0.0]
        for b, w in zip(idx, wt):
            m = M[b]
            f = w / 255
            for r in range(3):
                acc[r] += f * (m[r][0] * p[0] + m[r][1] * p[1] + m[r][2] * p[2] + m[r][3])
        out.append(acc)
    return out


def shortest_arc(s, t):
    """Rodrigues rotation taking normalize(s) to normalize(t): axis normalize(s x t), angle atan2(|s x t|, s.t).
    Stops on |s|,|t| < 1e-9 or angle > 179.9 deg."""
    ns, nt = norm(s), norm(t)
    if not (math.isfinite(ns) and math.isfinite(nt)) or ns < 1e-9 or nt < 1e-9:
        raise RetargetError("degenerate s or t (%r, %r)" % (ns, nt))
    s = [c / ns for c in s]
    t = [c / nt for c in t]
    cr = [s[1] * t[2] - s[2] * t[1], s[2] * t[0] - s[0] * t[2], s[0] * t[1] - s[1] * t[0]]
    sn = norm(cr)
    dot = s[0] * t[0] + s[1] * t[1] + s[2] * t[2]
    ang = math.atan2(sn, dot)
    if math.degrees(ang) > 179.9:
        raise RetargetError("s,t antiparallel (%r deg)" % math.degrees(ang))
    if sn == 0.0:
        return [[1.0, 0.0, 0.0], [0.0, 1.0, 0.0], [0.0, 0.0, 1.0]]
    k = [c / sn for c in cr]
    K = [[0.0, -k[2], k[1]], [k[2], 0.0, -k[0]], [-k[1], k[0], 0.0]]
    K2 = mul3(K, K)
    sa, ca = math.sin(ang), math.cos(ang)
    return [[(1.0 if r == c else 0.0) + sa * K[r][c] + (1 - ca) * K2[r][c] for c in range(3)] for r in range(3)]


# ---------------------------------------------------------------- context from the reader outputs
def source_worlds(I, qs):
    return fk(I["src_parents"], [source_local(I["src_t"][j], qs[j], I["src_s"][j]) for j in range(len(qs))])


def alignment_C(I):
    h_s, h_t = I["src_index"]["Hips"], I["tgt_index"]["Hips"]
    return mul3(lin(I["tgt_rest_W"][h_t]), inv3(lin(I["src_rest_W"][h_s])))


def rest_corrections(I, C):
    """Per arm joint j (designated child c): s = p_src_rest(c)-p_src_rest(j); t = inv3(C)*(p_tgt_rest(c)-p_tgt_rest(j));
    Q = shortest_arc(s, t). Child must be a direct child of j in both rigs. Returns {source index: Q}."""
    Ci = inv3(C)
    Ws, Wt = I["src_rest_W"], I["tgt_rest_W"]
    out = {}
    for j, c in ARM_BONES.items():
        if j not in I["src_index"] or c not in I["src_index"] or j not in I["tgt_index"] or c not in I["tgt_index"]:
            raise RetargetError("missing joint/child %s->%s" % (j, c))
        sj, sc, tj, tc = I["src_index"][j], I["src_index"][c], I["tgt_index"][j], I["tgt_index"][c]
        if I["src_parents"][sc] != sj or I["tgt_parents"][tc] != tj:
            raise RetargetError("child not direct %s->%s" % (j, c))
        s = sub([Ws[sc][r][3] for r in range(3)], [Ws[sj][r][3] for r in range(3)])
        t = mul3v(Ci, sub([Wt[tc][r][3] for r in range(3)], [Wt[tj][r][3] for r in range(3)]))
        out[sj] = shortest_arc(s, t)
    return out


def target_worlds(I, qs):
    """H137 target worlds: world delta with D_src_aligned on the arm joints."""
    Ws = source_worlds(I, qs)
    Qs = I["Q"]
    D = {si: (D_src_aligned(Ws[si], I["src_rest_W"][si], Qs[si]) if si in Qs else D_src(Ws[si], I["src_rest_W"][si]))
         for n, si, ti in I["matched"]}
    L = world_delta_locals(I["tgt_parents"], I["tgt_local"], I["tgt_rest_W"], I["t2s"], D, I["C"])
    return fk(I["tgt_parents"], L)


def context(rig, skeleton):
    """rig: gd.read_bank(...)['rig']; skeleton: meshset.read_meshes(...)['skeleton']. Joins by exact name."""
    src_names = [j["name"] for j in rig["joints"]]
    src_parents = [j["parent"] for j in rig["joints"]]
    tgt_names = list(skeleton["names"])
    tidx = {n: i for i, n in enumerate(tgt_names)}
    sidx = {n: i for i, n in enumerate(src_names)}
    if len(tidx) != len(tgt_names) or len(sidx) != len(src_names):
        raise RetargetError("duplicate joint names")
    matched = [(n, sidx[n], tidx[n]) for n in src_names if n in tidx]
    for n, si, ti in matched:
        sp, tp = src_parents[si], skeleton["parents"][ti]
        if (sp < 0) != (tp < 0) or (sp >= 0 and src_names[sp] != tgt_names[tp]):
            raise RetargetError("parent mismatch " + n)
    I = {"src_names": src_names, "src_parents": src_parents,
         "src_t": [list(j["t"]) for j in rig["joints"]], "src_s": [list(j["s"]) for j in rig["joints"]],
         "src_default_q": [list(j["q"]) for j in rig["joints"]],
         "tgt_names": tgt_names, "tgt_parents": list(skeleton["parents"]), "tgt_local": skeleton["local"],
         "inverse": skeleton["inverse"], "tgt_index": tidx, "src_index": sidx, "matched": matched,
         "t2s": {ti: si for n, si, ti in matched}}
    I["tgt_rest_W"] = fk(I["tgt_parents"], I["tgt_local"])
    I["src_rest_W"] = source_worlds(I, I["src_default_q"])
    I["C"] = alignment_C(I)
    I["Q"] = rest_corrections(I, I["C"])
    return I


# ---------------------------------------------------------------- clip channels -> per-key source rotations
def clip_rotations(rig, decoded, dof_ids):
    """Per key, the 82 local rotations: the clip channel bound to the joint's q DOF id, else the rig default."""
    chans = decoded["channels"]
    if len(dof_ids) != len(chans):
        raise RetargetError("DofIds %d != decoded channels %d" % (len(dof_ids), len(chans)))
    bydof = {dof: c for c, dof in zip(chans, dof_ids)}
    n = len(decoded["frame_index"])
    out = []
    for t in range(n):
        out.append([list(bydof[j["q_dof"]]["values"][t]) if j["q_dof"] in bydof else list(j["q"])
                    for j in rig["joints"]])
    return out


# ---------------------------------------------------------------- skinning and frame files
def skin_pieces(W, inverse, pieces):
    out = []
    for p in pieces:
        out.extend(lbs(W, inverse, p["positions"], p["bone_indices_raw"], p["bone_weights_raw"]))
    return out


def pack_f64(frames):
    flat = [c for V in frames for p in V for c in p]
    return struct.pack("<%dd" % len(flat), *flat)


def clip_frames(I, pieces, qs_per_key, frame_range):
    a, b = frame_range
    if not (0 <= a <= b < len(qs_per_key)):
        raise RetargetError("frame_range %r outside %d keys" % (frame_range, len(qs_per_key)))
    frames = [skin_pieces(I["tgt_rest_W"], I["inverse"], pieces)]
    for qs in qs_per_key[a:b + 1]:
        frames.append(skin_pieces(target_worlds(I, qs), I["inverse"], pieces))
    return pack_f64(frames)


def build_frames(recipe, bank, meshes, decode=None):
    """recipe: loaded recipe; bank: gd.read_bank output; meshes: meshset.read_meshes output. Returns {file name: bytes}
    in recipe clip order. Every file must equal its recipe golden.frames_f64le entry (bytes + sha256)."""
    if decode is None:
        from .vbr import decode
    I = context(bank["rig"], meshes["skeleton"])
    golden = recipe["golden"]["frames_f64le"]
    out = {}
    for rc, c in zip(recipe["clips"], bank["clips"]):
        if str(c["asset_key"]) != str(rc["asset_key"]):
            raise RetargetError("bank clip %s does not match recipe clip %s" % (c["asset_key"], rc["asset_key"]))
        name = "frames-%s-f64le.bin" % rc["source_id"]
        if name not in golden:
            raise RetargetError("recipe golden.frames_f64le has no entry for %s" % name)
        d = decode(c["fields"], c["Data"], c["FrameBlockSizes"], c["ConstantPalette"], norm=NORMALISATION)
        raw = clip_frames(I, meshes["pieces"], clip_rotations(bank["rig"], d, c["DofIds"]), rc["frame_range"])
        digest = hashlib.sha256(raw).hexdigest()
        if len(raw) != golden[name]["bytes"] or digest != golden[name]["sha256"]:
            raise RetargetError("%s: %d bytes sha256 %s differs from the recipe golden" % (name, len(raw), digest))
        out[name] = raw
    return out
