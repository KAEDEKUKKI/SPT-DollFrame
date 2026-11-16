// Character/ModelScaler.cs: 模型側等比縮放（2026-11-14-6 全模型單一等比改約）——遊戲骨架全程 pristine，
// rig 根於 Build 時「一次性」乘入單一等比因子 k：模型整體（軀幹/頭/四肢/手掌手指）逐位保持作者
// 原始比例（無任何相對變形），k 以腿鏈錨定（k＝遊戲 FBAK 腿鏈長/模型腿鏈長），令模型腿跨距貼齊
// 遊戲腿鏈——腳軌跡、接地與膠囊錨定零回歸。臂鏈個別縮放退役（2026-11-14-6）：肩 pivot 的鏈級
// localScale 曾把手臂子樹（含手掌手指）相對軀幹放大 arm k 1.29–1.41 倍（原始比例包實測），
// 即「手指與手臂被拉長」之根因，機制整組退役。零拉伸不變量改約為：縮放只發生在 Build 的 rig 根
// 一次性 uniform 乘入；每幀仍僅寫 localRotation。純數學（ModelScaleMath）雙編譯進 Tests。
using System;
using System.Collections.Generic;
#if !DOLLFRAME_PMX_PARSER_ONLY
using UnityEngine;
#endif

namespace DollFrame
{
    /// <summary>
    /// 純數學權威（雙編譯）：由 ModelMetrics 12 段因子中的腿鏈群組推出「全模型單一等比縮放因子」k。
    /// 遊戲跨距常數來自 analysis/eft_skeleton.json（pristine FBAK 基線，凍結）：
    ///   腿鏈（足 pivot 以下）＝ LThigh2+LCalf+LFoot+LToe = 1.07100 m。
    /// 因子 factor = 模型跨距 / 遊戲跨距（ModelMetrics 定義），故 k = 遊戲腿鏈長 / Σ(factor_i×遊戲段長)。
    /// k 為全身 uniform：腿貼合地面真值，臂/軀幹/頭隨全身同比縮放——原始比例不變形（臂鏈個別貼合已退役）。
    /// </summary>
    public static class ModelScaleMath
    {
        internal const float GameThigh = 0.22812f;   // LThigh2 / RThigh2
        internal const float GameCalf = 0.22812f;    // LCalf / RCalf
        internal const float GameFoot = 0.46025f;    // LFoot / RFoot（shank 跨距）
        internal const float GameToe = 0.15451f;     // LToe / RToe（foot 跨距）

        /// <summary>腿鏈全長（m，足 pivot 以下含趾）。＝ 1.07100。</summary>
        internal const float GameLegSpan = GameThigh + GameCalf + GameFoot + GameToe;

        /// <summary>腿錨定等比因子箝制帶（超帶時執行期箝制＋`scale:` 行帶 raw 標註）。</summary>
        internal const float KLegMin = 0.70f, KLegMax = 1.45f;

        /// <summary>縮放後全身高度上限（m），防止長身／缺骨模型被腿錨定異常放大。</summary>
        internal const float MaxScaledHeightM = 1.86f;

        /// <summary>模型腿鏈跨距（m）：thigh 跨距覆蓋 LThigh2+LCalf、shank 覆蓋 LFoot、foot 覆蓋 LToe。</summary>
        public static float ModelLegSpanM(float fThigh, float fShank, float fFoot)
        {
            if (fThigh <= 0f && fShank <= 0f && fFoot <= 0f) return 0f;
            float leg = 0f;
            if (fThigh > 0f) leg += fThigh * (GameThigh + GameCalf);
            if (fShank > 0f) leg += fShank * GameFoot;
            if (fFoot > 0f) leg += fFoot * GameToe;
            return leg;
        }

        public static float ClampK(float k, float min, float max)
        {
            if (k <= 0f) return 1f;
            if (k < min) return min;
            if (k > max) return max;
            return k;
        }

        /// <summary>全模型單一等比縮放計畫（箝制前後值一併攜帶，供 telemetry 與 harness 斷言）。</summary>
        public readonly struct ScalePlan
        {
            public readonly float K, Raw;
            public readonly bool Clamped;

            public ScalePlan(float k, float raw, bool clamped)
            {
                K = k; Raw = raw; Clamped = clamped;
            }
        }

        /// <summary>
        /// 由腿鏈因子推導全模型等比縮放因子；因子缺席（0）的群組不計入模型鏈長，亦不計入對應遊戲鏈長
        /// （避免缺足尖骨 `左足首先` 等模型把缺席段長攤入其餘段而異常放大）。
        /// 臂鏈因子（clavicle/uarm/farm）不再參與縮放（arm 錨定退役）；因子本身仍由
        /// `factors`/`proportions` 面照常量測與凍結。
        /// </summary>
        public static ScalePlan? Solve(IReadOnlyDictionary<string, float> factors, float heightM = 0f)
        {
            if (factors == null) return null;
            float F(string g) => factors.TryGetValue(g, out var v) ? v : 0f;
            float fThigh = F("thigh");
            float fShank = F("shank");
            float fFoot = F("foot");

            // 至少須具備大腿或小腿其一，避免僅憑單一足尖骨反推整具身體。
            if (fThigh <= 0f && fShank <= 0f) return null;

            float gameLegM = 0f;
            float modelLegM = 0f;
            if (fThigh > 0f) { gameLegM += GameThigh + GameCalf; modelLegM += fThigh * (GameThigh + GameCalf); }
            if (fShank > 0f) { gameLegM += GameFoot; modelLegM += fShank * GameFoot; }
            if (fFoot > 0f)  { gameLegM += GameToe; modelLegM += fFoot * GameToe; }
            if (modelLegM <= 0f || gameLegM <= 0f) return null;

            float raw = gameLegM / modelLegM;
            float maxK = heightM > 0f
                ? Math.Max(KLegMin, Math.Min(KLegMax, MaxScaledHeightM / heightM))
                : KLegMax;
            float k = ClampK(raw, KLegMin, maxK);
            const float eps = 1e-4f;
            return new ScalePlan(k, raw, Math.Abs(k - raw) > eps);
        }
    }

#if !DOLLFRAME_PMX_PARSER_ONLY
    /// <summary>Build 期一次性全模型等比縮放套用器（唯一合法的 localScale 寫入點）。</summary>
    internal static class ModelScaler
    {
        /// <summary>在 rig 根逐軸乘入單一等比因子 k（root.localScale 已攜 unitScale；一次寫入）。</summary>
        internal static int Apply(Transform root, ModelScaleMath.ScalePlan plan)
        {
            if (root == null) return 0;
            var s = root.localScale;
            root.localScale = new Vector3(s.x * plan.K, s.y * plan.K, s.z * plan.K);
            return 1;
        }

        /// <summary>`scale:` 行族行面（單一生產者）。</summary>
        internal static string ScaleLine(string pkgName, ModelScaleMath.ScalePlan plan, int roots)
        {
            var raw = plan.Clamped ? $" (raw {plan.Raw:0.####})" : "";
            return $"[DollFrame] scale: pkg={pkgName} k={plan.K:0.####} root={roots}{raw}";
        }
    }
#endif
}
