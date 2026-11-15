using System;
using System.Collections.Generic;
using UnityEngine;

namespace DollFrame
{
    /// <summary>
    /// Shared, parser-only model metrics: canonical bone validation, bounds/hip/ground
    /// heights and the 12 anatomical span groups with their fit factors
    /// (model span in PMX units x unitScale / game span in meters). Single source for the
    /// runtime fit and the packager verify matrix (todo 6/7); span math must not be
    /// duplicated elsewhere. Game spans arrive as the metric rig dump
    /// (analysis/eft_skeleton.json shape): game bone name ("Base Human*") -> local position.
    /// The pairs reproduce the pinned expectation table (analysis/fit_expectations.json)
    /// for 克萊妲原皮 within 1e-3 for all 12 groups.
    /// </summary>
    public static class ModelMetrics
    {
        /// <summary>
        /// Analyzes PMX bone hierarchy and bounds to measure anatomical baseline dimensions
        /// (eye height, arm reach, shoulder width) and generate declarative capsule colliders and spring chains.
        /// </summary>
        public static ModelProfile MeasureBaseline(PmxParser.PmxModel model, float unitScale = 0.078f)
        {
            if (model == null || model.Bones == null)
                throw new ArgumentNullException(nameof(model));

            var byName = new Dictionary<string, PmxParser.PmxBone>(StringComparer.Ordinal);
            var byIndex = new Dictionary<int, PmxParser.PmxBone>(model.Bones.Length);
            foreach (var b in model.Bones)
            {
                if (b == null) continue;
                if (!string.IsNullOrEmpty(b.Name) && !byName.ContainsKey(b.Name))
                    byName[b.Name] = b;
                byIndex[b.Index] = b;
            }

            // 1. Reference ground/toe plane
            float toeY = byName.TryGetValue("左足首先", out var toe) ? toe.Pos.y :
                         (byName.TryGetValue("右足首先", out var toeR) ? toeR.Pos.y :
                         (byName.TryGetValue("左足首", out var footL) ? footL.Pos.y : model.BoundsMin.y));

            // 2. Eye height
            float eyeY;
            PmxParser.PmxBone eyeL = null, eyeR = null, eyeBoth = null, head = null;
            if (!byName.TryGetValue("左目", out eyeL))
                byName.TryGetValue("Eye_L", out eyeL);
            if (!byName.TryGetValue("右目", out eyeR))
                byName.TryGetValue("Eye_R", out eyeR);
            if (!byName.TryGetValue("両目", out eyeBoth))
                byName.TryGetValue("Eyes", out eyeBoth);
            if (!byName.TryGetValue("頭", out head))
                byName.TryGetValue("Head", out head);

            if (eyeL != null && eyeR != null)
                eyeY = (eyeL.Pos.y + eyeR.Pos.y) * 0.5f;
            else if (eyeL != null)
                eyeY = eyeL.Pos.y;
            else if (eyeR != null)
                eyeY = eyeR.Pos.y;
            else if (eyeBoth != null)
                eyeY = eyeBoth.Pos.y;
            else if (head != null)
                eyeY = head.Pos.y + (0.10f / unitScale);
            else
                eyeY = model.BoundsMax.y * 0.9f;
            float eyeHeight = (eyeY - toeY) * unitScale;

            // 3. Arm reach (upper arm + forearm)
            float reachL = 0f, reachR = 0f;
            float uarmL = 0f, farmL = 0f;
            float uarmR = 0f, farmR = 0f;

            if (byName.TryGetValue("左腕", out var uarmBoneL) && byName.TryGetValue("左ひじ", out var farmBoneL))
            {
                uarmL = Dist(uarmBoneL.Pos, farmBoneL.Pos);
                if (byName.TryGetValue("左手首", out var wristBoneL))
                    farmL = Dist(farmBoneL.Pos, wristBoneL.Pos);
                reachL = (uarmL + farmL) * unitScale;
            }

            if (byName.TryGetValue("右腕", out var uarmBoneR) && byName.TryGetValue("右ひじ", out var farmBoneR))
            {
                uarmR = Dist(uarmBoneR.Pos, farmBoneR.Pos);
                if (byName.TryGetValue("右手首", out var wristBoneR))
                    farmR = Dist(farmBoneR.Pos, wristBoneR.Pos);
                reachR = (uarmR + farmR) * unitScale;
            }

            float armReach;
            if (reachL > 0f && reachR > 0f)
                armReach = (reachL + reachR) * 0.5f;
            else if (reachL > 0f)
                armReach = reachL;
            else if (reachR > 0f)
                armReach = reachR;
            else
                armReach = 0.45f;

            // 4. Shoulder width (distance between arm pivot joints)
            float shoulderWidth;
            if (byName.TryGetValue("左腕", out var shoulderArmL) && byName.TryGetValue("右腕", out var shoulderArmR))
                shoulderWidth = Dist(shoulderArmL.Pos, shoulderArmR.Pos) * unitScale;
            else if (byName.TryGetValue("左肩", out var clavL) && byName.TryGetValue("右肩", out var clavR))
                shoulderWidth = Dist(clavL.Pos, clavR.Pos) * unitScale;
            else
                shoulderWidth = 0.25f;

            // 5. Automatic capsule colliders
            var colliders = new List<CapsuleProfile>();

            // Head
            if (byName.TryGetValue("頭", out var headBone))
            {
                float headLen = byName.TryGetValue("首", out var neckBone)
                    ? Math.Max(Dist(headBone.Pos, neckBone.Pos) * unitScale, 0.12f)
                    : 0.15f;
                colliders.Add(new CapsuleProfile("頭", 0.02f, headLen, new float[] { 0f, headLen * 0.4f, 0f }));
            }

            // Chest / Upper Torso
            if (byName.TryGetValue("上半身2", out var chestBone))
            {
                float chestHeight = 0.20f;
                if (byName.TryGetValue("首", out var neck))
                    chestHeight = Math.Max(Dist(chestBone.Pos, neck.Pos) * unitScale * 0.8f, 0.15f);
                float chestRad = Math.Clamp(shoulderWidth * 0.45f, 0.10f, 0.18f);
                colliders.Add(new CapsuleProfile("上半身2", chestRad, chestHeight, new float[] { 0f, 0f, 0f }));
            }
            else if (byName.TryGetValue("上半身", out var spineBone))
            {
                float spineHeight = 0.22f;
                float spineRad = Math.Clamp(shoulderWidth * 0.45f, 0.10f, 0.18f);
                colliders.Add(new CapsuleProfile("上半身", spineRad, spineHeight, new float[] { 0f, 0f, 0f }));
            }

            // Arms
            if (uarmL > 0f)
            {
                float len = uarmL * unitScale;
                colliders.Add(new CapsuleProfile("左腕", 0.05f, len, new float[] { 0f, -len * 0.5f, 0f }));
            }
            if (uarmR > 0f)
            {
                float len = uarmR * unitScale;
                colliders.Add(new CapsuleProfile("右腕", 0.05f, len, new float[] { 0f, -len * 0.5f, 0f }));
            }

            if (farmL > 0f)
            {
                float len = farmL * unitScale;
                colliders.Add(new CapsuleProfile("左ひじ", 0.04f, len, new float[] { 0f, -len * 0.5f, 0f }));
            }
            if (farmR > 0f)
            {
                float len = farmR * unitScale;
                colliders.Add(new CapsuleProfile("右ひじ", 0.04f, len, new float[] { 0f, -len * 0.5f, 0f }));
            }

            // Legs
            if (byName.TryGetValue("左足", out var thighL) && byName.TryGetValue("左ひざ", out var kneeL))
            {
                float len = Dist(thighL.Pos, kneeL.Pos) * unitScale;
                colliders.Add(new CapsuleProfile("左足", 0.07f, len, new float[] { 0f, -len * 0.5f, 0f }));
            }
            if (byName.TryGetValue("右足", out var thighR) && byName.TryGetValue("右ひざ", out var kneeR))
            {
                float len = Dist(thighR.Pos, kneeR.Pos) * unitScale;
                colliders.Add(new CapsuleProfile("右足", 0.07f, len, new float[] { 0f, -len * 0.5f, 0f }));
            }

            if (byName.TryGetValue("左ひざ", out var calfL) && byName.TryGetValue("左足首", out var ankleL))
            {
                float len = Dist(calfL.Pos, ankleL.Pos) * unitScale;
                colliders.Add(new CapsuleProfile("左ひざ", 0.055f, len, new float[] { 0f, -len * 0.5f, 0f }));
            }
            if (byName.TryGetValue("右ひざ", out var calfR) && byName.TryGetValue("右足首", out var ankleR))
            {
                float len = Dist(calfR.Pos, ankleR.Pos) * unitScale;
                colliders.Add(new CapsuleProfile("右ひざ", 0.055f, len, new float[] { 0f, -len * 0.5f, 0f }));
            }

            // 6. Automatic spring chains
            var childrenMap = new Dictionary<int, List<PmxParser.PmxBone>>();
            foreach (var b in model.Bones)
            {
                if (b != null && b.Parent >= 0)
                {
                    if (!childrenMap.TryGetValue(b.Parent, out var list))
                    {
                        list = new List<PmxParser.PmxBone>();
                        childrenMap[b.Parent] = list;
                    }
                    list.Add(b);
                }
            }

            var springs = new List<SpringChainProfile>();
            foreach (var b in model.Bones)
            {
                if (b == null || string.IsNullOrEmpty(b.Name)) continue;
                string cat = ModelProfileKeywords.CategorizeSpring(b.Name);
                if (cat == null) continue;

                bool parentIsSpring = false;
                if (b.Parent >= 0 && byIndex.TryGetValue(b.Parent, out var parentBone) && parentBone != null)
                {
                    parentIsSpring = ModelProfileKeywords.CategorizeSpring(parentBone.Name) != null;
                }

                if (!parentIsSpring)
                {
                    // b is root of a spring chain. Collect all descendants in this chain.
                    var childList = new List<string>();
                    var queue = new Queue<int>();
                    queue.Enqueue(b.Index);
                    while (queue.Count > 0)
                    {
                        int currIdx = queue.Dequeue();
                        if (childrenMap.TryGetValue(currIdx, out var chList))
                        {
                            foreach (var ch in chList)
                            {
                                if (ch != null && !string.IsNullOrEmpty(ch.Name))
                                {
                                    childList.Add(ch.Name);
                                    queue.Enqueue(ch.Index);
                                }
                            }
                        }
                    }

                    float stiffness = 0.5f;
                    float drag = 0.4f;
                    float radius = 0.02f;
                    if (cat == "Skirt")
                    {
                        stiffness = 0.4f;
                        drag = 0.5f;
                        radius = 0.03f;
                    }
                    else if (cat == "Breast")
                    {
                        stiffness = 0.6f;
                        drag = 0.3f;
                        radius = 0.02f;
                    }

                    springs.Add(new SpringChainProfile(b.Name, childList.ToArray(), stiffness, drag, radius, cat));
                }
            }

            return new ModelProfile(eyeHeight, armReach, shoulderWidth, colliders.ToArray(), springs.ToArray());
        }

        internal struct Result
        {
            internal int Bones;
            internal int Verts;
            internal int SkinBones;
            internal float HeightU;
            internal float HeightM;
            internal float HipM;
            internal float GroundOffsetM;
            internal bool ScaleFlag;
            internal string[] Missing;
            internal int Dups;
            internal int AccessoryMats;
            /// <summary>12 logical groups, L side factor (0 when the span could not be measured).</summary>
            internal Dictionary<string, float> Factors;
            /// <summary>The 7 L/R symmetric groups: group -&gt; [factorL, factorR].</summary>
            internal Dictionary<string, float[]> PairFactors;
            /// <summary>True when any L/R pair diverges by more than 1% (|L - R| / L).</summary>
            internal bool LrWarn;
            /// <summary>Groups that triggered LrWarn (empty when LrWarn is false).</summary>
            internal string[] LrWarnGroups;
        }

        /// <summary>Canonical bones every packaged model must contain (matrix 'missing' set).</summary>
        internal static readonly string[] RequiredBones =
        {
            "下半身", "上半身", "上半身2", "首", "頭",
            "左肩", "右肩", "左腕", "右腕",
            "左ひじ", "右ひじ", "左手首", "右手首",
            "左足", "右足", "左ひざ", "右ひざ",
            "左足首", "右足首", "左足首先", "右足首先"
        };

        internal static readonly string[] FactorGroups =
        {
            "hips", "spineRoot", "torso", "chestNeck", "neckHead",
            "hipOffset", "thigh", "shank", "foot", "clavicle", "uarm", "farm"
        };

        private static readonly string[] AccessoryKeywords =
            { "gun", "glock", "bag", "butt", "weapon", "silencer" };

        private sealed class SpanDef
        {
            internal string Group;
            internal string ModelA;   // first bone of the model span pair (PMX units)
            internal string ModelB;   // second bone of the model span pair
            internal string[] GameL;  // game rig bones summed by |pos| (L side)
            internal string[] GameR;  // R side; null = group has no mirror pairing
            internal bool Vertical;   // hips group: |y| difference of the pair, not distance
        }

        // 12 anatomical span pairs -> game rig bones. The R pair mirrors ModelA/ModelB via
        // 左->右 (exact glyphs left as authored; verified against analysis/koleda_bones.json).
        // clavicle pairs 左肩->左腕: it is the only pairing that reproduces the pinned
        // factor 0.5928 (the whole-chain 上半身2->左腕 gives 1.4592 and matches nothing).
        private static readonly SpanDef[] Spans =
        {
            new SpanDef{ Group="hips",      ModelA="下半身",  ModelB="左足首先", GameL=new[]{ "Pelvis" }, Vertical=true },
            new SpanDef{ Group="spineRoot", ModelA="下半身",  ModelB="上半身",   GameL=new[]{ "Spine1" } },
            new SpanDef{ Group="torso",     ModelA="上半身",  ModelB="上半身2",  GameL=new[]{ "Spine2", "Spine3" } },
            new SpanDef{ Group="chestNeck", ModelA="上半身2", ModelB="首",       GameL=new[]{ "Neck" } },
            new SpanDef{ Group="neckHead",  ModelA="首",      ModelB="頭",       GameL=new[]{ "Head" } },
            new SpanDef{ Group="hipOffset", ModelA="下半身",  ModelB="左足",     GameL=new[]{ "LThigh1" }, GameR=new[]{ "RThigh1" } },
            new SpanDef{ Group="thigh",     ModelA="左足",    ModelB="左ひざ",   GameL=new[]{ "LThigh2", "LCalf" }, GameR=new[]{ "RThigh2", "RCalf" } },
            new SpanDef{ Group="shank",     ModelA="左ひざ",  ModelB="左足首",   GameL=new[]{ "LFoot" }, GameR=new[]{ "RFoot" } },
            new SpanDef{ Group="foot",      ModelA="左足首",  ModelB="左足首先", GameL=new[]{ "LToe" }, GameR=new[]{ "RToe" } },
            new SpanDef{ Group="clavicle",  ModelA="左肩",    ModelB="左腕",     GameL=new[]{ "LUpperarm" }, GameR=new[]{ "RUpperarm" } },
            new SpanDef{ Group="uarm",      ModelA="左腕",    ModelB="左ひじ",   GameL=new[]{ "LForearm1" }, GameR=new[]{ "RForearm1" } },
            new SpanDef{ Group="farm",      ModelA="左ひじ",  ModelB="左手首",   GameL=new[]{ "LForearm2", "LForearm3", "LPalm" }, GameR=new[]{ "RForearm2", "RForearm3", "RPalm" } },
        };

        private const string GamePrefix = "Base Human";

        internal static Result Compute(PmxParser.PmxModel model, float unitScale,
            IReadOnlyDictionary<string, Vector3> gameSpans)
        {
            var r = new Result { Bones = model.Bones.Length, Verts = model.Pos.Length };

            var byName = new Dictionary<string, PmxParser.PmxBone>(StringComparer.Ordinal);
            var counts = new Dictionary<string, int>(StringComparer.Ordinal);
            foreach (var b in model.Bones)
            {
                if (string.IsNullOrEmpty(b.Name)) continue;
                counts.TryGetValue(b.Name, out var c);
                counts[b.Name] = c + 1;
                if (c == 0) byName[b.Name] = b;
            }
            r.Dups = 0;
            foreach (var kv in counts) if (kv.Value > 1) r.Dups++;

            var missing = new List<string>();
            foreach (var name in RequiredBones)
                if (!byName.ContainsKey(name)) missing.Add(name);
            r.Missing = missing.ToArray();

            var skin = new HashSet<int>();
            foreach (var w in model.Weights)
            {
                if (w.weight0 > 0f) skin.Add(w.boneIndex0);
                if (w.weight1 > 0f) skin.Add(w.boneIndex1);
                if (w.weight2 > 0f) skin.Add(w.boneIndex2);
                if (w.weight3 > 0f) skin.Add(w.boneIndex3);
            }
            r.SkinBones = skin.Count;

            r.HeightU = model.BoundsMax.y - model.BoundsMin.y;
            r.HeightM = r.HeightU * unitScale;
            // todo 11 gate: authored height must be 14-26 units (0.078 m/unit)
            r.ScaleFlag = r.HeightU < 14f || r.HeightU > 26f;

            // todo 7/10: hip height above the toe bone (fallback: bounds edges); the root
            // Y offset that lands the toe on the ground plane is -toeY x unitScale
            // (0 for packages whose toe sits at the authored origin, e.g. 克萊妲原皮)
            float hipY = byName.TryGetValue("下半身", out var hip) ? hip.Pos.y : model.BoundsMax.y;
            float toeY = byName.TryGetValue("左足首先", out var toe) ? toe.Pos.y : model.BoundsMin.y;
            r.HipM = (hipY - toeY) * unitScale;
            r.GroundOffsetM = -toeY * unitScale;

            int acc = 0;
            foreach (var m in model.Mats)
                if (IsAccessory(m.Name) || IsAccessory(m.NameEn)) acc++;
            r.AccessoryMats = acc;

            r.Factors = new Dictionary<string, float>(FactorGroups.Length);
            r.PairFactors = new Dictionary<string, float[]>(7);
            var warn = new List<string>();
            foreach (var sp in Spans)
            {
                float fL = FactorOf(sp, byName, gameSpans, unitScale, false);
                r.Factors[sp.Group] = fL;
                if (sp.GameR == null) continue;
                float fR = FactorOf(sp, byName, gameSpans, unitScale, true);
                r.PairFactors[sp.Group] = new[] { fL, fR };
                if (fL > 0f && fR > 0f && Math.Abs(fL - fR) / Math.Abs(fL) > 0.01f)
                    warn.Add(sp.Group);
            }
            r.LrWarn = warn.Count > 0;
            r.LrWarnGroups = warn.ToArray();
            return r;
        }

        private static bool IsAccessory(string s)
        {
            if (string.IsNullOrEmpty(s)) return false;
            foreach (var k in AccessoryKeywords)
                if (s.Contains(k, StringComparison.OrdinalIgnoreCase)) return true;
            return false;
        }

        private static float FactorOf(SpanDef sp, Dictionary<string, PmxParser.PmxBone> byName,
            IReadOnlyDictionary<string, Vector3> gameSpans, float unitScale, bool mirrored)
        {
            var a = mirrored ? Mirror(sp.ModelA) : sp.ModelA;
            var b = mirrored ? Mirror(sp.ModelB) : sp.ModelB;
            if (!byName.TryGetValue(a, out var ba) || !byName.TryGetValue(b, out var bb)) return 0f;
            float spanU = sp.Vertical ? Math.Abs(ba.Pos.y - bb.Pos.y) : Dist(ba.Pos, bb.Pos);
            float modelM = spanU * unitScale;

            var game = mirrored ? sp.GameR : sp.GameL;
            float gameM = 0f;
            foreach (var g in game)
            {
                if (!gameSpans.TryGetValue(GamePrefix + g, out var p)) return 0f;
                gameM += Len(p);
            }
            if (gameM <= 0f) return 0f;
            return modelM / gameM;
        }

        private static string Mirror(string name) => name.Replace('左', '右');

        // Mathf-independent helpers: ModelMetrics compiles against the parser-only stubs too,
        // whose Mathf only carries Min/Max.
        private static float Dist(Vector3 a, Vector3 b)
        {
            float dx = a.x - b.x, dy = a.y - b.y, dz = a.z - b.z;
            return (float)Math.Sqrt(dx * dx + dy * dy + dz * dz);
        }

        private static float Len(Vector3 v)
        {
            return (float)Math.Sqrt(v.x * v.x + v.y * v.y + v.z * v.z);
        }
    }
}
