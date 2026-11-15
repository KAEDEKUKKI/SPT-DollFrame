using System;
using System.Collections.Generic;

namespace GF2
{
    /// <summary>
    /// THE single anatomical contract: PMX bone name -> game anchor key, shared by the
    /// rig (CharacterRig build + retarget pairing) and the parser/test builds. Data-only
    /// plus pure helpers - no Unity types, compiles against the parser-only stubs.
    /// Span rows list the L-side PMX span pair and the terminal game anchor of the
    /// measured chain per side; non-mirrored spine/hip rows repeat the anchor on both
    /// sides (the L chain terminal of each row starts where the previous row's terminal
    /// ended, following the shipped rig chain Root_Joint-Pelvis-Spine1-2-3-Neck,
    /// Collar-Upperarm-Forearm1-2-3-Palm, Pelvis-Thigh1-Thigh2-Calf-Foot-Toe). R-side PMX
    /// names mirror the L side via 左-&gt;右 (MirrorPmx), as in ModelMetrics. The FP arm
    /// section carries no entries (the 2026-11-14 view-module retirement deleted the
    /// FP slot consumer; real FP bone rows are not part of this contract)
    /// invented entries exist. Consumers rewire in todos 21/22; the boot-time self-check
    /// is todo 24 and must not be wired here.
    /// </summary>
    internal static class BoneContract
    {
        /// <summary>
        /// The 12 anatomical span groups: (group, pmxA, pmxB, anchorL, anchorR). pmxA/pmxB
        /// are the PMX joint pairs exactly as in ModelMetrics (L side as authored);
        /// anchorL/anchorR are BodyReplacer resolver keys - hips anchors the vertical
        /// Root_Joint->Pelvis height on pelvis for both ends.
        /// </summary>
        internal static readonly (string group, string pmxA, string pmxB, string anchorL, string anchorR)[] SpanGroups =
        {
            ("hips",      "下半身",  "左足首先", "pelvis",    "pelvis"),
            ("spineRoot", "下半身",  "上半身",   "spine1",    "spine1"),
            ("torso",     "上半身",  "上半身2",  "spine3",    "spine3"),
            ("chestNeck", "上半身2", "首",       "neck",      "neck"),
            ("neckHead",  "首",      "頭",       "head",      "head"),
            ("hipOffset", "下半身",  "左足",     "thigh1L",   "thigh1R"),
            ("thigh",     "左足",    "左ひざ",   "calfL",     "calfR"),
            ("shank",     "左ひざ",  "左足首",   "footL",     "footR"),
            ("foot",      "左足首",  "左足首先", "toeL",      "toeR"),
            ("clavicle",  "左肩",    "左腕",     "upperarmL", "upperarmR"),
            ("uarm",      "左腕",    "左ひじ",   "forearmL",  "forearmR"),
            ("farm",      "左ひじ",  "左手首",   "palmL",     "palmR"),
        };

        /// <summary>
        /// pmx jp bone name -> game key, the 53-row map (retarget pairing + FP slot
        /// resolution). Fingers use the "digit{f}_{s}{side}" anchors resolved from the
        /// palm transform (see Digits); unlisted bones follow their nearest mapped
        /// ancestor via the PMX parent chain (FP slots) or their own rig node (the rig
        /// binds every weighted bone to its instance node - no slot fallback there).
        /// </summary>
        internal static readonly (string pmxBone, string gameKey)[] BoneMap =
        {
            ("下半身", "pelvis"), ("腰", "pelvis"), ("センター", "pelvis"),
            ("上半身", "spine1"), ("上半身2", "spine3"), ("首", "neck"), ("頭", "head"),

            ("左肩", "collarL"),  ("右肩", "collarR"),
            ("左腕", "upperarmL"), ("右腕", "upperarmR"),
            ("左ひじ", "forearmL"), ("右ひじ", "forearmR"),
            ("左手首", "palmL"),  ("右手首", "palmR"),

            ("左親指０", "digit1_1L"), ("左親指１", "digit1_2L"), ("左親指２", "digit1_3L"),
            ("右親指０", "digit1_1R"), ("右親指１", "digit1_2R"), ("右親指２", "digit1_3R"),
            ("左人指１", "digit2_1L"), ("左人指２", "digit2_2L"), ("左人指３", "digit2_3L"),
            ("右人指１", "digit2_1R"), ("右人指２", "digit2_2R"), ("右人指３", "digit2_3R"),
            ("左中指１", "digit3_1L"), ("左中指２", "digit3_2L"), ("左中指３", "digit3_3L"),
            ("右中指１", "digit3_1R"), ("右中指２", "digit3_2R"), ("右中指３", "digit3_3R"),
            ("左薬指１", "digit4_1L"), ("左薬指２", "digit4_2L"), ("左薬指３", "digit4_3L"),
            ("右薬指１", "digit4_1R"), ("右薬指２", "digit4_2R"), ("右薬指３", "digit4_3R"),
            ("左小指１", "digit5_1L"), ("左小指２", "digit5_2L"), ("左小指３", "digit5_3L"),
            ("右小指１", "digit5_1R"), ("右小指２", "digit5_2R"), ("右小指３", "digit5_3R"),

            // the fitted thigh span is Thigh1->Thigh2->Calf, so the model knee = game Calf
            ("左足", "thigh1L"),  ("右足", "thigh1R"),
            ("左ひざ", "calfL"),  ("右ひざ", "calfR"),
            ("左足首", "footL"),  ("右足首", "footR"),
            ("左足首先", "toeL"), ("右足首先", "toeR"),
        };

        /// <summary>The 30 digit anchor keys resolved from the palms (AddDigitBones).</summary>
        internal static readonly string[] Digits =
        {
            "digit1_1L", "digit1_2L", "digit1_3L", "digit2_1L", "digit2_2L", "digit2_3L",
            "digit3_1L", "digit3_2L", "digit3_3L", "digit4_1L", "digit4_2L", "digit4_3L",
            "digit5_1L", "digit5_2L", "digit5_3L",
            "digit1_1R", "digit1_2R", "digit1_3R", "digit2_1R", "digit2_2R", "digit2_3R",
            "digit3_1R", "digit3_2R", "digit3_3R", "digit4_1R", "digit4_2R", "digit4_3R",
            "digit5_1R", "digit5_2R", "digit5_3R",
        };

        /// <summary>
        /// The 21 canonical PMX bones every packaged model must contain (the required
        /// matrix bone set, as in ModelMetrics).
        /// </summary>
        internal static readonly string[] RequiredAnchors =
        {
            "下半身", "上半身", "上半身2", "首", "頭",
            "左肩", "右肩", "左腕", "右腕",
            "左ひじ", "右ひじ", "左手首", "右手首",
            "左足", "右足", "左ひざ", "右ひざ",
            "左足首", "右足首", "左足首先", "右足首先"
        };

        /// <summary>
        /// Vanilla rest pelvis height above Root_Joint (m), pinned to
        /// analysis/eft_skeleton.json ("Base HumanPelvis" local Y = 0.928). The spine fit's
        /// target uses this instead of the attach-frame live pelvis height: the spawn pose's
        /// pelvis sway leaked into the whole-raid spine scale (measured 2026-11-07 raid log:
        /// spine x0.825 in raid vs x0.791 in the standing menu preview on the identical
        /// skeleton), riding the FP camera height and the ADS eye line for the entire raid.
        /// </summary>
        internal const float GamePelvisRestY = 0.928f;

        /// <summary>
        /// PMX bones whose rig drive is a PURE game-rotation-delta copy (no direction aim):
        /// currently only 下半身 (pelvis). Reason (2026-11-07 refactor): in the GFL2 layout
        /// 上半身 is a SIBLING of 下半身 (both children of 腰 - koleda idx6/idx5 both p4), so
        /// the only mapped descendants of 下半身 are the LEG chains - FirstMappedDescendant
        /// pairs the pelvis with 左足 (thigh1L). That aim points the model hip axis
        /// (46+ degrees splayed from vertical) at the near-vertical game thigh axis and
        /// bakes a permanent around-forward roll into RestOffset at attach (thigh1 is a
        /// direct game child of pelvis -> GameHops=1 -> baked CopyTarget path forever),
        /// which IS the tilted pelvis + crooked feet. A pure delta-copy
        /// target = G_now∘G0⁻¹∘C0 tracks the game pelvis's rotation delta while the rig
        /// keeps its authored attach orientation - zero static tilt by construction.
        /// Rows here must also exist in BoneMap (the anchor still drives the entry and
        /// the root translation tracking).
        /// </summary>
        internal static readonly HashSet<string> PureDeltaPmxBones =
            new HashSet<string>(StringComparer.Ordinal) { "下半身" };

        /// <summary>
        /// PMX bones driven by a PARENT-FRAME game-delta copy (no direction aim): the 4
        /// foot/toe bones. Reason (2026-11-07 raid: ground-check +8.9cm constant ×3): the
        /// MMD ankle sits high-behind with the toe low-front, so the authored 足首→足首先
        /// axis is ~36° below horizontal while the planted game foot axis is near
        /// horizontal - the pairing-axis aim bakes that over-rotation into the foot at
        /// attach and lifts the toe off the ground (0.183m toe span × sin36° ≈ 10.7cm ≈
        /// the measured 8.9cm after the game's own toe-angle contribution). A
        /// parent-frame delta copy keeps the authored foot shape and adds only the game
        /// foot's rotation delta relative to its own parent: authored flat sole at attach,
        /// correct heel/toe motion while walking, zero static lift.
        /// Rows here must also exist in BoneMap (the anchor still resolves the entry).
        /// </summary>
        internal static readonly HashSet<string> PureRelPmxBones =
            new HashSet<string>(StringComparer.Ordinal)
            {
                "左足首", "右足首", "左足首先", "右足首先",
            };

        /// <summary>Mirrors an L-side PMX name to its R-side twin (左 -&gt; 右), as ModelMetrics does for span pairs.</summary>
        internal static string MirrorPmx(string pmxName) => pmxName.Replace('左', '右');
    }
}
