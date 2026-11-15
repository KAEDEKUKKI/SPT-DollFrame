using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Linq;
using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using EFT;
using HarmonyLib;
using UnityEngine;

namespace DollFrame
{
    /// <summary>
    /// P0 runtime skeleton scaling engine (engine-risk validation phase).
    /// Scales the vanilla Tarkov player skeleton at player-body init so imported
    /// (unchanged) models fit; compensates the movement capsule; refreshes IK.
    /// </summary>
    [BepInPlugin("DollFrame", "DollFrame", "0.4.0")]
    public class Plugin : BaseUnityPlugin
    {
        internal static Plugin Instance;
        internal static ManualLogSource Log;

        internal static ConfigEntry<bool> ApplyToOwnPlayer;
        internal static ConfigEntry<bool> CapsuleScaleEnabled;
        internal static ConfigEntry<float> HeightMultiplier;
        internal static ConfigEntry<bool> ReplaceBody;
        internal static ConfigEntry<string> Character;
        internal static ConfigEntry<float> PmxUnitScale;
        internal static ConfigEntry<float> PmxYOffset;
        internal static ConfigEntry<int> MaxTextureSize;
        internal static ConfigEntry<bool> FlipUv;
        internal static ConfigEntry<bool> HideModelAccessories;
        internal static ConfigEntry<string> HideModelAccessoriesList;
        internal static ConfigEntry<float> PmxYaw;
        internal static ConfigEntry<int> IdleCacheSize;
        internal static ConfigEntry<bool> ProportionScale;
        internal static ConfigEntry<bool> HideHeadInFp;
        internal static ConfigEntry<bool> ArmIkRetarget;
        internal static ConfigEntry<bool> ReplaceVoice;
        internal static ConfigEntry<bool> SecondaryPhysics;
        internal static ConfigEntry<bool> EnableRimLight;
        internal static ConfigEntry<float> RimLightIntensity;
        internal static ConfigEntry<float> RimLightPower;
        internal static ConfigEntry<Color> RimLightColor;
        internal static ConfigEntry<bool> HideArmor;
        internal static ConfigEntry<bool> HideTacticalRig;
        internal static ConfigEntry<bool> HideBackpack;
        internal static ConfigEntry<bool> HideHeadwear;
        internal static ConfigEntry<bool> HideFaceCover;
        internal static ConfigEntry<bool> HideEyewear;
        internal static ConfigEntry<bool> HideEarpiece;
        internal static ConfigEntry<bool> HideArmBand;
        internal static ConfigEntry<KeyCode> SwitchKey;

        /// <summary>Awake 時掃出的角色名單（含 Vanilla (PMC) 置頂）＝選單熱鍵的循環順序。</summary>
        private static string[] _menuOptions = Array.Empty<string>();

        private void Awake()
        {
            Instance = this;
            Log = Logger;

            ApplyToOwnPlayer = Config.Bind("Scaling", "Apply to own player", true,
                "Scale your own body skeleton.");
            CapsuleScaleEnabled = Config.Bind("Capsule", "Scale capsule", true,
                "Scale the character controller capsule (height/center). Unity CharacterController ignores transform scale, this compensates it.");
            HeightMultiplier = Config.Bind("Scaling", "Height multiplier", 1.0f,
                "Multiplier on the model's authored height when deriving the capsule (0 restores the vanilla PMC capsule; 1.0 = capsule follows the model height).");

            ReplaceBody = Config.Bind("Visual", "Replace player body", false,
                "Replace the body with the packaged character (independent per-character rig + model-side proportion scaling). Default OFF: the menu preview and card still work, and the module writes nothing to the player in raids (fully vanilla motion/camera). Turning it on applies PMX character folders from Characters in raids.");
            var modelOptions = DiscoverPackages();
            var defaultModel = modelOptions.FirstOrDefault(o => o.Contains("克萊妲")) ?? modelOptions.FirstOrDefault() ?? "";
            Character = Config.Bind("Visual", "Character", defaultModel, new ConfigDescription("Character to import from BepInEx/plugins/DollFrame/Characters/<folder>. Hidden from this window (switch in-game: the DollFrame row on the menu Overall screen, or the switch key); the value persists here.", modelOptions.Length > 0 ? new AcceptableValueList<string>(modelOptions) : null, new BrowsableAttribute(false)));
            SwitchKey = Config.Bind("Visual", "Switch character key", KeyCode.PageUp,
                "Cycle the Visual/Character selection with a single key press (main menu only - ignored while a raid/hideout session is running, so a switch can never apply mid-session). Replaces opening the config manager (F12) to change characters. New character folders need a game restart to appear in the cycle.");
            _menuOptions = modelOptions;
            if (modelOptions.Length == 0)
                Log.LogWarning($"[DollFrame] no character packages found in '{CharactersDirectory}'; Character falls back to a text field");
            PmxUnitScale = Config.Bind("Visual", "PMX unit scale", 0.078f,
                "Meters per PMX unit (MMD: 1u = 8cm). 0.078 x 20.07u model height = ~1.57m.");
            PmxYOffset = Config.Bind("Visual", "Model Y offset (m)", 0f,
                "Vertical correction if the model floats or sinks into the ground.");
            MaxTextureSize = Config.Bind("Visual", "Max texture size", 2048,
                "Textures larger than this are downscaled on load (VRAM).");
            FlipUv = Config.Bind("Visual", "Flip UV", true,
                "PMX uses DirectX-style V top-down; Unity textures are bottom-up. Turn off if textures appear upside down.");
            HideModelAccessories = Config.Bind("Visual", "Hide model accessories", true,
                "Hide materials of weapons/guns/bags authored into the model (she should carry the in-game weapon instead).");
            HideModelAccessoriesList = Config.Bind("Visual", "Hide accessories name filter", "gun, glock, bag, butt, weapon, silencer",
                "Case-insensitive substrings: a material whose name or texture path contains any is hidden.");
            PmxYaw = Config.Bind("Visual", "Model facing yaw (deg)", 180f,
                "Yaw applied to the model relative to the player's forward. PMX models face -Z, so 180 makes them face forward.");
            IdleCacheSize = Config.Bind("Visual", "Idle cache size", 4,
                new ConfigDescription("Idle parsed-character cache capacity (LRU). Browsing more characters than this re-parses the evicted package on the next switch (a 250-460ms hitch); bigger values keep more models warm at the cost of texture VRAM.", new AcceptableValueRange<int>(2, 24)));
            PmxAssets.SetIdleCap(IdleCacheSize.Value);
ProportionScale = Config.Bind("Visual", "Scale model to game proportions", true,
"模型側全模型單一等比縮放（2026-11-14-6 改約）：Build 時於 rig 根一次性乘入腿錨定等比因子 k（k＝遊戲腿鏈長/模型腿鏈長），全身逐位保持作者原始比例（手指/手臂不變形），腳仍貼齊遊戲 FBAK 腿鏈；遊戲骨架全程 pristine。臂鏈個別縮放已退役（其曾使手臂/手指相對軀幹拉長）。");
            HideHeadInFp = Config.Bind("Visual", "Hide head in first person", true,
                "第一人稱頭部 SMR 切換 ShadowsOnly（防頭部穿透相機並保留地面頭部陰影）；第三人稱／死亡一律完整渲染。");
            ArmIkRetarget = Config.Bind("Visual", "IK arm retarget to game hands", true,
                "臂鏈 IK 重定向（2026-11-14-9）：上臂/前臂改由兩骨 IK 求解，手端沿遊戲肩→掌方向伸展至模型臂長（ArmRatio ≈0.83–0.95）落點——TP 手部自然持槍姿勢。每幀仍僅寫 localRotation（零 localPosition/localScale 寫入，零拉伸契約不變），手腕/十指維持 palmL/R 原複製；胸腔 SDF 防肘入軀幹；死亡/布娃娃自動回退 FK 複製。Build 時讀取，變更需重新掛載（切換角色或重進戰局）。");
            ReplaceVoice = Config.Bind("Visual", "Replace player voice", true,
                "選擇角色建模時自動替換玩家語音（Voices/<角色家族>/）；切回 Vanilla (PMC) 時還原原版語音。");
            SecondaryPhysics = Config.Bind("Visual", "Enable secondary physics", true,
                "啟用角色次要物理引擎（頭髮、裙擺、絲帶、衣襬、胸部彈性），含身體膠囊碰撞防穿模與嚴格零拉伸約束；前瀏海固定不動。");
            EnableRimLight = Config.Bind("Visual", "Enable rim light", true,
                "啟用角色環境自適應邊緣光（Rim Light）與布料／絲襪掠射光澤（DollFrame/CharacterPBR Shader）。");
            RimLightIntensity = Config.Bind("Visual", "Rim light intensity", 0.65f,
                new ConfigDescription("邊緣光強度倍率（0 = 關閉，建議 0.4–1.2）。", new AcceptableValueRange<float>(0f, 2.5f)));
            RimLightPower = Config.Bind("Visual", "Rim light sharpness", 3.2f,
                new ConfigDescription("邊緣光收斂銳利度（越大越貼近輪廓邊緣）。", new AcceptableValueRange<float>(0.8f, 8.0f)));
            RimLightColor = Config.Bind("Visual", "Rim light tint", new Color(0.85f, 0.93f, 1.0f, 1.0f),
                "邊緣光全域色調疊加。");
            EnableRimLight.SettingChanged += (_, __) => ShaderManager.ApplyGlobalRimSettings();
            RimLightIntensity.SettingChanged += (_, __) => ShaderManager.ApplyGlobalRimSettings();
            RimLightPower.SettingChanged += (_, __) => ShaderManager.ApplyGlobalRimSettings();
            RimLightColor.SettingChanged += (_, __) => ShaderManager.ApplyGlobalRimSettings();
            ShaderManager.ApplyGlobalRimSettings();

            // 裝備視覺隱藏（2026-11-07 起承接 EFT.Transmog-cust）：資料級快照清空，效果不變。
            HideArmor = Config.Bind("Equipment", "Hide armor on replaced bodies", true,
                "Hide the PMC armor visual on DollFrame-replaced bodies (menu body, raid body, FP viewmodel, flea card). Real slot equipment and its effects are untouched (data-level snapshot, ported from EFT.Transmog-cust).");
            HideTacticalRig = Config.Bind("Equipment", "Hide tactical rig on replaced bodies", true,
                "Same as hide-armor, for the tactical rig slot.");
            HideBackpack = Config.Bind("Equipment", "Hide backpack on replaced bodies", true,
                "Same as hide-armor, for the backpack slot.");
            HideHeadwear = Config.Bind("Equipment", "Hide headwear on replaced bodies", true,
                "Same as hide-armor, for the headwear slot.");
            HideFaceCover = Config.Bind("Equipment", "Hide face cover on replaced bodies", true,
                "Same as hide-armor, for the face cover slot.");
            HideEyewear = Config.Bind("Equipment", "Hide eyewear on replaced bodies", true,
                "Same as hide-armor, for the eyewear slot. 2026-11-07 flipped to hide per user report (PMC face gear on the card).");
            HideEarpiece = Config.Bind("Equipment", "Hide earpiece on replaced bodies", true,
                "Same as hide-armor, for the earpiece slot.");
            HideArmBand = Config.Bind("Equipment", "Hide armband on replaced bodies", true,
                "Same as hide-armor, for the armband slot.");

            // Each container is patched independently so one failure can't silently abort
            // the rest of Awake (Unity log isn't captured to LogOutput.log in this setup).
            var harmony = new Harmony("DollFrame");
            TryPatch(harmony, typeof(Patches));
            TryPatch(harmony, typeof(CapsulePatches));
            TryPatch(harmony, typeof(BodyReplacer));
            TryPatch(harmony, typeof(VoiceReplacer));
            TryPatch(harmony, typeof(PreviewProxy));
            TryPatch(harmony, typeof(TradingCardIcon));
            TryPatch(harmony, typeof(CharacterSwitchUI));
            var count = 0;
            foreach (var m in harmony.GetPatchedMethods()) count++;
            Log.LogInfo($"DollFrame loaded (Replace player body={ReplaceBody.Value}, {count} patched methods, {modelOptions.Length} character models)");
        }

        private static float _lastBlockedLog;

        private void Update()
        {
            try
            {
                var key = SwitchKey?.Value ?? KeyCode.None;
                if (key == KeyCode.None || !Input.GetKeyDown(key)) return;
                if (!CanSwitchInMenu())
                {
                    if (Time.unscaledTime - _lastBlockedLog > 2f)
                    {
                        _lastBlockedLog = Time.unscaledTime;
                        Log.LogInfo("[DollFrame] character switch key ignored outside the main menu (needs the menu preview up and no raid/hideout session)");
                    }
                    return;
                }
                var opts = _menuOptions.Length > 0 ? _menuOptions : DiscoverPackages();
                if (opts.Length == 0) return;
                var prev = Character?.Value;
                var idx = Array.IndexOf(opts, prev);
                Character.Value = opts[(idx + 1) % opts.Length];
                Log.LogInfo($"[DollFrame] character switch -> '{Character.Value}'");
                NotifyCharacterChanged();
            }
            catch (Exception e)
            {
                Log.LogError($"[DollFrame] character switch failed: {e}");
            }
        }

        internal static void NotifyCharacterChanged()
        {
            try
            {
                Instance?.Config?.Save();
                PreviewProxyDriver.NotifyAllCharacterChanged();
                foreach (var p in Resources.FindObjectsOfTypeAll<Player>())
                {
                    if (p == null || !p.gameObject.scene.isLoaded) continue;
                    if (p.IsYourPlayer)
                    {
                        BodyReplacer.Reapply(p);
                    }
                }
            }
            catch (Exception e)
            {
                Log.LogWarning($"[DollFrame] character change sync failed: {e.Message}");
            }
        }

        /// <summary>主選單判準：存在活著的 menu 預覽 driver（PlayerModelView 在場），且不在戰局中。</summary>
        private static bool CanSwitchInMenu()
        {
            if (!PreviewProxyDriver.HasLiveView) return false;
            // 藏身處 HideoutPlayer 留在後台記憶體不應阻礙主選單切換；僅在進入正式戰局且有非藏身處玩家時阻擋
            foreach (var br in UnityEngine.Object.FindObjectsOfType<BodyReplacer>())
            {
                var p = br._player;
                if (p != null && p.IsYourPlayer && !(p is HideoutPlayer))
                    return false;
            }
            return true;
        }

        /// <summary>Primary character folder next to DollFrame.dll.</summary>
        internal static readonly string CharactersDirectory = Path.Combine(
            Path.GetDirectoryName(typeof(Plugin).Assembly.Location), "Characters");

        private static readonly string[] CharacterFolderNames = { "Characters", "DollFrame_Characters" };

        /// <summary>Enumerate all character package directories (own plugin folder + sibling content packs under BepInEx/plugins).</summary>
        internal static IEnumerable<string> GetCharacterDirectories()
        {
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var selfDir = Path.GetDirectoryName(typeof(Plugin).Assembly.Location);
            if (!string.IsNullOrEmpty(selfDir))
            {
                foreach (var sub in CharacterFolderNames)
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
                        foreach (var sub in CharacterFolderNames)
                        {
                            var d = Path.Combine(sibling, sub);
                            if (Directory.Exists(d) && seen.Add(Path.GetFullPath(d)))
                                yield return d;
                        }
                    }
                }
            }
        }

        /// <summary>Display name (folder name containing .pmx) per character, with Vanilla (PMC) prepended.</summary>
        private static string[] DiscoverPackages()
        {
            try
            {
                var pkgs = GetCharacterDirectories()
                    .SelectMany(d => PmxContainer.EnumeratePackages(d))
                    .Select(Path.GetFileName)
                    .Distinct(StringComparer.Ordinal)
                    .OrderBy(n => n, StringComparer.Ordinal);
                return VanillaSelection.PrependVanilla(pkgs);
            }
            catch (Exception e)
            {
                Log.LogWarning($"[DollFrame] character package scan failed: {e.Message}");
                return new[] { VanillaSelection.VanillaPackageName };
            }
        }

        /// <summary>Menu switch UI 的即時重掃（每次畫面 Show／下拉展開都重掃，新角色資料夾免重啟即出現）。</summary>
        internal static string[] MenuPackageNames() => DiscoverPackages();

        /// <summary>Full character folder path for the selected character, or null when unset/missing/vanilla.</summary>
        internal static string ResolvePackagePath()
        {
            var choice = Character?.Value;
            if (VanillaSelection.IsVanilla(choice)) return null;
            var name = Path.GetFileName(choice);
            foreach (var dir in GetCharacterDirectories())
            {
                var charDir = Path.Combine(dir, name);
                if (Directory.Exists(charDir) && PmxContainer.FindPmxInDirectory(charDir) != null)
                    return charDir;
            }
            return null;
        }

        private static void TryPatch(Harmony harmony, System.Type container)
        {
            try
            {
                harmony.PatchAll(container);
            }
            catch (System.Exception e)
            {
                Log.LogError($"[DollFrame] patching {container.Name} failed: {e}");
            }
        }
    }
}