using System;

namespace GF2
{
    /// <summary>
    /// 一個 [GF2] log 行族的登記項（todo 13 的登記制資料單元）。
    /// Producer 為 grep 核實過的單一生產者；HarnessPin 是釘此行的
    /// harness 子命令（"—" = 無子命令釘點；failure 標記一律由 tools/check-log.sh 計數）；
    /// DocsLocus 是行族舉證的文件落點。
    /// </summary>
    internal readonly struct TelemetryLine
    {
        public readonly string Kind;       // "family"（正常行族）或 "failure"（失敗標記）
        public readonly string LineFamily; // log 上的字面前綴（含 [GF2] 後的第一段，逐字）
        public readonly string Producer;   // grep 核實的單一生產者（類名＋格式函式）
        public readonly string HarnessPin; // harness 子命令 / check-log
        public readonly string DocsLocus;  // 文件落點

        public TelemetryLine(string kind, string lineFamily, string producer, string harnessPin, string docsLocus)
        {
            Kind = kind;
            LineFamily = lineFamily;
            Producer = producer;
            HarnessPin = harnessPin;
            DocsLocus = docsLocus;
        }
    }

    /// <summary>
    /// [GF2] 行族登記表（零行為；只在編譯期載體上列資料，不改任何生產者）。
    /// log 契約「單一生產者、呼叫端不改格式」的機器可讀對映：每個行族在此有唯一登記。
    /// 新增 [GF2] 行族 = 先實作生產者、再於此登記、再視需要補 harness 子命令／
    /// check-log fixture／README 疑難排解／AGENTS.md log contract（見 AGENTS.md 行族同步規則）。
    /// 2026-11-14 架構反轉重列：退役 grip:/fp arms attached/fp visibility:/armik:/armik-frame:/
    /// fp eye:/fp plane:/fit:/warn: weapon operating（次要動作與視角模塊刪除），新增 scale:
    /// 與 fp head mask:；diverges 失敗標記隨 armik-frame: 退役除名，failure 標記 5→4。
    /// 0.3.10 新增 arm gate:/preview unarmed:/hideout body animator:（狀態感知三面，
    /// 2026-11-14-10 層 1/3 狀態治理）。
    /// </summary>
    internal static class TelemetryRegistry
    {
        /// <summary>check-log 釘的 6 個固定前綴（tools/check-log.sh 的 req 表，逐字）。</summary>
        internal static readonly string[] PinnedPrefixes =
        {
            "contract:",
            "rig: bones=",
            "body replaced on '",
            "preload:",
            "cache: evicted",
            "scale:",
        };

        /// <summary>全部行族登記（行族 18 ＋ failure 標記 4 ＝ 22 entries；0.3.11 新增兩態收斂；0.3.12 狀態判準改約＋rig source:）。</summary>
        internal static readonly TelemetryLine[] Entries =
        {
            // ---- check-log 釘的 6 個固定前綴 ----
            // grep: GameBones.cs（$"[GF2] contract: ..."，附屬行 contract skip 附於行尾）
            new TelemetryLine("family", "contract:", "GameBones.ContractLine", "constants", "AGENTS.md log contract"),
            // grep: CharacterRig.cs Build 尾行（rig 統計行）
            new TelemetryLine("family", "rig: bones=", "CharacterRig.Build", "—", "AGENTS.md log contract"),
            // grep: BodyReplacer.cs Apply 尾行（行尾附 ContractLine(contract)）
            new TelemetryLine("family", "body replaced on '", "BodyReplacer.Apply", "vanilla", "AGENTS.md log contract"),
            // grep: BodyReplacer.cs（preload 路徑打印；冷/暖由 PmxRuntime 讀取路徑決定）
            new TelemetryLine("family", "preload:", "BodyReplacer.Apply（preload 路徑，PmxRuntime 冷/暖）", "cache", "AGENTS.md log contract"),
            // grep: PmxRuntime.cs（LRU 逐出）
            new TelemetryLine("family", "cache: evicted", "PmxRuntime（idle LRU 逐出）", "cache", "AGENTS.md log contract"),
            // grep: CharacterRig.cs Build（模型側全模型單一等比縮放行；Producer 唯一＝ModelScaler.ScaleLine）
            new TelemetryLine("family", "scale:", "CharacterRig.Build ← ModelScaler.ScaleLine（rig 根 uniform localScale）", "proportions", "AGENTS.md log contract"),

            // ---- 觀測面行族 ----
            // grep: BodyReplacer.cs Apply（ModelMetrics 接地偏移）
            new TelemetryLine("family", "ground:", "BodyReplacer.Apply（ModelMetrics 接地偏移）", "—", "AGENTS.md log contract"),
            // grep: CharacterRig.cs GroundCheck（逐地面核對）
            new TelemetryLine("family", "ground-check:", "CharacterRig.GroundCheck", "—", "AGENTS.md log contract"),
            // grep: BodyReplacer.cs（warm 掛載計時）
            new TelemetryLine("family", "attach ", "BodyReplacer.Apply（attach 計時）", "—", "AGENTS.md log contract"),
            // grep: BodyReplacer.cs（>16ms 預算警告）
            new TelemetryLine("family", "warn: attach exceeded frame budget", "BodyReplacer.Apply（attach 計時警告）", "—", "AGENTS.md log contract"),
            // grep: PmxRuntime.cs（idle LRU 保留）
            new TelemetryLine("family", "cache: idle", "PmxRuntime（idle LRU 保留）", "—", "AGENTS.md log contract"),
            // grep: CharacterRig.cs SetupArmRest（0.3.9 重寫：臂鏈 rest 校準 attach 一次性行：
            // cfg／兩側 ok/skip:reason＋ok 側 palm/ratio/len）＋CharacterRig.cs Drive/EmitIkTargetLine
            // （節流 ≥0.25s 的 [GF2] ik target: 節流行，L/R token 僅在該側依驅動時輸出）——
            // IK 行族雙形：attach 行 `[GF2] ik: ...`＋節流行 `[GF2] ik target: ...`
            new TelemetryLine("family", "ik:", "CharacterRig.SetupArmRest（attach 一次性）＋CharacterRig.EmitIkTargetLine（每幀 target 節流）", "ik", "AGENTS.md log contract"),
            // grep: CharacterRig.cs Drive（parity 採樣行，每 attach 至多 3 行）
            new TelemetryLine("family", "pose-parity", "CharacterRig.Drive（parity 採樣行）", "—", "AGENTS.md log contract"),
            // grep: BodyReplacer.cs LateUpdate（頭部 SMR 遮罩切換一次性資訊行）
            new TelemetryLine("family", "fp head mask:", "BodyReplacer.LateUpdate（FirstPersonSMRMask 切換）", "smrmask", "AGENTS.md log contract"),
            // grep: BodyReplacer.cs LateUpdate（0.3.10 狀態閘翻轉一次性資訊行：alive∧armed→ik；0.3.12 加 item= token）
            new TelemetryLine("family", "arm gate:", "BodyReplacer.LateUpdate（狀態閘翻轉資訊行）", "—", "AGENTS.md log contract"),
            // grep: BodyReplacer.cs Apply（0.3.12 attach 一次性綁定源頭驗證行：TP 骨架 vs FP Hands 骨架）
            new TelemetryLine("family", "rig source:", "BodyReplacer.EmitRigSourceLine（attach 一次性）", "—", "AGENTS.md log contract"),
            // grep: MenuUI/PreviewProxy.cs（0.3.10 預覽 attach 一次性空手契約行）
            new TelemetryLine("family", "preview unarmed:", "PreviewProxyDriver.ApplyUnarmedContract（attach 一次性）", "—", "AGENTS.md log contract"),
            // grep: Character/Patches.cs（0.3.10 藏身處 FP 身體動畫機復活翻轉資訊行）
            new TelemetryLine("family", "hideout body animator:", "Patches.PostHideoutSetPointOfView（狀態翻轉資訊行）", "—", "AGENTS.md log contract"),

            // ---- 4 組失敗標記（僅計數於 [GF2] 生產行；由 check-log/fixture 計數）----
            // grep: GameBones.cs（ContractLine 的附屬行）
            new TelemetryLine("failure", "contract skip:", "GameBones.ContractLine", "check-log", "check-log + README"),
            // grep: PmxRuntime.cs（throw "implausible bone count"）經 BodyReplacer
            //（"body replace failed: <ex>"）帶出到 [GF2] 行上被 check-log 命中
            new TelemetryLine("failure", "implausible", "BodyReplacer『body replace failed』← PmxRuntime throw", "check-log", "check-log + README"),
            // grep: BodyReplacer.cs（Root_Joint missing ＋ character package missing）；
            // 注意 GameBones 的 "game anchors unresolved" 行攜的是 unresolved 標記，非 missing
            new TelemetryLine("failure", "missing", "BodyReplacer.Apply（Root_Joint ＋ character package missing）", "check-log", "check-log + README"),
            // grep: GameBones.cs（ContractLine 與 game anchors、FromSkeletonRoot preview 檢查行）、
            // PmxRuntime.cs（textures）、ModelScaler 縮放告警未攜此標記
            new TelemetryLine("failure", "unresolved", "GameBones.ContractLine ＋ GameBones.FromSkeletonRoot ＋ PmxRuntime（textures）", "check-log", "check-log + README"),
        };

        /// <summary>
        /// 登記表完整性斷言（harness `telemetry` 面）：行族 ≥12、failure 標記恰 4、
        /// check-log 的 6 個固定前綴逐字皆已登記。
        /// </summary>
        internal static bool VerifyCompleteness()
        {
            var families = 0;
            var failures = 0;
            foreach (var e in Entries)
            {
                if (e.Kind == "family") families++;
                else if (e.Kind == "failure") failures++;
            }

            if (families < 12 || failures != 4) return false;

            foreach (var pin in PinnedPrefixes)
            {
                var found = false;
                foreach (var e in Entries)
                {
                    if (string.Equals(e.LineFamily, pin, StringComparison.Ordinal) && e.Kind == "family")
                    {
                        found = true;
                        break;
                    }
                }
                if (!found) return false;
            }
            return true;
        }

        /// <summary>以行族字面查找登記項（找不到回 null；供 harness 用字串相等斷言）。</summary>
        internal static TelemetryLine? Find(string lineFamily)
        {
            foreach (var e in Entries)
            {
                if (string.Equals(e.LineFamily, lineFamily, StringComparison.Ordinal)) return e;
            }
            return null;
        }
    }
}
