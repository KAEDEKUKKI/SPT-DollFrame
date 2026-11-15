using System;
using System.Collections.Generic;
using EFT;
using UnityEngine;

namespace DollFrame
{
    /// <summary>
    /// THE single game-bone resolver for both rigs: the raid PlayerBones chain and the
    /// menu preview skeleton (raw root, standard "Base Human*" naming). Two separate
    /// copies of these rules used to exist and once drifted (the preview copy missed
    /// Spine2/Calf/Forearm2-3 in an earlier revision), so one class now owns every key
    /// rule and the shared guard set; both entry points move the former per-mode bodies
    /// verbatim - no key resolves differently than before.
    /// </summary>
    internal static class GameBones
    {
        /// <summary>
        /// The required game anchors both rig modes must deliver (unified guard set, the
        /// union of the two former per-mode lists). The preview palm repair runs before
        /// this check, so palms can never fail it there - as before; AnchorKeys is also
        /// the cross-check basis against BoneContract in the offline harness.
        /// </summary>
        internal static readonly string[] AnchorKeys =
        {
            "pelvis", "spine1", "spine3", "neck", "head",
            "upperarmL", "upperarmR", "forearmL", "forearmR", "palmL", "palmR",
            "thigh1L", "thigh1R", "thigh2L", "thigh2R",
        };

        /// <summary>Raid rig: resolves the game anchors from the player's live bone table.</summary>
        internal static Dictionary<string, Transform> FromPlayerBones(PlayerBones pb)
        {
            var g = new Dictionary<string, Transform>();
            g["pelvis"] = Or(pb.Pelvis);
            g["spine1"] = pb.Spine1;
            g["spine2"] = FindChild(g["spine1"], "Spine2");
            var spine3 = g["spine2"] != null ? FindChild(g["spine2"], "Spine3") : null;
            g["ribcage"] = Or(pb.Ribcage);
            // spine3 = the real chest bone of the humanoid chain; the Ribcage transform is
            // only an equipment anchor beside the neck (verified against the shipped skeleton prefab)
            g["spine3"] = spine3 != null ? spine3 : g["ribcage"];
            g["neck"] = pb.Neck;
            g["head"] = Or(pb.Head);

            var rib = Or(pb.Ribcage);
            var upperL = BySide(pb.Upperarms, wantLeft: true);
            var upperR = BySide(pb.Upperarms, wantLeft: false);
            var foreL = BySide(pb.Forearms, wantLeft: true);
            var foreR = BySide(pb.Forearms, wantLeft: false);
            // EFT skeleton places Base HumanRCollarbone at child index 0 and Base HumanLCollarbone at child index 1;
            // resolve explicitly by L/R side (and parent of Upperarm) so L/R collarbones are never swapped.
            g["collarL"] = FindChild(rib, "HumanLCollarbone") ?? FindChild(rib, "LCollarbone")
                ?? FindChild(rib, "HumanLClavicle") ?? FindChild(rib, "LClavicle")
                ?? (upperL != null && upperL.parent != rib ? upperL.parent : null)
                ?? ChildBySnippet(rib, "ollarbone", 1);
            g["collarR"] = FindChild(rib, "HumanRCollarbone") ?? FindChild(rib, "RCollarbone")
                ?? FindChild(rib, "HumanRClavicle") ?? FindChild(rib, "RClavicle")
                ?? (upperR != null && upperR.parent != rib ? upperR.parent : null)
                ?? ChildBySnippet(rib, "ollarbone", 0);
            g["upperarmL"] = upperL;
            g["upperarmR"] = upperR;
            g["forearmL"] = foreL;
            g["forearmR"] = foreR;
            g["palmL"] = pb.LeftPalm;
            g["palmR"] = pb.RightPalm;
            g["thigh1L"] = Or(pb.LeftThigh1);
            g["thigh1R"] = Or(pb.RightThigh1);
            g["thigh2L"] = Or(pb.LeftThigh2);
            g["thigh2R"] = Or(pb.RightThigh2);
            g["calfL"] = FindChild(g["thigh2L"], "Calf");
            g["calfR"] = FindChild(g["thigh2R"], "Calf");
            // in the shipped rig the ankle sits on the Calf bone, not on Thigh2
            var footL = FindChild(g["calfL"], "Foot") ?? ChildBySnippet(Or(pb.LeftThigh2), "oot", 0);
            var footR = FindChild(g["calfR"], "Foot") ?? ChildBySnippet(Or(pb.RightThigh2), "oot", -1);
            g["footL"] = footL;
            g["footR"] = footR;
            g["toeL"] = footL != null ? FindChild(footL, "Toe") : null;
            g["toeR"] = footR != null ? FindChild(footR, "Toe") : null;

            var missing = Missing(g);
            if (missing.Count > 0)
            {
                Plugin.Log.LogError($"[DollFrame] game anchors unresolved: {string.Join(", ", missing)} - body replace aborted");
                return null;
            }
            return g;
        }

        /// <summary>
        /// Menu preview rig: the skeleton root is named by the standard "Base Human*"
        /// humanoid convention, so anchors resolve by name rules over the whole tree.
        /// </summary>
        internal static Dictionary<string, Transform> FromSkeletonRoot(Transform skel, bool logMissing = true)
        {
            if (skel == null) return null;
            var g = new Dictionary<string, Transform>();
            foreach (var t in skel.GetComponentsInChildren<Transform>(true))
            {
                if (t == null || IsUnderWeaponSubtree(t, skel)) continue;
                var n = t.name;
                if (string.IsNullOrEmpty(n) || !n.StartsWith("Base Human")) continue;
                if (n == "Base HumanPelvis") { g["pelvis"] = t; continue; }
                if (n.Contains("Spine1")) { if (!g.ContainsKey("spine1")) g["spine1"] = t; continue; }
                if (n.Contains("Spine2")) { if (!g.ContainsKey("spine2")) g["spine2"] = t; continue; }
                if (n.Contains("Spine3")) { if (!g.ContainsKey("spine3")) g["spine3"] = t; continue; }
                if (n.Contains("Ribcage")) { if (!g.ContainsKey("ribcage")) g["ribcage"] = t; continue; }
                if (n.Contains("Neck")) { if (!g.ContainsKey("neck")) g["neck"] = t; continue; }
                if (n == "Base HumanHead") { g["head"] = t; continue; }
                bool isL = n.Contains("HumanL");
                bool isR = n.Contains("HumanR");
                if (!isL && !isR) continue;
                string side = isL ? "L" : "R";
                string key = null;
                if (n.Contains("Collarbone") || n.Contains("Clavicle")) key = "collar" + side;
                else if (n.Contains("Upperarm")) key = "upperarm" + side;
                else if (n.Contains("Forearm")) key = "forearm" + side;
                else if (n.Contains("Palm")) key = "palm" + side;
                else if (n.Contains("Thigh1")) key = "thigh1" + side;
                else if (n.Contains("Thigh2")) key = "thigh2" + side;
                else if (n.Contains("Calf")) key = "calf" + side;
                else if (n.Contains("Foot")) key = "foot" + side;
                else if (n.Contains("Toe")) key = "toe" + side;
                if (key == null) continue;
                if (g.ContainsKey(key) && key.StartsWith("forearm") || g.ContainsKey(key) && key.StartsWith("palm")) continue;
                if (!g.ContainsKey(key)) g[key] = t;
            }

            // palm repair before the shared guard: a missing palm falls back to the
            // forearm (never fails the palm anchors; the forearms are required anyway).
            // Null-safe on the lookup: a degenerate rig that lost the forearm subtree
            // reaches the shared guard's warning instead of a KeyNotFound crash.
            if (!g.ContainsKey("palmL") || !g.ContainsKey("palmR"))
            {
                if (!g.ContainsKey("palmL")) g["palmL"] = g.TryGetValue("forearmL", out var foreL) ? foreL : null;
                if (!g.ContainsKey("palmR")) g["palmR"] = g.TryGetValue("forearmR", out var foreR) ? foreR : null;
            }

            var missing = Missing(g);
            if (missing.Count > 0)
            {
                if (logMissing)
                    Plugin.Log.LogWarning("[DollFrame] skeleton bones unresolved: " + string.Join(", ", missing));
                return null;
            }
            return g;
        }

        /// <summary>
        /// Silent pre-check for candidate Root_Joint transforms in menu preview scenes:
        /// returns true only when the transform subtree contains all required humanoid anchors.
        /// </summary>
        internal static bool HasAllRequiredAnchors(Transform skel)
            => skel != null && FromSkeletonRoot(skel, logMissing: false) != null;

        /// <summary>
        /// Boot-time contract self-check (SPEC log contract #1): runs once per attach right
        /// after resolution and BEFORE any rig build, asserting every BoneContract entry
        /// on the live rig - SpanGroups anchors + non-digit BoneMap keys against the
        /// resolved table, Digits through the same palm lookups CharacterRig.DigitAnchors
        /// uses. Returns resolved/total anchor counts, the resolved digit count and the
        /// missing anchor keys in contract order. An unresolved entry never drives
        /// blindly: the rig pairing skips that bone (fail closed - it inherits rigidly
        /// from its mapped ancestors). Digits carry no fit group of their own, so a
        /// shortfall shows as `digits &lt; 30` rather than a skip line.
        /// </summary>
        internal static (int resolved, int total, int digits, List<string> missing) ContractCheck(
            Dictionary<string, Transform> games)
        {
            var anchors = CharacterRig.DigitAnchors(games);
            var missing = new List<string>();
            var seen = new HashSet<string>();
            int total = 0, resolved = 0;
            void Check(string key)
            {
                if (!seen.Add(key)) return;
                total++;
                if (anchors.TryGetValue(key, out var t) && t != null) resolved++;
                else missing.Add(key);
            }
            foreach (var row in BoneContract.SpanGroups) { Check(row.anchorL); Check(row.anchorR); }
            foreach (var (_, gameKey) in BoneContract.BoneMap)
                if (Array.IndexOf(BoneContract.Digits, gameKey) < 0) Check(gameKey);
            int digits = 0;
            foreach (var d in BoneContract.Digits)
                if (anchors.TryGetValue(d, out var t) && t != null) digits++;
            return (resolved, total, digits, missing);
        }

        /// <summary>
        /// The per-attach contract log block (SPEC log contract #1): the pinned one-line
        /// contract line (fallback = CharacterRig.RigStats.skipped - mapped rows whose
        /// game anchor resolved but produced no driver pairing: self-anchored pairs or
        /// degenerate directions, which inherit rigidly instead) plus one [DollFrame] contract
        /// skip line per unresolved entry; callers append this to the SAME attach log.
        /// Once per attach - never per frame.
        /// </summary>
        internal static string ContractLine((int resolved, int total, int digits, List<string> missing) check)
        {
            var line = $"[DollFrame] contract: {check.resolved}/{check.total} anchors, digits {check.digits}, fallback {CharacterRig.RigStats.skipped}";
            foreach (var m in check.missing)
                line += $"\n[DollFrame] contract skip: {m} (unresolved)";
            return line;
        }

        /// <summary>
        /// Pristine 基線（原 SkeletonFit 基礎建設，2026-11-14 隨 fit 退役移入）：首次接觸之
        /// 原版 localPosition 快照，供 GameRigDump 在任何時期讀得乾淨的遊戲骨架量測。
        /// </summary>
        private static readonly Dictionary<int, Vector3> PristineLocalPos = new Dictionary<int, Vector3>();

        internal static void CapturePristineBaseline(Transform rootJoint)
        {
            if (rootJoint == null) return;
            foreach (var t in rootJoint.GetComponentsInChildren<Transform>(true))
            {
                if (t == null || IsUnderWeaponSubtree(t, rootJoint)) continue;
                int id = t.GetInstanceID();
                if (!PristineLocalPos.ContainsKey(id))
                    PristineLocalPos[id] = t.localPosition;
            }
        }

        internal static Vector3 GetPristineLocalPosition(Transform t)
        {
            if (t == null) return Vector3.zero;
            int id = t.GetInstanceID();
            if (PristineLocalPos.TryGetValue(id, out var pos)) return pos;
            return t.localPosition;
        }

        /// <summary>
        /// 排除掛載在 Ribcage 下的第一人稱手部骨架（Weapon_root_anim／SkeletonHands）與展示武器
        /// （Weapon_root_3rd_anim／WeaponPrefab）子樹內的同名 Base Human* 克隆骨，確保解析與量測
        /// 100% 取自第三人稱素體骨架。
        /// </summary>
        internal static bool IsUnderWeaponSubtree(Transform t, Transform stopAt)
        {
            for (var p = t; p != null && p != stopAt; p = p.parent)
            {
                var n = p.name;
                if (!string.IsNullOrEmpty(n) && (n.StartsWith("Weapon_root", StringComparison.OrdinalIgnoreCase)
                    || n.StartsWith("DollFrame_", StringComparison.Ordinal)))
                    return true;
                if (p.GetComponent<WeaponPrefab>() != null)
                    return true;
            }
            return false;
        }

        /// <summary>
        /// The metric rig dump for menu/raid factor parity (todo 25): "Base Human*" bone
        /// names -> localPosition, exactly the analysis/eft_skeleton.json shape
        /// ModelMetrics.Compute reads (a span's game meters = summed |pos| of its bones).
        /// Captured at attach on both paths (the vanilla game skeletons are never
        /// deformed), so the 12 ModelMetrics factors of one compare the two rigs' own
        /// proportions - menu/raid rig equality is unproven (todo 25), and this dump is
        /// the neutral measure of both. Once per attach - never per frame.
        /// </summary>
        internal static Dictionary<string, Vector3> GameRigDump(Transform root)
        {
            var dump = new Dictionary<string, Vector3>();
            if (root == null) return dump;
            CapturePristineBaseline(root);
            foreach (var t in root.GetComponentsInChildren<Transform>(true))
            {
                if (t == null || IsUnderWeaponSubtree(t, root)) continue;
                var n = t.name;
                if (string.IsNullOrEmpty(n) || !n.StartsWith("Base Human", StringComparison.Ordinal)) continue;
                if (!dump.ContainsKey(n)) dump[n] = GetPristineLocalPosition(t);
            }
            return dump;
        }

        /// <summary>
        /// The per-attach menu-vs-raid factor parity line (todo 25): compares the 12
        /// logical ModelMetrics factors of the menu attach (menu) against the raid-attach
        /// factors of the SAME package (raid = BodyReplacer.LastFactorsByPackage), warning
        /// when |menu-raid|/raid exceeds 2% - menu/raid rig equality is unproven, so the
        /// divergence is a WARNING, never a failure. Unmeasured spans (factor 0) and
        /// logical groups absent from one side carry no baseline number. Returns null to
        /// stay silent (identical/near-identical factor sets log NOTHING - todo 25 happy
        /// scenario), "no baseline" when raid factors are unavailable (no raid attach
        /// yet), else the pinned single line
        /// [DollFrame] parity: &lt;pkg&gt; &lt;label&gt; menu &lt;m&gt; raid &lt;r&gt; (&gt;2%)
        /// listing the divergent groups only. Pure over its inputs - the offline parity
        /// harness unit-tests it with synthetic factor sets; callers emit the returned
        /// line at most once per attach.
        /// </summary>
        internal static string ParityLine(string pkg, IReadOnlyDictionary<string, float> menu,
            IReadOnlyDictionary<string, float> raid)
        {
            if (menu == null || raid == null || menu.Count == 0 || raid.Count == 0)
                return "[DollFrame] parity: no baseline";
            var div = new List<string>();
            foreach (var kv in menu)
            {
                if (!raid.TryGetValue(kv.Key, out var r) || r <= 0f || kv.Value <= 0f) continue;
                if (Math.Abs(kv.Value - r) / r > 0.02f)
                    div.Add($"{kv.Key} menu {kv.Value:0.###} raid {r:0.###} (>2%)");
            }
            return div.Count > 0 ? $"[DollFrame] parity: {pkg} {string.Join(", ", div)}" : null;
        }

        /// <summary>Keys of AnchorKeys neither exposed (preview mode) nor non-null by the resolved table.</summary>
        private static List<string> Missing(Dictionary<string, Transform> g)
        {
            var missing = new List<string>();
            foreach (var k in AnchorKeys)
                if (!g.TryGetValue(k, out var t) || t == null) missing.Add(k);
            return missing;
        }

        private static Transform Or(BifacialTransform b) => b != null ? b.Original : null;

        /// <summary>First direct child whose name contains the snippet (case-insensitive).</summary>
        private static Transform FindChild(Transform root, string snippet)
        {
            if (root == null) return null;
            for (int i = 0; i < root.childCount; i++)
            {
                var c = root.GetChild(i);
                if (c != null && c.name != null && c.name.IndexOf(snippet, StringComparison.OrdinalIgnoreCase) >= 0)
                    return c;
            }
            return null;
        }

        private static Transform BySide(Transform[] arr, bool wantLeft)
        {
            if (arr == null || arr.Length == 0) return null;
            var want = wantLeft ? "HumanL" : "HumanR";
            foreach (var t in arr)
                if (t != null && t.name != null && t.name.Contains(want))
                    return t;
            var fallback = wantLeft ? 0 : arr.Length - 1;
            return arr[fallback]; // PlayerBones arrays are L-first by game convention
        }

        private static Transform ChildBySnippet(Transform root, string snippet, int nth)
        {
            if (root == null) return null;
            int seen = 0;
            Transform last = null;
            for (int i = 0; i < root.childCount; i++)
            {
                var c = root.GetChild(i);
                if (c == null || c.name == null) continue;
                if (c.name.IndexOf(snippet, StringComparison.OrdinalIgnoreCase) < 0) continue;
                if (nth >= 0 && seen == nth) return c;
                last = c;
                seen++;
            }
            return nth < 0 ? last : null;
        }
    }
}
