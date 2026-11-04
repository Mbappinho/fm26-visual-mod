# SPDX-License-Identifier: MIT
"""Pinned third-party tools (zstd, BepInEx): user-supplied zip or official HTTPS download, sha256 before extraction."""


import hashlib
import json
import os
import shutil
import stat
import urllib.request
import zipfile
from pathlib import Path


PINS = {
    "zstd": {
        "version": "1.5.7",
        "url": "https://github.com/facebook/zstd/releases/download/v1.5.7/zstd-v1.5.7-win64.zip",
        "file": "zstd-v1.5.7-win64.zip",
        "sha256": "acb4e8111511749dc7a3ebedca9b04190e37a17afeb73f55d4425dbf0b90fad9",
        "folder": "zstd-1.5.7",
        "checks": {"zstd-v1.5.7-win64/zstd.exe": "8076aae03feac7c66b319579e82172eed168deed2a3f25e5e2d3c60f55e84111"},
    },
    "bepinex": {
        "version": "6.0.0-be.788",
        # U1 closed (H247): the H215 403 was Cloudflare refusing the default Python-urllib User-Agent; see open_url.
        "url": "https://builds.bepinex.dev/projects/bepinex_be/788/BepInEx-Unity.IL2CPP-win-x64-6.0.0-be.788%2B5b766a3.zip",
        "file": "BepInEx-Unity.IL2CPP-win-x64-6.0.0-be.788+5b766a3.zip",
        "sha256": "f4cc496bd098a0df4164b81e3737297707f13a47c2478dba2f60eefab784817a",
        "folder": "bepinex-6.0.0-be.788",
        "checks": {"BepInEx/core/BepInEx.Core.dll": "1d492eeecd40cd4feb7dc35aff7f08839928ac1637d4993bbde0060de36b7e0a"},
    },
}


# Caps from the pinned zips: zstd 1,747,181 B zip / 4,747,303 B extracted; BepInEx be.788 34,336,405 B zip /
# 75,667,532 B extracted (233 members). ~1.7-1.9x the largest leaves room for nothing else than these tools.
MAX_DOWNLOAD_BYTES = 64 * 1024 * 1024
MAX_EXTRACT_BYTES = 128 * 1024 * 1024
MARKER = ".fm26vm-tool.json"
# H247 U1: builds.bepinex.dev (Cloudflare) answers 403 to the default 'Python-urllib/x' User-Agent.
USER_AGENT = "fm26vm/0.1 (+https://github.com/Mbappinho/fm26-visual-mod)"
RESERVED = {"CON", "PRN", "AUX", "NUL"} | {f"COM{i}" for i in range(1, 10)} | {f"LPT{i}" for i in range(1, 10)}


class AcquireError(RuntimeError):
    pass


def default_tools_dir(env=None):
    env = os.environ if env is None else env
    return Path(env["LOCALAPPDATA"]) / "fm26-visual-mod" / "tools"


def _sha256(path):
    with open(path, "rb") as stream:
        return hashlib.file_digest(stream, "sha256").hexdigest()


def safe_extract(zip_path, dest):
    with zipfile.ZipFile(zip_path) as archive:
        seen = set()
        for info in archive.infolist():
            name = info.filename.replace("\\", "/")
            key = name.rstrip("/").casefold()
            if key in seen:
                raise AcquireError(f"unsafe zip member (duplicate name): {info.filename}")
            seen.add(key)
            if name.startswith("/") or ":" in name:
                raise AcquireError(f"unsafe zip member (absolute path): {info.filename}")
            if stat.S_ISLNK(info.external_attr >> 16):
                raise AcquireError(f"unsafe zip member (symlink): {info.filename}")
            if any(part.split(".")[0].upper() in RESERVED for part in name.split("/")):
                raise AcquireError(f"unsafe zip member (reserved Windows name): {info.filename}")
            if ".." in name.split("/"):  # H229 T2: before the trailing-dot rule, which would also catch '..'
                raise AcquireError(f"unsafe zip member (parent traversal): {info.filename}")
            if any(part and part != "." and part[-1] in ". " for part in name.split("/")):
                raise AcquireError(f"unsafe zip member (name ends with a dot or space): {info.filename}")
        total = sum(info.file_size for info in archive.infolist())
        if total > MAX_EXTRACT_BYTES:
            raise AcquireError(f"zip declares {total} uncompressed bytes, above the {MAX_EXTRACT_BYTES} cap")
        archive.extractall(dest)


def open_url(url, timeout=120):
    """Default opener for the pinned downloads: an explicit identifying User-Agent, never the urllib default."""
    return urllib.request.urlopen(urllib.request.Request(url, headers={"User-Agent": USER_AGENT}), timeout=timeout)


def _download(pin, folder, opener):
    folder.mkdir(parents=True, exist_ok=True)
    target, part = folder / pin["file"], folder / (pin["file"] + ".part")
    size = 0
    try:
        with opener(pin["url"]) as response, open(part, "wb") as out:
            while block := response.read(1 << 20):
                size += len(block)
                if size > MAX_DOWNLOAD_BYTES:
                    break
                out.write(block)
    except OSError as e:  # H229 REM4: URLError/HTTPError/timeouts are OSError
        part.unlink(missing_ok=True)
        raise AcquireError(f"download of {pin['file']} failed: {e}; nothing kept") from e
    if size > MAX_DOWNLOAD_BYTES:
        part.unlink()
        raise AcquireError(f"download of {pin['file']} exceeds the {MAX_DOWNLOAD_BYTES}-byte cap; nothing kept")
    os.replace(part, target)
    return target


def acquire(kind, tools_dir, supplied=None, opener=None, offline=False, pins=None):
    pin = (PINS if pins is None else pins)[kind]
    dest = Path(tools_dir) / pin["folder"]
    marker = Path(tools_dir) / (pin["folder"] + MARKER)  # beside the folder: the folder stays a pristine install stage
    cached = None
    if marker.is_file():
        try:
            cached = json.loads(marker.read_text(encoding="utf-8")).get("zip_sha256")
        except (ValueError, AttributeError) as e:  # H229 REM4
            raise AcquireError(f"{kind}: corrupt tool marker {marker}: delete it and run fetch-tools again ({e})") from e
    if (cached == pin["sha256"]
            and all((dest / m).is_file() and _sha256(dest / m) == h for m, h in pin["checks"].items())):
        return {"kind": kind, "version": pin["version"], "path": str(dest), "cached": True}
    if supplied is not None and not Path(supplied).is_file():
        raise AcquireError(f"{kind}: supplied zip not found: {supplied}")
    if supplied is None and offline:
        raise AcquireError(f"{kind}: offline mode: supply the official zip with --{kind} (pinned sha256 {pin['sha256']})")
    if supplied is None and not pin["url"]:
        raise AcquireError(f"{kind}: no pinned official download URL; supply the zip with --{kind}")
    if supplied is None and not pin["url"].startswith("https://"):
        raise AcquireError(f"{kind}: refusing a non-https download URL: {pin['url']}")
    if supplied is None:
        try:
            source = _download(pin, Path(tools_dir) / "downloads", opener)
        except AcquireError as e:
            raise AcquireError(f"{kind}: {e}. Manual path: download {pin['url']} in a browser and pass the "
                               f"zip with --{kind} ZIP (its sha256 is checked against {pin['sha256']})") from e
    else:
        source = Path(supplied)
    actual = _sha256(source)
    if actual != pin["sha256"]:
        if supplied is None:
            source.unlink()
        raise AcquireError(f"{kind}: refusing {source}: sha256 {actual} does not match the pinned {pin['sha256']} "
                           f"({kind} {pin['version']}); nothing was extracted")
    partial = Path(tools_dir) / (pin["folder"] + ".partial")
    shutil.rmtree(partial, ignore_errors=True)
    safe_extract(source, partial)
    for member, expected in pin["checks"].items():
        got = _sha256(partial / member) if (partial / member).is_file() else "missing"
        if got != expected:
            shutil.rmtree(partial, ignore_errors=True)
            raise AcquireError(f"{kind}: extracted {member} sha256 {got} != pinned {expected}; tool folder not installed")
    if dest.exists():
        shutil.rmtree(dest)
    os.replace(partial, dest)
    marker.write_text(json.dumps({"kind": kind, "version": pin["version"], "zip_sha256": actual}), encoding="utf-8")
    return {"kind": kind, "version": pin["version"], "path": str(dest), "cached": False}
