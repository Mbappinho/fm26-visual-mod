# SPDX-License-Identifier: MIT
"""fm26vm B5: clean-room GenericData reader tools/fm26vm/gd.py on synthetic banks (CONTRACT-GD + erratum E1).

Synthetic banks come from tests/fixtures/fm26vm/gd_synth.py (writer of clean-room implementation B, H218, plus the
H219 case catalogue). A crash is never a refusal: `refused` turns any non-GdError exception into a test failure.
"""
import hashlib
import importlib.util
import json
import struct
import sys
import unittest
from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]
if str(ROOT) not in sys.path:
    sys.path.insert(0, str(ROOT))

from tools.fm26vm import gd  # noqa: E402

_spec = importlib.util.spec_from_file_location("fm26vm_gd_synth", ROOT / "tests" / "fixtures" / "fm26vm" / "gd_synth.py")
S = importlib.util.module_from_spec(_spec)
_spec.loader.exec_module(S)


def f32(x):
    return struct.unpack(">f", struct.pack(">f", x))[0]


def positive():
    return gd.read_bank(S.positive_model().serialize(), S.recipe(), S.RIG_KEY)


class Refusals(unittest.TestCase):
    def part(self, obj, key):
        if not isinstance(obj, dict) or key not in obj:
            self.fail(f"no {key!r} in {type(obj).__name__}")
        return obj[key]

    def run_case(self, case):
        data, rec, rig, kw, budget = S.build(case)
        old = getattr(gd, budget[0], None) if budget else None
        if budget:
            setattr(gd, budget[0], budget[1])
        try:
            return gd.read_bank(data, rec, rig, **kw)
        finally:
            if budget:
                setattr(gd, budget[0], old)

    def refused(self, case, rule):
        try:
            self.run_case(case)
        except gd.GdError as e:
            self.assertEqual(e.rule, rule, str(e))
            return
        except Exception as e:  # noqa: BLE001  a crash is not a refusal
            self.fail(f"not a GdError: {e!r}")
        self.fail(f"not refused (expected {rule})")

    def accepted(self, case):
        try:
            return self.run_case(case)
        except Exception as e:  # noqa: BLE001
            self.fail(f"refused or crashed: {e!r}")


class C01ClipProjection(Refusals):
    def test_clip_fields_arrays_and_dof_ids(self):
        c = self.part(positive(), "clips")[0]
        got = (c["id"], c["asset_key"], c["section"][1] > c["section"][0], c["fields"]["NumKeys"], c["fields"]["QuatMin"],
               c["fields"]["Flags"], c["fields"]["Dct"], c["fields"]["__base"] > 0, c["Data"], c["FrameBlockSizes"],
               c["ConstantPalette"], c["channel_to_dof_key"], c["hierarchy_key"], c["DofIds"], len(c["fields"]))
        self.assertEqual(got, ("c1", "5001", True, 9, f32(-0.7654324), 270, 4.0, True, bytes(range(40)), [10, 12],
                               [0.5, -1.25], "4001", "6001", [11, 15, 13, 10], 25))


class C02RigProjection(Refusals):
    def test_joints_slots_and_defaults(self):
        r = self.part(positive(), "rig")
        j = [(x["name"], x["parent"], x["type"], x["t_dof"], x["q_dof"], x["s_dof"]) for x in r["joints"]]
        self.assertEqual(j, [("J0", -1, 1, 10, 11, 12), ("J1", 0, 0, 13, 15, 14)])
        self.assertEqual((r["rig_key"], r["skeleton_key"], sorted(r["slots"]), r["slots"]["15"]),
                         ("3001", "1001", ["10", "11", "12", "13", "14", "15"], {"name": "J1.q", "type": 14}))
        self.assertEqual((r["joints"][1]["q"], r["joints"][0]["t"], len(r["defaults"])),
                         ([f32(0.7071069), f32(0.7071066), f32(-3.9e-07), f32(-7.3e-07)], [f32(0.1), 0.0, -2.5], 6))


class C03Controller(Refusals):
    def test_controller_matched_by_anim(self):
        c = self.part(positive(), "clips")[0]
        self.assertEqual(c.get("controller"), {"key": "7001", "name": "ClipB_Name", "FPS": 30.0, "TimeScale": 0.5,
                                               "Distance": 2.5})


class C03bControllerBaseIgnored(Refusals):
    def test_controller_with_base_is_read(self):
        r = self.accepted("x: controller with a non-zero __base")
        self.assertEqual(r["clips"][0]["controller"]["key"], "7001")


class C04Canonical(Refusals):
    def test_canonical_json_hashes_data_and_sorts_keys(self):
        fn = getattr(gd, "canonical_json", None)
        if fn is None:
            self.fail("no canonical_json")
        text = fn(positive())
        obj = json.loads(text)
        self.assertEqual((obj["clips"][0]["Data"], ", " in text, text == json.dumps(obj, sort_keys=True, separators=(",", ":"))),
                         ({"count": 40, "sha256": hashlib.sha256(bytes(range(40))).hexdigest()}, False, True))


class C05Bounds(Refusals):
    def test_refused(self):
        self.refused('x: input shorter than the 8-byte envelope', 'R-BOUNDS')


class C06Packaging(Refusals):
    def test_refused(self):
        self.refused('packaging word 1', 'R-PACKAGING')


class C07StrmHeader(Refusals):
    def test_refused(self):
        self.refused('x: skip length beyond the input', 'R-STRM')


class C08StrmTag(Refusals):
    def test_refused(self):
        self.refused('x: stream tag GD.XXXX', 'R-STRM')


class C09StrmEnd(Refusals):
    def test_refused(self):
        self.refused('STRM end one byte short of EOF', 'R-STRM')


class C11SectionSize(Refusals):
    def test_refused(self):
        self.refused('a DATA size set to 15', 'R-SECTION')


class C13SectionTrunc(Refusals):
    def test_refused(self):
        self.refused('x: 8 trailing bytes inside the stream', 'R-SECTION')


class C10SectionTag(Refusals):
    def test_refused(self):
        self.refused('a DATA section tag changed to GD.XXXX', 'R-SECTION')


class C12SectionWord(Refusals):
    def test_refused(self):
        self.refused('a DATA word set to size + 1', 'R-SECTION')


class C14Endian(Refusals):
    def test_refused(self):
        self.refused('byte-order marker of the REFL set to l', 'R-ENDIAN')


class C14BEndianSwapped(Refusals):
    def test_refused(self):
        self.refused('x: byte-swapped bank', 'R-ENDIAN')


class C15ReflSecond(Refusals):
    def test_refused(self):
        self.refused('two REFL sections', 'R-REFL')


class C16ReflFirst(Refusals):
    def test_refused(self):
        self.refused('x: first child is a DATA section', 'R-REFL')


class C17ReflNone(Refusals):
    def test_refused(self):
        self.refused('x: stream without children', 'R-REFL')


class C18BudgetData(Refusals):
    def test_refused(self):
        self.refused('x: DATA section budget 2', 'R-BUDGET')


class C19ReflStoff(Refusals):
    def test_refused(self):
        self.refused('layout string-table offset not equal to 32 + 32N', 'R-REFL')


class C20ReflIndex(Refusals):
    def test_refused(self):
        self.refused('x: layout index does not fit', 'R-REFL')


class C21ReflRecord(Refusals):
    def test_refused(self):
        self.refused('x: layout offset beyond REFL', 'R-REFL')


class C22BudgetLayouts(Refusals):
    def test_refused(self):
        self.refused('x: layout budget 2', 'R-BUDGET')


class C23ReflNfields(Refusals):
    def test_refused(self):
        self.refused('x: field record budget 1', 'R-REFL')


class C24ReflStringUnterminated(Refusals):
    def test_refused(self):
        self.refused('class name without terminating NUL inside the table', 'R-REFL-STRING')


class C25ReflStringNul(Refusals):
    def test_refused(self):
        self.refused('x: string table not starting with NUL', 'R-REFL-STRING')


class C26ReflStringUtf8(Refusals):
    def test_refused(self):
        self.refused('x: class name not UTF-8', 'R-REFL-STRING')


class C27ReflDupName(Refusals):
    def test_refused(self):
        self.refused('two layouts with the same class name', 'R-REFL-DUP')


class C28ReflDupHash(Refusals):
    def test_refused(self):
        self.refused('x: two layouts with the same class hash', 'R-REFL-DUP')


class C29ReflSkipped(Refusals):
    def test_refused(self):
        self.refused('a needed class gets one field record with element size 0', 'R-REFL-SKIPPED')


class C30ReflTargetHash(Refusals):
    def test_refused(self):
        self.refused('field type hash differs from its target layout hash', 'R-REFL-TARGET')


class C31ReflTargetOff(Refusals):
    def test_refused(self):
        self.refused('field type offset not in the layout index', 'R-REFL-TARGET')


class C32TypeKey(Refusals):
    def test_refused(self):
        self.refused('Key layout with a second field', 'R-TYPE')


class C33TypePrim(Refusals):
    def test_refused(self):
        self.refused('UInt16 layout size 4', 'R-TYPE')


class C33BTypeString(Refusals):
    def test_refused(self):
        self.refused('x: String layout size 8', 'R-TYPE')


class C33CTypeVector(Refusals):
    def test_refused(self):
        self.refused('x: Vector3 layout with a fourth field', 'R-TYPE')


class C34FieldAbsent(Refusals):
    def test_refused(self):
        self.refused('VBR field NumKeys renamed', 'R-FIELD')


class C35FieldDup(Refusals):
    def test_refused(self):
        self.refused('x: VBR field NumKeys present twice', 'R-FIELD')


class C36FieldType(Refusals):
    def test_refused(self):
        self.refused('x: VBR field NumKeys of type Int32', 'R-FIELD')


class C37FieldBounds(Refusals):
    def test_refused(self):
        self.refused('VBR field NumKeys offset beyond class size', 'R-FIELD-BOUNDS')


class C38Flags(Refusals):
    def test_refused(self):
        self.refused('Data field flags 2', 'R-FLAGS')


class C39RootTypeHigh(Refusals):
    def test_refused(self):
        self.refused('root type word high half non-zero', 'R-ROOT-TYPE')


class C40RootTypeUnknown(Refusals):
    def test_refused(self):
        self.refused('x: root class hash unknown', 'R-ROOT-TYPE')


class C41RootBody(Refusals):
    def test_refused(self):
        self.refused('root body offset 16', 'R-ROOT-BODY')


class C42Key(Refusals):
    def test_refused(self):
        self.refused('x: root of a class without __key', 'R-KEY')


class C43KeyDup(Refusals):
    def test_refused(self):
        self.refused('two roots with the same key (one not requested)', 'R-KEY-DUP')


class C44KeyAbsent(Refusals):
    def test_refused(self):
        self.refused('recipe clip key absent', 'R-KEY-ABSENT')


class C45Class(Refusals):
    def test_refused(self):
        self.refused('clip key resolves to a LayoutAsset', 'R-CLASS')


class C46NullC2D(Refusals):
    def test_refused(self):
        self.refused('AnimationAsset ChannelToDofAsset key 0', 'R-NULL-REF')


class C47NullDsl(Refusals):
    def test_refused(self):
        self.refused('x: AnimationAsset DofSetList key 0', 'R-NULL-REF')


class C48BasePos(Refusals):
    def test_refused(self):
        self.refused('__base pointing past section end', 'R-BASE')


class C49BaseClass(Refusals):
    def test_refused(self):
        self.refused('x: clip base of class DeltaTrajLayoutAsset', 'R-CLASS')


class C50BaseDepth(Refusals):
    def test_refused(self):
        self.refused('AnimationAsset base with non-zero __base', 'R-BASE-DEPTH')


class C51BaseNone(Refusals):
    def test_refused(self):
        self.refused('x: clip __base 0', 'R-BASE')


class C52C2DBase(Refusals):
    def test_refused(self):
        self.refused('ChannelToDofAsset with non-zero __base', 'R-BASE')


class C53RigBase(Refusals):
    def test_refused(self):
        self.refused('x: RigAsset with non-zero __base', 'R-BASE')


class C54SkelBase(Refusals):
    def test_refused(self):
        self.refused('x: SkeletonAsset with non-zero __base', 'R-BASE')


class C55LayoutBase(Refusals):
    def test_refused(self):
        self.refused('x: LayoutAsset with non-zero __base', 'R-BASE')


class C56StringFinalNul(Refusals):
    def test_refused(self):
        self.refused('JointName without final NUL', 'R-STRING')


class C57StringInterior(Refusals):
    def test_refused(self):
        self.refused('JointName with an interior NUL', 'R-STRING')


class C58StringLen(Refusals):
    def test_refused(self):
        self.refused('x: string budget 2', 'R-STRING')


class C59StringSpan(Refusals):
    def test_refused(self):
        self.refused('x: JointName count beyond the section', 'R-STRING')


class C60StringUtf8(Refusals):
    def test_refused(self):
        self.refused('x: JointName not UTF-8', 'R-STRING')


class C61ArraySpan(Refusals):
    def test_refused(self):
        self.refused('Data array count overruns the section', 'R-ARRAY')


class C62ArrayCount(Refusals):
    def test_refused(self):
        self.refused('x: array budget 10', 'R-ARRAY')


class C63Depth(Refusals):
    def test_refused(self):
        self.refused('x: struct depth budget 0', 'R-DEPTH')


class C64ClipChannels(Refusals):
    def test_refused(self):
        self.refused('DofIds count one less than the channel sum', 'R-CLIP-CHANNELS')


class C65ClipBlocks(Refusals):
    def test_refused(self):
        self.refused('FrameBlockSizes sum greater than Data count', 'R-CLIP-BLOCKS')


class C66CtrlCount(Refusals):
    def test_refused(self):
        self.refused('second controller with the same Anim', 'R-CTRL')


class C67CtrlNone(Refusals):
    def test_refused(self):
        self.refused('x: no controller with the clip Anim', 'R-CTRL')


class C68CtrlName(Refusals):
    def test_refused(self):
        self.refused('controller name differs from recipe', 'R-CTRL-NAME')


class C69RigJointParent(Refusals):
    def test_refused(self):
        self.refused('joint 1 parent 1', 'R-RIG-JOINTS')


class C70RigJointDup(Refusals):
    def test_refused(self):
        self.refused('duplicate joint name', 'R-RIG-JOINTS')


class C71RigJointEmpty(Refusals):
    def test_refused(self):
        self.refused('x: empty joint name', 'R-RIG-JOINTS')


class C72RigJointCount(Refusals):
    def test_refused(self):
        self.refused('x: joint budget 1', 'R-RIG-JOINTS')


class C73RigTraj(Refusals):
    def test_refused(self):
        self.refused('x: TrajJointIndex 2', 'R-RIG-JOINTS')


class C74RigSets(Refusals):
    def test_refused(self):
        self.refused('DofSetIdIndices one shorter than RigDofSets', 'R-RIG-SETS')


class C75RigWindow(Refusals):
    def test_refused(self):
        self.refused('LayoutAsset start such that start + slots > DofIds count', 'R-RIG-WINDOW')


class C76RigConflict(Refusals):
    def test_refused(self):
        self.refused('same DofId mapped by two slots with different names', 'R-RIG-DOF-CONFLICT')


class C77DefaultDup(Refusals):
    def test_refused(self):
        self.refused('a DofId twice in DefaultVector3Values', 'R-DEFAULT-DUP')


class C78DefaultBoth(Refusals):
    def test_refused(self):
        self.refused('x: DofId in both default arrays', 'R-DEFAULT-DUP')


class C79RigBinding(Refusals):
    def test_refused(self):
        self.refused('slot J1.q removed', 'R-RIG-BINDING')


class C80RigSlotType(Refusals):
    def test_refused(self):
        self.refused('J0.t slot type 14', 'R-RIG-SLOT-TYPE')


class C81RigDefaultMissing(Refusals):
    def test_refused(self):
        self.refused('default for J1.s removed', 'R-RIG-DEFAULT-MISSING')


class C82RigName(Refusals):
    def test_refused(self):
        self.refused('x: rig name differs from the recipe', 'R-CLASS')


class C83Input(Refusals):
    def test_refused(self):
        self.refused('input sha256 differs (real-bank mode only)', 'R-INPUT')


class C85ReadBankFile(Refusals):
    def test_reads_a_bank_file_with_recipe_rig_and_golden(self):
        import tempfile
        data = S.positive_model().serialize()
        rec = dict(S.recipe(), rig={"key": "3001", "name": "rig_b"},
                   golden={"decoded_bank": {"bytes": len(data), "sha256": hashlib.sha256(data).hexdigest()}})
        with tempfile.TemporaryDirectory() as tmp:
            p = Path(tmp) / "bank.bin"
            p.write_bytes(data)
            fn = getattr(gd, "read_bank_file", None)
            if fn is None:
                self.fail("no read_bank_file")
            r = fn(p, rec)
        self.assertEqual((r["rig"]["rig_key"], r["clips"][0]["Data"], r["bank"]["bytes"]), ("3001", bytes(range(40)), len(data)))


class C91CtrlKey(Refusals):
    def test_refused(self):
        self.refused('x: controller key differs from the recipe', 'R-CTRL')


class C86CliReadBank(Refusals):
    def cli_tree(self, tmp, data, top=None, **golden):
        from tests.test_fm26vm_recipe import fake_tree
        _, obj = fake_tree(tmp / "g")
        obj["clips"][0].update(id="c1", asset_key="5001", asset_name="ClipB_Name")
        obj["rig"] = {"key": "3001", "name": "rig_b", "skeleton_key": "1001"}
        obj["golden"]["decoded_bank"] = {"bytes": len(data), "sha256": hashlib.sha256(data).hexdigest()}
        obj["golden"].update(golden)
        obj.update(top or {})
        (tmp / "bank.bin").write_bytes(data)
        if "gd_reader_sha256" not in golden:  # H226b: the output golden is required; pin the reader's own output
            try:
                obj["golden"]["gd_reader_sha256"] = hashlib.sha256(
                    gd.canonical_json(gd.read_bank_file(tmp / "bank.bin", obj)).encode("ascii")).hexdigest()
            except gd.GdError:
                pass
        (tmp / "r.json").write_text(json.dumps(obj), encoding="utf-8")
        return tmp / "bank.bin", tmp / "r.json", tmp / "o.json"

    def cli(self, args):
        import io
        from tools.fm26vm import cli
        out = io.StringIO()
        try:
            code = cli.main(args, out=out, registry=lambda *a: None, fm_running=lambda: False)
        except SystemExit as e:
            code = f"exit {e.code}"
        except Exception as e:  # noqa: BLE001 - a crash is a failed expectation
            code = f"raised {e!r}"
        return code, out.getvalue()

    def run_cli(self, data, top=None, **golden):
        import tempfile
        with tempfile.TemporaryDirectory() as t:
            b, r, o = self.cli_tree(Path(t), data, top, **golden)
            code, text = self.cli(["read-bank", "--bank", str(b), "--recipe", str(r), "--out", str(o)])
            return code, text, (o.read_bytes() if o.exists() else None), sorted(p.name for p in Path(t).iterdir())

    def test_read_bank_writes_canonical_json(self):
        code, text, body, _ = self.run_cli(S.positive_model().serialize())
        obj = json.loads(body) if body else {}
        self.assertEqual((code, obj.get("rig", {}).get("rig_key"), body is not None and hashlib.sha256(body).hexdigest() in text),
                         (0, "3001", True), text)


class C87CliRefused(C86CliReadBank):
    def test_refusal_is_an_error_line_and_no_file(self):
        bad = S.patch(S.positive_model().serialize(), 0, ">I", 1)
        code, text, body, names = self.run_cli(bad)
        self.assertEqual((code, text.startswith("error: R-PACKAGING"), body, "o.json.tmp" in names), (1, True, None, False), text)


class C87bCliBadRecipe(C86CliReadBank):
    def test_invalid_recipe_is_an_error_line(self):
        code, text, body, _ = self.run_cli(S.positive_model().serialize(), top={"bogus": 1})
        self.assertEqual((code, text.startswith("error:"), body), (1, True, None), text)


class C93CliGolden(C86CliReadBank):
    def test_output_differing_from_golden_is_refused(self):
        code, text, body, _ = self.run_cli(S.positive_model().serialize(), gd_reader_sha256="0" * 64)
        self.assertEqual((code, "differs from the recipe golden" in text, body), (1, True, None), text)
