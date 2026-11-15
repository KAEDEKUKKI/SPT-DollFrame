// Character/SdfFaceShading.cs: SDF 臉部陰影執行段——包級 SdfFaceAsset（解碼 sdf_face.png
// 的 θ* 場＋coverage＋臉漫貼快照）與 per-rig SdfFaceDriver（臉材質實例＋貼圖副本，
// 依頭骨基座×光向量得 ψ，對 coverage bbox 以 LUT 合成陰影到副本貼圖）。
// 契約：θ* 與 ψ 共用同一臉基座（Face 材質頂點平均法線正規化＝F，R=cross(+Y,F) 正規化），
// 與 tools/bake_sdf_face.py 的 encode/decode 完全對稱（R=(ψ*+π)/(2π)×255 → (k+0.5) 還原）；
// mask = clamp01((outer − |wrap(ψ−θ*)|)/(2w))，帶寬 w=0.14rad。貼圖契約：臉副本 ≤1024；
// 副本尺寸≠bake res 時以最近鄰映射（θ* 場為低頻，安全）。
using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;

namespace GF2
{
    /// <summary>包級 SDF 臉烘焙資料。</summary>
    internal sealed class SdfFaceAsset
    {
        internal const string ContainerKey = "sdf_face.png";
        internal const float BoundaryRad = 0.26f;

        internal bool HasData => Theta != null && Base != null;
        internal int SdfW, SdfH;
        internal byte[] Theta;
        internal bool[] Cover;
        internal int BoxX, BoxY, BoxW, BoxH;
        internal Color32[] Base;
        internal int CloneW = 1024, CloneH = 1024;
        internal Vector3 FaceF, FaceR;
    }

    /// <summary>
    /// per-rig SDF 臉驅動：臉材質實例與臉漫貼副本（≤1024、無 mip）走 per-renderer 材質
    /// 陣列覆寫——PmxAssets 共享材質由 LRU 銷毀，實例隨 rig 拆除（Terminate）銷毀。
    /// 每幀以頭骨基座量 ψ；角差 <2° 且 0.25s 內跳過，連續掃掠時最低 1/12s 更新。
    /// </summary>
    internal sealed class SdfFaceDriver
    {
        private readonly SdfFaceAsset _a;
        private readonly Material _mat;
        private readonly Texture2D _tex;
        private readonly Transform _headT;
        private readonly Vector3 _fallbackModelDir;
        private readonly byte[] _lut = new byte[256];
        private readonly Color32[] _rect;
        private readonly int _rx, _ry, _rw, _rh;
        private readonly int[] _sdfRowOf, _sdfColOf;
        private bool _primed;
        private float _lastPsi;
        private float _lastUpdate;

        private static readonly Vector3[] ProbeDirs =
        {
            Vector3.right, Vector3.left,
            Vector3.up, Vector3.down,
            Vector3.forward, Vector3.back
        };
        private readonly Color[] _probeCols = new Color[6];

        private SdfFaceDriver(SdfFaceAsset a, Material mat, Texture2D tex, Transform headT)
        {
            _a = a;
            _mat = mat;
            _tex = tex;
            _headT = headT;
            _fallbackModelDir = (a.FaceF + a.FaceR * 0.45f + Vector3.up * 0.55f).normalized;
            _rx = a.BoxX; _ry = a.BoxY; _rw = a.BoxW; _rh = a.BoxH;
            _rect = new Color32[a.BoxW * a.BoxH];
            _sdfRowOf = new int[a.BoxH];
            _sdfColOf = new int[a.BoxW];
            for (int y = 0; y < a.BoxH; y++)
                _sdfRowOf[y] = Mathf.Clamp((int)((a.BoxY + y + 0.5f) / a.CloneH * a.SdfH), 0, a.SdfH - 1);
            for (int x = 0; x < a.BoxW; x++)
                _sdfColOf[x] = Mathf.Clamp((int)((a.BoxX + x + 0.5f) / a.CloneW * a.SdfW), 0, a.SdfW - 1);
        }

        /// <summary>
        /// 建 per-rig 驅動：臉漫貼可讀副本（≤1024）、臉材質實例、per-renderer 材質陣列覆寫（body＋head）。
        /// </summary>
        internal static SdfFaceDriver Create(Transform headT, SdfFaceAsset a, Material faceMat,
            SkinnedMeshRenderer bodySmr, SkinnedMeshRenderer headSmr, int faceSlot)
        {
            if (a == null || !a.HasData || faceMat == null || faceSlot < 0) return null;
            var tex = BuildCloneTexture(a);
            if (tex == null) return null;
            var m = new Material(faceMat)
            {
                name = faceMat.name + "(SDF)",
                mainTexture = tex,
                hideFlags = HideFlags.DontUnloadUnusedAsset
            };
            ReplaceSlot(bodySmr, faceSlot, m);
            ReplaceSlot(headSmr, faceSlot, m);
            return new SdfFaceDriver(a, m, tex, headT);
        }

        private static void ReplaceSlot(SkinnedMeshRenderer smr, int slot, Material m)
        {
            if (smr == null) return;
            var arr = smr.sharedMaterials;
            if (arr == null || slot >= arr.Length) return;
            arr[slot] = m;
            smr.sharedMaterials = arr;
        }

        private static Texture2D BuildCloneTexture(SdfFaceAsset a)
        {
            var tex = new Texture2D(a.CloneW, a.CloneH, TextureFormat.RGBA32, false, false)
            {
                name = "GF2_sdf_face",
                wrapMode = TextureWrapMode.Clamp,
                hideFlags = HideFlags.DontUnloadUnusedAsset
            };
            tex.SetPixels32(a.Base);
            tex.Apply(false, false);
            return tex;
        }

        /// <summary>每幀驅動（rig.Drive 之後呼叫）；結合太陽與 LightProbe 主光方向驅動臉部 SDF。</summary>
        internal void Update()
        {
            if (_a == null || !_a.HasData || _tex == null || _headT == null) return;

            float now = Time.unscaledTime;
            if (_primed && now - _lastUpdate < 0.083f) return;

            Vector3 probeL = SampleProbeDominantDir(_headT.position);
            var sun = RenderSettings.sun;
            Vector3 L = sun != null && sun.intensity > 0.05f
                ? Vector3.Lerp(probeL, -sun.transform.forward, Mathf.Clamp01(sun.intensity)).normalized
                : probeL;

            var Fw = _headT.TransformDirection(_a.FaceF);
            var Rw = _headT.TransformDirection(_a.FaceR);
            float psi = Mathf.Atan2(Vector3.Dot(L, Rw), Vector3.Dot(L, Fw));

            if (_primed)
            {
                float d = Mathf.Abs(WrapPi(psi - _lastPsi));
                if (d < 0.035f)
                {
                    _lastUpdate = now;
                    return;
                }
            }
            _primed = true;
            _lastPsi = psi;
            _lastUpdate = now;
            Composite(psi);
        }

        private Vector3 SampleProbeDominantDir(Vector3 worldPos)
        {
            LightProbes.GetInterpolatedProbe(worldPos, null, out var sh);
            sh.Evaluate(ProbeDirs, _probeCols);
            Vector3 acc = Vector3.zero;
            for (int k = 0; k < 6; k++)
            {
                var c = _probeCols[k];
                float lum = c.r * 0.299f + c.g * 0.587f + c.b * 0.114f;
                acc += ProbeDirs[k] * Mathf.Max(0f, lum);
            }
            return acc.sqrMagnitude > 1e-4f
                ? acc.normalized
                : _headT.TransformDirection(_fallbackModelDir);
        }

        private void Composite(float psi)
        {
            float w = SdfFaceAsset.BoundaryRad;
            float outer = Mathf.PI / 2f + w;
            for (int k = 0; k < 256; k++)
            {
                float theta = (k + 0.5f) / 255f * (2f * Mathf.PI) - Mathf.PI;
                float dist = Mathf.Abs(WrapPi(psi - theta));
                float f = Mathf.Clamp01((outer - dist) / (2f * w));
                _lut[k] = (byte)Mathf.RoundToInt(f * 255f);
            }
            int rc = 0;
            int sdfW = _a.SdfW;
            for (int y = 0; y < _rh; y++)
            {
                int sRow = _sdfRowOf[y] * sdfW;
                int baseRow = (_ry + y) * _a.CloneW;
                for (int x = 0; x < _rw; x++)
                {
                    int ci = baseRow + _rx + x;
                    var b = _a.Base[ci];
                    int si = sRow + _sdfColOf[x];
                    if (_a.Cover[si])
                    {
                        float f = _lut[_a.Theta[si]] / 255f;
                        // 皮膚陰影下限＋暖色衰減（避免 0.0 純黑塊造成臉部發黑不自然）
                        float fr = 0.76f + 0.24f * f;
                        float fg = 0.68f + 0.32f * f;
                        float fb = 0.65f + 0.35f * f;
                        _rect[rc++] = new Color32(
                            (byte)Mathf.RoundToInt(b.r * fr),
                            (byte)Mathf.RoundToInt(b.g * fg),
                            (byte)Mathf.RoundToInt(b.b * fb),
                            b.a);
                    }
                    else
                    {
                        _rect[rc++] = b;
                    }
                }
            }
            _tex.SetPixels32(_rx, _ry, _rw, _rh, _rect);
            _tex.Apply(false, false);
        }

        internal void Terminate()
        {
            if (_mat != null) UnityEngine.Object.Destroy(_mat);
            if (_tex != null) UnityEngine.Object.Destroy(_tex);
        }

        private static float WrapPi(float a)
        {
            a %= 2f * Mathf.PI;
            if (a > Mathf.PI) a -= 2f * Mathf.PI;
            if (a < -Mathf.PI) a += 2f * Mathf.PI;
            return a;
        }
    }

    /// <summary>SdfFaceAsset 解碼（PmxAssets.Get 內呼叫）：θ* 場＋coverage＋臉漫貼可讀基準。</summary>
    internal static class SdfFaceLoader
    {
        /// <summary>臉基座（與 bake 同構）：Face 材質唯一頂點平均法線＝F，R=cross(+Y,F) 正規化；失敗時 F=zero。</summary>
        internal static void FaceBasis(PmxParser.PmxModel pmx, int faceMatIdx, out Vector3 F, out Vector3 R)
        {
            F = Vector3.zero;
            R = Vector3.zero;
            if (pmx?.Normals == null || pmx.MaterialTris == null || faceMatIdx < 0 || faceMatIdx >= pmx.MaterialTris.Length)
                return;
            var tris = pmx.MaterialTris[faceMatIdx];
            if (tris == null || tris.Length == 0) return;
            var seen = new HashSet<int>();
            var sum = Vector3.zero;
            foreach (var v in tris)
                if (v >= 0 && v < pmx.Normals.Length && seen.Add(v)) sum += pmx.Normals[v];
            if (sum.sqrMagnitude < 1e-9f) return;
            F = sum.normalized;
            R = Vector3.Cross(Vector3.up, F);
            if (R.sqrMagnitude < 1e-9f) { F = Vector3.zero; return; }
            R.Normalize();
        }

        internal static SdfFaceAsset Build(PmxParser.PmxModel pmx, Dictionary<string, byte[]> textureBytes,
            int faceMatIdx, Vector3 faceF, Vector3 faceR)
        {
            var mat = pmx.Mats[faceMatIdx];
            if (mat.TexIdx < 0 || mat.TexIdx >= pmx.TexPaths.Length) return null;
            var rel = pmx.TexPaths[mat.TexIdx];
            var baseBytes = ResolveBytes(textureBytes, rel);
            if (baseBytes == null || !textureBytes.TryGetValue(SdfFaceAsset.ContainerKey, out var sdfBytes))
                return null;
            try
            {
                var temp = new Texture2D(2, 2, TextureFormat.RGBA32, false, false);
                if (!temp.LoadImage(sdfBytes, false))
                {
                    UnityEngine.Object.Destroy(temp);
                    return null;
                }
                Color32[] px = temp.GetPixels32();
                int w = temp.width, h = temp.height;
                UnityEngine.Object.Destroy(temp);

                var a = new SdfFaceAsset
                {
                    SdfW = w,
                    SdfH = h,
                    Theta = new byte[w * h],
                    Cover = new bool[w * h],
                    FaceF = faceF,
                    FaceR = faceR
                };
                int x0 = int.MaxValue, y0 = int.MaxValue, x1 = int.MinValue, y1 = int.MinValue;
                for (int y = 0; y < h; y++)
                {
                    int row = y * w;
                    for (int x = 0; x < w; x++)
                    {
                        int i = row + x;
                        a.Theta[i] = px[i].r;
                        if (px[i].a > 0)
                        {
                            a.Cover[i] = true;
                            if (x < x0) x0 = x;
                            if (y < y0) y0 = y;
                            if (x > x1) x1 = x;
                            if (y > y1) y1 = y;
                        }
                    }
                }
                if (x1 < x0 || y1 < y0) { a.Theta = null; return null; }
                a.BoxX = x0; a.BoxY = y0; a.BoxW = x1 - x0 + 1; a.BoxH = y1 - y0 + 1;

                var (clone, basePx) = ReadableFaceBase(baseBytes, a.CloneW, a.CloneH);
                if (clone == null) { a.Theta = null; return null; }
                a.Base = basePx;
                a.CloneW = clone.width;
                a.CloneH = clone.height;
                a.BoxW = Mathf.Min(a.BoxW, a.CloneW - a.BoxX);
                a.BoxH = Mathf.Min(a.BoxH, a.CloneH - a.BoxY);
                UnityEngine.Object.Destroy(clone);
                if (a.BoxW <= 0 || a.BoxH <= 0) { a.Theta = null; return null; }
                return a;
            }
            catch (Exception e)
            {
                Plugin.Log.LogWarning($"[GF2] sdf_face decode failed: {e.Message}");
                return null;
            }
        }

        private static byte[] ResolveBytes(Dictionary<string, byte[]> textureBytes, string rel)
        {
            if (textureBytes.TryGetValue(rel, out var b)) return b;
            var bn = System.IO.Path.GetFileName(rel);
            return textureBytes.TryGetValue(bn, out b) ? b : null;
        }

        /// <summary>臉漫貼可讀副本（≤上限，sRGB；縮放公式＝PmxAssets.Downscale），取 Color32 後由呼叫端銷毀。</summary>
        internal static (Texture2D, Color32[]) ReadableFaceBase(byte[] bytes, int tw, int th)
        {
            var t = new Texture2D(2, 2, TextureFormat.RGBA32, false, false);
            if (!t.LoadImage(bytes, false))
            {
                UnityEngine.Object.Destroy(t);
                return (null, null);
            }
            int w = t.width, h = t.height;
            if (w > tw || h > th)
            {
                float k = Mathf.Min(1f, Mathf.Min(tw / (float)w, th / (float)h));
                int nw = Mathf.Max(1, Mathf.RoundToInt(w * k));
                int nh = Mathf.Max(1, Mathf.RoundToInt(h * k));
                var rt = RenderTexture.GetTemporary(nw, nh, 0, RenderTextureFormat.ARGB32, RenderTextureReadWrite.sRGB);
                var prev = RenderTexture.active;
                try
                {
                    Graphics.Blit(t, rt);
                    var dst = new Texture2D(nw, nh, TextureFormat.RGBA32, false, false);
                    RenderTexture.active = rt;
                    dst.ReadPixels(new Rect(0, 0, nw, nh), 0, 0);
                    dst.Apply(false, false);
                    return (dst, dst.GetPixels32());
                }
                finally
                {
                    RenderTexture.active = prev;
                    RenderTexture.ReleaseTemporary(rt);
                    UnityEngine.Object.Destroy(t);
                }
            }
            return (t, t.GetPixels32());
        }
    }
}
