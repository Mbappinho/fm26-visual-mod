# SPDX-License-Identifier: MIT
"""Clean-room FIFA 17 CAS span extractor (B4). Written from the local prose contract CONTRACT-CAS (H216),
merged from two blind MIT implementations (H217). Decodes kind-15 blocks only through the pinned zstd CLI."""


import hashlib
import os
import shutil
import subprocess
import tempfile
import threading
import time
from collections import namedtuple
from pathlib import Path


# Project budget on the 24-bit decoded size. The stored size is a 20-bit field (max 1,048,575),
# so the contract's 2 MiB stored-size cap could never fire and is deliberately not implemented.
DECODED_BUDGET = 2 * 1024 * 1024
ZSTD_ARGS = ("--decompress", "--stdout", "--no-progress", "-M2MB", "--")
STDERR_CAP = 16384
CHUNK = 1 << 20
INNER_BLOCK_MAX = 131072
WINDOW_BUDGET = 2 * 1024 * 1024
LEGACY_MAGIC = bytes.fromhex("26b52ffd")
FCS_WIDTH = (0, 1, 2, 8)  # descriptor top 2 bits -> frame-content-size field width


Header = namedtuple("Header", "kind decoded stored")
Block = namedtuple("Block", "index offset payload_offset kind decoded stored")


class CasError(RuntimeError):
    """One refusal/failure type: gate name, detail, span id and block index (if any)."""

    def __init__(self, gate, detail, span_id=None, block=None):
        self.gate, self.detail, self.span_id, self.block = gate, detail, span_id, block
        parts = [f"span={span_id}" if span_id is not None else None, f"gate={gate}",
                 f"block={block}" if block is not None else None, detail]
        super().__init__(" | ".join(p for p in parts if p))


def parse_header(raw, index=0):
    """Bytes 0-3 and 6-7 big-endian; bytes 4 (kind) and 5 (extension) are single bytes."""
    decoded = int.from_bytes(raw[1:4], "big")
    kind = raw[4] & 0x7F
    stored = ((raw[5] & 0x0F) << 16) | int.from_bytes(raw[6:8], "big")
    if raw[0]:
        raise CasError("framing", f"flag byte 0x{raw[0]:02x} refused (dictionary unsupported)", block=index)
    if raw[4] & 0x80:
        raise CasError("framing", f"kind byte 0x{raw[4]:02x} has bit 7 set (refused)", block=index)
    if raw[5] >> 4 != 0x7:
        raise CasError("framing", f"extension upper nibble 0x{raw[5] >> 4:x} != 0x7 (refused)", block=index)
    if kind not in (0, 15):
        raise CasError("framing", f"unsupported kind {kind}", block=index)
    if decoded == 0:
        raise CasError("framing", "decoded size 0 refused", block=index)
    if stored == 0:
        raise CasError("framing", "stored size 0 refused", block=index)
    if decoded > DECODED_BUDGET:
        raise CasError("framing", f"decoded size {decoded} exceeds the {DECODED_BUDGET}-byte budget", block=index)
    if kind == 0 and stored != decoded:
        raise CasError("framing", f"kind 0 stored size {stored} != decoded size {decoded}", block=index)
    return Header(kind, decoded, stored)


def walk(read_at, span_size, expected_total):
    """Validate every header of the span (no decoding); read_at(offset, n) returns span bytes."""
    table, pos, total = [], 0, 0
    while pos < span_size:
        index = len(table)
        if span_size - pos < 8:
            raise CasError("framing", f"truncated header: {span_size - pos} byte(s) left at {pos}", block=index)
        h = parse_header(read_at(pos, 8), index)
        if pos + 8 + h.stored > span_size:
            raise CasError("framing", f"truncated payload: {h.stored} bytes at {pos + 8} overrun {span_size}", block=index)
        total += h.decoded
        if total > expected_total:
            raise CasError("framing", f"running decoded total {total} exceeds expected {expected_total}", block=index)
        table.append(Block(index, pos, pos + 8, h.kind, h.decoded, h.stored))
        pos += 8 + h.stored
    if not table:
        raise CasError("framing", "span contains no block")
    if total != expected_total:
        raise CasError("framing", f"decoded total {total} != expected {expected_total}")
    return table


def check_frame(payload, decoded, index=0):
    """Pre-check of one legacy Zstandard v0.6 frame envelope (a bound gate, not a decoder)."""
    n = len(payload)
    if n < 5:
        raise CasError("envelope", f"payload of {n} bytes is too short for a legacy frame", block=index)
    if payload[:4] != LEGACY_MAGIC:
        raise CasError("envelope", f"magic {payload[:4].hex()} is not the legacy v0.6 magic", block=index)
    desc = payload[4]
    if desc & 0x20:
        raise CasError("envelope", f"reserved descriptor bit set (0x{desc:02x})", block=index)
    width = FCS_WIDTH[desc >> 6]
    window = 1 << ((desc & 0x0F) + 12)
    if window > WINDOW_BUDGET:
        raise CasError("envelope", f"window {window} exceeds {WINDOW_BUDGET}", block=index)
    if width == 0:
        raise CasError("envelope", "frame content size absent (refused: must match the CAS header)", block=index)
    pos = 5 + width
    content = int.from_bytes(payload[5:pos], "little") + (256 if width == 2 else 0)
    if content != decoded:
        raise CasError("envelope", f"frame content size {content} != CAS decoded size {decoded}", block=index)
    inner = 0
    while True:
        if pos + 3 > n:
            raise CasError("envelope", f"missing end block (payload ends at {n})", block=index)
        b0 = payload[pos]
        btype, size = b0 >> 6, ((b0 & 0x07) << 16) | (payload[pos + 1] << 8) | payload[pos + 2]
        pos += 3
        if btype == 3:
            if pos != n:
                raise CasError("envelope", f"{n - pos} byte(s) after the end block", block=index)
            break
        if size > INNER_BLOCK_MAX:
            raise CasError("envelope", f"inner block size {size} exceeds {INNER_BLOCK_MAX}", block=index)
        pos += 1 if btype == 2 else size
        inner += 1
    return {"window": window, "content_size": content, "inner_blocks": inner}


def _verify_tool(exe, pinned_sha256):
    exe = Path(exe)
    if not exe.is_file():
        raise CasError("tool", f"zstd executable not found: {exe}")
    with open(exe, "rb") as f:
        got = hashlib.file_digest(f, "sha256").hexdigest()
    if got != pinned_sha256:
        raise CasError("tool", f"zstd executable sha256 {got} != pinned {pinned_sha256}")
    return exe


def run_native(exe, pinned_sha256, input_path, expected, sink, timeout, launcher=()):
    """One launch of the pinned zstd CLI on input_path; streams exactly `expected` bytes to sink."""
    exe = _verify_tool(exe, pinned_sha256)
    t0 = time.monotonic()
    proc = subprocess.Popen([*launcher, str(exe), *ZSTD_ARGS, str(input_path)], stdin=subprocess.DEVNULL,
                            stdout=subprocess.PIPE, stderr=subprocess.PIPE, shell=False)
    err = bytearray()

    def drain_stderr():  # concurrent drain: a full stderr pipe must never block the stdout reader
        while chunk := proc.stderr.read(4096):
            err.extend(chunk[:max(STDERR_CAP - len(err), 0)])

    reader = threading.Thread(target=drain_stderr, daemon=True)
    reader.start()
    timed_out = threading.Event()

    def on_timeout():
        timed_out.set()
        proc.kill()

    timer = threading.Timer(timeout, on_timeout)
    timer.start()
    got = 0
    try:
        while got < expected and (chunk := proc.stdout.read(min(CHUNK, expected - got))):
            sink(chunk)
            got += len(chunk)
        extra = proc.stdout.read(1) if got == expected else b""
        if extra:
            proc.kill()
        rc = proc.wait()
    finally:
        timer.cancel()
        if proc.poll() is None:
            proc.kill()
            proc.wait()
        reader.join()
        proc.stdout.close()
        proc.stderr.close()
    if timed_out.is_set():
        raise CasError("native-timeout", f"zstd CLI exceeded the {timeout}s timeout (killed)")
    if extra:
        raise CasError("native", f"native output exceeds plan ({expected} bytes expected)")
    if rc != 0:
        raise CasError("native", f"zstd exit {rc} (stderr: {bytes(err).decode('utf-8', 'replace').strip()!r})")
    if err:
        raise CasError("native", f"non-empty stderr: {bytes(err).decode('utf-8', 'replace').strip()!r}")
    if got != expected:
        raise CasError("native", f"short output: {got} of {expected} bytes")
    return {"exit": rc, "seconds": round(time.monotonic() - t0, 3), "output_bytes": got}


class _Sink:
    """Reassembles the span in block order: kind-0 payloads from the verified copy, kind-15 bytes from the CLI."""

    def __init__(self, out, table, read_at):
        self.out, self.table, self.read_at = out, table, read_at
        self.i, self.left, self.total, self.sha = 0, 0, 0, hashlib.sha256()
        self._literals()

    def _emit(self, data):
        self.out.write(data)
        self.sha.update(data)
        self.total += len(data)

    def _literals(self):
        while self.i < len(self.table) and self.table[self.i].kind == 0:
            b = self.table[self.i]
            self._emit(self.read_at(b.payload_offset, b.stored))
            self.i += 1
        self.left = self.table[self.i].decoded if self.i < len(self.table) else 0

    def __call__(self, data):
        view = memoryview(data)
        while len(view):
            if self.left == 0:  # unreachable while run_native caps stdout at the plan; never loop forever
                raise CasError("native", "native output exceeds plan (no kind-15 block pending)")
            take = min(self.left, len(view))
            self._emit(view[:take])
            view, self.left = view[take:], self.left - take
            if self.left == 0:
                self.i += 1
                self._literals()


def _copy_span(src, offset, size, dst):
    """Read exactly [offset, offset+size) once (read-only), SHA-1 while copying into a private work file."""
    h, left = hashlib.sha1(), size
    with open(src, "rb") as f, open(dst, "xb") as out:
        f.seek(offset)
        while left and (chunk := f.read(min(CHUNK, left))):
            h.update(chunk)
            out.write(chunk)
            left -= len(chunk)
    return h.hexdigest()


def extract_span(fifa_root, span, expected_size, expected_sha256, zstd_exe, zstd_sha256, work_dir, out_path,
                 launcher=(), timeout=10, unsupported_message="unsupported FIFA 17 build"):
    """Decode one recipe span into out_path; returns a JSON-serialisable report."""
    try:
        return _extract(fifa_root, span, expected_size, expected_sha256, zstd_exe, zstd_sha256, Path(work_dir),
                        Path(out_path), launcher, timeout, unsupported_message)
    except CasError as e:
        raise CasError(e.gate, e.detail, span["id"], e.block) from None


def _extract(fifa_root, span, expected_size, expected_sha256, zstd_exe, zstd_sha256, work_dir, out_path, launcher,
             timeout, unsupported_message):
    t0 = time.monotonic()
    _verify_tool(zstd_exe, zstd_sha256)  # gate 1 first: never read the game with an unverified tool
    if out_path.exists():
        raise CasError("output", f"refusing to overwrite existing output {out_path}")
    src = Path(fifa_root).joinpath(*span["path"].split("/"))
    if not src.is_file():
        raise CasError("physical", f"CAS file missing: {src}")
    if span["offset"] + span["size"] > src.stat().st_size:
        raise CasError("physical", f"CAS file {src} is shorter than offset {span['offset']} + size {span['size']}")
    work = Path(tempfile.mkdtemp(prefix="cas-", dir=work_dir))
    try:
        stored = work / "stored.bin"
        sha1 = _copy_span(src, span["offset"], span["size"], stored)
        if sha1 != span["sha1"]:
            raise CasError("stored-identity", f"{unsupported_message} (stored sha1 {sha1} != recipe {span['sha1']})")
        partial = work / "decoded.partial"
        with open(stored, "rb") as f:
            def read_at(offset, size):
                f.seek(offset)
                return f.read(size)

            table = walk(read_at, span["size"], expected_size)
            frames = [b for b in table if b.kind == 15]
            for b in frames:
                check_frame(read_at(b.payload_offset, b.stored), b.decoded, b.index)
            frames_path = work / "frames.bin"
            with open(frames_path, "xb") as dst:
                for b in frames:
                    dst.write(read_at(b.payload_offset, b.stored))
            launches = []
            with open(partial, "xb") as out:
                sink = _Sink(out, table, read_at)
                if frames:
                    launches.append(run_native(zstd_exe, zstd_sha256, frames_path, sum(b.decoded for b in frames),
                                               sink, timeout, launcher))
        if sink.sha.hexdigest() != expected_sha256:
            raise CasError("final", f"decoded {sink.total} bytes, sha256 {sink.sha.hexdigest()} != expected {expected_sha256}")
        os.rename(partial, out_path)
        counts = {}
        for b in table:
            counts[str(b.kind)] = counts.get(str(b.kind), 0) + 1
        return {"span_id": span["id"], "stored_sha1": sha1, "blocks_by_kind": counts, "decoded_size": sink.total,
                "decoded_sha256": sink.sha.hexdigest(), "launches": len(launches),
                "exit_codes": [x["exit"] for x in launches], "seconds": round(time.monotonic() - t0, 3)}
    finally:
        shutil.rmtree(work, ignore_errors=True)


def timeout_for(span):
    """Contract 5.4: 180 s for the bank launch, 10 s for a small span."""
    return 180 if span.get("role") == "bank" else 10
