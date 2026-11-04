# SPDX-License-Identifier: MIT
"""fm26vm B3: pinned tool acquisition (fake zips + fake opener only; never the network)."""
import hashlib
import io
import json
import os
import stat
import sys
import tempfile
import unittest
import zipfile
from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]
if str(ROOT) not in sys.path:
    sys.path.insert(0, str(ROOT))

from tools.fm26vm import cli, tools_acquire as acq  # noqa: E402

EXE = b"MZ fake zstd executable"


def make_zip(path, members, symlinks=()):
    path = Path(path)
    path.parent.mkdir(parents=True, exist_ok=True)
    with zipfile.ZipFile(path, "w") as z:
        for name, data in members.items():
            info = zipfile.ZipInfo(name)
            if name in symlinks:
                info.external_attr = (stat.S_IFLNK | 0o777) << 16
            z.writestr(info, data)
    return path


def sha(data):
    return hashlib.sha256(data).hexdigest()


def pins_for(zip_path, kind="zstd", checks=None, url="https://example.invalid/fake.zip"):
    return {kind: {"version": "1.5.7", "url": url, "file": "fake.zip", "sha256": sha(Path(zip_path).read_bytes()),
                   "folder": "zstd-1.5.7", "checks": {"z/zstd.exe": sha(EXE)} if checks is None else checks}}


class Tmp(unittest.TestCase):
    def setUp(self):
        self._tmp = tempfile.TemporaryDirectory()
        self.tmp = Path(self._tmp.name)
        self.tools = self.tmp / "tools"
        self.zip = make_zip(self.tmp / "in" / "fake.zip", {"z/zstd.exe": EXE, "z/README.md": b"readme"})

    def tearDown(self):
        self._tmp.cleanup()


class PinTests(unittest.TestCase):
    def test_official_pins(self):
        self.assertEqual(acq.PINS.get("zstd"), {
            "version": "1.5.7",
            "url": "https://github.com/facebook/zstd/releases/download/v1.5.7/zstd-v1.5.7-win64.zip",
            "file": "zstd-v1.5.7-win64.zip",
            "sha256": "acb4e8111511749dc7a3ebedca9b04190e37a17afeb73f55d4425dbf0b90fad9",
            "folder": "zstd-1.5.7",
            "checks": {"zstd-v1.5.7-win64/zstd.exe": "8076aae03feac7c66b319579e82172eed168deed2a3f25e5e2d3c60f55e84111"}})
        self.assertEqual(acq.PINS.get("bepinex"), {
            "version": "6.0.0-be.788",
            "url": "https://builds.bepinex.dev/projects/bepinex_be/788/BepInEx-Unity.IL2CPP-win-x64-6.0.0-be.788%2B5b766a3.zip",  # U1 closed (H247)
            "file": "BepInEx-Unity.IL2CPP-win-x64-6.0.0-be.788+5b766a3.zip",
            "sha256": "f4cc496bd098a0df4164b81e3737297707f13a47c2478dba2f60eefab784817a",
            "folder": "bepinex-6.0.0-be.788",
            "checks": {"BepInEx/core/BepInEx.Core.dll": "1d492eeecd40cd4feb7dc35aff7f08839928ac1637d4993bbde0060de36b7e0a"}})


class DefaultDirTests(unittest.TestCase):
    def test_tools_dir_is_under_localappdata(self):
        self.assertEqual(acq.default_tools_dir({"LOCALAPPDATA": "C:/U/AppData/Local"}),
                         Path("C:/U/AppData/Local") / "fm26-visual-mod" / "tools")


class SuppliedZipTests(Tmp):
    def test_supplied_zip_is_extracted_into_the_tools_folder(self):
        result = acq.acquire("zstd", self.tools, supplied=self.zip, pins=pins_for(self.zip))
        exe = self.tools / "zstd-1.5.7" / "z" / "zstd.exe"
        self.assertTrue(exe.is_file())
        self.assertEqual(exe.read_bytes(), EXE)
        self.assertEqual(result["path"], str(self.tools / "zstd-1.5.7"))


class SuppliedMismatchTests(Tmp):
    def test_hash_mismatch_is_refused_before_extraction(self):
        pins = pins_for(self.zip)
        pins["zstd"]["sha256"] = "0" * 64
        try:
            acq.acquire("zstd", self.tools, supplied=self.zip, pins=pins)
            error = None
        except acq.AcquireError as e:
            error = str(e)
        self.assertIsNotNone(error)
        self.assertIn("0" * 64, error)
        self.assertIn(sha(self.zip.read_bytes()), error)
        self.assertFalse((self.tools / "zstd-1.5.7").exists())


class MemberCheckTests(Tmp):
    def test_extracted_member_hash_mismatch_leaves_no_tool_folder(self):
        pins = pins_for(self.zip, checks={"z/zstd.exe": "1" * 64})
        try:
            acq.acquire("zstd", self.tools, supplied=self.zip, pins=pins)
            error = None
        except acq.AcquireError as e:
            error = str(e)
        self.assertIsNotNone(error)
        self.assertIn("z/zstd.exe", error)
        self.assertEqual(sorted(p.name for p in self.tools.iterdir()) if self.tools.exists() else [], [])


class TraversalTests(Tmp):
    def test_parent_traversal_member_is_refused_and_nothing_written(self):
        bad = make_zip(self.tmp / "in" / "bad.zip", {"z/zstd.exe": EXE, "z/../../evil.txt": b"x"})
        try:
            acq.acquire("zstd", self.tools, supplied=bad, pins=pins_for(bad))
            error = None
        except acq.AcquireError as e:
            error = str(e)
        self.assertIsNotNone(error)
        self.assertIn("evil.txt", error)
        self.assertEqual(list(self.tmp.rglob("evil.txt")), [])
        self.assertFalse((self.tools / "zstd-1.5.7").exists())


class AbsolutePathTests(Tmp):
    def test_absolute_member_paths_are_refused(self):
        accepted = []
        for i, name in enumerate(("/abs.txt", "C:/abs.txt", "\\\\server\\share\\abs.txt")):
            bad = make_zip(self.tmp / "in" / f"abs{i}.zip", {"z/zstd.exe": EXE, name: b"x"})
            try:
                acq.acquire("zstd", self.tmp / f"tools{i}", supplied=bad, pins=pins_for(bad))
                accepted.append(name)
            except acq.AcquireError as e:
                self.assertIn("absolute", str(e))
        self.assertEqual(accepted, [])


class SymlinkTests(Tmp):
    def test_symlink_member_is_refused(self):
        bad = make_zip(self.tmp / "in" / "link.zip", {"z/zstd.exe": EXE, "z/link": b"../../outside"}, symlinks={"z/link"})
        try:
            acq.acquire("zstd", self.tools, supplied=bad, pins=pins_for(bad))
            error = None
        except acq.AcquireError as e:
            error = str(e)
        self.assertIsNotNone(error)
        self.assertIn("symlink", error)
        self.assertFalse((self.tools / "zstd-1.5.7").exists())


class FakeOpener:
    """Stands in for urllib.request.urlopen: records URLs, serves fixed bytes, never touches the network."""
    def __init__(self, data):
        self.data, self.calls = data, []

    def __call__(self, url):
        self.calls.append(url)
        return io.BytesIO(self.data)


class DownloadTests(Tmp):
    def test_download_uses_the_pinned_url_then_extracts(self):
        opener = FakeOpener(self.zip.read_bytes())
        try:  # keep the RED an assertion: the pre-download code path raises TypeError on supplied=None
            acq.acquire("zstd", self.tools, opener=opener, pins=pins_for(self.zip))
        except Exception:
            pass
        self.assertEqual(opener.calls, ["https://example.invalid/fake.zip"])
        self.assertEqual((self.tools / "zstd-1.5.7" / "z" / "zstd.exe").read_bytes(), EXE)
        self.assertEqual((self.tools / "downloads" / "fake.zip").read_bytes(), self.zip.read_bytes())


class DownloadMismatchTests(Tmp):
    def test_downloaded_mismatch_is_refused_and_not_kept(self):
        opener = FakeOpener(b"PK tampered mirror bytes")
        try:
            acq.acquire("zstd", self.tools, opener=opener, pins=pins_for(self.zip))
            error = None
        except acq.AcquireError as e:
            error = str(e)
        self.assertIsNotNone(error)
        self.assertEqual(sorted(p.name for p in (self.tools / "downloads").iterdir()), [])
        self.assertFalse((self.tools / "zstd-1.5.7").exists())


class OfflineTests(Tmp):
    def test_offline_without_a_supplied_zip_never_downloads(self):
        opener = FakeOpener(self.zip.read_bytes())
        try:
            acq.acquire("zstd", self.tools, opener=opener, offline=True, pins=pins_for(self.zip))
            error = None
        except acq.AcquireError as e:
            error = str(e)
        self.assertEqual(opener.calls, [])
        self.assertIsNotNone(error)
        self.assertIn("--zstd", error)


class NoUrlTests(Tmp):
    def test_unpinned_url_requires_a_supplied_zip(self):
        opener = FakeOpener(self.zip.read_bytes())
        pins = pins_for(self.zip, kind="bepinex", checks={}, url=None)
        try:
            acq.acquire("bepinex", self.tools, opener=opener, pins=pins)
            error = None
        except Exception as e:  # pre-GREEN the opener is called with None and nothing raises
            error = str(e) if isinstance(e, acq.AcquireError) else None
        self.assertEqual(opener.calls, [])
        self.assertIsNotNone(error)
        self.assertIn("--bepinex", error)


class HttpsOnlyTests(Tmp):
    def test_non_https_pin_is_refused_without_download(self):
        opener = FakeOpener(self.zip.read_bytes())
        try:
            acq.acquire("zstd", self.tools, opener=opener, pins=pins_for(self.zip, url="http://example.invalid/fake.zip"))
            error = None
        except acq.AcquireError as e:
            error = str(e)
        self.assertEqual(opener.calls, [])
        self.assertIsNotNone(error)
        self.assertIn("https", error)


class IdempotentTests(Tmp):
    def test_second_acquire_reuses_the_verified_folder(self):
        opener = FakeOpener(self.zip.read_bytes())
        pins = pins_for(self.zip)
        first = acq.acquire("zstd", self.tools, opener=opener, pins=pins)
        second = acq.acquire("zstd", self.tools, opener=opener, pins=pins)
        self.assertEqual(len(opener.calls), 1)
        self.assertEqual((first["cached"], second["cached"]), (False, True))
        self.assertEqual(second["path"], first["path"])


class CacheTamperTests(Tmp):
    def test_tampered_cached_tool_is_reinstalled(self):
        pins = pins_for(self.zip)
        acq.acquire("zstd", self.tools, supplied=self.zip, pins=pins)
        exe = self.tools / "zstd-1.5.7" / "z" / "zstd.exe"
        exe.write_bytes(b"modified")
        again = acq.acquire("zstd", self.tools, supplied=self.zip, pins=pins)
        self.assertEqual(exe.read_bytes(), EXE)
        self.assertFalse(again["cached"])


def two_pins(zstd_zip, bep_zip):
    pins = pins_for(zstd_zip)
    pins.update(pins_for(bep_zip, kind="bepinex", checks={}))
    pins["bepinex"]["folder"] = "bep"
    return pins


class CliFetchTests(Tmp):
    def test_fetch_tools_with_supplied_zips_offline(self):
        bep = make_zip(self.tmp / "in" / "bep.zip", {"BepInEx/core/BepInEx.Core.dll": b"core"})
        out = io.StringIO()
        code = cli.main(["fetch-tools", "--zstd", str(self.zip), "--bepinex", str(bep), "--offline"], out=out,
                        registry=lambda *a: None, fm_running=lambda: False, env={"LOCALAPPDATA": str(self.tmp)},
                        pins=two_pins(self.zip, bep), opener=FakeOpener(b""))
        self.assertEqual(code, 0, out.getvalue())
        tools = self.tmp / "fm26-visual-mod" / "tools"
        report = json.loads(out.getvalue())
        self.assertEqual([t["kind"] for t in report["tools"]], ["zstd", "bepinex"])
        self.assertTrue((tools / "bep" / "BepInEx" / "core" / "BepInEx.Core.dll").is_file())
        self.assertTrue((tools / "zstd-1.5.7" / "z" / "zstd.exe").is_file())


class CliFetchErrorTests(Tmp):
    def test_fetch_tools_mismatch_exits_1_with_an_explanation(self):
        bep = make_zip(self.tmp / "in" / "bep.zip", {"BepInEx/core/BepInEx.Core.dll": b"core"})
        pins = two_pins(self.zip, bep)
        pins["bepinex"]["sha256"] = "2" * 64
        out = io.StringIO()
        try:
            code = cli.main(["fetch-tools", "--zstd", str(self.zip), "--bepinex", str(bep), "--offline"], out=out,
                            registry=lambda *a: None, fm_running=lambda: False, env={"LOCALAPPDATA": str(self.tmp)},
                            pins=pins, opener=FakeOpener(b""))
        except acq.AcquireError as e:
            code = f"raised: {e}"
        self.assertEqual(code, 1)
        self.assertIn("error: bepinex", out.getvalue())
        self.assertIn("2" * 64, out.getvalue())


class CliFetchOnlyTests(Tmp):
    def test_fetch_tools_only_zstd_offline_needs_no_bepinex_zip(self):
        out = io.StringIO()
        try:
            code = cli.main(["fetch-tools", "--only", "zstd", "--zstd", str(self.zip), "--offline"], out=out,
                            registry=lambda *a: None, fm_running=lambda: False, env={"LOCALAPPDATA": str(self.tmp)},
                            pins=pins_for(self.zip), opener=FakeOpener(b""))
        except SystemExit as e:
            code = f"exit {e.code}"
        self.assertEqual(code, 0, out.getvalue())
        self.assertEqual([t["kind"] for t in json.loads(out.getvalue())["tools"]], ["zstd"])


class UserAgentTests(Tmp):
    """H247 U1: builds.bepinex.dev (Cloudflare) answers 403 to the default 'Python-urllib/x' User-Agent. Every default
    download (fetch-tools and setup --download) must send an explicit, identifying fm26vm User-Agent."""
    def test_it(self):
        import urllib.request
        from unittest import mock
        seen = []

        def fake_urlopen(req, *a, **k):
            seen.append(req)
            return io.BytesIO(self.zip.read_bytes())
        lad = self.tmp / "lad"
        out = io.StringIO()
        with mock.patch.object(urllib.request, "urlopen", fake_urlopen):
            code = cli.main(["fetch-tools", "--only", "zstd"], out=out, env={"LOCALAPPDATA": str(lad)},
                            pins=pins_for(self.zip))
            cli._ctx({"LOCALAPPDATA": str(lad)}, None, None, (), None, None, None, None).opener(
                "https://example.invalid/setup.zip")
        got = [(r.full_url, r.get_header("User-agent")) if isinstance(r, urllib.request.Request) else (r, None)
               for r in seen]
        bad = [g for g in got if not (g[1] or "").startswith("fm26vm/") or "urllib" in (g[1] or "")]
        self.assertEqual((code, len(got), bad), (0, 2, []), out.getvalue())


@unittest.skipUnless(os.environ.get("FM26VM_REAL_DOWNLOAD") == "1", "opt-in real download (FM26VM_REAL_DOWNLOAD=1)")
class RealDownload(unittest.TestCase):
    """Network, opt-in (H247): the default opener fetches the pinned BepInEx zip into a temp dir; sha256 == pin."""
    def test_it(self):
        pin = acq.PINS["bepinex"]
        with tempfile.TemporaryDirectory() as tmp:
            result = acq.acquire("bepinex", tmp, opener=acq.open_url)
            zip_path = Path(tmp) / "downloads" / pin["file"]
            self.assertEqual((result["cached"], zip_path.stat().st_size, acq._sha256(zip_path)),
                             (False, 34336405, pin["sha256"]))
