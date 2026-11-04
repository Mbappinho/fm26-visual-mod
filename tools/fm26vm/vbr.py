# SPDX-License-Identifier: MIT
"""Clean-room FIFA17 VBR decoder (roadmap B7: moved unchanged from the H147/H149 clean-room implementation).

Written ONLY from the project's prose contract CONTRACT-VBR.md (H146) by a blind implementer (H147); H149 added the
closed DCT normalisation family `norm` (F1 orthonormal / F2 unnormalised inverse / F3 uniform) and selected F2 on two
targets + 15 held-out clips (the choice between F2 and F3 rests on the predeclared max-p99 tie-break). No third-party
source code was viewed. Python stdlib only. Raw values are preserved: no quaternion normalization, no repair, no
exception swallowing. The pipeline (retarget.build_frames) passes norm='F2'.
"""
import math


def counts(f):
    Q, Vc, F = f['QuaternionCount'], f['Vector3Count'], f['FloatCount']
    CQ, CV, CF = f['ConstQuaternionCount'], f['ConstVector3Count'], f['ConstFloatCount']
    return Q, Vc, F, CQ, CV, CF


def split_header(data, f, block_sizes):
    """Contract §2 'Découpage de Data'. G0 = actual header size equals summed declared sections."""
    Q, Vc, F, CQ, CV, CF = counts(f)
    chan = 4 * Q + 3 * Vc + F
    const_dof = 4 * CQ + 3 * CV + CF
    sizes = [const_dof, f['ConstChanMapSize'], 4 * chan, f['VectorOffsetSize'] + f['FloatOffsetSize']]
    total_blocks = sum(block_sizes)
    if total_blocks > len(data):
        raise ValueError('sum(FrameBlockSizes) exceeds Data length')
    actual = len(data) - total_blocks
    out = dict(chan=chan, const_dof=const_dof, header_size_expected=sum(sizes), header_size_actual=actual,
               section_sizes=dict(palette_indexes=sizes[0], const_chan_map=sizes[1], numbits=sizes[2], offsets=sizes[3]))
    out['G0_pass'] = actual == sum(sizes)
    if not out['G0_pass']:
        return out
    pos = 0
    parts = []
    for n in sizes:
        parts.append(bytes(data[pos:pos + n]))
        pos += n
    out['palette_indexes'], out['const_chan_map'], out['numbits_bytes'], out['offset_bytes'] = parts
    blocks = []
    for n in block_sizes:
        blocks.append(bytes(data[pos:pos + n]))
        pos += n
    if pos != len(data):
        raise ValueError('internal: Data not exactly consumed')
    out['blocks'] = blocks
    return out


def channel_layout(ccm, f):
    """Global channel order (quats+vec3 only; floats are outside this space per contract).

    ConstChanMap = [start, n0, skip1, n1, skip2, n2, ...] (size 1+2k). Interpretation (documented
    ambiguity A2): runs of n_i constant channels separated by skip_i free indices; a trailing element
    of an odd-size map is a final skip and places nothing. The first CQ constants are quaternions, the
    next CV are vec3. Free indices are filled by the Q variable quats then the V variable vec3, in order.
    ConstChanMapSize == 0 is only accepted when CQ+CV == 0 (no out-of-range read)."""
    Q, Vc, F, CQ, CV, CF = counts(f)
    total = Q + CQ + Vc + CV
    nconst = CQ + CV
    const_pos = []
    if len(ccm) == 0:
        if nconst:
            raise ValueError('ConstChanMapSize=0 but constant quat/vec3 channels declared')
    else:
        pos = ccm[0]
        i = 1
        while i < len(ccm):
            n = ccm[i]
            const_pos.extend(range(pos, pos + n))
            pos += n
            if i + 1 < len(ccm):
                pos += ccm[i + 1]
            i += 2
    if len(const_pos) != nconst or len(set(const_pos)) != nconst or any(p < 0 or p >= total for p in const_pos):
        raise ValueError('ConstChanMap does not place exactly CQ+CV distinct in-range constants')
    lay = [None] * total
    for j, p in enumerate(const_pos):
        lay[p] = ('Q', 'const', j) if j < CQ else ('V', 'const', j - CQ)
    free = [i for i in range(total) if lay[i] is None]
    for j, p in enumerate(free):
        lay[p] = ('Q', 'var', j) if j < Q else ('V', 'var', j - Q)
    return lay


def channel_numbits(four):
    """Contract: 4 bytes -> 8 nibbles, high nibble first; then pair permutation (1,0,3,2,5,4,7,6)."""
    if len(four) != 4:
        raise ValueError('numbits needs 4 bytes per channel')
    n = []
    for b in four:
        n += [b >> 4, b & 15]
    return [n[p] for p in (1, 0, 3, 2, 5, 4, 7, 6)]


class BitReader:
    """Block payload reader. Default = contract: bytes taken last->first, each byte bit-reversed, then
    bits read MSB first and multi-bit values assembled MSB first. Flags exist only for G3 controls."""

    def __init__(self, data, byte_reverse=True, bit_reverse=True, msb_first=True):
        seq = list(reversed(data)) if byte_reverse else list(data)
        bits = []
        for b in seq:
            msb = [(b >> (7 - i)) & 1 for i in range(8)]  # MSB first
            bits += msb[::-1] if bit_reverse else msb
        self.bits = bits
        self.pos = 0
        self.msb_first = msb_first

    def read(self, n):
        if self.pos + n > len(self.bits):
            raise ValueError('bitstream overrun')
        chunk = self.bits[self.pos:self.pos + n]
        self.pos += n
        if not self.msb_first:
            chunk = chunk[::-1]
        v = 0
        for b in chunk:
            v = (v << 1) | b
        return v


def reverse_bits(v, n):
    r = 0
    for _ in range(n):
        r = (r << 1) | (v & 1)
        v >>= 1
    return r


def parse_block(block, numbits, byte_reverse=True, bit_reverse=True, msb_first=True, mag_reverse=True):
    """One frame block: 3 table bytes (rot, trj, tra) then the bitstream. Per scalar channel (ambiguity A3:
    the three phases are taken per channel): presence bits for coefficients with numbits>0, sign bits for
    present ones (0 = negative), magnitudes on numbits bits, bit-reversed. Absent coefficient = 0."""
    if len(block) < 3:
        raise ValueError('block shorter than 3 table bytes')
    r = BitReader(block[3:], byte_reverse, bit_reverse, msb_first)
    coeffs = []
    for nb in numbits:
        present = [k for k in range(8) if nb[k] > 0 and r.read(1)]
        signs = {k: r.read(1) for k in present}
        c = [0] * 8
        for k in present:
            m = r.read(nb[k])
            if mag_reverse:
                m = reverse_bits(m, nb[k])
            c[k] = m if signs[k] else -m
        coeffs.append(c)
    total = len(r.bits)
    pad = r.bits[r.pos:]
    return dict(tables=tuple(block[:3]), coeffs=coeffs, bits_consumed=r.pos, bits_total=total,
                padding_bits=pad, G1_pass=0 <= total - r.pos < 8)


# H149: closed predeclared normalisation family (spike-149-150-batch PLAN.family). Per member: (a_0, a_i>0).
NORMS = {'F1': (math.sqrt(1 / 8), 0.5),   # F1_orthonormal, == H147/H148 (sqrt(2/8) == 1/2)
         'F2': (1 / 8, 2 / 8),            # F2_unnormalised_inverse
         'F3': (1 / 8, 1 / 8)}            # F3_uniform


def idct_matrix(norm='F1'):
    """Ambiguity A4: the contract names an '8-point DCT' without its normalization. H147 chose F1 (orthonormal
    DCT-III, M[k][i] = a_i cos(pi (2k+1) i / 16), a_0 = sqrt(1/8), a_i = 1/2). H149 adds the switch norm in
    NORMS; F1 keeps the exact H147 expression (byte-identical regression anchor)."""
    if norm not in NORMS:
        raise ValueError('unknown DCT normalisation %r' % (norm,))
    a0, ai = NORMS[norm]
    return [[(a0 if i == 0 else ai) * math.cos(math.pi * (2 * k + 1) * i / 16) for i in range(8)]
            for k in range(8)]


_MS = {n: idct_matrix(n) for n in NORMS}
_M = _MS['F1']


def coeff_step(table, dct, k):
    """q_k = (scale*ln(k+2)+1)*Dct/32768, scale = (table+1)*0.2*Dct, Dct<=0 -> 4."""
    if dct <= 0:
        dct = 4.0
    scale = (table + 1) * 0.2 * dct
    return (scale * math.log(k + 2) + 1) * dct / 32768


def dequant_channel(coeffs, table, dct, vmin, vmax, norm='F1'):
    """8 frame values of one scalar channel in one block: (0.5 + sum_i c_i M[k][i] q_i)(Max-Min)+Min."""
    if norm not in _MS:
        raise ValueError('unknown DCT normalisation %r' % (norm,))
    M = _MS[norm]
    q = [coeff_step(table, dct, i) for i in range(8)]
    out = []
    for k in range(8):
        s = 0.0
        for i in range(8):
            if coeffs[i]:
                s += coeffs[i] * M[k][i] * q[i]
        out.append((0.5 + s) * (vmax - vmin) + vmin)
    return out


def frame_sources(n):
    """(block, row) for each frame. Uniform frames, frame i = key i (no i*3 clock). Last block covers the
    last 8 frames: row = frame - (N-8) (contract §2); earlier blocks: row = frame % 8."""
    nblocks = (n + 7) // 8
    out = []
    for fr in range(n):
        b = fr // 8
        out.append((b, fr - (n - 8)) if b == nblocks - 1 and nblocks > 1 and n % 8 else (b, fr % 8))
    return out


def component_ranges(f):
    """Per scalar channel (table name, Min, Max): quats -> rot/Quat; first 3 vec3 components -> trj/Traj;
    other vec3 components -> tra/Vec3; floats -> tra/Float."""
    Q, Vc, F = f['QuaternionCount'], f['Vector3Count'], f['FloatCount']
    r = [('rot', f['QuatMin'], f['QuatMax'])] * (4 * Q)
    for j in range(3 * Vc):
        r.append(('trj', f['TrajMin'], f['TrajMax']) if j < 3 else ('tra', f['Vec3Min'], f['Vec3Max']))
    r += [('tra', f['FloatMin'], f['FloatMax'])] * F
    return r


def _sbyte(b):
    return b - 256 if b > 127 else b


def parse_offset_curves(buf):
    """count, then per curve: skip, pointCount, y0 (sbyte), then (pointCount-1) pairs (spanBlocks, y sbyte).
    Ambiguity A7: channel = previous channel + 1 + skip (first curve: skip); y read signed; pointCount
    counts y0. Exact consumption required."""
    if not buf:
        return []
    count = buf[0]
    pos = 1
    curves = []
    ch = -1
    for _ in range(count):
        if pos + 3 > len(buf):
            raise ValueError('offset curve header overrun')
        skip, pc, y0 = buf[pos], buf[pos + 1], _sbyte(buf[pos + 2])
        pos += 3
        if pc < 1:
            raise ValueError('pointCount 0')
        ch = ch + 1 + skip
        pts = [(0, y0)]
        for _ in range(pc - 1):
            if pos + 2 > len(buf):
                raise ValueError('offset curve points overrun')
            pts.append((buf[pos], _sbyte(buf[pos + 1])))
            pos += 2
        curves.append(dict(channel4=ch, points=pts))
    if pos != len(buf):
        raise ValueError('offset section not exactly consumed')
    return curves


def eval_curve(curve, n, scale):
    """Per-frame offset: linear between points over spanBlocks*8 frames, hold after last point; y*scale."""
    pts = curve['points']
    out = []
    for t in range(n):
        start = 0
        y = pts[0][1]
        for span, y1 in pts[1:]:
            length = span * 8
            if t < start + length:
                y = y + (y1 - y) * (t - start) / length
                break
            start += length
            y = y1
        out.append(y * scale)
    return out


def offset_component(channel4):
    """Stride-4 vec3 component index -> stride-3 scalar component index; a 4th component is invalid."""
    if channel4 % 4 == 3:
        raise ValueError('offset curve on 4th vec component')
    return channel4 // 4 * 3 + channel4 % 4


def pct_nearest(xs, p):
    s = sorted(xs)
    return s[max(1, math.ceil(p / 100 * len(s))) - 1]


def quat_norm_stats(quats):
    """G2 over raw (never normalized) quaternions: median and p99 (nearest rank) of |norm-1|."""
    dev = [abs(math.sqrt(sum(c * c for c in q)) - 1) for q in quats]
    med = pct_nearest(dev, 50) if len(dev) % 2 else sorted(dev)[len(dev) // 2 - 1] / 2 + sorted(dev)[len(dev) // 2] / 2
    p99 = pct_nearest(dev, 99)
    return dict(n=len(dev), median_abs_dev=med, p99_abs_dev=p99, max_abs_dev=max(dev),
                G2_pass=med <= 0.01 and p99 <= 0.05)


def decode(f, data, block_sizes, palette, byte_reverse=True, bit_reverse=True, msb_first=True, mag_reverse=True,
           norm='F1'):
    """Full decode. Returns gate data and, when G0 holds, per-channel per-frame raw values in global channel
    order (quat/vec3 space from ConstChanMap, then var floats, then const floats — ambiguity A9)."""
    if norm not in _MS:
        raise ValueError('unknown DCT normalisation %r' % (norm,))
    Q, Vc, F, CQ, CV, CF = counts(f)
    n = f['NumKeys']
    h = split_header(data, f, block_sizes)
    out = dict(G0_pass=h['G0_pass'], header=dict((k, h[k]) for k in ('chan', 'const_dof', 'header_size_expected',
                                                                    'header_size_actual', 'section_sizes')))
    if not h['G0_pass']:
        return out
    if len(block_sizes) != (n + 7) // 8:
        raise ValueError('block count != ceil(NumKeys/8)')
    chan = h['chan']
    nb = [channel_numbits(h['numbits_bytes'][4 * c:4 * c + 4]) for c in range(chan)]
    ranges = component_ranges(f)
    tidx = {'rot': 0, 'trj': 1, 'tra': 2}
    blocks, blockvals = [], []
    for b in h['blocks']:
        r = parse_block(b, nb, byte_reverse, bit_reverse, msb_first, mag_reverse)
        blocks.append(dict(size=len(b), tables=r['tables'], bits_total=r['bits_total'], bits_consumed=r['bits_consumed'],
                           padding_bits=r['padding_bits'], G1_pass=r['G1_pass']))
        blockvals.append([dequant_channel(r['coeffs'][c], r['tables'][tidx[ranges[c][0]]], f['Dct'], ranges[c][1], ranges[c][2], norm)
                          for c in range(chan)])
    out['blocks'] = blocks
    src = frame_sources(n)
    scal = [[blockvals[b][c][row] for (b, row) in src] for c in range(chan)]  # scal[c][frame]
    # offset curves: first VectorOffsetSize bytes -> var vec3 components; then FloatOffsetSize -> var floats
    vos = f['VectorOffsetSize']
    vcurves = parse_offset_curves(h['offset_bytes'][:vos])
    fcurves = parse_offset_curves(h['offset_bytes'][vos:])
    vscale, fscale = f['VectorOffsetScale'] / 127, f['FloatOffsetScale'] / 127
    applied = []
    for cv in vcurves:
        comp = offset_component(cv['channel4'])
        if comp >= 3 * Vc:
            raise ValueError('vector offset curve beyond vec3 channels')
        add = eval_curve(cv, n, vscale)
        c = 4 * Q + comp
        scal[c] = [a + b for a, b in zip(scal[c], add)]
        applied.append(dict(kind='V', channel4=cv['channel4'], var_vec3=comp // 3, component=comp % 3, points=cv['points']))
    for cf in fcurves:
        if cf['channel4'] >= F:
            raise ValueError('float offset curve beyond float channels')
        add = eval_curve(cf, n, fscale)
        c = 4 * Q + 3 * Vc + cf['channel4']
        scal[c] = [a + b for a, b in zip(scal[c], add)]
        applied.append(dict(kind='F', float_index=cf['channel4'], points=cf['points']))
    out['offset_curves'] = applied
    # constants
    pidx = h['palette_indexes']
    if any(i >= len(palette) for i in pidx):
        raise ValueError('palette index out of range')
    cvals = [palette[i] for i in pidx]
    def cconv(v, lo, hi):
        return v * (hi - lo) + lo
    cq = [[cconv(cvals[4 * j + k], f['QuatMin'], f['QuatMax']) for k in range(4)] for j in range(CQ)]
    o = 4 * CQ
    cvv = [[cconv(cvals[o + 3 * j + k], f['Vec3Min'], f['Vec3Max']) for k in range(3)] for j in range(CV)]
    o += 3 * CV
    cff = [[cconv(cvals[o + j], f['FloatMin'], f['FloatMax'])] for j in range(CF)]
    lay = channel_layout(h['const_chan_map'], f)
    channels = []
    for gi, (kind, source, j) in enumerate(lay):
        if source == 'var':
            base = 4 * j if kind == 'Q' else 4 * Q + 3 * j
            w = 4 if kind == 'Q' else 3
            vals = [[scal[base + k][t] for k in range(w)] for t in range(n)]
        else:
            vals = [list(cq[j] if kind == 'Q' else cvv[j])] * n
        channels.append(dict(index=gi, kind=kind, source=source, ordinal=j, values=vals))
    for j in range(F):
        channels.append(dict(index=len(channels), kind='F', source='var', ordinal=j,
                             values=[[scal[4 * Q + 3 * Vc + j][t]] for t in range(n)]))
    for j in range(CF):
        channels.append(dict(index=len(channels), kind='F', source='const', ordinal=j, values=[cff[j]] * n))
    out['channels'] = channels
    out['frame_index'] = list(range(n))
    return out
