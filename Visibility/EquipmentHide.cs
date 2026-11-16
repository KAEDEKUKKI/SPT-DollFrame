using System;
using System.Collections.Generic;
using EFT;
using EFT.InventoryLogic;
using UnityEngine;
using UnityEngine.Rendering;

namespace DollFrame.Visibility
{
    /// <summary>
    /// 【Visibility 次要連動 2B：自身裝備槽位渲染器隱藏器】
    /// 直接針對目標玩家（僅自身或主選單預覽）的 PlayerBody.SlotViews 切換 Renderer 可見性，
    /// 不再修改 InventoryEquipment 快照，徹底解決：
    /// 1) 全地圖 AI/Scav/隊友裝備被誤隱藏的 bug；
    /// 2) DollFrame ↔ Vanilla (PMC) 切換時需重載 PlayerModelView 的卡頓與脫光閃爍。
    /// </summary>
    internal sealed class EquipmentSlotHider
    {
        internal static readonly EquipmentSlot[] VisualSlots =
        {
            EquipmentSlot.ArmorVest,
            EquipmentSlot.TacticalVest,
            EquipmentSlot.Backpack,
            EquipmentSlot.Headwear,
            EquipmentSlot.FaceCover,
            EquipmentSlot.Eyewear,
            EquipmentSlot.Earpiece,
            EquipmentSlot.ArmBand,
        };

        // 未持在手上的背後/腰間武器槽位（避免背上與手上同時出現重複武器，或遮擋角色模型）
        private static readonly EquipmentSlot[] HolsteredWeaponSlots =
        {
            EquipmentSlot.FirstPrimaryWeapon,
            EquipmentSlot.SecondPrimaryWeapon,
            EquipmentSlot.Holster,
            EquipmentSlot.Scabbard,
        };

        private readonly Dictionary<Renderer, (ShadowCastingMode mode, bool enabled)> _hidden =
            new Dictionary<Renderer, (ShadowCastingMode mode, bool enabled)>();
        private readonly HashSet<Renderer> _desiredHidden = new HashSet<Renderer>();
        private readonly List<Renderer> _toRestore = new List<Renderer>();
        private static readonly List<Renderer> _renderBuf = new List<Renderer>(16);

        internal static bool ShouldHide(EquipmentSlot slot) => slot switch
        {
            EquipmentSlot.ArmorVest => Plugin.HideArmor.Value,
            EquipmentSlot.TacticalVest => Plugin.HideTacticalRig.Value,
            EquipmentSlot.Backpack => Plugin.HideBackpack.Value,
            EquipmentSlot.Headwear => Plugin.HideHeadwear.Value,
            EquipmentSlot.FaceCover => Plugin.HideFaceCover.Value,
            EquipmentSlot.Eyewear => Plugin.HideEyewear.Value,
            EquipmentSlot.Earpiece => Plugin.HideEarpiece.Value,
            EquipmentSlot.ArmBand => Plugin.HideArmBand.Value,
            _ => false,
        };

        internal void Sync(PlayerBody body, Item inHandsItem = null)
        {
            if (body == null || body.SlotViews == null) return;

            _desiredHidden.Clear();
            try
            {
                foreach (var slot in VisualSlots)
                {
                    if (!ShouldHide(slot)) continue;
                    CollectSlotRenderers(body, slot, _desiredHidden);
                }

                // 背上／腰間未持在手上的武器一併隱藏，但正在手上的武器（inHandsItem）保留
                foreach (var slot in HolsteredWeaponSlots)
                    CollectSlotRenderers(body, slot, _desiredHidden, inHandsItem);
            }
            catch { _desiredHidden.Clear(); return; }

            // 釋放不再需要隱藏的渲染器（例如使用者在 F12 即時關閉 HideArmor）
            _toRestore.Clear();
            foreach (var kv in _hidden)
            {
                if (kv.Key == null || !_desiredHidden.Contains(kv.Key))
                    _toRestore.Add(kv.Key);
            }
            foreach (var r in _toRestore)
            {
                if (r != null)
                {
                    var orig = _hidden[r];
                    r.shadowCastingMode = orig.mode != ShadowCastingMode.Off ? orig.mode : ShadowCastingMode.On;
                    r.enabled = orig.enabled;
                }
                _hidden.Remove(r);
            }

            foreach (var r in _desiredHidden)
            {
                if (!_hidden.ContainsKey(r))
                    _hidden[r] = (r.shadowCastingMode, r.enabled);
                if (r.enabled) r.enabled = false;
                if (r.shadowCastingMode != ShadowCastingMode.Off) r.shadowCastingMode = ShadowCastingMode.Off;
            }
            _desiredHidden.Clear();
        }

        internal void EnforceEveryFrame()
        {
            foreach (var kv in _hidden)
            {
                var r = kv.Key;
                if (r == null) continue;
                if (r.enabled) r.enabled = false;
                if (r.shadowCastingMode != ShadowCastingMode.Off) r.shadowCastingMode = ShadowCastingMode.Off;
            }
        }

        internal void RestoreAll()
        {
            foreach (var kv in _hidden)
            {
                var r = kv.Key;
                if (r == null) continue;
                r.shadowCastingMode = kv.Value.mode != ShadowCastingMode.Off ? kv.Value.mode : ShadowCastingMode.On;
                r.enabled = kv.Value.enabled;
            }
            _hidden.Clear();
        }

        internal static void CollectSlotRenderers(PlayerBody body, EquipmentSlot slot, HashSet<Renderer> into, Item skipItem = null)
        {
            if (body == null || body.SlotViews == null || !body.SlotViews.ContainsKey(slot)) return;
            var sv = body.SlotViews.GetByKey(slot);
            if (sv == null) return;
            if (skipItem != null && sv.ContainedItem != null && sv.ContainedItem.Value == skipItem)
                return;

            var parentedGo = sv.ParentedModel != null ? sv.ParentedModel.Value : null;
            var modelGo = sv.Model;
            if (skipItem != null && (IsUnderWeaponRoot(parentedGo) || IsUnderWeaponRoot(modelGo)))
                return;

            if (parentedGo != null) AddRenderersInChildren(parentedGo, into);
            if (modelGo != null && modelGo != parentedGo) AddRenderersInChildren(modelGo, into);

            if (sv.Renderers != null)
            {
                for (int i = 0; i < sv.Renderers.Length; i++)
                    AddIfValid(sv.Renderers[i], into);
            }

            if (sv.MainDress != null && sv.MainDress.Value != null)
                AddDressRenderers(sv.MainDress.Value, into);

            if (sv.Dresses != null)
            {
                for (int i = 0; i < sv.Dresses.Length; i++)
                    AddDressRenderers(sv.Dresses[i], into);
            }
        }

        private static void AddDressRenderers(EFT.Visual.Dress dress, HashSet<Renderer> into)
        {
            if (dress == null) return;
            AddRenderersInChildren(dress.gameObject, into);
            if (dress.Renderers != null)
            {
                for (int i = 0; i < dress.Renderers.Length; i++)
                    AddIfValid(dress.Renderers[i], into);
            }
            try
            {
                var bodyR = dress.GetBodyRenderer();
                if (bodyR.Renderers != null)
                {
                    for (int i = 0; i < bodyR.Renderers.Length; i++)
                        AddIfValid(bodyR.Renderers[i], into);
                }
            }
            catch { }
            try
            {
                var rs = dress.GetRenderers();
                if (rs != null)
                {
                    foreach (var r in rs)
                        AddIfValid(r, into);
                }
            }
            catch { }
        }

        private static void AddRenderersInChildren(GameObject go, HashSet<Renderer> into)
        {
            if (go == null) return;
            _renderBuf.Clear();
            go.GetComponentsInChildren(true, _renderBuf);
            for (int i = 0; i < _renderBuf.Count; i++)
                AddIfValid(_renderBuf[i], into);
        }

        private static bool IsUnderWeaponRoot(GameObject go)
        {
            if (go == null) return false;
            for (var t = go.transform; t != null; t = t.parent)
            {
                var n = t.name;
                if (!string.IsNullOrEmpty(n) && n.StartsWith("Weapon_root", StringComparison.OrdinalIgnoreCase))
                    return true;
            }
            return false;
        }

        private static void AddIfValid(Renderer r, HashSet<Renderer> into)
        {
            if (r != null && (r.name == null || !r.name.StartsWith("DollFrame_", StringComparison.Ordinal)))
                into.Add(r);
        }
    }
}
