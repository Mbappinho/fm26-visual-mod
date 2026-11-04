# SPDX-License-Identifier: MIT
"""SYNTHETIC fixtures for tools/fm26vm vbr.py + retarget.py (B7, H224). No game bytes: small hand-built VBR clip,
a 14-joint T-pose source rig in the gd.read_bank rig shape, a 15-joint render skeleton (arms hanging) in the
meshset skeleton shape, and 2 tiny skinned pieces."""
import math

# ---------------------------------------------------------------- VBR clip (contract layout, hand-built)
VBR_FIELDS = {"QuaternionCount": 1, "Vector3Count": 1, "FloatCount": 1, "ConstQuaternionCount": 1,
              "ConstVector3Count": 0, "ConstFloatCount": 1, "ConstChanMapSize": 2, "VectorOffsetSize": 6,
              "FloatOffsetSize": 4, "NumKeys": 10, "QuatMin": -1.0, "QuatMax": 1.0, "TrajMin": -2.0, "TrajMax": 3.0,
              "Vec3Min": -0.5, "Vec3Max": 0.5, "FloatMin": 0.0, "FloatMax": 4.0, "Dct": 3.0,
              "VectorOffsetScale": 0.25, "FloatOffsetScale": 0.5, "KeyTimeSize": 0, "ConstPaletteSize": 5,
              "Flags": 270}
PALETTE = [0.25, 0.5, 0.75, 0.125, 0.875, 0.375]


def vbr_clip():
    """(fields, Data, FrameBlockSizes, ConstantPalette). Header = palette idx (5) + ConstChanMap (2) + numbits (32)
    + offsets (6 + 4); two blocks of 3 table bytes + 48 payload bytes (deterministic pattern)."""
    pidx = bytes([0, 1, 2, 3, 4])
    ccm = bytes([0, 1])
    numbits = bytes([0x21, 0x12, 0x30, 0x03, 0x11, 0x22, 0x00, 0x13] * 4)
    voff = bytes([1, 0, 2, 5, 1, 253])
    foff = bytes([1, 0, 1, 7])
    blocks = []
    for b in range(2):
        payload = bytes(((i * 37 + 11 + 59 * b) * 13) & 0xFF for i in range(48))
        blocks.append(bytes([b + 1, 2 * b + 3, 5 - b]) + payload)
    data = pidx + ccm + numbits + voff + foff + b"".join(blocks)
    return dict(VBR_FIELDS), data, [len(x) for x in blocks], list(PALETTE)


# ---------------------------------------------------------------- rigs
def qaxis(axis, deg, scale=1.0):
    h = math.radians(deg) / 2
    n = math.sqrt(sum(a * a for a in axis))
    return [scale * a / n * math.sin(h) for a in axis] + [scale * math.cos(h)]


def rmat(q):
    x, y, z, w = q
    return [[1 - 2 * (y * y + z * z), 2 * (x * y - z * w), 2 * (x * z + y * w)],
            [2 * (x * y + z * w), 1 - 2 * (x * x + z * z), 2 * (y * z - x * w)],
            [2 * (x * z - y * w), 2 * (y * z + x * w), 1 - 2 * (x * x + y * y)]]


def m4(q, t):
    R = rmat(q)
    return [R[0] + [t[0]], R[1] + [t[1]], R[2] + [t[2]], [0.0, 0.0, 0.0, 1.0]]


def mul4(A, B):
    return [[sum(A[r][k] * B[k][c] for k in range(4)) for c in range(4)] for r in range(4)]


def rigid_inverse(M):
    R = [[M[c][r] for c in range(3)] for r in range(3)]
    t = [-sum(R[r][k] * M[k][3] for k in range(3)) for r in range(3)]
    return [R[0] + [t[0]], R[1] + [t[1]], R[2] + [t[2]], [0.0, 0.0, 0.0, 1.0]]


# name, parent, t (source rest, feet-like units, T-pose arms along +-x)
SRC = [("Reference", -1, [0.0, 0.0, 0.0]), ("Hips", 0, [0.0, 3.2, 0.1]), ("Spine", 1, [0.0, 0.6, 0.0]),
       ("LeftShoulder", 2, [0.3, 1.0, 0.0]), ("LeftArm", 3, [0.5, 0.0, 0.0]), ("LeftForeArm", 4, [0.9, 0.0, 0.0]),
       ("LeftHand", 5, [0.8, 0.0, 0.0]), ("LeftInHandMiddle", 6, [0.3, 0.0, 0.05]),
       ("RightShoulder", 2, [-0.3, 1.0, 0.0]), ("RightArm", 8, [-0.5, 0.0, 0.0]),
       ("RightForeArm", 9, [-0.9, 0.0, 0.0]), ("RightHand", 10, [-0.8, 0.0, 0.0]),
       ("RightInHandMiddle", 11, [-0.3, 0.0, 0.05]), ("SourceOnly", 1, [0.0, -0.2, 0.3])]


def source_rig():
    """gd.read_bank(...)['rig'] shape. Raw default q slightly off unit (norm 0.9995..1.0005)."""
    joints = []
    for i, (n, p, t) in enumerate(SRC):
        q = qaxis([0.2 + 0.1 * i, 1.0, 0.3 - 0.05 * i], 4.0 + 3.0 * i, 1.0 + (-1) ** i * 0.0005)
        joints.append({"name": n, "parent": p, "type": 0, "t": list(t), "q": q,
                       "s": [1.0, 1.0 + 0.001 * (i % 3), 1.0], "t_dof": 1000 + 3 * i, "q_dof": 1001 + 3 * i,
                       "s_dof": 1002 + 3 * i})
    return {"rig_key": "1", "skeleton_key": "2", "joints": joints, "slots": {}, "defaults": {}}


def target_skeleton():
    """meshset skeleton shape: metres, arms hanging ~40 deg, an extra helper joint, rotated Hips basis."""
    names = [n for n, _, _ in SRC if n != "SourceOnly"] + ["TargetHelper"]
    src = {n: (p, t) for n, p, t in SRC}
    parents, local = [], []
    for i, n in enumerate(names):
        if n == "TargetHelper":
            parents.append(2)
            local.append(m4(qaxis([1, 0, 0], 10.0), [0.0, 0.1, 0.12]))
            continue
        p, t = src[n]
        parents.append(p)
        t = [c * 0.3048 for c in t]
        if n == "Hips":
            q = qaxis([0, 1, 0], 90.0)
        elif n in ("LeftArm", "RightArm"):
            q = qaxis([0, 0, 1], -40.0 if n == "LeftArm" else 40.0)
        elif n in ("LeftForeArm", "RightForeArm"):
            q = qaxis([0, 1, 0], 12.0)
        else:
            q = qaxis([1, 0.5, 0], 2.0 + i)
        local.append(m4(q, t))
    model = []
    for j, p in enumerate(parents):
        model.append(local[j] if p < 0 else mul4(model[p], local[j]))
    return {"joint_count": len(names), "names": names, "parents": parents, "local": local, "model": model,
            "inverse": [rigid_inverse(M) for M in model]}


def pieces():
    """2 pieces (meshset piece shape subset): positions, triangles, 4 raw lanes indices/weights (sum 255)."""
    out = []
    for k in range(2):
        pos, idx, wt = [], [], []
        for v in range(6):
            pos.append([0.1 * v - 0.2 + k, 0.9 + 0.13 * v, 0.05 * ((v + k) % 3)])
            b0 = (3 * v + 5 * k) % 14
            idx.append([b0, (b0 + 1) % 14, (b0 + 4) % 14, 0])
            wt.append([200 - 20 * v, 40 + 10 * v, 15 + 10 * v, 0])
        out.append({"positions": pos, "triangles": [[0, 1, 2], [3, 4, 5]], "bone_indices_raw": idx,
                    "bone_weights_raw": wt})
    return out


def decoded_clip(rig, nkeys=5):
    """vbr.decode-shaped channels: Q channels for Hips, LeftArm, RightForeArm, SourceOnly; one V and one F channel;
    returns (decoded, DofIds)."""
    byname = {j["name"]: j for j in rig["joints"]}
    chans, dofs = [], []
    for c, n in enumerate(("Hips", "LeftArm", "RightForeArm", "SourceOnly")):
        vals = [qaxis([0.3, 1.0 - 0.2 * c, 0.1 * c], 15.0 * k + 5.0 * c, 1.0 + 0.0003 * k) for k in range(nkeys)]
        chans.append({"index": c, "kind": "Q", "source": "var", "ordinal": c, "values": vals})
        dofs.append(byname[n]["q_dof"])
    chans.append({"index": 4, "kind": "V", "source": "var", "ordinal": 0,
                  "values": [[0.1 * k, 0.0, 0.2] for k in range(nkeys)]})
    dofs.append(byname["Hips"]["t_dof"])
    chans.append({"index": 5, "kind": "F", "source": "var", "ordinal": 0, "values": [[0.5 * k] for k in range(nkeys)]})
    dofs.append(77)
    return {"channels": chans, "frame_index": list(range(nkeys))}, dofs
