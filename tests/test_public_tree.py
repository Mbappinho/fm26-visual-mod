# SPDX-License-Identifier: MIT
"""The published tree must stay free of game data, binaries, personal data and secrets (tools/leak_scan.py)."""
import shutil
import subprocess
import unittest
from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]


class PublicTreeIsClean(unittest.TestCase):
    def test_tracked_files_pass_the_leak_scan(self):
        if not (ROOT / ".git").exists() or shutil.which("git") is None:
            self.skipTest("not a git checkout")
        from tools import leak_scan
        report = leak_scan.scan(ROOT, leak_scan.load_terms(None), tracked=True)
        self.assertEqual(report["findings"], [])


if __name__ == "__main__":
    unittest.main()
