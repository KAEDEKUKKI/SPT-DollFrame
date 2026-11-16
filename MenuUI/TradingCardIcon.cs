using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using EFT;
using EFT.UI;
using HarmonyLib;
using UnityEngine;
using UnityEngine.UI;
using PlayerIcons;

namespace DollFrame
{
    /// <summary>
    /// 跳蚤市場卡片頭像替換（TradingPlayerPanel，"You in trading" 那張卡）。
    /// 反編譯實證（Assembly-CSharp 0.16.9.5）：原版路徑 PlayerIcons.PlayerIconImage
    /// .SetPresetIcon(Profile) → PlayerIconCreator.GetIcon → PlayerIconCreatorModelView
    /// 自行 Instantiate "menuplayer.bundle"＋直接 PlayerBody.Init（不經 Player.InitVisuals、
    /// 不經 EFT.UI.PlayerModelView——DollFrame 的兩個 hook 都管不到）；成品 sprite 存進記憶體 hash
    /// cache 與磁碟 PlayerIcons cache（跨 session 重用）→ 卡片頭像永遠是預設 PMC。
    /// 該 overload 的唯一外部呼叫點就是這張卡（反編譯全庫僅 TradingPlayerPanel.Set 呼叫）。
    /// 修法：prefix 直接覆寫整個呼叫（回傳 false，原版 icon 流程完全隔離），卡片頭像先空白，
    /// 再以 PlayerModelLoader（選單 preview 同款載入路徑、共用 PmxAssets cache）起一次性渲染：
    /// Root_Joint 下建獨立 rig（CharacterRig，模型側等比縮放）、裝備走 EquipmentHide 的資料級
    /// 快照（Init prefix 已清空武器與指定槽位），CardIconDriver 常駐補隱非 DollFrame_ renderers
    /// （watch／LOD 換裝／遲生成件），2 幀穩定後 rig.Drive()＋BSG 自家 icon 相機
    /// （PlayerIconCreator/PlayerIconCamera prefab＋PlayerIconCreatorSettings 構圖，取不到
    /// 才落回自有 fallback）拍 ARGB RenderTexture 成 sprite。sprite 以 .gfc 路徑為鍵做靜態
    /// 快取：同套件再開卡瞬間套用、不重渲染；換角色換鍵重渲染。失敗隔離：任何例外只留空白
    /// 頭像＋每 session 一次警告（例外訊息可能帶入契約失敗字樣，只記型別名），絕不上拋、不重試。
    /// </summary>
    [HarmonyPatch]
    internal static class TradingCardIcon
    {
        private static bool _typeFailLogged;
        private static bool _warned;

        internal sealed class CardSprite
        {
            internal Sprite Sprite;
            internal Texture2D Texture;
        }

        // sprite cache key = .gfc 完整路徑（換角色即換鍵重渲染）；渲染與續行全在主執行緒
        // （Unity sync context 流經 await），無需鎖。
        private static readonly Dictionary<string, CardSprite> _cache =
            new Dictionary<string, CardSprite>(StringComparer.Ordinal);

        private static string BuildCacheKey(string pkgPath)
        {
            int mask = 0;
            if (Plugin.HideArmor?.Value == true) mask |= 1 << 0;
            if (Plugin.HideTacticalRig?.Value == true) mask |= 1 << 1;
            if (Plugin.HideBackpack?.Value == true) mask |= 1 << 2;
            if (Plugin.HideHeadwear?.Value == true) mask |= 1 << 3;
            if (Plugin.HideFaceCover?.Value == true) mask |= 1 << 4;
            if (Plugin.HideEyewear?.Value == true) mask |= 1 << 5;
            if (Plugin.HideEarpiece?.Value == true) mask |= 1 << 6;
            if (Plugin.HideArmBand?.Value == true) mask |= 1 << 7;
            if (Plugin.HideModelAccessories?.Value == true) mask |= 1 << 8;
            int maxTex = Plugin.MaxTextureSize?.Value ?? 0;
            return $"{pkgPath}|m{mask}|t{maxTex}";
        }

        private static IEnumerable<MethodBase> TargetMethods()
        {
            var t = AccessTools.TypeByName("PlayerIcons.PlayerIconImage") ?? AccessTools.TypeByName("PlayerIconImage");
            if (t == null)
            {
                if (!_typeFailLogged)
                {
                    _typeFailLogged = true;
                    Plugin.Log.LogWarning("[DollFrame] PlayerIconImage type not found; trading card icon hook disabled");
                }
                yield break;
            }
            var m = AccessTools.DeclaredMethod(t, "SetPresetIcon", new[] { typeof(EFT.Profile) });
            if (m == null)
            {
                if (!_typeFailLogged)
                {
                    _typeFailLogged = true;
                    Plugin.Log.LogWarning("[DollFrame] PlayerIconImage.SetPresetIcon(Profile) not found; trading card icon hook disabled");
                }
                yield break;
            }
            yield return m;
        }

        [HarmonyPrefix]
        internal static bool PreSetIcon(PlayerIconImage __instance, EFT.Profile profile)
        {
            if (profile == null) return true;               // 原 null 分支照走（空白頭像）
            if (!Plugin.ReplaceBody.Value) return true;
            if (VanillaSelection.IsVanilla(Plugin.Character?.Value)) return true;

            if (__instance._pmcPreview == null) return true;
            var pkgPath = Plugin.ResolvePackagePath();
            if (string.IsNullOrEmpty(pkgPath))
            {
                if (!_warned)
                {
                    _warned = true;
                    Plugin.Log.LogWarning("[DollFrame] card icon: no character package selected - vanilla PMC card kept");
                }
                return true;                                 // 沒角色可掛 → 原版卡片
            }

            if (__instance._progress != null) __instance._progress.SetActive(false);
            var cacheKey = BuildCacheKey(pkgPath);
            if (_cache.TryGetValue(cacheKey, out var cached))
            {
                if (cached != null && cached.Sprite != null && cached.Texture != null)
                {
                    __instance._pmcPreview.sprite = cached.Sprite;  // 同套件：瞬間套用，不重渲染
                    __instance._pmcPreview.enabled = true;
                    return false;
                }
                _cache.Remove(cacheKey);
            }

            __instance._pmcPreview.enabled = false;          // 渲染完成前保持空白，避免 PMC 閃現
            _ = RenderCardSpriteAsync(pkgPath, profile, __instance._pmcPreview);
            return false;                                    // 覆寫原版卡片 icon 供給
        }

        private static async Task RenderCardSpriteAsync(string pkgPath, EFT.Profile profile, Image preview)
        {
            PlayerModelLoader loader = null;
            GameObject host = null;
            string pkgName = null;
            PlayerIconCreatorSettings fallbackSettings = null;
            try
            {
                var sw = Stopwatch.StartNew();
                host = new GameObject("DollFrame_card_icon_host");
                loader = new PlayerModelLoader();
                // BSG 的 icon layer（與 PlayerIconCreatorModelView.Show 相同）；NameToLayer
                // 取不到（層名缺失）時退 0，camera mask 仍以 rootJoint 事後的實際 layer 推導
                var previewLayer = LayerMask.NameToLayer("Weapon Preview");
                if (previewLayer < 0) previewLayer = 0;
                var visual = profile.GetVisualEquipmentState(firstLevelItemsOnly: false);
                if (visual.Equipment == null) throw new InvalidOperationException("profile visual equipment absent");
                // 裝備＝EquipmentHide 的 Init prefix 資料級清空（含武器槽，見該檔）
                await loader.Load(visual, null, 0f, ThirdPersonCustomizationFilter.Default, null,
                    CancellationToken.None, Vector3.zero, host.transform,
                    previewLayer, animateWeapon: false);
                if (loader.PlayerBody == null) throw new InvalidOperationException("no PlayerBody after load");
                var modelGo = loader.PlayerBody.gameObject;

                var rootJoint = FindRootJoint(modelGo.transform);
                if (rootJoint == null) throw new InvalidOperationException("Root_Joint absent");
                var games = GameBones.FromSkeletonRoot(rootJoint);
                if (games == null) throw new InvalidOperationException("skeleton anchors incomplete");

                var assets = PmxAssets.LoadOrAcquire(pkgPath, Plugin.MaxTextureSize.Value,
                    true, AccessoryFilter.FromConfig(),
                    out pkgName, out var warm, out _);
                if (assets == null) throw new InvalidOperationException($"package '{pkgName}' failed to load");

                var contract = GameBones.ContractCheck(games);

                // BSG 自家 icon 構圖（Resources 資產；取不到則用其欄位預設值，等 同構圖）
                var settings = Resources.Load<PlayerIconCreatorSettings>("PlayerIconCreatorSettings");
                if (settings == null)
                {
                    fallbackSettings = ScriptableObject.CreateInstance<PlayerIconCreatorSettings>();
                    settings = fallbackSettings;
                }
                ApplyIconPose(modelGo, settings);

                // 構圖走 BSG 自家 PlayerIconCreatorSettings（模型側等比縮放後骨架 pristine，無 post-fit 高差補償）。
                var rig = CharacterRig.Build(rootJoint, games, assets, Plugin.PmxUnitScale.Value,
                    Plugin.PmxYaw.Value, Plugin.PmxYOffset.Value, rootJoint.gameObject.layer);

                // 拍照與收尾交給常駐 driver：每幀補隱（遲生成件：watch／LOD 換裝／裝備 view）、
                // 2 幀穩定後 Drive＋捕捉；本續行到此為止，不再持有清理責任。
                var driver = host.AddComponent<CardIconDriver>();
                driver.Bind(rootJoint, games, rig, host, loader, modelGo, settings,
                    pkgPath, pkgName, preview, sw, warm, contract, fallbackSettings);
                fallbackSettings = null;
            }
            catch (Exception e)
            {
                if (!_warned)
                {
                    _warned = true;
                    Plugin.Log.LogWarning($"[DollFrame] card icon: render failed ({e.GetType().Name}) - card stays blank this session");
                }
                if (preview != null && _cache.TryGetValue(pkgPath, out var cached))
                {
                    preview.sprite = cached.Sprite;
                    preview.enabled = true;
                }
                if (fallbackSettings != null) UnityEngine.Object.Destroy(fallbackSettings);
                if (loader != null) SafeClear(loader, pkgName);
                if (host != null) UnityEngine.Object.Destroy(host);
            }
        }

        private static void WarnTeardown(Exception e)
        {
            if (!_warned)
            {
                _warned = true;
                Plugin.Log.LogWarning($"[DollFrame] card icon: teardown skipped ({e.GetType().Name})");
            }
        }

        /// <summary>loader.Clear（bundle token／body／poser 的 DestroyImmediate）與 PmxAssets refcount 的容錯收尾。</summary>
        private static void SafeClear(PlayerModelLoader loader, string pkgName)
        {
            try { if (pkgName != null) PmxAssets.Release(pkgName); }
            catch (Exception e) { WarnTeardown(e); }
            try { loader.Clear(); }
            catch (Exception e) { WarnTeardown(e); }
        }

        /// <summary>
        /// 卡片渲染常駐 driver：掛在 host 上、隨 host 銷毀。每個 LateUpdate 補隱非 DollFrame_
        /// renderers（watch／LOD 換裝／裝備 view 等遲生成件；未隱藏槽位 view＝顯性放行），
        /// SettleFrames 幀穩定後 rig.Drive()→相機捕捉→sprite 快取與指派→一次性收尾
        /// （PmxAssets release／loader.Clear／host 銷毀）後自毀。
        /// </summary>
        internal sealed class CardIconDriver : MonoBehaviour
        {
            private const int SettleFrames = 2;

            private Transform _rootJoint;
            private Dictionary<string, Transform> _games;
            private CharacterRig _rig;
            private GameObject _host;
            private PlayerModelLoader _loader;
            private GameObject _modelGo;
            private PlayerIconCreatorSettings _settings;
            private string _pkgPath;
            private string _pkgName;
            private Image _preview;
            private Stopwatch _sw;
            private bool _warm;
            private (int resolved, int total, int digits, List<string> missing) _contract;
            private int _spawnFrame;
            private PlayerIconCreatorSettings _fallbackSettings;

            internal void Bind(Transform rootJoint, Dictionary<string, Transform> games, CharacterRig rig,
                GameObject host, PlayerModelLoader loader, GameObject modelGo, PlayerIconCreatorSettings settings,
                string pkgPath, string pkgName, Image preview, Stopwatch sw, bool warm,
                (int resolved, int total, int digits, List<string> missing) contract,
                PlayerIconCreatorSettings fallbackSettings)
            {
                _rootJoint = rootJoint;
                _games = games;
                _rig = rig;
                _host = host;
                _loader = loader;
                _modelGo = modelGo;
                _settings = settings;
                _pkgPath = pkgPath;
                _pkgName = pkgName;
                _preview = preview;
                _sw = sw;
                _warm = warm;
                _contract = contract;
                _spawnFrame = Time.frameCount;
                _fallbackSettings = fallbackSettings;
            }

            private void LateUpdate()
            {
                try
                {
                    if (_host == null || _rootJoint == null || _loader == null || _loader.PlayerBody == null)
                    { Finish(); return; }
                    HideCardSurfaces(_modelGo, _loader.PlayerBody);
                    if (Time.frameCount - _spawnFrame < SettleFrames) return;
                    FinalizeCapture();
                }
                catch (Exception e)
                {
                    if (!TradingCardIcon._warned)
                    {
                        TradingCardIcon._warned = true;
                        Plugin.Log.LogWarning($"[DollFrame] card icon: capture failed ({e.GetType().Name}) - card stays blank this session");
                    }
                    var cacheKey = TradingCardIcon.BuildCacheKey(_pkgPath);
                    if (_preview != null && TradingCardIcon._cache.TryGetValue(cacheKey, out var cached))
                    {
                        _preview.sprite = cached.Sprite;
                        _preview.enabled = true;
                    }
                    Finish();
                }
            }

            private void FinalizeCapture()
            {
                // 捕捉前審計：殘餘啟用中的非 DollFrame_ renderer 就地現形（最多點名 3 個 GO）
                var stray = new List<string>();
                int strayCount = 0, total = 0;
                foreach (var r in _modelGo.GetComponentsInChildren<Renderer>(true))
                {
                    if (r == null) continue;
                    total++;
                    if (!r.enabled || TradingCardIcon.UnderPrefix(r.transform, "DollFrame_")) continue;
                    strayCount++;
                    if (stray.Count < 3) stray.Add($"{r.gameObject.name}({r.GetType().Name})");
                }
                if (strayCount > 0)
                    Plugin.Log.LogInfo($"[DollFrame] card icon: pre-capture audit renderers={total} strayEnabled={strayCount} e.g. {string.Join(", ", stray)}");

                _rig.Drive();                                 // 與捕捉同一同步區塊，姿勢一致
                var tex = Capture(_rootJoint, _games, _rig, _host, _settings);
                tex.hideFlags = HideFlags.DontUnloadUnusedAsset;
                var sprite = Sprite.Create(tex, new Rect(0f, 0f, tex.width, tex.height),
                    new Vector2(0.5f, 0.5f), 100f, 0, SpriteMeshType.FullRect, Vector4.zero);
                sprite.hideFlags = HideFlags.DontUnloadUnusedAsset;

                var cacheKey = TradingCardIcon.BuildCacheKey(_pkgPath);
                if (TradingCardIcon._cache.TryGetValue(cacheKey, out var old))
                {
                    if (old.Sprite != null) UnityEngine.Object.Destroy(old.Sprite);
                    if (old.Texture != null) UnityEngine.Object.Destroy(old.Texture);
                }
                TradingCardIcon._cache[cacheKey] = new TradingCardIcon.CardSprite { Sprite = sprite, Texture = tex };

                if (_preview != null)
                {
                    _preview.sprite = sprite;
                    _preview.enabled = true;
                }
                _sw.Stop();
                Plugin.Log.LogInfo($"[DollFrame] card icon: rendered '{_pkgName}' {tex.width}x{tex.height} in {_sw.Elapsed.TotalMilliseconds:0.##}ms ({(_warm ? "warm" : "cold")})\n{GameBones.ContractLine(_contract)}");
                Finish();
            }

            private void Finish()
            {
                // 收尾順序＝與 async 版相同：mesh＋SDF＋PmxAssets refcount → loader.Clear（連帶
                // 拆 rig GO 與 fit）→ host 連相機整棵銷毀。可重入。
                if (_rig != null)
                {
                    if (_rig.Mesh != null) UnityEngine.Object.DestroyImmediate(_rig.Mesh);
                    if (_rig.HeadMesh != null) UnityEngine.Object.DestroyImmediate(_rig.HeadMesh);
                    _rig.TerminateSdf();
                }
                if (_fallbackSettings != null) UnityEngine.Object.Destroy(_fallbackSettings);
                if (_loader != null) TradingCardIcon.SafeClear(_loader, _pkgName);
                if (_host != null) UnityEngine.Object.Destroy(_host);
                _rootJoint = null; _games = null; _rig = null; _host = null;
                _loader = null; _modelGo = null; _preview = null; _fallbackSettings = null;
                Destroy(this);
            }
        }

        /// <summary>
        /// PlayerIconCreator.PoseModelByBounds 的等價套用：model 擺 Settings 的
        /// LocalPosition/Rotation（正交／透視兩組，依 isOrthographic 選其一）。
        /// </summary>
        private static void ApplyIconPose(GameObject modelGo, PlayerIconCreatorSettings s)
        {
            var pose = s.isOrthographic ? s.orthographicLocalPosition : s.perspectiveLocalPosition;
            var rot = s.isOrthographic ? s.orthographicRotation : s.perspectiveRotation;
            modelGo.transform.localPosition = pose;
            modelGo.transform.rotation = Quaternion.Euler(rot);
        }

        /// <summary>
        /// 相機：優先載 BSG 的 icon 相機 prefab（其 transform 是他們構圖的一半），相機參數照抄
        /// Settings；prefab 取不到才落回自有 fallback（沿骨架 forward 擺機位、perspective FOV 25）。
        /// 捕捉 = 手動 Render 進 ARGB RT、透明底、以本體 layer 為 cullingMask、ReadPixels 回 texture。
        /// </summary>
        private static Texture2D Capture(Transform rootJoint, Dictionary<string, Transform> games,
            CharacterRig rig, GameObject host, PlayerIconCreatorSettings s)
        {
            Camera cam = null;
            var camPrefab = Resources.Load<GameObject>("PlayerIconCreator/PlayerIconCamera");
            if (camPrefab != null)
            {
                var camGo = UnityEngine.Object.Instantiate(camPrefab, host.transform, false);
                cam = camGo.GetComponent<Camera>();
                if (cam == null) cam = camGo.GetComponentInChildren<Camera>(true);
            }
            bool own = cam == null;
            if (own)
            {
                var camGo = new GameObject("DollFrame_card_icon_cam", typeof(Camera));
                camGo.transform.SetParent(host.transform, false);
                cam = camGo.GetComponent<Camera>();
                cam.fieldOfView = 25f;
            }
            try
            {
                cam.enabled = false;                          // 手動 Render；不成為常駐相機
                cam.orthographic = s.isOrthographic;
                cam.orthographicSize = s.orthographicSize;
                cam.fieldOfView = s.fieldOfView;
                cam.nearClipPlane = s.nearClipPlane;
                cam.farClipPlane = s.farClipPlane;
                cam.renderingPath = s.isOrthographic ? s.orthographicRenderingPath : s.perspectiveRenderingPath;
                if (own)
                {
                    var look = games.TryGetValue("head", out var head) && head != null
                        ? head.position : rootJoint.position;
                    cam.transform.position = look + Vector3.up * 0.1f - rootJoint.forward * 1.9f;
                    cam.transform.LookAt(look, Vector3.up);
                }
                const int w = 512, h = 512;                   // 卡片頭像尺寸；原版 icon textureSize 同級
                cam.cullingMask = 1 << rootJoint.gameObject.layer;
                cam.clearFlags = CameraClearFlags.SolidColor;
                cam.backgroundColor = new Color(0f, 0f, 0f, 0f);
                cam.allowHDR = false;
                var rt = RenderTexture.GetTemporary(w, h, 24, RenderTextureFormat.ARGB32);
                try
                {
                    cam.targetTexture = rt;
                    RenderTexture.active = rt;
                    cam.Render();
                    var tex = new Texture2D(w, h, TextureFormat.RGBA32, false, false);
                    tex.ReadPixels(new Rect(0f, 0f, w, h), 0, 0);
                    tex.Apply(false, false);
                    return tex;
                }
                finally
                {
                    RenderTexture.active = null;
                    cam.targetTexture = null;
                    RenderTexture.ReleaseTemporary(rt);
                }
            }
            finally
            {
                if (own && cam != null) UnityEngine.Object.Destroy(cam.gameObject);
            }
        }

        /// <summary>Root_Joint 掃描（PreviewProxy.FindPreviewSkeleton 的同名規則，這裡以 body 子樹為域）。</summary>
        private static Transform FindRootJoint(Transform body)
        {
            foreach (var t in body.GetComponentsInChildren<Transform>(true))
            {
                if (t.name != "Root_Joint") continue;
                bool replaced = false;
                for (int c = 0; c < t.childCount; c++)
                    if (t.GetChild(c).name != null && t.GetChild(c).name.StartsWith("DollFrame_")) { replaced = true; break; }
                if (!replaced) return t;
            }
            return null;
        }

        /// <summary>
        /// 每幀補隱：除 DollFrame_ rig 與未設隱藏之裝備槽 view（現行預設僅 Eyewear 可留；該槽位經
        /// 資料級快照保留，顯性放行其 renderers）外，body 子樹內一切 renderer（PMC 蒙皮、
        /// 武器、BottomShadow、遲生成的 watch／LOD 件）全部隱藏。戰局側裝備隱藏隨
        /// EFT.Transmog-cust 移除已由 DollFrame 承接（EquipmentHide）。
        /// </summary>
        private static void HideCardSurfaces(GameObject bodyGo, EFT.PlayerBody body)
        {
            var keep = new HashSet<Renderer>();
            foreach (var slot in DollFrame.Visibility.EquipmentSlotHider.VisualSlots)
            {
                if (DollFrame.Visibility.EquipmentSlotHider.ShouldHide(slot)) continue;      // 已被快照清空，不會生成 view
                try
                {
                    DollFrame.Visibility.EquipmentSlotHider.CollectSlotRenderers(body, slot, keep);
                }
                catch (Exception e) { WarnTeardown(e); }
            }
            foreach (var r in bodyGo.GetComponentsInChildren<Renderer>(true))
            {
                if (r == null) continue;
                if (UnderPrefix(r.transform, "DollFrame_")) continue;
                if (keep.Contains(r)) continue;
                if (r.enabled) r.enabled = false;
                if (r.shadowCastingMode != UnityEngine.Rendering.ShadowCastingMode.Off)
                    r.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            }
        }

        private static bool UnderPrefix(Transform t, string prefix)
        {
            var cur = t;
            while (cur != null)
            {
                if (cur.name != null && cur.name.StartsWith(prefix)) return true;
                cur = cur.parent;
            }
            return false;
        }
    }
}
