// Character/Kinematics/ArmRest.cs: 臂鏈 rest-pose 校準與具名掌錨（0.3.9 重寫，M1/M2 模塊）——
// Build 期一次性捕捉 ArmRestChain（純數學面收編 Kinematics/TwoBoneIk 的 TwoBoneIkChain：
// L1/L2/ArmRatio/InitRotUpper/InitRotFore/DirCUpper/DirCFore/ChainGamePalm0/PoleLocalChest；
// 本檔不重複任何求值公式，每幀求解權威在 TwoBoneIkMath.Solve）。
// M2 具名掌錨（raid／預覽統一）：右手首/左手首契約列錨（games palmL/pbR）優先精確名
// Base HumanLPalm / Base HumanRPalm（在活體骨架錨下 DFS 搜尋；命名版為遊戲真身掌骨，
// 契約錨可能落在 palm 中繼節點），尋獲即 source=BaseHuman*，缺漏／摧毀回退契約錨 source=pb*。
// 胸腔 pole（chest-local）：Build 捕捉 rig 根空間規約 pole（下 −Y×1.0／外 ×0.6／後 −Z×0.4，
// X 側符按 attach 肘位定側）經上半身2 attach 世界旋轉的反變換入 chest 局部；每幀由
// TwoBoneIkMath.Solve 經 chestRotNow 升回世界。胸腔 SDF 半徑 Build 期收緊：
// clamp(0.6×|chest→上臂 attach 距|＋0.02, 0.09, 0.18)，缺量測回退 0.13。
#if !DOLLFRAME_PMX_PARSER_ONLY
using System;
using Snx = System.Numerics;

namespace DollFrame
{
    /// <summary>
    /// 單側臂鏈 Build 校準結果（M1）：純數學鏈（Chain，權威 Kinematics/TwoBoneIk）＋
    /// 具名掌錨（PalmBone/PalmSource）＋ chest 節點參照（ChestNode）＋收緊後胸腔半徑（ChestRadius）。
    /// 每幀唯一消費者＝CharacterRig.Drive 的臂鏈 IK 面（Upper/Fore 面骨）。
    /// </summary>
    internal readonly struct ArmRestChain
    {
        /// <summary>純數學鏈（TwoBoneIkMath.TwoBoneIkChain：L1/L2/ArmRatio/InitRot*/DirC*/ChainGamePalm0/PoleLocalChest）。</summary>
        internal readonly TwoBoneIkMath.TwoBoneIkChain Chain;
        /// <summary>具名遊戲掌錨（M2 動態目標端點；＝掌位置與掌旋轉的逐幀來源）。</summary>
        internal readonly UnityEngine.Transform PalmBone;
        /// <summary>遊戲掌心中指根節點（Build 期快取，避免每幀 DFS/字串比對）。</summary>
        internal readonly UnityEngine.Transform PalmMidBone;
        /// <summary>掌錨來源："BaseHumanLPalm"/"BaseHumanRPalm"（具名）或 "pbLeftPalm"/"pbRightPalm"（契約錨回退）。</summary>
        internal readonly string PalmSource;
        /// <summary>胸腔中心 rig 節點（上半身2；每幀 Drive 取其 position/rotation 餵 Solve）。</summary>
        internal readonly UnityEngine.Transform ChestNode;
        /// <summary>收緊後胸腔 SDF 半徑（Build 期箝位）。</summary>
        internal readonly float ChestRadius;
        /// <summary>PMX 手腕到掌心握持點（45% 中指根）的腕局部空間偏移。</summary>
        internal readonly Snx.Vector3 RigPalmGripLocal;
        /// <summary>PMX 上臂/前臂/手腕骨索引與 attach 原始 localPosition（供幀首還原零拉伸基準）。</summary>
        internal readonly int UpperIdx;
        internal readonly int ForeIdx;
        internal readonly int WristIdx;
        internal readonly UnityEngine.Vector3 UpperLocalPos0;
        internal readonly UnityEngine.Vector3 ForeLocalPos0;
        internal readonly UnityEngine.Vector3 WristLocalPos0;

        internal ArmRestChain(TwoBoneIkMath.TwoBoneIkChain chain, UnityEngine.Transform palmBone,
            UnityEngine.Transform palmMidBone, string palmSource, UnityEngine.Transform chestNode, float chestRadius,
            Snx.Vector3 rigPalmGripLocal, int upperIdx, int foreIdx, int wristIdx,
            UnityEngine.Vector3 upperLocalPos0, UnityEngine.Vector3 foreLocalPos0, UnityEngine.Vector3 wristLocalPos0)
        {
            Chain = chain;
            PalmBone = palmBone;
            PalmMidBone = palmMidBone;
            PalmSource = palmSource;
            ChestNode = chestNode;
            ChestRadius = chestRadius;
            RigPalmGripLocal = rigPalmGripLocal;
            UpperIdx = upperIdx;
            ForeIdx = foreIdx;
            WristIdx = wristIdx;
            UpperLocalPos0 = upperLocalPos0;
            ForeLocalPos0 = foreLocalPos0;
            WristLocalPos0 = wristLocalPos0;
        }
    }

    /// <summary>ArmRestBuild 校準權威（M1/M2；Unity 面私有，純數學不在此）。</summary>
    internal static class ArmRestMath
    {
        /// <summary>胸腔 SDF 半徑常數（0.3.9 重寫收緊）：0.6×|chest→上臂 attach|＋0.02 箝 [0.09, 0.18]，缺量測回退 0.13。</summary>
        internal const float ChestRadiusMin = 0.09f;
        internal const float ChestRadiusMax = 0.18f;
        internal const float ChestRadiusFallback = 0.13f;

        /// <summary>
        /// 單側校準（Build 期一次性）：長鏈量測（post uniform 縮放後）、ArmRatio、chest-local pole
        /// 捕捉、收緊半徑與具名掌錨解析（M2）。回 false＝退化（呼叫端輸出 skip:degenerate、面不掛）。
        /// </summary>
        internal static bool TryCalibrate(
            UnityEngine.Transform rigRoot,
            UnityEngine.Transform[] rigBones,
            int upperIdx, int foreIdx, int wristIdx, int midFingerIdx,
            UnityEngine.Transform anchorWrist,
            Snx.Vector3 dirCUpper, Snx.Vector3 dirCFore,
            Snx.Quaternion worldRotUpper, Snx.Quaternion worldRotFore,
            UnityEngine.Transform chestNode,
            bool isLeft,
            out ArmRestChain rest)
        {
            var fail = default(ArmRestChain);
            rest = fail;
            if (rigBones == null || chestNode == null
                || upperIdx < 0 || foreIdx < 0 || wristIdx < 0
                || upperIdx >= rigBones.Length || foreIdx >= rigBones.Length || wristIdx >= rigBones.Length)
                return false;

            float l1 = UnityEngine.Vector3.Distance(rigBones[foreIdx].position, rigBones[upperIdx].position);
            float l2 = UnityEngine.Vector3.Distance(rigBones[wristIdx].position, rigBones[foreIdx].position);
            if (l1 <= 0f || l2 <= 0f || TwoBoneIkMath.GameArmChainM <= 0f) return false;

            // pole（rigRoot 局部空間 → 世界 → chest 局部）：
            // PMX 局部空間中：外向為 outSign*+X、向下為 −Y、角色後方為 +Z（因 PmxYaw=180°）。
            // 經 rigRoot.TransformDirection 升為 attach 世界方向後，再以 chestNode.rotation⁻¹ 收進 chest 局部，
            // 使每幀 Transform(localChest, chestRotNow) 在 attach 時精確還原世界方向，不再受世界初始朝向或 PmxYaw 影響。
            float elbowRootX = rigRoot.InverseTransformPoint(rigBones[foreIdx].position).x;
            float outSign = MathF.Abs(elbowRootX) < 1e-4f ? (isLeft ? 1f : -1f) : MathF.Sign(elbowRootX);
            var chestInv = Snx.Quaternion.Inverse(Quat(chestNode.rotation));
            var poleWorld0 = rigRoot.TransformDirection(new UnityEngine.Vector3(0.6f * outSign, -1f, 0.4f)).normalized;
            var poleLocalChest = Snx.Vector3.Normalize(Snx.Vector3.Transform(Vec(poleWorld0), chestInv));

            // 收緊半徑：0.6×|chest→上臂 attach 距|＋0.02 箝 [0.09, 0.18]；缺量測（零距）回退 0.13
            float upperDelta = UnityEngine.Vector3.Distance(chestNode.position, rigBones[upperIdx].position);
            float chestRadius = upperDelta > 0f
                ? Math.Clamp(0.6f * upperDelta + 0.02f, ChestRadiusMin, ChestRadiusMax)
                : ChestRadiusFallback;

            // M2 具名掌錨：掌位置/掌旋轉的逐幀來源（raid／預覽同一解析）
            var palmBone = FindNamedPalm(anchorWrist, isLeft, out var palmSource);
            var palmMidBone = FindPalmMidBone(palmBone);

            // PMX 腕關節到掌心握持點（中指根 45%）的腕局部空間偏移，供精準對位遊戲掌心抓點
            var rigPalmGripLocal = Snx.Vector3.Zero;
            if (midFingerIdx >= 0 && midFingerIdx < rigBones.Length && rigBones[midFingerIdx] != null)
            {
                var gripWorld0 = Vec(rigBones[midFingerIdx].position - rigBones[wristIdx].position) * 0.45f;
                rigPalmGripLocal = Snx.Vector3.Transform(gripWorld0, Snx.Quaternion.Inverse(Quat(rigBones[wristIdx].rotation)));
            }

            var chain = new TwoBoneIkMath.TwoBoneIkChain(
                L1: l1,
                L2: l2,
                ArmRatio: (l1 + l2) / TwoBoneIkMath.GameArmChainM,
                ChainGamePalm0: Quat(anchorWrist.rotation),
                InitRotUpper: worldRotUpper,
                InitRotFore: worldRotFore,
                PoleLocalChest: poleLocalChest,
                DirCUpper: dirCUpper,
                DirCFore: dirCFore);
            rest = new ArmRestChain(chain, palmBone, palmMidBone, palmSource, chestNode, chestRadius, rigPalmGripLocal,
                upperIdx, foreIdx, wristIdx,
                rigBones[upperIdx].localPosition, rigBones[foreIdx].localPosition, rigBones[wristIdx].localPosition);
            return true;
        }

        private static UnityEngine.Transform FindPalmMidBone(UnityEngine.Transform palm)
        {
            if (palm == null) return null;
            var hit = DfsContaining(palm, "Digit31") ?? DfsContaining(palm, "Middle");
            if (hit == null && palm.childCount > 0)
                hit = palm.GetChild(palm.childCount / 2);
            return hit;
        }

        private static UnityEngine.Transform DfsContaining(UnityEngine.Transform root, string needle)
        {
            for (int i = 0; i < root.childCount; i++)
            {
                var c = root.GetChild(i);
                if (c == null) continue;
                if (c.name != null && c.name.IndexOf(needle, StringComparison.OrdinalIgnoreCase) >= 0) return c;
                var deeper = DfsContaining(c, needle);
                if (deeper != null) return deeper;
            }
            return null;
        }

        /// <summary>
        /// M2 具名掌錨解析（raid／預覽統一）：契約錨（games palmL/pbR）本尊或其活體骨架子樹內
        /// 精確名 Base HumanLPalm/RPalm 的節點優先（source=BaseHuman*）；缺漏／摧毀回退契約錨
        /// （source=pb*——位置面同錨，僅命名真值差異）。
        /// </summary>
        internal static UnityEngine.Transform FindNamedPalm(UnityEngine.Transform wristAnchor, bool isLeft, out string source)
        {
            string want = isLeft ? "Base HumanLPalm" : "Base HumanRPalm";
            string namedToken = isLeft ? "BaseHumanLPalm" : "BaseHumanRPalm";
            string pbToken = isLeft ? "pbLeftPalm" : "pbRightPalm";
            if (wristAnchor != null)
            {
                if (string.Equals(wristAnchor.name, want, StringComparison.Ordinal))
                {
                    source = namedToken;
                    return wristAnchor;
                }
                var hit = DfsExact(wristAnchor, want);
                if (hit != null)
                {
                    source = namedToken;
                    return hit;
                }
            }
            source = pbToken;
            return wristAnchor;
        }

        // 精確名 DFS（活體骨架子樹；掌鏈淺、規模小，Build 一次性開銷可忽略）
        private static UnityEngine.Transform DfsExact(UnityEngine.Transform root, string name)
        {
            for (int i = 0; i < root.childCount; i++)
            {
                var c = root.GetChild(i);
                if (c == null) continue;
                if (string.Equals(c.name, name, StringComparison.Ordinal)) return c;
                var deeper = DfsExact(c, name);
                if (deeper != null) return deeper;
            }
            return null;
        }

        // boundary relabels（Quat/Vec：Unity ⇄ System.Numerics 欄位逐位拷貝，CharacterRig 同款）
        private static Snx.Quaternion Quat(UnityEngine.Quaternion q) => new Snx.Quaternion(q.x, q.y, q.z, q.w);
        private static Snx.Vector3 Vec(UnityEngine.Vector3 v) => new Snx.Vector3(v.x, v.y, v.z);
    }
}
#endif
