#!/usr/bin/env python3
"""GF2 SDF 臉部陰影烘焙（角度域）——對角色 PMX 的 Face 材質區域，逐紋素計算
「光繞臉基座方位角掃掠時的明暗終止線方位角 θ*」場，輸出 sdf_face.png（R=θ* 8-bit、
A=coverage），執行期以 mask = saturate((outer − |wrap(ψ−θ*)|)/(2w)) 合成進臉部漫貼圖副本。
座標契約：authored PMX UV 空間（v=0 對應貼圖頂行）；θ*/ψ 兩側共用同一臉基座（Face材質
頂點平均法線正規化＝F，R=cross(+Y,F) 正規化），mask 邊界帶寬 w 常數見 ModuleRuntime。

用法：tools/bake_sdf_face.py <角色資料夾或 pmx> [-o sdf_face.png] [--res 1024] [--qa qa.png]
"""
import argparse
import math
import os
import struct
import sys

import numpy as np
from PIL import Image

TOOLS = os.path.dirname(os.path.abspath(__file__))
RES_DEFAULT = 1024
CONF_MIN = 0.15


def read_png_size(path):
    with open(path, "rb") as f:
        head = f.read(26)
    if head[:8] != b"\x89PNG\r\n\x1a\n" or head[12:16] != b"IHDR":
        raise ValueError(f"not a PNG: {path}")
    w, h = struct.unpack_from(">II", head, 16)
    return w, h


def parse_pmx_geometry(path):
    d = open(path, "rb").read()
    ng = d[8]
    g = [d[9 + i] for i in range(ng)]
    enc, adduv, vtx = g[0], g[1], g[2]
    pos = 17

    def rs(p):
        (l,) = struct.unpack_from("<i", d, p)
        return d[p + 4:p + 4 + l].decode("utf-16-le" if enc == 0 else "utf-8"), p + 4 + l

    for _ in range(4):
        _, pos = rs(pos)
    (vc,) = struct.unpack_from("<i", d, pos)
    pos += 4
    P = np.zeros((vc, 3), np.float64)
    N = np.zeros((vc, 3), np.float64)
    U = np.zeros((vc, 2), np.float64)
    for i in range(vc):
        P[i] = struct.unpack_from("<3f", d, pos); pos += 12
        N[i] = struct.unpack_from("<3f", d, pos); pos += 12
        U[i] = struct.unpack_from("<2f", d, pos); pos += 8
        pos += adduv * 16
        kind = d[pos]; pos += 1
        pos += {0: 2, 1: 8, 2: 24, 3: 48, 4: 24}[kind] + 4
    (fc,) = struct.unpack_from("<i", d, pos); pos += 4
    idx = np.frombuffer(d[pos:pos + fc * vtx], dtype=np.uint16 if vtx == 2 else np.uint32).astype(np.int64)
    pos += fc * vtx
    (tc,) = struct.unpack_from("<i", d, pos); pos += 4
    texs = []
    for _ in range(tc):
        t, pos = rs(pos)
        texs.append(t.replace("\\", "/"))
    (mc,) = struct.unpack_from("<i", d, pos); pos += 4
    mats = []
    spans = []
    cursor = 0
    for i in range(mc):
        n, pos = rs(pos); _, pos = rs(pos)
        pos += 16 + 12 + 4 + 12 + 1 + 16 + 4
        t1 = d[pos]; pos += 1
        pos += 1 + 1
        shared = d[pos]; pos += 1
        pos += 1 if shared == 1 else 1
        _, pos = rs(pos)
        (fv,) = struct.unpack_from("<i", d, pos); pos += 4
        mats.append((n, t1))
        spans.append((cursor, fv))
        cursor += fv
    tris = idx.reshape(-1, 3) if fc >= 3 else np.zeros((0, 3), np.int64)
    return dict(P=P, N=N, U=U, texs=texs, mats=mats, spans=spans, tris=tris)


def pick_face_material(mats):
    exact = [i for i, (n, _) in enumerate(mats) if n == "Face"]
    if exact:
        return exact[0], "exact"
    loose = [i for i, (n, _) in enumerate(mats) if "face" in n.lower() or "臉" in n]
    return (loose[0], "loose") if loose else (None, "none")


def face_basis(N, verts):
    m = N[verts].mean(axis=0)
    ln = np.linalg.norm(m)
    if ln < 1e-9:
        return None
    F = m / ln
    Y = np.array([0.0, 1.0, 0.0])
    R = np.cross(Y, F)
    ln = np.linalg.norm(R)
    return None if ln < 1e-9 else (F, R / ln)


def bake(pmx_path, res=None):
    g = parse_pmx_geometry(pmx_path)
    fi, how = pick_face_material(g["mats"])
    if fi is None:
        raise ValueError("no Face material")
    start, fv = g["spans"][fi]
    ft = g["tris"][start // 3:(start + fv) // 3]
    verts = np.unique(ft.reshape(-1))
    basis = face_basis(g["N"], verts)
    if basis is None:
        raise ValueError("degenerate face basis")
    F, R = basis
    t1 = g["mats"][fi][1]
    texname = g["texs"][t1] if 0 <= t1 < len(g["texs"]) else ""
    tex_path = None
    if texname:
        tex_path = find_texture(os.path.dirname(pmx_path), texname)
    if res is None:
        res = 1024
        if tex_path:
            try:
                w, h = read_png_size(tex_path)
                res = max(1, min(1024, w, h))
            except (OSError, ValueError):
                pass
    U = g["U"]
    uvA, uvB, uvC = U[ft[:, 0]], U[ft[:, 1]], U[ft[:, 2]]
    nA, nB, nC = g["N"][ft[:, 0]], g["N"][ft[:, 1]], g["N"][ft[:, 2]]
    ax, ay = uvA[:, 0] * res, uvA[:, 1] * res
    bx, by = uvB[:, 0] * res, uvB[:, 1] * res
    cx, cy = uvC[:, 0] * res, uvC[:, 1] * res
    grid = np.zeros((res, res, 3), np.float64)
    for t in range(len(ft)):
        x0 = int(max(0.0, min(ax[t], bx[t], cx[t])))
        x1 = int(min(res - 1.0, max(ax[t], bx[t], cx[t])))
        y0 = int(max(0.0, min(ay[t], by[t], cy[t])))
        y1 = int(min(res - 1.0, max(ay[t], by[t], cy[t])))
        if x1 < x0 or y1 < y0:
            continue
        den = (by[t] - cy[t]) * (ax[t] - cx[t]) + (cx[t] - bx[t]) * (ay[t] - cy[t])
        if abs(den) < 1e-12:
            continue
        xs, ys = np.meshgrid(np.arange(x0, x1 + 1) + 0.5, np.arange(y0, y1 + 1) + 0.5)
        l1 = ((by[t] - cy[t]) * (xs - cx[t]) + (cx[t] - bx[t]) * (ys - cy[t])) / den
        l2 = ((cy[t] - ay[t]) * (xs - cx[t]) + (ax[t] - cx[t]) * (ys - cy[t])) / den
        inside = (l1 >= 0) & (l2 >= 0) & (l1 + l2 <= 1)
        if not inside.any():
            continue
        l3 = 1.0 - l1 - l2
        nf = nA[t] * l1[..., None] + nB[t] * l2[..., None] + nC[t] * l3[..., None]
        slc = (slice(y0, y1 + 1), slice(x0, x1 + 1))
        wgt = inside.astype(np.float64)
        m0 = grid[slc + (2,)]
        take = wgt > m0
        grid[slc + (0,)] = np.where(take, nf @ F, grid[slc + (0,)])
        grid[slc + (1,)] = np.where(take, nf @ R, grid[slc + (1,)])
        grid[slc + (2,)] = np.maximum(m0, wgt)
    cover = grid[:, :, 2] > 0
    mag = np.hypot(grid[:, :, 0], grid[:, :, 1])
    cover &= mag > CONF_MIN
    th = np.arctan2(grid[:, :, 1], grid[:, :, 0])
    th = smooth_covered(th, cover, res)
    th, cover = dilate(th, cover, rounds=2)
    out = np.zeros((res, res, 4), np.uint8)
    out[..., 0] = np.clip(np.round((th / (2 * np.pi) + 0.5) * 255), 0, 255).astype(np.uint8)
    out[..., 1] = 255
    out[..., 2] = 255
    out[..., 3] = (cover * 255).astype(np.uint8)
    stats = dict(F=F.tolist(), R=R.tolist(), material_how=how, tex=texname,
                 tex_path=tex_path, tris=len(ft), res=res, covered=int(cover.sum()))
    return out, stats


def find_texture(base, texname):
    cand = os.path.join(base, texname)
    if os.path.isfile(cand):
        return cand
    bn = os.path.basename(texname)
    for root, _, files in os.walk(base):
        if bn in files:
            return os.path.join(root, bn)
    return None


def smooth_covered(th, cover, res, sigma=1.0):
    c = cover.astype(np.float64)
    base_num = th * c
    kern = np.exp(-0.5 * (np.arange(-2, 3) / sigma) ** 2)
    kern /= kern.sum()
    num = np.zeros_like(th)
    den = np.zeros_like(th)
    for oy in range(-2, 3):
        for ox in range(-2, 3):
            k = kern[oy + 2] * kern[ox + 2]
            num += shift2(base_num, oy, ox) * k
            den += shift2(c, oy, ox) * k
    fill = den > 1.0
    return np.where(fill, num / np.maximum(den, 1e-12), th)


def shift2(a, oy, ox):
    out = np.zeros_like(a)
    h, w = a.shape
    ys0, ys1 = max(0, oy), min(h, h + oy)
    xs0, xs1 = max(0, ox), min(w, w + ox)
    if ys1 <= ys0 or xs1 <= xs0:
        return out
    out[ys0:ys1, xs0:xs1] = a[ys0 - oy:ys1 - oy, xs0 - ox:xs1 - ox]
    return out


def dilate(th, cover, rounds=2):
    out = th.copy()
    cov = cover.copy()
    for _ in range(rounds):
        c = cov.astype(np.float64)
        base_num = th * c
        base_den = c.copy()
        num = th * cov
        den = c.copy()
        for oy in (-1, 0, 1):
            for ox in (-1, 0, 1):
                if oy == 0 and ox == 0:
                    continue
                num += shift2(base_num, oy, ox)
                den += shift2(base_den, oy, ox)
        grow = (den > 0.5) & (~cov)
        out = np.where(grow, num / np.maximum(den, 1e-12), out)
        cov |= grow
    return out, cov


def qa_sheet(pmx_path, sdf_img, tex_path, out_png, w=0.14):
    th = (sdf_img[..., 0].astype(np.float64) / 255.0 * 2 * np.pi - np.pi)
    cov = sdf_img[..., 3] > 0
    face = Image.open(tex_path).convert("RGB").resize((sdf_img.shape[1], sdf_img.shape[0]), Image.BILINEAR)
    base = np.asarray(face).astype(np.float64)
    yaws = [i * 45 - 135 for i in range(8)]
    tiles = []
    for deg in yaws:
        psi = math.radians(deg)
        dist = np.abs(((psi - th + np.pi) % (2 * np.pi)) - np.pi)
        mask = np.clip(1.0 - (dist - (np.pi / 2 - w)) / (2 * w), 0.0, 1.0)
        mask = np.where(cov, mask, 1.0)
        comp = np.clip(base * (0.32 + 0.68 * mask[..., None]), 0, 255).astype(np.uint8)
        tiles.append(comp)
    H, W = sdf_img.shape[:2]
    sheet = Image.new("RGB", (W * 4, H * 2))
    for i, tile in enumerate(tiles):
        sheet.paste(Image.fromarray(tile), ((i % 4) * W, (i // 4) * H))
    sheet.save(out_png)


def main(argv):
    ap = argparse.ArgumentParser()
    ap.add_argument("target", help="角色資料夾或 PMX 檔")
    ap.add_argument("-o", "--out", default="sdf_face.png")
    ap.add_argument("--res", type=int, default=None)
    ap.add_argument("--qa", default=None)
    args = ap.parse_args(argv)

    t = args.target
    pmx = t if t.endswith(".pmx") else None
    if pmx is None:
        cands = []
        for f in os.listdir(t):
            if f.endswith(".pmx"):
                cands.append(os.path.join(t, f))
        cands.sort(key=lambda p: os.path.getsize(p), reverse=True)
        for c in cands:
            try:
                g = parse_pmx_geometry(c)
            except (struct.error, ValueError, KeyError):
                continue
            if pick_face_material(g["mats"])[0] is not None:
                pmx = c
                break
        if pmx is None:
            print(f"no Face-material PMX in {t}")
            return 1
    img, stats = bake(pmx, res=args.res)
    out = args.out if os.path.isabs(args.out) else os.path.join(os.path.dirname(pmx), args.out)
    Image.fromarray(img, "RGBA").save(out)
    print(f"sdf: {out} res={stats['res']} tris={stats['tris']} covered={stats['covered']} "
          f"tex={os.path.basename(stats['tex_path'] or '?')} material={stats['material_how']}")
    if args.qa:
        if not stats["tex_path"]:
            print("qa skipped: face texture not found")
            return 0
        qa_sheet(pmx, img, stats["tex_path"], args.qa if os.path.isabs(args.qa) else os.path.join(os.path.dirname(pmx), args.qa))
        print(f"qa: {args.qa if os.path.isabs(args.qa) else os.path.join(os.path.dirname(pmx), args.qa)}")
    return 0


if __name__ == "__main__":
    raise SystemExit(main(sys.argv[1:]))
