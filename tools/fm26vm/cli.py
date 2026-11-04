# SPDX-License-Identifier: MIT
"""fm26vm command line."""


import argparse
import hashlib
import io
import json
import os
import shutil
import sys
import tempfile
import time

from pathlib import Path

from . import cas, detect, gd, meshset, orchestrate, recipe, retarget, state, tools_acquire
from .. import fifa_pack


def build_parser():
    parser = argparse.ArgumentParser(prog="python -m tools.fm26vm")
    sub = parser.add_subparsers(dest="command", required=True)
    d = sub.add_parser("detect", help="find FM26 (Steam) and FIFA 17 install folders (read-only)")
    d.add_argument("--steam")
    d.add_argument("--fm")
    d.add_argument("--fifa17")
    for name in ("verify", "rebuild-pack"):
        sub.add_parser(name)  # reserved, not implemented yet: hidden from --help
    n = sub.add_parser("uninstall", help="deploy uninstall (+ auto-off) with a final readback; the pack is kept")
    n.add_argument("--purge-pack", action="store_true", help="also delete the private pack")
    u = sub.add_parser("setup", help="detect -> verify-fifa17 -> tools -> build-pack -> deploy -> auto-on -> verify "
                                     "(resumable, recorded in %%LOCALAPPDATA%%/fm26-visual-mod/state.json)")
    for a in ("--steam", "--fm", "--fifa17"):
        u.add_argument(a)
    u.add_argument("--recipe", default=str(recipe.DEFAULT_RECIPE))
    u.add_argument("--zstd", help="local zstd-v1.5.7-win64.zip")
    u.add_argument("--bepinex", help="local BepInEx 6.0.0-be.788 IL2CPP win-x64 zip (official)")
    u.add_argument("--plugin", help="release FM26.VisualDiagnostics.dll (sha256 pinned in tools/fm26vm/release.json)")
    u.add_argument("--download", action="store_true", help="allow the pinned official downloads (never by default)")
    u.add_argument("--rebuild-pack", action="store_true", help="move an existing pack to pack-backups and rebuild it")
    u.add_argument("--dry-run", action="store_true", help="print the plan, write nothing")
    f = sub.add_parser("fetch-tools", help="acquire pinned zstd + BepInEx (sha256 verified) into the user tools folder")
    f.add_argument("--zstd", help="local zstd-v1.5.7-win64.zip instead of downloading")
    f.add_argument("--bepinex", help="local BepInEx 6.0.0-be.788 IL2CPP win-x64 zip instead of downloading")
    f.add_argument("--only", choices=("zstd", "bepinex"), help="acquire only this tool (zstd is enough for extract-spans)")
    f.add_argument("--offline", action="store_true", help="never download; require --zstd/--bepinex")
    v = sub.add_parser("verify-fifa17", help="read-only streaming check of the FIFA 17 build against the recipe")
    v.add_argument("--fifa17")
    v.add_argument("--recipe", default=str(recipe.DEFAULT_RECIPE))
    x = sub.add_parser("extract-spans", help="decode the recipe CAS spans with the pinned zstd (FIFA 17 read-only)")
    x.add_argument("--fifa17")
    x.add_argument("--recipe", default=str(recipe.DEFAULT_RECIPE))
    x.add_argument("--out", required=True, help="output folder (outside the FIFA 17 folder)")
    r = sub.add_parser("read-bank", help="project the 10 clips + rig from the decoded gameplay bank (clean-room reader)")
    r.add_argument("--bank", required=True, help="gameplay-antstate-bank.bin written by extract-spans")
    r.add_argument("--recipe", default=str(recipe.DEFAULT_RECIPE))
    r.add_argument("--out", required=True, help="canonical JSON output file")
    m = sub.add_parser("read-meshes", help="read the render skeleton + 6 mesh pieces from decoded spans (clean-room)")
    m.add_argument("--spans", required=True, help="folder written by extract-spans")
    m.add_argument("--recipe", default=str(recipe.DEFAULT_RECIPE))
    m.add_argument("--out", required=True, help="canonical JSON output file")
    b = sub.add_parser("build-frames", help="extract-spans -> read-bank -> read-meshes -> vbr -> retarget: the 10 "
                                         "frame arrays, each checked against the recipe golden")
    b.add_argument("--fifa17")
    b.add_argument("--recipe", default=str(recipe.DEFAULT_RECIPE))
    b.add_argument("--out", required=True, help="output folder for frames-<clip>-f64le.bin")
    b.add_argument("--keep-work", action="store_true", help="keep the temporary work folder (decoded spans)")
    k = sub.add_parser("build-pack", help="extract-spans -> readers -> frames -> fifa_pack v2 in memory, verified, "
                                          "moved atomically into --out")
    k.add_argument("--fifa17")
    k.add_argument("--recipe", default=str(recipe.DEFAULT_RECIPE))
    k.add_argument("--out", required=True, help="new or empty pack folder (outside the FIFA 17 folder)")
    k.add_argument("--keep-work", action="store_true", help="keep the temporary work folder (decoded spans)")
    s = sub.add_parser("status", help="read-only summary: detected paths, pack, plugin hash, auto on/off, BepInEx mode")
    s.add_argument("--dry-run", action="store_true", help="only print the state file path")
    for a in ("--steam", "--fm", "--fifa17"):
        s.add_argument(a)
    s.add_argument("--recipe", default=str(recipe.DEFAULT_RECIPE))
    hidden = {"verify", "rebuild-pack"}
    sub.metavar = "{" + ",".join(n for n in sub.choices if n not in hidden) + "}"
    return parser


def main(argv=None, registry=None, fm_running=None, env=None, fifa_candidates=None, out=None, pins=None, opener=None,
         launcher=(), frames_pipeline=None, pack_pipeline=None, hooks=None):
    out = out or sys.stdout
    args = build_parser().parse_args(argv)
    if registry is None and sys.platform == "win32":
        import winreg
        registry = detect.registry_reader(winreg)
    if fm_running is None:
        import subprocess
        fm_running = detect.fm_running_checker(subprocess.run)
    if fifa_candidates is None:
        fifa_candidates = detect.FIFA_DEFAULT_FOLDERS
    if args.command == "status" and args.dry_run:
        path = state.state_path(env)
        out.write(json.dumps({"dry_run": True, "state_path": str(path), "state_exists": path.is_file()}, indent=2) + "\n")
        return 0
    if args.command == "uninstall":
        return orchestrate.uninstall(_ctx(env, registry, fm_running, fifa_candidates, opener, pins, launcher, hooks), args, out)
    if args.command == "setup":
        return orchestrate.setup(_ctx(env, registry, fm_running, fifa_candidates, opener, pins, launcher, hooks), args, out)
    if args.command == "status":
        return orchestrate.status(_ctx(env, registry, fm_running, fifa_candidates, opener, pins, launcher, hooks), args, out)
    if args.command == "verify-fifa17":
        try:
            fifa = detect.find_fifa17(args.fifa17, registry, fifa_candidates or ())
            t0 = time.perf_counter()
            report = recipe.verify_fifa17(fifa, recipe.load(args.recipe))
        except (detect.DetectError, recipe.RecipeError, recipe.VerifyError) as e:
            out.write(f"error: {e}\n")
            return 1
        report.update(fifa17=str(fifa), seconds=round(time.perf_counter() - t0, 2))
        out.write(json.dumps(report, indent=2) + "\n")
        return 0
    if args.command == "fetch-tools":
        if opener is None:
            opener = tools_acquire.open_url
        tools_dir = tools_acquire.default_tools_dir(env)
        try:
            results = [tools_acquire.acquire(kind, tools_dir, supplied=getattr(args, kind), opener=opener,
                                             offline=args.offline, pins=pins)
                   for kind in ((args.only,) if args.only else ("zstd", "bepinex"))]
        except tools_acquire.AcquireError as e:
            out.write(f"error: {e}\n")
            return 1
        out.write(json.dumps({"tools_dir": str(tools_dir), "tools": results}, indent=2) + "\n")
        return 0
    if args.command == "extract-spans":
        return _extract_spans(args, registry, fifa_candidates, env, out, pins, launcher)
    if args.command == "read-bank":
        return _read_bank(args, out)
    if args.command == "read-meshes":
        return _read_meshes(args, out)
    if args.command == "build-frames":
        try:
            fifa = detect.find_fifa17(args.fifa17, registry, fifa_candidates or ())
        except detect.DetectError:
            fifa = None  # extract-spans reports the missing FIFA 17 folder itself
        if fifa is not None and Path(args.out).resolve().is_relative_to(Path(fifa).resolve()):
            out.write(f"error: --out {args.out} is inside the FIFA 17 folder (the game folder stays read-only)\n")
            return 1
        return _build_frames(args, out, env, frames_pipeline or (
            lambda ns, o: _extract_spans(ns, registry, fifa_candidates, env, o, pins, launcher), _frames_from_spans))
    if args.command == "build-pack":
        try:
            fifa = detect.find_fifa17(args.fifa17, registry, fifa_candidates or ())
        except detect.DetectError:
            fifa = None  # extract-spans reports the missing FIFA 17 folder itself
        if fifa is not None and Path(args.out).resolve().is_relative_to(Path(fifa).resolve()):
            out.write(f"error: --out {args.out} is inside the FIFA 17 folder (the game folder stays read-only)\n")
            return 1
        return _build_pack(args, out, env, pack_pipeline or (
            lambda ns, o: _extract_spans(ns, registry, fifa_candidates, env, o, pins, launcher), _pack_from_spans,
            fifa_pack.verify_pack))
    if args.command != "detect":
        out.write(f"{args.command}: not implemented yet\n")
        return 2
    try:
        found = detect.detect(args.steam, args.fm, args.fifa17, registry, fifa_candidates or (), fm_running)
    except detect.DetectError as e:
        out.write(f"error: {e}\n")
        return 1
    out.write(json.dumps(found, indent=2) + "\n")
    return 0


def _ctx(env, registry, fm_running, fifa_candidates, opener, pins, launcher, hooks):
    if opener is None:
        opener = tools_acquire.open_url  # only used with setup --download
    return orchestrate.Ctx(env=env, registry=registry, fm_running=fm_running, fifa_candidates=fifa_candidates,
                           opener=opener, pins=pins, launcher=launcher, **(hooks or {}))


def _extract_spans(args, registry, fifa_candidates, env, out, pins, launcher):
    pin = (tools_acquire.PINS if pins is None else pins)["zstd"]
    member, exe_sha = next(iter(pin["checks"].items()))
    exe = tools_acquire.default_tools_dir(env) / pin["folder"] / member
    try:
        fifa = detect.find_fifa17(args.fifa17, registry, fifa_candidates or ())
        rec = recipe.load(args.recipe)
    except (recipe.RecipeError, detect.DetectError) as e:
        out.write(f"error: {e}\n")
        return 1
    if Path(args.out).resolve().is_relative_to(Path(fifa).resolve()):
        out.write(f"error: --out {args.out} is inside the FIFA 17 folder (the game folder stays read-only)\n")
        return 1
    if not exe.is_file():
        out.write(f"error: pinned zstd not installed at {exe}: run `python -m tools.fm26vm fetch-tools --only zstd`\n")
        return 1
    dest = Path(args.out)
    dest.mkdir(parents=True, exist_ok=True)
    work = dest / ".work"
    work.mkdir(exist_ok=True)
    reports, written = [], []
    try:
        for span in rec["spans"]:
            target = dest / f"{span['id']}.bin"
            reports.append(cas.extract_span(fifa, span, span["decoded_size"], span["decoded_sha256"], exe, exe_sha, work,
                                            target, launcher=launcher, timeout=cas.timeout_for(span),
                                            unsupported_message=rec["supported_build"]["message"]))
            written.append(target)
    except BaseException as e:
        for target in written:  # all-or-nothing: no partial span set is left behind, whatever the failure
            target.unlink(missing_ok=True)
        if not isinstance(e, (cas.CasError, OSError)):
            raise
        out.write(f"error: {e}\n")
        return 1
    finally:
        shutil.rmtree(work, ignore_errors=True)  # never masks the original error
    out.write(json.dumps({"fifa17": str(fifa), "out": str(dest), "spans": reports}, indent=2) + "\n")
    return 0


def _read_bank(args, out):
    try:
        rec = recipe.load(args.recipe)
        text = gd.canonical_json(gd.read_bank_file(args.bank, rec))
    except (gd.GdError, recipe.RecipeError, OSError) as e:
        out.write(f"error: {e}\n")
        return 1
    digest = hashlib.sha256(text.encode("ascii")).hexdigest()
    golden = rec["golden"].get("gd_reader_sha256")
    if golden is not None and digest != golden:
        out.write(f"error: reader output sha256 {digest} differs from the recipe golden {golden}\n")
        return 1
    tmp = Path(str(args.out) + ".tmp")
    tmp.write_bytes(text.encode("ascii"))
    os.replace(tmp, args.out)
    out.write(json.dumps({"out": str(args.out), "sha256": digest}, indent=2) + "\n")
    return 0


def _read_meshes(args, out):
    try:
        rec = recipe.load(args.recipe)
        text = meshset.canonical_json(meshset.read_meshes_dir(args.spans, rec))
    except (meshset.MeshError, recipe.RecipeError) as e:
        out.write(f"error: {e}\n")
        return 1
    digest = hashlib.sha256(text.encode("ascii")).hexdigest()
    golden = rec["golden"].get("meshset_reader_sha256")
    if golden is not None and digest != golden:
        out.write(f"error: reader output sha256 {digest} differs from the recipe golden {golden}\n")
        return 1
    tmp = Path(str(args.out) + ".tmp")
    tmp.write_bytes(text.encode("ascii"))
    os.replace(tmp, args.out)
    out.write(json.dumps({"out": str(args.out), "sha256": digest}, indent=2) + "\n")
    return 0


def _frames_from_spans(spans, rec):
    """read-bank -> read-meshes -> vbr -> retarget on an extract-spans folder; returns {frames file: bytes}."""
    bank = gd.read_bank_file(Path(spans) / "gameplay-antstate-bank.bin", rec)
    return retarget.build_frames(rec, bank, meshset.read_meshes_dir(spans, rec))


def _pack_from_spans(spans, rec):
    """read-bank -> read-meshes -> vbr + retarget -> fifa_pack.build_pack_v2_files; returns {pack file: bytes}."""
    meshes = meshset.read_meshes_dir(spans, rec)
    bank = gd.read_bank_file(Path(spans) / "gameplay-antstate-bank.bin", rec)
    files = fifa_pack.build_pack_v2_files(rec, meshes, retarget.build_frames(rec, bank, meshes))
    golden = rec["golden"]["pack_v2_files"]
    bad = sorted(n for n in set(files) | set(golden) if n not in files or n not in golden
                 or len(files[n]) != golden[n]["bytes"] or hashlib.sha256(files[n]).hexdigest() != golden[n]["sha256"])
    if bad:
        raise ValueError(f"pack file(s) {', '.join(bad)} differ from the recipe golden pack_v2_files")
    return files


def _tree_bytes(path):
    return sum(p.stat().st_size for p in Path(path).rglob("*") if p.is_file())


def _build_frames(args, out, env, pipeline):
    extract, build = pipeline
    try:
        rec = recipe.load(args.recipe)
    except recipe.RecipeError as e:
        out.write(f"error: {e}\n")
        return 1
    base = Path((os.environ if env is None else env).get("LOCALAPPDATA") or tempfile.gettempdir())
    root = base / "fm26-visual-mod" / "work"
    root.mkdir(parents=True, exist_ok=True)
    work = Path(tempfile.mkdtemp(prefix="build-frames-", dir=root))
    t0 = time.perf_counter()
    try:
        log = io.StringIO()
        if extract(argparse.Namespace(fifa17=args.fifa17, recipe=args.recipe, out=str(work / "spans")), log) != 0:
            out.write(log.getvalue())
            return 1
        peak, t1 = _tree_bytes(work), time.perf_counter()
        files = build(work / "spans", rec)
        peak, t2 = max(peak, _tree_bytes(work)), time.perf_counter()
    except (gd.GdError, meshset.MeshError, recipe.RecipeError, ValueError) as e:  # RetargetError/vbr refusals are ValueError
        out.write(f"error: {e}\n")
        return 1
    finally:
        if not args.keep_work:
            shutil.rmtree(work, ignore_errors=True)
    dest = Path(args.out)
    dest.mkdir(parents=True, exist_ok=True)
    for name, raw in files.items():
        tmp = dest / (name + ".tmp")
        tmp.write_bytes(raw)
        os.replace(tmp, dest / name)
    report = {"out": str(dest), "work": str(work), "work_kept": args.keep_work,
              "files": {n: {"bytes": len(b), "sha256": hashlib.sha256(b).hexdigest()} for n, b in files.items()},
              "seconds": {"extract": round(t1 - t0, 2), "frames": round(t2 - t1, 2), "total": round(time.perf_counter() - t0, 2)},
              "peak_work_bytes": peak}
    out.write(json.dumps(report, indent=2) + "\n")
    return 0



def _build_pack(args, out, env, pipeline):
    extract, build, verify = pipeline
    dest = Path(args.out)
    if dest.exists() and (not dest.is_dir() or any(dest.iterdir())):
        out.write(f"error: --out {dest} exists and is not empty (build-pack never replaces an existing pack)\n")
        return 1
    try:
        rec = recipe.load(args.recipe)
    except recipe.RecipeError as e:
        out.write(f"error: {e}\n")
        return 1
    base = Path((os.environ if env is None else env).get("LOCALAPPDATA") or tempfile.gettempdir())
    root = base / "fm26-visual-mod" / "work"
    root.mkdir(parents=True, exist_ok=True)
    work = Path(tempfile.mkdtemp(prefix="build-pack-", dir=root))
    try:
        return _build_pack_in(args, out, rec, dest, work, extract, build, verify)
    finally:
        if not args.keep_work:
            shutil.rmtree(work, ignore_errors=True)


def _build_pack_in(args, out, rec, dest, work, extract, build, verify):
    t0 = time.perf_counter()
    log = io.StringIO()
    if extract(argparse.Namespace(fifa17=args.fifa17, recipe=args.recipe, out=str(work / "spans")), log) != 0:
        out.write(log.getvalue())
        return 1
    t1 = time.perf_counter()
    try:
        files = build(work / "spans", rec)
    except (gd.GdError, meshset.MeshError, recipe.RecipeError, ValueError) as e:  # RetargetError/vbr/fifa_pack: ValueError
        out.write(f"error: {e}\n")
        return 1
    t2 = time.perf_counter()
    pack = work / "pack"
    pack.mkdir()
    for name, raw in files.items():
        (pack / name).write_bytes(raw)
    result = verify(pack)
    if not result["ok"]:
        out.write(f"error: the built pack fails fifa_pack verify: {', '.join(result['errors'])}\n")
        return 1
    peak = _tree_bytes(work)
    dest.parent.mkdir(parents=True, exist_ok=True)
    if dest.is_dir():
        dest.rmdir()
    os.replace(pack, dest)  # same volume as the work folder: one rename, never a half-written pack in --out
    report = {"out": str(dest), "work": str(work), "work_kept": args.keep_work, "files": {n: {"bytes": len(b), "sha256": hashlib.sha256(b).hexdigest()}
                                                            for n, b in sorted(files.items())},
              "seconds": {"extract": round(t1 - t0, 2), "pack": round(t2 - t1, 2), "total": round(time.perf_counter() - t0, 2)},
              "peak_work_bytes": peak}
    out.write(json.dumps(report, indent=2) + "\n")
    return 0
