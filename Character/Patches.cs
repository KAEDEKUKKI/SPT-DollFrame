using EFT;
using HarmonyLib;

namespace DollFrame
{
    /// <summary>Player visibility guard for the body-replacement route.</summary>
    internal static class Patches
    {
        internal static bool ShouldScale(Player player)
        {
            if (player == null) return false;
            if (!player.IsYourPlayer) return false;
            return Plugin.ApplyToOwnPlayer.Value;
        }

        [HarmonyPrefix]
        [HarmonyPatch(typeof(Player), "InitVisuals")]
        internal static void PreInitVisuals(Player __instance)
        {
            if (!Plugin.ReplaceBody.Value || !ShouldScale(__instance)) return;
            if (VanillaSelection.IsVanilla(Plugin.Character?.Value)) return;
            try
            {
                BodyReplacer.PreHideVanilla(__instance);
            }
            catch (System.Exception e)
            {
                Plugin.Log.LogWarning($"PreInitVisuals step failed on {__instance?.name}: {e.Message}");
            }
        }

        [HarmonyPostfix]
        [HarmonyPatch(typeof(Player), "InitVisuals")]
        internal static void PostInitVisuals(Player __instance)
        {
            try
            {
                BodyReplacer.Apply(__instance);
            }
            catch (System.Exception e)
            {
                Plugin.Log.LogError($"InitVisuals step failed on {__instance?.name}: {e}");
            }
        }

        /// <summary>
        /// 藏身處 FP 身體動畫機復活（2026-11-14-10，層 1 狀態治理；TelemetryRegistry
        /// 'hideout body animator:' 單一生產者）：原版 HideoutPlayerOwner.SetPointOfView 在
        /// FirstPersonMode 時把 BodyAnimatorCommon 整個停用（省效能；原版玩家在 FP 看不到
        /// 自己的 TP 身體故無人察覺骨骼凍結），被 DollFrame 替換後 FreeCam／影子仍讀取該 TP 骨架，
        /// 凍結骨架會被 rig 忠實鏡射成卡死姿勢。恢復 enabled=true 讓空手待機持續播放、
        /// rig 鏡射自然跟擺；無 BodyReplacer（未替換）者完全維持遊戲原生行為。
        /// </summary>
        [HarmonyPostfix]
        [HarmonyPatch(typeof(HideoutPlayerOwner), "SetPointOfView")]
        internal static void PostHideoutSetPointOfView(HideoutPlayerOwner __instance)
        {
            if (!Plugin.ReplaceBody.Value) return;
            if (VanillaSelection.IsVanilla(Plugin.Character?.Value)) return;
            try
            {
                var player = __instance?.Player;
                if (player == null || player.gameObject == null) return;
                if (player.gameObject.GetComponent<BodyReplacer>() == null) return;
                var body = player.BodyAnimatorCommon;
                if (body != null && !body.enabled)
                {
                    body.enabled = true;
                    Plugin.Log.LogInfo("[DollFrame] hideout body animator: re-enabled (replaced TP body keeps idle source alive)");
                }
            }
            catch (System.Exception e)
            {
                Plugin.Log.LogWarning($"PostHideoutSetPointOfView step failed: {e.Message}");
            }
        }
    }
}
