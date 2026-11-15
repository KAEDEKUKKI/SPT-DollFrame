using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using UnityEngine;
#if !GF2_PMX_PARSER_ONLY
using UnityEngine.Rendering;
#endif

namespace GF2
{
    /// <summary>
    /// Runtime PMX 2.0 parser + Unity asset builder for the GFL2 character import.
    /// Format details cross-checked against mmd_tools (core/pmx) and the offline
    /// analysis run: KoledaDefault = ver 2.0, enc=0 (UTF-16LE), addUV=1, vertexidx=2,
    /// textureidx=1, materialidx=1, boneidx=2, morphidx=1, rigididx=2, 60352 verts, 401 bones,
    /// 0 after-physics bones (no physics chains - non-retargeted bones can stay frozen at rest).
    /// The parser is pure (no Unity types) so it can be validated standalone against the real file.
    /// </summary>
    public sealed class PmxParser
    {
        public sealed class PmxMat
        {
            public string Name;
            public string NameEn;
            public Color Diffuse;
            public Color Specular;
            public float Shininess;
            public Color Ambient;
            public bool DoubleSided;
            public bool HasEdge;
            public int TexIdx = -1;
            public int SphereIdx = -1;
            public byte SphereMode;
            public int ToonIdx = -1;
            public bool SharedToon;
            public string Memo;
            public int FaceCount;
        }

        public sealed class PmxBone
        {
            public int Index;
            public string Name;
            public string NameEn;
            public Vector3 Pos;
            public int Parent = -1;
            public ushort Flags;
            // MMD append ("付与親"): this bone additionally receives GrantWeight x the
            // grant parent's local rotation. GFL2 legs are SKINNED to the D bones
            // (左足D/左ひざD/左足首D) which carry no motion of their own - they copy
            // 左足/左ひざ/左足首 through this link; ignoring it froze the leg mesh.
            public int GrantParent = -1;
            public float GrantWeight;
            public bool GrantRotate;

            public bool Rotationable => (Flags & 0x0002) != 0;
            public bool Movable => (Flags & 0x0004) != 0;
            public bool Visible => (Flags & 0x0008) != 0;
            public bool IsIk => (Flags & 0x0020) != 0;
            public bool AfterPhysics => (Flags & 0x0400) != 0;
        }

        public sealed class PmxModel
        {
            public string Name;
            public string NameEn;
            public float Version;
            public Vector3[] Pos;
            public Vector3[] Normals;
            public Vector2[] Uv;
            public BoneWeight[] Weights;
            public string[] TexPaths;
            public PmxMat[] Mats;
            public PmxBone[] Bones;
            public int[][] MaterialTris;
            public Vector3 BoundsMin;
            public Vector3 BoundsMax;
        }

        private readonly BinaryReader _f;
        private readonly short _enc;
        private readonly byte _addUv;
        private readonly byte _vtxIdx, _texIdx, _boneIdx;

        private PmxParser(BinaryReader f, short enc, byte addUv, byte vtxIdx, byte texIdx, byte boneIdx)
        {
            _f = f; _enc = enc; _addUv = addUv;
            _vtxIdx = vtxIdx; _texIdx = texIdx; _boneIdx = boneIdx;
        }

        internal static PmxModel Parse(string path) => Parse(File.ReadAllBytes(path));

        internal static PmxModel Parse(byte[] pmxBytes)
        {
            // one bulk read: the vertex loop does ~20 tiny reads per vertex, which is
            // slow against an unbuffered FileStream (this runs on the main thread at raid load)
            using var f = new BinaryReader(new MemoryStream(pmxBytes, false));
            var magic = f.ReadBytes(4);
            if (magic[0] != 'P' || magic[1] != 'M' || magic[2] != 'X' || magic[3] != ' ')
                throw new InvalidDataException("not a PMX file");
            float ver = f.ReadSingle();
            int gcount = f.ReadSByte();
            var g = f.ReadBytes(8);
            var parser = new PmxParser(f, g[0], g[1], g[2], g[3], g[5]); // g[4]/g[6]/g[7] (mat/morph/rig idx) are read but unused
            if (ver < 2.0f || ver >= 2.1f)
                throw new InvalidDataException($"unsupported PMX version {ver:0.##} (this importer handles 2.0 only)");
            return parser.ReadBody(ver);
        }

        private PmxModel ReadBody(float ver)
        {
            var m = new PmxModel { Version = ver };
            m.Name = ReadStr();
            m.NameEn = ReadStr();
            ReadStr(); ReadStr(); // comments

            // ---- vertices ----
            int nv = _f.ReadInt32();
            m.Pos = new Vector3[nv];
            m.Normals = new Vector3[nv];
            m.Uv = new Vector2[nv];
            m.Weights = new BoneWeight[nv];
            m.BoundsMin = new Vector3(float.MaxValue, float.MaxValue, float.MaxValue);
            m.BoundsMax = new Vector3(float.MinValue, float.MinValue, float.MinValue);
            for (int i = 0; i < nv; i++)
            {
                var p = ReadV3();
                m.Pos[i] = p;
                m.Normals[i] = ReadV3();
                m.Uv[i] = new Vector2(_f.ReadSingle(), _f.ReadSingle());
                for (int k = 0; k < _addUv; k++) ReadV4();
                if (p.x < m.BoundsMin.x) m.BoundsMin.x = p.x;
                if (p.y < m.BoundsMin.y) m.BoundsMin.y = p.y;
                if (p.z < m.BoundsMin.z) m.BoundsMin.z = p.z;
                if (p.x > m.BoundsMax.x) m.BoundsMax.x = p.x;
                if (p.y > m.BoundsMax.y) m.BoundsMax.y = p.y;
                if (p.z > m.BoundsMax.z) m.BoundsMax.z = p.z;
                byte wt = _f.ReadByte();
                m.Weights[i] = ReadWeights(wt);
                _f.ReadSingle(); // edge scale
            }
            // ---- faces: convert to per-material triangle lists later ----
            // vertex indices may exceed 32767 (60352 verts); read unsigned
            int nfaceIndices = _f.ReadInt32();
            var allIndices = new int[nfaceIndices];
            for (int i = 0; i < nfaceIndices; i++)
                allIndices[i] = ReadIdx(_vtxIdx, false);
            // ---- textures ----
            int nt = _f.ReadInt32();
            m.TexPaths = new string[nt];
            for (int i = 0; i < nt; i++)
                m.TexPaths[i] = ReadStr().Replace('\\', '/');
            // ---- materials ----
            int nm = _f.ReadInt32();
            m.Mats = new PmxMat[nm];
            m.MaterialTris = new int[nm][];
            int faceCursor = 0;
            for (int i = 0; i < nm; i++)
            {
                var mat = new PmxMat
                {
                    Name = ReadStr(),
                    NameEn = ReadStr(),
                    Diffuse = ReadColor(),
                    Specular = ReadColor3(),
                    Shininess = _f.ReadSingle(),
                    Ambient = ReadColor3()
                };
                byte flags = _f.ReadByte();
                mat.DoubleSided = (flags & 0x01) != 0;
                mat.HasEdge = (flags & 0x10) != 0;
                ReadColor(); // edge color
                _f.ReadSingle(); // edge size
                mat.TexIdx = ReadIdx(_texIdx, false);
                mat.SphereIdx = ReadIdx(_texIdx, false);
                mat.SphereMode = _f.ReadByte();
                int sharedToon = _f.ReadByte();
                mat.SharedToon = sharedToon == 1;
                mat.ToonIdx = mat.SharedToon ? _f.ReadByte() : ReadIdx(_texIdx, false);
                mat.Memo = ReadStr();
                mat.FaceCount = _f.ReadInt32();
                m.Mats[i] = mat;

                var tris = new int[mat.FaceCount];
                for (int k = 0; k < mat.FaceCount; k++)
                    tris[k] = faceCursor + k < nfaceIndices ? allIndices[faceCursor + k] : 0;
                faceCursor += mat.FaceCount;
                m.MaterialTris[i] = tris;
            }
            if (faceCursor != nfaceIndices)
                Plugin.Log.LogWarning($"[GF2] pmx face count mismatch: materials sum {faceCursor} vs indices {nfaceIndices}");
            // ---- bones ----
            // Two exporters are in circulation for these models. The GFL2 variant packs
            // [u16 parent | u8 layer | u8 pad] into a 4-byte int, skips 2 extra bytes and
            // shifts the optional-block flag bits (grant=0x100, fixed-axis=0x400; verified
            // end to end against the 401-bone gold walk in analysis/koleda_bones.json).
            // Other exports use the stock PMX 2.0 record ([i32 parent][u8 layer][u16 flags],
            // grant=0x40, fixed-axis=0x80, local-axes=0x100, IK link count is int32).
            // The stream is a seekable MemoryStream: try the GFL2 layout first (the case the
            // runtime was verified against) and fall back to the stock layout on decode failure.
            int nb = _f.ReadInt32();
            if (nb < 0 || nb > 65536) throw new InvalidDataException($"implausible bone count {nb}");
            var boneSectionStart = _f.BaseStream.Position;
            try
            {
                m.Bones = ReadBonesGfl(nb);
            }
            catch (Exception e) when (e is InvalidDataException or EndOfStreamException || e is ArgumentOutOfRangeException)
            {
                Plugin.Log.LogInfo($"[GF2] GFL bone layout attempt failed: {e.Message}");
                _f.BaseStream.Position = boneSectionStart;
                m.Bones = ReadBonesSpec(nb);
                Plugin.Log.LogInfo($"[GF2] pmx uses the stock PMX bone layout ({m.Bones.Length} bones)");
            }
            // clamp out-of-range / negative vertex weight indices now that the bone count is known
            for (int i = 0; i < m.Weights.Length; i++)
                m.Weights[i] = ClampWeight(m.Weights[i], nb);
            // morphs / display frames / rigidbodies / joints deliberately unread
            return m;
        }

        private PmxBone[] ReadBonesGfl(int nb)
        {
            var bones = new PmxBone[nb];
            for (int i = 0; i < nb; i++)
            {
                var b = new PmxBone { Index = i, Name = ReadStr(), NameEn = ReadStr(), Pos = ReadV3() };
                int parentRaw = _f.ReadInt32();
                b.Parent = (parentRaw & 0xFFFF) == 0xFFFF ? -1 : (parentRaw & 0xFFFF);
                _f.ReadByte(); // layer (packed parentRaw high half)
                _f.ReadByte(); // pad
                b.Flags = _f.ReadUInt16();
                ReadBoneTail(b);
                // GFL2 exporter flag map: 0x100 = grant [i16 parent + f32 weight],
                // 0x200 = after-physics (no bytes), 0x400 = fixed axis [3f],
                // 0x800 = local axes [2x 3f]; 0x1000 seen on weapon bones, no bytes
                if ((b.Flags & 0x0100) != 0) ReadGrant(b, rotate: true);
                if ((b.Flags & 0x0400) != 0) ReadV3();
                if ((b.Flags & 0x0800) != 0) { ReadV3(); ReadV3(); }
                ReadBoneIk(b, gflLimits: true);
                ValidateBone(bones, b, nb);
                bones[i] = b;
            }
            return bones;
        }

        private PmxBone[] ReadBonesSpec(int nb)
        {
            var bones = new PmxBone[nb];
            for (int i = 0; i < nb; i++)
            {
                var b = new PmxBone { Index = i, Name = ReadStr(), NameEn = ReadStr(), Pos = ReadV3() };
                b.Parent = _f.ReadInt32();
                _f.ReadByte(); // deform layer
                b.Flags = _f.ReadUInt16();
                ReadBoneTail(b);
                // stock PMX 2.0 flag map: grant=0x40 [idx+f32], fixed axis=0x80 [3f],
                // local axes=0x100 [24B], external parent=0x400 [i32]
                if ((b.Flags & 0x0040) != 0) ReadGrant(b, rotate: true);
                if ((b.Flags & 0x0080) != 0) ReadV3();
                if ((b.Flags & 0x0100) != 0) { ReadV3(); ReadV3(); }
                if ((b.Flags & 0x0400) != 0) _f.ReadInt32();
                ReadBoneIk(b, gflLimits: false);
                ValidateBone(bones, b, nb);
                bones[i] = b;
            }
            return bones;
        }

        private void ReadGrant(PmxBone b, bool rotate)
        {
            b.GrantParent = ReadIdx(_boneIdx, true);
            b.GrantWeight = _f.ReadSingle();
            b.GrantRotate = rotate;
        }

        private void ReadBoneTail(PmxBone b)
        {
            if ((b.Flags & 0x0001) != 0)
                ReadIdx(_boneIdx, true); // tail via bone index (unused)
            else
                ReadV3(); // tail offset
        }

        private void ReadBoneIk(PmxBone b, bool gflLimits)
        {
            if ((b.Flags & 0x0020) == 0) return;
            ReadIdx(_boneIdx, true); // ik target
            _f.ReadInt32(); // loop count
            _f.ReadSingle(); // angle limit
            int nl = _f.ReadInt32();
            for (int l = 0; l < nl; l++)
            {
                ReadIdx(_boneIdx, true);
                byte lf = _f.ReadByte();
                if (gflLimits)
                {
                    // this exporter always writes min+max together (24B) when any limit is set
                    if (lf != 0) { ReadV3(); ReadV3(); }
                }
                else
                {
                    if ((lf & 1) != 0) ReadV3(); // min limit
                    if ((lf & 2) != 0) ReadV3(); // max limit
                }
            }
        }

        private static void ValidateBone(PmxBone[] acc, PmxBone b, int nb)
        {
            if (b.Parent < -1 || b.Parent >= nb)
                throw new InvalidDataException($"bone #{b.Index} '{b.Name}' parent {b.Parent} out of range (layout variant mismatch)");
            if (b.Index == 0 && b.Parent != -1)
                throw new InvalidDataException($"bone 0 is not a root (parent {b.Parent})");
            if (acc[b.Index] != null)
                throw new InvalidDataException($"bone #{b.Index} parsed twice");
        }

        private string ReadStr()
        {
            int len = _f.ReadInt32();
            var raw = _f.ReadBytes(len);
            return (_enc == 0 ? Encoding.Unicode : Encoding.UTF8).GetString(raw);
        }

        private Vector3 ReadV3() => new Vector3(_f.ReadSingle(), _f.ReadSingle(), _f.ReadSingle());

        private void ReadV4() { _f.ReadSingle(); _f.ReadSingle(); _f.ReadSingle(); _f.ReadSingle(); }

        private Color ReadColor() => new Color(_f.ReadSingle(), _f.ReadSingle(), _f.ReadSingle(), _f.ReadSingle());

        private Color ReadColor3() => new Color(_f.ReadSingle(), _f.ReadSingle(), _f.ReadSingle(), 1f);

        private int ReadIdx(byte size, bool signed)
        {
            if (size == 1) return signed ? _f.ReadSByte() : _f.ReadByte();
            if (size == 2) return signed ? _f.ReadInt16() : _f.ReadUInt16();
            return signed ? _f.ReadInt32() : (int)_f.ReadUInt32();
        }

        private BoneWeight ReadWeights(byte wt)
        {
            int[] bones;
            float[] w;
            switch (wt)
            {
                case 0: // BDEF1
                    bones = new[] { ReadIdx(_boneIdx, true), 0, 0, 0 };
                    w = new[] { 1f, 0f, 0f, 0f };
                    break;
                case 1: // BDEF2
                    bones = new[] { ReadIdx(_boneIdx, true), ReadIdx(_boneIdx, true), 0, 0 };
                    w = new[] { _f.ReadSingle(), 0f, 0f, 0f };
                    w[1] = 1f - w[0];
                    break;
                case 3: // SDEF -> approximated as BDEF2 (C/R0/R1 ignored)
                    bones = new[] { ReadIdx(_boneIdx, true), ReadIdx(_boneIdx, true), 0, 0 };
                    w = new[] { _f.ReadSingle(), 0f, 0f, 0f };
                    w[1] = 1f - w[0];
                    _f.ReadSingle(); // second weight (== 1-w)
                    _f.ReadSingle(); _f.ReadSingle(); _f.ReadSingle(); // C
                    _f.ReadSingle(); _f.ReadSingle(); _f.ReadSingle(); // R0
                    _f.ReadSingle(); _f.ReadSingle(); _f.ReadSingle(); // R1
                    break;
                case 2: // BDEF4
                case 4: // QDEF
                    bones = new[] { ReadIdx(_boneIdx, true), ReadIdx(_boneIdx, true), ReadIdx(_boneIdx, true), ReadIdx(_boneIdx, true) };
                    w = new[] { _f.ReadSingle(), _f.ReadSingle(), _f.ReadSingle(), _f.ReadSingle() };
                    break;
                default:
                    throw new InvalidDataException($"unknown vertex weight type {wt}");
            }
            var bw = new BoneWeight
            {
                boneIndex0 = bones[0], boneIndex1 = bones[1],
                boneIndex2 = bones[2], boneIndex3 = bones[3],
                weight0 = w[0], weight1 = w[1], weight2 = w[2], weight3 = w[3]
            };
            return bw;
        }

        /// <summary>
        /// Drops invalid influences, merges duplicates, renormalizes and sorts descending
        /// (Unity's 1/2-bone quality settings only read the first influences).
        /// </summary>
        private static BoneWeight ClampWeight(in BoneWeight w, int count)
        {
            var bones = new[] { w.boneIndex0, w.boneIndex1, w.boneIndex2, w.boneIndex3 };
            var wt = new[] { w.weight0, w.weight1, w.weight2, w.weight3 };
            for (int i = 0; i < 4; i++)
                if (bones[i] < 0 || bones[i] >= count || !(wt[i] > 0f))
                { bones[i] = 0; wt[i] = 0f; }
            for (int i = 0; i < 4; i++)
                for (int j = i + 1; j < 4; j++)
                    if (wt[i] > 0f && wt[j] > 0f && bones[i] == bones[j])
                    { wt[i] += wt[j]; wt[j] = 0f; bones[j] = 0; }
            float total = wt[0] + wt[1] + wt[2] + wt[3];
            if (total <= 0f)
            {
                // no valid influence: pin to the first valid source bone (or bone 0)
                // instead of leaving an all-zero weight that collapses the vertex
                int b = w.boneIndex0 >= 0 && w.boneIndex0 < count ? w.boneIndex0 : 0;
                return new BoneWeight { boneIndex0 = b, weight0 = 1f };
            }
            for (int i = 0; i < 4; i++) wt[i] /= total;
            for (int i = 1; i < 4; i++)
                for (int j = i; j > 0 && wt[j] > wt[j - 1]; j--)
                {
                    (wt[j], wt[j - 1]) = (wt[j - 1], wt[j]);
                    (bones[j], bones[j - 1]) = (bones[j - 1], bones[j]);
                }
            return new BoneWeight
            {
                boneIndex0 = bones[0], boneIndex1 = bones[1],
                boneIndex2 = bones[2], boneIndex3 = bones[3],
                weight0 = wt[0], weight1 = wt[1], weight2 = wt[2], weight3 = wt[3]
            };
        }
    }

    /// <summary>
    /// .gfc character package (self-contained: PMX binary + all referenced textures).
    /// Layout v1: "GFCP" | u32 version | u32+utf8 display name | u32+utf8 pmx filename |
    /// u32+bytes PMX | u32 texCount | { u32+utf8 authored texture path | u32+bytes PNG }.
    /// Pure IO, no Unity types: one reader shared verbatim by the plugin and the packager.
    /// </summary>
    internal sealed class PmxContainerData
    {
        internal string Name;
        internal string PmxFilename;
        internal byte[] Pmx;
        internal Dictionary<string, byte[]> Textures;
    }

    internal static class PmxContainer
    {
        internal const string Magic = "GFCP";
        internal const uint FormatVersion = 1;

        internal static PmxContainerData Read(Stream input)
        {
            var f = new BinaryReader(input);
            if (Encoding.ASCII.GetString(f.ReadBytes(4)) != Magic)
                throw new InvalidDataException($"not a {Magic} character package");
            uint ver = f.ReadUInt32();
            if (ver != FormatVersion)
                throw new InvalidDataException($"package format {ver}, supported is {FormatVersion}");
            var d = new PmxContainerData
            {
                Name = Encoding.UTF8.GetString(f.ReadBytes(f.ReadInt32())),
                PmxFilename = Encoding.UTF8.GetString(f.ReadBytes(f.ReadInt32())),
                Textures = new Dictionary<string, byte[]>(StringComparer.Ordinal)
            };
            d.Pmx = f.ReadBytes(f.ReadInt32());
            int n = f.ReadInt32();
            for (int i = 0; i < n; i++)
                d.Textures[Encoding.UTF8.GetString(f.ReadBytes(f.ReadInt32()))] = f.ReadBytes(f.ReadInt32());
            return d;
        }

        /// <summary>
        /// 只讀 magic + 版本 + 顯示名稱（cache 鍵），payload 全部略過。warm 命中時呼叫端
        /// 不需要把數十 MB 的容器讀進記憶體；壞 magic 與 Read 同樣拋 InvalidDataException。
        /// 版本不在此驗證：warm 命中代表先前已由 Read 驗證過；cold 命中會走 Read 補驗。
        /// </summary>
        internal static string ReadName(Stream input)
        {
            var f = new BinaryReader(input);
            if (Encoding.ASCII.GetString(f.ReadBytes(4)) != Magic)
                throw new InvalidDataException($"not a {Magic} character package");
            f.ReadUInt32();
            return Encoding.UTF8.GetString(f.ReadBytes(f.ReadInt32()));
        }
    }

    /// <summary>
    /// PmxAssets 的 refcount / 保留策略（純 C#，離線 harness 驅動同一份）。
    /// refcount 歸零不再硬逐出：entry 保留為 idle，選單切角色、跨戰局重掛、pooled
    /// re-init 都走 warm；僅當 idle 數超過上限時逐出「最早 idle 起保留至今」者
    /// （caller 負責銷毀 Unity assets 並記 cache: evicted 行）。
    /// 上限可依安裝時組態調大（<see cref="PmxAssets.SetIdleCap"/>）：瀏覽 20+ 個角色
    /// 時 cap=2 意味每次切換都重新冷解析（250-460ms hitch + 貼圖重建 = 切換不流暢）。
    /// </summary>
    internal sealed class PmxCachePolicy
    {
        /// <summary>預設保留 idle 包數上限（無覆寫時）。2 = A↔B 來回切與「上一個角色」都保持 warm。</summary>
        internal const int IdleCap = 2;
        private readonly int _idleCap;
        private readonly Dictionary<string, int> _refs = new Dictionary<string, int>();
        private readonly List<string> _idleOldestFirst = new List<string>();

        internal PmxCachePolicy() : this(IdleCap) { }

        /// <summary>帶上限建構（runtime 由組態驅動；harness 驅動覆寫行為）。</summary>
        internal PmxCachePolicy(int idleCap)
        {
            _idleCap = idleCap > 0 ? idleCap : IdleCap;
        }

        internal void Acquire(string key)
        {
            _refs.TryGetValue(key, out int n);
            _refs[key] = n + 1;
            _idleOldestFirst.Remove(key);
        }

        /// <summary>refcount--。歸 0 保留為 idle；回傳本次需要硬逐出（銷毀 assets）的 key 清單。</summary>
        internal List<string> Release(string key)
        {
            var victims = new List<string>();
            if (!_refs.TryGetValue(key, out int n)) return victims;
            if (n > 1) { _refs[key] = n - 1; return victims; }
            _refs.Remove(key);
            _idleOldestFirst.Add(key);
            while (_idleOldestFirst.Count > _idleCap)
            {
                victims.Add(_idleOldestFirst[0]);
                _idleOldestFirst.RemoveAt(0);
            }
            return victims;
        }

        internal int RefCount(string key) => _refs.TryGetValue(key, out int n) ? n : 0;
        internal int IdleCount => _idleOldestFirst.Count;

        internal void Remove(string key)
        {
            _refs.Remove(key);
            _idleOldestFirst.Remove(key);
        }
    }

#if !GF2_PMX_PARSER_ONLY
    /// <summary>
    /// Cached Unity assets for one PMX file: mesh + materials (Standard) + parsed model.
    /// Built once, shared by every player using the model. The mesh is skinned against a
    /// compact bone list (only weighted bones) with bindposes expressed in PMX model space,
    /// so it is independent of where/how a rig instance is attached.
    /// </summary>
    internal sealed class PmxAssets
    {
        internal static readonly Dictionary<string, PmxAssets> Cache = new Dictionary<string, PmxAssets>();

        internal bool IsAlive()
        {
            if (Model == null || Mesh == null || Materials == null) return false;
            if (HeadMesh != null && !HeadMesh) return false;
            for (int i = 0; i < Materials.Length; i++)
                if (Materials[i] == null) return false;
            if (Textures != null)
                for (int i = 0; i < Textures.Length; i++)
                    if (Textures[i] == null) return false;
            return true;
        }

        private static void PurgeDeadEntry(string key, PmxAssets a)
        {
            Cache.Remove(key);
            Policy.Remove(key);
            if (a == null) return;
            if (a.Mesh != null) UnityEngine.Object.Destroy(a.Mesh);
            if (a.HeadMesh != null) UnityEngine.Object.Destroy(a.HeadMesh);
            if (a.Materials != null)
                foreach (var m in a.Materials)
                    if (m != null) UnityEngine.Object.Destroy(m);
            if (a.Textures != null)
                foreach (var t in a.Textures)
                    if (t != null) UnityEngine.Object.Destroy(t);
        }

        internal static bool IsCached(string cacheKey)
        {
            if (!Cache.TryGetValue(cacheKey, out var a)) return false;
            if (a != null && a.IsAlive()) return true;
            PurgeDeadEntry(cacheKey, a);
            return false;
        }

        internal static int CacheCount => Cache.Count;
        private static PmxCachePolicy _policy;
        private static int _idleCapOverride;

        /// <summary>
        /// Plugin.Awake 在任何 cache 使用（patch 掛載／探測）前呼叫：0 = 預設 IdleCap。
        /// </summary>
        internal static void SetIdleCap(int idleCap) => _idleCapOverride = idleCap;

        private static PmxCachePolicy Policy => _policy ??= new PmxCachePolicy(_idleCapOverride > 0 ? _idleCapOverride : PmxCachePolicy.IdleCap);

        internal static void Acquire(string key)
        {
            if (!Cache.ContainsKey(key))
            {
                Plugin.Log.LogWarning($"[GF2] cache: acquire on non-cached key '{key}'");
                return;
            }
            Policy.Acquire(key);
        }

        /// <summary>
        /// warm 命中並取得 refcount（warm 附掛的 keep-alive）：此前三個 consumer 的 warm
        /// 路徑只取資產不 Acquire——每包唯一的 refcredit 來自首次冷載，會被任何其他
        /// consumer 的 Release 花掉（卡片收尾／BodyReplacer 銷毀／另一個 view 的
        /// Teardown），正在渲染的 entry 掉進 idle LRU 後被逐出，EvictEntry 銷毀的正是
        /// 活著的 rig 正引用的共享 Material/Texture（SMR 綁共享材質）→ 預覽無聲消失。
        /// 若快取內的 Unity 資產因外部 UnloadUnusedAssets 失效，自動清除並回傳 null 讓呼叫端冷載重建。
        /// </summary>
        internal static PmxAssets AcquireIfCached(string cacheKey)
        {
            if (!Cache.TryGetValue(cacheKey, out var a)) return null;
            if (a == null || !a.IsAlive())
            {
                PurgeDeadEntry(cacheKey, a);
                return null;
            }
            Policy.Acquire(cacheKey);
            return a;
        }

        /// <summary>
        /// 統一的容器預載入口：先讀 header 嘗試 warm Acquire；若未命中或快取資產已失效，
        /// 立即於同一呼叫內回退完整容器讀取與冷載建構，確保切換角色永不因快取競態而失敗。
        /// </summary>
        internal static PmxAssets LoadOrAcquire(string pkgPath, int maxTexSize, bool flipUv,
            string[] accessoryPatterns, out string pkgName, out bool warm, out double elapsedMs)
        {
            PmxContainerData pkg = null;
            using (var fs = File.OpenRead(pkgPath))
            {
                pkgName = PmxContainer.ReadName(fs);
                warm = IsCached(pkgName);
                if (!warm)
                {
                    fs.Position = 0;
                    pkg = PmxContainer.Read(fs);
                }
            }
            var sw = System.Diagnostics.Stopwatch.StartNew();
            var assets = warm ? AcquireIfCached(pkgName) : null;
            if (assets == null)
            {
                warm = false;
                if (pkg == null)
                {
                    using (var fs = File.OpenRead(pkgPath))
                        pkg = PmxContainer.Read(fs);
                }
                assets = Get(pkgName, pkg.Pmx, pkg.Textures, maxTexSize, flipUv, accessoryPatterns);
            }
            sw.Stop();
            elapsedMs = sw.Elapsed.TotalMilliseconds;
            return assets;
        }

        internal static void Release(string key)
        {
            if (Policy.RefCount(key) <= 0) return;
            var victims = Policy.Release(key);
            if (Policy.RefCount(key) == 0)
                Plugin.Log.LogInfo($"[GF2] cache: idle {key} (kept {Policy.IdleCount})");
            foreach (var v in victims) EvictEntry(v);
        }

        private static void EvictEntry(string key)
        {
            if (!Cache.TryGetValue(key, out var a)) return;
            Cache.Remove(key);
            if (a.Mesh != null) UnityEngine.Object.Destroy(a.Mesh);
            if (a.HeadMesh != null) UnityEngine.Object.Destroy(a.HeadMesh);
            if (a.Materials != null)
                foreach (var m in a.Materials)
                    if (m != null) UnityEngine.Object.Destroy(m);
            if (a.Textures != null)
                foreach (var t in a.Textures)
                    if (t != null) UnityEngine.Object.Destroy(t);
            Plugin.Log.LogInfo($"[GF2] cache: evicted {key}");
        }

        internal PmxParser.PmxModel Model;
        internal Mesh Mesh;
        internal Mesh HeadMesh;
        internal Material[] Materials;
        /// <summary>Face 材質在 Materials 陣列中的槽位（-1＝無）；SDF 臉驅動的覆寫錨點。</summary>
        internal int FaceSlot = -1;
        /// <summary>包級 SDF 臉烘焙資料（sdf_face.png 缺席或解碼失敗時為 null＝沿用原渲染）。</summary>
        internal SdfFaceAsset SdfFace;
        internal int[] KeptMaterialIdx;
        internal string[] UnresolvedTextures;
        internal Texture2D[] Textures;
        /// <summary>Compact skin bone slot -> PMX bone index (mesh boneWeights index into this).</summary>
        internal int[] SkinBones;
        internal int[] HeadSkinBones;

        private static Shader ResolveCharacterShader() => ShaderManager.ResolveOpaqueShader();

        private static bool IsEyeBlendMaterial(string matName, string texPath)
        {
            if (!string.IsNullOrEmpty(matName))
            {
                var mn = matName.ToLowerInvariant();
                if (mn.Contains("eyes+") || mn.Contains("eyeblend") || mn.Contains("eye_blend") ||
                    mn.Contains("eyeshadow") || mn.Contains("eyehi") || mn.Contains("eye_hi") ||
                    mn.Contains("cornea"))
                    return true;
            }
            if (!string.IsNullOrEmpty(texPath))
            {
                var tp = texPath.ToLowerInvariant();
                if (tp.Contains("eyeblend") || tp.Contains("eye_blend"))
                    return true;
            }
            return false;
        }

        private static bool IsAlphaCutoutMaterial(string matName)
        {
            if (string.IsNullOrEmpty(matName)) return false;
            var mn = matName.ToLowerInvariant();
            return mn.Contains("lash") || mn.Contains("brow") || mn.Contains("まつ毛") || mn.Contains("眉毛");
        }

        /// <summary>
        /// PBR + NPR 材質分類：依材質名稱決定着色模式（0=布料/金屬/皮革, 1=皮膚 SSS, 2=頭髮各向異性, 3=臉部 GPU-SDF）、金屬度與光滑度。
        /// </summary>
        private static (int matMode, float metallic, float smoothness) ClassifyMaterial(string matName)
        {
            if (string.IsNullOrEmpty(matName)) return (0, 0f, 0.26f);
            var s = matName.ToLowerInvariant();
            if (s == "face" || s.Contains("face") || s.Contains("顔")) return (3, 0f, 0.28f);
            if (s.Contains("eye") || s.Contains("hitomi") || s.Contains("lash") || s.Contains("brow") || s.Contains("瞳") || s.Contains("まつ毛"))
                return (0, 0f, 0.65f);
            if (s.Contains("skin") || s.Contains("肌") || s.Contains("素体")) return (1, 0f, 0.34f);
            if (s.Contains("hair") || s.Contains("kami") || s.Contains("髪")) return (2, 0f, 0.48f);
            if (s.Contains("metal") || s.Contains("iron") || s.Contains("steel") || s.Contains("gold") || s.Contains("silver") ||
                s.Contains("buckle") || s.Contains("zip") || s.Contains("button") || s.Contains("armor") ||
                s.Contains("金属") || s.Contains("鉄") || s.Contains("鋼") || s.Contains("金具"))
                return (0, 0.78f, 0.72f);
            if (s.Contains("leather") || s.Contains("boot") || s.Contains("shoe") || s.Contains("belt") || s.Contains("strap") || s.Contains("glove") ||
                s.Contains("革") || s.Contains("靴") || s.Contains("ベルト") || s.Contains("手袋"))
                return (0, 0.06f, 0.52f);
            if (s.Contains("cloth") || s.Contains("cth") || s.Contains("dorm") || s.Contains("sock"))
                return (0, 0f, 0.26f);
            return (0, 0f, 0.28f);
        }

        /// <summary>Built from one packaged character (name/Pmx/Textures, see PmxContainer).</summary>
        internal static PmxAssets Get(string cacheKey, byte[] pmxBytes, Dictionary<string, byte[]> textureBytes, int maxTexSize, bool flipUv, string[] accessoryPatterns)
        {
            if (Cache.TryGetValue(cacheKey, out var cached))
            {
                if (cached != null && cached.IsAlive())
                {
                    Acquire(cacheKey);
                    return cached;
                }
                PurgeDeadEntry(cacheKey, cached);
            }

            var pmx = PmxParser.Parse(pmxBytes);
            var a = new PmxAssets { Model = pmx };

            var textures = new Dictionary<int, Texture2D>();
            var unresolved = new List<string>();
            // accessory material ranges are dropped from the mesh; their faces then
            // belong to no submesh and never render
            var keptIdx = new List<int>();
            for (int i = 0; i < pmx.Mats.Length; i++)
            {
                if (!IsAccessory(pmx, i, accessoryPatterns))
                    keptIdx.Add(i);
            }
            a.KeptMaterialIdx = keptIdx.ToArray();

            foreach (var mat in pmx.Mats)
            {
                if (mat.TexIdx < 0 || mat.TexIdx >= pmx.TexPaths.Length) continue;
                var path = pmx.TexPaths[mat.TexIdx];
                if (path.EndsWith(".spa", StringComparison.OrdinalIgnoreCase) || path.EndsWith(".sph", StringComparison.OrdinalIgnoreCase) ||
                    path.EndsWith(".bmp", StringComparison.OrdinalIgnoreCase) || path.EndsWith(".tga", StringComparison.OrdinalIgnoreCase))
                    continue;
                if (!textures.ContainsKey(mat.TexIdx))
                {
                    var tex = LoadTexture(path, textureBytes, maxTexSize);
                    if (tex != null) textures[mat.TexIdx] = tex;
                    else unresolved.Add(path);
                }
            }

            var opaqueShader = ResolveCharacterShader();
            var transparentShader = ShaderManager.ResolveTransparentShader();
            if (opaqueShader == null)
                Plugin.Log.LogError("[GF2] Character shader not found; materials will be unlit pink");
            var materials = new List<Material>();
            int faceMatIdx = -1;
            for (int i = 0; i < pmx.Mats.Length; i++)
                if (pmx.Mats[i].Name == "Face") { faceMatIdx = i; break; }
            int faceSlot = -1;
            foreach (int i in a.KeptMaterialIdx)
            {
                var mat = pmx.Mats[i];
                var tint = mat.Diffuse;
                string texPath = (mat.TexIdx >= 0 && mat.TexIdx < pmx.TexPaths.Length) ? pmx.TexPaths[mat.TexIdx] : null;
                bool isEyeBlend = IsEyeBlendMaterial(mat.Name, texPath);
                bool isTransparent = tint.a < 0.90f || isEyeBlend;
                bool isCutout = !isTransparent && IsAlphaCutoutMaterial(mat.Name);
                var matShader = isTransparent ? (transparentShader ?? opaqueShader) : opaqueShader;
                var m = new Material(matShader ?? Shader.Find("Sprites/Default"))
                {
                    name = "GF2_" + mat.Name,
                    hideFlags = HideFlags.DontUnloadUnusedAsset
                };
                if (mat.TexIdx >= 0 && textures.TryGetValue(mat.TexIdx, out var tex))
                    m.mainTexture = tex;
                var (_, metallic, smoothness) = ClassifyMaterial(mat.Name);
                ShaderManager.ConfigureMaterial(m, mat.Name, metallic, smoothness, tint.a, isTransparent, isCutout);
                if (i == faceMatIdx) faceSlot = materials.Count;
                // Standard has no cull property; double-sided materials get back-face
                // triangles duplicated in the mesh instead (see below)
                materials.Add(m);
            }
            a.Materials = materials.ToArray();
            a.FaceSlot = faceSlot;
            if (faceMatIdx >= 0 && faceSlot >= 0)
            {
                SdfFaceLoader.FaceBasis(pmx, faceMatIdx, out var faceF, out var faceR);
                if (faceF.sqrMagnitude > 0.9f)
                    a.SdfFace = SdfFaceLoader.Build(pmx, textureBytes, faceMatIdx, faceF, faceR);
            }
            a.UnresolvedTextures = unresolved.ToArray();
            a.Textures = textures.Values.ToArray();

            // Split mesh into Body and Head by bone weights for first-person hiding + 100% full-body ground shadows
            var (bodyMesh, headMesh) = MeshSplitter.SplitMeshByBones(pmx, null);
            int doubleSided = 0;
            if (headMesh != null && headMesh.TriangleCount > 0)
            {
                a.Mesh = BuildUnityMesh("GF2_" + pmx.Name + "_Body", bodyMesh, pmx, a.KeptMaterialIdx, flipUv, ref doubleSided, out a.SkinBones);
                a.HeadMesh = BuildUnityMesh("GF2_" + pmx.Name + "_Head", headMesh, pmx, a.KeptMaterialIdx, flipUv, ref doubleSided, out a.HeadSkinBones);
            }
            else
            {
                var fullMesh = new PmxMesh
                {
                    Name = pmx.Name,
                    Pos = pmx.Pos,
                    Normals = pmx.Normals,
                    Uv = pmx.Uv,
                    Weights = pmx.Weights,
                    MaterialTris = pmx.MaterialTris,
                    BoundsMin = pmx.BoundsMin,
                    BoundsMax = pmx.BoundsMax
                };
                a.Mesh = BuildUnityMesh("GF2_" + pmx.Name, fullMesh, pmx, a.KeptMaterialIdx, flipUv, ref doubleSided, out a.SkinBones);
                a.HeadMesh = null;
                a.HeadSkinBones = null;
            }

            Cache[cacheKey] = a;
            Acquire(cacheKey);
            int triTotal = 0;
            foreach (var tr in pmx.MaterialTris) triTotal += tr.Length;
            Plugin.Log.LogInfo($"[GF2] pmx loaded: '{pmx.Name}' ({cacheKey}) verts={pmx.Pos.Length} tris={triTotal / 3} " +
                               $"mats={pmx.Mats.Length} kept={a.Materials.Length} (double-sided {doubleSided}) bones={pmx.Bones.Length} " +
                               $"skin={a.SkinBones.Length} height={pmx.BoundsMax.y - pmx.BoundsMin.y:0.0##}u " +
                               $"textures(ok={textures.Count},miss={unresolved.Count})");
            if (a.SkinBones.Length > 255)
                Plugin.Log.LogWarning($"[GF2] pmx mesh skins {a.SkinBones.Length} bones (>255); some platforms cap skinned bones");
            if (unresolved.Count > 0)
                Plugin.Log.LogWarning($"[GF2] pmx textures unresolved: {string.Join(", ", a.UnresolvedTextures)}");
            return a;
        }

        private static Mesh BuildUnityMesh(string meshName, PmxMesh pmxMesh, PmxParser.PmxModel pmx, int[] keptMaterialIdx, bool flipUv, ref int doubleSidedCount, out int[] outSkinBones)
        {
            var used = new bool[pmx.Bones.Length];
            foreach (var w in pmxMesh.Weights) MarkUsed(w, used);
            var slotOf = new int[pmx.Bones.Length];
            var skinBones = new List<int>();
            for (int i = 0; i < used.Length; i++)
            {
                slotOf[i] = used[i] ? skinBones.Count : 0;
                if (used[i]) skinBones.Add(i);
            }
            outSkinBones = skinBones.ToArray();

            var weights = new BoneWeight[pmxMesh.Weights.Length];
            for (int i = 0; i < weights.Length; i++)
            {
                var w = pmxMesh.Weights[i];
                weights[i] = new BoneWeight
                {
                    boneIndex0 = slotOf[w.boneIndex0], boneIndex1 = slotOf[w.boneIndex1],
                    boneIndex2 = slotOf[w.boneIndex2], boneIndex3 = slotOf[w.boneIndex3],
                    weight0 = w.weight0, weight1 = w.weight1, weight2 = w.weight2, weight3 = w.weight3
                };
            }

            var binds = new Matrix4x4[outSkinBones.Length];
            for (int i = 0; i < binds.Length; i++)
                binds[i] = Matrix4x4.Translate(-pmx.Bones[outSkinBones[i]].Pos);

            var mesh = new Mesh
            {
                name = meshName,
                hideFlags = HideFlags.DontUnloadUnusedAsset
            };
            mesh.indexFormat = pmxMesh.Pos.Length > 65000 ? IndexFormat.UInt32 : IndexFormat.UInt16;
            mesh.vertices = pmxMesh.Pos;
            mesh.normals = pmxMesh.Normals;
            var uv = new Vector2[pmxMesh.Uv.Length];
            for (int i = 0; i < pmxMesh.Uv.Length; i++)
                uv[i] = flipUv ? new Vector2(pmxMesh.Uv[i].x, 1f - pmxMesh.Uv[i].y) : pmxMesh.Uv[i];
            mesh.uv = uv;
            mesh.boneWeights = weights;
            mesh.bindposes = binds;
            mesh.subMeshCount = keptMaterialIdx.Length;

            for (int s = 0; s < keptMaterialIdx.Length; s++)
            {
                int mi = keptMaterialIdx[s];
                var tris = pmxMesh.MaterialTris != null && mi < pmxMesh.MaterialTris.Length ? pmxMesh.MaterialTris[mi] : null;
                if (tris == null || tris.Length == 0)
                {
                    mesh.SetTriangles(Array.Empty<int>(), s, false);
                    continue;
                }
                if (pmx.Mats[mi].DoubleSided && tris.Length >= 3)
                {
                    var both = new int[tris.Length * 2];
                    Array.Copy(tris, both, tris.Length);
                    for (int t = 0; t + 2 < tris.Length; t += 3)
                    {
                        both[tris.Length + t] = tris[t];
                        both[tris.Length + t + 1] = tris[t + 2];
                        both[tris.Length + t + 2] = tris[t + 1];
                    }
                    tris = both;
                    doubleSidedCount++;
                }
                mesh.SetTriangles(tris, s, false);
            }
            mesh.RecalculateTangents();
            mesh.RecalculateBounds();
            return mesh;
        }

        private static void MarkUsed(BoneWeight w, bool[] used)        {
            if (w.weight0 > 0f && w.boneIndex0 < used.Length) used[w.boneIndex0] = true;
            if (w.weight1 > 0f && w.boneIndex1 < used.Length) used[w.boneIndex1] = true;
            if (w.weight2 > 0f && w.boneIndex2 < used.Length) used[w.boneIndex2] = true;
            if (w.weight3 > 0f && w.boneIndex3 < used.Length) used[w.boneIndex3] = true;
        }

        private static bool IsAccessory(PmxParser.PmxModel pmx, int matIdx, string[] patterns)
            => AccessoryFilter.IsAccessory(pmx, matIdx, patterns);

        private static Texture2D LoadTexture(string relPath, Dictionary<string, byte[]> textureBytes, int maxTexSize)
        {
            var baseName = Path.GetFileName(relPath);
            if (!textureBytes.TryGetValue(relPath, out var bytes))
                textureBytes.TryGetValue(baseName, out bytes);
            if (bytes == null) return null;

            try
            {
                var tex = new Texture2D(2, 2, TextureFormat.RGBA32, true, false);
                if (!tex.LoadImage(bytes, true))
                {
                    UnityEngine.Object.Destroy(tex);
                    return null;
                }
                if ((tex.width > maxTexSize || tex.height > maxTexSize) && maxTexSize >= 128)
                    tex = Downscale(tex, maxTexSize);
                tex.name = baseName;
                tex.hideFlags = HideFlags.DontUnloadUnusedAsset;
                return tex;
            }
            catch (Exception e)
            {
                Plugin.Log.LogWarning($"[GF2] texture {baseName} failed: {e.Message}");
                return null;
            }
        }

        private static Texture2D Downscale(Texture2D src, int maxSize)
        {
            float k = Mathf.Min(1f, Mathf.Min(maxSize / (float)src.width, maxSize / (float)src.height));
            int w = Mathf.Max(1, Mathf.RoundToInt(src.width * k));
            int h = Mathf.Max(1, Mathf.RoundToInt(src.height * k));
            var rt = RenderTexture.GetTemporary(w, h, 0, RenderTextureFormat.ARGB32, RenderTextureReadWrite.sRGB);
            var prev = RenderTexture.active;
            try
            {
                Graphics.Blit(src, rt);
                var dst = new Texture2D(w, h, TextureFormat.RGBA32, true, false);
                RenderTexture.active = rt;
                dst.ReadPixels(new Rect(0, 0, w, h), 0, 0);
                dst.Apply(false, true);
                RenderTexture.active = prev;
                UnityEngine.Object.Destroy(src);
                return dst;
            }
            finally
            {
                RenderTexture.active = prev;
                RenderTexture.ReleaseTemporary(rt);
            }
        }
    }
#endif
}
