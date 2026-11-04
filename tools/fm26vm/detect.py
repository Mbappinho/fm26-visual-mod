# SPDX-License-Identifier: MIT
"""Read-only detection of FM26 (Steam) and FIFA 17 install folders."""
import os
import subprocess
from pathlib import Path

from . import vdf

FM_APPID = "3551340"
FM_CHECK_TIMEOUT_S = 30


FM_FILES = ("fm.exe", "GameAssembly.dll")
FIFA_FILES = ("FIFA17.exe",)
FIFA_DIRS = ("Data",)
# Read-only registry candidates for FIFA 17. All are GUESSES from EA/Origin installer conventions: none of them exists on
# the development machine (its FIFA 17 is found through the default folder), so they are tried first but never trusted
# without the FIFA17.exe + Data/ validation.
FIFA_DEFAULT_FOLDERS = ("C:/Program Files/FIFA 17", "C:/Program Files (x86)/Origin Games/FIFA 17")
FIFA_REGISTRY = (
    ("HKLM", r"SOFTWARE\WOW6432Node\EA Sports\FIFA 17", "Install Dir"),
    ("HKLM", r"SOFTWARE\EA Sports\FIFA 17", "Install Dir"),
    ("HKLM", r"SOFTWARE\WOW6432Node\Electronic Arts\EA Sports\FIFA 17", "Install Dir"),
    ("HKLM", r"SOFTWARE\WOW6432Node\Origin Games\1041264", "InstallDir"),
)


class DetectError(RuntimeError):
    pass


def _valid(folder, files, dirs):
    try:
        _require(folder, files, dirs, "")
    except DetectError:
        return False
    return True


def _require(folder, files, dirs, what):
    folder = Path(folder)
    missing = [f for f in files if not (folder / f).is_file()] + [d + "/" for d in dirs if not (folder / d).is_dir()]
    if missing:
        raise DetectError(f"{what} folder {folder} is missing: {', '.join(missing)}")
    return folder


def registry_reader(winreg_module):
    """Return registry(hive, key, value) -> str|None using read-only KEY_READ opens (never writes)."""
    hives = {"HKCU": winreg_module.HKEY_CURRENT_USER, "HKLM": winreg_module.HKEY_LOCAL_MACHINE}

    def read(hive, key, value):
        try:
            handle = winreg_module.OpenKey(hives[hive], key, 0, winreg_module.KEY_READ)
        except OSError:
            return None
        try:
            return str(winreg_module.QueryValueEx(handle, value)[0])
        except OSError:
            return None
        finally:
            winreg_module.CloseKey(handle)
    return read


def fm_running_checker(run):
    """Return fm_running() -> bool from `tasklist /FI "IMAGENAME eq fm.exe" /FO CSV /NH` (read-only query)."""
    def fm_running():
        try:
            raw = run(["tasklist", "/FI", "IMAGENAME eq fm.exe", "/FO", "CSV", "/NH"],
                      capture_output=True, check=False, timeout=FM_CHECK_TIMEOUT_S).stdout or b""
        except (subprocess.SubprocessError, OSError) as e:
            raise DetectError(f"Could not check whether Football Manager (fm.exe) is running: {e}") from e
        out = raw.decode("ascii", "replace") if isinstance(raw, bytes) else raw
        return any(line.strip().lower().startswith('"fm.exe"') for line in out.splitlines())
    return fm_running


def steam_root(override=None, registry=None):
    if override:
        return Path(override)
    value = registry("HKCU", r"Software\Valve\Steam", "SteamPath") if registry else None
    return Path(value) if value else None


def _same(a, b):
    return os.path.normcase(os.path.normpath(str(a))) == os.path.normcase(os.path.normpath(str(b)))


def library_paths(root):
    root = Path(root)
    out = [root]
    f = root / "steamapps" / "libraryfolders.vdf"
    if not f.is_file():
        return out
    try:
        data = vdf.parse(f.read_text(encoding="utf-8", errors="replace"))
    except vdf.VdfError as e:
        raise DetectError(f"unreadable Steam library list {f}: {e}")
    block = next((v for k, v in data.items() if k.lower() == "libraryfolders" and isinstance(v, dict)), {})
    for key, entry in block.items():
        if isinstance(entry, dict):
            entry = entry.get("path")
        elif not key.isdigit():
            continue
        if not entry:
            continue
        p = Path(entry)
        if not any(_same(p, q) for q in out):
            out.append(p)
    return out


def find_fm(steam=None, fm=None, registry=None):
    if fm:
        return _require(fm, FM_FILES, (), "FM26")
    root = steam_root(steam, registry)
    if root is None:
        raise DetectError("Steam not found (HKCU\\Software\\Valve\\Steam SteamPath); use --steam PATH or --fm PATH")
    for lib in library_paths(root):
        acf = lib / "steamapps" / f"appmanifest_{FM_APPID}.acf"
        if acf.is_file():
            installdir = vdf.parse(acf.read_text(encoding="utf-8", errors="replace"))["AppState"]["installdir"]
            return _require(lib / "steamapps" / "common" / installdir, FM_FILES, (), "FM26")
    raise DetectError(f"FM26 (Steam app {FM_APPID}) not found in the Steam libraries of {root}; use --fm PATH")


def find_fifa17(fifa17=None, registry=None, candidates=()):
    if fifa17:
        return _require(fifa17, FIFA_FILES, FIFA_DIRS, "FIFA 17")
    tried = [registry(*k) if registry else None for k in FIFA_REGISTRY] + list(candidates)
    for found in tried:
        if found and _valid(found, FIFA_FILES, FIFA_DIRS):
            return Path(found)
    listed = "; ".join(str(t) for t in tried if t)
    raise DetectError(f"FIFA 17 not found (needs FIFA17.exe + Data/). Tried: {listed or 'nothing'}. Use --fifa17 PATH")


def detect(steam=None, fm=None, fifa17=None, registry=None, fifa_candidates=(), fm_running=None):
    if fm_running is not None and fm_running():
        raise DetectError("Football Manager (fm.exe) is running: close it first")
    return {"fm": str(find_fm(steam, fm, registry)),
            "fifa17": str(find_fifa17(fifa17, registry, fifa_candidates))}
