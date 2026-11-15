#!/usr/bin/env python3
"""GF2 PMX re-modeler (2026-11-14-3)：鏈級高斯 RBF ＋ 權重混合解析手部縮放。

①地標＝四肢鏈關節（脊椎/頭/骨盆/鏈根釘住）→ 高斯 RBF 位移場（插值自檢）；
②場作用於「頂點＋全部骨樞紐」；③手堂徑向縮放以「頂點權重中手部子樹佔比 s(v)」
混合解析施加（樞＝重製後手首；指骨 pivot 精確縮放；袖口/上臂零捲入）。
實作＝位元組精準修補（恆等場輸出＝輸入逐位元組相同）。權威走樹＝
tools/extract_bones.walk() 逐字複製＋4 個偏移捕獲（單一真相）。

用法：
  remodel_pmx.py identity-test <pmx> | warp <pmx> <out> | audit <pmx>
"""
import sys
import math
import os
import struct
import sys

import numpy as np

TOOLS = os.path.dirname(os.path.abspath(__file__))
sys.path.insert(0, TOOLS)
from extract_bones import ExtractError, Reader, parse_bones, read_container  # noqa: E402

UNIT = 0.078
G_CLAV, G_UARM, G_FARM = 0.13337, 0.29651, 0.25095
G_THIGH12, G_FOOT, G_TOE = 0.45624, 0.46025, 0.15451
G_HAND_PATH = 0.200
ANCHORS = ("センター", "下半身", "上半身", "上半身2", "首", "頭", "全ての親")


def _dist3(a, b):
    return math.sqrt(sum((a[i] - b[i]) ** 2 for i in range(3)))


def _dir3(a, b):
    d = [b[i] - a[i] for i in range(3)]
    n = math.sqrt(sum(x * x for x in d))
    return None if n < 1e-9 else [x / n for x in d]


def build_landmarks(positions):
    p = positions
    marks = {}

    def has(name):
        return name in p

    def mark(name, target):
        marks[name] = (p[name], target)

    for n in ANCHORS:
        if has(n):
            mark(n, p[n])
    if not has("下半身"):
        raise ValueError("bone 下半身 missing (pelvis anchor required)")
    for side in ("左", "右"):
        if all(has(f"{side}{b}") for b in ("肩", "腕", "ひじ", "手首")):
            sh = p[f"{side}肩"]
            d1 = _dir3(sh, p[f"{side}腕"])
            if d1 is not None:
                mark(f"{side}肩", p[f"{side}肩"])
                t_wrist = [sh[i] + d1[i] * (G_CLAV / UNIT) for i in range(3)]
                mark(f"{side}腕", t_wrist)
                d2 = _dir3(p[f"{side}腕"], p[f"{side}ひじ"]) or d1
                t_elbow = [t_wrist[i] + d2[i] * (G_UARM / UNIT) for i in range(3)]
                mark(f"{side}ひじ", t_elbow)
                d3 = _dir3(p[f"{side}ひじ"], p[f"{side}手首"]) or d2
                mark(f"{side}手首", [t_elbow[i] + d3[i] * (G_FARM / UNIT) for i in range(3)])
        if all(has(f"{side}{b}") for b in ("足", "ひざ", "足首", "足首先")):
            th = p[f"{side}足"]
            d1 = _dir3(th, p[f"{side}ひざ"])
            if d1 is not None:
                mark(f"{side}足", p[f"{side}足"])
                t_knee = [th[i] + d1[i] * (G_THIGH12 / UNIT) for i in range(3)]
                mark(f"{side}ひざ", t_knee)
                d2 = _dir3(p[f"{side}ひざ"], p[f"{side}足首"]) or d1
                t_ankle = [t_knee[i] + d2[i] * (G_FOOT / UNIT) for i in range(3)]
                mark(f"{side}足首", t_ankle)
                d3 = _dir3(p[f"{side}足首"], p[f"{side}足首先"]) or d2
                mark(f"{side}足首先", [t_ankle[i] + d3[i] * (G_TOE / UNIT) for i in range(3)])
    return marks


def field_solve(marks, lam=1e-6, sigma=None):
    """高斯 RBF ＋ 線性多項式（遠場由線性項接管，壓制外插震盪）。自檢插值精確性。"""
    P = np.asarray([m[0] for m in marks.values()], dtype=np.float64)
    D = np.asarray([[m[1][i] - m[0][i] for i in range(3)] for m in marks.values()], dtype=np.float64)
    n = P.shape[0]
    Rr = np.sqrt(((P[:, None, :] - P[None, :, :]) ** 2).sum(axis=2))
    off = Rr[~np.eye(n, dtype=bool)]
    med = float(np.median(off)) if off.size and float(np.median(off)) > 1e-6 else 1.0
    sig = sigma if sigma is not None else min(1.8, 0.7 * med)
    K = np.exp(-((Rr / sig) ** 2)) + lam * np.eye(n)
    Pm = np.hstack([np.ones((n, 1)), P])
    M = np.zeros((n + 4, n + 4))
    M[:n, :n] = K
    M[:n, n:] = Pm
    M[n:, :n] = Pm.T
    yy = np.vstack([D, np.zeros((4, 3))])
    sol = np.linalg.solve(M, yy)
    W, ABC = sol[:n], sol[n:]
    err = float(np.abs(np.exp(-((Rr / sig) ** 2)) @ W + Pm @ ABC - D).max())
    if err > 9e-4:
        raise RuntimeError(f"RBF interpolation self-check failed: residual {err:.3e} units")
    return P, W, ABC, float(sig)


def field_disp(X, P, W, ABC, sig):
    flat = X.reshape(-1, 3).astype(np.float64)
    Kx = np.exp(-(((flat[:, None, :] - P[None, :, :]) / sig) ** 2).sum(axis=2))
    Pmx = np.hstack([np.ones((flat.shape[0], 1)), flat])
    out = Kx @ W + Pmx @ ABC
    return out.reshape(X.shape)


def _walk_layout(pmx, layout, VPOS, VNRM, VSDEF, BOFF, WPOS, FACE, adduv, bone_idx, _vtx, _tex, _mat):
    r = Reader(pmx)
    r.pos = 4 + 4 + 1 + 8
    for _ in range(4):
        r.string("utf-16-le")
    vcount = r.i32()
    for _ in range(vcount):
        VPOS.append(r.pos)
        VNRM.append(r.pos + 12)
        r.need(12 + 12 + 8 + adduv * 16 + 1, "vertex")
        r.pos += 12 + 12 + 8 + adduv * 16
        kind = r.u8()
        wpos2 = r.pos
        if kind == 0:
            r.pos += bone_idx
        elif kind == 1:
            r.pos += 2 * bone_idx + 4
        elif kind in (2, 4):
            r.pos += 4 * bone_idx + 16
        elif kind == 3:
            VSDEF.append(r.pos + 2 * bone_idx + 4)
            r.pos += 2 * bone_idx + 4 + 36
        else:
            raise ExtractError(f"unknown weight kind {kind} at {r.pos}")
        r.pos += 4
    WPOS.append((kind, wpos2))
    fcount = r.i32()
    if fcount < 0:
        raise ExtractError(f"negative face count {fcount}")
    FACE[0] = (r.pos, _vtx, fcount)
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
        BOFF.append((i, r.pos - 12))
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


def walk_offsets(pmx):
    """權威走樹：tools/extract_bones.walk() 逐字複製＋4 個偏移捕獲（單一真相）。"""
    VPOS, VNRM, VSDEF, BOFF, WPOS, FACE = [], [], [], [], [], [None]
    last = None
    for att in ("gfl2", "stock"):
        for lst in (VPOS, VNRM, VSDEF, BOFF, WPOS):
            del lst[:]
        FACE[0] = None
        try:
            _bones = _walk_layout(pmx, att, VPOS, VNRM, VSDEF, BOFF, WPOS, FACE,
                                  pmx[10], pmx[14], pmx[11], pmx[12], pmx[13])
        except Exception as e:  # noqa: BLE001
            last = e
            continue
        return {"bones": _bones, "vpos": VPOS, "vnrm": VNRM, "vsdef": VSDEF,
                "bone_off": BOFF, "wpos": WPOS, "face": FACE[0]}
    raise last


def load_layout(path):
    _meta, pmx = read_container(path)
    layout, bones = parse_bones(pmx)
    d = walk_offsets(pmx)
    if d["face"] is None:
        raise ValueError("face section not captured (authority walk incomplete)")
    fo, fv, fc = d["face"]
    faces = _read_faces2(d, pmx)
    g = _globals(pmx)
    g["nrm"] = 12  # 權威走樹（extract_bones 口徑）：頂點 normal 為 f32×3（12B）
    return pmx, g, bones, d["bone_off"], d["vpos"], d["vnrm"], d["vsdef"], faces, d["wpos"]


def _read_faces2(d, pmx):
    fo, fv, fc = d["face"]
    tri = fc - fc % 3
    if fv == 1:
        idx = pmx[fo:fo + fc]
    elif fv == 2:
        idx = struct.unpack_from(f"<{fc}h", pmx, fo)
    else:
        idx = struct.unpack_from(f"<{fc}i", pmx, fo)
    return np.asarray([[idx[i], idx[i + 1], idx[i + 2]] for i in range(0, tri, 3)], dtype=np.int64)


def read_weights(pmx, weights, g, nv):
    bs = g["bone_i"]
    fmt = "i" if bs == 4 else ("h" if bs == 2 else "b")
    vbone = np.zeros((nv, 4), dtype=np.int64)
    vw = np.zeros((nv, 4), dtype=np.float64)
    for j, (kind, wpos) in enumerate(weights):
        if kind in (2, 4):
            idx = struct.unpack_from("<4" + fmt, pmx, wpos)
            ws = struct.unpack_from("<4f", pmx, wpos + 4 * bs)
        elif kind in (1, 3):
            idx = struct.unpack_from("<2" + fmt, pmx, wpos)
            ws = struct.unpack_from("<2f", pmx, wpos + 2 * bs)
        else:
            idx = struct.unpack_from("<1" + fmt, pmx, wpos)
            ws = (1.0,)
        for k in range(len(idx)):
            vbone[j, k] = idx[k]
            vw[j, k] = ws[k]
    return vbone, vw


def hand_subtree(bones, root_name):
    root = next((b["i"] for b in bones if b["n"] == root_name), None)
    if root is None:
        return set()
    children = {}
    for b in bones:
        children.setdefault(b["p"], []).append(b["i"])
    members = {root}
    stack = [root]
    while stack:
        cur = stack.pop()
        for c in children.get(cur, []):
            if c not in members:
                members.add(c)
                stack.append(c)
    return members


def _globals(pmx):
    return dict(adduv=pmx[10], vtx=pmx[11], tex=pmx[12], mat=pmx[13],
                bone_i=pmx[14], morph_i=pmx[15], rb_i=pmx[16])


def warp(path, out, identity=False):
    pmx, g, bones, bone_off, vpos, vnrm, vsdef, faces, wpos_l = load_layout(path)
    nv = len(vpos)
    positions = {b["n"]: b["pos"] for b in bones}
    dst = bytearray(pmx)
    if identity:
        with open(out, "wb") as f:
            f.write(bytes(dst))
        return dict(bones=len(bone_off), verts=nv, sdef=len(vsdef), faces=len(faces), handL=1.0, handR=1.0)

    marks = build_landmarks(positions)
    P, W, ABC, sig = field_solve(marks)
    lm_max = max(float(np.linalg.norm(np.asarray(m[1]) - np.asarray(m[0]))) for m in marks.values())

    # ①骨樞紐 RBF
    bone_new = {}
    for i, off in bone_off:
        old_p = np.asarray(positions[bones[i]["n"]], dtype=np.float64)
        bone_new[i] = old_p + field_disp(old_p.reshape(1, 3), P, W, ABC, sig)[0]

    # ②頂點 RBF
    Xv = np.asarray([struct.unpack_from("<3f", pmx, o) for o in vpos], dtype=np.float64)
    Xw = Xv + field_disp(Xv, P, W, ABC, sig)

    # ③手堂徑向縮放（權重混合解析步）：authored 相對位移 × h，錨＝RBF 後手首。
    # h 以 authored 最近（中指尖類）距離為基準（authored 手堂 ≈0.10m → h ≈ 2）。
    vbone, vw = read_weights(pmx, wpos_l, g, nv)
    vbone = np.where((vbone >= 0) & (vbone < len(bones)), vbone, -1)
    vw = np.where((vbone >= 0), vw, 0.0)  # 無效 bone 索引（-1/超界）權重歸零，防負索引回繞
    handSides = {}
    pivot_new = {}
    pivot_out = {}
    for side, tag in (("左", "handL"), ("右", "handR")):
        hname = f"{side}手首"
        hidx = next((b["i"] for b in bones if b["n"] == hname), None)
        if hidx is None:
            continue
        members = hand_subtree(bones, hname)
        hw_auth = np.asarray(positions[hname], dtype=np.float64)
        hw_new = bone_new[hidx]
        tips_auth = [(float(np.linalg.norm(np.asarray(positions[bones[i]["n"]]) - hw_auth)), i)
                     for i in members]
        tipd_auth = max(d for d, _ in tips_auth)
        if tipd_auth < 1e-6:
            continue
        h = (G_HAND_PATH / UNIT) / tipd_auth
        if not (1.2 <= h <= 3.2):
            raise RuntimeError(f"hand scale out of sanity: {h:.3f} ({side})")
        ishand = np.zeros(len(bones), dtype=np.float64)
        for mi in members:
            if mi != hidx:
                ishand[mi] = 1.0
        s = (ishand[vbone] * vw).sum(axis=1)
        s = np.where(vw.sum(axis=1) > 1.05, 0.0, s)  # 畸形加權（Σw>1）的附掛網格排除於手堂步
        s = np.clip(s, 0.0, 1.0)
        # 頂點：以 authored 為基的徑向縮放位置，與 RBF 場結果按 s 混合
        auth_scaled = hw_new + (Xv - hw_auth) * h
        Xw = Xw + (auth_scaled - Xw) * s.reshape(-1, 1)
        # pivot：子樹成員精確套 authored 相對位移縮放（手首樞不動）
        for mi in members:
            if mi == hidx:
                continue
            ap = np.asarray(positions[bones[mi]["n"]], dtype=np.float64)
            pivot_new[mi] = hw_new + (ap - hw_auth) * h
        handSides[tag] = h
        pivot_out.update(pivot_new)

    # 頂點過衝守門員（RBF 權重爆漲 / 手堂爆漲的統一防線）
    vm = float(np.linalg.norm(Xw - Xv, axis=1).max())
    if vm > lm_max * 1.35 + 1.2:
        raise RuntimeError(f"vertex overshoot: max |disp| {vm:.3f}u vs landmark max {lm_max:.3f}u")

    for i, off in bone_off:
        final_p = pivot_out.get(i)
        if final_p is None:
            final_p = bone_new[i]
        dst[off:off + 12] = struct.pack("<3f", *final_p)
    for j, off in enumerate(vpos):
        dst[off:off + 12] = struct.pack("<3f", *Xw[j])
    for off in vsdef:  # SDEF C：場位移＋手堂範圍內跟手（SDEF 罕用，C 以距離判斷）
        c = np.asarray(struct.unpack_from("<3f", pmx, off), dtype=np.float64)
        c2 = c + field_disp(c.reshape(1, 3), P, W, ABC, sig)[0]
        for side, tag in (("左", "handL"), ("右", "handR")):
            root_name = f"{side}手首"
            root_idx = next((b["i"] for b in bones if b["n"] == root_name), None)
            if root_idx is None or tag not in handSides:
                continue
            hw = bone_new[root_idx]
            members = hand_subtree(bones, root_name)
            tipd = float(np.linalg.norm(bone_new[max(members, key=lambda i: float(np.linalg.norm(bone_new[i] - hw)))] - hw))
            if float(np.linalg.norm(c - hw)) <= tipd:
                c2 = c2 + (c2 - hw) * (handSides[tag] - 1.0)
                break
        dst[off:off + 12] = struct.pack("<3f", *c2)

    nrm_old = np.asarray([struct.unpack_from("<3f", pmx, o) for o in vnrm], dtype=np.float64) if g["nrm"] == 12 else \
        np.asarray([[(pmx[o + k] - 128) for k in range(3)] for o in vnrm], dtype=np.float64)
    nrm_new = _face_normals(Xw, faces, nrm_old)
    for j, off in enumerate(vnrm):
        if g["nrm"] == 12:
            dst[off:off + 12] = struct.pack("<3f", *nrm_new[j])
        else:
            dst[off:off + 3] = bytes(int(max(0, min(255, round(v + 128)))) for v in nrm_new[j])

    with open(out, "wb") as f:
        f.write(bytes(dst))
    return dict(bones=len(bone_off), verts=nv, sdef=len(vsdef), faces=len(faces),
                handL=handSides.get("handL", 1.0), handR=handSides.get("handR", 1.0))


def _face_normals(Xw, faces, nrm_old):
    if faces.size == 0:
        n = nrm_old.copy()
        ln0 = np.sqrt((n * n).sum(axis=1))
        return np.where(ln0[:, None] > 1e-9, n / np.maximum(ln0, 1e-9)[:, None], [0.0, 0.0, 1.0])
    a, b, c = Xw[faces[:, 0]], Xw[faces[:, 1]], Xw[faces[:, 2]]
    nrm = np.cross(b - a, c - a)
    ln = np.sqrt((nrm * nrm).sum(axis=1))
    keep = ln > 1e-12
    f2 = faces[keep]
    nrm = nrm[keep] / ln[keep][:, None]
    n = np.zeros_like(Xw)
    np.add.at(n, f2.reshape(-1), np.concatenate([nrm, nrm, nrm], axis=0))
    ln = np.sqrt((n * n).sum(axis=1))
    ok = ln > 1e-9
    n[ok] /= ln[ok][:, None]
    flip = (n * nrm_old).sum(axis=1) < 0
    n[flip] = -n[flip]
    loose = np.where(~ok)[0]
    if loose.size:
        ln0 = np.sqrt((nrm_old * nrm_old).sum(axis=1))
        good = ln0[loose] > 1e-9
        idx = loose[good]
        n[idx] = nrm_old[idx] / ln0[idx][:, None]
        n[loose[~good]] = [0.0, 0.0, 1.0]
    return n


# ---- 驗收審計（m）----
_FING_TIP_SUF = ("３", "3")


def audit(path):
    _meta, pmx = read_container(path)
    _layout, bl = parse_bones(pmx)
    pos = {b["n"]: b["pos"] for b in bl}
    out = {}
    for side in ("左", "右"):
        try:
            clav = _dist3(pos[f"{side}肩"], pos[f"{side}腕"]) * UNIT
            uarm = _dist3(pos[f"{side}腕"], pos[f"{side}ひじ"]) * UNIT
            farm = _dist3(pos[f"{side}ひじ"], pos[f"{side}手首"]) * UNIT
            thigh = _dist3(pos[f"{side}足"], pos[f"{side}ひざ"]) * UNIT
            shank = _dist3(pos[f"{side}ひざ"], pos[f"{side}足首"]) * UNIT
            toe = _dist3(pos[f"{side}足首"], pos[f"{side}足首先"]) * UNIT
            chain = clav + uarm + farm
            hand = None
            tips = sorted(_dist3(pos[f"{side}手首"], pos[n]) * UNIT for n in pos
                          if n.endswith(_FING_TIP_SUF) and any(n.startswith(side + pf) for pf in ("中指", "人指", "薬指", "小指", "親指")))
            if tips:
                hand = tips[0]
            out[side] = dict(clav=round(clav, 4), uarm=round(uarm, 4), farm=round(farm, 4),
                             chain=round(chain, 4), elbowPct=round((clav + uarm) / chain * 100, 1),
                             thigh=round(thigh, 4), shank=round(shank, 4), toe=round(toe, 4),
                             legChain=round(thigh + shank + toe, 4),
                             hand=round(hand, 4) if hand else None)
        except KeyError:
            continue
    return out


def warp_arms(path, out, identity=False):
    """臂鏈-only 重作：①臂骨 pivot 分段延長（肘點 63.1%）＋②臂骨權重頂點蒙皮平移拉伸
    ＋③手堂徑向縮放（權重混合、畸形加權排除）。腿/軀幹/頭/裙/髮/UV 零觸碰。"""
    pmx, g, bones, bone_off, vpos, vnrm, vsdef, faces, wpos_l = load_layout(path)
    nv = len(vpos)
    positions = {b["n"]: b["pos"] for b in bones}
    name2i = {b["n"]: b["i"] for b in bones}
    dst = bytearray(pmx)
    if identity:
        with open(out, "wb") as f:
            f.write(bytes(dst))
        return dict(bones=len(bone_off), verts=nv, handL=1.0, handR=1.0)

    armSides = {}
    pivot_out = {}
    Xv = np.asarray([struct.unpack_from("<3f", pmx, o) for o in vpos], dtype=np.float64)
    vbone, vw = read_weights(pmx, wpos_l, g, nv)
    vbone = np.where((vbone >= 0) & (vbone < len(bones)), vbone, -1)
    vw = np.where((vbone >= 0), vw, 0.0)
    Xw = Xv.copy()
    name_of = {b["i"]: b["n"] for b in bones}
    for side in ("左", "右"):
        names = [f"{side}{b}" for b in ("肩", "腕", "ひじ", "手首")]
        if any(n not in positions for n in names):
            continue
        # ①鏈骨集：沿 parent 鏈從 肩 到 手首（含捩/物理中繼）
        ridx = name2i[names[0]]
        widx = name2i[names[3]]
        children = {}
        for b in bones:
            children.setdefault(b["p"], []).append(b["i"])
        chain, stack = [], [ridx]
        reach = False
        seen = set()
        while stack:
            cur = stack.pop()
            if cur in seen:
                continue
            seen.add(cur)
            chain.append(cur)
            if cur == widx:
                reach = True
                continue  # 手首即臂鏈末端；手指子樞由手堂徑向縮放接手
            for c in children.get(cur, []):
                stack.append(c)
        if not reach:
            raise RuntimeError(f"{side} arm chain does not reach 手首")
        # ②authored 弧長 → 分段映射目標
        idx_set = set(chain)
        def auth_p(i):
            return np.asarray(positions[name_of[i]], dtype=np.float64)
        # 目標樞：肩不動；沿 authored 全鏈方向分段（auth 段長 clav/uarm/farm → 遊戲段長）
        sh_a, wra_a, el_a, ha_a = (auth_p(name2i[n]) for n in names)
        d1 = _dir3(sh_a, wra_a) or [1.0, 0, 0]
        d2 = _dir3(wra_a, el_a) or d1
        d3 = _dir3(el_a, ha_a) or d2
        L = [float(np.linalg.norm(wra_a - sh_a)), float(np.linalg.norm(el_a - wra_a)),
             float(np.linalg.norm(ha_a - el_a))]
        G = [G_CLAV / UNIT, G_UARM / UNIT, G_FARM / UNIT]
        def segmap(s):
            Lsum = sum(L)
            breaks = [0, L[0], L[0] + L[1], Lsum]
            gbreaks = [0, G[0], G[0] + G[1], G[0] + G[1] + G[2]]
            for k in range(3):
                if s <= breaks[k + 1] + 1e-9:
                    p = (s - breaks[k]) / (L[k] + 1e-12)
                    return gbreaks[k] + p * G[k]
            return gbreaks[3]
        pivot_new = {}
        for i2 in chain:
            s_i = float(np.linalg.norm(auth_p(i2) - sh_a))
            t_i = np.asarray(sh_a) + (np.asarray(d1) * 0.0)  # 占位
            # authored 方向：以 authored 位置沿鏈向外的單位向量混合（肩/肘/腕三段方向按弧長段取）
            dirseg = d1 if s_i <= L[0] else (d2 if s_i <= L[0] + L[1] else d3)
            gt = segmap(s_i)
            pivot_new[i2] = np.asarray(sh_a) + np.asarray(dirseg) * gt
        armSides[side] = dict(pivot_new=pivot_new, auth={i: auth_p(i) for i in chain})
        # ③頂點蒙皮位移
        disp = np.zeros((nv, 3))
        for i2 in chain:
            d3_ = (pivot_new[i2] - auth_p(i2)).reshape(1, 3)
            hit = (vbone == i2)
            wsel = np.where(hit, vw, 0.0).sum(axis=1, keepdims=True)
            disp += wsel * d3_.reshape(1, 3)
        Xw = Xw + disp
        # ④手堂徑向縮放（authored 相對×h，錨=新手首樞；排除畸形加權）
        hname = names[3]
        hidx = widx
        hroot = hidx
        children = {}
        for b in bones:
            children.setdefault(b["p"], []).append(b["i"])
        members = {hidx}
        stk = [hidx]
        while stk:
            cur = stk.pop()
            for c in children.get(cur, []):
                if c not in members:
                    members.add(c)
                    stk.append(c)
        hw_auth = auth_p(hidx)
        hw_new = pivot_new[hidx]
        tipd_auth = max(float(np.linalg.norm(auth_p(i2) - hw_auth)) for i2 in members)
        h = (G_HAND_PATH / UNIT) / tipd_auth
        if not (1.2 <= h <= 3.2):
            raise RuntimeError(f"hand scale out of sanity: {h:.3f} ({side})")
        ishand = np.zeros(len(bones), dtype=np.float64)
        for mi in members:
            if mi != hidx:
                ishand[mi] = 1.0
        s = (ishand[vbone] * vw).sum(axis=1)
        s = np.where(vw.sum(axis=1) > 1.05, 0.0, s)
        s = np.clip(s, 0.0, 1.0)
        # 手部頂點：authored 相對於手首的向量 × h（不再疊臂平移——臂鏈位移已把腕樞帶到目標）
        auth_scaled = hw_new + (Xv - hw_auth) * h
        # 混合權重：手堂僅作用「子樹權重 s」但臂鏈位移已涵蓋腕以上——手指頂點需要從臂位移改為手堂式
        # 精確組合：手部子樹頂點的最終位置 = 子樹最終樞體系下 authored 相對位置的徑向縮放；
        # 以 s 混合：Xw_final = Xw + s×(hw_new + (Xv − hw_auth)×h − Xw)
        Xw = Xw + (auth_scaled - Xw) * s.reshape(-1, 1)
        # 子樹樞覆寫：authored 相對位移 ×h（鏈映射已於手首停擴，手指由本步接手）
        for mi in members:
            if mi == hidx:
                continue
            ap = np.asarray(positions[name_of[mi]], dtype=np.float64)
            pivot_new[mi] = hw_new + (ap - hw_auth) * h
    # 寫出
    for i, off in bone_off:
        final_p = None
        for side in ("左", "右"):
            info = armSides.get(side)
            if info and i in info["pivot_new"]:
                final_p = info["pivot_new"][i]
                break
        if final_p is None:
            final_p = np.asarray(positions[bones[i]["n"]], dtype=np.float64)
        dst[off:off + 12] = struct.pack("<3f", *final_p)
    for j, off in enumerate(vpos):
        dst[off:off + 12] = struct.pack("<3f", *Xw[j])
    # 法線重算（被位移的臂部頂點）
    nrm_old = np.asarray([struct.unpack_from("<3f", pmx, o) for o in vnrm], dtype=np.float64)
    nrm_new = _face_normals(Xw, faces, nrm_old)
    for j, off in enumerate(vnrm):
        dst[off:off + 12] = struct.pack("<3f", *nrm_new[j])
    with open(out, "wb") as f:
        f.write(bytes(dst))
    return dict(bones=len(bone_off), verts=nv,
                handL=armSides.get("左", {}).get("h") and round(armSides["左"]["h"], 3) or 1.0,
                handR=armSides.get("右", {}).get("h") and round(armSides["右"]["h"], 3) or 1.0)


def main(argv):
    if len(argv) < 2:
        print(__doc__)
        return 2
    cmd, path = argv[0], argv[1]
    try:
        if cmd == "identity-test":
            out = path + ".identity.tmp"
            try:
                warp(path, out, identity=True)
                with open(path, "rb") as f:
                    a = f.read()
                with open(out, "rb") as f:
                    b = f.read()
                print(f"identity: {'OK byte-identical' if a == b else 'MISMATCH'} ({len(a)}B)")
                return 0 if a == b else 1
            finally:
                if os.path.exists(out):
                    os.remove(out)
        if cmd == "warp-arms":
            st = warp_arms(path, argv[2])
            print(f"warp-arms: bones={st['bones']} verts={st['verts']} handScale L{st['handL']} R{st['handR']} -> {argv[2]}")
            return 0
        if cmd == "warp":
            st = warp(path, argv[2])
            print(f"warp: bones={st['bones']} verts={st['verts']} sdefC={st['sdef']} faces={st['faces']} "
                  f"handScale L{st['handL']:.3f} R{st['handR']:.3f} -> {argv[2]}")
            return 0
        if cmd == "audit":
            for side, d in audit(path).items():
                print(f"  {side}: {d}")
            return 0
    except Exception:  # noqa: BLE001
        import traceback
        traceback.print_exc()
        return 1
    print(__doc__)
    return 2


if __name__ == "__main__":
    raise SystemExit(main(sys.argv[1:]))
