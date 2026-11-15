using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using EFT;
using HarmonyLib;
using UnityEngine;

namespace DollFrame
{
    /// <summary>
    /// 【Character 主模塊核心宿主：BodyReplacer】
    /// 負責玩家角色生命週期管理與單向更新管線（2026-11-14 架構反轉：模型側等比縮放貼合
    /// pristine 遊戲骨架，次要動作（1C SpringBone／1B 姿態補償）與視角模塊（1D 相機/FP 手臂）
    /// 已移除，動作與視角回歸遊戲原生）：
    /// 1. 掛載（Apply）：孤兒網格清理 → Root_Joint scale 歸一 → 遊戲骨架契約檢查 →
    ///    .gfc 快取預載 → ModelMetrics 量測（12 段因子）→ 全模型單一等比因子求解（腿錨定）→
    ///    核心零拉伸獨立骨架建構（CharacterRig.Build，內部一次性寫入 rig 根 uniform localScale）→
    ///    2A 原版素體隱藏（VanillaBodyHider）→ 第一人稱頭部 SMR 遮罩（FirstPersonSMRMask）。
    /// 2. 每幀單向驅動（LateUpdate）：
    ///    CharacterRig.Drive（FK 重定向 + 接地位）→ 頭部遮罩 POV 切換 →
    ///    VanillaBodyHider.EnforceHiddenEveryFrame。
    /// 3. 卸載（OnDestroy）：自動釋放快取引用、銷毀複製網格並完整還原原版渲染器。
    /// </summary>
    [DefaultExecutionOrder(32100)]
    internal sealed class BodyReplacer : MonoBehaviour
    {
        internal Player _player;
        private SkinnedMeshRenderer _skin;
        private SkinnedMeshRenderer _headSkin;
        private Mesh _skinMesh;
        private Mesh _headMesh;
        private string _pkgName;
        private CharacterRig _rig;
        private Transform _rootJoint;

        // 次要連動：1D 頭部開關（FirstPersonSMRMask，唯一保留的視角面）、2A 原版素體隱藏
        private readonly FirstPersonSMRMask _headMask = new FirstPersonSMRMask();
        private readonly VanillaBodyHider _vanillaHider = new VanillaBodyHider();
        private readonly DollFrame.Visibility.EquipmentSlotHider _equipHider = new DollFrame.Visibility.EquipmentSlotHider();
        private float _lastEquipSyncTime = -10f;
        private Func<(bool fp, bool alive)> _povQuery;
        private (bool fp, bool alive) _lastPov = (false, true);
        private float _lastLateUpdateErrorTime;
        private bool _suppressRestoreOnDestroy;
        private int _lastPreCullFrame = -1;
        private UnityEngine.Camera.CameraCallback _onPreCullDelegate;
        private static bool _missingLogged;

        internal static readonly Dictionary<string, IReadOnlyDictionary<string, float>>
            LastFactorsByPackage = new Dictionary<string, IReadOnlyDictionary<string, float>>();

        internal static class Stats
        {
            internal static int Builds;
            internal static int Destroys;
            internal static int LiveSkins => Builds - Destroys;
        }

        internal static int MeshCopies;

        private static void ReleaseAndDestroy(BodyReplacer existing, bool suppressRestore = false)
        {
            if (existing == null) return;
            if (existing._pkgName != null) PmxAssets.Release(existing._pkgName);
            existing._pkgName = null;
            if (suppressRestore) existing._suppressRestoreOnDestroy = true;
            UnityEngine.Object.DestroyImmediate(existing);
        }

        internal static void Reapply(Player player)
        {
            if (player == null) return;
            var existing = player.gameObject.GetComponent<BodyReplacer>();
            ReleaseAndDestroy(existing, suppressRestore: !VanillaSelection.IsVanilla(Plugin.Character?.Value));
            Apply(player);
        }

        internal static void Apply(Player player)
        {
            if (player == null || !Plugin.ReplaceBody.Value) return;
            if (!Patches.ShouldScale(player)) return;

            var existing = player.gameObject.GetComponent<BodyReplacer>();
            if (VanillaSelection.IsVanilla(Plugin.Character?.Value))
            {
                ReleaseAndDestroy(existing);
                VanillaBodyHider.RestoreAllVanilla(player);
                CapsulePatches.ApplyScale(player, 1f);
                VoiceReplacer.Restore(player);
                IkRefresh.RefreshIK(player);
                return;
            }

            var reinit = false;
            if (existing != null)
            {
                var cur = Path.GetFileName(Plugin.Character?.Value);
                if (string.IsNullOrEmpty(cur) || existing._pkgName == cur)
                {
                    var newRoot = player.PlayerBones != null ? player.PlayerBones.RootJoint : null;
                    if (newRoot != null && existing._rootJoint != null && existing._rootJoint != newRoot)
                    {
                        reinit = true;
                        Plugin.Log.LogInfo($"[DollFrame] re-init: skeleton instance changed on '{player.Profile?.Nickname}' (rig rebuild + re-attach)");
                        ReleaseAndDestroy(existing);
                    }
                    else
                    {
                        existing._vanillaHider.SyncVanillaHidden(player);
                        existing._equipHider.Sync(player.PlayerBody, SafeHandsController(player)?.Item);
                        RemoveStraySkins(player, existing._skin != null ? existing._skin.transform : null,
                            existing._headSkin != null ? existing._headSkin.transform : null);
                        return;
                    }
                }
                else
                {
                    ReleaseAndDestroy(existing);
                }
            }

            string packagePath = Plugin.ResolvePackagePath();
            if (string.IsNullOrEmpty(packagePath))
            {
                if (!_missingLogged)
                {
                    _missingLogged = true;
                    Plugin.Log.LogWarning($"[DollFrame] character package missing for '{Plugin.Character.Value}' in '{Plugin.CharactersDirectory}' (place PMX character folders here)");
                }
                return;
            }

            try
            {
                RemoveStraySkins(player, null, null);

                var rootJoint = player.PlayerBones != null ? player.PlayerBones.RootJoint : null;
                if (rootJoint == null) { Plugin.Log.LogError("[DollFrame] Root_Joint missing; body not replaced"); return; }
                if (!Mathf.Approximately(rootJoint.localScale.x, 1f))
                {
                    rootJoint.localScale = Vector3.one;
                    Plugin.Log.LogInfo("[DollFrame] Root_Joint reset to scale 1 (independent rig route)");
                }
                EmitRigSourceLine(player, rootJoint);

                var games = GameBones.FromPlayerBones(player.PlayerBones);
                if (games == null) return;

                var contract = GameBones.ContractCheck(games);
                var raidDump = GameBones.GameRigDump(rootJoint);

                var assets = PmxAssets.LoadOrAcquire(packagePath, Plugin.MaxTextureSize.Value,
                    Plugin.FlipUv.Value, AccessoryFilter.FromConfig(),
                    out var pkgName, out var warm, out var preloadMs);
                if (assets == null)
                {
                    Plugin.Log.LogWarning($"[DollFrame] package '{pkgName}' failed to load - body not replaced this init");
                    return;
                }
                if (!warm)
                    Plugin.Log.LogInfo($"[DollFrame] preload: {pkgName} {preloadMs:0.##}ms (cold)");

                var metrics = ModelMetrics.Compute(assets.Model, Plugin.PmxUnitScale.Value, raidDump);
                LastFactorsByPackage[pkgName] = metrics.Factors;

                // 全模型單一等比縮放因子（2026-11-14-6：腿錨定 uniform k，臂鏈縮放退役）
                ModelScaleMath.ScalePlan? scale = null;
                if (Plugin.ProportionScale.Value)
                    scale = ModelScaleMath.Solve(metrics.Factors);

                // 縮放後模型身高 → 膠囊補償輸入（全身 uniform 等比，身高精確隨 k）
                float scaledHeightM = scale.HasValue
                    ? metrics.HeightM * scale.Value.K
                    : metrics.HeightM;

                Plugin.Log.LogInfo($"[DollFrame] ground: derived {metrics.GroundOffsetM:0.####}m (override {Plugin.PmxYOffset.Value:0.####})");
                var attach = Stopwatch.StartNew();
                var rig = CharacterRig.Build(rootJoint, games, assets,
                    Plugin.PmxUnitScale.Value, Plugin.PmxYaw.Value, Plugin.PmxYOffset.Value + metrics.GroundOffsetM, 0,
                    scale.HasValue ? metrics.Factors : null);
                rig.GroundTelemetry = player.IsYourPlayer;
                MeshCopies++;
                attach.Stop();
                var attachMs = attach.Elapsed.TotalMilliseconds;
                Plugin.Log.LogInfo($"[DollFrame] attach {attachMs:0.##}ms");
                if (warm && attachMs > 16.0)
                    Plugin.Log.LogWarning($"[DollFrame] warn: attach exceeded frame budget ({attachMs:0.##}ms)");

                CapsulePatches.ApplyDerived(player, scaledHeightM);

                var comp = player.gameObject.AddComponent<BodyReplacer>();
                comp._player = player;
                comp._rig = rig;
                comp._skin = rig.Skin;
                comp._headSkin = rig.HeadSkin;
                comp._skinMesh = rig.Mesh;
                comp._headMesh = rig.HeadMesh;
                comp._pkgName = pkgName;
                comp._rootJoint = rootJoint;
                comp._vanillaHider.SyncVanillaHidden(player);
                comp._equipHider.Sync(player.PlayerBody, SafeHandsController(player)?.Item);
                VoiceReplacer.Apply(player);
                comp._povQuery = () =>
                {
                    try
                    {
                        if (player == null || !player.IsYourPlayer || player.PlayerBody == null) return (false, true);
                        bool fp = player.PlayerBody.PointOfView.Value.IsFirstPerson();
                        bool alive = player.HealthController == null || player.HealthController.IsAlive;
                        return (fp, alive);
                    }
                    catch { return (false, true); }
                };
                Stats.Builds++;
                comp._skin.gameObject.layer = comp._vanillaHider.PickLayer(player);
                if (comp._rig != null && comp._rig.HeadSkin != null)
                    comp._rig.HeadSkin.gameObject.layer = comp._skin.gameObject.layer;
                if (comp._rig != null)
                {
                    var hc = SafeHandsController(player);
                    bool alive = player.HealthController == null || player.HealthController.IsAlive;
                    bool fp = player.IsYourPlayer && player.PlayerBody != null && player.PlayerBody.PointOfView.Value.IsFirstPerson();
                    comp._rig.Alive = alive;
                    comp._rig.InFirstPerson = fp;
                    bool armIkInit = alive && IsArmed(player);
                    bool nonWeaponInit = alive && !armIkInit && IsHoldingNonWeaponItem(hc);
                    comp._rig.ArmIkActive = armIkInit;
                    comp._rig.HoldingNonWeaponItem = nonWeaponInit;
                    comp._rig.UnarmedHangFallback = alive && !armIkInit && !nonWeaponInit;
                    comp._rig.Drive();
                    comp._rig.UpdateSdfFace();
                }
                Plugin.Log.LogInfo($"[DollFrame] body replaced on '{player.Profile?.Nickname}': vanilla hidden ({comp._vanillaHider.HiddenCount} renderers){(reinit ? " (re-init)" : "")} builds={Stats.Builds} skins={Stats.LiveSkins} cache={PmxAssets.CacheCount} copies={MeshCopies}\n{GameBones.ContractLine(contract)}");
            }
            catch (Exception e)
            {
                Plugin.Log.LogError($"[DollFrame] body replace failed: {e}");
            }
        }

        private void LateUpdate()
        {
            if (_player == null) return;

            try
            {
                if (_player.IsYourPlayer)
                {
                    var cur = Path.GetFileName(Plugin.Character?.Value);
                    if (VanillaSelection.IsVanilla(Plugin.Character?.Value))
                    {
                        Plugin.Log.LogInfo($"[DollFrame] live character switch to Vanilla (PMC)");
                        Reapply(_player);
                        return;
                    }
                    if (!string.IsNullOrEmpty(cur) && cur != _pkgName)
                    {
                        var p = _player;
                        Plugin.Log.LogInfo($"[DollFrame] live character switch on player: '{_pkgName}' -> '{cur}'");
                        Reapply(p);
                        return;
                    }
                }

                // POV 查詢提早取值一次（臂鏈 IK 狀態閘：Drive 前寫入 alive∧armed；死亡/布娃娃或
                // 空手（EmptyHandsController）時上臂/前臂回退 FK 複製路徑），頭部遮罩區塊沿用同一取值。
                var povNow = (fp: false, alive: true);
                if (_povQuery != null) povNow = _povQuery();
                var hcNow = SafeHandsController(_player);
                bool armed = hcNow is Player.FirearmController && hcNow.Item is EFT.InventoryLogic.Weapon;
                bool armIk = povNow.alive && armed;

                // 狀態翻轉資訊行（TelemetryRegistry 'arm gate:'，單一生產者）：遊戲原版對空手/
                // 持械的差異是動畫機契約（WeaponType/層權重/手部 IK 源），空手時原版手臂即
                // 純動畫機 FK——此時臂鏈面停用，rig 完全鏡射原生垂手/擺臂/待機。
                if (armIk != _lastArmIk || _armGatePending)
                {
                    var item = hcNow != null ? hcNow.Item : null;
                    Plugin.Log.LogInfo($"[DollFrame] arm gate: alive={povNow.alive} armed={armed} ik={armIk} hands={(hcNow == null ? "<none>" : hcNow.GetType().Name)} item={(item == null ? "-" : item.GetType().Name)}");
                    _lastArmIk = armIk;
                    _armGatePending = false;
                }

                if (_rig != null)
                {
                    bool nonWeapon = povNow.alive && !armIk && IsHoldingNonWeaponItem(hcNow);
                    _rig.Alive = povNow.alive;
                    _rig.InFirstPerson = povNow.fp;
                    _rig.ArmIkActive = armIk;
                    _rig.HoldingNonWeaponItem = nonWeapon;
                    _rig.UnarmedHangFallback = povNow.alive && !armIk && !nonWeapon;
                    _rig.Drive();
                    _rig.UpdateSdfFace();
                }

                // 1D 頭部開關（唯一保留的視角面）：第一人稱頭部 ShadowsOnly、第三人稱還原；
                // 死亡優先，布娃娃與擊殺重播頭部完整。Body_SMR 維持 On（第一人稱可見雙手與軀幹雙腿）。
                if (_povQuery != null)
                {
                    var (fp, alive) = povNow;
                    bool masked = fp && Plugin.HideHeadInFp.Value;
                    if (masked != _lastPov.fp || alive != _lastPov.alive || _headMaskFirstApply)
                    {
                        _lastPov = (masked, alive);
                        _headMaskFirstApply = false;
                        _headMask.SetPointOfView(
                            masked ? PointOfView.FirstPerson : PointOfView.ThirdPerson,
                            _skin, _headSkin, isAiming: false, isAlive: alive);
                        Plugin.Log.LogInfo($"[DollFrame] fp head mask: {(masked ? "ShadowsOnly" : "On")} ({(alive ? "alive" : "dead")})");
                    }
                }

                // Vanilla Body Hiding & Own-Player Equipment Slot Hiding
                _vanillaHider.EnforceHiddenEveryFrame();
                if (Time.unscaledTime - _lastEquipSyncTime > 0.5f)
                {
                    _lastEquipSyncTime = Time.unscaledTime;
                    _equipHider.Sync(_player.PlayerBody, hcNow?.Item);
                }
                _equipHider.EnforceEveryFrame();
            }
            catch (Exception ex)
            {
                if (Time.unscaledTime - _lastLateUpdateErrorTime > 3.0f)
                {
                    _lastLateUpdateErrorTime = Time.unscaledTime;
                    Plugin.Log.LogWarning($"[DollFrame] BodyReplacer.LateUpdate error: {ex.Message}");
                }
            }
        }

        private bool _headMaskFirstApply = true;

        // 0.3.12 兩態判準（2026-11-14-12，用戶指令）：armed 唯一判準＝手上實體物品是否為
        // 槍械實體 `HandsController?.Item is EFT.InventoryLogic.Weapon`——控制器型別身分判定
        // （0.3.10/11 的 Player.EmptyHandsController 測試，Fika/SIT/SPT-VR idiom）在藏身處
        // 情境遭實測反證，退役。Weapon : CompoundItem 為 runtime 項類基類（全部槍械子類命中）；
        // Knife/grenade/道具→空手態純鏡射；取物資訊異常時回 false（鏡射安全方向）。
        private bool _lastArmIk;
        private bool _armGatePending = true;

        internal static Player.AbstractHandsController SafeHandsController(Player player)
        {
            try { return player != null ? player.HandsController : null; }
            catch { return null; }
        }

        internal static bool IsArmed(Player player)
        {
            try
            {
                var hc = SafeHandsController(player);
                return hc is Player.FirearmController && hc.Item is EFT.InventoryLogic.Weapon;
            }
            catch { return false; }
        }

        internal static bool IsHoldingNonWeaponItem(Player.AbstractHandsController hc)
        {
            try
            {
                return hc != null
                    && !(hc is Player.EmptyHandsController)
                    && hc.Item != null
                    && !(hc.Item is EFT.InventoryLogic.Weapon);
            }
            catch { return false; }
        }

        private void OnEnable()
        {
            _onPreCullDelegate ??= OnCameraPreCull;
            UnityEngine.Camera.onPreCull -= _onPreCullDelegate;
            UnityEngine.Camera.onPreCull += _onPreCullDelegate;
        }

        private void OnCameraPreCull(UnityEngine.Camera cam)
        {
            if (_player == null || _rig == null) return;
            int frame = Time.frameCount;
            if (_lastPreCullFrame == frame) return;
            _lastPreCullFrame = frame;
            try
            {
                _rig.Drive();
            }
            catch { }
        }

        private void OnDisable()
        {
            if (_onPreCullDelegate != null)
                UnityEngine.Camera.onPreCull -= _onPreCullDelegate;
        }

        private void OnDestroy()
        {
            if (_onPreCullDelegate != null)
                UnityEngine.Camera.onPreCull -= _onPreCullDelegate;
            Stats.Destroys++;
            _headMaskFirstApply = true;
            try
            {
                if (!_suppressRestoreOnDestroy)
                {
                    try { _equipHider.RestoreAll(); } catch { }
                    try { _vanillaHider.RestoreVanilla(); } catch (Exception ex) { Plugin.Log.LogWarning($"[DollFrame] OnDestroy RestoreVanilla error: {ex.Message}"); }
                    if (_player != null)
                    {
                        try { VanillaBodyHider.RestoreAllVanilla(_player); } catch (Exception ex) { Plugin.Log.LogWarning($"[DollFrame] OnDestroy RestoreAllVanilla error: {ex.Message}"); }
                        try { CapsulePatches.ApplyScale(_player, 1f); } catch (Exception ex) { Plugin.Log.LogWarning($"[DollFrame] OnDestroy CapsulePatches.ApplyScale error: {ex.Message}"); }
                        try { VoiceReplacer.Restore(_player); } catch (Exception ex) { Plugin.Log.LogWarning($"[DollFrame] OnDestroy VoiceReplacer.Restore error: {ex.Message}"); }
                        try { IkRefresh.RefreshIK(_player); } catch (Exception ex) { Plugin.Log.LogWarning($"[DollFrame] OnDestroy RefreshIK error: {ex.Message}"); }
                    }
                }
            }
            catch (Exception ex)
            {
                Plugin.Log.LogWarning($"[DollFrame] BodyReplacer OnDestroy EFT restore error: {ex.Message}");
            }
            finally
            {
                try
                {
                    if (_headSkin != null)
                    {
                        _headSkin.transform.SetParent(null, false);
                        UnityEngine.Object.DestroyImmediate(_headSkin.gameObject);
                        _headSkin = null;
                    }
                }
                catch { }

                try
                {
                    _skinMesh = _skin != null ? _skin.sharedMesh : _skinMesh;
                    if (_skin != null)
                    {
                        _skin.transform.SetParent(null, false);
                        UnityEngine.Object.DestroyImmediate(_skin.gameObject);
                        _skin = null;
                    }
                }
                catch { }

                try
                {
                    if (_skinMesh != null)
                    {
                        UnityEngine.Object.DestroyImmediate(_skinMesh);
                        _skinMesh = null;
                    }
                }
                catch { }

                try
                {
                    if (_headMesh != null)
                    {
                        UnityEngine.Object.DestroyImmediate(_headMesh);
                        _headMesh = null;
                    }
                }
                catch { }

                try
                {
                    if (_rig != null && _rig.Root != null)
                    {
                        _rig.TerminateSdf();
                        _rig.Root.SetParent(null, false);
                        UnityEngine.Object.DestroyImmediate(_rig.Root.gameObject);
                        _rig = null;
                    }
                }
                catch { }

                try
                {
                    if (_pkgName != null) PmxAssets.Release(_pkgName);
                    _pkgName = null;
                }
                catch { }
            }
        }

        internal static void PreHideVanilla(Player player) => VanillaBodyHider.PreHideVanilla(player);

        /// <summary>
        /// 0.3.12 綁定源頭驗證行（TelemetryRegistry 'rig source:'，attach 一次性）：證明鏡射
        /// 讀取的 Root_Joint 來自 PlayerBody 第三人稱骨架子樹（SkeletonRootJoint 側），而非
        /// 第一人稱手部骨架（SkeletonHands）——`fpHandsInside=True` 即綁定錯源，立即可判。
        /// </summary>
        private static void EmitRigSourceLine(Player player, Transform rootJoint)
        {
            try
            {
                var body = player.PlayerBody;
                bool underTPBody = body != null && rootJoint.IsChildOf(body.transform);
                Transform handsRoot = body != null && body.SkeletonHands != null ? body.SkeletonHands.transform : null;
                bool fpHandsInside = handsRoot != null && rootJoint.IsChildOf(handsRoot);
                Plugin.Log.LogInfo($"[DollFrame] rig source: root='{rootJoint.name}'@{rootJoint.GetInstanceID()} parent='{(rootJoint.parent != null ? rootJoint.parent.name : "<null>")}' underTPBody={underTPBody} fpHandsInside={fpHandsInside}");
            }
            catch (Exception e)
            {
                Plugin.Log.LogWarning($"[DollFrame] rig source: failed: {e.Message}");
            }
        }

        private static void RemoveStraySkins(Player player, Transform owned, Transform ownedHead)
        {
            int removed = RemoveStrays(player.gameObject.transform, owned, ownedHead);
            if (removed > 0)
                Plugin.Log.LogWarning($"[DollFrame] stray skins removed on '{player.Profile?.Nickname}': {removed} (double-init guard)");
        }

        private static int RemoveStrays(Transform root, Transform owned, Transform ownedHead)
        {
            int removed = 0;
            for (int i = root.childCount - 1; i >= 0; i--)
            {
                var c = root.GetChild(i);
                if (c == null) continue;
                removed += RemoveStrays(c, owned, ownedHead);
                if (c == owned || c == ownedHead || c.name == null || !c.name.StartsWith("DollFrame_", StringComparison.Ordinal)) continue;
                var smr = c.GetComponent<SkinnedMeshRenderer>();
                if (smr != null && smr.sharedMesh != null) UnityEngine.Object.DestroyImmediate(smr.sharedMesh);
                c.SetParent(null, false);
                UnityEngine.Object.DestroyImmediate(c.gameObject);
                removed++;
            }
            return removed;
        }

        [HarmonyPostfix]
        [HarmonyPatch(typeof(PlayerBody), nameof(PlayerBody.UpdatePlayerRenders))]
        internal static void PostUpdatePlayerRenders(PlayerBody __instance)
        {
            if (!Plugin.ReplaceBody.Value || __instance == null) return;
            if (VanillaSelection.IsVanilla(Plugin.Character?.Value)) return;
            try
            {
                var comp = __instance.GetComponentInParent<BodyReplacer>();
                if (comp != null)
                {
                    comp._vanillaHider.SyncVanillaHidden(comp._player);
                    return;
                }

                var player = __instance.GetComponentInParent<Player>();
                if (player != null)
                {
                    if (Patches.ShouldScale(player))
                        VanillaBodyHider.PreHideVanilla(player);
                    return;
                }

                var drv = __instance.GetComponentInParent<PreviewProxyDriver>() ?? PreviewProxyDriver.ActiveInstance;
                if (drv != null)
                {
                    drv.RehideVanilla();
                }
            }
            catch (Exception e)
            {
                Plugin.Log.LogWarning($"[DollFrame] re-hide after UpdatePlayerRenders failed: {e.Message}");
            }
        }
    }
}
