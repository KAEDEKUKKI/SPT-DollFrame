#!/usr/bin/env python3
"""GF2 dataset extractor: PMX bones from a .gfc package, game skeleton from a .bundle.

Modes:
  python3 tools/extract_bones.py --pmx <pkg.gfc|model.pmx> --out <json> [--check <baseline.json>]
  python3 tools/extract_bones.py --skeleton <skeleton.bundle> --out <json> [--check <baseline.json>]

See docs/DATA.md for the schemas this tool reproduces.
"""

import argparse
import json
import struct
import sys

MAGIC_GFCP = b"GFCP"
MAGIC_PMX = b"PMX "

BONE_LAYOUTS = ("gfl2", "stock")

FLAG_BIT = {"rot": 0x0002, "mov": 0x0004, "ik": 0x0020, "ctl": 0x0010, "phys": 0x0200}


class ExtractError(Exception):
    pass


class Reader:
    def __init__(self, data):
        self.data = data
        self.pos = 0

    def need(self, n, what):
        if self.pos + n > len(self.data):
            raise ExtractError(f"truncated PMX at {self.pos}: need {n} bytes for {what}")

    def u8(self):
        self.need(1, "u8")
        v = self.data[self.pos]
        self.pos += 1
        return v

    def i32(self):
        self.need(4, "i32")
        (v,) = struct.unpack_from("<i", self.data, self.pos)
        self.pos += 4
        return v

    def f32(self, n=1):
        self.need(4 * n, "f32x%d" % n)
        v = struct.unpack_from("<" + "f" * n, self.data, self.pos)
        self.pos += 4 * n
        return v

    def idx(self, size):
        widths = {1: 1, 2: 2, 4: 4}
        if size not in widths:
            raise ExtractError(f"unsupported index size {size}")
        fmt = {1: "<h", 2: "<h", 4: "<i"}[size]
        self.need(widths[size], "idx")
        (v,) = struct.unpack_from(fmt, self.data, self.pos)
        self.pos += widths[size]
        return v

    def string(self, encoding):
        self.need(4, "string length")
        (n,) = struct.unpack_from("<i", self.data, self.pos)
        self.pos += 4
        if n < 0 or self.pos + n > len(self.data):
            raise ExtractError(f"bad string length {n} at {self.pos - 4}")
        s = self.data[self.pos : self.pos + n].decode(encoding, "replace")
        self.pos += n
        return s


def read_container(path):
    with open(path, "rb") as fh:
        buf = fh.read()
    if buf[:4] == MAGIC_PMX:
        return f"{path} (raw pmx)", buf
    if buf[:4] != MAGIC_GFCP:
        raise ExtractError(f"unknown container magic {buf[:4]!r} in {path}")
    r = Reader(buf)
    r.pos = 4
    version = r.i32()
    if version != 1:
        raise ExtractError(f"GFCP version {version} not verified; expected 1 ({path})")
    name_len = r.i32()
    name = buf[r.pos : r.pos + name_len].decode("utf-8", "replace")
    r.pos += name_len
    fn_len = r.i32()
    pmx_name = buf[r.pos : r.pos + fn_len].decode("utf-8", "replace")
    r.pos += fn_len
    data_len = r.i32()
    pmx = buf[r.pos : r.pos + data_len]
    r.pos += data_len
    tex_count = r.i32()
    for _ in range(tex_count):
        p_len = r.i32()
        r.pos += p_len
        d_len = r.i32()
        r.pos += d_len
    if r.pos != len(buf):
        raise ExtractError(f"GFCP trailing {len(buf) - r.pos} bytes unread ({path})")
    return f"{name} / {pmx_name}", pmx


def parse_bones(pmx):
    if pmx[:4] != MAGIC_PMX:
        raise ExtractError(f"missing PMX magic: {pmx[:4]!r}")
    header = Reader(pmx)
    header.pos = 4
    header.f32(1)
    gcount = header.u8()
    if gcount != 8:
        raise ExtractError(f"expected 8 global counts, got {gcount}")
    _enc, adduv, _vtx, _tex, _mat, bone_idx, _morph, _rb = (header.u8() for _ in range(8))

    def walk(layout):
        r = Reader(pmx)
        r.pos = 4 + 4 + 1 + 8
        for _ in range(4):
            r.string("utf-16-le")
        vcount = r.i32()
        for _ in range(vcount):
            r.need(12 + 12 + 8 + adduv * 16 + 1, "vertex")
            r.pos += 12 + 12 + 8 + adduv * 16
            kind = r.u8()
            if kind == 0:
                r.pos += bone_idx
            elif kind == 1:
                r.pos += 2 * bone_idx + 4
            elif kind in (2, 4):
                r.pos += 4 * bone_idx + 16
            elif kind == 3:
                r.pos += 2 * bone_idx + 4 + 36
            else:
                raise ExtractError(f"unknown weight kind {kind} at {r.pos}")
            r.pos += 4
        fcount = r.i32()
        if fcount < 0:
            raise ExtractError(f"negative face count {fcount}")
        r.pos += fcount * _vtx
        tcount = r.i32()
        for _ in range(tcount):
            r.string("utf-16-le")
        mcount = r.i32()
        for _ in range(mcount):
            r.string("utf-16-le")
            r.string("utf-16-le")
            r.f32(4)
            r.f32(3)
            r.i32()
            r.f32(3)
            r.u8()
            r.f32(4)
            r.f32(1)
            r.idx(_tex)
            r.idx(_tex)
            r.u8()
            shared = r.u8()
            if shared == 1:
                r.u8()
            else:
                r.idx(_tex)
            r.string("utf-16-le")
            r.i32()
        bcount = r.i32()
        if bcount <= 0:
            raise ExtractError(f"bad bone count {bcount}")
        bones = []
        for i in range(bcount):
            bn = r.string("utf-16-le")
            ben = r.string("utf-16-le")
            px, py, pz = r.f32(3)
            if layout == "gfl2":
                praw = r.i32()
                parent = praw & 0xFFFF
                if parent == 0xFFFF:
                    parent = -1
                layer = r.u8()
                r.u8()
                flags = r.idx(2)
            else:
                praw = None
                parent = r.i32()
                layer = r.u8()
                flags = r.idx(2)
            if flags & 1:
                r.idx(bone_idx)
            else:
                r.need(12, "tail vec3")
                r.pos += 12
            if layout == "gfl2":
                if flags & 0x100:
                    r.idx(bone_idx)
                    r.f32(1)
                if flags & 0x400:
                    r.pos += 12
                if flags & 0x800:
                    r.pos += 24
                if flags & 0x20:
                    r.idx(bone_idx)
                    r.i32()
                    r.f32(1)
                    nlinks = r.i32()
                    for _ in range(nlinks):
                        r.idx(bone_idx)
                        if r.u8():
                            r.pos += 24
            else:
                if flags & 0x40:
                    r.idx(bone_idx)
                    r.f32(1)
                if flags & 0x80:
                    r.pos += 12
                if flags & 0x100:
                    r.pos += 24
                if flags & 0x200:
                    r.idx(bone_idx)
                    r.f32(1)
                if flags & 0x400:
                    r.i32()
                if flags & 0x20:
                    r.idx(bone_idx)
                    r.i32()
                    r.f32(1)
                    nlinks = r.i32()
                    for _ in range(nlinks):
                        r.idx(bone_idx)
                        lf = r.u8()
                        if lf & 1:
                            r.pos += 12
                        if lf & 2:
                            r.pos += 12
            bones.append({
                "i": i, "n": bn, "en": ben, "p": parent, "praw": praw,
                "layer": layer, "flags": flags, "pos": [px, py, pz],
            })
        names = [b["n"] for b in bones]
        if len(names) != len(set(names)):
            raise ExtractError(f"bone names not unique ({bcount} bones)")
        parents = [b["p"] for b in bones]
        bad = [p for p in parents if p < -1 or p >= bcount]
        if bad:
            raise ExtractError(f"parent index out of range: {bad[:5]}")
        if bones[0]["p"] != -1:
            raise ExtractError(f"bone 0 parent is {bones[0]['p']}, expected -1")
        return bones

    errors = []
    for layout in BONE_LAYOUTS:
        try:
            return layout, walk(layout)
        except (struct.error, ExtractError, IndexError, OverflowError) as exc:
            errors.append(f"{layout}: {exc}")
    raise ExtractError("could not parse PMX bones:\n  " + "\n  ".join(errors))


def extract_pmx(path):
    meta, pmx = read_container(path)
    layout, bones = parse_bones(pmx)
    out = []
    for b in bones:
        out.append({
            "i": b["i"],
            "n": b["n"],
            "en": b["en"],
            "p": b["p"],
            "pos": [round(v, 4) for v in b["pos"]],
            "rot": bool(b["flags"] & FLAG_BIT["rot"]),
            "mov": bool(b["flags"] & FLAG_BIT["mov"]),
            "ik": bool(b["flags"] & FLAG_BIT["ik"]),
            "ctl": bool(b["flags"] & FLAG_BIT["ctl"]),
            "phys": bool(b["flags"] & FLAG_BIT["phys"]),
        })
    return out, f"{meta} [{layout} layout, {len(out)} bones]"


def extract_skeleton(path):
    try:
        import UnityPy
    except ImportError:
        raise ExtractError(
            "UnityPy is required for --skeleton; install with "
            "`uv pip install --python /tmp/opencode/upy/bin/python UnityPy`"
        )
    env = UnityPy.load(path)
    go_names = {}
    transforms = {}
    for obj in env.objects:
        if obj.type.name == "GameObject":
            go_names[obj.path_id] = obj.read().m_Name
        elif obj.type.name == "Transform":
            transforms[obj.path_id] = obj.read()
    if not transforms or len(transforms) != len(go_names):
        raise ExtractError(
            f"unexpected bundle contents: {len(go_names)} GameObject(s), {len(transforms)} Transform(s)"
        )
    out = {}
    for pid, tr in transforms.items():
        go_id = tr.m_GameObject.path_id
        if go_id not in go_names:
            raise ExtractError(f"transform {pid} references unknown GameObject {go_id}")
        father_id = tr.m_Father.path_id if tr.m_Father is not None else 0
        father_name = "(ROOT)"
        if father_id:
            if father_id not in transforms:
                raise ExtractError(f"transform {pid} references unknown parent {father_id}")
            father_go = transforms[father_id].m_GameObject.path_id
            if father_go not in go_names:
                raise ExtractError(f"parent transform {father_id} references unknown GameObject {father_go}")
            father_name = go_names[father_go]
        out[go_names[go_id]] = {
            "parent": father_name,
            "pos": [round(float(v), 5) for v in (
                tr.m_LocalPosition.x, tr.m_LocalPosition.y, tr.m_LocalPosition.z)],
            "scale": [round(float(v), 5) for v in (
                tr.m_LocalScale.x, tr.m_LocalScale.y, tr.m_LocalScale.z)],
        }
    return out, f"{path} [{len(out)} transforms]"


def deep_diff(expected, actual, path="$", limit=20):
    diffs = []
    if isinstance(expected, dict) and isinstance(actual, dict):
        for k in sorted(set(expected) - set(actual), key=repr):
            diffs.append(f"{path}.{k}: only in expected ({expected[k]!r})")
        for k in sorted(set(actual) - set(expected), key=repr):
            diffs.append(f"{path}.{k}: only in actual ({actual[k]!r})")
        for k in sorted(set(expected) & set(actual), key=repr):
            diffs.extend(deep_diff(expected[k], actual[k], f"{path}.{k}", limit - len(diffs)))
            if len(diffs) > limit:
                return diffs
    elif isinstance(expected, list) and isinstance(actual, list):
        if len(expected) != len(actual):
            diffs.append(f"{path}: length {len(expected)} != {len(actual)}")
        for i, (e, a) in enumerate(zip(expected, actual)):
            diffs.extend(deep_diff(e, a, f"{path}[{i}]", limit - len(diffs)))
            if len(diffs) > limit:
                return diffs
    elif expected != actual:
        diffs.append(f"{path}: {expected!r} != {actual!r}")
    return diffs


def main(argv=None):
    ap = argparse.ArgumentParser(
        description="Extract PMX bones (from .gfc/.pmx) or a Unity skeleton (.bundle) to JSON."
    )
    ap.add_argument("--pmx", help="path to a .gfc package or a raw .pmx file")
    ap.add_argument("--skeleton", help="path to a Unity skeleton .bundle")
    ap.add_argument("--out", required=True, help="output JSON path")
    ap.add_argument("--check", help="baseline JSON to diff against; non-zero exit on mismatch")
    args = ap.parse_args(argv)
    if bool(args.pmx) == bool(args.skeleton):
        ap.error("exactly one of --pmx / --skeleton is required")
    try:
        if args.pmx:
            payload, label = extract_pmx(args.pmx)
        else:
            payload, label = extract_skeleton(args.skeleton)
        if not payload:
            raise ExtractError("empty extraction result")
    except (ExtractError, OSError) as exc:
        print(f"error: {exc}", file=sys.stderr)
        return 1
    with open(args.out, "w", encoding="utf-8") as fh:
        json.dump(payload, fh, indent=1, ensure_ascii=False)
    print(f"extract -> {json.dumps(label, ensure_ascii=True)}", file=sys.stderr)
    if args.check:
        with open(args.check, "r", encoding="utf-8") as fh:
            baseline = json.load(fh)
        diffs = deep_diff(baseline, payload)
        if diffs:
            print(f"CHECK FAILED: {len(diffs)} differences vs {args.check}", file=sys.stderr)
            for d in diffs[:20]:
                print("  " + d, file=sys.stderr)
            if len(diffs) > 20:
                print(f"  ... and {len(diffs) - 20} more", file=sys.stderr)
            return 1
        print(f"check: 0 differences vs {args.check}", file=sys.stderr)
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
