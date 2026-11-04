# SPDX-License-Identifier: MIT
"""fm26vm B1: VDF parser, Steam/FIFA 17 detection, state path, CLI (temp fixtures only, no real registry)."""
import io
import json
import os
import sys
import tempfile
import unittest
from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]
if str(ROOT) not in sys.path:
    sys.path.insert(0, str(ROOT))

from tools.fm26vm import cli, detect, state, vdf  # noqa: E402


def touch(path, data=b"x"):
    path = Path(path)
    path.parent.mkdir(parents=True, exist_ok=True)
    path.write_bytes(data)
    return path


def no_registry(hive, key, value):
    return None


class Tmp(unittest.TestCase):
    def setUp(self):
        self._tmp = tempfile.TemporaryDirectory()
        self.tmp = Path(self._tmp.name)

    def tearDown(self):
        self._tmp.cleanup()


class VdfNestedTests(unittest.TestCase):
    def test_parses_nested_quoted_blocks(self):
        text = '"libraryfolders"\n{\n\t"0"\n\t{\n\t\t"path"\t\t"D:/Games"\n\t\t"label"\t\t""\n\t}\n}\n'
        self.assertEqual(vdf.parse(text), {"libraryfolders": {"0": {"path": "D:/Games", "label": ""}}})


class VdfEscapeTests(unittest.TestCase):
    def test_unescapes_backslashes_and_quotes(self):
        text = '"a" { "path" "C:\\\\Program Files (x86)\\\\Steam" "name" "say \\"hi\\"" }'
        self.assertEqual(vdf.parse(text), {"a": {"path": "C:\\Program Files (x86)\\Steam", "name": 'say "hi"'}})


class VdfUnterminatedTests(unittest.TestCase):
    def test_unterminated_block_is_refused(self):
        with self.assertRaises(vdf.VdfError):
            vdf.parse('"AppState" { "installdir" "Football Manager 26"')


class VdfCommentTests(unittest.TestCase):
    def test_line_comments_are_ignored(self):
        text = '// header "x"\n"a"\n{\n\t"k"\t"v" // trailing "y"\n}\n'
        self.assertEqual(vdf.parse(text), {"a": {"k": "v"}})


def norm(paths):
    return [os.path.normcase(os.path.normpath(str(p))) for p in paths]


def write_libraryfolders(root, libs):
    body = "".join('\t"%d"\n\t{\n\t\t"path"\t\t"%s"\n\t\t"apps"\n\t\t{\n\t\t}\n\t}\n'
                   % (i, str(p).replace("\\", "\\\\")) for i, p in enumerate(libs))
    touch(Path(root) / "steamapps" / "libraryfolders.vdf", ('"libraryfolders"\n{\n' + body + '}\n').encode())


class LibraryPathsTests(Tmp):
    def test_lists_root_first_then_other_libraries_once(self):
        root, lib2 = self.tmp / "Steam", self.tmp / "Games Lib"
        write_libraryfolders(root, [root, lib2])
        self.assertEqual(norm(detect.library_paths(root)), norm([root, lib2]))


class LibraryMissingVdfTests(Tmp):
    def test_missing_libraryfolders_means_root_only(self):
        root = self.tmp / "Steam"
        try:
            got = detect.library_paths(root)
        except OSError as e:
            self.fail(f"missing libraryfolders.vdf must not raise: {e!r}")
        self.assertEqual(norm(got), norm([root]))


class LibraryOldFormatTests(Tmp):
    def test_legacy_string_entries_are_libraries(self):
        root, lib2 = self.tmp / "Steam", self.tmp / "Old Lib"
        text = '"LibraryFolders"\n{\n\t"TimeNextStatsReport"\t\t"123"\n\t"ContentStatsID"\t\t"-1"\n\t"1"\t\t"%s"\n}\n' % (
            str(lib2).replace("\\", "\\\\"))
        touch(root / "steamapps" / "libraryfolders.vdf", text.encode())
        try:
            got = detect.library_paths(root)
        except Exception as e:  # noqa: BLE001 - a crash is the failure under test
            self.fail(f"legacy format must parse: {e!r}")
        self.assertEqual(norm(got), norm([root, lib2]))


def make_fm(folder, files=("fm.exe", "GameAssembly.dll")):
    for f in files:
        touch(Path(folder) / f)
    return Path(folder)


def make_steam_with_fm(tmp, installdir="Football Manager 26", files=("fm.exe", "GameAssembly.dll")):
    root, lib2 = tmp / "Steam", tmp / "Lib 2"
    write_libraryfolders(root, [root, lib2])
    touch(lib2 / "steamapps" / "appmanifest_3551340.acf",
          ('"AppState"\n{\n\t"appid"\t\t"3551340"\n\t"name"\t\t"Football Manager 26"\n\t"installdir"\t\t"%s"\n}\n'
           % installdir).encode())
    return root, make_fm(lib2 / "steamapps" / "common" / installdir, files)


class FindFmTests(Tmp):
    def test_finds_install_through_manifest_in_second_library(self):
        root, fm = make_steam_with_fm(self.tmp)
        self.assertEqual(norm([detect.find_fm(steam=root, registry=no_registry)]), norm([fm]))


class FindFmValidationTests(Tmp):
    def test_install_without_game_assembly_is_refused(self):
        root, _ = make_steam_with_fm(self.tmp, files=("fm.exe",))
        with self.assertRaises(detect.DetectError) as ctx:
            detect.find_fm(steam=root, registry=no_registry)
        self.assertIn("GameAssembly.dll", str(ctx.exception))


class FindFmNoManifestTests(Tmp):
    def test_no_manifest_in_any_library_is_refused(self):
        root = self.tmp / "Steam"
        write_libraryfolders(root, [root, self.tmp / "Lib 2"])
        with self.assertRaises(detect.DetectError) as ctx:
            detect.find_fm(steam=root, registry=no_registry)
        self.assertIn("3551340", str(ctx.exception))


class FindFmOverrideTests(Tmp):
    def test_fm_override_wins_over_steam(self):
        root, _ = make_steam_with_fm(self.tmp)
        other = make_fm(self.tmp / "Other FM")
        self.assertEqual(norm([detect.find_fm(steam=root, fm=other, registry=no_registry)]), norm([other]))


class FindFmOverrideValidationTests(Tmp):
    def test_invalid_fm_override_is_refused_even_with_valid_steam(self):
        root, _ = make_steam_with_fm(self.tmp)
        bad = make_fm(self.tmp / "Bad FM", files=("GameAssembly.dll",))
        with self.assertRaises(detect.DetectError) as ctx:
            detect.find_fm(steam=root, fm=bad, registry=no_registry)
        self.assertIn("fm.exe", str(ctx.exception))


class SteamRegistryTests(unittest.TestCase):
    def test_steam_root_from_hkcu_steampath(self):
        seen = []

        def registry(hive, key, value):
            seen.append((hive, key, value))
            return "c:/steam" if (hive, key, value) == ("HKCU", r"Software\Valve\Steam", "SteamPath") else None

        self.assertEqual(norm([detect.steam_root(None, registry)]), norm([Path("c:/steam")]))


class NoSteamTests(unittest.TestCase):
    def test_no_steam_and_no_override_is_a_detect_error(self):
        try:
            detect.find_fm(steam=None, registry=no_registry)
        except detect.DetectError as e:
            self.assertIn("--steam", str(e))
            return
        except Exception as e:  # noqa: BLE001 - wrong exception type is the failure under test
            self.fail(f"expected DetectError, got {e!r}")
        self.fail("expected DetectError, got a result")


def make_fifa(folder, exe=True, data=True):
    folder = Path(folder)
    folder.mkdir(parents=True, exist_ok=True)
    if exe:
        touch(folder / "FIFA17.exe")
    if data:
        (folder / "Data").mkdir(exist_ok=True)
    return folder


class FifaOverrideTests(Tmp):
    def test_valid_fifa17_override_is_returned(self):
        fifa = make_fifa(self.tmp / "FIFA 17")
        self.assertEqual(norm([detect.find_fifa17(fifa17=fifa, registry=no_registry, candidates=())]), norm([fifa]))


class FifaOverrideValidationTests(Tmp):
    def test_override_without_data_folder_is_refused(self):
        fifa = make_fifa(self.tmp / "FIFA 17", data=False)
        with self.assertRaises(detect.DetectError) as ctx:
            detect.find_fifa17(fifa17=fifa, registry=no_registry, candidates=())
        self.assertIn("Data/", str(ctx.exception))


class FifaRegistryTests(Tmp):
    def test_registry_install_dir_is_used(self):
        fifa = make_fifa(self.tmp / "EA Games" / "FIFA 17")

        def registry(hive, key, value):
            if (hive, key, value) == ("HKLM", r"SOFTWARE\WOW6432Node\EA Sports\FIFA 17", "Install Dir"):
                return str(fifa) + "\\"
            return None

        self.assertEqual(norm([detect.find_fifa17(registry=registry, candidates=())]), norm([fifa]))


class FifaRegistryInvalidTests(Tmp):
    def test_invalid_registry_folder_falls_through_to_next_candidate(self):
        stale = self.tmp / "Uninstalled FIFA 17"
        fifa = make_fifa(self.tmp / "FIFA 17")

        def registry(hive, key, value):
            return str(stale) if value == "Install Dir" else None

        self.assertEqual(norm([detect.find_fifa17(registry=registry, candidates=(stale, fifa))]), norm([fifa]))


class FifaNotFoundTests(Tmp):
    def test_nothing_valid_is_a_detect_error_listing_tried_folders(self):
        exe_only = make_fifa(self.tmp / "Exe Only", data=False)
        with self.assertRaises(detect.DetectError) as ctx:
            detect.find_fifa17(registry=no_registry, candidates=(exe_only, self.tmp / "Nowhere"))
        self.assertIn("--fifa17", str(ctx.exception))
        self.assertIn("Exe Only", str(ctx.exception))


class DetectTests(Tmp):
    def test_detect_returns_both_install_folders(self):
        root, fm = make_steam_with_fm(self.tmp)
        fifa = make_fifa(self.tmp / "FIFA 17")
        got = detect.detect(steam=root, registry=no_registry, fifa_candidates=(fifa,), fm_running=lambda: False)
        self.assertEqual(norm([got.get("fm"), got.get("fifa17")]), norm([fm, fifa]))


class DetectRunningTests(Tmp):
    def test_refuses_while_fm_is_running(self):
        root, _ = make_steam_with_fm(self.tmp)
        fifa = make_fifa(self.tmp / "FIFA 17")
        with self.assertRaises(detect.DetectError) as ctx:
            detect.detect(steam=root, registry=no_registry, fifa_candidates=(fifa,), fm_running=lambda: True)
        self.assertIn("running", str(ctx.exception))


class StatePathTests(Tmp):
    def test_state_file_under_localappdata(self):
        got = state.state_path({"LOCALAPPDATA": str(self.tmp)})
        self.assertEqual(norm([got]), norm([self.tmp / "fm26-visual-mod" / "state.json"]))


class FakeWinreg:
    HKEY_CURRENT_USER, HKEY_LOCAL_MACHINE, KEY_READ = "HKCU-handle", "HKLM-handle", 0x20019

    def __init__(self, values):
        self.values, self.opened = values, []

    def OpenKey(self, hive, key, reserved=0, access=None):
        self.opened.append((hive, key, access))
        if (hive, key) not in {(h, k) for h, k, _ in self.values}:
            raise FileNotFoundError(key)
        return (hive, key)

    def QueryValueEx(self, handle, value):
        if (handle[0], handle[1], value) not in self.values:
            raise FileNotFoundError(value)
        return self.values[(handle[0], handle[1], value)], 1

    def CloseKey(self, handle):
        pass


class RegistryAdapterTests(unittest.TestCase):
    def test_reads_value_read_only_and_maps_missing_to_none(self):
        fake = FakeWinreg({("HKCU-handle", r"Software\Valve\Steam", "SteamPath"): "c:/steam"})
        read = detect.registry_reader(fake)
        self.assertEqual(read("HKCU", r"Software\Valve\Steam", "SteamPath"), "c:/steam")
        self.assertIsNone(read("HKLM", r"SOFTWARE\Nope", "Install Dir"))
        self.assertIsNone(read("HKCU", r"Software\Valve\Steam", "Missing"))
        self.assertTrue(all(access == fake.KEY_READ for _, _, access in fake.opened))


class ProcessAdapterTests(unittest.TestCase):
    def test_tasklist_csv_detects_fm_exe_only(self):
        class Done:
            def __init__(self, out):
                self.stdout, self.returncode = out, 0

        running = detect.fm_running_checker(lambda *a, **k: Done('"fm.exe","1234","Console","1","2 000 Ko"\r\n'))
        absent = detect.fm_running_checker(lambda *a, **k: Done('INFO: No tasks are running which match the criteria.\r\n'))
        other = detect.fm_running_checker(lambda *a, **k: Done('"fmx.exe","1","Console","1","1 Ko"\r\n'))
        self.assertEqual((running(), absent(), other()), (True, False, False))


class CliDetectTests(Tmp):
    def test_detect_prints_json_and_exits_zero(self):
        root, fm = make_steam_with_fm(self.tmp)
        fifa = make_fifa(self.tmp / "FIFA 17")
        out = io.StringIO()
        code = cli.main(["detect", "--steam", str(root), "--fifa17", str(fifa)], registry=no_registry,
                        fm_running=lambda: False, env={"LOCALAPPDATA": str(self.tmp)}, fifa_candidates=(), out=out)
        self.assertEqual(code, 0)
        self.assertTrue(out.getvalue().strip(), "detect printed nothing")
        got = json.loads(out.getvalue())
        self.assertEqual(norm([got["fm"], got["fifa17"]]), norm([fm, fifa]))


class CliDetectErrorTests(Tmp):
    def test_detect_error_exits_one_with_message(self):
        out = io.StringIO()
        try:
            code = cli.main(["detect", "--fm", str(self.tmp / "missing")], registry=no_registry, fm_running=lambda: False,
                            env={"LOCALAPPDATA": str(self.tmp)}, fifa_candidates=(), out=out)
        except detect.DetectError as e:
            self.fail(f"CLI must report, not raise: {e!r}")
        self.assertEqual(code, 1)
        self.assertIn("fm.exe", out.getvalue())


class CliNotImplementedTests(Tmp):
    def test_future_subcommands_exit_two(self):
        for command in (["verify"], ["rebuild-pack"]):
            out = io.StringIO()
            try:
                code = cli.main(command, registry=no_registry, fm_running=lambda: False,
                                env={"LOCALAPPDATA": str(self.tmp)}, fifa_candidates=(), out=out)
            except SystemExit as e:
                self.fail(f"{command}: argparse rejected the subcommand ({e.code})")
            self.assertEqual((command, code), (command, 2))
            self.assertIn("not implemented yet", out.getvalue())
        self.assertEqual(list(self.tmp.iterdir()), [])


class CliStatusDryRunTests(Tmp):
    def test_status_dry_run_reports_state_path_and_writes_nothing(self):
        out = io.StringIO()
        code = cli.main(["status", "--dry-run"], registry=no_registry, fm_running=lambda: False,
                        env={"LOCALAPPDATA": str(self.tmp)}, fifa_candidates=(), out=out)
        self.assertEqual(code, 0)
        got = json.loads(out.getvalue())
        self.assertEqual(norm([got["state_path"]]), norm([self.tmp / "fm26-visual-mod" / "state.json"]))
        self.assertEqual((got["state_exists"], got["dry_run"]), (False, True))
        self.assertEqual(list(self.tmp.iterdir()), [])


class ProcessAdapterBytesTests(unittest.TestCase):
    """Real run on a French Windows: tasklist prints OEM code-page bytes (0xFF = NBSP in '2 000 Ko'); text=True decoding
    as UTF-8 crashed the reader thread, stdout became None and the check silently answered 'not running'."""

    def test_non_utf8_tasklist_bytes_still_detect_fm(self):
        class Done:
            stdout, returncode = b'"fm.exe","1234","Console","1","2\xff000 Ko"\r\n', 0

        try:
            running = detect.fm_running_checker(lambda *a, **k: Done())()
        except Exception as e:  # noqa: BLE001 - a crash is the failure under test
            self.fail(f"tasklist bytes must be decoded: {e!r}")
        self.assertTrue(running)
