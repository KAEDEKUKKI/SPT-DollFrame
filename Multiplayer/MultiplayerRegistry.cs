using System;
using System.Collections.Generic;
using EFT;
using UnityEngine;

namespace DollFrame.Multiplayer
{
    /// <summary>
    /// 遠端玩家外觀（角色包名稱）的執行期登錄表。
    /// 純淨無網路框架依賴：FikaBridge 或未來的多人橋接層只需寫入 ProfileId → CharacterName，
    /// BodyReplacer 與 VoiceReplacer 即可統一查詢並套用至對應的 ObservedPlayer / 遠端 Player。
    /// </summary>
    internal static class MultiplayerRegistry
    {
        private static readonly object SyncLock = new object();
        private static readonly Dictionary<string, string> RemoteCharacters =
            new Dictionary<string, string>(StringComparer.Ordinal);
        private static readonly HashSet<string> MissingRemotePkgWarned =
            new HashSet<string>(StringComparer.Ordinal);

        /// <summary>
        /// 記錄或更新遠端玩家的角色選擇，若該玩家已在戰局中生成則立即套用建模與語音。
        /// </summary>
        internal static void SetRemoteCharacter(string profileId, string characterName)
        {
            if (string.IsNullOrEmpty(profileId)) return;

            string normalized = VanillaSelection.IsVanilla(characterName)
                ? string.Empty
                : System.IO.Path.GetFileName(characterName);

            bool changed;
            lock (SyncLock)
            {
                if (RemoteCharacters.TryGetValue(profileId, out var prev) &&
                    string.Equals(prev, normalized, StringComparison.Ordinal))
                {
                    changed = false;
                }
                else
                {
                    RemoteCharacters[profileId] = normalized;
                    changed = true;
                }
            }

            if (changed)
            {
                Plugin.Log.LogInfo(string.IsNullOrEmpty(normalized)
                    ? $"[DollFrame] mp: remote '{profileId}' set to Vanilla (PMC)"
                    : $"[DollFrame] mp: remote '{profileId}' set to '{normalized}'");
            }

            var player = FindPlayerByProfileId(profileId);
            if (player != null && !player.IsYourPlayer)
            {
                BodyReplacer.Reapply(player);
                VoiceReplacer.Apply(player);
            }
        }

        /// <summary>查詢遠端玩家指定的角色包名稱；Vanilla 或未同步則回傳 null。</summary>
        internal static string GetRemoteCharacter(string profileId)
        {
            if (string.IsNullOrEmpty(profileId)) return null;
            lock (SyncLock)
            {
                if (RemoteCharacters.TryGetValue(profileId, out var name) && !string.IsNullOrEmpty(name))
                    return name;
                return null;
            }
        }

        /// <summary>取得目前所有已記錄的遠端角色快照（供 Host 同步給新連線的 Client）。</summary>
        internal static KeyValuePair<string, string>[] SnapshotAll()
        {
            lock (SyncLock)
            {
                var arr = new KeyValuePair<string, string>[RemoteCharacters.Count];
                int i = 0;
                foreach (var kv in RemoteCharacters)
                    arr[i++] = kv;
                return arr;
            }
        }

        internal static void Clear()
        {
            lock (SyncLock)
            {
                RemoteCharacters.Clear();
                MissingRemotePkgWarned.Clear();
            }
        }

        /// <summary>當本機缺少遠端隊友選用的角色包時，每個包名僅記錄一次警告避免洗版。</summary>
        internal static void WarnMissingRemotePackageOnce(string pkgName, string nickname)
        {
            if (string.IsNullOrEmpty(pkgName)) return;
            lock (SyncLock)
            {
                if (!MissingRemotePkgWarned.Add(pkgName)) return;
            }
            Plugin.Log.LogWarning($"[DollFrame] mp: teammate '{nickname}' selected '{pkgName}', but package is not installed locally (keeping vanilla)");
        }

        /// <summary>
        /// 取得指定玩家應套用的 DollFrame 角色包名稱；若應維持 Vanilla (PMC) 則回傳 null。
        /// 本機玩家與遠端隊友共用同一解析契約，不依賴全域單一角色。
        /// </summary>
        internal static string ResolveCharacterForPlayer(Player player)
        {
            if (player == null) return null;

            if (player.IsYourPlayer)
            {
                if (!Plugin.ReplaceBody.Value || !Plugin.ApplyToOwnPlayer.Value) return null;
                var choice = Plugin.Character?.Value;
                if (VanillaSelection.IsVanilla(choice)) return null;
                return System.IO.Path.GetFileName(choice);
            }

            if (Plugin.SyncMultiplayer != null && !Plugin.SyncMultiplayer.Value) return null;
            return GetRemoteCharacter(player.ProfileId);
        }

        /// <summary>
        /// 取得本機玩家欲廣播給隊友的角色名稱（空字串代表 Vanilla (PMC) 或關閉替換）。
        /// </summary>
        internal static string GetLocalBroadcastCharacter()
        {
            if (!Plugin.ReplaceBody.Value || !Plugin.ApplyToOwnPlayer.Value)
                return string.Empty;
            var choice = Plugin.Character?.Value;
            if (VanillaSelection.IsVanilla(choice))
                return string.Empty;
            return System.IO.Path.GetFileName(choice) ?? string.Empty;
        }

        /// <summary>當 Multiplayer 同步設定切換時，即時重新套用所有遠端玩家的外觀與語音。</summary>
        internal static void ReapplyAllRemote()
        {
            try
            {
                foreach (var p in UnityEngine.Object.FindObjectsOfType<Player>())
                {
                    if (p != null && p.gameObject.scene.isLoaded && !p.IsYourPlayer)
                    {
                        BodyReplacer.Reapply(p);
                        VoiceReplacer.Apply(p);
                    }
                }
            }
            catch (Exception e)
            {
                Plugin.Log.LogWarning($"[DollFrame] mp: ReapplyAllRemote failed: {e.Message}");
            }
        }

        internal static Player FindPlayerByProfileId(string profileId)
        {
            if (string.IsNullOrEmpty(profileId)) return null;
            try
            {
                foreach (var p in UnityEngine.Object.FindObjectsOfType<Player>())
                {
                    if (p != null && p.gameObject.scene.isLoaded &&
                        string.Equals(p.ProfileId, profileId, StringComparison.Ordinal))
                        return p;
                }
            }
            catch { }
            return null;
        }

        internal static Player FindPlayerBySpeaker(BaseSpeaker speaker)
        {
            if (speaker == null) return null;
            try
            {
                var main = GamePlayerOwner.MyPlayer;
                if (main != null && main.Speaker == speaker) return main;

                foreach (var p in UnityEngine.Object.FindObjectsOfType<Player>())
                {
                    if (p != null && p.gameObject.scene.isLoaded && p.Speaker == speaker)
                        return p;
                }
            }
            catch { }
            return null;
        }
    }
}
