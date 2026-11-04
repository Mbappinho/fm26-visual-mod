#!/usr/bin/env python3
# SPDX-License-Identifier: MIT
"""Build the public tree of fm26-visual-mod from an explicit ALLOWLIST (never a denylist).

Only the files listed in ALLOW (globs relative to the source checkout) are copied; everything else -- local
experiment folders, game captures, build outputs, packs, downloaded tools, internal notes -- is left out by
construction. A few files are transformed on the way (TRANSFORMS), files from an optional overlay folder
(public README / docs) are added, then tools/leak_scan.py is run on the result.

Usage:
  python tools/export_public.py --source <checkout> --dest <empty or previous export folder>
         [--overlay <folder with public README.md, docs/...>] [--private-terms FILE]

The destination is wiped (except its .git folder) only if it is empty or carries the EXPORT_MARKER of a previous run.
"""
import argparse
import fnmatch
import json
import re
import shutil
import subprocess
import sys
from pathlib import Path

EXPORT_MARKER = ".fm26vm-public-export"

ALLOW = [
    # plugin
    "src/*.cs",
    "FM26.VisualDiagnostics.csproj",
    # installer: fm26vm + deploy.py + fifa_pack.py (closed import graph, checked by check_imports)
    "tools/deploy.py",
    "tools/fifa_pack.py",
    "tools/fm26vm/*.py",
    "tools/fm26vm/recipe-fifa17-v2.json",
    "tools/fm26vm/release.json",
    # tests that pass on synthetic data only (verified in the export; real-data cases skip themselves)
    "tests/__init__.py",
    "tests/multi_pass_legacy_projection.py",
    "tests/fixtures/fm26vm/*.py",
    "tests/PolicyTests/*.cs",
    "tests/PolicyTests/PolicyTests.csproj",
    "tests/test_candidate_overlay_integration.py",
    "tests/test_deploy.py",
    "tests/test_deploy_h226.py",
    "tests/test_deploy_h239.py",
    "tests/test_fifa_cross_contract.py",
    "tests/test_fifa_locomotion_integration.py",
    "tests/test_fifa_overlay_integration.py",
    "tests/test_fifa_pack.py",
    "tests/test_fifa_pack_v2.py",
    "tests/test_fifa_stability.py",
    "tests/test_fm26vm_*.py",
    "tests/test_h178_keeper_wiring.py",
    "tests/test_h186_auto_wiring.py",
    # legal / meta
    "LICENSE",
    "THIRD-PARTY-NOTICES.md",
    ".gitignore",
]

# Self-copies: the maintainer tools that produced the tree are published with it.
SELF = {"tools/export_public.py": Path(__file__).resolve(),
        "tools/leak_scan.py": Path(__file__).resolve().with_name("leak_scan.py")}

GITIGNORE_EXTRA = """
# --- public export additions ---
# Never commit local experiments, downloaded tools, build outputs or private packs.
local/
.tools/
dist/
spikes/
packs/
*.dll
*.exe
*.zip
*.f32le
*.u16le
*.f64le
*.sb
*.cat
*.ebx
*.res
*.chunk
*.dds
*.fbx
*.glb
*.bin
"""

RELEASE_KEYS = ("file", "version", "bytes", "sha256")


def t_license(text):
    new, n = re.subn(r"(?m)^Copyright \(c\) 2026 .*$", "Copyright (c) 2026 Mbappinho", text)
    if n != 1:
        raise SystemExit("LICENSE: copyright line not found exactly once")
    return new


def t_release(text):
    data = json.loads(text)
    data["plugin"] = {k: data["plugin"][k] for k in RELEASE_KEYS if k in data["plugin"]}
    return json.dumps(data, indent=2) + "\n"


def t_csproj(text):
    old = re.search(r"<InteropDir Condition=\"'\$\(InteropDir\)' == ''\">[^<]*</InteropDir>", text)
    if not old:
        raise SystemExit("csproj: InteropDir default not found")
    fm = ("<FM26Dir Condition=\"'$(FM26Dir)' == ''\">$(MSBuildProgramFiles32)/Steam/steamapps/common/"
          "Football Manager 26</FM26Dir>\n    ")
    return text.replace(old.group(0), fm + "<InteropDir Condition=\"'$(InteropDir)' == ''\">"
                        "$(FM26Dir)/BepInEx/interop</InteropDir>")


def t_gitignore(text):
    return text.rstrip("\n") + "\n" + GITIGNORE_EXTRA


def t_fake_profile(text):
    # Synthetic test paths: use a neutral placeholder root instead of a Windows user-profile path.
    new, n = re.subn(r"C:(\\\\|\\)Users(\\\\|\\)([ux])(\\\\|\\)AppData", r"C:\1Profiles\2\3\4AppData", text)
    if not n:
        raise SystemExit("fake profile path not found")
    return new


TRANSFORMS = {"LICENSE": t_license,
              "tests/PolicyTests/FifaLocomotionTests.cs": t_fake_profile,
              "tests/PolicyTests/FifaOverlayPolicyTests.cs": t_fake_profile, "tools/fm26vm/release.json": t_release,
              "FM26.VisualDiagnostics.csproj": t_csproj, ".gitignore": t_gitignore}


def select(source):
    picked = {}
    for pattern in ALLOW:
        hits = [p for p in sorted(source.glob(pattern)) if p.is_file() and "__pycache__" not in p.parts]
        if not hits:
            raise SystemExit(f"allowlist entry matched nothing: {pattern}")
        for p in hits:
            picked[p.relative_to(source).as_posix()] = p
    return picked


def check_imports(dest):
    """Every first-party import in the exported Python must resolve inside the export."""
    missing = []
    for py in sorted(dest.rglob("*.py")):
        rel = py.relative_to(dest)
        text = py.read_text(encoding="utf-8")
        for m in re.finditer(r"(?m)^\s*from (tools(?:\.\w+)*) import ([\w, ]+)|^\s*import (tools(?:\.\w+)+)", text):
            mod = m.group(1) or m.group(3)
            names = [n.split(" as ")[0].strip() for n in (m.group(2) or "").split(",") if n.strip()]
            base = dest.joinpath(*mod.split("."))
            for n in names or [None]:
                cands = [base.with_suffix(".py"), base / "__init__.py", base]
                if n:
                    cands += [base / f"{n}.py"]
                if not any(c.exists() for c in cands):
                    missing.append(f"{rel}: {mod}{'.' + n if n else ''}")
        if rel.parts[0] == "tools" and len(rel.parts) > 2:
            for m in re.finditer(r"(?m)^\s*from (\.+)(\w*) import ([\w, ]+)", text):
                up = py.parent
                for _ in range(len(m.group(1)) - 1):
                    up = up.parent
                if m.group(2):
                    if not ((up / f"{m.group(2)}.py").exists() or (up / m.group(2)).is_dir()):
                        missing.append(f"{rel}: {m.group(0).strip()}")
                else:
                    for n in [n.split(" as ")[0].strip() for n in m.group(3).split(",")]:
                        if not ((up / f"{n}.py").exists() or (up / n).is_dir()):
                            missing.append(f"{rel}: {m.group(0).strip()} ({n})")
        for m in re.finditer(r"ROOT\s*/\s*\"tools\"\s*/\s*\"(\w+\.py)\"|tools/(\w+\.py)", text):
            name = m.group(1) or m.group(2)
            if not (dest / "tools" / name).exists() and not (dest / "tools" / "fm26vm" / name).exists():
                missing.append(f"{rel}: path reference tools/{name}")
    return missing


def clean_dest(dest):
    if dest.exists():
        entries = [e for e in dest.iterdir() if e.name != ".git"]
        if entries and not (dest / EXPORT_MARKER).exists():
            raise SystemExit(f"{dest} is not empty and is not a previous export (no {EXPORT_MARKER}); refusing")
        for e in entries:
            shutil.rmtree(e) if e.is_dir() else e.unlink()
    dest.mkdir(parents=True, exist_ok=True)


def main(argv=None):
    ap = argparse.ArgumentParser(description=__doc__.splitlines()[0])
    ap.add_argument("--source", required=True)
    ap.add_argument("--dest", required=True)
    ap.add_argument("--overlay", help="folder whose files are added on top (public README.md, docs/...)")
    ap.add_argument("--private-terms", help="maintainer terms file for leak_scan (kept outside the repository)")
    ap.add_argument("--no-scan", action="store_true")
    a = ap.parse_args(argv)
    source, dest = Path(a.source).resolve(), Path(a.dest).resolve()
    if dest == source or source in dest.parents:
        raise SystemExit("destination must be outside the source checkout")
    picked = select(source)
    clean_dest(dest)
    (dest / EXPORT_MARKER).write_text("generated by tools/export_public.py" + chr(10), encoding="utf-8")
    for rel, src in picked.items():
        out = dest / rel
        out.parent.mkdir(parents=True, exist_ok=True)
        if rel in TRANSFORMS:
            text = src.read_text(encoding="utf-8")
            out.write_text(TRANSFORMS[rel](text), encoding="utf-8", newline="")
        else:
            shutil.copyfile(src, out)
    if a.overlay:
        ov = Path(a.overlay).resolve()
        for p in sorted(ov.rglob("*")):
            if p.is_file():
                out = dest / p.relative_to(ov)
                out.parent.mkdir(parents=True, exist_ok=True)
                shutil.copyfile(p, out)
    for rel, src in SELF.items():
        (dest / rel).parent.mkdir(parents=True, exist_ok=True)
        shutil.copyfile(src, dest / rel)
    missing = check_imports(dest)
    files = sorted(p.relative_to(dest).as_posix() for p in dest.rglob("*") if p.is_file() and ".git" not in p.parts)
    print(f"export: {len(files)} files -> {dest}")
    if missing:
        print("unresolved first-party imports:\n  " + "\n  ".join(missing))
        return 2
    if a.no_scan:
        return 0
    cmd = [sys.executable, "-B", str(dest / "tools" / "leak_scan.py"), str(dest)]
    if a.private_terms:
        cmd += ["--private-terms", a.private_terms, "--require-terms"]
    return subprocess.call(cmd)


if __name__ == "__main__":
    sys.exit(main())
