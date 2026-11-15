using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.CompilerServices;
using EFT;
using HarmonyLib;
using UnityEngine;

namespace DollFrame
{
    /// <summary>
    /// 玩家角色語音自動替換器：
    /// 依目前選擇的角色套件（Plugin.Character）自動匹配 DollFrame_Voices/&lt;角色家族&gt;/ 下的
    /// 各 EPhraseTrigger 資料夾（.wav），於玩家掛載／切換角色／BaseSpeaker.Init 時
    /// 替換 player.Speaker.PhrasesBanks 的 AudioClip；切回 Vanilla (PMC) 時完整還原原版語音。
    /// </summary>
    [HarmonyPatch]
    internal static class VoiceReplacer
    {
        internal static readonly string VoicesDirectory = Path.Combine(
            Path.GetDirectoryName(typeof(Plugin).Assembly.Location), "Voices");

        private static readonly ConditionalWeakTable<BaseSpeaker, Dictionary<TagBank, (AudioClip[] clips, float[] lengths)>>
            OriginalSnapshots = new ConditionalWeakTable<BaseSpeaker, Dictionary<TagBank, (AudioClip[], float[])>>();

        private static readonly Dictionary<string, string[]> TriggerAliases =
            new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase)
            {
                // 語音包多以 USEC TagBank 資產名分類；BEAR 等不同命名語音包需透過 EPhraseTrigger 別名匹配
                { "OnFight", new[] { "Fight", "OnEnemyConversation", "OnFirstContact" } },
                { "OnMutter", new[] { "Mutter", "OnBreath" } },
                { "HandBroken", new[] { "BrokenHand" } },
                { "LegBroken", new[] { "BrokenLeg" } },
                { "NoisePhrase", new[] { "Noise" } },
                { "SniperPhrase", new[] { "Sniper" } },
                { "OnWeaponJammed", new[] { "WeaponJammed", "WeaponBroken" } },
                { "Look", new[] { "Attention" } },
                // 較新版 EPhraseTrigger 若語音包未獨立收錄，對應至同語意銀行避免漏回原版男聲
                { "OnWeaponMisfired", new[] { "WeaponJammed", "OnWeaponJammed", "WeaponBroken" } },
                { "UnderFire", new[] { "OnBeingHurt", "GetInCover", "Suppress" } },
                { "StartHeal", new[] { "NeedMedkit" } },
                { "LocateHostiles", new[] { "OnFirstContact", "OnEnemyConversation", "Attention" } },
                { "Greetings", new[] { "Cooperation", "Ready" } },
                { "Toxic", new[] { "OnFight", "Fight", "BadWork" } },
                { "Provocation", new[] { "OnFight", "Fight" } },
                { "LowKarmaAttack", new[] { "OnFight", "Fight" } },
                { "Warning", new[] { "Attention", "HoldFire" } },
                { "Mine", new[] { "Attention", "OnGrenade" } },
                { "Frozen", new[] { "Attention", "Bleeding" } },
                { "OnSwitchToMeleeWeapon", new[] { "KnifesOnly", "KnifeKill" } },
            };

        private static string _cachedFamily;
        private static Dictionary<string, AudioClip[]> _cachedClips;
        private static string _lastMissingWarnFamily;

        [HarmonyPostfix]
        [HarmonyPatch(typeof(BaseSpeaker), nameof(BaseSpeaker.Init))]
        internal static void PostSpeakerInit(BaseSpeaker __instance)
        {
            ApplyToSpeaker(__instance, "PostSpeakerInit");
        }

        [HarmonyPostfix]
        [HarmonyPatch(typeof(BaseSpeaker), nameof(BaseSpeaker.ReplaceVoice))]
        internal static void PostSpeakerReplaceVoice(BaseSpeaker __instance)
        {
            ApplyToSpeaker(__instance, "PostSpeakerReplaceVoice");
        }

        private static void ApplyToSpeaker(BaseSpeaker speaker, string caller)
        {
            if (speaker == null || !Plugin.ReplaceBody.Value || !Plugin.ReplaceVoice.Value) return;
            if (VanillaSelection.IsVanilla(Plugin.Character?.Value)) return;
            try
            {
                var main = GamePlayerOwner.MyPlayer;
                if (main != null)
                {
                    if (main.IsYourPlayer && main.Speaker == speaker)
                        Apply(main);
                    return;
                }
                foreach (var p in UnityEngine.Object.FindObjectsOfType<Player>())
                {
                    if (p != null && p.IsYourPlayer && p.Speaker == speaker)
                    {
                        Apply(p);
                        return;
                    }
                }
            }
            catch (Exception e)
            {
                Plugin.Log.LogWarning($"[DollFrame] voice {caller} failed: {e.Message}");
            }
        }

        internal static void Apply(Player player)
        {
            if (player == null || !player.IsYourPlayer) return;
            if (!Plugin.ReplaceBody.Value || !Plugin.ReplaceVoice.Value) return;
            if (VanillaSelection.IsVanilla(Plugin.Character?.Value))
            {
                Restore(player);
                return;
            }

            var speaker = player.Speaker;
            if (speaker == null || speaker.PhrasesBanks == null || speaker.PhrasesBanks.Count == 0) return;

            var pkgName = Path.GetFileName(Plugin.Character?.Value);
            var voiceDir = ResolveVoiceDir(pkgName);
            if (voiceDir == null)
            {
                if (!string.IsNullOrEmpty(pkgName) && _lastMissingWarnFamily != pkgName)
                {
                    _lastMissingWarnFamily = pkgName;
                    Plugin.Log.LogInfo($"[DollFrame] voice: no Voices pack for '{pkgName}', keeping vanilla voice");
                }
                return;
            }

            var family = Path.GetFileNameWithoutExtension(voiceDir);
            var bankMap = GetOrLoadFamilyClips(family, voiceDir);
            if (bankMap == null || bankMap.Count == 0) return;

            SnapshotOriginals(speaker);

            int replacedBanks = 0;
            int replacedClips = 0;
            List<string> unmatched = null;
            foreach (var kv in speaker.PhrasesBanks)
            {
                var bank = kv.Value;
                if (bank == null || bank.Clips == null || bank.Clips.Length == 0) continue;

                var customClips = ResolveBankClips(bankMap, kv.Key, bank);
                if (customClips == null || customClips.Length == 0)
                {
                    (unmatched ??= new List<string>()).Add(kv.Key.ToString());
                    continue;
                }

                for (int i = 0; i < bank.Clips.Length; i++)
                {
                    var tc = bank.Clips[i];
                    if (tc == null) continue;
                    var clip = customClips[i % customClips.Length];
                    tc.Clip = clip;
                    tc.Length = clip.length;
                    replacedClips++;
                }
                bank.UpdateClips();
                replacedBanks++;
            }

            string unmatchedInfo = unmatched != null ? $" (unmatched={unmatched.Count}: {string.Join(",", unmatched)})" : string.Empty;
            Plugin.Log.LogInfo($"[DollFrame] voice: applied '{family}' ({replacedBanks} banks, {replacedClips} clip slots){unmatchedInfo}");
        }

        private static AudioClip[] ResolveBankClips(Dictionary<string, AudioClip[]> bankMap, EPhraseTrigger trigger, TagBank bank)
        {
            string trigName = trigger.ToString();
            if (bankMap.TryGetValue(trigName, out var clips) && clips != null && clips.Length > 0)
                return clips;
            if (!string.IsNullOrEmpty(bank.name) && bankMap.TryGetValue(bank.name, out clips) && clips != null && clips.Length > 0)
                return clips;
            if (TriggerAliases.TryGetValue(trigName, out var aliases))
            {
                for (int i = 0; i < aliases.Length; i++)
                {
                    if (bankMap.TryGetValue(aliases[i], out clips) && clips != null && clips.Length > 0)
                        return clips;
                }
            }
            return null;
        }

        internal static void Restore(Player player)
        {
            if (player == null || player.Speaker == null) return;
            var speaker = player.Speaker;
            if (!OriginalSnapshots.TryGetValue(speaker, out var snap) || snap == null) return;

            int restored = 0;
            foreach (var kv in snap)
            {
                var bank = kv.Key;
                if (bank == null || bank.Clips == null) continue;
                var (origClips, origLens) = kv.Value;
                int n = Math.Min(bank.Clips.Length, origClips.Length);
                for (int i = 0; i < n; i++)
                {
                    if (bank.Clips[i] == null) continue;
                    bank.Clips[i].Clip = origClips[i];
                    bank.Clips[i].Length = origLens[i];
                }
                bank.UpdateClips();
                restored++;
            }
            if (restored > 0)
                Plugin.Log.LogInfo($"[DollFrame] voice: restored vanilla ({restored} banks)");
        }

        private static void SnapshotOriginals(BaseSpeaker speaker)
        {
            if (OriginalSnapshots.TryGetValue(speaker, out _)) return;
            var snap = new Dictionary<TagBank, (AudioClip[], float[])>();
            foreach (var kv in speaker.PhrasesBanks)
            {
                var bank = kv.Value;
                if (bank == null || bank.Clips == null) continue;
                var clips = new AudioClip[bank.Clips.Length];
                var lens = new float[bank.Clips.Length];
                for (int i = 0; i < bank.Clips.Length; i++)
                {
                    if (bank.Clips[i] != null)
                    {
                        clips[i] = bank.Clips[i].Clip;
                        lens[i] = bank.Clips[i].Length;
                    }
                }
                snap[bank] = (clips, lens);
            }
            OriginalSnapshots.Add(speaker, snap);
        }

        private static readonly string[] VoiceFolderNames = { "Voices", "DollFrame_Voices" };

        private static IEnumerable<string> GetVoiceDirectories()
        {
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var selfDir = Path.GetDirectoryName(typeof(Plugin).Assembly.Location);
            if (string.IsNullOrEmpty(selfDir)) yield break;
            foreach (var sub in VoiceFolderNames)
            {
                var d = Path.Combine(selfDir, sub);
                if (Directory.Exists(d) && seen.Add(Path.GetFullPath(d)))
                    yield return d;
            }
            var pluginsRoot = Path.GetDirectoryName(selfDir);
            if (!string.IsNullOrEmpty(pluginsRoot) && Directory.Exists(pluginsRoot))
            {
                foreach (var sibling in Directory.GetDirectories(pluginsRoot))
                {
                    foreach (var sub in VoiceFolderNames)
                    {
                        var d = Path.Combine(sibling, sub);
                        if (Directory.Exists(d) && seen.Add(Path.GetFullPath(d)))
                            yield return d;
                    }
                }
            }
        }

        private static string ResolveVoiceDir(string pkgName)
        {
            if (string.IsNullOrEmpty(pkgName)) return null;

            string best = null;
            int bestLen = 0;
            foreach (var voiceRoot in GetVoiceDirectories())
            {
                var exactDir = Path.Combine(voiceRoot, pkgName);
                if (Directory.Exists(exactDir)) return exactDir;

                foreach (var dir in Directory.GetDirectories(voiceRoot))
                {
                    var name = Path.GetFileName(dir);
                    if (!string.IsNullOrEmpty(name) && pkgName.StartsWith(name, StringComparison.Ordinal) && name.Length > bestLen)
                    {
                        best = dir;
                        bestLen = name.Length;
                    }
                }
            }
            return best;
        }

        private static Dictionary<string, AudioClip[]> GetOrLoadFamilyClips(string family, string voiceSource)
        {
            if (string.Equals(_cachedFamily, family, StringComparison.Ordinal) && _cachedClips != null)
                return _cachedClips;

            ClearCache();
            var map = LoadDirectory(family, voiceSource);

            _cachedFamily = family;
            _cachedClips = map;
            return map;
        }

        private static Dictionary<string, AudioClip[]> LoadDirectory(string family, string voiceDir)
        {
            var map = new Dictionary<string, AudioClip[]>(StringComparer.OrdinalIgnoreCase);
            foreach (var bankDir in Directory.GetDirectories(voiceDir))
            {
                var bankName = Path.GetFileName(bankDir);
                var files = Directory.GetFiles(bankDir, "*.wav");
                if (files.Length == 0) continue;
                Array.Sort(files, StringComparer.OrdinalIgnoreCase);
                var list = new List<AudioClip>(files.Length);
                foreach (var f in files)
                {
                    var clip = LoadWavBytes(File.ReadAllBytes(f), $"DollFrame_{family}_{bankName}_{Path.GetFileNameWithoutExtension(f)}");
                    if (clip != null) list.Add(clip);
                }
                if (list.Count > 0) map[bankName] = list.ToArray();
            }
            return map;
        }

        private static void ClearCache()
        {
            if (_cachedClips != null)
            {
                var destroyed = new HashSet<AudioClip>();
                foreach (var kv in _cachedClips)
                {
                    if (kv.Value == null) continue;
                    foreach (var c in kv.Value)
                        if (c != null && destroyed.Add(c)) UnityEngine.Object.Destroy(c);
                }
                _cachedClips.Clear();
            }
            _cachedFamily = null;
            _cachedClips = null;
        }

        private static AudioClip LoadWavBytes(byte[] bytes, string clipName)
        {
            try
            {
                if (bytes == null || bytes.Length < 44) return null;
                if (bytes[0] != 'R' || bytes[1] != 'I' || bytes[2] != 'F' || bytes[3] != 'F') return null;

                int pos = 12;
                int channels = 1;
                int sampleRate = 24000;
                int bitsPerSample = 16;
                int audioFormat = 1;
                int dataOffset = -1;
                int dataSize = 0;

                while (pos + 8 <= bytes.Length)
                {
                    string chunkId = System.Text.Encoding.ASCII.GetString(bytes, pos, 4);
                    int chunkSize = BitConverter.ToInt32(bytes, pos + 4);
                    pos += 8;
                    if (chunkSize < 0 || pos + chunkSize > bytes.Length) break;

                    if (chunkId == "fmt " && chunkSize >= 16)
                    {
                        audioFormat = BitConverter.ToInt16(bytes, pos);
                        channels = BitConverter.ToInt16(bytes, pos + 2);
                        sampleRate = BitConverter.ToInt32(bytes, pos + 4);
                        bitsPerSample = BitConverter.ToInt16(bytes, pos + 14);
                    }
                    else if (chunkId == "data")
                    {
                        dataOffset = pos;
                        dataSize = chunkSize;
                        break;
                    }
                    pos += chunkSize + (chunkSize & 1);
                }

                if (dataOffset < 0 || dataSize <= 0 || channels <= 0 || sampleRate <= 0) return null;

                int bytesPerSample = bitsPerSample / 8;
                if (bytesPerSample <= 0) return null;
                int totalSamples = dataSize / bytesPerSample;
                int frames = totalSamples / channels;
                if (frames <= 0) return null;

                var samples = new float[totalSamples];
                if (audioFormat == 1 && bitsPerSample == 16)
                {
                    for (int i = 0; i < totalSamples; i++)
                        samples[i] = BitConverter.ToInt16(bytes, dataOffset + i * 2) / 32768f;
                }
                else if (audioFormat == 3 && bitsPerSample == 32)
                {
                    for (int i = 0; i < totalSamples; i++)
                        samples[i] = BitConverter.ToSingle(bytes, dataOffset + i * 4);
                }
                else
                {
                    return null;
                }

                var clip = AudioClip.Create(clipName, frames, channels, sampleRate, false);
                clip.SetData(samples, 0);
                clip.hideFlags = HideFlags.DontUnloadUnusedAsset;
                return clip;
            }
            catch (Exception e)
            {
                Plugin.Log.LogWarning($"[DollFrame] voice wav load failed '{clipName}': {e.Message}");
                return null;
            }
        }
    }
}
