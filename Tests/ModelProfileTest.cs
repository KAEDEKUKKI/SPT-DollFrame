using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text.RegularExpressions;
using UnityEngine;

namespace GF2.Tests
{
    internal static class ModelProfileTest
    {
        private const string DefaultPackagesDir = "C:\SPT/BepInEx/plugins/kukki.GF2/GF2_Characters";
        private const string PinnedPackage = "克萊妲原皮.gfc";

        internal static int Run()
        {
            var fail = new List<string>();

            // 1. Load Koleda model (either from gfc or koleda_bones.json)
            PmxParser.PmxModel model = LoadKoledaModel();
            if (model == null || model.Bones == null || model.Bones.Length == 0)
            {
                Console.Error.WriteLine("profile FAIL: failed to load Koleda PMX model");
                return 1;
            }

            // 2. Measure baseline
            ModelProfile profile;
            try
            {
                profile = ModelMetrics.MeasureBaseline(model);
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"profile FAIL: MeasureBaseline threw exception: {ex.Message}");
                return 1;
            }

            if (profile == null)
            {
                Console.Error.WriteLine("profile FAIL: MeasureBaseline returned null");
                return 1;
            }

            // 3. Verify anatomical dimensions for Koleda (PMX unit scale 0.078)
            // Eye span: (左目y + 右目y)/2 - 左足首先y = 18.2533u * 0.078 = 1.4236m（原始幾何）
            if (Math.Abs(profile.EyeHeight - 1.4236f) > 0.03f)
                fail.Add($"EyeHeight expected ~1.4236m (原始), actual {profile.EyeHeight:F4}m");

            // Arm reach: (uarm 2.72 + farm 2.94) * 0.078 = 0.4422m（原始幾何）
            if (Math.Abs(profile.ArmReach - 0.4422f) > 0.03f)
                fail.Add($"ArmReach expected ~0.4422m (原始), actual {profile.ArmReach:F4}m");

            // Shoulder width: dist(左腕, 右腕) = 克萊妲原始幾何 0.2072m
            if (Math.Abs(profile.ShoulderWidth - 0.2072f) > 0.03f)
                fail.Add($"ShoulderWidth expected ~0.2072m (原始), actual {profile.ShoulderWidth:F4}m");

            // 4. Verify capsule colliders
            if (profile.Colliders == null || profile.Colliders.Length < 10)
            {
                fail.Add($"Colliders count expected >= 10, actual {(profile.Colliders?.Length ?? 0)}");
            }
            else
            {
                var colBones = new HashSet<string>();
                foreach (var c in profile.Colliders)
                {
                    colBones.Add(c.BoneName);
                    if (c.Radius <= 0f || c.Height <= 0f)
                        fail.Add($"Collider {c.BoneName} invalid radius={c.Radius} height={c.Height}");
                }

                string[] reqBones = { "頭", "左腕", "右腕", "左ひじ", "右ひじ", "左足", "右足", "左ひざ", "右ひざ" };
                foreach (var b in reqBones)
                {
                    if (!colBones.Contains(b))
                        fail.Add($"Colliders missing expected body bone: {b}");
                }

                var headCol = Array.Find(profile.Colliders, c => c.BoneName == "頭");
                if (headCol != null && Math.Abs(headCol.Radius - 0.02f) > 1e-4f)
                    fail.Add($"Head collider radius expected 0.02m, actual {headCol.Radius:F4}m");
            }

            // 5. Verify spring chains
            if (profile.Springs == null || profile.Springs.Length < 12)
            {
                fail.Add($"Springs count expected >= 12, actual {(profile.Springs?.Length ?? 0)}");
            }
            else
            {
                int hairChains = 0;
                foreach (var s in profile.Springs)
                {
                    if (s.Category == "Hair" || s.RootBone.StartsWith("Hair", StringComparison.OrdinalIgnoreCase))
                        hairChains++;
                    if (s.Stiffness <= 0f || s.Drag <= 0f)
                        fail.Add($"Spring {s.RootBone} invalid stiffness={s.Stiffness} drag={s.Drag}");
                }
                if (hairChains < 12)
                    fail.Add($"Expected at least 12 Hair spring chains, actual {hairChains}");
            }

            // 6. Test JSON serialization and deserialization roundtrip
            string json;
            try
            {
                json = profile.ToJson(pretty: true);
            }
            catch (Exception ex)
            {
                fail.Add($"ToJson threw exception: {ex.Message}");
                json = null;
            }

            if (!string.IsNullOrEmpty(json))
            {
                if (!json.Contains("\"EyeHeight\"") || !json.Contains("\"ArmReach\"") ||
                    !json.Contains("\"ShoulderWidth\"") || !json.Contains("\"Colliders\"") ||
                    !json.Contains("\"Springs\""))
                {
                    fail.Add("JSON output missing core fields");
                }

                ModelProfile roundtrip = null;
                try
                {
                    roundtrip = ModelProfile.FromJson(json);
                }
                catch (Exception ex)
                {
                    fail.Add($"FromJson threw exception: {ex.Message}");
                }

                if (roundtrip != null)
                {
                    if (Math.Abs(roundtrip.EyeHeight - profile.EyeHeight) > 1e-4f)
                        fail.Add($"Roundtrip EyeHeight mismatch: {roundtrip.EyeHeight} vs {profile.EyeHeight}");
                    if (Math.Abs(roundtrip.ArmReach - profile.ArmReach) > 1e-4f)
                        fail.Add($"Roundtrip ArmReach mismatch: {roundtrip.ArmReach} vs {profile.ArmReach}");
                    if (Math.Abs(roundtrip.ShoulderWidth - profile.ShoulderWidth) > 1e-4f)
                        fail.Add($"Roundtrip ShoulderWidth mismatch: {roundtrip.ShoulderWidth} vs {profile.ShoulderWidth}");
                    if (roundtrip.Colliders?.Length != profile.Colliders?.Length)
                        fail.Add($"Roundtrip Colliders count mismatch: {roundtrip.Colliders?.Length} vs {profile.Colliders?.Length}");
                    if (roundtrip.Springs?.Length != profile.Springs?.Length)
                        fail.Add($"Roundtrip Springs count mismatch: {roundtrip.Springs?.Length} vs {profile.Springs?.Length}");
                    else if (roundtrip.Springs != null)
                    {
                        for (int i = 0; i < roundtrip.Springs.Length; i++)
                        {
                            var s1 = profile.Springs[i];
                            var s2 = roundtrip.Springs[i];
                            if (s1.Gravity != null && s2.Gravity != null)
                            {
                                if (Math.Abs(s1.Gravity[0] - s2.Gravity[0]) > 1e-4f ||
                                    Math.Abs(s1.Gravity[1] - s2.Gravity[1]) > 1e-4f ||
                                    Math.Abs(s1.Gravity[2] - s2.Gravity[2]) > 1e-4f)
                                {
                                    fail.Add($"Spring {s1.RootBone} Gravity mismatch: [{s2.Gravity[0]},{s2.Gravity[1]},{s2.Gravity[2]}] vs [{s1.Gravity[0]},{s1.Gravity[1]},{s1.Gravity[2]}]");
                                }
                            }
                        }
                    }
                }
            }

            // 7. Verify eye bone detection variants (English names, 両目, Head anatomical fallback)
            TestEyeBoneVariants(fail);

            // 8. Verify secondary physics math invariants (zero-stretch, capsule push-out, cone clamp, teleport reset)
            TestSpringPhysicsMath(fail);

            if (fail.Count > 0)
            {
                foreach (var msg in fail)
                    Console.Error.WriteLine($"profile FAIL: {msg}");
                return 1;
            }

            Console.WriteLine($"profile: OK (EyeHeight={profile.EyeHeight:F3}m ArmReach={profile.ArmReach:F3}m ShoulderWidth={profile.ShoulderWidth:F3}m, {profile.Colliders.Length} colliders, {profile.Springs.Length} springs, JSON roundtrip OK)");
            return 0;
        }

        private static void TestEyeBoneVariants(List<string> fail)
        {
            // Case A: Model with English eye bones (Eye_L, Eye_R)
            var bonesA = new[]
            {
                new PmxParser.PmxBone { Index = 0, Name = "Toe_L", Pos = new Vector3(0, 0, 0) },
                new PmxParser.PmxBone { Index = 1, Name = "Head", Pos = new Vector3(0, 18, 0) },
                new PmxParser.PmxBone { Index = 2, Name = "Eye_L", Pos = new Vector3(0.3f, 19f, 0) },
                new PmxParser.PmxBone { Index = 3, Name = "Eye_R", Pos = new Vector3(-0.3f, 19f, 0) }
            };
            var modelA = new PmxParser.PmxModel { Name = "TestEng", Bones = bonesA };
            var profA = ModelMetrics.MeasureBaseline(modelA);
            float expectedEyeA = 19f * 0.078f;
            if (Math.Abs(profA.EyeHeight - expectedEyeA) > 1e-4f)
                fail.Add($"EyeHeight for English bones expected {expectedEyeA}, got {profA.EyeHeight}");

            // Case B: Model with Eyes (両目 fallback)
            var bonesB = new[]
            {
                new PmxParser.PmxBone { Index = 0, Name = "左足首先", Pos = new Vector3(0, 0, 0) },
                new PmxParser.PmxBone { Index = 1, Name = "頭", Pos = new Vector3(0, 18, 0) },
                new PmxParser.PmxBone { Index = 2, Name = "両目", Pos = new Vector3(0, 19.5f, 0) }
            };
            var modelB = new PmxParser.PmxModel { Name = "TestBoth", Bones = bonesB };
            var profB = ModelMetrics.MeasureBaseline(modelB);
            float expectedEyeB = 19.5f * 0.078f;
            if (Math.Abs(profB.EyeHeight - expectedEyeB) > 1e-4f)
                fail.Add($"EyeHeight for 両目 fallback expected {expectedEyeB}, got {profB.EyeHeight}");

            // Case C: Model with only Head fallback
            var bonesC = new[]
            {
                new PmxParser.PmxBone { Index = 0, Name = "左足首先", Pos = new Vector3(0, 0, 0) },
                new PmxParser.PmxBone { Index = 1, Name = "頭", Pos = new Vector3(0, 18, 0) }
            };
            var modelC = new PmxParser.PmxModel { Name = "TestHeadOnly", Bones = bonesC };
            var profC = ModelMetrics.MeasureBaseline(modelC);
            float expectedEyeC = 18f * 0.078f + 0.10f;
            if (Math.Abs(profC.EyeHeight - expectedEyeC) > 1e-4f)
                fail.Add($"EyeHeight for Head only expected {expectedEyeC}, got {profC.EyeHeight}");
        }

        private static PmxParser.PmxModel LoadKoledaModel()
        {
            // 1. Try real package first
            var pkgDir = Environment.GetEnvironmentVariable("GF2_PACKAGES_DIR");
            if (string.IsNullOrEmpty(pkgDir)) pkgDir = DefaultPackagesDir;
            var pkgPath = Path.Combine(pkgDir, PinnedPackage);
            if (File.Exists(pkgPath))
            {
                try
                {
                    using var fs = File.OpenRead(pkgPath);
                    var data = PmxContainer.Read(fs);
                    return PmxParser.Parse(data.Pmx);
                }
                catch { }
            }

            // 2. Fallback to analysis/koleda_bones.json
            var bonesJsonPath = RepoFile(Path.Combine("analysis", "koleda_bones.json"));
            if (File.Exists(bonesJsonPath))
            {
                try
                {
                    var text = File.ReadAllText(bonesJsonPath);
                    var rx = new Regex(@"""i""\s*:\s*(\d+)\s*,\s*""n""\s*:\s*""([^""]*)""[\s\S]*?""p""\s*:\s*(-?\d+)\s*,\s*""pos""\s*:\s*\[\s*([-+0-9.eE]+)\s*,\s*([-+0-9.eE]+)\s*,\s*([-+0-9.eE]+)\s*\]");
                    var boneList = new List<PmxParser.PmxBone>();
                    foreach (Match m in rx.Matches(text))
                    {
                        var b = new PmxParser.PmxBone
                        {
                            Index = int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture),
                            Name = m.Groups[2].Value,
                            Parent = int.Parse(m.Groups[3].Value, CultureInfo.InvariantCulture),
                            Pos = new Vector3(
                                float.Parse(m.Groups[4].Value, CultureInfo.InvariantCulture),
                                float.Parse(m.Groups[5].Value, CultureInfo.InvariantCulture),
                                float.Parse(m.Groups[6].Value, CultureInfo.InvariantCulture)
                            )
                        };
                        boneList.Add(b);
                    }
                    if (boneList.Count > 0)
                    {
                        return new PmxParser.PmxModel
                        {
                            Name = "Koleda",
                            Bones = boneList.ToArray(),
                            Pos = Array.Empty<Vector3>(),
                            Weights = Array.Empty<BoneWeight>(),
                            Mats = Array.Empty<PmxParser.PmxMat>(),
                            BoundsMin = new Vector3(-1f, 0f, -1f),
                            BoundsMax = new Vector3(1f, 20f, 1f)
                        };
                    }
                }
                catch { }
            }

            return null;
        }

        private static void TestSpringPhysicsMath(List<string> fail)
        {
            var sp = SpringParams.ForCategory(SpringCategory.Hair);
            var state = default(SpringNodeState);
            var pivot = new System.Numerics.Vector3(0f, 1.5f, 0f);
            var parentRot = System.Numerics.Quaternion.Identity;
            var restDirLocal = new System.Numerics.Vector3(0f, -1f, 0f);
            float restLen = 0.25f;

            // 1. Initialize & rest frame
            var q0 = SpringPhysicsMath.StepNode(ref state, pivot, parentRot, System.Numerics.Quaternion.Identity, restDirLocal, restLen, 1f / 60f, in sp, null, 0);
            if (!state.Initialized) fail.Add("SpringPhysicsMath: state did not initialize");
            if (MathF.Abs((state.TipPos - pivot).Length() - restLen) > 1e-4f)
                fail.Add("SpringPhysicsMath: initial tip length mismatch");

            // 2. Sudden horizontal motion -> inertial lag + strict zero-stretch
            for (int f = 0; f < 10; f++)
            {
                pivot.X += 0.04f;
                SpringPhysicsMath.StepNode(ref state, pivot, parentRot, System.Numerics.Quaternion.Identity, restDirLocal, restLen, 1f / 60f, in sp, null, 0);
                float len = (state.TipPos - pivot).Length();
                if (MathF.Abs(len - restLen) > 1e-4f)
                {
                    fail.Add($"SpringPhysicsMath: zero-stretch violated at frame {f}: {len:F6} vs {restLen:F6}");
                    break;
                }
            }
            if (state.TipPos.X >= pivot.X)
                fail.Add($"SpringPhysicsMath: expected hair tip to lag behind +X motion, tip.X={state.TipPos.X:F4}, pivot.X={pivot.X:F4}");

            // 3. Capsule push-out + cone clamp + zero-stretch preserved
            var caps = new[] { new BodyCapsule(new System.Numerics.Vector3(pivot.X, 1.25f, 0f), new System.Numerics.Vector3(pivot.X, 1.6f, 0f), 0.14f) };
            for (int f = 0; f < 15; f++)
            {
                SpringPhysicsMath.StepNode(ref state, pivot, parentRot, System.Numerics.Quaternion.Identity, restDirLocal, restLen, 1f / 60f, in sp, caps, caps.Length);
                float len = (state.TipPos - pivot).Length();
                if (MathF.Abs(len - restLen) > 1e-4f)
                {
                    fail.Add($"SpringPhysicsMath: zero-stretch violated under collider: {len:F6}");
                    break;
                }
            }

            // 4. Teleport guard resets velocity cleanly
            pivot.X += 5.0f;
            var qWarp = SpringPhysicsMath.StepNode(ref state, pivot, parentRot, System.Numerics.Quaternion.Identity, restDirLocal, restLen, 1f / 60f, in sp, null, 0);
            if (state.Velocity.LengthSquared() > 1e-6f || MathF.Abs(qWarp.W - 1f) > 1e-5f)
                fail.Add("SpringPhysicsMath: teleport guard failed to reset state");
        }

        private static string RepoFile(string relative)
        {
            var p = Path.GetFullPath(Path.Combine(Directory.GetCurrentDirectory(), relative));
            if (File.Exists(p)) return p;
            var repo = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", relative));
            if (File.Exists(repo)) return repo;
            return p;
        }
    }
}
