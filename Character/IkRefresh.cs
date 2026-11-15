using System;
using System.Reflection;
using EFT;
using UnityEngine;

namespace DollFrame
{
    /// <summary>Force Final IK solvers to re-cache limb data on the fitted transforms.</summary>
    internal static class IkRefresh
    {
        internal static void RefreshIK(Player player)
        {
            try
            {
                Toggle(GetBehaviour(player, "_fbbik"));
                Toggle(GetBehaviour(player, "Grounder"));
                if (FieldOf(player, "_limbs")?.GetValue(player) is Behaviour[] limbs)
                    foreach (var limb in limbs) Toggle(limb);
            }
            catch (Exception e)
            {
                Plugin.Log.LogWarning($"[DollFrame] IK refresh failed (non-fatal): {e.Message}");
            }
        }

        // EFT keeps these solver fields on private base classes; Type.GetField with
        // NonPublic|Instance flags does not walk the base type chain, so the exact-type
        // lookup returned null and the refresh silently no-op'd.
        private static Behaviour GetBehaviour(Player player, string name)
        {
            var f = FieldOf(player, name);
            if (f == null)
            {
                Plugin.Log.LogWarning($"[DollFrame] IK refresh: solver field '{name}' not found (non-fatal)");
                return null;
            }
            return f.GetValue(player) as Behaviour;
        }

        private static FieldInfo FieldOf(Player player, string name)
        {
            for (var t = player.GetType(); t != null; t = t.BaseType)
            {
                var f = t.GetField(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                if (f != null) return f;
            }
            return null;
        }

        private static void Toggle(Behaviour c)
        {
            if (c == null) return;
            c.enabled = false;
            c.enabled = true;
        }
    }
}
