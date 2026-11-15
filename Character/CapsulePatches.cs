using System.Collections.Generic;
using EFT;
using HarmonyLib;
using UnityEngine;

namespace GF2
{
    /// <summary>
    /// Capsule compensation. Unity's CharacterController ignores transform scale, so
    /// height/center/radius are re-applied by factor on every raw-constant write
    /// (MovementContext.CalculatePoseLevelValues writes absolute constants: no drift).
    /// The player prefab uses SimpleCharacterController (CapsuleCollider-based), bots may
    /// use PlayerCharacterController (Unity CC): both paths are compensated.
    /// </summary>
    internal static class CapsulePatches
    {
        internal static readonly Dictionary<CharacterController, float> UnityScale =
            new Dictionary<CharacterController, float>();

        internal static readonly Dictionary<CapsuleCollider, float> SimpleScale =
            new Dictionary<CapsuleCollider, float>();

        private static readonly Dictionary<Collider, Vector3> VanillaCenter =
            new Dictionary<Collider, Vector3>();
        private static readonly Dictionary<Collider, float> VanillaRadius =
            new Dictionary<Collider, float>();
        private static readonly Dictionary<Collider, float> VanillaHeight =
            new Dictionary<Collider, float>();

        private static Vector3 ScaledY(Vector3 c, float s) => new Vector3(c.x, c.y * s, c.z);

        private static void SetControllerCapsule(CharacterController cc, Vector3 center, float radius, float height)
        {
            if (height < cc.height)
            {
                cc.height = height;
                cc.center = center;
            }
            else
            {
                cc.center = center;
                cc.height = height;
            }
            cc.radius = radius;
        }

        private static void SetColliderCapsule(CapsuleCollider col, Vector3 center, float radius, float height)
        {
            if (height < col.height)
            {
                col.height = height;
                col.center = center;
            }
            else
            {
                col.center = center;
                col.height = height;
            }
            col.radius = radius;
        }

        /// <summary>Live toggle entry: applies or resets capsule scale for a player.</summary>
        internal static void ApplyScale(Player player, float s)
        {
            if (!Plugin.CapsuleScaleEnabled.Value) return;
            if (player.CharacterController is PlayerCharacterController pcc)
            {
                var cc = pcc._characterController;
                if (cc == null) return;
                if (Mathf.Approximately(s, 1f))
                {
                    if (!UnityScale.TryGetValue(cc, out _)) return;
                    VanillaCenter.TryGetValue(cc, out var c0);
                    VanillaRadius.TryGetValue(cc, out float r0);
                    VanillaHeight.TryGetValue(cc, out float h0);
                    UnityScale.Remove(cc);
                    SetControllerCapsule(cc, c0, r0, h0);
                    Physics.SyncTransforms();
                    return;
                }
                if (!UnityScale.TryGetValue(cc, out _))
                {
                    UnityScale[cc] = s;
                    VanillaCenter[cc] = cc.center;
                    VanillaRadius[cc] = cc.radius;
                    VanillaHeight[cc] = cc.height;
                }
                else UnityScale[cc] = s;
                SetControllerCapsule(cc, ScaledY(VanillaCenter[cc], s), VanillaRadius[cc] * s, VanillaHeight[cc] * s);
                Physics.SyncTransforms();
            }
            else if (player.CharacterController is SimpleCharacterController scc)
            {
                var col = scc._mainCollider;
                if (col == null) return;
                if (Mathf.Approximately(s, 1f))
                {
                    if (!SimpleScale.TryGetValue(col, out _)) return;
                    VanillaCenter.TryGetValue(col, out var c0);
                    VanillaRadius.TryGetValue(col, out float r0);
                    VanillaHeight.TryGetValue(col, out float h0);
                    SimpleScale.Remove(col);
                    SetColliderCapsule(col, c0, r0, h0);
                    Physics.SyncTransforms();
                    return;
                }
                if (!SimpleScale.TryGetValue(col, out _))
                {
                    SimpleScale[col] = s;
                    VanillaCenter[col] = col.center;
                    VanillaRadius[col] = col.radius;
                    VanillaHeight[col] = col.height;
                }
                else SimpleScale[col] = s;
                SetColliderCapsule(col, ScaledY(VanillaCenter[col], s), VanillaRadius[col] * s, VanillaHeight[col] * s);
                Physics.SyncTransforms();
            }
        }

        /// <summary>
        /// Proxy-route capsule (single call site: BodyReplacer.Apply, after the rig build):
        /// the vanilla capsule pinned by <see cref="ApplyScale"/> is multiplied by
        /// modelHeight x HeightMultiplier / 1.83 (vanilla PMC reference height).
        /// </summary>
        internal static void ApplyDerived(Player player, float fittedHeightM)
        {
            if (!Plugin.CapsuleScaleEnabled.Value) return;
            float multiplier = Plugin.HeightMultiplier.Value;
            if (multiplier <= 0f)
            {
                Plugin.Log.LogWarning("[GF2] capsule: multiplier 0 -> vanilla");
                return;
            }
            float factor = (fittedHeightM * multiplier) / 1.83f;
            ApplyScale(player, factor);
            Plugin.Log.LogInfo($"[GF2] capsule: height {fittedHeightM:0.###}m x{factor:0.###} (derived)");
        }

        [HarmonyPostfix]
        [HarmonyPatch(typeof(PlayerCharacterController), "height", MethodType.Setter)]
        internal static void PostHeight(PlayerCharacterController __instance, float value)
        {
            var cc = __instance._characterController;
            if (cc != null && UnityScale.TryGetValue(cc, out float s))
                cc.height = value * s;
        }

        [HarmonyPostfix]
        [HarmonyPatch(typeof(PlayerCharacterController), "center", MethodType.Setter)]
        internal static void PostCenter(PlayerCharacterController __instance, Vector3 value)
        {
            var cc = __instance._characterController;
            if (cc != null && UnityScale.TryGetValue(cc, out float s))
                cc.center = ScaledY(value, s);
        }

        [HarmonyPostfix]
        [HarmonyPatch(typeof(PlayerCharacterController), "radius", MethodType.Setter)]
        internal static void PostRadius(PlayerCharacterController __instance, float value)
        {
            var cc = __instance._characterController;
            if (cc != null && UnityScale.TryGetValue(cc, out float s))
                cc.radius = value * s;
        }

        [HarmonyPostfix]
        [HarmonyPatch(typeof(SimpleCharacterController), "height", MethodType.Setter)]
        internal static void PostSccHeight(SimpleCharacterController __instance, float value)
        {
            var col = __instance._mainCollider;
            if (col != null && SimpleScale.TryGetValue(col, out float s))
                col.height = value * s;
        }

        [HarmonyPostfix]
        [HarmonyPatch(typeof(SimpleCharacterController), "center", MethodType.Setter)]
        internal static void PostSccCenter(SimpleCharacterController __instance, Vector3 value)
        {
            var col = __instance._mainCollider;
            if (col != null && SimpleScale.TryGetValue(col, out float s))
                col.center = ScaledY(value, s);
        }
    }
}