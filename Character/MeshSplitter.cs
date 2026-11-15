using System;
using System.Collections.Generic;
using UnityEngine;

namespace DollFrame
{
    public sealed class PmxMesh
    {
        public string Name;
        public Vector3[] Pos;
        public Vector3[] Normals;
        public Vector2[] Uv;
        public BoneWeight[] Weights;
        public int[][] MaterialTris;
        public Vector3 BoundsMin;
        public Vector3 BoundsMax;
        public int[] OriginalVertexIndices;

        public int VertexCount => Pos != null ? Pos.Length : 0;
        public int TriangleCount
        {
            get
            {
                if (MaterialTris == null) return 0;
                int sum = 0;
                for (int i = 0; i < MaterialTris.Length; i++)
                    if (MaterialTris[i] != null) sum += MaterialTris[i].Length / 3;
                return sum;
            }
        }
    }

    public static class MeshSplitter
    {
        private static readonly string[] HeadKeywordsJp =
        {
            "頭", "首", "目", "耳", "口", "鼻", "眉", "舌", "歯", "頬", "顎", "髪", "毛"
        };

        private static readonly string[] HeadKeywordsEn =
        {
            "head", "neck", "hair", "face", "eye", "ear", "nose", "mouth", "jaw", "chin", "tongue", "teeth", "tooth"
        };

        public static HashSet<string> DetectHeadBones(PmxParser.PmxModel model)
        {
            var result = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (model == null || model.Bones == null) return result;

            for (int i = 0; i < model.Bones.Length; i++)
            {
                var b = model.Bones[i];
                if (IsHeadBoneName(b.Name, b.NameEn))
                {
                    result.Add(b.Name);
                    if (!string.IsNullOrEmpty(b.NameEn))
                        result.Add(b.NameEn);
                }
            }

            return result;
        }

        public static bool IsHeadBoneName(string name, string nameEn)
        {
            if (string.IsNullOrEmpty(name)) name = "";
            if (string.IsNullOrEmpty(nameEn)) nameEn = "";

            // Exclude ankle, wrist and sleeve cuff from matching "首" and "口"
            if (name.Contains("足首") || name.Contains("手首") || name.Contains("袖口"))
                return false;

            var lowerEn = nameEn.ToLowerInvariant();
            var lowerName = name.ToLowerInvariant();
            if (lowerEn.Contains("forearm") || lowerEn.Contains("arm") || lowerEn.Contains("wrist") || lowerEn.Contains("elbow") || lowerEn.Contains("hand") ||
                lowerName.Contains("forearm") || lowerName.Contains("arm") || lowerName.Contains("wrist") || lowerName.Contains("elbow") || lowerName.Contains("hand"))
                return false;

            for (int k = 0; k < HeadKeywordsJp.Length; k++)
                if (name.Contains(HeadKeywordsJp[k])) return true;

            for (int k = 0; k < HeadKeywordsEn.Length; k++)
                if (lowerEn.Contains(HeadKeywordsEn[k]) || lowerName.Contains(HeadKeywordsEn[k])) return true;

            return false;
        }

        public static HashSet<int> ResolveHeadBoneIndices(PmxParser.PmxModel model, HashSet<string> headBoneNames, bool includeDescendants = true)
        {
            var indices = new HashSet<int>();
            if (model == null || model.Bones == null) return indices;

            if (headBoneNames == null)
                headBoneNames = DetectHeadBones(model);

            for (int i = 0; i < model.Bones.Length; i++)
            {
                var b = model.Bones[i];
                if (headBoneNames.Contains(b.Name) || (!string.IsNullOrEmpty(b.NameEn) && headBoneNames.Contains(b.NameEn)))
                {
                    indices.Add(i);
                }
            }

            if (includeDescendants)
            {
                bool added;
                do
                {
                    added = false;
                    for (int i = 0; i < model.Bones.Length; i++)
                    {
                        if (indices.Contains(i)) continue;
                        int parent = model.Bones[i].Parent;
                        if (parent >= 0 && indices.Contains(parent))
                        {
                            indices.Add(i);
                            added = true;
                        }
                    }
                } while (added);
            }

            return indices;
        }

        public static float GetVertexHeadWeight(in BoneWeight bw, HashSet<int> headBoneIndices)
        {
            float w = 0f;
            if (bw.weight0 > 0f && headBoneIndices.Contains(bw.boneIndex0)) w += bw.weight0;
            if (bw.weight1 > 0f && headBoneIndices.Contains(bw.boneIndex1)) w += bw.weight1;
            if (bw.weight2 > 0f && headBoneIndices.Contains(bw.boneIndex2)) w += bw.weight2;
            if (bw.weight3 > 0f && headBoneIndices.Contains(bw.boneIndex3)) w += bw.weight3;
            return w;
        }

        public static (PmxMesh body, PmxMesh head) SplitMeshByBones(PmxParser.PmxModel model, HashSet<string> headBoneNames, float threshold = 0.5f, bool includeDescendants = true)
        {
            if (model == null) throw new ArgumentNullException(nameof(model));

            var headBoneIndices = ResolveHeadBoneIndices(model, headBoneNames, includeDescendants);

            int numMats = model.Mats != null ? model.Mats.Length : 0;
            int numVerts = model.Pos != null ? model.Pos.Length : 0;

            var origToBody = new int[numVerts];
            var origToHead = new int[numVerts];
            for (int i = 0; i < numVerts; i++)
            {
                origToBody[i] = -1;
                origToHead[i] = -1;
            }

            var bodyPos = new List<Vector3>();
            var bodyNormals = new List<Vector3>();
            var bodyUv = new List<Vector2>();
            var bodyWeights = new List<BoneWeight>();
            var bodyOrigIndices = new List<int>();

            var headPos = new List<Vector3>();
            var headNormals = new List<Vector3>();
            var headUv = new List<Vector2>();
            var headWeights = new List<BoneWeight>();
            var headOrigIndices = new List<int>();

            var bodyMaterialTris = new int[numMats][];
            var headMaterialTris = new int[numMats][];

            for (int m = 0; m < numMats; m++)
            {
                var tris = model.MaterialTris != null && m < model.MaterialTris.Length ? model.MaterialTris[m] : null;
                if (tris == null || tris.Length == 0)
                {
                    bodyMaterialTris[m] = Array.Empty<int>();
                    headMaterialTris[m] = Array.Empty<int>();
                    continue;
                }

                var bTris = new List<int>(tris.Length);
                var hTris = new List<int>(tris.Length);

                for (int t = 0; t + 2 < tris.Length; t += 3)
                {
                    int i0 = tris[t];
                    int i1 = tris[t + 1];
                    int i2 = tris[t + 2];

                    float w0 = i0 >= 0 && i0 < numVerts ? GetVertexHeadWeight(model.Weights[i0], headBoneIndices) : 0f;
                    float w1 = i1 >= 0 && i1 < numVerts ? GetVertexHeadWeight(model.Weights[i1], headBoneIndices) : 0f;
                    float w2 = i2 >= 0 && i2 < numVerts ? GetVertexHeadWeight(model.Weights[i2], headBoneIndices) : 0f;

                    float triHeadWeight = (w0 + w1 + w2) / 3.0f;
                    bool isHead = triHeadWeight >= threshold;

                    if (isHead)
                    {
                        hTris.Add(GetOrCreateHeadVertex(i0, model, origToHead, headPos, headNormals, headUv, headWeights, headOrigIndices));
                        hTris.Add(GetOrCreateHeadVertex(i1, model, origToHead, headPos, headNormals, headUv, headWeights, headOrigIndices));
                        hTris.Add(GetOrCreateHeadVertex(i2, model, origToHead, headPos, headNormals, headUv, headWeights, headOrigIndices));
                    }
                    else
                    {
                        bTris.Add(GetOrCreateBodyVertex(i0, model, origToBody, bodyPos, bodyNormals, bodyUv, bodyWeights, bodyOrigIndices));
                        bTris.Add(GetOrCreateBodyVertex(i1, model, origToBody, bodyPos, bodyNormals, bodyUv, bodyWeights, bodyOrigIndices));
                        bTris.Add(GetOrCreateBodyVertex(i2, model, origToBody, bodyPos, bodyNormals, bodyUv, bodyWeights, bodyOrigIndices));
                    }
                }

                bodyMaterialTris[m] = bTris.ToArray();
                headMaterialTris[m] = hTris.ToArray();
            }

            ComputeBounds(bodyPos, out var bMin, out var bMax);
            ComputeBounds(headPos, out var hMin, out var hMax);

            var bodyMesh = new PmxMesh
            {
                Name = (model.Name ?? "Model") + "_Body",
                Pos = bodyPos.ToArray(),
                Normals = bodyNormals.ToArray(),
                Uv = bodyUv.ToArray(),
                Weights = bodyWeights.ToArray(),
                MaterialTris = bodyMaterialTris,
                BoundsMin = bMin,
                BoundsMax = bMax,
                OriginalVertexIndices = bodyOrigIndices.ToArray()
            };

            var headMesh = new PmxMesh
            {
                Name = (model.Name ?? "Model") + "_Head",
                Pos = headPos.ToArray(),
                Normals = headNormals.ToArray(),
                Uv = headUv.ToArray(),
                Weights = headWeights.ToArray(),
                MaterialTris = headMaterialTris,
                BoundsMin = hMin,
                BoundsMax = hMax,
                OriginalVertexIndices = headOrigIndices.ToArray()
            };

            return (bodyMesh, headMesh);
        }

        private static int GetOrCreateBodyVertex(int origIdx, PmxParser.PmxModel model, int[] origToBody,
            List<Vector3> bodyPos, List<Vector3> bodyNormals, List<Vector2> bodyUv,
            List<BoneWeight> bodyWeights, List<int> bodyOrigIndices)
        {
            if (origIdx < 0 || origIdx >= model.Pos.Length) return 0;
            int idx = origToBody[origIdx];
            if (idx >= 0) return idx;

            idx = bodyPos.Count;
            origToBody[origIdx] = idx;
            bodyPos.Add(model.Pos[origIdx]);
            bodyNormals.Add(model.Normals[origIdx]);
            bodyUv.Add(model.Uv[origIdx]);
            bodyWeights.Add(model.Weights[origIdx]);
            bodyOrigIndices.Add(origIdx);
            return idx;
        }

        private static int GetOrCreateHeadVertex(int origIdx, PmxParser.PmxModel model, int[] origToHead,
            List<Vector3> headPos, List<Vector3> headNormals, List<Vector2> headUv,
            List<BoneWeight> headWeights, List<int> headOrigIndices)
        {
            if (origIdx < 0 || origIdx >= model.Pos.Length) return 0;
            int idx = origToHead[origIdx];
            if (idx >= 0) return idx;

            idx = headPos.Count;
            origToHead[origIdx] = idx;
            headPos.Add(model.Pos[origIdx]);
            headNormals.Add(model.Normals[origIdx]);
            headUv.Add(model.Uv[origIdx]);
            headWeights.Add(model.Weights[origIdx]);
            headOrigIndices.Add(origIdx);
            return idx;
        }

        private static void ComputeBounds(List<Vector3> pos, out Vector3 min, out Vector3 max)
        {
            if (pos == null || pos.Count == 0)
            {
                min = new Vector3(0, 0, 0);
                max = new Vector3(0, 0, 0);
                return;
            }
            float minX = float.MaxValue, minY = float.MaxValue, minZ = float.MaxValue;
            float maxX = float.MinValue, maxY = float.MinValue, maxZ = float.MinValue;
            for (int i = 0; i < pos.Count; i++)
            {
                var p = pos[i];
                if (p.x < minX) minX = p.x;
                if (p.y < minY) minY = p.y;
                if (p.z < minZ) minZ = p.z;
                if (p.x > maxX) maxX = p.x;
                if (p.y > maxY) maxY = p.y;
                if (p.z > maxZ) maxZ = p.z;
            }
            min = new Vector3(minX, minY, minZ);
            max = new Vector3(maxX, maxY, maxZ);
        }
    }
}
