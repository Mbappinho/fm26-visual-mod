#!/usr/bin/env python3
# SPDX-License-Identifier: MIT
"""Leak scanner for the public tree of fm26-visual-mod.

Fails (exit 1) when the scanned tree contains anything that must never be published:
game/engine binary or data files, files over 1 MB, binary blobs, personal paths or identifiers,
secret/token patterns, large base64 blobs, markers of GPL-derived modding tools, EA/SI copyright text,
or committed .NET assemblies (game interop or BepInEx DLLs).

Maintainer-specific terms (real name, user name, account ids...) are NOT stored in this file: pass them with
--private-terms FILE (one term per line, '#' comments) and/or the FM26VM_LEAK_TERMS environment variable
(';'-separated). Matching is case-insensitive.

Usage: python tools/leak_scan.py [ROOT] [--private-terms FILE] [--json REPORT]
"""
import argparse
import json
import os
import re
import subprocess
import sys
from pathlib import Path

MAX_BYTES = 1024 * 1024
SKIP_DIRS = {".git", "__pycache__"}

# Game / engine / asset data and compiled artefacts. Patterns are written so this file never matches itself.
FORBIDDEN_EXT = {
    ".cas", ".toc", ".sb", ".cat", ".bundle", ".pkm", ".ebx", ".res", ".chunk", ".dds", ".fbx", ".bin",
    ".f32le", ".u16le", ".f64le", ".glb", ".gltf", ".obj", ".anim", ".assets", ".ress", ".resource",
    ".dll", ".exe", ".pdb", ".so", ".dylib", ".pyc", ".nupkg", ".zip", ".7z", ".rar", ".gz", ".tar",
    ".npy", ".npz", ".png", ".jpg", ".jpeg", ".bmp", ".tga", ".gif", ".webp", ".ico", ".psd",
    ".wav", ".mp3", ".ogg", ".mp4", ".mkv", ".avi", ".log", ".har", ".etl", ".dmp",
}
# Multi-dot data names produced by the pack builder (e.g. positions.f32le.bin).
FORBIDDEN_NAME = re.compile(r"\.(f32le|u16le|f64le|u8|i16le)\.[a-z0-9]+$", re.I)

RULES = [
    ("personal-path", re.compile(r"[A-Za-z]:(\\\\|\\|/)+Users(\\\\|\\|/)+", re.I)),
    ("personal-path", re.compile(r"/(home|Users)/[a-z][a-z0-9_.-]+/(?!\.\.\.)", re.I)),
    ("personal-path", re.compile(r"Users[\\/]+[^\\/\s\"'<>%$]+[\\/]+AppData", re.I)),
    ("steam-id", re.compile(r"\b7656119\d{10}\b")),
    ("private-ip", re.compile(r"\b192\.168\.\d{1,3}\.\d{1,3}\b|\b10\.\d{1,3}\.\d{1,3}\.\d{1,3}\b")),
    ("secret", re.compile(r"\bgh[opsur]_[A-Za-z0-9]{20,}|github_pat_[A-Za-z0-9_]{20,}")),
    ("secret", re.compile(r"\bsk-[A-Za-z0-9_-]{20,}")),
    ("secret", re.compile(r"\bxox[abposr]-[A-Za-z0-9-]{10,}")),
    ("secret", re.compile(r"\b(AKIA|ASIA)[0-9A-Z]{16}\b")),
    ("secret", re.compile(r"-----BEGIN (RSA |EC |DSA |OPENSSH |PGP |ENCRYPTED )?PRIVATE KEY-----")),
    ("secret", re.compile(r"\b[MN][A-Za-z\d]{23,25}\.[\w-]{6}\.[\w-]{27,}\b")),
    ("secret", re.compile(r"discord(app)?\.com/api/webhooks/\d+", re.I)),
    ("secret", re.compile(r"pass[w]ord|pass[w]d|api[_-]?key\s*[:=]|secret[_-]?key\s*[:=]|bearer\s+[A-Za-z0-9._-]{20,}", re.I)),
    ("base64-blob", re.compile(r"[A-Za-z0-9+/]{2048,}={0,2}")),
    ("hex-blob", re.compile(r"\b[0-9a-fA-F]{4096,}\b")),
    ("gpl-marker", re.compile(r"Fr[o]sty|AssetBan[k]Plugin|FIFA ?Mod(ding)? ?Too[l]|GNU Gene[r]al Public|GNU Lesse[r] General|\bGPL-?[23](\.0)?-or-later\b", re.I)),
    # 'FMT' as the modding tool name (case-sensitive, prose context), not a struct-format identifier.
    ("gpl-marker", re.compile(r"\bF[M]T(?= (?:[Mm]od|[Tt]ool|[Pp]lugin|[Ee]xport|SDK|by|v?\d))")),
    ("ea-copyright", re.compile(r"(©|\(c\)|copyright)\s*(\d{4}[-–, ]*)*\s*(Electronic Ar[t]s|EA Spor[t]s|EA Digital Illusions|Sports Interac[t]ive|SEGA)", re.I)),
    ("ea-copyright", re.compile(r"Electronic Ar[t]s Inc|All righ[t]s reserved", re.I)),
]

TEXT_EXT_HINT = {".py", ".cs", ".csproj", ".md", ".json", ".txt", ".cmd", ".bat", ".ps1", ".gitignore", ".toml",
                 ".yml", ".yaml", ".cfg", ".ini", ".xml", ".sln", ".props", ".targets", ""}


def load_terms(path):
    terms = []
    if path:
        for line in Path(path).read_text(encoding="utf-8").splitlines():
            line = line.strip()
            if line and not line.startswith("#"):
                terms.append(line)
    terms += [t.strip() for t in os.environ.get("FM26VM_LEAK_TERMS", "").split(";") if t.strip()]
    return terms


def tracked_files(root):
    out = subprocess.run(["git", "-C", str(root), "ls-files", "-z"], capture_output=True, check=True).stdout
    return [root / f for f in out.decode("utf-8").split("\0") if f]


def walk_files(root):
    for dirpath, dirnames, filenames in os.walk(root):
        dirnames[:] = sorted(d for d in dirnames if d not in SKIP_DIRS)
        for name in sorted(filenames):
            yield Path(dirpath) / name


def scan(root, terms, tracked=False):
    root = Path(root).resolve()
    findings, files, total = [], 0, 0
    term_res = [(t, re.compile(re.escape(t), re.I)) for t in terms]

    def add(rel, kind, detail, line=None):
        if kind in ("secret", "base64-blob", "hex-blob"):  # never echo a potential secret in the report
            detail = detail[:4] + "...(" + str(len(detail)) + " chars)"
        findings.append({"file": rel, "kind": kind, "line": line, "detail": detail[:160]})

    for p in (tracked_files(root) if tracked else walk_files(root)):
        name = p.name
        rel = p.relative_to(root).as_posix()
        files += 1
        size = p.stat().st_size
        total += size
        ext = p.suffix.lower()
        if ext in FORBIDDEN_EXT or FORBIDDEN_NAME.search(name):
            add(rel, "forbidden-extension", ext or name)
        if size > MAX_BYTES:
            add(rel, "too-large", f"{size} bytes > {MAX_BYTES}")
        for part in Path(rel).parts[:-1]:
            if part.lower() in {"bin", "obj", "local", ".tools", "dist", "spikes", "interop", "packs", "assets"}:
                add(rel, "forbidden-folder", part)
                break
        data = p.read_bytes()
        if b"\x00" in data[:65536]:
            add(rel, "binary-file", "NUL bytes")
            continue
        try:
            text = data.decode("utf-8")
        except UnicodeDecodeError:
            add(rel, "binary-file", "not UTF-8")
            continue
        for t, rx in term_res:
            for m in rx.finditer(text):
                add(rel, "private-term", f"term #{terms.index(t) + 1}", text.count("\n", 0, m.start()) + 1)
        for kind, rx in RULES:
            for m in rx.finditer(text):
                add(rel, kind, m.group(0), text.count("\n", 0, m.start()) + 1)
        if ext == ".csproj":
            for m in re.finditer(r"<(Reference|HintPath)[^>]*>|<HintPath>[^<]*</HintPath>", text):
                ref = m.group(0)
                inc = re.search(r'Include="([^"]+)"', ref)
                target = inc.group(1) if inc else re.sub(r"</?HintPath>", "", ref)
                if target and target.lower().endswith(".dll") and not target.startswith("$("):
                    add(rel, "committed-assembly-reference", target, text.count("\n", 0, m.start()) + 1)
    return {"root_name": root.name, "files": files, "bytes": total, "findings": findings,
            "private_terms_checked": len(terms), "clean": not findings}


def main(argv=None):
    ap = argparse.ArgumentParser(description=__doc__.splitlines()[0])
    ap.add_argument("root", nargs="?", default=str(Path(__file__).resolve().parents[1]))
    ap.add_argument("--private-terms", help="file with maintainer-specific terms (kept outside the repository)")
    ap.add_argument("--json", help="write the report as JSON to this path")
    ap.add_argument("--require-terms", action="store_true", help="fail if no private terms were supplied")
    ap.add_argument("--tracked", action="store_true", help="scan only git-tracked files (ignores local build outputs)")
    a = ap.parse_args(argv)
    terms = load_terms(a.private_terms)
    rep = scan(a.root, terms, tracked=a.tracked)
    if a.require_terms and not terms:
        rep["findings"].append({"file": "-", "kind": "config", "line": None, "detail": "no private terms supplied"})
        rep["clean"] = False
    if a.json:
        Path(a.json).write_text(json.dumps(rep, indent=2, ensure_ascii=False) + "\n", encoding="utf-8")
    for f in rep["findings"]:
        print(f"FAIL {f['kind']:<28} {f['file']}:{f['line'] or '-'}  {f['detail']}")
    print(f"leak_scan: {rep['files']} files, {rep['bytes']} bytes, {len(rep['findings'])} findings, "
          f"{rep['private_terms_checked']} private terms -> {'CLEAN' if rep['clean'] else 'FAIL'}")
    return 0 if rep["clean"] else 1


if __name__ == "__main__":
    sys.exit(main())
