# SPDX-License-Identifier: MIT
"""Public FIFA 17 recipe (numbers/paths/hashes only): load, validate, streaming read-only verification."""


import hashlib
import json
import re
from pathlib import Path


CHUNK = 1 << 20
DEFAULT_RECIPE = Path(__file__).resolve().parent / "recipe-fifa17-v2.json"


class RecipeError(ValueError):
    pass


class VerifyError(RuntimeError):
    pass


SPAN_FIELDS = ("id", "role", "asset", "path", "offset", "size", "sha1", "decoded_size", "decoded_sha256")
CLIP_FIELDS = ("id", "source_id", "asset_key", "asset_name", "frame_range", "loop", "tier", "direction", "keys",
               "keys_per_cycle", "cycles_in_loop", "cycle_origin_key", "native_speed_mps")
BUILD_FIELDS = ("platform", "changelist", "patch", "message")
GOLDEN_FILE_MAPS = ("meshes", "frames_f64le", "pack_v2_files")
FP_FIELDS = ("path", "bytes", "sha256")
TOP_FIELDS = ("schema", "note", "supported_build", "fingerprints", "spans", "clips", "golden")
OPTIONAL_TOP = ("rig",)  # G1 (H219): animation rig key/name read by the GenericData reader
RIG_FIELDS = ("key", "name", "skeleton_key")
GOLDEN_FIELDS = ("decoded_bank", "meshes", "frames_f64le", "pack_v2_files", "pack_v2_build_json_sha256",
                 "gd_reader_sha256", "meshset_reader_sha256")  # output goldens are required (H226b N7)
OPTIONAL_SPAN = ("res_meta_hex", "rid")  # G1 (H223): per-RES ResMeta + RID read by the MeshSet reader


def _keys(obj, allowed, where, optional=()):
    if not isinstance(obj, dict):
        raise RecipeError(f"{where}: must be a JSON object")
    unknown = sorted(set(obj) - set(allowed) - set(optional))
    if unknown:
        raise RecipeError(f"{where}: unknown field(s) {', '.join(unknown)}")
    missing = [k for k in allowed if k not in obj]
    if missing:
        raise RecipeError(f"{where}: missing field(s) {', '.join(missing)}")


def _uint(value):
    return isinstance(value, int) and not isinstance(value, bool) and value >= 0


def _hex(value, length):
    return isinstance(value, str) and len(value) == length and all(c in "0123456789abcdef" for c in value)


def _ident(value):
    return isinstance(value, str) and re.fullmatch(r"[A-Za-z0-9_-]+", value) is not None


def _relative_path(value):
    """A FIFA-relative path: never empty, never climbing out with '..'."""
    if not (isinstance(value, str) and value):
        return False
    if ".." in value.replace("\\", "/").split("/"):
        return False
    if value[0] in "/\\":
        return False
    if ":" in value:
        return False
    return True


def _dec(value):
    """Signed decimal string (asset keys, RIDs): optional '-' then ASCII digits only."""
    return isinstance(value, str) and re.fullmatch(r"-?[0-9]+", value) is not None


def _num(value):
    return isinstance(value, (int, float)) and not isinstance(value, bool)


def _text(value):
    return isinstance(value, str) and bool(value)


CLIP_TYPES = {
    "id": _ident, "source_id": _text, "asset_key": _dec, "asset_name": _text,
    "frame_range": lambda v: isinstance(v, list) and len(v) == 2 and all(_uint(x) for x in v),
    "loop": lambda v: isinstance(v, bool), "tier": _text, "direction": _text, "keys": _uint,
    "keys_per_cycle": lambda v: v is None or _num(v), "cycles_in_loop": lambda v: v is None or _uint(v),
    "cycle_origin_key": lambda v: v is None or _uint(v), "native_speed_mps": _num, "controller_key": _dec,
    "direction_deg_source_left_positive": lambda v: v is None or _num(v),
}


def validate(obj):
    _keys(obj, TOP_FIELDS, "recipe", OPTIONAL_TOP)
    _keys(obj["supported_build"], BUILD_FIELDS, "supported_build")
    message = obj["supported_build"]["message"]
    if not (isinstance(message, str) and message.strip()):
        raise RecipeError("supported_build: message must be a non-empty string")
    for i, fp in enumerate(obj["fingerprints"]):
        _keys(fp, FP_FIELDS, f"fingerprints[{i}]")
        if not _relative_path(fp["path"]):  # H229 REM5
            raise RecipeError(f"fingerprints[{i}]: path must be relative to the FIFA 17 folder (no '..', drive or root)")
        if not _uint(fp["bytes"]):
            raise RecipeError(f"fingerprints[{i}]: bytes must be a non-negative integer")
        if not _hex(fp["sha256"], 64):
            raise RecipeError(f"fingerprints[{i}]: sha256 must be 64 lowercase hex digits")
    for i, span in enumerate(obj["spans"]):
        _keys(span, SPAN_FIELDS, f"spans[{i}]", OPTIONAL_SPAN)
        if not _ident(span["id"]):
            raise RecipeError(f"spans[{i}]: id must match [A-Za-z0-9_-]+")
        if not _relative_path(span["path"]):
            raise RecipeError(f"spans[{i}]: path must be relative to the FIFA 17 folder (no '..', drive or root)")
        sha1 = span["sha1"]
        if not (isinstance(sha1, str) and len(sha1) == 40 and all(c in "0123456789abcdef" for c in sha1)):
            raise RecipeError(f"spans[{i}]: sha1 must be 40 lowercase hex digits")
        if not _uint(span["offset"]):
            raise RecipeError(f"spans[{i}]: offset must be a non-negative integer")
        if not _uint(span["size"]):
            raise RecipeError(f"spans[{i}]: size must be a non-negative integer")
        if not _uint(span["decoded_size"]):
            raise RecipeError(f"spans[{i}]: decoded_size must be a non-negative integer")
        if not _hex(span["decoded_sha256"], 64):
            raise RecipeError(f"spans[{i}]: decoded_sha256 must be 64 lowercase hex digits")
        if "res_meta_hex" in span and not _hex(span["res_meta_hex"], 32):
            raise RecipeError(f"spans[{i}]: res_meta_hex must be 32 lowercase hex digits")
        if "rid" in span and not _dec(span["rid"]):
            raise RecipeError(f"spans[{i}]: rid must be a signed decimal string")
    if "rig" in obj:
        _keys(obj["rig"], RIG_FIELDS, "rig")
    for k in ("key", "skeleton_key") if "rig" in obj else ():
        if not _dec(obj["rig"][k]):
            raise RecipeError(f"rig: {k} must be a signed decimal string")
    for i, clip in enumerate(obj["clips"]):
        _keys(clip, CLIP_FIELDS, f"clips[{i}]", ("controller_key", "direction_deg_source_left_positive"))  # G2 (H219), B8
        for field, ok in CLIP_TYPES.items():
            if field in clip and not ok(clip[field]):
                raise RecipeError(f"clips[{i}]: {field} has the wrong type or format: {clip[field]!r}")
    _keys(obj["golden"], GOLDEN_FIELDS, "golden", ("pack_v2_installed_header",))  # B8
    if not _hex(obj["golden"]["decoded_bank"]["sha256"], 64):
        raise RecipeError("golden.decoded_bank: sha256 must be 64 lowercase hex digits")
    if not _uint(obj["golden"]["decoded_bank"]["bytes"]):
        raise RecipeError("golden.decoded_bank: bytes must be a non-negative integer")
    if "meshset_reader_sha256" in obj["golden"] and not _hex(obj["golden"]["meshset_reader_sha256"], 64):
        raise RecipeError("golden.meshset_reader_sha256 must be 64 lowercase hex digits")
    if not _hex(obj["golden"]["gd_reader_sha256"], 64):
        raise RecipeError("golden.gd_reader_sha256 must be 64 lowercase hex digits")
    if not _hex(obj["golden"]["pack_v2_build_json_sha256"], 64):
        raise RecipeError("golden.pack_v2_build_json_sha256 must be 64 lowercase hex digits")
    if "pack_v2_installed_header" in obj["golden"]:
        entry = obj["golden"]["pack_v2_installed_header"]
        if not (isinstance(entry, dict) and _hex(entry.get("sha256"), 64) and _uint(entry.get("bytes"))):
            raise RecipeError("golden.pack_v2_installed_header: needs bytes (uint) and sha256 (64 lowercase hex digits)")
    for group in GOLDEN_FILE_MAPS:
        for name, entry in obj["golden"][group].items():
            if not _hex(entry["sha256"], 64):
                raise RecipeError(f"golden.{group}[{name}]: sha256 must be 64 lowercase hex digits")
            if not _uint(entry["bytes"]):
                raise RecipeError(f"golden.{group}[{name}]: bytes must be a non-negative integer")
    return obj


def load(path):
    try:
        text = Path(path).read_text(encoding="utf-8")
    except OSError as e:
        raise RecipeError(f"cannot read recipe {path}: {e.strerror or e}")
    try:
        obj = json.loads(text)
    except ValueError as e:
        raise RecipeError(f"recipe {path} is not valid JSON: {e}")
    return validate(obj)


def _digest(open_fn, path, offset, size, algo, counter=None):
    """Hash exactly [offset, offset+size) with seek + bounded reads (never the whole file)."""
    h, left = hashlib.new(algo), size
    if not Path(path).is_file():
        raise VerifyError(f"missing file: {path}")
    with open_fn(path, "rb") as f:
        f.seek(offset)
        while left:
            block = f.read(min(CHUNK, left))
            if not block:
                break
            h.update(block)
            left -= len(block)
            if counter is not None:
                counter.append(len(block))
    if left:
        return "short-read"
    return h.hexdigest()


def verify_fifa17(root, recipe, open_fn=open):
    root, msg, read = Path(root), recipe["supported_build"]["message"], []
    for fp in recipe["fingerprints"]:
        path = root / fp["path"]
        if path.is_file() and path.stat().st_size != fp["bytes"]:
            raise VerifyError(f"{msg} (fingerprint {fp['path']} size {path.stat().st_size} != {fp['bytes']})")
        if _digest(open_fn, root / fp["path"], 0, fp["bytes"], "sha256", read) != fp["sha256"]:
            raise VerifyError(f"{msg} (fingerprint {fp['path']} differs)")
    for span in recipe["spans"]:
        if _digest(open_fn, root / span["path"], span["offset"], span["size"], "sha1", read) != span["sha1"]:
            raise VerifyError(f"{msg} (span {span['id']} SHA1 differs: {span['path']} @{span['offset']}+{span['size']})")
    return {"ok": True, "fingerprints": len(recipe["fingerprints"]), "spans": len(recipe["spans"]), "bytes_read": sum(read)}
