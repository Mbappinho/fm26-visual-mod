# SPDX-License-Identifier: MIT
"""fm26vm B7: clean-room VBR decoder tools/fm26vm/vbr.py (moved from H147/H149; characterization).

Expected hashes were computed by local/spike-224-b7-retarget/gen_char.py with the ORIGINAL spike-149 vbr_decoder.py on
the SYNTHETIC clip of tests/fixtures/fm26vm/b7_synth.py (no game bytes)."""
import hashlib
import importlib.util
import json
import sys
import unittest
from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]
if str(ROOT) not in sys.path:
    sys.path.insert(0, str(ROOT))

from tools.fm26vm import vbr  # noqa: E402

_spec = importlib.util.spec_from_file_location("fm26vm_b7_synth", ROOT / "tests" / "fixtures" / "fm26vm" / "b7_synth.py")
S = importlib.util.module_from_spec(_spec)
_spec.loader.exec_module(S)


def canon_sha(obj):
    return hashlib.sha256(json.dumps(obj, sort_keys=True, separators=(",", ":")).encode("ascii")).hexdigest()


class T_c01_decode_characterization(unittest.TestCase):
    def test_it(self):
        f, data, fbs, pal = S.vbr_clip()
        got = {n: canon_sha(vbr.decode(dict(f), data, fbs, pal, norm=n)) for n in ("F1", "F2", "F3")}
        self.assertEqual(got, {"F1": "198120eca59e8b0053285de4182f509353b9bf5ea39e1549b49274db490f8452",
                               "F2": "a422b0009fe03f2703d10e03863c88a45084a83a3e61026cb184e2d5f4d525c3",
                               "F3": "15843a8bd064619f1d95891d0d6741722549548952e543724a0834acd51d067a"})

    def test_header_gate_and_refusals(self):
        f, data, fbs, pal = S.vbr_clip()
        self.assertFalse(vbr.decode(dict(f), data + b"\0", fbs, pal, norm="F2")["G0_pass"])
        with self.assertRaises(ValueError):
            vbr.decode(dict(f), data, fbs, pal, norm="F9")
        g = dict(f, ConstChanMapSize=0)
        with self.assertRaisesRegex(ValueError, "ConstChanMapSize=0"):
            vbr.decode(g, data[:5] + data[7:], fbs, pal, norm="F2")
