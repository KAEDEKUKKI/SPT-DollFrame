using System;
using System.Collections.Generic;
using EFT;
using UnityEngine;
using UnityEngine.Rendering;

namespace GF2
{
    /// <summary>
    /// 【Visibility 次要連動 2A：原版 PMC 素體與視角殘留部件隱藏器】
    /// 獨立管理被替換玩家身上的原版 PlayerBody.BodySkins 渲染器與第一人稱控制器下的
    /// PMC 素體部件（頭、衣服、褲子、手）之隱藏與狀態還原，與骨架數學完全解耦。
    /// </summary>
    internal sealed class VanillaBodyHider
    {
        // 原版身體渲染器 -> 原始陰影模式（釋放時完整還原）
        private readonly Dictionary<Renderer, ShadowCastingMode> _hidden =
            new Dictionary<Renderer, ShadowCastingMode>();
        private readonly HashSet<Renderer> _scratch = new HashSet<Renderer>();
        private readonly List<Renderer> _released = new List<Renderer>();


        // 還原路徑的一次性警告旗標（_operWarned 欄位模式；static 因 RestoreAllVanilla 為 static）
        private static bool _bodyRestoreWarned;

        private static readonly string[] ViewmodelWeaponTokens =
            { "weapon", "gun", "muzzle", "silencer", "grenade", "knife" };

        internal int HiddenCount => _hidden.Count;

        internal void SyncVanillaHidden(Player player)
        {
            _scratch.Clear();
            CollectVanilla(player, _scratch);

            _released.Clear();
            foreach (var kv in _hidden)
                if (kv.Key == null || !_scratch.Contains(kv.Key))
                    _released.Add(kv.Key);
            foreach (var r in _released)
            {
                if (r != null)
                {
                    r.shadowCastingMode = _hidden[r];
                    r.enabled = true;
                }
                _hidden.Remove(r);
            }

            foreach (var r in _scratch)
            {
                if (!_hidden.ContainsKey(r)) _hidden[r] = r.shadowCastingMode;
                if (r.enabled) r.enabled = false;
                if (r.shadowCastingMode != ShadowCastingMode.Off) r.shadowCastingMode = ShadowCastingMode.Off;
            }
            _scratch.Clear();
        }

        private static void CollectVanilla(Player player, HashSet<Renderer> into)
        {
            if (player == null) return;
            var body = player.PlayerBody;
            if (body != null && body.BodySkins != null)
            {
                foreach (var kv in body.BodySkins)
                {
                    if (kv.Value == null) continue;
                    foreach (var r in kv.Value.GetRenderers())
                        if (r != null) into.Add(r);
                }
            }
            // Also sweep any SkinnedMeshRenderer under the player hierarchy that is skinned
            // to the vanilla Base Human* skeleton (catches LOD1/head/hands/shirt/pants pieces
            // even when EFT detaches or swaps them outside BodySkins).
            // 重要：排除第一人稱 SkeletonHands（Weapon_root_anim）子樹，第一人稱維持原生 FP 手臂，
            // 避免每幀與 EFT 互搶 enabled 權限導致「若影若現的手」。
            var handsRoot = body != null && body.SkeletonHands != null ? body.SkeletonHands.transform : null;
            foreach (var smr in player.GetComponentsInChildren<SkinnedMeshRenderer>(true))
            {
                if (smr == null) continue;
                if (handsRoot != null && smr.transform.IsChildOf(handsRoot)) continue;
                if (UnderAncestorSnippet(smr.transform, "Weapon_root_anim")) continue;
                if (IsViewmodelBodyPiece(smr))
                    into.Add(smr);
            }
            // Hide vanilla pants pistol_holster static mesh attached under Pants_*
            foreach (var mr in player.GetComponentsInChildren<MeshRenderer>(true))
            {
                if (mr != null && mr.name != null &&
                    mr.name.IndexOf("pistol_holster", StringComparison.OrdinalIgnoreCase) >= 0 &&
                    UnderAncestorSnippet(mr.transform, "Pants_"))
                {
                    into.Add(mr);
                }
            }
        }

        /// <summary>
        /// Zero-allocation per-frame guard called every LateUpdate: ensures EFT's LODGroup
        /// or camera transitions can never re-enable already-collected vanilla PMC renderers
        /// for even a single frame between full hierarchy scans.
        /// </summary>
        internal void EnforceHiddenEveryFrame()
        {
            foreach (var kv in _hidden)
            {
                var r = kv.Key;
                if (r == null) continue;
                if (r.enabled) r.enabled = false;
                if (r.shadowCastingMode != ShadowCastingMode.Off) r.shadowCastingMode = ShadowCastingMode.Off;
            }
        }

        internal void RestoreVanilla()
        {
            foreach (var kv in _hidden)
            {
                if (kv.Key == null) continue;
                kv.Key.shadowCastingMode = kv.Value != ShadowCastingMode.Off ? kv.Value : ShadowCastingMode.On;
                kv.Key.enabled = true;
            }
            _hidden.Clear();
        }

        internal static void RestoreAllVanilla(Player player)
        {
            if (player == null) return;
            IkRefresh.RefreshIK(player);
            var body = player.PlayerBody;
            if (body != null && body.BodySkins != null)
            {
                foreach (var kv in body.BodySkins)
                {
                    if (kv.Value == null) continue;
                    foreach (var r in kv.Value.GetRenderers())
                    {
                        if (r != null)
                        {
                            r.enabled = true;
                            r.shadowCastingMode = ShadowCastingMode.On;
                        }
                    }
                }
                try
                {
                    var side = body.PlayerSide != null ? body.PlayerSide.Value : player.Side;
                    body.UpdatePlayerRenders(player.PointOfView, side);
                }
                catch (Exception ex)
                {
                    if (!_bodyRestoreWarned)
                    {
                        _bodyRestoreWarned = true;
                        Plugin.Log.LogWarning($"[GF2] warn: vanilla render restore failed ({ex.GetType().Name}: {ex.Message}) - body SMR restore enforcement continues");
                    }
                }
            }
            foreach (var r in player.GetComponentsInChildren<Renderer>(true))
            {
                if (r == null || (r.name != null && r.name.StartsWith("GF2_"))) continue;
                r.enabled = true;
                if (r.shadowCastingMode == ShadowCastingMode.Off)
                    r.shadowCastingMode = ShadowCastingMode.On;
            }

        }

        internal int PickLayer(Player player)
        {
            foreach (var kv in _hidden)
                if (kv.Key != null) return kv.Key.gameObject.layer;
            return player != null ? player.gameObject.layer : 0;
        }

        /// <summary>
        /// 判定第一人稱控制器下的 SkinnedMeshRenderer 是否屬於原版 PMC 身體部件（頭／衣／褲／手）。
        /// GF2 自建網格與武器本體網格一律排除；空手武器（empty_hands）本體為 PMC 裸手網格，納入隱藏。
        /// </summary>
        internal static bool IsViewmodelBodyPiece(SkinnedMeshRenderer r)
        {
            if (r == null) return false;
            if (r.name != null && r.name.StartsWith("GF2_", StringComparison.Ordinal)) return false;
            if (UnderAncestorSnippet(r.transform, "empty_hands")) return true;
            if (ContainsToken(r.name) || ContainsToken(r.gameObject != null ? r.gameObject.name : null)) return false;
            if (r.bones == null || r.bones.Length == 0) return false;
            if (r.rootBone != null && r.rootBone.name != null &&
                r.rootBone.name.StartsWith("Base Human", StringComparison.Ordinal)) return true;
            bool any = false, allHuman = true;
            foreach (var b in r.bones)
            {
                if (b == null) continue;
                if (b.name == null || !b.name.StartsWith("Base Human", StringComparison.Ordinal)) { allHuman = false; break; }
                any = true;
            }
            return any && allHuman;
        }

        private static bool ContainsToken(string s)
        {
            if (string.IsNullOrEmpty(s)) return false;
            foreach (var t in ViewmodelWeaponTokens)
                if (s.IndexOf(t, StringComparison.OrdinalIgnoreCase) >= 0) return true;
            return false;
        }

        private static bool UnderAncestorSnippet(Transform t, string snippet)
        {
            while (t != null)
            {
                if (t.name != null && t.name.IndexOf(snippet, StringComparison.OrdinalIgnoreCase) >= 0) return true;
                t = t.parent;
            }
            return false;
        }





        /// <summary>
        /// 在 InitVisuals prefix 提早同步隱藏原版皮膚，防止掛載完成前閃現 PMC。
        /// </summary>
        internal static void PreHideVanilla(Player player)
        {
            if (player == null) return;
            var body = player.PlayerBody;
            if (body != null && body.BodySkins != null)
            {
                foreach (var kv in body.BodySkins)
                {
                    if (kv.Value == null) continue;
                    foreach (var r in kv.Value.GetRenderers())
                    {
                        if (r != null && r.enabled)
                        {
                            r.enabled = false;
                            r.shadowCastingMode = ShadowCastingMode.Off;
                        }
                    }
                }
            }
            var handsRoot = body != null && body.SkeletonHands != null ? body.SkeletonHands.transform : null;
            foreach (var smr in player.GetComponentsInChildren<SkinnedMeshRenderer>(true))
            {
                if (smr == null || !smr.enabled) continue;
                if (handsRoot != null && smr.transform.IsChildOf(handsRoot)) continue;
                if (UnderAncestorSnippet(smr.transform, "Weapon_root_anim")) continue;
                if (smr.name == null || !smr.name.StartsWith("GF2_"))
                {
                    bool isHuman = smr.rootBone != null && smr.rootBone.name != null && smr.rootBone.name.Contains("Base Human");
                    if (!isHuman && smr.bones != null)
                    {
                        foreach (var b in smr.bones)
                        {
                            if (b != null && b.name != null && b.name.StartsWith("Base Human", StringComparison.Ordinal))
                            {
                                isHuman = true;
                                break;
                            }
                        }
                    }
                    if (isHuman)
                    {
                        smr.enabled = false;
                        smr.shadowCastingMode = ShadowCastingMode.Off;
                    }
                }
            }
        }
    }
}
