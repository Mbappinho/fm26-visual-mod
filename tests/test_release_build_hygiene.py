# SPDX-License-Identifier: MIT
"""Release builds of the plugin must be reproducible and must not embed any build-machine path.

The published FM26.VisualDiagnostics.dll is compared byte-for-byte (sha256) with tools/fm26vm/release.json, so the
csproj has to make the compiler output independent of the checkout directory and of the clock:
  * Deterministic + ContinuousIntegrationBuild: no timestamp/random MVID, normalised source paths;
  * PathMap $(MSBuildProjectDirectory)=/_/: any remaining source path (e.g. CallerFilePath) is rewritten;
  * DebugType none: no CodeView debug directory, hence no absolute .pdb path in the PE file;
  * no source-revision suffix in AssemblyInformationalVersion (a git checkout and a tarball build the same bytes).
"""
import re
import unittest
import xml.etree.ElementTree as ET
from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]
CSPROJ = ROOT / "FM26.VisualDiagnostics.csproj"


def unconditional_properties():
    """Properties set in PropertyGroups without a Condition (last one wins, as in MSBuild)."""
    props = {}
    for group in ET.parse(CSPROJ).getroot().iter("PropertyGroup"):
        if group.get("Condition"):
            continue
        for el in group:
            if not el.get("Condition"):
                props[el.tag] = (el.text or "").strip()
    return props


class ReleaseBuildHygiene(unittest.TestCase):
    def test_it(self):
        p = unconditional_properties()
        self.assertEqual(p.get("Deterministic", "").lower(), "true")
        self.assertEqual(p.get("ContinuousIntegrationBuild", "").lower(), "true")
        self.assertEqual(p.get("DebugType", "").lower(), "none")
        self.assertEqual(p.get("DebugSymbols", "").lower(), "false")
        self.assertEqual(p.get("IncludeSourceRevisionInInformationalVersion", "").lower(), "false")
        self.assertRegex(p.get("PathMap", ""), r"^\$\(MSBuildProjectDirectory\)=/_/$")

    def test_no_absolute_default_paths_except_documented(self):
        # Only the game interop default may name a location; the loader default stays project-relative.
        text = CSPROJ.read_text(encoding="utf-8")
        self.assertRegex(text, r"<LoaderDir [^>]*>\$\(MSBuildProjectDirectory\)/")
        self.assertNotRegex(text, r"(?i)\\Users\\|/Users/")


if __name__ == "__main__":
    unittest.main()
