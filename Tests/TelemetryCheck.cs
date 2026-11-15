using System;

namespace GF2.Tests
{
    /// <summary>
    /// `telemetry` 子命令：TelemetryRegistry 登記表完整性斷言。
    /// 以字串相等逐條核對 check-log 釘的 6 個固定前綴與其 grep 核實的生產者
    /// （2026-11-14 架構反轉：6 釘點 / 4 失敗標記 / 15 harness 子命令）。
    /// </summary>
    public static class TelemetryCheck
    {
        public static int Run()
        {
            if (!TelemetryRegistry.VerifyCompleteness())
            {
                Console.Error.WriteLine("telemetry: VerifyCompleteness failed (expect families >= 12, failure markers == 4, all 6 pinned prefixes registered)");
                return 1;
            }

            // 6 個 pinned prefix 逐字斷言，每條寫明登記表內對應誰（producer 以 grep 核實）。
            var pins = new (string pin, string producer, string harness)[]
            {
                ("contract:", "GameBones", "constants"),                     // GameBones.ContractLine
                ("rig: bones=", "CharacterRig", "—"),                        // CharacterRig.Build
                ("body replaced on '", "BodyReplacer", "vanilla"),           // BodyReplacer.Apply
                ("preload:", "BodyReplacer", "cache"),                       // preload 路徑（BodyReplacer.cs）
                ("cache: evicted", "PmxRuntime", "cache"),                   // LRU 逐出（PmxRuntime.cs）
                ("scale:", "ModelScaler", "proportions"),                    // ModelScaler.ScaleLine ← CharacterRig.Build
            };

            foreach (var (pin, producer, harness) in pins)
            {
                var entry = TelemetryRegistry.Find(pin);
                if (entry == null)
                {
                    Console.Error.WriteLine($"telemetry: pinned prefix '{pin}' not registered");
                    return 1;
                }
                if (!entry.Value.Producer.Contains(producer, StringComparison.Ordinal))
                {
                    Console.Error.WriteLine($"telemetry: '{pin}' producer '{entry.Value.Producer}' does not contain expected class '{producer}'");
                    return 1;
                }
                if (!string.Equals(entry.Value.HarnessPin, harness, StringComparison.Ordinal))
                {
                    Console.Error.WriteLine($"telemetry: '{pin}' harness pin '{entry.Value.HarnessPin}' != expected '{harness}'");
                    return 1;
                }
                if (!string.Equals(entry.Value.LineFamily, pin, StringComparison.Ordinal))
                {
                    Console.Error.WriteLine($"telemetry: '{pin}' line family text drifted");
                    return 1;
                }
            }

            // 0.3.9 臂鏈 IK 行族：生產者 CharacterRig.SetupArmRest（attach）＋EmitIkTargetLine（節流），harness pin ik
            var ikEntry = TelemetryRegistry.Find("ik:");
            if (ikEntry == null)
            {
                Console.Error.WriteLine("telemetry: 'ik:' family not registered");
                return 1;
            }
            if (!ikEntry.Value.Producer.Contains("CharacterRig", StringComparison.Ordinal)
                || !string.Equals(ikEntry.Value.HarnessPin, "ik", StringComparison.Ordinal))
            {
                Console.Error.WriteLine($"telemetry: 'ik:' producer '{ikEntry.Value.Producer}' / harness pin '{ikEntry.Value.HarnessPin}' drifted");
                return 1;
            }

            var families = 0;
            var failures = 0;
            foreach (var e in TelemetryRegistry.Entries)
            {
                if (e.Kind == "family") families++;
                else if (e.Kind == "failure") failures++;
            }
            if (failures != 4)
            {
                Console.Error.WriteLine($"telemetry: expected 4 failure marker entries, got {failures}");
                return 1;
            }

            // 驗證離線 harness 15 個子命令註冊完整性（2026-11-14 架構反轉＋0.3.9 臂鏈 IK ik 子命令)
            var commandNames = new System.Collections.Generic.HashSet<string>(Program.GetCommandNames(), StringComparer.Ordinal);
            if (commandNames.Count != 15)
            {
                Console.Error.WriteLine($"telemetry: expected exactly 15 harness subcommands, got {commandNames.Count}");
                return 1;
            }

            var modularSubcommands = new[]
            {
                "profile", "split", "smrmask", "proportions", "telemetry", "ik"
            };
            foreach (var subcmd in modularSubcommands)
            {
                if (!commandNames.Contains(subcmd))
                {
                    Console.Error.WriteLine($"telemetry: modular architecture subcommand '{subcmd}' missing from harness registry");
                    return 1;
                }
            }

            // 驗證 TelemetryRegistry 的 harness pins 皆能對應到有效子命令或已知外部釘點
            foreach (var e in TelemetryRegistry.Entries)
            {
                if (e.HarnessPin != "—" && e.HarnessPin != "check-log" && !commandNames.Contains(e.HarnessPin))
                {
                    Console.Error.WriteLine($"telemetry: registry entry '{e.LineFamily}' references unknown harness pin '{e.HarnessPin}'");
                    return 1;
                }
            }

            Console.WriteLine($"telemetry: OK ({families} line families + {failures} failure markers = {families + failures} entries, 6 pinned prefixes verified, {commandNames.Count} harness subcommands confirmed)");
            return 0;
        }
    }
}
