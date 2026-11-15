using System;
using System.Collections.Generic;
using System.IO;
using UnityEngine;

namespace GF2.Tests
{
    internal static class MeshSplitTest
    {
        private const string DefaultPackagesDir = "C:\SPT/BepInEx/plugins/kukki.GF2/GF2_Characters";
        private const string PinnedPackage = "克萊妲原皮.gfc";

        internal static int Run()
        {
            var fail = new List<string>();

            TestSyntheticCube(fail);
            TestBoneHierarchy(fail);
            TestEdgeCases(fail);
            TestSubstringCollisions(fail);
            TestRealKoledaModel(fail);

            if (fail.Count > 0)
            {
                Console.Error.WriteLine($"split FAIL ({fail.Count} errors):");
                foreach (var err in fail)
                    Console.Error.WriteLine("  " + err);
                return 1;
            }

            Console.WriteLine("split PASS: mesh splitting, normal preservation, UV integrity, and hierarchy traversal verified.");
            return 0;
        }

        private static void TestSyntheticCube(List<string> fail)
        {
            var model = CreateSyntheticCubeModel();
            int origTotalTris = 0;
            foreach (var tris in model.MaterialTris) origTotalTris += tris.Length / 3;

            var headBones = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "頭", "Head" };
            (PmxMesh body, PmxMesh head) result;
            try
            {
                result = MeshSplitter.SplitMeshByBones(model, headBones, threshold: 0.5f);
            }
            catch (Exception ex)
            {
                fail.Add($"Synthetic: SplitMeshByBones threw {ex.GetType().Name}: {ex.Message}");
                return;
            }

            var body = result.body;
            var head = result.head;

            if (body == null || head == null)
            {
                fail.Add("Synthetic: body or head PmxMesh is null");
                return;
            }

            // Verify triangle counts: each original triangle must be in exactly one split mesh
            if (body.TriangleCount != 10)
                fail.Add($"Synthetic: expected 10 body triangles, got {body.TriangleCount}");
            if (head.TriangleCount != 10)
                fail.Add($"Synthetic: expected 10 head triangles, got {head.TriangleCount}");
            if (body.TriangleCount + head.TriangleCount != origTotalTris)
                fail.Add($"Synthetic: triangle sum mismatch: body({body.TriangleCount}) + head({head.TriangleCount}) != orig({origTotalTris})");

            // Vertices 4, 5, 6, 7 are boundary vertices at y=1.0 shared by body and head
            // Check that they exist in both meshes and their Pos, Normal, UV, Weights match 100%
            int[] boundaryOrig = { 4, 5, 6, 7 };
            foreach (int origIdx in boundaryOrig)
            {
                int bodyIdx = Array.IndexOf(body.OriginalVertexIndices, origIdx);
                int headIdx = Array.IndexOf(head.OriginalVertexIndices, origIdx);

                if (bodyIdx < 0)
                {
                    fail.Add($"Synthetic: boundary vertex {origIdx} missing from body mesh");
                    continue;
                }
                if (headIdx < 0)
                {
                    fail.Add($"Synthetic: boundary vertex {origIdx} missing from head mesh");
                    continue;
                }

                // Verify exact position match
                var origP = model.Pos[origIdx];
                var bp = body.Pos[bodyIdx];
                var hp = head.Pos[headIdx];
                if (bp.x != origP.x || bp.y != origP.y || bp.z != origP.z ||
                    hp.x != origP.x || hp.y != origP.y || hp.z != origP.z)
                {
                    fail.Add($"Synthetic: boundary vertex {origIdx} position mismatch: orig={origP}, body={bp}, head={hp}");
                }

                // Verify exact normal match (boundary normal preservation)
                var origN = model.Normals[origIdx];
                var bn = body.Normals[bodyIdx];
                var hn = head.Normals[headIdx];
                if (bn.x != origN.x || bn.y != origN.y || bn.z != origN.z ||
                    hn.x != origN.x || hn.y != origN.y || hn.z != origN.z)
                {
                    fail.Add($"Synthetic: boundary vertex {origIdx} normal mismatch: orig={origN}, body={bn}, head={hn}");
                }

                // Verify exact UV match
                var origUv = model.Uv[origIdx];
                var bu = body.Uv[bodyIdx];
                var hu = head.Uv[headIdx];
                if (bu.x != origUv.x || bu.y != origUv.y || hu.x != origUv.x || hu.y != origUv.y)
                {
                    fail.Add($"Synthetic: boundary vertex {origIdx} UV mismatch: orig=({origUv.x},{origUv.y}), body=({bu.x},{bu.y}), head=({hu.x},{hu.y})");
                }

                // Verify weights match
                var origW = model.Weights[origIdx];
                var bw = body.Weights[bodyIdx];
                var hw = head.Weights[headIdx];
                if (bw.boneIndex0 != origW.boneIndex0 || bw.weight0 != origW.weight0 ||
                    hw.boneIndex0 != origW.boneIndex0 || hw.weight0 != origW.weight0)
                {
                    fail.Add($"Synthetic: boundary vertex {origIdx} weight mismatch");
                }
            }

            // Material sub-range verification
            if (body.MaterialTris.Length != model.Mats.Length)
                fail.Add($"Synthetic: body.MaterialTris length {body.MaterialTris.Length} != mats {model.Mats.Length}");
            if (head.MaterialTris.Length != model.Mats.Length)
                fail.Add($"Synthetic: head.MaterialTris length {head.MaterialTris.Length} != mats {model.Mats.Length}");

            // Mat 0 was body, Mat 1 was head
            if (body.MaterialTris[0].Length != 30) // 10 tris
                fail.Add($"Synthetic: body MaterialTris[0] expected 30 indices, got {body.MaterialTris[0].Length}");
            if (body.MaterialTris[1].Length != 0)
                fail.Add($"Synthetic: body MaterialTris[1] expected 0 indices, got {body.MaterialTris[1].Length}");
            if (head.MaterialTris[0].Length != 0)
                fail.Add($"Synthetic: head MaterialTris[0] expected 0 indices, got {head.MaterialTris[0].Length}");
            if (head.MaterialTris[1].Length != 30) // 10 tris
                fail.Add($"Synthetic: head MaterialTris[1] expected 30 indices, got {head.MaterialTris[1].Length}");
        }

        private static void TestBoneHierarchy(List<string> fail)
        {
            // Test that children of head bones (e.g. hair chains, eyes) are automatically recognized
            var model = CreateSyntheticCubeModel();
            // Add a hair bone whose parent is "頭" (index 1)
            var bones = new List<PmxParser.PmxBone>(model.Bones);
            int hairBoneIdx = bones.Count;
            bones.Add(new PmxParser.PmxBone
            {
                Index = hairBoneIdx,
                Name = "Hair_Strand_1",
                NameEn = "Hair1",
                Parent = 1, // Parent is "頭"
                Pos = new Vector3(0, 2.5f, 0)
            });
            model.Bones = bones.ToArray();

            // Add a hair vertex weighted 100% to hairBoneIdx
            var pos = new List<Vector3>(model.Pos) { new Vector3(0, 2.5f, 0) };
            var norm = new List<Vector3>(model.Normals) { new Vector3(0, 1, 0) };
            var uv = new List<Vector2>(model.Uv) { new Vector2(0.5f, 0.5f) };
            var weights = new List<BoneWeight>(model.Weights)
            {
                new BoneWeight { boneIndex0 = hairBoneIdx, weight0 = 1.0f }
            };
            int hairVertIdx = pos.Count - 1;

            // Add a triangle connecting top vertices and the hair vertex (material 1)
            var headTris = new List<int>(model.MaterialTris[1])
            {
                8, 9, hairVertIdx
            };
            model.Pos = pos.ToArray();
            model.Normals = norm.ToArray();
            model.Uv = uv.ToArray();
            model.Weights = weights.ToArray();
            model.MaterialTris[1] = headTris.ToArray();

            // Split with only "頭" specified (Hair_Strand_1 not explicitly listed)
            var headBones = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "頭" };
            var (body, head) = MeshSplitter.SplitMeshByBones(model, headBones, threshold: 0.5f, includeDescendants: true);

            // Hair triangle must belong to head
            if (head.TriangleCount != 11) // 10 + 1 hair tri
                fail.Add($"Hierarchy: expected 11 head triangles, got {head.TriangleCount}");
            if (Array.IndexOf(head.OriginalVertexIndices, hairVertIdx) < 0)
                fail.Add("Hierarchy: hair vertex missing from head mesh");
        }

        private static void TestEdgeCases(List<string> fail)
        {
            var model = CreateSyntheticCubeModel();
            int totalTris = 0;
            foreach (var t in model.MaterialTris) totalTris += t.Length / 3;

            // 1. All body: empty headBoneNames
            var emptyHead = new HashSet<string>();
            var (bodyAll, headNone) = MeshSplitter.SplitMeshByBones(model, emptyHead, threshold: 0.5f, includeDescendants: false);
            if (headNone.TriangleCount != 0)
                fail.Add($"EdgeCase: empty head expected 0 head triangles, got {headNone.TriangleCount}");
            if (bodyAll.TriangleCount != totalTris)
                fail.Add($"EdgeCase: empty head expected {totalTris} body triangles, got {bodyAll.TriangleCount}");

            // 2. All head: specify both "上半身" and "頭"
            var allHead = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "上半身", "頭" };
            var (bodyNone, headAll) = MeshSplitter.SplitMeshByBones(model, allHead, threshold: 0.5f, includeDescendants: false);
            if (bodyNone.TriangleCount != 0)
                fail.Add($"EdgeCase: all head expected 0 body triangles, got {bodyNone.TriangleCount}");
            if (headAll.TriangleCount != totalTris)
                fail.Add($"EdgeCase: all head expected {totalTris} head triangles, got {headAll.TriangleCount}");
        }

        private static void TestSubstringCollisions(List<string> fail)
        {
            // Substring collision regression test:
            // "Left Forearm" contains "ear" (for-ear-m), but must NOT be classified as head!
            // "左袖口" contains "口", but must NOT be classified as head!
            // "Left Wrist" or "Right Ankle" ("足首", "手首") must NOT be classified as head!
            if (MeshSplitter.IsHeadBoneName("Left Forearm", "Left Forearm"))
                fail.Add("Collision: 'Left Forearm' was incorrectly classified as head bone (substring 'ear')");
            if (MeshSplitter.IsHeadBoneName("forearm.L", "forearm.L"))
                fail.Add("Collision: 'forearm.L' was incorrectly classified as head bone");
            if (MeshSplitter.IsHeadBoneName("左袖口", "SleeveCuff_L"))
                fail.Add("Collision: '左袖口' was incorrectly classified as head bone (substring '口')");
            if (MeshSplitter.IsHeadBoneName("左手首", "Wrist_L"))
                fail.Add("Collision: '左手首' was incorrectly classified as head bone (substring '首')");
            if (MeshSplitter.IsHeadBoneName("左足首", "Ankle_L"))
                fail.Add("Collision: '左足首' was incorrectly classified as head bone (substring '首')");

            // Also test DetectHeadBones on a model containing forearm and sleeve cuff
            var bones = new PmxParser.PmxBone[]
            {
                new PmxParser.PmxBone { Index = 0, Name = "左腕", NameEn = "Left Arm" },
                new PmxParser.PmxBone { Index = 1, Name = "左ひじ", NameEn = "Left Forearm" },
                new PmxParser.PmxBone { Index = 2, Name = "左袖口", NameEn = "Sleeve_L" },
                new PmxParser.PmxBone { Index = 3, Name = "頭", NameEn = "Head" },
            };
            var testModel = new PmxParser.PmxModel { Bones = bones };
            var detected = MeshSplitter.DetectHeadBones(testModel);
            if (detected.Contains("左ひじ") || detected.Contains("Left Forearm"))
                fail.Add("Collision: DetectHeadBones included 'Left Forearm'");
            if (detected.Contains("左袖口") || detected.Contains("Sleeve_L"))
                fail.Add("Collision: DetectHeadBones included '左袖口'");
            if (!detected.Contains("頭") && !detected.Contains("Head"))
                fail.Add("Collision: DetectHeadBones failed to include '頭'");
        }

        private static void TestRealKoledaModel(List<string> fail)
        {
            var pkgPath = Path.Combine(DefaultPackagesDir, PinnedPackage);
            if (!File.Exists(pkgPath)) return; // Skip if package file not in test environment

            PmxParser.PmxModel koleda;
            try
            {
                using var fs = File.OpenRead(pkgPath);
                var data = PmxContainer.Read(fs);
                koleda = PmxParser.Parse(data.Pmx);
            }
            catch (Exception ex)
            {
                fail.Add($"RealKoleda: parse failed: {ex.Message}");
                return;
            }

            int koledaTotalTris = 0;
            foreach (var tr in koleda.MaterialTris) koledaTotalTris += tr.Length / 3;

            var headBones = MeshSplitter.DetectHeadBones(koleda);
            if (headBones.Count == 0)
            {
                fail.Add("RealKoleda: DetectHeadBones returned empty set");
                return;
            }

            var (body, head) = MeshSplitter.SplitMeshByBones(koleda, headBones, threshold: 0.5f);
            if (body.VertexCount == 0 || head.VertexCount == 0)
            {
                fail.Add($"RealKoleda: body({body.VertexCount}) or head({head.VertexCount}) has 0 vertices");
                return;
            }

            if (body.TriangleCount + head.TriangleCount != koledaTotalTris)
            {
                fail.Add($"RealKoleda: triangle sum mismatch: body({body.TriangleCount}) + head({head.TriangleCount}) != total({koledaTotalTris})");
            }

            // Verify index ranges in submeshes
            for (int m = 0; m < body.MaterialTris.Length; m++)
            {
                foreach (int idx in body.MaterialTris[m])
                    if (idx < 0 || idx >= body.Pos.Length)
                    {
                        fail.Add($"RealKoleda: body tri index {idx} out of range [0, {body.Pos.Length})");
                        break;
                    }
            }
            for (int m = 0; m < head.MaterialTris.Length; m++)
            {
                foreach (int idx in head.MaterialTris[m])
                    if (idx < 0 || idx >= head.Pos.Length)
                    {
                        fail.Add($"RealKoleda: head tri index {idx} out of range [0, {head.Pos.Length})");
                        break;
                    }
            }

            // Verify boundary vertices: vertices appearing in both body and head
            var bodyOrigSet = new Dictionary<int, int>();
            for (int i = 0; i < body.OriginalVertexIndices.Length; i++)
                bodyOrigSet[body.OriginalVertexIndices[i]] = i;

            int boundaryCount = 0;
            for (int hIdx = 0; hIdx < head.OriginalVertexIndices.Length; hIdx++)
            {
                int origIdx = head.OriginalVertexIndices[hIdx];
                if (bodyOrigSet.TryGetValue(origIdx, out int bIdx))
                {
                    boundaryCount++;
                    var bp = body.Pos[bIdx];
                    var hp = head.Pos[hIdx];
                    var bn = body.Normals[bIdx];
                    var hn = head.Normals[hIdx];
                    var bu = body.Uv[bIdx];
                    var hu = head.Uv[hIdx];

                    if (bp.x != hp.x || bp.y != hp.y || bp.z != hp.z)
                    {
                        fail.Add($"RealKoleda: boundary vertex {origIdx} position mismatch");
                        break;
                    }
                    if (bn.x != hn.x || bn.y != hn.y || bn.z != hn.z)
                    {
                        fail.Add($"RealKoleda: boundary vertex {origIdx} normal mismatch");
                        break;
                    }
                    if (bu.x != hu.x || bu.y != hu.y)
                    {
                        fail.Add($"RealKoleda: boundary vertex {origIdx} UV mismatch");
                        break;
                    }
                }
            }

            if (boundaryCount == 0)
                fail.Add("RealKoleda: expected boundary vertices shared at neck, but found 0");

            Console.WriteLine($"   RealKoleda split: body(v={body.VertexCount}, tris={body.TriangleCount}) head(v={head.VertexCount}, tris={head.TriangleCount}) boundary={boundaryCount}");
        }

        private static PmxParser.PmxModel CreateSyntheticCubeModel()
        {
            // Construct a two-segment tower:
            // Bottom (y=0): verts 0..3 (Bone 0: 上半身, w=1.0)
            // Middle (y=1): verts 4..7 (Bone 0: 上半身 w=0.5, Bone 1: 頭 w=0.5)
            // Top    (y=2): verts 8..11 (Bone 1: 頭, w=1.0)
            var pos = new Vector3[12];
            var norm = new Vector3[12];
            var uv = new Vector2[12];
            var weights = new BoneWeight[12];

            // Bottom square y=0
            pos[0] = new Vector3(-1, 0, -1); norm[0] = new Vector3(-0.707f, -0.707f, 0); uv[0] = new Vector2(0f, 0f); weights[0] = new BoneWeight { boneIndex0 = 0, weight0 = 1.0f };
            pos[1] = new Vector3( 1, 0, -1); norm[1] = new Vector3( 0.707f, -0.707f, 0); uv[1] = new Vector2(1f, 0f); weights[1] = new BoneWeight { boneIndex0 = 0, weight0 = 1.0f };
            pos[2] = new Vector3( 1, 0,  1); norm[2] = new Vector3( 0.707f, -0.707f, 0); uv[2] = new Vector2(1f, 0.2f); weights[2] = new BoneWeight { boneIndex0 = 0, weight0 = 1.0f };
            pos[3] = new Vector3(-1, 0,  1); norm[3] = new Vector3(-0.707f, -0.707f, 0); uv[3] = new Vector2(0f, 0.2f); weights[3] = new BoneWeight { boneIndex0 = 0, weight0 = 1.0f };

            // Middle square y=1 (boundary / neck)
            pos[4] = new Vector3(-1, 1, -1); norm[4] = new Vector3(-0.707f, 0, -0.707f); uv[4] = new Vector2(0f, 0.5f); weights[4] = new BoneWeight { boneIndex0 = 0, weight0 = 0.5f, boneIndex1 = 1, weight1 = 0.5f };
            pos[5] = new Vector3( 1, 1, -1); norm[5] = new Vector3( 0.707f, 0, -0.707f); uv[5] = new Vector2(1f, 0.5f); weights[5] = new BoneWeight { boneIndex0 = 0, weight0 = 0.5f, boneIndex1 = 1, weight1 = 0.5f };
            pos[6] = new Vector3( 1, 1,  1); norm[6] = new Vector3( 0.707f, 0,  0.707f); uv[6] = new Vector2(1f, 0.7f); weights[6] = new BoneWeight { boneIndex0 = 0, weight0 = 0.5f, boneIndex1 = 1, weight1 = 0.5f };
            pos[7] = new Vector3(-1, 1,  1); norm[7] = new Vector3(-0.707f, 0,  0.707f); uv[7] = new Vector2(0f, 0.7f); weights[7] = new BoneWeight { boneIndex0 = 0, weight0 = 0.5f, boneIndex1 = 1, weight1 = 0.5f };

            // Top square y=2
            pos[8]  = new Vector3(-1, 2, -1); norm[8]  = new Vector3(-0.707f, 0.707f, 0); uv[8]  = new Vector2(0f, 0.8f); weights[8]  = new BoneWeight { boneIndex0 = 1, weight0 = 1.0f };
            pos[9]  = new Vector3( 1, 2, -1); norm[9]  = new Vector3( 0.707f, 0.707f, 0); uv[9]  = new Vector2(1f, 0.8f); weights[9]  = new BoneWeight { boneIndex0 = 1, weight0 = 1.0f };
            pos[10] = new Vector3( 1, 2,  1); norm[10] = new Vector3( 0.707f, 0.707f, 0); uv[10] = new Vector2(1f, 1.0f); weights[10] = new BoneWeight { boneIndex0 = 1, weight0 = 1.0f };
            pos[11] = new Vector3(-1, 2,  1); norm[11] = new Vector3(-0.707f, 0.707f, 0); uv[11] = new Vector2(0f, 1.0f); weights[11] = new BoneWeight { boneIndex0 = 1, weight0 = 1.0f };

            // Material 0: Body triangles (lower segment sides + bottom) = 10 triangles = 30 indices
            var bodyTris = new int[]
            {
                // bottom
                0, 2, 1,  0, 3, 2,
                // side 1
                0, 1, 5,  0, 5, 4,
                // side 2
                1, 2, 6,  1, 6, 5,
                // side 3
                2, 3, 7,  2, 7, 6,
                // side 4
                3, 0, 4,  3, 4, 7
            };

            // Material 1: Head triangles (upper segment sides + top) = 10 triangles = 30 indices
            var headTris = new int[]
            {
                // top
                8, 9, 10,  8, 10, 11,
                // side 1
                4, 5, 9,   4, 9, 8,
                // side 2
                5, 6, 10,  5, 10, 9,
                // side 3
                6, 7, 11,  6, 11, 10,
                // side 4
                7, 4, 8,   7, 8, 11
            };

            var bones = new PmxParser.PmxBone[]
            {
                new PmxParser.PmxBone { Index = 0, Name = "上半身", NameEn = "Spine", Parent = -1, Pos = new Vector3(0, 0.5f, 0) },
                new PmxParser.PmxBone { Index = 1, Name = "頭", NameEn = "Head", Parent = 0, Pos = new Vector3(0, 1.5f, 0) }
            };

            var mats = new PmxParser.PmxMat[]
            {
                new PmxParser.PmxMat { Name = "BodyMat", FaceCount = bodyTris.Length },
                new PmxParser.PmxMat { Name = "HeadMat", FaceCount = headTris.Length }
            };

            return new PmxParser.PmxModel
            {
                Name = "TestCube",
                Pos = pos,
                Normals = norm,
                Uv = uv,
                Weights = weights,
                Bones = bones,
                Mats = mats,
                MaterialTris = new int[][] { bodyTris, headTris },
                BoundsMin = new Vector3(-1, 0, -1),
                BoundsMax = new Vector3(1, 2, 1)
            };
        }
    }
}
