# SPDX-License-Identifier: MIT
"""Install only new loader files and archive them for reversible removal."""
import argparse
import datetime
import hashlib
import json
import os
from pathlib import Path
import shutil
import subprocess

ALLOWED_ROOTS = {"BepInEx", "dotnet", "winhttp.dll", "doorstop_config.ini", ".doorstop_version", "changelog.txt"}
AUTO_RELATIVE = "BepInEx/visual-diagnostics/fifa-overlay-auto.json"
PLUGIN_RELATIVE = "BepInEx/plugins/FM26.VisualDiagnostics/FM26.VisualDiagnostics.dll"
REQUIRED_BEPINEX = "6.0.0-be.788"
CRITICAL_FILES = ("fm.exe", "UnityPlayer.dll", "GameAssembly.dll", "fm_Data/boot.config", "fm_Data/il2cpp_data/Metadata/global-metadata.dat")


PRIVATE_PACK_NAMES = {"fifa-pack.json"}
PRIVATE_PACK_SUFFIXES = (".f32le.bin", ".u16le.bin", ".f64le.bin")
# H156 S2: content markers of a private pack header (UTF-8 JSON as written by tools/fifa_pack.py). The plugin DLL holds the
# schema only as UTF-16 metadata, so these byte markers do not match the reviewed plugin itself.
PRIVATE_PACK_MARKERS = (b'"fm26.fifa-pack.v1"', b'"fm26.fifa-pack.v2"', b"PRIVATE_NOT_REDISTRIBUTABLE")  # H171: + v2 schema
_CHUNK = 1 << 20
_MAX_ARCHIVE_DEPTH = 3
_MAX_MEMBER_BYTES = 512 * 1024 * 1024
_MAX_CORE_BYTES = 16 * 1024 * 1024


def local_pack_dirs():
    """Existing local private packs under %LOCALAPPDATA%/fm26-visual-mod/packs/* (read-only discovery)."""
    base = os.environ.get("LOCALAPPDATA")
    root = Path(base) / "fm26-visual-mod" / "packs" if base else None
    if root is None or not root.is_dir():
        return []
    return sorted(p for p in root.iterdir() if p.is_dir() and (p / "fifa-pack.json").is_file())


def _pack_fingerprints(pack_dirs):
    """(size, sha256) of every regular file of each local pack (header + payloads)."""
    prints = set()
    for folder in pack_dirs:
        folder = Path(folder)
        if not folder.is_dir():
            continue
        for item in folder.iterdir():
            if item.is_file() and not item.is_symlink():
                prints.add((item.stat().st_size, digest(item)))
    return prints


def _stream_flags(stream, size, prints):
    """Return a refusal reason for a binary stream (marker bytes or size+sha of a local pack file), else None."""
    tail, hasher = b"", hashlib.sha256() if any(s == size for s, _ in prints) else None
    keep = max(len(m) for m in PRIVATE_PACK_MARKERS) - 1
    while True:
        chunk = stream.read(_CHUNK)
        if not chunk:
            break
        if hasher:
            hasher.update(chunk)
        window = tail + chunk
        if any(m in window for m in PRIVATE_PACK_MARKERS):
            return "pack header marker"
        tail = window[-keep:]
    if hasher and (size, hasher.hexdigest()) in prints:
        return "size+sha256 of a local pack file"
    return None


def _is_archive(head):
    return head[:4] == b"PK" or (len(head) >= 262 and head[257:262] == b"ustar")


def _check_archive(stream, label, prints, depth):
    """Inspect every member of a zip/tar stream (seekable) with the same name/content rules, recursing into nested archives."""
    import tarfile
    import zipfile
    if depth > _MAX_ARCHIVE_DEPTH:
        raise ValueError(f"Refusing to deploy deeply nested archive: {label}")
    stream.seek(0)
    if zipfile.is_zipfile(stream):
        stream.seek(0)
        with zipfile.ZipFile(stream) as archive:
            for info in archive.infolist():
                if not info.is_dir():
                    _check_member(info.filename.replace("\\", "/").rsplit("/", 1)[-1], info.file_size, lambda i=info: archive.open(i),
                                  f"{label}!{info.filename}", prints, depth + 1)
        return
    stream.seek(0)
    with tarfile.open(fileobj=stream) as archive:
        for info in archive.getmembers():
            if info.isfile():
                _check_member(info.name.rsplit("/", 1)[-1], info.size, lambda i=info: archive.extractfile(i), f"{label}!{info.name}", prints, depth + 1)


def _check_member(name, size, opener, label, prints, depth):
    import io
    lower = name.lower()
    if lower in PRIVATE_PACK_NAMES or lower.endswith(PRIVATE_PACK_SUFFIXES):
        raise ValueError(f"Refusing to deploy private FIFA pack data: {label}")
    with opener() as stream:
        reason = _stream_flags(stream, size, prints)
    if reason:
        raise ValueError(f"Refusing to deploy private FIFA pack data ({reason}): {label}")
    with opener() as stream:
        head = stream.read(512)
    if not _is_archive(head):
        return
    if depth == 0:
        with opener() as stream:  # regular file: seekable, inspected in place
            _check_archive(stream, label, prints, depth)
        return
    with opener() as stream:
        data = stream.read(_MAX_MEMBER_BYTES + 1)
    if len(data) > _MAX_MEMBER_BYTES:
        raise ValueError(f"Refusing to deploy an archive member too large to inspect: {label}")
    _check_archive(io.BytesIO(data), label, prints, depth)


def refuse_private_pack(path, pack_dirs=None):
    """Never deploy/bundle a local private FIFA pack (EA-derived data stays in the user's external pack folder).

    H154: refusal by name. H156 S2: also by CONTENT — any file (or archive member, nested archives included) whose bytes
    contain the pack header schema/label marker, or whose size+sha256 equals a file of a local pack (default: discovered
    under %LOCALAPPDATA%/fm26-visual-mod/packs; pass pack_dirs explicitly in tests)."""
    path = Path(path)
    prints = _pack_fingerprints(local_pack_dirs() if pack_dirs is None else pack_dirs)
    candidates = [path] + (sorted(path.rglob("*")) if path.is_dir() else [])
    for item in candidates:
        name = item.name.lower()
        if name in PRIVATE_PACK_NAMES or name.endswith(PRIVATE_PACK_SUFFIXES):
            raise ValueError(f"Refusing to deploy private FIFA pack data: {item}")
    for item in candidates:
        if item.is_file() and not item.is_symlink():
            _check_member(item.name, item.stat().st_size, lambda i=item: i.open("rb"), str(item), prints, 0)


def digest(path):
    with path.open("rb") as stream:
        return hashlib.file_digest(stream, "sha256").hexdigest()


def save_state(path, data):
    path.parent.mkdir(parents=True, exist_ok=True)
    temporary = path.with_suffix(".tmp")
    temporary.write_text(json.dumps(data, indent=2), encoding="utf-8")
    os.replace(temporary, path)


def install(stage, game, state, public=False):
    stage, game, state = Path(stage).resolve(), Path(game).resolve(), Path(state).resolve()
    if state.exists():
        raise FileExistsError(f"Deployment state already exists: {state}")
    if not (game / "fm.exe").is_file():
        raise ValueError("Target is not an FM installation")
    refuse_private_pack(stage)
    for generated in ("BepInEx/interop", "BepInEx/unity-libs"):
        if public and (stage / generated).exists():
            raise ValueError(f"Refusing a public install from a stage containing FM-generated {generated}; "
                             "stage the official BepInEx zip only")
    entries = sorted(stage.iterdir())
    if not entries:
        raise ValueError("Empty loader staging directory")
    for entry in entries:
        if entry.name not in ALLOWED_ROOTS:
            raise ValueError(f"Unexpected loader root: {entry.name}")
        if (game / entry.name).exists():
            raise FileExistsError(f"Refusing to overwrite existing path: {entry.name}")
    for source in stage.rglob("*"):
        if source.is_symlink():
            raise ValueError(f"Symlinks are not allowed: {source}")
    files = [{"path": source.relative_to(stage).as_posix(), "sha256": digest(source)}
             for source in sorted(stage.rglob("*")) if source.is_file()]
    baseline = {name: digest(game / name) for name in CRITICAL_FILES if (game / name).is_file()}
    data = {"game": str(game), "status": "installing", "owned_roots": [entry.name for entry in entries],
            "files": files, "baseline_sha256": baseline,
            "created_at": datetime.datetime.now().astimezone().isoformat()}
    save_state(state, data)
    try:
        for entry in entries:
            target = game / entry.name
            if entry.is_dir():
                shutil.copytree(entry, target)
            else:
                shutil.copy2(entry, target)
        for item in files:
            if digest(game / item["path"]) != item["sha256"]:
                raise RuntimeError(f"Deployment verification failed: {item['path']}")
        data["status"] = "installed"
        save_state(state, data)
        return data
    except Exception:
        data["status"] = "partial-install-needs-rollback"
        save_state(state, data)
        raise


def uninstall(game, state, backup_root, fm_running=None):
    if fm_running is not None and fm_running():
        raise RuntimeError("Football Manager (fm.exe) is running: close it before removal")
    game, state = Path(game).resolve(), Path(state).resolve()
    data = json.loads(state.read_text(encoding="utf-8"))
    if Path(data["game"]).resolve() != game:
        raise ValueError("Game path does not match deployment state")
    if data["status"] == "uninstalled":
        raise ValueError("Already uninstalled")
    if data.get("mode") == "plugin-only":
        if data["owned_dirs"] != [PLUGIN_RELATIVE.rsplit("/", 1)[0]]:
            raise ValueError("Unsafe plugin-only deployment manifest")
        if data.get("auto_json") and data["auto_json"].get("path") != AUTO_RELATIVE:
            raise ValueError("Unsafe plugin-only deployment manifest (auto_json path)")
        moved = data["owned_dirs"] + ([AUTO_RELATIVE] if data.get("auto_json") else [])
        for relative in moved:
            path = game / relative
            if os.path.normcase(os.path.realpath(path)) != os.path.normcase(os.path.normpath(path)):
                raise ValueError(f"Refusing to move a redirected path (symlink/junction): {relative}")
        missing = [relative for relative in data["owned_dirs"] if not (game / relative).is_dir()]
        if missing:
            raise ValueError(f"Owned plugin folder is missing: {', '.join(missing)}; nothing was moved")
        # H229 REM3: a tracked auto json already deleted by the user counts as removed (recorded), never a half-done move
        gone = [relative for relative in moved[len(data["owned_dirs"]):] if not os.path.lexists(game / relative)]
        if gone:
            data["already_removed"] = gone
        backup = Path(backup_root).resolve() / datetime.datetime.now().strftime("rollback-%Y%m%d-%H%M%S-%f")
        for relative in [relative for relative in moved if relative not in gone]:
            (backup / relative).parent.mkdir(parents=True, exist_ok=True)
            shutil.move(str(game / relative), str(backup / relative))
        if data.get("auto_dir_created"):  # H239 M1: only a folder auto-on created, and only if empty
            try:
                os.rmdir(game / AUTO_RELATIVE.rsplit("/", 1)[0])
            except OSError:
                pass
        unchanged = {name: digest(game / name) == expected for name, expected in data["baseline_sha256"].items()}
        data.update(status="uninstalled", backup=str(backup), originals_unchanged=unchanged)
        save_state(state, data)
        if not all(unchanged.values()):
            raise RuntimeError("Original critical files differ from the baseline; inspect deployment state")
        return backup
    roots = data["owned_roots"]
    if not roots or any(name not in ALLOWED_ROOTS for name in roots):
        raise ValueError("Unsafe deployment manifest")
    backup = Path(backup_root).resolve() / datetime.datetime.now().strftime("rollback-%Y%m%d-%H%M%S-%f")
    backup.mkdir(parents=True)
    # Archive the injection proxy first, preventing a subsequent modded startup.
    ordered = sorted(roots, key=lambda name: (name != "winhttp.dll", name))
    for name in ordered:
        path = game / name
        if path.exists():
            shutil.move(str(path), str(backup / name))
    for name in roots:
        if (game / name).exists():
            raise RuntimeError(f"Rollback verification failed: {name}")
    unchanged = {name: digest(game / name) == expected for name, expected in data["baseline_sha256"].items()}
    data.update(status="uninstalled", backup=str(backup), originals_unchanged=unchanged)
    save_state(state, data)
    if not all(unchanged.values()):
        raise RuntimeError("Original critical files differ from the baseline; inspect deployment state")
    return backup


def add_plugin(source, game, state):
    source, game, state = Path(source).resolve(), Path(game).resolve(), Path(state).resolve()
    data = json.loads(state.read_text(encoding="utf-8"))
    if Path(data["game"]).resolve() != game or data["status"] != "installed":
        raise ValueError("Loader is not installed in this target")
    refuse_private_pack(source)
    if source.name != "FM26.VisualDiagnostics.dll":
        raise ValueError("Unexpected plugin filename")
    relative = "BepInEx/plugins/FM26.VisualDiagnostics/" + source.name
    target = game / relative
    if target.exists():
        raise FileExistsError("Refusing to overwrite an existing plugin")
    target.parent.mkdir(parents=True, exist_ok=True)
    shutil.copy2(source, target)
    expected = digest(source)
    if digest(target) != expected:
        raise RuntimeError("Plugin hash mismatch after deployment")
    data["files"].append({"path": relative, "sha256": expected})
    save_state(state, data)
    return target


def update_plugin(source, game, state):
    source, game, state = Path(source).resolve(), Path(game).resolve(), Path(state).resolve()
    data = json.loads(state.read_text(encoding="utf-8"))
    if Path(data["game"]).resolve() != game or data["status"] != "installed":
        raise ValueError("Loader is not installed in this target")
    refuse_private_pack(source)
    if source.name != "FM26.VisualDiagnostics.dll":
        raise ValueError("Unexpected plugin filename")
    relative = "BepInEx/plugins/FM26.VisualDiagnostics/" + source.name
    entries = [item for item in data["files"] if item["path"] == relative]
    target = game / relative
    if len(entries) != 1 or not target.is_file() or digest(target) != entries[0]["sha256"]:
        raise ValueError("Refusing to replace a plugin not matching the owned deployment hash")
    backup_dir = state.parent / "plugin-versions" / datetime.datetime.now().strftime("version-%Y%m%d-%H%M%S-%f")
    backup_dir.mkdir(parents=True)
    backup = backup_dir / source.name
    shutil.copy2(target, backup)
    temporary = target.with_suffix(".staging")
    expected = digest(source)
    shutil.copy2(source, temporary)
    if digest(temporary) != expected:
        temporary.unlink()
        raise RuntimeError("Staged plugin hash mismatch")
    os.replace(temporary, target)
    if digest(target) != expected:
        shutil.copy2(backup, target)
        raise RuntimeError("Updated plugin hash mismatch; previous version restored")
    entries[0]["sha256"] = expected
    data.setdefault("plugin_update_history", []).append({"backup": str(backup), "new_sha256": expected})
    save_state(state, data)
    return backup


def resolve_state(project, env=None):
    """(state file, backup root) used when --state is omitted."""
    project = Path(project)
    legacy = project / "local/install-state.json"
    if legacy.is_file():
        return legacy, project / "local/backups"
    user = Path((os.environ if env is None else env)["LOCALAPPDATA"]) / "fm26-visual-mod"
    return user / "install-state.json", user / "backups"


def migrate_state(project, env=None):
    """Explicit, one-time move of the legacy repo-local state to %LOCALAPPDATA%/fm26-visual-mod (legacy kept as .migrated)."""
    legacy = Path(project) / "local/install-state.json"
    user = Path((os.environ if env is None else env)["LOCALAPPDATA"]) / "fm26-visual-mod" / "install-state.json"
    if user.exists():
        raise FileExistsError(f"User deployment state already exists: {user}")
    data = json.loads(legacy.read_text(encoding="utf-8"))
    data["migrated_from"] = str(legacy)
    save_state(user, data)
    os.replace(legacy, legacy.with_name(legacy.name + ".migrated"))
    return user


def set_auto(game, state, enabled):
    """Opt-in automatic overlay start: write/remove BepInEx/visual-diagnostics/fifa-overlay-auto.json and track it."""
    game, state = Path(game).resolve(), Path(state).resolve()
    data = json.loads(state.read_text(encoding="utf-8"))
    if Path(data["game"]).resolve() != game:
        raise ValueError("Game path does not match deployment state")
    if data["status"] != "installed":
        raise ValueError(f"Deployment status is {data['status']!r}, not 'installed'")
    target = game / AUTO_RELATIVE
    if not enabled:
        if target.exists() and "auto_json" not in data:
            raise ValueError(f"Refusing to remove an untracked {AUTO_RELATIVE}")
        if target.exists() and (data["auto_json"].get("path") != AUTO_RELATIVE
                                or digest(target) != data["auto_json"].get("sha256")):
            raise ValueError(f"Refusing to remove {AUTO_RELATIVE}: modified since auto-on")
        target.unlink(missing_ok=True)
        data.pop("auto_json", None)
        save_state(state, data)
        return target
    if target.exists() and "auto_json" not in data:
        raise FileExistsError(f"Refusing to overwrite an untracked {AUTO_RELATIVE}")
    payload = b'{"Enabled":true}'
    if not target.parent.exists():
        data["auto_dir_created"] = True
    target.parent.mkdir(parents=True, exist_ok=True)
    target.write_bytes(payload)
    data["auto_json"] = {"path": AUTO_RELATIVE, "sha256": hashlib.sha256(payload).hexdigest()}
    save_state(state, data)
    return target


def bepinex_version(game):
    """Read-only: ProductVersion string (UTF-16LE, VS_VERSION_INFO) of BepInEx/core/BepInEx.Core.dll."""
    core = Path(game) / "BepInEx/core/BepInEx.Core.dll"
    if not core.is_file():
        return None
    with core.open("rb") as stream:
        data = stream.read(_MAX_CORE_BYTES + 1)
    if len(data) > _MAX_CORE_BYTES:
        return None
    key = "ProductVersion\0".encode("utf-16-le")
    at = data.find(key)
    if at < 0:
        return None
    i = at + len(key)
    while data[i:i + 2] == b"\0\0":
        i += 2
    end = i
    while end + 1 < len(data) and data[end:end + 2] != b"\0\0":
        end += 2
    try:
        return data[i:end].decode("utf-16-le")
    except UnicodeDecodeError:
        return None


def install_plugin_only(plugin, game, state):
    """BepInEx already present (user-installed): add only our plugin folder, after a BepInEx core version check."""
    plugin, game, state = Path(plugin).resolve(), Path(game).resolve(), Path(state).resolve()
    if state.exists():
        raise FileExistsError(f"Deployment state already exists: {state}")
    version = bepinex_version(game)
    if (version or "").split("+")[0] != REQUIRED_BEPINEX:
        raise ValueError(f"Existing BepInEx version {version} is not supported: plugin-only install requires "
                         f"BepInEx {REQUIRED_BEPINEX} (Unity IL2CPP win-x64)")
    refuse_private_pack(plugin)
    if plugin.name != PLUGIN_RELATIVE.rsplit("/", 1)[1]:
        raise ValueError("Unexpected plugin filename")
    target = game / PLUGIN_RELATIVE
    if target.parent.exists():
        raise FileExistsError(f"Refusing to overwrite existing path: {target.parent}")
    expected = digest(plugin)
    target.parent.mkdir(parents=True, exist_ok=True)
    try:
        shutil.copy2(plugin, target)
        if digest(target) != expected:
            raise RuntimeError("Plugin hash mismatch after deployment")
        data = {"game": str(game), "status": "installed", "mode": "plugin-only", "owned_roots": [],
                "owned_dirs": [PLUGIN_RELATIVE.rsplit("/", 1)[0]], "files": [{"path": PLUGIN_RELATIVE, "sha256": expected}],
                "baseline_sha256": {name: digest(game / name) for name in CRITICAL_FILES if (game / name).is_file()},
                "created_at": datetime.datetime.now().astimezone().isoformat()}
        save_state(state, data)  # atomic (temporary file + os.replace)
    except BaseException:
        shutil.rmtree(target.parent, ignore_errors=True)
        raise
    return data


def detect_game():
    """FM26 folder from the read-only Steam detection of tools/fm26vm (B1)."""
    import sys
    import winreg
    project = str(Path(__file__).resolve().parents[1])
    if project not in sys.path:
        sys.path.insert(0, project)
    from tools.fm26vm import detect
    return detect.find_fm(registry=detect.registry_reader(winreg))


def require_game_stopped():
    """H229 REM1: one fm.exe mechanism, the tasklist checker of tools/fm26vm/detect.py (timeout -> 'Could not check')."""
    if os.name == "nt":
        import sys
        project = str(Path(__file__).resolve().parents[1])
        if project not in sys.path:
            sys.path.insert(0, project)
        from tools.fm26vm import detect
        if detect.fm_running_checker(subprocess.run)():
            raise RuntimeError("Close Football Manager before deployment or removal")


def main(argv=None, project=None, env=None, find_game=None, stopped=None):
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("action", choices=("install", "uninstall", "add-plugin", "update-plugin", "verify",
                                                 "auto-on", "auto-off", "migrate-state"))
    parser.add_argument("--game", help="FM26 folder (default: detected via Steam)")
    project = Path(project) if project is not None else Path(__file__).resolve().parents[1]
    parser.add_argument("--state")
    parser.add_argument("--stage", default=str(project / ".tools/bepinex-stage"))
    parser.add_argument("--plugin")
    parser.add_argument("--public", action="store_true", help="refuse stages holding FM-generated interop/unity-libs")
    args = parser.parse_args(argv)
    if args.action == "migrate-state":
        print(json.dumps({"state": str(migrate_state(project, env)), "migrated": True}))
        return
    game = Path(args.game) if args.game else Path((find_game or detect_game)())
    state = Path(args.state) if args.state else resolve_state(project, env)[0]
    if args.action != "verify":
        (stopped or require_game_stopped)()
    if args.action == "install" and args.plugin and (game / "BepInEx").exists():
        data = install_plugin_only(Path(args.plugin), game, state)
        print(json.dumps({"status": data["status"], "mode": data["mode"], "state": str(state)}))
    elif args.action == "install":
        data = install(Path(args.stage), game, state, public=args.public)
        print(json.dumps({"status": data["status"], "installed_file_count": len(data["files"]), "state": str(state)}))
    elif args.action == "uninstall":
        backups = state.resolve().parent / "backups" if args.state else resolve_state(project, env)[1]
        print(json.dumps({"backup": str(uninstall(game, state, backups)), "status": "uninstalled"}))
    elif args.action in ("auto-on", "auto-off"):
        enabled = args.action == "auto-on"
        print(json.dumps({"auto_json": str(set_auto(game, state, enabled)), "enabled": enabled}))
    elif args.action == "add-plugin":
        if not args.plugin:
            parser.error("--plugin is required")
        print(json.dumps({"plugin": str(add_plugin(Path(args.plugin), game, state)), "verified": True}))
    elif args.action == "update-plugin":
        if not args.plugin:
            parser.error("--plugin is required")
        print(json.dumps({"previous_version_backup": str(update_plugin(Path(args.plugin), game, state)), "verified": True}))
    else:
        data = json.loads(state.read_text(encoding="utf-8"))
        originals = {name: digest(game / name) == expected for name, expected in data["baseline_sha256"].items()}
        files = {item["path"]: (game / item["path"]).exists() for item in data["files"]}
        print(json.dumps({"status": data["status"], "originals_unchanged": originals,
                          "deployed_file_count": len(files), "all_deployed_files_present": all(files.values())}, indent=2))
        if not all(originals.values()) or (data["status"] == "installed" and not all(files.values())):
            raise SystemExit(1)


def cli(argv=None):
    """Script entry point: refusals become 'error: ...' on stderr with exit code 1 (main() itself still raises)."""
    import sys
    try:
        main(argv)
    except (OSError, ValueError, RuntimeError, KeyError) as error:
        print(f"error: {error}", file=sys.stderr)
        raise SystemExit(1)


if __name__ == "__main__":
    cli()
