# SPDX-License-Identifier: MIT
"""fm26vm B4: clean-room CAS span extractor (synthetic spans + a fake zstd stub only; never FIFA bytes)."""
import hashlib
import io
import json
import sys
import tempfile
import unittest
from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]
if str(ROOT) not in sys.path:
    sys.path.insert(0, str(ROOT))

from tools.fm26vm import cas  # noqa: E402

P = bytes(range(256)) * 4
MAGIC = bytes.fromhex("26b52ffd")
END = bytes.fromhex("c00000")

# Test stub standing in for the zstd CLI (NOT the real tool): decodes legacy frames made of raw inner
# blocks only, fails on RLE like the pinned CLI; sidecar <stub>.mode selects a fault, <stub>.log records argv.
FAKE_SRC = r"""import json, sys, time
from pathlib import Path
me = Path(sys.argv[0])
mode = me.with_suffix(".mode").read_text() if me.with_suffix(".mode").exists() else ""
args = sys.argv[1:]
data = Path(args[-1]).read_bytes()
with open(me.with_suffix(".log"), "a") as log:
    log.write(json.dumps(args) + "\n")
out, pos, err = bytearray(), 0, None
while pos < len(data) and err is None:
    if data[pos:pos + 4] != bytes.fromhex("26b52ffd"):
        err = "unsupported format"
        break
    pos += 5 + (0, 1, 2, 8)[data[pos + 4] >> 6]
    while True:
        t, n = data[pos] >> 6, ((data[pos] & 7) << 16) | (data[pos + 1] << 8) | data[pos + 2]
        pos += 3
        if t == 3:
            break
        if t != 1:
            err = "Decoding error (36)"
            break
        out += data[pos:pos + n]
        pos += n
if mode == "sleep":
    time.sleep(4)
if mode == "extra":
    out += b"!"
if mode == "short":
    out = out[:-1]
sys.stdout.buffer.write(out)
sys.stdout.flush()
if mode == "stderr":
    sys.stderr.write("warning\n")
if err:
    sys.stderr.write(err + "\n")
sys.exit(1 if err or mode == "exit1" else 0)
"""


def hdr(kind, decoded, stored, flag=0, ext_hi=0x7, kind_byte=None):
    kb = kind if kind_byte is None else kind_byte
    return (bytes([flag]) + decoded.to_bytes(3, "big") + bytes([kb, (ext_hi << 4) | ((stored >> 16) & 0xF)])
            + (stored & 0xFFFF).to_bytes(2, "big"))


def inner(btype, size):
    return bytes([(btype << 6) | ((size >> 16) & 0x7), (size >> 8) & 0xFF, size & 0xFF])


def frame(data, width=2, window=4, desc=None, magic=MAGIC, end=True, fcs=None, body=None):
    size = len(data) if fcs is None else fcs
    d = ({0: 0, 1: 1, 2: 2, 8: 3}[width] << 6) | window if desc is None else desc
    field = {0: lambda: b"", 1: lambda: bytes([size & 0xFF]), 2: lambda: (size - 256).to_bytes(2, "little"),
             8: lambda: size.to_bytes(8, "little")}
    return magic + bytes([d]) + field[width]() + (inner(1, len(data)) + data if body is None else body) + (END if end else b"")


def b15(data, decoded=None, **kw):
    f = frame(data, **kw)
    return hdr(15, len(data) if decoded is None else decoded, len(f)) + f


def b0(data):
    return hdr(0, len(data), len(data)) + data


def walk(data, expected):
    return cas.walk(lambda o, n: data[o:o + n], len(data), expected)


class Refusals(unittest.TestCase):
    def refused(self, fn, gate, needle):
        try:
            fn()
        except cas.CasError as e:
            self.assertEqual((e.gate, needle in str(e)), (gate, True), str(e))
            return e
        except Exception as e:  # a crash is not a refusal
            self.fail(f"not a CasError: {e!r}")
        self.fail("not refused")


class Tmp(Refusals):
    def setUp(self):
        self._tmp = tempfile.TemporaryDirectory()
        self.tmp = Path(self._tmp.name)
        self.fake = self.tmp / "fake_zstd.py"
        self.fake.write_text(FAKE_SRC, encoding="utf-8")
        self.pin = hashlib.sha256(self.fake.read_bytes()).hexdigest()

    def tearDown(self):
        self._tmp.cleanup()

    def mode(self, m):
        self.fake.with_suffix(".mode").write_text(m)

    def launches(self):
        log = self.fake.with_suffix(".log")
        return [json.loads(x) for x in log.read_text().splitlines()] if log.exists() else []

    def native(self, payload, expected, timeout=10, pin=None, exe=None):
        src = self.tmp / "frames.bin"
        src.write_bytes(payload)
        chunks = []
        rep = cas.run_native(exe or self.fake, pin or self.pin, src, expected, chunks.append, timeout,
                             launcher=(sys.executable,))
        return b"".join(chunks), rep

    def span(self, data, sha1=None, prefix=b"PRE", rel="Data/Win32/x/cas_01.cas"):
        path = self.tmp / "FIFA 17" / rel
        path.parent.mkdir(parents=True, exist_ok=True)
        path.write_bytes(prefix + data + b"POST")
        return {"id": "syn", "role": "mesh-res", "path": rel, "offset": len(prefix), "size": len(data),
                "sha1": sha1 or hashlib.sha1(data).hexdigest()}

    def extract(self, span, expected, pin=None, sha=None, name="o.bin"):
        self.work = self.tmp / "work"
        self.work.mkdir(exist_ok=True)
        self.out = self.tmp / "out" / name
        self.out.parent.mkdir(exist_ok=True)
        return cas.extract_span(self.tmp / "FIFA 17", span, len(expected), sha or hashlib.sha256(expected).hexdigest(),
                                self.fake, pin or self.pin, self.work, self.out, launcher=(sys.executable,))


class HeaderDecodeTests(Refusals):
    def test_header_fields_use_mixed_byte_order(self):
        self.assertEqual(cas.parse_header(bytes.fromhex("000100000f70749b")), cas.Header(15, 65536, 29851))
        self.assertEqual(cas.parse_header(bytes.fromhex("0001000000710000")), cas.Header(0, 65536, 65536))
        self.assertEqual(cas.parse_header(bytes.fromhex("0000fbd00f70c374")), cas.Header(15, 64464, 50036))


class HeaderFlagTests(Refusals):
    def test_nonzero_flag_byte_is_refused(self):
        self.refused(lambda: cas.parse_header(hdr(15, 1024, 100, flag=1)), "framing", "flag byte")


class HeaderBit7Tests(Refusals):
    def test_kind_byte_bit7_is_refused(self):
        self.refused(lambda: cas.parse_header(hdr(15, 1024, 100, kind_byte=0x8F)), "framing", "bit 7")


class HeaderExtTests(Refusals):
    def test_extension_upper_nibble_must_be_7(self):
        for nib in (0x0, 0xF):
            self.refused(lambda: cas.parse_header(hdr(15, 1024, 100, ext_hi=nib)), "framing", "extension")


class HeaderKindTests(Refusals):
    def test_kinds_other_than_0_and_15_are_refused(self):
        for kind in (1, 2):
            self.refused(lambda: cas.parse_header(hdr(kind, 10, 10)), "framing", f"unsupported kind {kind}")


class HeaderDecodedZeroTests(Refusals):
    def test_decoded_size_zero_is_refused(self):
        self.refused(lambda: cas.parse_header(hdr(15, 0, 100)), "framing", "decoded size 0")


class HeaderStoredZeroTests(Refusals):
    def test_stored_size_zero_is_refused(self):
        self.refused(lambda: cas.parse_header(hdr(15, 10, 0)), "framing", "stored size 0")


class HeaderBudgetTests(Refusals):
    def test_decoded_size_above_2mib_is_refused(self):
        self.refused(lambda: cas.parse_header(hdr(15, 2 * 1024 * 1024 + 1, 100)), "framing", "budget")
        self.assertEqual(cas.parse_header(hdr(15, 2 * 1024 * 1024, 100)), cas.Header(15, 2 * 1024 * 1024, 100))


class HeaderStoredMaxTests(Refusals):
    def test_stored_size_is_a_20_bit_field(self):
        self.assertEqual(cas.parse_header(hdr(15, 65536, 0xFFFFF)), cas.Header(15, 65536, 1048575))


class HeaderKind0Tests(Refusals):
    def test_kind0_stored_must_equal_decoded(self):
        self.refused(lambda: cas.parse_header(hdr(0, 5, 4)), "framing", "kind 0")


class WalkTableTests(Refusals):
    def test_walk_builds_the_block_table(self):
        data = b0(b"AAAA") + b15(P) + b0(b"BB")
        got = [tuple(b) for b in walk(data, 1030)]
        self.assertEqual(got, [(0, 0, 8, 0, 4, 4), (1, 12, 20, 15, 1024, 1037), (2, 1057, 1065, 0, 2, 2)])


class WalkTruncHeaderTests(Refusals):
    def test_header_cut_at_7_bytes_is_refused(self):
        self.refused(lambda: walk(b0(b"AAAA") + hdr(0, 1, 1)[:7], 5), "framing", "truncated header")


class WalkTrailingTests(Refusals):
    def test_one_trailing_byte_is_refused(self):
        self.refused(lambda: walk(b0(b"AAAA") + b"\x00", 4), "framing", "truncated header")


class WalkTruncPayloadTests(Refusals):
    def test_payload_past_span_is_refused(self):
        self.refused(lambda: walk(hdr(0, 10, 10) + b"abc", 10), "framing", "truncated payload")


class WalkRunningTotalTests(Refusals):
    def test_running_total_above_expected_is_refused_at_that_block(self):
        e = self.refused(lambda: walk(b0(b"AAAA") + b0(b"BB"), 4), "framing", "running decoded total")
        self.assertEqual(e.block, 1)


class WalkFinalTotalTests(Refusals):
    def test_total_below_expected_is_refused(self):
        self.refused(lambda: walk(b0(b"AAAA"), 5), "framing", "decoded total 4 != expected 5")


class WalkEmptyTests(Refusals):
    def test_span_without_block_is_refused(self):
        self.refused(lambda: walk(b"", 0), "framing", "no block")


class FrameFactsTests(Refusals):
    def test_valid_frames_report_window_content_size_and_blocks(self):
        self.assertEqual(cas.check_frame(frame(P), 1024), {"window": 65536, "content_size": 1024, "inner_blocks": 1})
        self.assertEqual(cas.check_frame(frame(b"abcdefghij", width=1, window=1), 10),
                         {"window": 8192, "content_size": 10, "inner_blocks": 1})
        self.assertEqual(cas.check_frame(frame(P, width=8, window=9), 1024),
                         {"window": 2097152, "content_size": 1024, "inner_blocks": 1})


class FrameShortTests(Refusals):
    def test_payload_shorter_than_5_bytes_is_refused(self):
        self.refused(lambda: cas.check_frame(MAGIC, 10), "envelope", "too short")


class FrameMagicTests(Refusals):
    def test_modern_magic_is_refused(self):
        self.refused(lambda: cas.check_frame(frame(P, magic=bytes.fromhex("28b52ffd")), 1024), "envelope", "magic")


class FrameReservedTests(Refusals):
    def test_reserved_descriptor_bit_is_refused(self):
        self.refused(lambda: cas.check_frame(frame(P, desc=0xA4), 1024), "envelope", "reserved")


class FrameWindowTests(Refusals):
    def test_window_above_2mib_is_refused(self):
        self.refused(lambda: cas.check_frame(frame(P, window=10), 1024), "envelope", "window")


class FrameFcsAbsentTests(Refusals):
    def test_missing_content_size_is_refused(self):
        self.refused(lambda: cas.check_frame(frame(b"abcdefghij", width=0), 10), "envelope", "content size absent")


class FrameFcsMismatchTests(Refusals):
    def test_content_size_must_equal_cas_decoded_size(self):
        self.refused(lambda: cas.check_frame(frame(P), 1000), "envelope", "content size 1024 != CAS decoded size 1000")


class FrameNoEndTests(Refusals):
    def test_missing_end_block_is_refused(self):
        self.refused(lambda: cas.check_frame(frame(P, end=False), 1024), "envelope", "missing end block")


class FrameAfterEndTests(Refusals):
    def test_bytes_after_end_block_are_refused(self):
        self.refused(lambda: cas.check_frame(frame(P) + b"\x00", 1024), "envelope", "after the end block")


class FrameInnerMaxTests(Refusals):
    def test_inner_block_above_131072_is_refused(self):
        f = frame(b"", width=8, fcs=131073, body=inner(1, 131073) + bytes(131073))
        self.refused(lambda: cas.check_frame(f, 131073), "envelope", "inner block")


class NativeMissingToolTests(Tmp):
    def test_missing_executable_is_a_tool_refusal(self):
        self.refused(lambda: self.native(frame(P), 1024, exe=self.tmp / "missing.exe"), "tool", "not found")


class NativeToolHashTests(Tmp):
    def test_executable_hash_must_match_the_pin(self):
        self.refused(lambda: self.native(frame(P), 1024, pin="0" * 64), "tool", "sha256")
        self.assertEqual(self.launches(), [])


class NativeDecodeTests(Tmp):
    def test_cli_output_is_streamed_with_the_proven_arguments(self):
        data, rep = self.native(frame(P) + frame(b"abcdefghij", width=1), 1034)
        self.assertEqual((data, rep and rep["exit"]), (P + b"abcdefghij", 0))
        self.assertEqual(self.launches(), [["--decompress", "--stdout", "--no-progress", "-M2MB", "--",
                                            str(self.tmp / "frames.bin")]])


class NativeExtraTests(Tmp):
    def test_extra_output_byte_is_refused(self):
        self.mode("extra")
        self.refused(lambda: self.native(frame(P), 1024), "native", "exceeds plan")


class NativeShortTests(Tmp):
    def test_short_output_is_refused(self):
        self.mode("short")
        self.refused(lambda: self.native(frame(P), 1024), "native", "short output")


class NativeStderrTests(Tmp):
    def test_any_stderr_is_refused_even_with_exit_0(self):
        self.mode("stderr")
        self.refused(lambda: self.native(frame(P), 1024), "native", "stderr")


class NativeExitTests(Tmp):
    def test_nonzero_exit_is_refused(self):
        self.mode("exit1")
        self.refused(lambda: self.native(frame(P), 1024), "native", "exit 1")


class NativeTimeoutTests(Tmp):
    def test_timeout_kills_the_cli_and_is_a_distinct_gate(self):
        self.mode("sleep")
        self.refused(lambda: self.native(frame(P), 1024, timeout=1), "native-timeout", "timeout")


class ExtractInterleaveTests(Tmp):
    def test_kind0_literals_keep_their_place_around_cli_output(self):
        data = b0(b"AAAA") + b15(P) + b0(b"BB")
        rep = self.extract(self.span(data), b"AAAA" + P + b"BB")
        self.assertEqual(self.out.read_bytes() if self.out.is_file() else None, b"AAAA" + P + b"BB")
        self.assertEqual({k: rep[k] for k in ("blocks_by_kind", "decoded_size", "launches", "exit_codes")},
                         {"blocks_by_kind": {"0": 2, "15": 1}, "decoded_size": 1030, "launches": 1, "exit_codes": [0]})


class ExtractLiteralOnlyTests(Tmp):
    def test_literal_only_span_never_launches_the_cli(self):
        data = b0(b"x" * 65536) + b0(b"y" * 65537)
        self.extract(self.span(data), b"x" * 65536 + b"y" * 65537)
        self.assertEqual((self.out.read_bytes(), self.launches()), (b"x" * 65536 + b"y" * 65537, []))


class ExtractSha1Tests(Tmp):
    def test_stored_sha1_mismatch_is_unsupported_build_without_launch(self):
        span = self.span(b15(P), sha1="0" * 40)
        self.refused(lambda: self.extract(span, P), "stored-identity", "unsupported FIFA 17 build")
        self.assertEqual(self.launches(), [])


class ExtractSpanIdTests(Tmp):
    def test_refusals_name_the_span(self):
        self.refused(lambda: self.extract(self.span(b15(P), sha1="0" * 40), P), "stored-identity", "span=syn")


class ExtractToolFirstTests(Tmp):
    def test_tool_hash_is_checked_before_reading_the_game(self):
        span = self.span(b15(P), sha1="0" * 40)
        self.refused(lambda: self.extract(span, P, pin="1" * 64), "tool", "sha256")


class ExtractOutputExistsTests(Tmp):
    def test_existing_output_is_never_overwritten(self):
        (self.tmp / "out").mkdir()
        (self.tmp / "out" / "o.bin").write_bytes(b"keep")
        self.refused(lambda: self.extract(self.span(b15(P)), P), "output", "overwrite")
        self.assertEqual((self.tmp / "out" / "o.bin").read_bytes(), b"keep")


class ExtractBoundsTests(Tmp):
    def test_span_past_end_of_file_is_a_physical_refusal(self):
        span = self.span(b15(P))
        span["offset"] += 10
        self.refused(lambda: self.extract(span, P), "physical", "shorter than")


class ExtractMissingFileTests(Tmp):
    def test_missing_cas_file_is_a_physical_refusal(self):
        span = self.span(b15(P))
        span["path"] = "Data/Win32/x/cas_99.cas"
        self.refused(lambda: self.extract(span, P), "physical", "missing")


class ExtractPrevalidateTests(Tmp):
    def test_a_late_bad_envelope_refuses_before_any_launch(self):
        data = b15(P) + b15(P, fcs=1000)
        self.refused(lambda: self.extract(self.span(data), P + P), "envelope", "content size")
        self.assertEqual(self.launches(), [])


class ExtractNativeFailureTests(Tmp):
    def test_native_failure_keeps_no_output(self):
        rle = frame(b"", width=1, fcs=5, body=inner(2, 5) + b"Z")
        self.refused(lambda: self.extract(self.span(hdr(15, 5, len(rle)) + rle), b"ZZZZZ"), "native", "exit 1")
        self.assertEqual(list((self.tmp / "out").iterdir()), [])


class ExtractFinalHashTests(Tmp):
    def test_decoded_sha256_mismatch_is_refused_and_nothing_kept(self):
        self.refused(lambda: self.extract(self.span(b15(P)), P, sha="0" * 64), "final", "sha256")
        self.assertEqual(list((self.tmp / "out").iterdir()), [])


class ExtractCleanTests(Tmp):
    def test_work_dir_is_emptied_and_the_game_file_untouched(self):
        span = self.span(b0(b"AAAA") + b15(P))
        game = self.tmp / "FIFA 17" / span["path"]
        before = game.read_bytes()
        self.extract(span, b"AAAA" + P)
        self.refused(lambda: self.extract(span, b"AAAA" + P, sha="0" * 64, name="p.bin"), "final", "sha256")
        self.assertEqual((list(self.work.iterdir()), game.read_bytes()), ([], before))


class TimeoutForTests(unittest.TestCase):
    def test_bank_gets_180s_and_small_spans_10s(self):
        self.assertEqual((cas.timeout_for({"role": "bank"}), cas.timeout_for({"role": "mesh-res"})), (180, 10))


class CliExtractTests(Tmp):
    def tree(self, spans):
        from tests.test_fm26vm_recipe import fake_tree
        root, obj = fake_tree(self.tmp / "g")
        (root / "FIFA17.exe").write_bytes(b"MZ")
        rel = "Data/Win32/superbundlelayout/p/cas_01.cas"
        blob, obj["spans"] = b"HEAD", []
        for i, (data, decoded, role) in enumerate(spans):
            obj["spans"].append({"id": f"s{i}", "role": role, "asset": "content/x", "path": rel, "offset": len(blob),
                                 "size": len(data), "sha1": hashlib.sha1(data).hexdigest(), "decoded_size": len(decoded),
                                 "decoded_sha256": hashlib.sha256(decoded).hexdigest()})
            blob += data
        (root / rel).write_bytes(blob + b"TAIL")
        rec = self.tmp / "recipe.json"
        rec.write_text(json.dumps(obj), encoding="utf-8")
        tools = self.tmp / "fm26-visual-mod" / "tools" / "zstd-1.5.7" / "z"
        tools.mkdir(parents=True)
        (tools / "zstd.py").write_bytes(self.fake.read_bytes())
        return root, rec

    def cli(self, args):
        from tools.fm26vm import cli
        out = io.StringIO()
        pins = {"zstd": {"folder": "zstd-1.5.7", "checks": {"z/zstd.py": self.pin}}}
        try:
            code = cli.main(args, out=out, registry=lambda *a: None, fm_running=lambda: False,
                            env={"LOCALAPPDATA": str(self.tmp)}, pins=pins, launcher=(sys.executable,))
        except SystemExit as e:
            code = f"exit {e.code}"
        except Exception as e:  # noqa: BLE001 - an uncaught crash is a failed expectation, not a test error
            code = f"raised {e!r}"
        return code, out.getvalue()

    def test_extract_spans_writes_every_span_and_a_report(self):
        root, rec = self.tree([(b0(b"AAAA") + b15(P), b"AAAA" + P, "bank"), (b0(b"zz"), b"zz", "mesh-ebx")])
        dest = self.tmp / "dest"
        code, text = self.cli(["extract-spans", "--fifa17", str(root), "--recipe", str(rec), "--out", str(dest)])
        self.assertEqual(code, 0, text)
        self.assertEqual(sorted(p.name for p in dest.iterdir()), ["s0.bin", "s1.bin"])
        self.assertEqual(((dest / "s0.bin").read_bytes(), (dest / "s1.bin").read_bytes()), (b"AAAA" + P, b"zz"))
        self.assertEqual([s["span_id"] for s in json.loads(text)["spans"]], ["s0", "s1"])

    def test_output_inside_the_fifa_folder_is_refused(self):
        root, rec = self.tree([(b0(b"zz"), b"zz", "mesh-ebx")])
        dest = root / "Data" / "x"
        code, text = self.cli(["extract-spans", "--fifa17", str(root), "--recipe", str(rec), "--out", str(dest)])
        self.assertEqual((code, "inside the FIFA 17 folder" in text, dest.exists()), (1, True, False), text)

    def test_missing_zstd_points_to_fetch_tools(self):
        root, rec = self.tree([(b15(P), P, "mesh-res")])
        (self.tmp / "fm26-visual-mod" / "tools" / "zstd-1.5.7" / "z" / "zstd.py").unlink()
        dest = self.tmp / "dest"
        code, text = self.cli(["extract-spans", "--fifa17", str(root), "--recipe", str(rec), "--out", str(dest)])
        self.assertEqual((code, "fetch-tools --only zstd" in text), (1, True), text)

    def test_a_failing_span_exits_1_and_keeps_no_output(self):
        root, rec = self.tree([(b0(b"zz"), b"zz", "mesh-ebx"), (b15(P), P, "mesh-res")])
        obj = json.loads(rec.read_text(encoding="utf-8"))
        obj["spans"][1]["decoded_sha256"] = "0" * 64
        rec.write_text(json.dumps(obj), encoding="utf-8")
        dest = self.tmp / "dest"
        code, text = self.cli(["extract-spans", "--fifa17", str(root), "--recipe", str(rec), "--out", str(dest)])
        self.assertEqual((code, "error:" in text, "gate=final" in text), (1, True, True), text)
        self.assertEqual(list(dest.iterdir()) if dest.exists() else [], [])


    def test_invalid_recipe_is_an_error_line_not_a_traceback(self):
        root, rec = self.tree([(b0(b"zz"), b"zz", "mesh-ebx")])
        obj = json.loads(rec.read_text(encoding="utf-8"))
        obj["bogus"] = 1
        rec.write_text(json.dumps(obj), encoding="utf-8")
        code, text = self.cli(["extract-spans", "--fifa17", str(root), "--recipe", str(rec), "--out", str(self.tmp / "dest")])
        self.assertEqual((code, text.startswith("error:")), (1, True), text)


    def test_missing_fifa_is_an_error_line_not_a_traceback(self):
        root, rec = self.tree([(b0(b"zz"), b"zz", "mesh-ebx")])
        code, text = self.cli(["extract-spans", "--fifa17", str(self.tmp / "nope"), "--recipe", str(rec), "--out", str(self.tmp / "dest")])
        self.assertEqual((code, text.startswith("error:")), (1, True), text)


if __name__ == "__main__":
    unittest.main()
