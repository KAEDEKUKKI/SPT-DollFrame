using System;
using System.Collections.Generic;
using EFT;
using EFT.UI;
using HarmonyLib;
using UnityEngine;
using UnityEngine.Rendering;
using System.Reflection;

namespace DollFrame
{
    /// <summary>
    /// 主畫面角色預覽替換：攔截 PlayerModelView.Show，偵測選單生成的 PMC 預覽骨架
    /// (Root_Joint + "Base Human*" 命名)。與戰局同一套獨立骨架路線：在選單骨架
    /// (Root_Joint) 下建出 PMX 的獨立 Transform 樹與 SMR（CharacterRig），再以每幀
    /// aim retarget 跟蹤選單動畫（CharacterRig.Drive）——選單骨架保持 vanilla 不動，
    /// 沒有逐段縮放、沒有蒙皮歸屬改動。蒙皮到此骨架的原版網格（身體、頭、手套、裝備）
    /// 一律隱藏；剛性武器、掛件不在蒙皮歸屬內，一律保留。設定選單切換 Visual/Character
    /// 時拆除重掛。
    /// </summary>
    [HarmonyPatch]
    internal static class PreviewProxy
    {
        private static bool _typeFailLogged;

        private static IEnumerable<MethodBase> TargetMethods()
        {
            var t = AccessTools.TypeByName("EFT.UI.PlayerModelView") ?? AccessTools.TypeByName("PlayerModelView");
            if (t == null)
            {
                if (!_typeFailLogged)
                {
                    _typeFailLogged = true;
                    Plugin.Log.LogWarning("[DollFrame] PlayerModelView type not found; menu preview hook disabled");
                }
                yield break;
            }
            foreach (var m in t.GetMethods(AccessTools.all))
            {
                if (m.Name == "Show" && m.GetParameters().Length >= 3)
                    yield return m;
            }
        }

        [HarmonyPrefix]
        internal static void PreShow(object __instance, ref Action onCreated)
        {
            if (!Plugin.ReplaceBody.Value) return;
            if (!(__instance is Component view)) return;
            var drv = view.GetComponent<PreviewProxyDriver>();
            if (drv == null) drv = view.gameObject.AddComponent<PreviewProxyDriver>();
            drv.PrepareForShow(view);
            drv.ArmVisualShield();

            var prevOnCreated = onCreated;
            onCreated = () =>
            {
                try
                {
                    prevOnCreated?.Invoke();
                }
                finally
                {
                    if (drv != null)
                    {
                        drv.AttachNow();
                        if (drv.IsDriving || VanillaSelection.IsVanilla(Plugin.Character?.Value))
                            drv.DisarmVisualShield();
                    }
                }
            };
        }

        [HarmonyPostfix]
        internal static void PostShow(object __instance)
        {
            if (!Plugin.ReplaceBody.Value) return;
            if (!(__instance is Component view)) return;
            var drv = view.GetComponent<PreviewProxyDriver>();
            if (drv != null)
            {
                drv.AttachNow();
                if (drv.IsDriving || VanillaSelection.IsVanilla(Plugin.Character?.Value))
                    drv.DisarmVisualShield();
            }
        }
    }

    [DefaultExecutionOrder(32100)]
    internal sealed class PreviewProxyDriver : MonoBehaviour
    {
        private Component _view;
        private SkinnedMeshRenderer _skin;
        private CharacterRig _rig;
        private Transform _skeleton;
        private HashSet<Renderer> _hidden = new HashSet<Renderer>();
        private readonly DollFrame.Visibility.EquipmentSlotHider _equipHider = new DollFrame.Visibility.EquipmentSlotHider();
        private string _curPkg;
        private string _curPkgName;
        private float _nextScan;
        private float _lastScan;
        private bool _pkgWarned;

        /// <summary>活著的 menu PlayerModelView driver 數（OnEnable/OnDisable 計數）——
        /// Plugin 的切換熱鍵以「存在可即時預覽的 menu 檢視」為主選單判準的一部分。</summary>
        internal static bool HasLiveView => _liveViews > 0;
        internal static PreviewProxyDriver ActiveInstance { get; private set; }
        private static int _liveViews;
        private static readonly List<PreviewProxyDriver> LiveDrivers = new List<PreviewProxyDriver>();

        private void OnEnable()
        {
            _liveViews++;
            if (!LiveDrivers.Contains(this)) LiveDrivers.Add(this);
            ActiveInstance = this;
        }

        private void OnDisable()
        {
            _liveViews--;
            if (_liveViews < 0) _liveViews = 0;
            LiveDrivers.Remove(this);
            if (ActiveInstance == this)
                ActiveInstance = LiveDrivers.Count > 0 ? LiveDrivers[LiveDrivers.Count - 1] : null;
        }

        internal static void NotifyAllCharacterChanged()
        {
            for (int i = LiveDrivers.Count - 1; i >= 0; i--)
            {
                var d = LiveDrivers[i];
                if (d != null) d._nextScan = 0f;
            }
        }

        private readonly VisualShieldState _shieldState = new VisualShieldState();
        private Camera _shieldedCamera;
        private int _origCullingMask;

        internal void ArmVisualShield()
        {
            if (VanillaSelection.IsVanilla(Plugin.Character?.Value)) return;
            if (_view == null) return;
            _shieldState.Arm(Time.unscaledTime);
            HideAllRenderers();
            var cam = _view.GetComponentInChildren<Camera>(true);
            if (cam != null && cam.cullingMask != 0)
            {
                _shieldedCamera = cam;
                _origCullingMask = cam.cullingMask;
                cam.cullingMask = 0;
            }
        }

        internal void DisarmVisualShield()
        {
            if (_shieldState.IsArmed)
            {
                _shieldState.Disarm();
                if (_shieldedCamera != null)
                {
                    _shieldedCamera.cullingMask = _origCullingMask;
                    _shieldedCamera = null;
                }
            }
        }

        internal void PrepareForShow(Component view)
        {
            if (_phase == Phase.Driving || _skin != null || _rig != null)
                Teardown("view reshow");
            _hidden.Clear();
            Bind(view);
        }

        internal void Bind(Component view)
        {
            _view = view;
            enabled = true;
            _phase = Phase.Scan;
            _nextScan = 0f;
        }

        internal void HideAllRenderers()
        {
            if (_view == null) return;
            foreach (var smr in _view.GetComponentsInChildren<SkinnedMeshRenderer>(true))
            {
                if (smr == null || (smr.name != null && smr.name.StartsWith("DollFrame_"))) continue;
                _hidden.Add(smr);
                if (smr.enabled) smr.enabled = false;
                smr.shadowCastingMode = ShadowCastingMode.Off;
            }
            foreach (var mr in _view.GetComponentsInChildren<MeshRenderer>(true))
            {
                if (mr == null || (mr.name != null && mr.name.StartsWith("DollFrame_"))) continue;
                _hidden.Add(mr);
                if (mr.enabled) mr.enabled = false;
                mr.shadowCastingMode = ShadowCastingMode.Off;
            }
        }

        /// <summary>
        /// 同幀附掛（PostShow 直呼）：不走第一個掃描 tick，立即沿 TryAttach 的既有
        /// 受保護路徑附掛——骨架在場時同一幀隱藏原版 PMC 蒙皮，選單檢視不再閃 PMC。
        /// 骨架未進場（延遲生成或骨骼未就緒）時靜默返回：不記 log、不蓋重試章，
        /// 交給 Bind 的 0.05s 快速首掃在後續幀接手（骨架仍未進場則落回 +0.5s 迴圈）。
        /// </summary>
        internal void AttachNow()
        {
            if (_view == null || _phase != Phase.Scan) return;
            if (!Plugin.ReplaceBody.Value) return;
            if (FindPreviewSkeleton() == null) return;
            // TryAttach 入口自蓋重入章（_nextScan = now + 1.5f，重試沿用既有模式）；
            // 預先清 0 只是讓同一條保護路徑在本幀可進入，不繞過任何既有檢查
            _nextScan = 0f;
            try
            {
                TryAttach(Plugin.ResolvePackagePath());
            }
            catch (Exception e)
            {
                // 壞套件等例外不上拋到 Show（選單不中斷）；章已蓋，既有重試節奏照舊。
                // rig 已建成（Driving 後尾段 telemetry 例外）時不降級：降級會讓
                // FindPreviewSkeleton 因 DollFrame_ 哨兵永遠找不到骨架 → 死鎖在無驅動的靜態 rig
                if (_rig == null || _skin == null) _phase = Phase.Scan;
                Plugin.Log.LogWarning($"[DollFrame-preview] immediate attach failed: {e.Message}");
            }
        }

        private void LateUpdate()
        {
            if (_shieldState.ShouldAutoDisarm(Time.unscaledTime))
                DisarmVisualShield();

            if (_view == null) { Teardown("view gone"); Destroy(gameObject); return; }
            if (!Plugin.ReplaceBody.Value) return;

            // 切換角色（Visual/Character 設定變動）→ 拆掉重掛
            if (VanillaSelection.IsVanilla(Plugin.Character?.Value))
            {
                if (_skin != null || _rig != null || _hidden.Count > 0)
                    Teardown("vanilla selected");
                return;
            }
            var pkgPath = Plugin.ResolvePackagePath();
            if (pkgPath != _curPkg)
            {
                Teardown("character changed");
                // 同幀重掃：Teardown 後立即進入下方 Scan 分支重掛——warm 時同幀完成、
                // 連點切換不再被上一輪 TryAttach 入口蓋的 +1.5s 重試章擋成 blank 人形
                _nextScan = 0f;
            }

            if (_phase == Phase.Scan)
            {
                if (Time.unscaledTime < _nextScan) return;
                _nextScan = Time.unscaledTime + 0.1f;
                try
                {
                    TryAttach(pkgPath);
                }
                catch (Exception e)
                {
                    // 與 AttachNow 同一容錯：壞套件等例外不讓 LateUpdate 中斷；章已蓋，
                    // 重試照舊（每次重試前不殘留半套 state——TryAttach 前段全部失敗即返回）
                    Plugin.Log.LogWarning($"[DollFrame-preview] scan attach failed: {e.Message}");
                }
                return;
            }

            if (_rig == null || _skeleton == null || _skin == null)
            {
                // view 重建摧毀了 rig（_skeleton Unity-null）：正式拆除釋放 refcount 與
                // fit keeper，而非靜默降級——後者會洩漏 Acquire 持有的引用
                Teardown("preview rig lost");
                return;
            }

            UpdatePreviewArmState();
            _rig.Drive();
            _rig.UpdateSdfFace();

            // 選單/LOD 控制器可能重新啟用 PMC 蒙皮，逐幀重隱
            foreach (var r in _hidden)
                if (r != null && r.enabled) r.enabled = false;
            _equipHider.EnforceEveryFrame();

            // 裝備蒙皮可能在 Show 之後才生成，只掃一次會漏（浮在模型上）；與戰局同調 0.5s 重掃描
            if (Time.unscaledTime - _lastScan > 0.5f && _skeleton != null)
            {
                _lastScan = Time.unscaledTime;
                HideVanillaSkins(_skeleton);
                if (_view is PlayerModelView pmv && pmv.PlayerBody != null)
                    _equipHider.Sync(pmv.PlayerBody);
            }
        }

        private void UpdatePreviewArmState()
        {
            if (_rig == null) return;
            _rig.Alive = true;

            bool hasWeaponMesh = HasActiveShowcaseWeapon(_skeleton);
            bool isFirearm = hasWeaponMesh;
            bool isNonFirearm = false;

            if (_view is PlayerModelView pmv && pmv.ModelPlayerPoser != null)
            {
                var anim = pmv.ModelPlayerPoser.GetComponent<Animator>();
                if (anim != null)
                {
                    int wt = anim.GetInteger(EFT.PlayerAnimator.WEAPON_TYPE);
                    if (wt == 0 || wt == 1)
                    {
                        isFirearm = hasWeaponMesh;
                        isNonFirearm = false;
                    }
                    else if (wt == 2 || wt == 3)
                    {
                        isFirearm = false;
                        isNonFirearm = true;
                    }
                    else
                    {
                        isFirearm = false;
                        isNonFirearm = false;
                    }
                }
            }

            _rig.ArmIkActive = isFirearm;
            _rig.HoldingNonWeaponItem = !isFirearm && isNonFirearm;
            _rig.UnarmedHangFallback = !isFirearm && !isNonFirearm;
        }

        private static bool HasActiveShowcaseWeapon(Transform skel)
        {
            if (skel == null) return false;
            foreach (var wp in skel.GetComponentsInChildren<WeaponPrefab>(true))
            {
                if (wp == null || !wp.gameObject.activeInHierarchy) continue;
                foreach (var r in wp.GetComponentsInChildren<Renderer>(true))
                    if (r != null && r.enabled && r.gameObject.activeInHierarchy) return true;
            }
            return false;
        }

        private enum Phase { Scan, Driving }
        private Phase _phase;
        internal bool IsDriving => _phase == Phase.Driving;

        private void TryAttach(string pkgPath)
        {
            // 先設定短間隔重試保護，避免重入與長時間凍結
            _nextScan = Time.unscaledTime + 0.2f;

            if (string.IsNullOrEmpty(pkgPath))
            {
                if (VanillaSelection.IsVanilla(Plugin.Character?.Value))
                {
                    Teardown("vanilla selected");
                    return;
                }
                if (!_pkgWarned)
                {
                    _pkgWarned = true;
                    Plugin.Log.LogWarning("[DollFrame-preview] no character package selected (Visual/Character)");
                }
                return;
            }

            Transform skel = FindPreviewSkeleton();
            if (skel == null) { _nextScan = Time.unscaledTime + 0.05f; return; }

            var games = GameBones.FromSkeletonRoot(skel);
            if (games == null) { _nextScan = Time.unscaledTime + 0.1f; return; }

            // boot-time contract self-check (SPEC #1): resolved before any rig build
            var contract = GameBones.ContractCheck(games);

            // rig dump (todo 25 parity): the menu proportions the factors are measured
            // against - the vanilla skeletons are never deformed, so both menu and raid
            // dumps measure the true game rig
            var menuDump = GameBones.GameRigDump(skel);

            var assets = PmxAssets.LoadOrAcquire(pkgPath, Plugin.MaxTextureSize.Value,
                Plugin.FlipUv.Value, AccessoryFilter.FromConfig(),
                out var pkgName, out var warm, out var preloadMs);
            if (assets == null)
            {
                Plugin.Log.LogWarning($"[DollFrame-preview] package '{pkgName}' failed to load - retrying next scan");
                return;
            }
            Plugin.Log.LogInfo($"[DollFrame] preload: {pkgName} {preloadMs:0.##}ms ({(warm ? "warm" : "cold")})");

            RemoveStrayDollFrameChildren(skel);

            // independent rig on the preview skeleton (menu keeps no ground offset, as before)
            var menuMetrics = ModelMetrics.Compute(assets.Model, Plugin.PmxUnitScale.Value, menuDump);
            var previewScale = Plugin.ProportionScale.Value ? ModelScaleMath.Solve(menuMetrics.Factors) : (ModelScaleMath.ScalePlan?)null;
            _rig = CharacterRig.Build(skel, games, assets,
                Plugin.PmxUnitScale.Value, Plugin.PmxYaw.Value, Plugin.PmxYOffset.Value,
                skel.gameObject.layer,
                previewScale.HasValue ? menuMetrics.Factors : null);
            _skin = _rig.Skin;

            HideVanillaSkins(skel);
            if (_view is PlayerModelView pmvAttach && pmvAttach.PlayerBody != null)
                _equipHider.Sync(pmvAttach.PlayerBody);
            _skeleton = skel;
            _curPkg = pkgPath;
            _curPkgName = pkgName;
            _phase = Phase.Driving;
            UpdatePreviewArmState();
            _rig.Drive();
            _rig.UpdateSdfFace();
            DisarmVisualShield();

            // menu-vs-raid factor parity (todo 25): the 12 logical factors of THIS menu
            // rig vs the raid rig of the same package (captured once per attach);
            // divergence >2% warns, no raid yet logs no baseline - never fatal.
            var attachLog = $"[DollFrame] preview body replaced: {pkgName}\n{GameBones.ContractLine(contract)}";
            var menuFactors = menuMetrics.Factors;
            BodyReplacer.LastFactorsByPackage.TryGetValue(pkgName, out var raidFactors);
            var parity = GameBones.ParityLine(pkgName, menuFactors, raidFactors);
            if (parity != null && raidFactors == null) attachLog += "\n" + parity;
            Plugin.Log.LogInfo(attachLog);
            if (parity != null && raidFactors != null) Plugin.Log.LogWarning(parity);
        }

        private Transform FindPreviewSkeleton()
        {
            if (_view is PlayerModelView pmv)
            {
                // PlayerModelView 以非同步 PlayerModelLoader 載入 PlayerBody；
                // 當 Show 剛呼叫 Close() 銷毀舊模型時，舊 GameObject 要到幀末才會從 transform 樹移除，
                // 但 pmv.PlayerBody / pmv.ModelPlayerPoser 已立即為 null。
                // 僅從 pmv 當前活著的 PlayerBody / ModelPlayerPoser 取骨架，絕不誤抓待銷毀舊骨架或後台 HideoutPlayer。
                Transform liveModelRoot = pmv.PlayerBody != null
                    ? pmv.PlayerBody.transform
                    : (pmv.ModelPlayerPoser != null ? pmv.ModelPlayerPoser.transform : null);
                if (liveModelRoot == null) return null;

                foreach (var t in liveModelRoot.GetComponentsInChildren<Transform>(true))
                {
                    if (t == null || t.name != "Root_Joint") continue;
                    if (UnderDollFrameAncestor(t) || !GameBones.HasAllRequiredAnchors(t)) continue;
                    RemoveStrayDollFrameChildren(t);
                    return t;
                }
                return null;
            }

            if (_view != null)
            {
                foreach (var t in _view.GetComponentsInChildren<Transform>(true))
                {
                    if (t == null || t.name != "Root_Joint") continue;
                    if (UnderDollFrameAncestor(t) || !GameBones.HasAllRequiredAnchors(t)) continue;
                    RemoveStrayDollFrameChildren(t);
                    return t;
                }
            }

            foreach (var t in UnityEngine.Object.FindObjectsOfType<Transform>())
            {
                if (t == null || t.name != "Root_Joint" || !t.gameObject.activeInHierarchy) continue;
                // 嚴格排除戰局/藏身處 Player（如 Y=-500m 的 HideoutPlayer）與其他 PlayerModelView
                if (t.GetComponentInParent<Player>() != null || t.GetComponentInParent<BodyReplacer>() != null) continue;
                var ownerView = t.GetComponentInParent<PlayerModelView>();
                if (ownerView != null && ownerView != _view) continue;
                bool replaced = false;
                for (int c = 0; c < t.childCount; c++)
                    if (t.GetChild(c).name.StartsWith("DollFrame_")) { replaced = true; break; }
                // DollFrame_ 祖先（TradingCardIcon 的 DollFrame_card_icon_host 等）與缺漏錨點的非完整骨架永不接手
                if (replaced || UnderDollFrameAncestor(t) || !GameBones.HasAllRequiredAnchors(t)) continue;
                return t;
            }
            return null;
        }

        private void RemoveStrayDollFrameChildren(Transform rootJoint)
        {
            if (rootJoint == null) return;
            for (int i = rootJoint.childCount - 1; i >= 0; i--)
            {
                var c = rootJoint.GetChild(i);
                if (c == null || (_skin != null && c == _skin.transform)) continue;
                if (c.name != null && c.name.StartsWith("DollFrame_", StringComparison.Ordinal))
                {
                    var smr = c.GetComponent<SkinnedMeshRenderer>();
                    if (smr != null && smr.sharedMesh != null) DestroyImmediate(smr.sharedMesh);
                    c.SetParent(null, false);
                    DestroyImmediate(c.gameObject);
                }
            }
        }

        private static bool UnderDollFrameAncestor(Transform t)
        {
            var cur = t;
            while (cur != null)
            {
                if (cur.name != null && cur.name.StartsWith("DollFrame_", StringComparison.Ordinal)) return true;
                cur = cur.parent;
            }
            return false;
        }

        /// <summary>
        /// 只隱藏「蒙皮到此預覽骨架」的 SkinnedMeshRenderer（身體、頭、手套等蒙皮件，
        /// 不論其 gameobject 掛在骨架樹內或旁邊的 Mesh 節點）；剛性武器與掛件不蒙皮
        /// 到骨架，一律保留顯示。
        /// </summary>
        internal void RehideVanilla()
        {
            foreach (var r in _hidden)
                if (r != null && r.enabled) r.enabled = false;
            if (_skeleton != null)
                HideVanillaSkins(_skeleton);
        }

        internal void HideVanillaSkins(Transform skel)
        {
            if (skel == null) return;
            var scope = _view != null ? _view.transform : skel.root;
            foreach (var smr in scope.GetComponentsInChildren<SkinnedMeshRenderer>(true))
            {
                if (smr == null || _hidden.Contains(smr)) continue;
                if (!IsSkinnedTo(smr, skel)) continue;
                if (UnderPrefix(smr.transform, "DollFrame_", skel)) continue;
                _hidden.Add(smr);
                smr.enabled = false;
                smr.shadowCastingMode = ShadowCastingMode.Off;
            }
        }

        private static bool IsSkinnedTo(SkinnedMeshRenderer smr, Transform skel)
        {
            if (smr == null || skel == null) return false;
            if (smr.rootBone != null && smr.rootBone.IsChildOf(skel)) return true;
            if (smr.bones != null && smr.bones.Length > 0)
            {
                foreach (var b in smr.bones)
                {
                    if (b != null && b.IsChildOf(skel)) return true;
                }
            }
            return false;
        }

        private static bool UnderPrefix(Transform t, string prefix, Transform stopAt)
        {
            var cur = t;
            while (cur != null && cur != stopAt)
            {
                if (cur.name.StartsWith(prefix)) return true;
                cur = cur.parent;
            }
            return false;
        }

        internal void RestoreAllVanilla(Component view)
        {
            foreach (var r in _hidden)
            {
                if (r != null)
                {
                    r.enabled = true;
                    r.shadowCastingMode = ShadowCastingMode.On;
                }
            }
            _hidden.Clear();

            if (view != null)
            {
                foreach (var r in view.GetComponentsInChildren<Renderer>(true))
                {
                    if (r == null || (r.name != null && r.name.StartsWith("DollFrame_"))) continue;
                    r.enabled = true;
                    r.shadowCastingMode = ShadowCastingMode.On;
                }

                if (view is PlayerModelView pmv && pmv.PlayerBody != null)
                {
                    try
                    {
                        var side = pmv.PlayerBody.PlayerSide != null ? pmv.PlayerBody.PlayerSide.Value : EPlayerSide.Usec;
                        pmv.PlayerBody.UpdatePlayerRenders(EPointOfView.ThirdPerson, side);
                    }
                    catch { }
                }
            }
        }

        internal static void ReloadActivePreview()
        {
            try
            {
                PlayerModelView pmv = null;
                if (ActiveInstance != null && ActiveInstance._view is PlayerModelView p)
                    pmv = p;
                else if (ActiveInstance != null && ActiveInstance._view != null)
                    pmv = ActiveInstance._view.GetComponentInChildren<PlayerModelView>(true);
                if (pmv == null)
                    pmv = UnityEngine.Object.FindObjectOfType<PlayerModelView>();
                if (pmv == null) return;

                var session = ItemUiContext.Instance != null ? ItemUiContext.Instance.Session : null;
                if (session == null || session.Profile == null) return;
                var visual = session.Profile.GetVisualEquipmentState(false);
                if (visual == null) return;

                pmv.Show(visual, null, null, 0f, null, true);
            }
            catch (Exception e)
            {
                Plugin.Log.LogWarning($"[DollFrame-preview] reload preview failed: {e.Message}");
            }
        }

        /// <summary>拆除：還原隱藏的蒙皮、刪除 rig（根 GO 含整棵 Transform 樹與 SMR）與其 mesh 複本、回到掃描狀態。可重入。
        /// 用 DestroyImmediate：同一幀內的「拆＋同幀重掛」必須對 FindPreviewSkeleton 的 DollFrame_ 哨兵立即可見，
        /// 否則 deferred Destroy 會把同幀重掃擋到 +0.5s 迴圈——期間 global fallback 可能抓到卡片 icon host 的
        /// Root_Joint（跨骨架附掛＝預覽位無模型）。選 Vanilla (PMC) 時同步還原被隱藏的原版蒙皮。</summary>
        private void Teardown(string reason)
        {
            DisarmVisualShield();
            bool had = _skin != null || _hidden.Count > 0;
            bool restoreVanilla = !Plugin.ReplaceBody.Value || VanillaSelection.IsVanilla(Plugin.Character?.Value);
            if (restoreVanilla)
            {
                _equipHider.RestoreAll();
                RestoreAllVanilla(_view);
            }
            if (_skin != null)
            {
                _skin.transform.SetParent(null, false);
                DestroyImmediate(_skin.gameObject);
            }
            _skin = null;
            if (_rig != null && _rig.Mesh != null) DestroyImmediate(_rig.Mesh);
            if (_rig != null && _rig.HeadMesh != null) DestroyImmediate(_rig.HeadMesh);
            if (_rig != null) _rig.TerminateSdf();
            _rig = null;
            if (_curPkgName != null) PmxAssets.Release(_curPkgName);
            _curPkgName = null;
            _skeleton = null;
            _curPkg = null;
            _phase = Phase.Scan;
            _nextScan = 0f;
            if (had) Plugin.Log.LogInfo($"[DollFrame] preview body removed ({reason})");
        }

        private void OnDestroy()
        {
            DisarmVisualShield();
            _equipHider.RestoreAll();
            RestoreAllVanilla(_view);
            if (_skin != null) Destroy(_skin.gameObject);
            if (_rig != null && _rig.Mesh != null) Destroy(_rig.Mesh);
            if (_rig != null && _rig.HeadMesh != null) Destroy(_rig.HeadMesh);
            if (_rig != null) _rig.TerminateSdf();
            if (_curPkgName != null) PmxAssets.Release(_curPkgName);
            _curPkgName = null;
        }
    }
}
