# SPDX-License-Identifier: MIT
"""fm26vm B6: clean-room EBX skeleton + MeshSet reader tools/fm26vm/meshset.py (CONTRACT-EBX-MESH + erratum E1).

Synthetic payloads come from tests/fixtures/fm26vm/em_synth.py (builders of clean-room implementation B, H220/H223).
A crash is never a refusal: `refused` and `ok` turn any unexpected exception into a test failure. The real-data test
runs only when FM26VM_SPANS points at an `extract-spans` folder (no game bytes are stored in the repository).
"""
import hashlib
import importlib.util
import io
import json
import os
import struct
import sys
import tempfile
import unittest
from pathlib import Path
from unittest import mock

ROOT = Path(__file__).resolve().parents[1]
if str(ROOT) not in sys.path:
    sys.path.insert(0, str(ROOT))

from tools.fm26vm import cli, meshset, recipe as recipe_mod  # noqa: E402

_spec = importlib.util.spec_from_file_location("fm26vm_em_synth", ROOT / "tests" / "fixtures" / "fm26vm" / "em_synth.py")
S = importlib.util.module_from_spec(_spec)
_spec.loader.exec_module(S)

J = 3
RID = 0x8123456789ABCDEF
RID_SIGNED = str(RID - (1 << 64))


def patch(buf, off, fmt, *vals):
    b = bytearray(buf)
    struct.pack_into("<" + fmt, b, off, *vals)
    return bytes(b)


def u32(buf, off):
    return struct.unpack_from("<I", buf, off)[0]


def tables(buf):
    """Positions of the metadata tables of a synthetic EBX, from its own header."""
    imports, tlen = u32(buf, 12), struct.unpack_from("<H", buf, 26)[0]
    nfields, nclasses = struct.unpack_from("<H", buf, 24)[0], struct.unpack_from("<H", buf, 22)[0]
    fields = 64 + 32 * imports + tlen
    return {"fields": fields, "classes": fields + 16 * nfields, "groups": fields + 16 * nfields + 16 * nclasses,
            "so": u32(buf, 4), "slen": u32(buf, 28), "D": u32(buf, 4) + u32(buf, 28)}


def mats(k):
    return [m + [[0.0, 0.0, 0.0, 1.0]] for m in S.sk_mats(k)]


def piece(names_rid=RID_SIGNED, **kw):
    r, m, _ = S.build_res(**kw)
    e, _ = S.build_mesh_ebx(RID)
    return {"ebx": e, "res": r, "res_meta": m, "rid": names_rid, "asset": S.RES_PATH}


def synth_recipe(skel_bytes, pieces):
    """A schema-valid recipe whose spans describe synthetic payloads (keeps the shipped top-level fields)."""
    rec = json.loads((ROOT / "tools" / "fm26vm" / "recipe-fifa17-v2.json").read_text(encoding="utf-8"))
    rec["golden"]["meshset_reader_sha256"] = "0" * 64  # H226b: required; a passing test pins its own output
    spans, data = [], {}

    def add(sid, role, asset, payload, **extra):
        spans.append(dict({"id": sid, "role": role, "asset": asset, "path": "Data/x.cas", "offset": 0, "size": 1,
                           "sha1": "0" * 40, "decoded_size": len(payload),
                           "decoded_sha256": hashlib.sha256(payload).hexdigest()}, **extra))
        data[sid] = payload

    for k, p in enumerate(pieces, 1):
        asset = p["asset"]
        add(f"m{k}-ebx", "mesh-ebx", asset, p["ebx"])
        add(f"m{k}-res", "mesh-res", asset, p["res"], res_meta_hex=p["res_meta"].hex(), rid=str(p["rid"]))
    add("skel", "skeleton-ebx", "skel/path", skel_bytes)
    rec["spans"] = spans
    return rec, data


class Case(unittest.TestCase):
    def refused(self, fn, rule):
        try:
            fn()
        except meshset.MeshError as e:
            self.assertEqual(e.rule, rule, str(e))
            return
        except Exception as e:  # noqa: BLE001  a crash is not a refusal
            self.fail(f"not a MeshError: {e!r}")
        self.fail(f"not refused (expected {rule})")

    def ok(self, fn):
        try:
            result = fn()
        except Exception as e:  # noqa: BLE001
            self.fail(f"refused or crashed: {e!r}")
        self.assertIsNotNone(result, "no result")
        return result

    def sk(self, data, joints=J):
        return meshset.read_skeleton(data, joints)

    def rr(self, r, m, path=S.RES_PATH, joints=J):
        return meshset.read_res(r, m, path, joints)


class T_s01_skeleton_read(Case):
    def test_it(self):
        b, _ = S.build_skeleton()
        out = self.ok(lambda: self.sk(b))
        self.assertEqual(out["joint_count"], 3)
        self.assertEqual(out["names"], S.SK_NAMES)
        self.assertEqual(out["name_hashes"], S.SK_HASHES)
        self.assertEqual(out["parents"], S.SK_PARENTS)
        self.assertEqual((out["local"], out["model"], out["inverse"]), (mats(1), mats(2), mats(3)))


class T_s02_malformed(Case):
    def test_it(self):
        b, _ = S.build_skeleton()
        self.refused(lambda: self.sk(b[:30]), "MALFORMED")


class T_s03_hdr_magic(Case):
    def test_it(self):
        b, _ = S.build_skeleton()
        self.refused(lambda: self.sk(patch(b, 0, "I", 0x0FB2D1CF)), "E-HDR-1")


class T_s04_hdr_total(Case):
    def test_it(self):
        b, _ = S.build_skeleton()
        self.refused(lambda: self.sk(patch(b, 8, "I", u32(b, 8) + 4)), "E-HDR-2")


class T_s05_hdr_regions(Case):
    def test_it(self):
        b, _ = S.build_skeleton()
        self.refused(lambda: self.sk(patch(b, 36, "I", u32(b, 8) + 16)), "E-HDR-2")


class T_s06_meta_names(Case):
    def test_it(self):
        b, _ = S.build_skeleton()
        self.refused(lambda: self.sk(patch(b, 26, "H", struct.unpack_from("<H", b, 26)[0] - 1)), "E-META-1")


class T_s07_meta_field(Case):
    def test_it(self):
        b, _ = S.build_skeleton()
        self.refused(lambda: self.sk(patch(b, tables(b)["fields"], "I", 0x12345678)), "E-META-2")


class T_s08_meta_class_name(Case):
    def test_it(self):
        b, _ = S.build_skeleton()
        self.refused(lambda: self.sk(patch(b, tables(b)["classes"], "I", 0x12345678)), "E-META-3")


class T_s09_meta_class_first(Case):
    def test_it(self):
        b, _ = S.build_skeleton()
        self.refused(lambda: self.sk(patch(b, tables(b)["classes"] + 4, "i", -1)), "E-META-3")


class T_s10_meta_class_range(Case):
    def test_it(self):
        b, _ = S.build_skeleton()
        self.refused(lambda: self.sk(patch(b, tables(b)["classes"] + 8, "B", 200)), "E-META-3")


class T_s11_meta_class_align(Case):
    def test_it(self):
        b, _ = S.build_skeleton()
        self.refused(lambda: self.sk(patch(b, tables(b)["classes"] + 9, "B", 3)), "E-META-3")


class T_s12_meta_group(Case):
    def test_it(self):
        b, _ = S.build_skeleton()
        self.refused(lambda: self.sk(patch(b, tables(b)["groups"], "H", 50)), "E-META-4")


class T_s13_meta_end(Case):
    def test_it(self):
        b, _ = S.build_skeleton()
        self.refused(lambda: self.sk(patch(b, 32, "I", 1000)), "E-META-5")


class T_s14_inst_exported(Case):
    def test_it(self):
        b, _ = S.build_skeleton()
        self.refused(lambda: self.sk(patch(b, 18, "H", 0)), "E-INST-1")


class T_s15_inst_aligned(Case):
    def test_it(self):
        b, _ = S.build_skeleton(root_align=16)
        self.refused(lambda: self.sk(patch(b, 28, "I", u32(b, 28) - 4)), "E-INST-2")


class T_s16_inst_base(Case):
    def test_it(self):
        b, _ = S.build_skeleton(root_align=8)
        self.refused(lambda: self.sk(b), "E-INST-3")


class T_s17_inst_bounds(Case):
    def test_it(self):
        b, _ = S.build_skeleton()
        self.refused(lambda: self.sk(patch(b, tables(b)["fields"] + 2 * 16 + 8, "I", 10000)), "E-INST-4")


class T_s18_inst_depth(Case):
    def test_it(self):
        b, _ = S.build_skeleton()
        self.refused(lambda: self.sk(patch(b, tables(b)["fields"] + 16 + 6, "H", 1)), "E-INST-5")


class T_s19_inst_once(Case):
    def test_it(self):
        b, _ = S.build_skeleton()
        self.refused(lambda: self.sk(patch(b, tables(b)["fields"], "I", S.name_hash("BoneNames"))), "E-INST-6")


class T_s19b_inst_missing(Case):
    def test_it(self):
        b, _ = S.build_skeleton()
        self.refused(lambda: self.sk(patch(b, tables(b)["fields"] + 2 * 16, "I", S.name_hash("member"))), "E-INST-6")


class T_s20_arr_index(Case):
    def test_it(self):
        b, _ = S.build_skeleton()
        self.refused(lambda: self.sk(patch(b, tables(b)["D"] + 20, "I", 99)), "E-ARR-1")


class T_s21_arr_align(Case):
    def test_it(self):
        b, info = S.build_skeleton()
        at = info["desc_pos"] + 12 * 4
        self.refused(lambda: self.sk(patch(b, at, "I", u32(b, at) + 4)), "E-ARR-2")


class T_s22_arr_span(Case):
    def test_it(self):
        b, info = S.build_skeleton()
        self.refused(lambda: self.sk(patch(b, info["desc_pos"] + 12 * 4 + 4, "I", 100000)), "E-ARR-2")


class T_s23_arr_prefix(Case):
    def test_it(self):
        b, _ = S.build_skeleton(local_prefix=7)
        self.refused(lambda: self.sk(b), "E-ARR-2")


class T_s24_str_offset(Case):
    def test_it(self):
        b, _ = S.build_skeleton(name_offsets=[0, 5, 999])
        self.refused(lambda: self.sk(b), "E-ARR-3")


class T_s25_str_nul(Case):
    def test_it(self):
        b, _ = S.build_skeleton()
        t = tables(b)
        path_off = b.index(b"skel/path", t["so"]) - t["so"]
        bad = bytearray(S.build_skeleton(name_offsets=[0, 5, path_off])[0])
        bad[t["so"] + path_off:t["so"] + t["slen"]] = b"x" * (t["slen"] - path_off)
        self.refused(lambda: self.sk(bytes(bad)), "E-ARR-3")


class T_s26_str_empty(Case):
    def test_it(self):
        b, _ = S.build_skeleton(name_offsets=[0, 5, 0xFFFFFFFF])
        self.assertEqual(self.ok(lambda: self.sk(b))["names"], ["Root", "Spine", ""])


class T_s27_sk_name(Case):
    def test_it(self):
        b, _ = S.build_skeleton(root_name="OtherAsset")
        self.refused(lambda: self.sk(b), "SK-1")


class T_s28_sk_align(Case):
    def test_it(self):
        b, _ = S.build_skeleton(root_align=16)
        self.refused(lambda: self.sk(b), "SK-1")


class T_s29_sk_size(Case):
    def test_it(self):
        b, _ = S.build_skeleton(root_size=60)
        self.refused(lambda: self.sk(b), "SK-1")


class T_s30_sk_equal(Case):
    def test_it(self):
        b, info = S.build_skeleton()
        at = info["desc_pos"] + 12 * 3
        b = patch(b, at + 4, "I", 2)
        b = patch(b, info["B"] + u32(b, at) - 4, "I", 2)
        self.refused(lambda: self.sk(b), "SK-2")


class T_s31_sk_joints(Case):
    def test_it(self):
        b, _ = S.build_skeleton()
        self.refused(lambda: self.sk(b, joints=4), "SK-2")


class T_s32_sk_overlap(Case):
    def test_it(self):
        b, _ = S.build_skeleton(model_index=4)
        self.refused(lambda: self.sk(b), "SK-3")


class T_s33_sk_lt_name(Case):
    def test_it(self):
        b, _ = S.build_skeleton(lt_name="OtherTransform")
        self.refused(lambda: self.sk(b), "SK-4")


class T_s34_sk_lt_size(Case):
    def test_it(self):
        b, _ = S.build_skeleton(lt_size=48)
        self.refused(lambda: self.sk(b), "SK-4")


class T_s35_sk_finite(Case):
    def test_it(self):
        m = [S.sk_mats(1), S.sk_mats(2), S.sk_mats(3)]
        m[0][1][2][3] = float("inf")
        b, _ = S.build_skeleton(mats=m)
        self.refused(lambda: self.sk(b), "SK-5")


class T_s36_sk_parent_order(Case):
    def test_it(self):
        b, _ = S.build_skeleton(parents=[-1, 2, 0])
        self.refused(lambda: self.sk(b), "SK-6")


class T_s37_sk_parent_neg(Case):
    def test_it(self):
        b, _ = S.build_skeleton(parents=[-1, -2, 0])
        self.refused(lambda: self.sk(b), "SK-6")


class T_s38_sk_distinct(Case):
    def test_it(self):
        b, _ = S.build_skeleton(names=["Root", "Spine", "Root"])
        self.refused(lambda: self.sk(b), "SK-7")


class T_m01_link_read(Case):
    def test_it(self):
        b, info = S.build_mesh_ebx(RID)
        out = self.ok(lambda: meshset.read_mesh_link(b, RID_SIGNED))
        self.assertEqual(out, {"rid_u64": RID, "absolute_offset": info["D"] + 32})


class T_m02_me_name(Case):
    def test_it(self):
        b, _ = S.build_mesh_ebx(RID, root_name="StaticMeshAsset")
        self.refused(lambda: meshset.read_mesh_link(b, RID_SIGNED), "ME-1")


class T_m03_me_align(Case):
    def test_it(self):
        b, _ = S.build_mesh_ebx(RID, root_align=4)
        self.refused(lambda: meshset.read_mesh_link(b, RID_SIGNED), "ME-1")


class T_m04_me_code(Case):
    def test_it(self):
        b, _ = S.build_mesh_ebx(RID, msr_code=15)
        self.refused(lambda: meshset.read_mesh_link(b, RID_SIGNED), "ME-2")


class T_m05_me_rid(Case):
    def test_it(self):
        b, _ = S.build_mesh_ebx(RID)
        self.refused(lambda: meshset.read_mesh_link(b, str(RID - (1 << 64) + 1)), "ME-3")


class T_r01_res_read(Case):
    def test_it(self):
        r, m, _ = S.build_res()
        p = self.ok(lambda: self.rr(r, m))
        self.assertEqual((p["mesh"], p["short_name"], p["material_name"]), (S.RES_PATH, "piece_mesh", "test_mat"))
        self.assertEqual((p["vertex_count"], p["triangle_count"]), (3, 1))
        self.assertEqual(p["positions"], [list(v) for v in S.RES_POS])
        self.assertEqual((p["triangles"], p["indices"]), ([[0, 1, 2]], [0, 1, 2]))
        self.assertEqual(p["bone_indices_raw"], [list(v) for v in S.RES_BONES])
        self.assertEqual(p["bone_weights_raw"], [list(v) for v in S.RES_WEIGHTS])
        self.assertEqual((p["section_palette"], p["lod_palette"], p["bones_used"]), ([0, 1, 2], [0, 1, 2], [0, 1]))
        self.assertEqual((p["index_policy"], p["index_posthoc_pad_bytes"]), ("U16LE", 0))
        self.assertEqual((p["vertex_buffer_slack_bytes"], p["inline_padding"], p["stride"], p["bones_per_vertex_raw"]),
                         (0, 10, 32, 4))
        self.assertEqual(p["res_meta_u32"], list(struct.unpack("<4I", m)))


class T_r02_res_u32(Case):
    def test_it(self):
        r, m, _ = S.build_res(index_width=4)
        p = self.ok(lambda: self.rr(r, m))
        self.assertEqual((p["index_policy"], p["triangles"]), ("U32LE", [[0, 1, 2]]))


class T_r03_res_posthoc(Case):
    def test_it(self):
        r, m, _ = S.build_res(trailing=b"\0\0")
        p = self.ok(lambda: self.rr(r, m))
        self.assertEqual((p["index_policy"], p["index_posthoc_pad_bytes"], p["triangles"]),
                         ("POSTHOC_U16LE_ZERO_PAD_TO_4", 2, [[0, 1, 2]]))


class T_r04_rm_start(Case):
    def test_it(self):
        r, m, _ = S.build_res()
        self.refused(lambda: self.rr(r, patch(m, 0, "I", 0)), "RM-1")


class T_r05_rm_sum(Case):
    def test_it(self):
        r, m, _ = S.build_res()
        self.refused(lambda: self.rr(r, patch(m, 8, "I", u32(m, 8) - 4)), "RM-1")


class T_r06_rm_bounds(Case):
    def test_it(self):
        r, m, info = S.build_res()
        self.refused(lambda: self.rr(patch(r, 88, "q", info["inline_start"] + 5), m), "RM-2")


class T_r07_rs1_bbox(Case):
    def test_it(self):
        r, m, _ = S.build_res(bbox=((-1.0, float("nan"), -1.0), (1.0, 1.0, 1.0)))
        self.refused(lambda: self.rr(r, m), "RS-1")


class T_r08_rs2_lodptrs(Case):
    def test_it(self):
        r, m, _ = S.build_res()
        self.refused(lambda: self.rr(patch(r, 40, "q", 5), m), "RS-2")


class T_r09_rs2_lod160(Case):
    def test_it(self):
        r, m, _ = S.build_res()
        self.refused(lambda: self.rr(patch(r, 32, "q", 176), m), "RS-2")


class T_r10_rs2_lod144(Case):
    def test_it(self):
        r, m, _ = S.build_res(bone_count=0, part_count=0)
        self.refused(lambda: self.rr(r, m), "RS-2")


class T_r11_rs3_path(Case):
    def test_it(self):
        r, m, _ = S.build_res()
        self.refused(lambda: self.rr(r, m, path="content/other/piece_mesh"), "RS-3")


class T_r12_rs3_short(Case):
    def test_it(self):
        r, m, _ = S.build_res()
        self.refused(lambda: self.rr(patch(r, 96, "q", struct.unpack_from("<q", r, 88)[0]), m), "RS-3")


class T_r13_rs4_type(Case):
    def test_it(self):
        r, m, _ = S.build_res()
        self.refused(lambda: self.rr(patch(r, 108, "I", 2), m), "RS-4")


class T_r14_rs4_lods(Case):
    def test_it(self):
        r, m, _ = S.build_res()
        self.refused(lambda: self.rr(patch(r, 124, "H", 2), m), "RS-4")


class T_r15_rs4_sections(Case):
    def test_it(self):
        r, m, _ = S.build_res()
        self.refused(lambda: self.rr(patch(r, 126, "H", 2), m), "RS-4")


class T_r16_rs5_lodtype(Case):
    def test_it(self):
        r, m, info = S.build_res()
        self.refused(lambda: self.rr(patch(r, info["L"], "I", 2), m), "RS-5")


class T_r17_rs5_seccount(Case):
    def test_it(self):
        r, m, info = S.build_res()
        self.refused(lambda: self.rr(patch(r, info["L"] + 8, "i", 2), m), "RS-5")


class T_r18_rs5_secptr(Case):
    def test_it(self):
        r, m, info = S.build_res()
        self.refused(lambda: self.rr(patch(r, info["L"] + 12, "q", 352), m), "RS-5")


class T_r19_rs6_count(Case):
    def test_it(self):
        r, m, info = S.build_res()
        self.refused(lambda: self.rr(patch(r, info["L"] + 20, "i", 2), m), "RS-6")


class T_r20_rs6_byte(Case):
    def test_it(self):
        r, m, info = S.build_res()
        self.refused(lambda: self.rr(patch(r, info["cat"], "B", 1), m), "RS-6")


class T_r21_rs7_guid(Case):
    def test_it(self):
        r, m, info = S.build_res()
        self.refused(lambda: self.rr(patch(r, info["L"] + 99, "B", 1), m), "RS-7")


class T_r22_rs8_t0(Case):
    def test_it(self):
        r, m, info = S.build_res()
        self.refused(lambda: self.rr(patch(r, info["S"] + 24, "I", 0), m), "RS-8")


class T_r23_rs8_tmax(Case):
    def test_it(self):
        r, m, info = S.build_res()
        self.refused(lambda: self.rr(patch(r, info["S"] + 24, "I", 200001), m), "RS-8")


class T_r24_rs8_n0(Case):
    def test_it(self):
        r, m, info = S.build_res()
        self.refused(lambda: self.rr(patch(r, info["S"] + 36, "I", 0), m), "RS-8")


class T_r25_rs8_nmax(Case):
    def test_it(self):
        r, m, info = S.build_res()
        self.refused(lambda: self.rr(patch(r, info["S"] + 36, "I", 65536), m), "RS-8")


class T_r26_rs8_prim(Case):
    def test_it(self):
        r, m, info = S.build_res()
        self.refused(lambda: self.rr(patch(r, info["S"] + 41, "B", 4), m), "RS-8")


class T_r27_rs9_stride(Case):
    def test_it(self):
        r, m, info = S.build_res()
        self.refused(lambda: self.rr(patch(r, info["S"] + 40, "B", 36), m), "RS-9")


class T_r28_rs10_e0(Case):
    def test_it(self):
        r, m, info = S.build_res()
        self.refused(lambda: self.rr(patch(r, info["S"] + 160, "B", 0), m), "RS-10")


class T_r29_rs10_e16(Case):
    def test_it(self):
        r, m, info = S.build_res()
        self.refused(lambda: self.rr(patch(r, info["S"] + 160, "B", 17), m), "RS-10")


class T_r30_rs10_streams(Case):
    def test_it(self):
        r, m, info = S.build_res()
        self.refused(lambda: self.rr(patch(r, info["S"] + 161, "B", 2), m), "RS-10")


class T_r31_rs10_class(Case):
    def test_it(self):
        r, m, info = S.build_res()
        self.refused(lambda: self.rr(patch(r, info["S"] + 145, "B", 1), m), "RS-10")


class T_r32_rs10_otherstride(Case):
    def test_it(self):
        r, m, info = S.build_res()
        self.refused(lambda: self.rr(patch(r, info["S"] + 146, "B", 4), m), "RS-10")


class T_r33_rs10_inactive(Case):
    def test_it(self):
        r, m, info = S.build_res()
        self.refused(lambda: self.rr(patch(r, info["S"] + 80 + 4 * 5, "B", 9), m), "RS-10")


class T_r34_rs10_usage0(Case):
    def test_it(self):
        r, m, _ = S.build_res(elements=S.RES_ELEMS + [(0, 6, 24)])
        self.refused(lambda: self.rr(r, m), "RS-10")


class T_r35_rs10_stream(Case):
    def test_it(self):
        r, m, info = S.build_res()
        self.refused(lambda: self.rr(patch(r, info["S"] + 80 + 3, "B", 1), m), "RS-10")


class T_r36_rs10_fit(Case):
    def test_it(self):
        r, m, _ = S.build_res(elements=S.RES_ELEMS + [(6, 8, 28)])
        self.refused(lambda: self.rr(r, m), "RS-10")


class T_r37_rs10_overlap(Case):
    def test_it(self):
        r, m, _ = S.build_res(elements=S.RES_ELEMS + [(6, 6, 22)])
        self.refused(lambda: self.rr(r, m), "RS-10")


class T_r38_rs10_dup(Case):
    def test_it(self):
        r, m, _ = S.build_res(elements=S.RES_ELEMS + [(4, 13, 24)])
        self.refused(lambda: self.rr(r, m), "RS-10")


class T_r39_rs11_format(Case):
    def test_it(self):
        r, m, _ = S.build_res(elements=S.RES_ELEMS + [(6, 99, 24)])
        self.refused(lambda: self.rr(r, m), "RS-11")


class T_r40_rs12_pos(Case):
    def test_it(self):
        r, m, _ = S.build_res(elements=[(1, 8, 0), (2, 23, 12), (4, 13, 20)])
        self.refused(lambda: self.rr(r, m), "RS-12")


class T_r41_rs12_idx(Case):
    def test_it(self):
        r, m, _ = S.build_res(elements=[(1, 3, 0), (2, 8, 12), (4, 13, 20)])
        self.refused(lambda: self.rr(r, m), "RS-12")


class T_r42_rs12_w(Case):
    def test_it(self):
        r, m, _ = S.build_res(elements=[(1, 3, 0), (2, 23, 12), (4, 6, 20)])
        self.refused(lambda: self.rr(r, m), "RS-12")


class T_r43_rs13_size(Case):
    def test_it(self):
        r, m, _ = S.build_res()
        ins, isz, ts, op = struct.unpack("<4I", m)
        r2 = r[:ins + isz] + bytes(16) + r[ins + isz:]
        self.refused(lambda: self.rr(r2, struct.pack("<4I", ins, isz + 16, ts, op)), "RS-13")


class T_r44_rs13_zero(Case):
    def test_it(self):
        r, m, info = S.build_res()
        self.refused(lambda: self.rr(patch(r, info["ib_start"] + info["ib"], "B", 1), m), "RS-13")


class T_r45_rs14_neg(Case):
    def test_it(self):
        r, m, info = S.build_res()
        self.refused(lambda: self.rr(patch(r, info["L"] + 92, "I", 95), m), "RS-14")


class T_r46_rs14_stride(Case):
    def test_it(self):
        r, m, _ = S.build_res(slack=bytes(32))
        self.refused(lambda: self.rr(r, m), "RS-14")


class T_r47_rs14_zero(Case):
    def test_it(self):
        r, m, _ = S.build_res(slack=b"\x01" + bytes(7))
        self.refused(lambda: self.rr(r, m), "RS-14")


class T_r47b_rs14_slack_ok(Case):
    def test_it(self):
        r, m, _ = S.build_res(slack=bytes(8))
        self.assertEqual(self.ok(lambda: self.rr(r, m))["vertex_buffer_slack_bytes"], 8)


class T_r48_rs15_bbox(Case):
    def test_it(self):
        r, m, _ = S.build_res(positions=[(0.0, 0.0, 0.0), (1.002, 0.0, 0.5), (0.0, -1.0, 0.25)])
        self.refused(lambda: self.rr(r, m), "RS-15")


class T_r48b_rs15_tolerance(Case):
    def test_it(self):
        r, m, _ = S.build_res(positions=[(0.0, 0.0, 0.0), (1.0005, 0.0, 0.5), (0.0, -1.0, 0.25)])
        self.ok(lambda: self.rr(r, m))


class T_r49_rs16_range(Case):
    def test_it(self):
        r, m, _ = S.build_res(indices=(0, 1, 3))
        self.refused(lambda: self.rr(r, m), "RS-16")


class T_r50_rs17_extra(Case):
    def test_it(self):
        r, m, _ = S.build_res(trailing=bytes(6))
        self.refused(lambda: self.rr(r, m), "RS-16")


class T_r51_rs17_mult4(Case):
    def test_it(self):
        r, m, _ = S.build_res(trailing=b"\0")
        self.refused(lambda: self.rr(r, m), "RS-16")


class T_r52_rs17_zero(Case):
    def test_it(self):
        r, m, _ = S.build_res(trailing=b"\0\x01")
        self.refused(lambda: self.rr(r, m), "RS-16")


class T_r54_rs18_lane(Case):
    def test_it(self):
        r, m, _ = S.build_res(bones=[(0, 1, 2, 3), (1, 1, 0, 0), (2, 0, 1, 1)])
        self.refused(lambda: self.rr(r, m), "RS-18")


class T_a01_meshes(Case):
    def test_it(self):
        sk, _ = S.build_skeleton(names=["Zed", "Amy", "Bob"])
        out = self.ok(lambda: meshset.read_meshes(sk, [piece(), piece()], 3))
        p1, p2 = out["pieces"]
        self.assertEqual((p1["piece"], p2["piece"], p1["file"]), (1, 2, "mesh-1-piece_mesh.json"))
        self.assertEqual((p1["rid_u64"], p1["bones_used"], p1["bone_names_used"]), (RID, [0, 1], ["Amy", "Zed"]))
        self.assertEqual(out["skeleton"]["names"], ["Zed", "Amy", "Bob"])
        self.assertEqual(out["ranges"][1], {"piece": 2, "vertex_offset": 3, "vertex_count": 3, "triangle_offset": 1,
                                            "triangle_count": 1})
        blob = struct.pack("<6H", 0, 1, 2, 3, 4, 5)
        self.assertEqual(out["totals"], {"vertices": 6, "triangles": 2, "concatenated_triangles_bytes": 12,
                                         "concatenated_triangles_u16le_sha256": hashlib.sha256(blob).hexdigest()})


class T_a02_cat_range(Case):
    def test_it(self):
        sk, _ = S.build_skeleton()
        n = 65535
        big = piece(positions=[(0.0, 0.0, 0.0)] * n, bones=[(0, 0, 0, 0)] * n, weights=[(255, 0, 0, 0)] * n)
        self.refused(lambda: meshset.read_meshes(sk, [big, piece()], 3), "CAT-1")


class T_a03_canonical(Case):
    def test_it(self):
        sk, _ = S.build_skeleton()
        out = self.ok(lambda: meshset.read_meshes(sk, [piece()], 3))
        out["pieces"][0]["diagnostic_extra"] = 1
        text = self.ok(lambda: meshset.canonical_json(out))
        obj = json.loads(text)
        self.assertEqual(set(obj), {"skeleton", "pieces", "ranges", "totals"})
        self.assertEqual(set(obj["skeleton"]), {"joint_count", "names", "name_hashes", "parents", "local", "model", "inverse"})
        self.assertNotIn("diagnostic_extra", obj["pieces"][0])
        self.assertEqual(len(obj["pieces"][0]), 24)
        self.assertEqual(text, json.dumps(obj, sort_keys=True, separators=(",", ":"), ensure_ascii=True))


class T_a04_spans(Case):
    def test_it(self):
        sk, _ = S.build_skeleton()
        rec, data = synth_recipe(sk, [piece()])
        out = self.ok(lambda: meshset.read_meshes_from_spans(data, rec, 3))
        self.assertEqual(meshset.canonical_json(out), meshset.canonical_json(meshset.read_meshes(sk, [piece()], 3)))


class T_a05_in1_size(Case):
    def test_it(self):
        sk, _ = S.build_skeleton()
        rec, data = synth_recipe(sk, [piece()])

        data["m1-res"] = data["m1-res"][:-4]
        self.refused(lambda: meshset.read_meshes_from_spans(data, rec, 3), "IN-1")


class T_a06_in1_sha(Case):
    def test_it(self):
        sk, _ = S.build_skeleton()
        rec, data = synth_recipe(sk, [piece()])

        data["skel"] = patch(data["skel"], tables(data["skel"])["D"], "B", 0x55)
        self.refused(lambda: meshset.read_meshes_from_spans(data, rec, 3), "IN-1")


class T_a07_in2(Case):
    def test_it(self):
        sk, _ = S.build_skeleton()
        rec, data = synth_recipe(sk, [piece()])

        del rec["spans"][1]["res_meta_hex"]
        self.refused(lambda: meshset.read_meshes_from_spans(data, rec, 3), "IN-2")


class T_a08_in3_skeleton(Case):
    def test_it(self):
        sk, _ = S.build_skeleton()
        rec, data = synth_recipe(sk, [piece()])

        rec["spans"].append(dict(rec["spans"][-1]))
        self.refused(lambda: meshset.read_meshes_from_spans(data, rec, 3), "IN-3")


class T_a09_in3_pair(Case):
    def test_it(self):
        sk, _ = S.build_skeleton()
        rec, data = synth_recipe(sk, [piece()])

        rec["spans"][1]["asset"] = "content/other/piece_mesh"
        self.refused(lambda: meshset.read_meshes_from_spans(data, rec, 3), "IN-3")


class T_a10_in1_missing(Case):
    def test_it(self):
        sk, _ = S.build_skeleton()
        rec, data = synth_recipe(sk, [piece()])

        del data["m1-ebx"]
        self.refused(lambda: meshset.read_meshes_from_spans(data, rec, 3), "IN-1")


class T_p01_recipe_g1_fields(Case):
    def test_it(self):
        rec = json.loads((ROOT / "tools" / "fm26vm" / "recipe-fifa17-v2.json").read_text(encoding="utf-8"))
        res = next(s for s in rec["spans"] if s["role"] == "mesh-res")
        res.update(res_meta_hex="b005000070af000044000000a000e000", rid="-5848083960300851095")

        try:
            recipe_mod.validate(rec)
        except recipe_mod.RecipeError as e:
            self.fail(f"refused: {e}")


class T_p02_recipe_resmeta_hex(Case):
    def test_it(self):
        rec = json.loads((ROOT / "tools" / "fm26vm" / "recipe-fifa17-v2.json").read_text(encoding="utf-8"))
        res = next(s for s in rec["spans"] if s["role"] == "mesh-res")
        res.update(res_meta_hex="b005000070af000044000000a000e000", rid="-5848083960300851095")

        res["res_meta_hex"] = "B005000070AF000044000000A000E000"
        with self.assertRaises(recipe_mod.RecipeError):
            recipe_mod.validate(rec)


class T_p03_recipe_rid(Case):
    def test_it(self):
        rec = json.loads((ROOT / "tools" / "fm26vm" / "recipe-fifa17-v2.json").read_text(encoding="utf-8"))
        res = next(s for s in rec["spans"] if s["role"] == "mesh-res")
        res.update(res_meta_hex="b005000070af000044000000a000e000", rid="-5848083960300851095")

        res["rid"] = "12a"
        with self.assertRaises(recipe_mod.RecipeError):
            recipe_mod.validate(rec)


class T_p04_recipe_golden_hex(Case):
    def test_it(self):
        rec = json.loads((ROOT / "tools" / "fm26vm" / "recipe-fifa17-v2.json").read_text(encoding="utf-8"))
        res = next(s for s in rec["spans"] if s["role"] == "mesh-res")
        res.update(res_meta_hex="b005000070af000044000000a000e000", rid="-5848083960300851095")

        rec["golden"]["meshset_reader_sha256"] = "not-hex"
        with self.assertRaises(recipe_mod.RecipeError):
            recipe_mod.validate(rec)


class T_p05_recipe_g1_data(Case):
    def test_it(self):
        expected = json.loads(""" {
         "02-m1-res": [
          "b005000070af000044000000a000e000",
          "-5848083960300851095"
         ],
         "04-m2-res": [
          "a00800007044000048000000a000e000",
          "6775724048701308811"
         ],
         "06-m3-res": [
          "80070000a034000048000000a000e000",
          "-4366258733662313673"
         ],
         "08-m4-res": [
          "e0030000c032000044000000a000e000",
          "9014725910787184547"
         ],
         "10-m5-res": [
          "500300001021000044000000a000e000",
          "7265429924975780017"
         ],
         "12-m6-res": [
          "40030000c047000044000000a000e000",
          "-9055962213275608401"
         ]
        } """)
        rec = recipe_mod.load(recipe_mod.DEFAULT_RECIPE)
        got = {s["id"]: [s.get("res_meta_hex"), s.get("rid")] for s in rec["spans"] if s["role"] == "mesh-res"}
        self.assertEqual(got, expected)


class T_p06_recipe_golden_data(Case):
    def test_it(self):
        rec = recipe_mod.load(recipe_mod.DEFAULT_RECIPE)
        self.assertEqual(rec["golden"].get("meshset_reader_sha256"), "6f7b50231baa859b0692cb16a33dfef348ce63ab89ea4e0c89bc104f38411715")


class T_c01_cli_read_meshes(Case):
    def test_it(self):
        with mock.patch.object(S, "SK_HASHES", list(range(438))):
            sk, _ = S.build_skeleton(names=[f"j{i}" for i in range(438)], parents=[-1] + list(range(437)),
                                     mats=[[[[1.0] * 4] * 3] * 438] * 3)
        p = piece()
        rec, data = synth_recipe(sk, [p])
        tmp = tempfile.TemporaryDirectory()
        self.addCleanup(tmp.cleanup)
        d = Path(tmp.name)
        for sid, payload in data.items():
            (d / f"{sid}.bin").write_bytes(payload)

        def run(rec):
            (d / "recipe.json").write_text(json.dumps(rec), encoding="utf-8")
            buf = io.StringIO()
            try:
                code = cli.main(["read-meshes", "--spans", str(d), "--out", str(d / "out.json"), "--recipe",
                                 str(d / "recipe.json")], registry={}, fm_running=lambda: False, out=buf)
            except BaseException as e:  # noqa: BLE001  argparse exit / crash is a failure, not a refusal
                self.fail(f"read-meshes raised {e!r}")
            return code, buf.getvalue()

        rec["golden"]["meshset_reader_sha256"] = hashlib.sha256(
            meshset.canonical_json(meshset.read_meshes_from_spans(data, rec)).encode("ascii")).hexdigest()
        code, text = run(rec)
        self.assertEqual(code, 0, text)
        want = meshset.canonical_json(meshset.read_meshes_from_spans(data, rec))
        self.assertEqual((d / "out.json").read_text(encoding="ascii"), want)
        self.assertIn(hashlib.sha256(want.encode("ascii")).hexdigest(), text)


class T_c02_cli_error(Case):
    def test_it(self):
        with mock.patch.object(S, "SK_HASHES", list(range(438))):
            sk, _ = S.build_skeleton(names=[f"j{i}" for i in range(438)], parents=[-1] + list(range(437)),
                                     mats=[[[[1.0] * 4] * 3] * 438] * 3)
        p = piece()
        rec, data = synth_recipe(sk, [p])
        tmp = tempfile.TemporaryDirectory()
        self.addCleanup(tmp.cleanup)
        d = Path(tmp.name)
        for sid, payload in data.items():
            (d / f"{sid}.bin").write_bytes(payload)

        def run(rec):
            (d / "recipe.json").write_text(json.dumps(rec), encoding="utf-8")
            buf = io.StringIO()
            try:
                code = cli.main(["read-meshes", "--spans", str(d), "--out", str(d / "out.json"), "--recipe",
                                 str(d / "recipe.json")], registry={}, fm_running=lambda: False, out=buf)
            except BaseException as e:  # noqa: BLE001  argparse exit / crash is a failure, not a refusal
                self.fail(f"read-meshes raised {e!r}")
            return code, buf.getvalue()

        (d / "m1-res.bin").unlink()
        code, text = run(rec)
        self.assertEqual(code, 1)
        self.assertIn("error: IN-1", text)
        self.assertFalse((d / "out.json").exists())


class T_c03_cli_golden(Case):
    def test_it(self):
        with mock.patch.object(S, "SK_HASHES", list(range(438))):
            sk, _ = S.build_skeleton(names=[f"j{i}" for i in range(438)], parents=[-1] + list(range(437)),
                                     mats=[[[[1.0] * 4] * 3] * 438] * 3)
        p = piece()
        rec, data = synth_recipe(sk, [p])
        tmp = tempfile.TemporaryDirectory()
        self.addCleanup(tmp.cleanup)
        d = Path(tmp.name)
        for sid, payload in data.items():
            (d / f"{sid}.bin").write_bytes(payload)

        def run(rec):
            (d / "recipe.json").write_text(json.dumps(rec), encoding="utf-8")
            buf = io.StringIO()
            try:
                code = cli.main(["read-meshes", "--spans", str(d), "--out", str(d / "out.json"), "--recipe",
                                 str(d / "recipe.json")], registry={}, fm_running=lambda: False, out=buf)
            except BaseException as e:  # noqa: BLE001  argparse exit / crash is a failure, not a refusal
                self.fail(f"read-meshes raised {e!r}")
            return code, buf.getvalue()

        rec["golden"]["meshset_reader_sha256"] = "0" * 64
        code, text = run(rec)
        self.assertEqual(code, 1)
        self.assertIn("differs from the recipe golden", text)
        self.assertFalse((d / "out.json").exists())


class T_z01_real(Case):
    def test_it(self):
        spans = os.environ.get("FM26VM_SPANS")
        if not spans:
            self.skipTest("FM26VM_SPANS not set (real decoded spans are never stored in the repository)")
        rec = recipe_mod.load(recipe_mod.DEFAULT_RECIPE)
        out = meshset.read_meshes_dir(spans, rec)
        text = meshset.canonical_json(out)
        self.assertEqual(hashlib.sha256(text.encode("ascii")).hexdigest(), rec["golden"]["meshset_reader_sha256"])
        self.assertEqual((out["skeleton"]["joint_count"], out["totals"]["vertices"], out["totals"]["triangles"]), (438, 1868, 2911))
        self.assertEqual(out["totals"]["concatenated_triangles_u16le_sha256"],
                         rec["golden"]["pack_v2_files"]["triangles.u16le.bin"]["sha256"])


class T_g_s21_arr_align(Case):
    def test_it(self):
        b, info = S.build_skeleton()
        at = info["desc_pos"] + 12 * 4
        rel = u32(b, at) + 4
        b = patch(patch(b, at, "I", rel), info["B"] + rel - 4, "I", 3)
        self.refused(lambda: self.sk(b), "E-ARR-2")


class T_g_s22_arr_span(Case):
    def test_it(self):
        b, info = S.build_skeleton()
        at = info["desc_pos"] + 12 * 4
        b = patch(patch(b, at + 4, "I", 100000), info["B"] + u32(b, at) - 4, "I", 100000)
        self.refused(lambda: self.sk(b), "E-ARR-2")


class T_g_r04_rm_start(Case):
    def test_it(self):
        r, m, _ = S.build_res()
        _ins, _isz, ts, op = struct.unpack("<4I", m)
        self.refused(lambda: self.rr(r, struct.pack("<4I", 0, len(r) - ts, ts, op)), "RM-1")


class T_g_r28_rs10_e0(Case):
    def test_it(self):
        r, m, info = S.build_res()
        b = bytearray(patch(r, info["S"] + 160, "B", 0))
        for k in range(16):
            b[info["S"] + 80 + 4 * k] = 0
        self.refused(lambda: self.rr(bytes(b), m), "RS-10")


class T_g_r29_rs10_e16(Case):
    def test_it(self):
        extra = [(u, 6, 24 + 4 * i) for i, u in enumerate(range(5, 18))]
        r, m, info = S.build_res(elements=S.RES_ELEMS + extra, stride=80)
        self.refused(lambda: self.rr(patch(r, info["S"] + 160, "B", 17), m), "RS-10")
