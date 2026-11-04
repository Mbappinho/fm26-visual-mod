# SPDX-License-Identifier: MIT
"""fm26vm setup / uninstall / status (B10): orchestrates detect, verify-fifa17, fetch-tools, build-pack and deploy.

Every setup step is idempotent and recorded in %LOCALAPPDATA%/fm26-visual-mod/state.json; a failed run records the
failing step and the next run resumes (completed steps are re-checked against the disk, not trusted blindly).
`--dry-run` only reads. The deployment itself is tools/deploy.py with the user state
%LOCALAPPDATA%/fm26-visual-mod/install-state.json and backups beside it.
"""
import argparse
import datetime
import hashlib
import io
import json
import os
import shutil
from pathlib import Path

from . import detect, recipe, state as state_file, tools_acquire
from .. import deploy, fifa_pack

RELEASE = Path(__file__).with_name("release.json")
AUTO_PAYLOAD = b'{"Enabled":true}'  # the bytes deploy.set_auto writes: only an identical untracked file is adopted
PACK_NAME = "fifa17-player-v2"
SCHEMA = "fm26vm.state.v1"
STEPS = ("detect", "verify-fifa17", "tools", "pack", "deploy", "auto-on", "verify")
FIRST_LAUNCH = ("the first FM launch after a full install is slower (BepInEx generates its interop); success shows in "
                "BepInEx/LogOutput.log as 'Registered mono type ...FifaOverlayBehaviour'")


class SetupError(RuntimeError):
    pass


def _sha(path):
    with open(path, "rb") as stream:
        return hashlib.file_digest(stream, "sha256").hexdigest()


def _now():
    return datetime.datetime.now().astimezone().isoformat()


def _stamp():
    return datetime.datetime.now().strftime("%Y%m%d-%H%M%S-%f")


def default_pack_check(pack, rec):
    """(ok, kind): fifa_pack verify AND every file equal to the recipe golden (clean header: 'golden') or the payloads
    golden with the installed legacy-provenance header ('installed-golden'). Read-only."""
    pack = Path(pack)
    if not (pack / fifa_pack.HEADER).is_file():
        return False, "missing"
    result = fifa_pack.verify_pack(pack)
    if not result["ok"]:
        return False, "fifa_pack verify failed: " + ", ".join(result["errors"])
    golden = rec["golden"]["pack_v2_files"]
    names = sorted(p.name for p in pack.iterdir() if p.is_file())
    if names != sorted(golden):
        return False, "file set differs from the recipe golden pack_v2_files"
    got = {n: {"bytes": (pack / n).stat().st_size, "sha256": _sha(pack / n)} for n in names}
    if any(got[n] != golden[n] for n in names if n != fifa_pack.HEADER):
        return False, "payloads differ from the recipe golden pack_v2_files"
    if got[fifa_pack.HEADER] == golden[fifa_pack.HEADER]:
        return True, "golden"
    if got[fifa_pack.HEADER] == rec["golden"].get("pack_v2_installed_header"):
        return True, "installed-golden"
    return False, "header differs from the recipe goldens"


def default_pack_builder(ctx, fifa, dest, recipe_path):
    """Real B8 build-pack into dest (work folder under %LOCALAPPDATA%/fm26-visual-mod/work, removed afterwards)."""
    from . import cli
    args = argparse.Namespace(fifa17=str(fifa), recipe=str(recipe_path), out=str(dest), keep_work=False)
    log = io.StringIO()
    code = cli._build_pack(args, log, ctx.env, (
        lambda ns, o: cli._extract_spans(ns, ctx.registry, ctx.fifa_candidates, ctx.env, o, ctx.pins, ctx.launcher),
        cli._pack_from_spans, fifa_pack.verify_pack))
    if code != 0:
        raise SetupError(f"build-pack failed: {log.getvalue().strip()}")


class Ctx:
    """Everything the orchestrator touches; tests inject fakes (never the real game, never the network)."""

    def __init__(self, env=None, registry=None, fm_running=None, fifa_candidates=(), opener=None, pins=None,
                 launcher=(), project=None, release=None, pack_builder=None, pack_check=None):
        self.env = os.environ if env is None else env
        self.registry, self.fm_running, self.fifa_candidates = registry, fm_running, tuple(fifa_candidates or ())
        self.opener, self.pins, self.launcher = opener, pins, launcher
        self.project = Path(project) if project is not None else Path(__file__).resolve().parents[2]
        self.release = release if release is not None else json.loads(RELEASE.read_text(encoding="utf-8"))["plugin"]
        self.pack_builder = pack_builder or default_pack_builder
        self.pack_check = pack_check or default_pack_check
        self.root = Path(self.env["LOCALAPPDATA"]) / "fm26-visual-mod"
        self.state = state_file.state_path(self.env)
        self.deploy_state = self.root / "install-state.json"
        self.backups = self.root / "backups"
        self.pack = self.root / "packs" / PACK_NAME
        self.pack_backups = self.root / "pack-backups"
        self.tools = tools_acquire.default_tools_dir(self.env)

    def pin(self, kind):
        return (tools_acquire.PINS if self.pins is None else self.pins)[kind]


def _read(path):
    return json.loads(Path(path).read_text(encoding="utf-8")) if Path(path).is_file() else None


def _load_state(ctx):
    return _read(ctx.state) or {"schema": SCHEMA, "steps": {}}


def _save_state(ctx, data):
    deploy.save_state(ctx.state, data)


# ---------------------------------------------------------------------------------------------------------- status

def status(ctx, args, out):
    """Read-only summary: detected paths, pack, plugin hash, auto on/off, BepInEx mode, setup steps."""
    report = {"state_path": str(ctx.state)}
    try:
        game = detect.find_fm(args.steam, args.fm, ctx.registry)
        report["fm"] = str(game)
    except detect.DetectError as e:
        game, report["fm"] = None, f"not found: {e}"
    try:
        report["fifa17"] = str(detect.find_fifa17(args.fifa17, ctx.registry, ctx.fifa_candidates))
    except detect.DetectError as e:
        report["fifa17"] = f"not found: {e}"
    try:
        ok, kind = ctx.pack_check(ctx.pack, recipe.load(args.recipe))
    except (recipe.RecipeError, OSError, ValueError, KeyError) as e:
        ok, kind = False, f"unchecked: {e}"
    report["pack"] = {"path": str(ctx.pack), "exists": ctx.pack.is_dir(), "ok": ok, "kind": kind}
    legacy = ctx.project / "local" / "install-state.json"
    state_path = ctx.deploy_state if ctx.deploy_state.is_file() else legacy if legacy.is_file() else None
    data = _read(state_path) if state_path else None
    report["deployment"] = None if data is None else {
        "state": str(state_path), "legacy": state_path == legacy, "status": data.get("status"),
        "mode": data.get("mode", "full"), "game": data.get("game")}
    if game is None and data is not None:
        game = Path(data["game"])
    plugin = game / deploy.PLUGIN_RELATIVE if game is not None else None
    sha = _sha(plugin) if plugin is not None and plugin.is_file() else None
    report["plugin"] = {"path": str(plugin) if plugin else None, "sha256": sha,
                        "release_sha256": ctx.release["sha256"], "matches_release": sha == ctx.release["sha256"]}
    auto = game / deploy.AUTO_RELATIVE if game is not None else None
    report["auto"] = {"enabled": bool(auto and auto.is_file()), "tracked": bool(data and data.get("auto_json"))}
    version = deploy.bepinex_version(game) if game is not None else None
    present = game is not None and (game / "BepInEx").exists()
    mode = "absent" if not present else (data.get("mode", "full") if data and data.get("status") == "installed"
                                         else "untracked")
    report["bepinex"] = {"present": present, "version": version, "mode": mode}
    setup = _read(ctx.state) if ctx.state.is_file() else None
    report["setup"] = None if setup is None else {"steps_done": sorted(setup.get("steps", {})),
                                                  "failed": setup.get("failed")}
    out.write(json.dumps(report, indent=2) + "\n")
    return 0


# ----------------------------------------------------------------------------------------------------------- steps

def _step_detect(ctx, args, data, facts, dry):
    if ctx.fm_running is not None and ctx.fm_running():
        raise SetupError("Football Manager (fm.exe) is running: close it first")
    fm = detect.find_fm(args.steam, args.fm, ctx.registry)
    fifa = detect.find_fifa17(args.fifa17, ctx.registry, ctx.fifa_candidates)
    facts.update(fm=Path(fm), fifa17=Path(fifa))
    return {"action": "checked", "fm": str(fm), "fifa17": str(fifa)}


def _step_verify_fifa17(ctx, args, data, facts, dry):
    raw = Path(args.recipe).read_bytes()
    facts["recipe"] = recipe.load(args.recipe)
    key = {"fifa17": str(facts["fifa17"]), "recipe_sha256": hashlib.sha256(raw).hexdigest()}
    done = data["steps"].get("verify-fifa17") or {}
    if all(done.get(k) == v for k, v in key.items()):
        return {"action": "skip", **key}
    report = recipe.verify_fifa17(facts["fifa17"], facts["recipe"])
    return {"action": "verified", **key, "spans": report["spans"]}


def _tool_plan(ctx, kind, supplied, download):
    """Read-only: 'cached', 'supplied' (zip sha256 equals the pin) or 'download'; refuses anything else."""
    pin = ctx.pin(kind)
    dest, marker = ctx.tools / pin["folder"], ctx.tools / (pin["folder"] + tools_acquire.MARKER)
    if marker.is_file() and all((dest / m).is_file() and _sha(dest / m) == h for m, h in pin["checks"].items()):
        return "cached"
    if supplied:
        if not (Path(supplied).is_file() and _sha(supplied) == pin["sha256"]):
            raise SetupError(f"{kind}: supplied zip {supplied} is missing or its sha256 differs from the pin {pin['sha256']}")
        return "supplied"
    if download and pin["url"]:
        return "download"
    raise SetupError(f"{kind}: not installed; supply the official zip with --{kind} ZIP"
                     + (" or allow the pinned download with --download" if pin["url"] else
                        " (no pinned download URL for this tool)"))


def _deploy_mode(ctx, game):
    current = _read(ctx.deploy_state)
    if current and current.get("status") == "installed":
        return current.get("mode", "full"), True
    legacy = _read(ctx.project / "local" / "install-state.json")  # our own install, tracked by the repo-local state
    if legacy and legacy.get("status") == "installed" and legacy.get("game") and \
            Path(legacy["game"]).resolve() == Path(game).resolve():
        return legacy.get("mode", "full") + " (legacy state, migrate first)", True
    return ("plugin-only" if (game / "BepInEx").exists() else "full"), False


def _step_tools(ctx, args, data, facts, dry):
    mode, deployed = _deploy_mode(ctx, facts["fm"])
    facts["mode"] = mode
    need = []
    if args.rebuild_pack or not ctx.pack_check(ctx.pack, facts["recipe"])[0]:
        need.append("zstd")
    if mode == "full" and not deployed:
        need.append("bepinex")
    tools = {}
    for kind in need:
        plan = _tool_plan(ctx, kind, getattr(args, kind), args.download)
        if plan == "cached" or dry:
            tools[kind] = plan if dry else "cached"
            continue
        tools[kind] = tools_acquire.acquire(kind, ctx.tools, supplied=getattr(args, kind), opener=ctx.opener,
                                            offline=not args.download, pins=ctx.pins)["path"]
    return {"action": "checked" if dry else "ready", "mode": mode, "tools": tools}


def _step_pack(ctx, args, data, facts, dry):
    exists = ctx.pack.exists()
    moved = None
    if exists and not args.rebuild_pack:
        ok, kind = ctx.pack_check(ctx.pack, facts["recipe"])
        if not ok:
            raise SetupError(f"existing pack {ctx.pack} is not the recipe golden ({kind}); refusing to overwrite it: "
                             f"pass --rebuild-pack (the old pack is moved to {ctx.pack_backups})")
        return {"action": "skip", "path": str(ctx.pack), "kind": kind,
                "header_sha256": _sha(ctx.pack / fifa_pack.HEADER)}
    if dry:
        return {"action": "build" + (" (existing pack moved to pack-backups)" if exists else ""), "path": str(ctx.pack)}
    if exists:
        moved = ctx.pack_backups / f"pack-{_stamp()}" / PACK_NAME
        moved.parent.mkdir(parents=True)
        os.replace(ctx.pack, moved)
    ctx.pack_builder(ctx, facts["fifa17"], ctx.pack, args.recipe)
    ok, kind = ctx.pack_check(ctx.pack, facts["recipe"])
    if not ok:
        raise SetupError(f"built pack {ctx.pack} fails the pack check: {kind}")
    return {"action": "built", "path": str(ctx.pack), "kind": kind, "header_sha256": _sha(ctx.pack / fifa_pack.HEADER),
            "previous_pack_moved_to": str(moved) if moved else None}


def _plugin(ctx, args):
    if not args.plugin:
        raise SetupError("--plugin PATH (the release FM26.VisualDiagnostics.dll) is required")
    plugin = Path(args.plugin)
    pin = ctx.release["sha256"]
    got = _sha(plugin) if plugin.is_file() else "missing"
    if got != pin:
        raise SetupError(f"plugin {plugin} sha256 {got} does not match the release pin {pin} (tools/fm26vm/release.json)")
    return plugin


def _step_deploy(ctx, args, data, facts, dry):
    game, pin = facts["fm"], ctx.release["sha256"]
    if ctx.fm_running is not None and ctx.fm_running():  # re-checked right before touching the game folder
        raise SetupError("Football Manager (fm.exe) is running: close it first")
    legacy = ctx.project / "local" / "install-state.json"
    if legacy.is_file():
        raise SetupError(f"legacy repo-local deployment state {legacy} exists: run `python tools/deploy.py migrate-state` "
                         f"first (setup only uses {ctx.deploy_state})")
    current = _read(ctx.deploy_state)
    if current and current.get("status") == "installed":
        if Path(current["game"]).resolve() != game.resolve():
            raise SetupError(f"deployment state {ctx.deploy_state} belongs to another FM folder: {current['game']}")
        target = game / deploy.PLUGIN_RELATIVE
        if target.is_file() and _sha(target) == pin:
            return {"action": "skip", "mode": current.get("mode", "full"), "plugin_sha256": pin}
        raise SetupError(f"installed plugin {target} does not match the release pin {pin}: use "
                         "`python tools/deploy.py update-plugin` or uninstall first")
    if current and current.get("status") != "uninstalled":
        raise SetupError(f"deployment state {ctx.deploy_state} is {current.get('status')!r}: inspect it "
                         "(python tools/deploy.py verify / uninstall)")
    plugin = _plugin(ctx, args)
    mode = facts["mode"]  # plugin-only: deploy.install_plugin_only itself refuses a BepInEx other than be.788
    if dry:
        version = deploy.bepinex_version(game)
        if mode == "plugin-only" and (version or "").split("+")[0] != deploy.REQUIRED_BEPINEX:
            raise SetupError(f"existing BepInEx version {version} is not supported: plugin-only install requires "
                             f"BepInEx {deploy.REQUIRED_BEPINEX}")
        if (game / deploy.PLUGIN_RELATIVE).parent.exists():
            raise SetupError(f"refusing to overwrite the existing untracked plugin folder "
                             f"{(game / deploy.PLUGIN_RELATIVE).parent} (no deployment state)")
        return {"action": f"install ({mode})", "mode": mode}
    if current:
        archived = ctx.deploy_state.with_name(f"install-state.uninstalled-{_stamp()}.json")
        os.replace(ctx.deploy_state, archived)
    if mode == "plugin-only":
        deploy.install_plugin_only(plugin, game, ctx.deploy_state)
        return {"action": "installed", "mode": mode, "plugin_sha256": _sha(game / deploy.PLUGIN_RELATIVE)}
    stage = ctx.tools / ctx.pin("bepinex")["folder"]  # the pristine official zip content: never FM-generated interop
    deploy.install(stage, game, ctx.deploy_state, public=True)
    deploy.add_plugin(plugin, game, ctx.deploy_state)
    return {"action": "installed", "mode": mode, "plugin_sha256": _sha(game / deploy.PLUGIN_RELATIVE)}


def _step_auto(ctx, args, data, facts, dry):
    game = facts["fm"]
    target = game / deploy.AUTO_RELATIVE
    current = _read(ctx.deploy_state)
    tracked = (current or {}).get("auto_json")
    if tracked and target.is_file() and _sha(target) == tracked["sha256"]:
        return {"action": "skip", "path": str(target)}
    if not tracked and target.is_file() and target.read_bytes() == AUTO_PAYLOAD:  # exactly what auto-on would write
        if dry:
            return {"action": "adopt", "path": str(target)}
        if current is None:
            raise SetupError(f"no deployment state {ctx.deploy_state}: refusing to adopt {target}")
        digest = _sha(target)
        current["auto_json"] = {"path": deploy.AUTO_RELATIVE, "sha256": digest, "adopted": True}
        deploy.save_state(ctx.deploy_state, current)
        return {"action": "adopted", "path": str(target), "sha256": digest}
    if dry:
        return {"action": "enable", "path": str(target)}
    deploy.set_auto(game, ctx.deploy_state, True)
    return {"action": "enabled", "path": str(target)}


def _step_final(ctx, args, data, facts, dry):
    if dry:
        return {"action": "verify after the run"}
    game, pin = facts["fm"], ctx.release["sha256"]
    current = _read(ctx.deploy_state)
    problems = []
    originals = {n: deploy.digest(game / n) == h for n, h in current["baseline_sha256"].items()}
    if not all(originals.values()):
        problems.append("original FM files differ from the baseline: " + ", ".join(n for n, v in originals.items() if not v))
    missing = [f["path"] for f in current["files"] if not (game / f["path"]).is_file()]
    if missing:
        problems.append(f"{len(missing)} deployed file(s) missing")
    plugin = game / deploy.PLUGIN_RELATIVE
    sha = _sha(plugin) if plugin.is_file() else None
    if sha != pin:
        problems.append(f"plugin sha256 {sha} != release pin {pin}")
    if not (game / deploy.AUTO_RELATIVE).is_file():
        problems.append("auto activation file missing")
    ok, kind = ctx.pack_check(ctx.pack, facts["recipe"])
    if not ok:
        problems.append(f"pack check failed: {kind}")
    if problems:
        raise SetupError("final verify failed: " + "; ".join(problems))
    return {"action": "verified", "status": current["status"], "mode": current.get("mode", "full"),
            "deployed_files": len(current["files"]), "plugin_sha256": sha, "auto": "on", "pack": kind,
            "note": FIRST_LAUNCH}


STEP_FUNCS = {"detect": _step_detect, "verify-fifa17": _step_verify_fifa17, "tools": _step_tools, "pack": _step_pack,
              "deploy": _step_deploy, "auto-on": _step_auto, "verify": _step_final}
ERRORS = (SetupError, detect.DetectError, recipe.RecipeError, recipe.VerifyError, tools_acquire.AcquireError, OSError,
          ValueError, RuntimeError)


def setup(ctx, args, out):
    data = _load_state(ctx)
    facts, report = {}, []
    if args.dry_run:
        blocked = False
        for name in STEPS:
            try:
                report.append({"step": name, **STEP_FUNCS[name](ctx, args, data, facts, True)})
            except ERRORS + (KeyError,) as e:  # KeyError: a fact of an earlier blocked step is missing
                blocked = True
                report.append({"step": name, "action": "blocked",
                               "detail": str(e) if not isinstance(e, KeyError) else f"needs an earlier step ({e})"})
        out.write(json.dumps({"dry_run": True, "writes": False, "state": str(ctx.state), "steps": report}, indent=2) + "\n")
        return 1 if blocked else 0
    current = None
    try:
        for name in STEPS:
            current = name
            record = STEP_FUNCS[name](ctx, args, data, facts, False)
            report.append({"step": name, **record})
            data["steps"][name] = {**record, "at": _now()}
            _save_state(ctx, data)
    except ERRORS as e:
        data["failed"] = {"step": current, "error": str(e), "at": _now()}
        _save_state(ctx, data)
        out.write(f"error: setup step {current}: {e}\n")
        return 1
    data.pop("failed", None)
    data["completed_at"] = _now()
    _save_state(ctx, data)
    out.write(json.dumps({"dry_run": False, "state": str(ctx.state), "steps": report}, indent=2) + "\n")
    return 0


# ------------------------------------------------------------------------------------------------------- uninstall

def uninstall(ctx, args, out):
    current = _read(ctx.deploy_state)
    try:
        if current is None or current.get("status") != "installed":
            raise SetupError(f"no installed deployment in {ctx.deploy_state}"
                             + (f" (status {current.get('status')!r})" if current else "") + ": nothing to uninstall")
        if ctx.fm_running is not None and ctx.fm_running():
            raise SetupError("Football Manager (fm.exe) is running: close it first")
        game = Path(current["game"])
        report = {"game": str(game), "auto": "off"}
        if current.get("auto_json") and (game / deploy.AUTO_RELATIVE).is_file():
            try:
                deploy.set_auto(game, ctx.deploy_state, False)
            except ValueError as e:  # modified by the user: archived with the uninstall instead of deleted
                report["auto"] = f"kept for the uninstall archive: {e}"
        report["backup"] = str(deploy.uninstall(game, ctx.deploy_state, ctx.backups, fm_running=ctx.fm_running))
        if args.purge_pack and (ctx.pack / fifa_pack.HEADER).is_file():
            shutil.rmtree(ctx.pack)
            report["pack"] = "purged"
        else:
            report["pack"] = "kept" if ctx.pack.is_dir() else "absent"
        final = _read(ctx.deploy_state)
        left = [r for r in current.get("owned_roots", []) + current.get("owned_dirs", []) if (game / r).exists()]
        report.update(status=final["status"], originals_unchanged=all(final["originals_unchanged"].values()),
                      owned_paths_left=left)
        if final["status"] != "uninstalled" or left or not report["originals_unchanged"]:
            raise SetupError(f"uninstall readback failed: {json.dumps(report)}")
    except ERRORS as e:
        out.write(f"error: uninstall: {e}\n")
        return 1
    data = _load_state(ctx)
    for name in ("deploy", "auto-on", "verify"):
        data["steps"].pop(name, None)
    data["uninstalled"] = {**report, "at": _now()}
    _save_state(ctx, data)
    out.write(json.dumps(report, indent=2) + "\n")
    return 0
