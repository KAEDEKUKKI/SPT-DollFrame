// Character/CharacterRig.cs: 零拉伸獨立 PMX rig 的 Unity 執行段——Build 一次性掛載
// （bindpose ＋ 模型側全模型單一等比縮放 ModelScaler）、Drive 每幀僅寫 localRotation 的驅動器、
// FK 骨骼重定向＋臂鏈兩骨 IK（0.3.9 混合 FK＋臂鏈 IK 重寫：上臂/前臂由 Kinematics/TwoBoneIk 求解，
// Build 校準（rest 鏈＋具名掌錨＋chest-local pole）由 Kinematics/ArmRest 提供，其餘骨照常 FK）；
// 純數學在 Kinematics/RigAim、Kinematics/TwoBoneIk 與 Contract/。
using System;
using System.Collections.Generic;
using Snx = System.Numerics;
#if !DOLLFRAME_PMX_PARSER_ONLY
using UnityEngine;
using UnityEngine.Rendering;
#endif

namespace DollFrame
{
#if !DOLLFRAME_PMX_PARSER_ONLY
    /// <summary>0.3.9 臂鏈 IK 面骨分派：None＝純 FK 面；Upper＝上臂求解面（每幀兩骨 IK 生產者）；Fore＝前臂消費面（讀取同次求解的 ForeWorld）。</summary>
    internal enum ArmIkRole
    {
        None = 0,
        Upper = 1,
        Fore = 2,
        Clavicle = 3,
        Wrist = 4,
        Digit = 5,
    }

    /// <summary>
    /// ONE character rig instance (the pivot architecture): a full Transform tree - one
    /// node per PMX bone - hung under the game Root_Joint as an INDEPENDENT per-character
    /// skeleton, plus the SMR that renders the copied pmx mesh on it. Pivot placement is
    /// the pre-pivot proxy-rig recipe, kept verbatim: the placement root carries the unit scale, the
    /// model facing yaw and the ground offset (localPosition (0, yOffset, 0), yaw rotation,
    /// scale unitScale), every bone node uses pmx absolute/difference local positions and
    /// IDENTITY rotations at build. The mesh bind then is the exact decree formula
    ///   bindpose[i] = rigBone_i.worldToLocalMatrix * modelRootWorld,  modelRootWorld = rig ROOT l2w,
    /// so at build the skinned world vertex == modelRootWorld * v: the model renders
    /// exactly as PMX-authored with ZERO stretch by construction, and because the driver
    /// only ever writes local rotations (never positions/scales) the rig stays zero-stretch
    /// every frame while the per-bone aim retarget (RigAim) tracks the vanilla game
    /// skeleton's motion. The GAME skeleton is never modified.
    /// 2026-11-14-6 全模型單一等比改約：Build 時以 ModelScaler 於 rig 根一次性乘入腿錨定等比
    /// 因子 k（全身原比例不變形，臂鏈個別縮放退役）；uniform 乘入之外每幀仍僅 localRotation。
    /// </summary>
    internal sealed class CharacterRig
    {
        internal Transform Root;
        internal Transform[] Bones;
        internal SkinnedMeshRenderer Skin;
        internal SkinnedMeshRenderer HeadSkin;
        internal Mesh Mesh;
        internal Mesh HeadMesh;
        /// <summary>SDF 臉部陰影驅動（sdf_face.png 在包內時建立；隨 rig 拆除 TerminateSdf）。</summary>
        internal SdfFaceDriver SdfFace;
        private Transform _sdfHead;
        private Transform _headRig;
        private Transform _headGame;

        private sealed class DriveEntry
        {
            internal int PmxBone;              // rig bone index (pmx parent-first order)
            internal Transform RigBone;
            internal int ParentIndex;          // pmx parent for the local composition
            internal Transform GameBone;       // the bone's own game anchor
            internal Transform GameMate;       // game-side direction partner (child anchor; leaf: parent anchor)
            internal bool Leaf;                // dG = bone - mate (leaf) else mate - bone
            internal bool PureDelta;           // BoneContract.PureDeltaPmxBones: no aim at all,
                                               // target = G_now∘R_off with R_off = G0⁻¹∘C0 (the
                                               // pelvis exemption - the descendant pairing is a leg anchor)
            internal bool PureRel;             // BoneContract.PureRelPmxBones (foot/toe): parent-frame
                                               // game-delta copy, target = parentWorld∘AuthoredRel∘(GRel(t)∘GRel0⁻¹)
            internal Transform GameParent;     // PureRel only: the game anchor's own parent
            internal Quaternion AuthoredRel;   // PureRel only: C0 ∘ parentC0⁻¹ captured at attach
            internal Quaternion GameRel0;      // PureRel only: GP0⁻¹∘G0 captured at attach
            internal Vector3 DirC;             // attach-frame unit direction of the character pairing
            internal Vector3 DirCPerp;         // rig attach-frame roll reference (⊥ DirC, TryBindLiveFrame)
            internal Vector3 GameSecLocal;     // roll reference in the game bone's local frame
            internal Quaternion WorldRotAttach; // rig bone world rotation captured at build
            internal Quaternion RestOffset;     // G0⁻¹ ∘ aim(C0): baked-copy fallback (degenerate frames)
            internal int PmzMate;               // pmx mate node index (the rig-side pairing pivot, parity telemetry)
            internal int GameHops;              // game-hierarchy steps from the mate anchor up to the bone's own
                                                // anchor; >1 = the span crosses animated intermediates, the only
                                                // case the live two-vector path was built for (Spine2/Thigh2/Forearm2-3).
                                                // leaf entries are always 1 (baked): a leaf's pairing direction is
                                                // locked to the parent frame, so swing+roll cannot carry the leaf's
                                                // own rotation (the 2026-11-07 head/digits freeze found by pose-parity).
            internal ArmIkRole ArmIkRole;      // 0.3.9 臂鏈 IK 面（None＝純 FK 面；Upper＝兩骨 IK 生產面、Fore＝消費面）
            internal int ArmIkSide = -1;       // 臂鏈 IK 側（0＝左、1＝右；ArmIkRole == None 時無效）
            internal bool HasFistCurl;         // 空手握拳局部旋轉是否已標定（解除 VisualPass HandPoser 扣扳機／托護木手勢）
            internal Snx.Quaternion FistCurlLocal = Snx.Quaternion.Identity;
            internal float DigitSnapWeight;    // 持槍/持物指尖吸附收攏權重
            internal float DigitHangWeight;    // 空手垂放手指漸進微彎權重
            internal bool HasRelaxedWrist;     // 空手手腕自然微屈局部旋轉
            internal Snx.Quaternion RelaxedWristLocal = Snx.Quaternion.Identity;
        }

        private readonly List<DriveEntry> _entries = new List<DriveEntry>();
        // parent-first evaluation plan over ALL pmx bones: driven entries and rotate-append
        // (付与) bones interleaved in pmx index order, so an append bone always reads its
        // append parent's FINAL local rotation of this frame and a driven bone always
        // reads its parent's final world rotation.
        private DriveEntry[] _entryOf;
        private int[] _grantParent;
        private float[] _grantWeight;
        private SpringPhysicsRig _springPhysics;
        private int[] _plan;
        private Transform _rootJoint;         // game Root_Joint (the rig root's parent)
        private Transform _pelvis;            // game pelvis anchor: translation drive source
        private Vector3 _rootLocalAttach;     // rig root localPosition at attach (Root_Joint space)
        private Quaternion _rootLocalRotAttach;
        private Vector3 _pelvisLocalAttach;   // game pelvis position in Root_Joint space at attach

        // F3 ground telemetry (telemetry only, no behavior change): the rig's own toe-node
        // world Y vs the game skeleton's toe-anchor world Y, 3 samples per attach (the
        // at-attach one + two steady-state ones 15 s, 30 s in) - the at-attach delta
        // answers the user's 腳綁定 question with numbers (stable ±2 cm = the placement
        // is right and post-motion drift is animation-space; large = the offset to
        // correct); the later samples separate a spawn-frame transient from a real
        // floating/sinking offset. Armed only for the own-player raid rig (BodyReplacer
        // sets the flag); a side with no resolved toe anchor stays silent.
        internal bool GroundTelemetry;
        private Transform _charToeL, _charToeR;
        private Transform _gameToeL, _gameToeR;
        private float _groundNextTry;
        private float _groundStopAt;
        private int _groundSamplesLeft;   // F3: 3 samples per attach (spawn transient + 2 steady-state)

        // pose-parity telemetry (telemetry only): per attach, 3 lines sampling the drive's
        // fidelity under real animation - where the live two-vector target deviates from
        // the baked copy (the old visual behavior) and how well the rig's driven pairing
        // axes track the live game axes. The RAID postures matching the pre-fix bug/
        // screenshots despite harness sim green made this the discriminating instrument.
        private Dictionary<int, string> _parityKeyOf;
        private int _parityLinesLeft;
        private float _parityNext;
        private int _driveTotal, _driveFallback;

        // Kinematics muscle retargeter context
        private float _lastDriveErrorTime;

        // 0.3.9 臂鏈 IK 狀態（attach 一次性捕捉、每幀純旋轉求解）：
        // 每側 ArmRestChain（M1 校準：TwoBoneIkChain＋具名掌錨＋chest 節點＋收緊半徑，ArmRest.cs）；
        // _armIkForeTarget 為幀內上臂面喂前臂面的 ForeWorld（pmx 順序上臂(32)＜前臂(37)保證先解後用）；
        // _ikTargetDir/_ikTargetReach 為每幀目標取樣，由 EmitIkTargetLine 節流彙整為 [DollFrame] ik target: 行。
        internal bool Alive = true;                 // 存活狀態閘：死亡／布娃娃時回退純 FK 鏡射
        internal bool InFirstPerson = false;        // 第一人稱視角閘
        internal bool ArmIkActive = false;          // 持械狀態閘：BodyReplacer 逐幀寫 alive∧armed（選單＝false）
        internal bool HoldingNonWeaponItem = false; // 手持非槍械道具（近戰/手榴彈/醫療包等），右手持物抓取 IK
        internal bool UnarmedHangFallback = false;  // 戰局／藏身處空手防 VisualPass 架槍卡死後備（左／雙側）
        private readonly ArmRestChain?[] _armRestSide = new ArmRestChain?[2];
        private readonly Snx.Quaternion?[] _armIkForeTarget = new Snx.Quaternion?[2];
        private readonly bool[] _armHangSide = new bool[2];
        private readonly Snx.Vector3[] _armHangTarget = new Snx.Vector3[2];
        private readonly Snx.Vector3[] _armHangPole = new Snx.Vector3[2];
        private readonly float[] _armHangSwing = new float[2];
        private Transform _gameThighL, _gameKneeL, _gameThighR, _gameKneeR;
        private readonly Snx.Vector3?[] _ikTargetDir = new Snx.Vector3?[2];
        private readonly float[] _ikTargetReach = new float[2];
        private float _ikTargetNext;        // [DollFrame] ik target: 行節流閘（≥0.25s，unscaled）
        private Transform _chestNode;       // 胸腔中心（上半身2 rig 節點；SDF 圓球心）

        /// <summary>Stats of the last Build (the contract line's fallback source, GameBones.ContractLine).</summary>
        internal static (int bones, int mapped, int skipped, int slots) RigStats;

        #region 建構 Build — .gfc rig 一次性建立、bindpose 與遊戲錨點解析（pivots/scales 固定於此）
        /// <summary>
        /// Builds the rig instance under <paramref name="attachParent"/> (game Root_Joint /
        /// preview skeleton root) and returns the driver. Emits the SPEC log contract #2:
        /// [DollFrame] rig: bones=&lt;n&gt; mapped=&lt;m&gt;.
        /// </summary>
        internal static CharacterRig Build(Transform attachParent, Dictionary<string, Transform> games,
            PmxAssets assets, float unitScale, float yaw, float yOffset, int layer,
            IReadOnlyDictionary<string, float> spanFactors = null)
        {
            var pmx = assets.Model;
            var bones = pmx.Bones;
            var digits = DigitAnchors(games);

            // placement root: the pre-pivot proxy-rig pivot recipe + the old reskin placement terms
            var rootGo = new GameObject("DollFrame_rig_" + pmx.Name);
            var root = rootGo.transform;
            root.SetParent(attachParent, false);
            root.localPosition = new Vector3(0f, yOffset, 0f);
            root.localRotation = Quaternion.Euler(0f, yaw, 0f);
            root.localScale = Vector3.one * unitScale;

            var rig = new CharacterRig { Root = root };
            rig.Bones = new Transform[bones.Length];
            var byIndexKey = new Dictionary<string, Transform>(bones.Length, StringComparer.Ordinal);
            for (int i = 0; i < bones.Length; i++)
            {
                var b = bones[i];
                var t = new GameObject(b.Name).transform;
                bool hasParent = b.Parent >= 0 && b.Parent < i;
                t.SetParent(hasParent ? rig.Bones[b.Parent] : root, false);
                // PMX bone positions are absolute model-space coordinates, not parent-relative
                t.localPosition = hasParent ? b.Pos - bones[b.Parent].Pos : b.Pos;
                t.localRotation = Quaternion.identity;
                rig.Bones[i] = t;
                if (!string.IsNullOrEmpty(b.Name) && !byIndexKey.ContainsKey(b.Name)) byIndexKey[b.Name] = t;
            }
            rootGo.layer = layer;

            // 全模型單一等比縮放（Build 一次性，骨架 pristine 前提下的唯一 localScale 寫入點）：
            // rig 根乘入腿錨定等比因子 k——全身（含手指）逐位保持作者原始比例，腿跨距貼齊遊戲
            // 腿鏈；所有驅動捕捉量（bindposes/pairing 方向/接地位）皆在此之後取值。
            ModelScaleMath.ScalePlan? scalePlan = null;
            if (spanFactors != null)
            {
                float heightM = pmx != null ? (pmx.BoundsMax.y - pmx.BoundsMin.y) * unitScale : 0f;
                scalePlan = ModelScaleMath.Solve(spanFactors, heightM);
                if (scalePlan.HasValue)
                {
                    var scaled = ModelScaler.Apply(root, scalePlan.Value);
                    Plugin.Log.LogInfo(ModelScaler.ScaleLine(pmx.Name, scalePlan.Value, scaled));
                }
            }

            // driver entries: mapped pmx bones with a resolved anchor and a valid pairing
            var keyOf = new string[bones.Length];
            var anchorOf = new Transform[bones.Length];
            for (int i = 0; i < bones.Length; i++)
            {
                var name = bones[i].Name;
                if (string.IsNullOrEmpty(name)) continue;
                var row = Array.Find(BoneContract.BoneMap, r => r.pmxBone == name);
                if (string.IsNullOrEmpty(row.gameKey)) continue;
                ResolveAnchor(row.gameKey, games, digits, out var t);
                keyOf[i] = row.gameKey;
                anchorOf[i] = t;
            }
            bool MappedOrResolved(int i) => i >= 0 && i < bones.Length && anchorOf[i] != null;
            var firstDesc = RigPairing.FirstMappedDescendant(Array.ConvertAll(bones, b => b.Parent), MappedOrResolved);

            int skipped = 0;
            for (int i = 0; i < bones.Length; i++)
            {
                if (anchorOf[i] == null) continue;
                var b = bones[i];

                // pure delta-copy bones (BoneContract.PureDeltaPmxBones + spine1/spine3):
                // no pairing, no aim. R_off = G0⁻¹∘C0 preserves the authored spine/pelvis
                // silhouette at rest (avoiding the waist S-curve caused by aligning PMX's
                // -7.6°/+15.4° spine segments onto EFT's Spine1->Spine3->Neck vectors).
                bool isSpineDelta = string.Equals(b.Name, "上半身", StringComparison.Ordinal)
                    || string.Equals(b.Name, "上半身2", StringComparison.Ordinal);
                if (!string.IsNullOrEmpty(b.Name) && (BoneContract.PureDeltaPmxBones.Contains(b.Name) || isSpineDelta))
                {
                    rig._entries.Add(new DriveEntry
                    {
                        PmxBone = i,
                        RigBone = rig.Bones[i],
                        ParentIndex = b.Parent,
                        GameBone = anchorOf[i],
                        Leaf = true,
                        PureDelta = true,
                        WorldRotAttach = rig.Bones[i].rotation,
                        RestOffset = QuatU(Snx.Quaternion.Normalize(
                            Snx.Quaternion.Inverse(QuatS(anchorOf[i].rotation)) * QuatS(rig.Bones[i].rotation))),
                        PmzMate = -1,
                        GameHops = 1,
                    });
                    continue;
                }

                // parent-frame game-delta bones (BoneContract.PureRelPmxBones, the foot/toe
                // convention exemption): keep the authored rotation relative to the rig parent
                // and compose the game bone's rotation delta relative to its own parent. The
                // target composes as AuthoredRel ∘ (GameRel0⁻¹ ∘ GRel(t)) so the anatomical
                // delta applied is the same motion conjugated into the authored attach frame.
                if (!string.IsNullOrEmpty(b.Name) && BoneContract.PureRelPmxBones.Contains(b.Name))
                {
                    var gp = anchorOf[i].parent;
                    if (gp == null) continue;   // no game parent -> no frame to measure the delta in; retry is pointless, drop silently
                    var parentC0 = (b.Parent >= 0 && b.Parent < i ? rig.Bones[b.Parent] : null);
                    var parentC0Rot = parentC0 != null ? parentC0.rotation : root.rotation;
                    rig._entries.Add(new DriveEntry
                    {
                        PmxBone = i,
                        RigBone = rig.Bones[i],
                        ParentIndex = b.Parent,
                        GameBone = anchorOf[i],
                        Leaf = true,
                        PureRel = true,
                        GameParent = gp,
                        AuthoredRel = Quaternion.Inverse(parentC0Rot) * rig.Bones[i].rotation,
                        GameRel0 = Quaternion.Inverse(gp.rotation) * anchorOf[i].rotation,
                        PmzMate = -1,
                        GameHops = 1,
                    });
                    continue;
                }

                var d = firstDesc[i];
                Transform mate;
                bool leaf;
                Vector3 dC;
                if (d >= 0 && MappedOrResolved(d))
                {
                    if (keyOf[d] == keyOf[i]) { skipped++; continue; }   // self-anchored pairing: no direction
                    mate = anchorOf[d];
                    leaf = false;
                    dC = rig.Bones[d].position - rig.Bones[i].position;
                }
                else
                {
                    int p = RigPairing.NearestMappedAncestor(Array.ConvertAll(bones, x => x.Parent), i, MappedOrResolved);
                    if (p < 0 || anchorOf[p] == null) { skipped++; continue; }
                    if (b.Parent < 0 || b.Parent >= i) { skipped++; continue; }
                    mate = anchorOf[p];
                    leaf = true;
                    dC = rig.Bones[i].position - rig.Bones[b.Parent].position;
                }
                if (dC.sqrMagnitude < 1e-10f) { skipped++; continue; }
                var dG0 = leaf ? anchorOf[i].position - mate.position : mate.position - anchorOf[i].position;
                int gameHops = 1;
                if (!leaf)
                {
                    // steps from the mate anchor up to this bone's anchor: a direct child
                    // is 1 (baked), thigh1->calf crosses Thigh2 (2, live), spine1->spine3
                    // crosses Spine2 (2), forearm1->palm crosses Forearm2/3 (3).
                    // Only count hops when mate is an actual Transform descendant of anchorOf[i].
                    int hops = 0;
                    var t = mate;
                    while (t != null && t != anchorOf[i]) { hops++; t = t.parent; }
                    gameHops = (t == anchorOf[i] && hops > 0) ? hops : 1;
                }
                // M4 手腕 delta 映射：左手首/右手首走普通 baked/live delta 路徑（G_n∘G0⁻¹∘C0，
                // 十指同式）——0.3.9 重寫移除腕 cross-axis（dAcrossC/dAcrossG0）特殊通道，
                // 腕端 roll 回歸與十指同屬單一 delta 映射面。
                if (!RigAim.TryBindLiveFrame(QuatS(anchorOf[i].rotation), QuatS(rig.Bones[i].rotation),
                        VecS(dC.normalized), VecS(dG0), out var rOff, out var dCp, out var vSec)) { skipped++; continue; }
                var e = new DriveEntry
                {
                    PmxBone = i,
                    RigBone = rig.Bones[i],
                    ParentIndex = b.Parent,
                    GameBone = anchorOf[i],
                    GameMate = mate,
                    Leaf = leaf,
                    DirC = dC.normalized,
                    DirCPerp = VecU(dCp),
                    GameSecLocal = VecU(vSec),
                    WorldRotAttach = rig.Bones[i].rotation, // captured at attach (identity locals: this is the placement chain)
                    RestOffset = QuatU(rOff),
                    PmzMate = leaf ? b.Parent : d,
                    GameHops = gameHops,
                };
                rig._entries.Add(e);
            }

            // evaluation plan: driven entries + rotate-append bones, pmx index order
            rig._entryOf = new DriveEntry[bones.Length];
            foreach (var en in rig._entries) rig._entryOf[en.PmxBone] = en;
            rig._grantParent = new int[bones.Length];
            rig._grantWeight = new float[bones.Length];
            var plan = new List<int>();
            int grants = 0;
            for (int i = 0; i < bones.Length; i++)
            {
                var b = bones[i];
                bool grant = b.GrantRotate && b.GrantParent >= 0 && b.GrantParent < i && Mathf.Abs(b.GrantWeight) > 1e-6f;
                rig._grantParent[i] = grant ? b.GrantParent : -1;
                rig._grantWeight[i] = grant ? b.GrantWeight : 0f;
                if (grant) grants++;
                if (grant || rig._entryOf[i] != null) plan.Add(i);
            }
            rig._plan = plan.ToArray();
            RigStats = (bones.Length, rig._entries.Count, skipped, assets.SkinBones.Length);

            // skinned mesh: the decree bind - every slot bone binds its OWN rig node, so
            // bindpose = rigNode.worldToLocal * rigRoot.l2w and world(v, t0) = rigRoot.l2w * v
            var mesh = UnityEngine.Object.Instantiate(assets.Mesh);
            mesh.name = "DollFrame_rigmesh_" + pmx.Name;
            var slots = assets.SkinBones;
            var slotBones = new Transform[slots.Length];
            var binds = new Matrix4x4[slots.Length];
            for (int i = 0; i < slots.Length; i++)
            {
                slotBones[i] = rig.Bones[slots[i]];
                binds[i] = slotBones[i].worldToLocalMatrix * root.localToWorldMatrix;
            }
            mesh.bindposes = binds;
            rig.Mesh = mesh;

            var smr = rootGo.AddComponent<SkinnedMeshRenderer>();
            smr.sharedMesh = mesh;
            smr.bones = slotBones;
            // bounds follow the driven pelvis, sized so any body orientation stays inside
            smr.rootBone = byIndexKey.TryGetValue("下半身", out var pelvisBone) ? pelvisBone
                : rig.Bones.Length > 1 ? rig.Bones[1] : root;
            float h = Mathf.Max(1f, pmx.BoundsMax.y - pmx.BoundsMin.y) * unitScale;
            smr.localBounds = new Bounds(Vector3.zero, new Vector3(2f * h, 4f * h, 4f * h));
            smr.sharedMaterials = assets.Materials;
            smr.shadowCastingMode = ShadowCastingMode.On;
            smr.receiveShadows = true;
            smr.updateWhenOffscreen = true;
            smr.quality = SkinQuality.Bone4;
            rig.Skin = smr;

            if (assets.HeadMesh != null && assets.HeadSkinBones != null && assets.HeadSkinBones.Length > 0)
            {
                var headMesh = UnityEngine.Object.Instantiate(assets.HeadMesh);
                headMesh.name = "DollFrame_righeadmesh_" + pmx.Name;
                var headSlots = assets.HeadSkinBones;
                var headSlotBones = new Transform[headSlots.Length];
                var headBinds = new Matrix4x4[headSlots.Length];
                for (int i = 0; i < headSlots.Length; i++)
                {
                    headSlotBones[i] = rig.Bones[headSlots[i]];
                    headBinds[i] = headSlotBones[i].worldToLocalMatrix * root.localToWorldMatrix;
                }
                headMesh.bindposes = headBinds;
                rig.HeadMesh = headMesh;

                var headGo = new GameObject("Head_SMR");
                headGo.transform.SetParent(root, false);
                headGo.layer = layer;
                var headSmr = headGo.AddComponent<SkinnedMeshRenderer>();
                headSmr.sharedMesh = headMesh;
                headSmr.bones = headSlotBones;
                headSmr.rootBone = smr.rootBone;
                headSmr.localBounds = smr.localBounds;
                headSmr.sharedMaterials = assets.Materials;
                headSmr.shadowCastingMode = ShadowCastingMode.On;
                headSmr.receiveShadows = true;
                headSmr.updateWhenOffscreen = true;
                headSmr.quality = SkinQuality.Bone4;
                rig.HeadSkin = headSmr;
            }

            // SDF 臉部陰影：包內帶 sdf_face.png 時建立 per-rig 驅動（臉材質實例＋貼圖副本）；
            // 缺包資料即沿用原渲染
            if (assets.SdfFace != null && assets.SdfFace.HasData && assets.FaceSlot >= 0)
            {
                byIndexKey.TryGetValue("頭", out var sdfHead);
                rig._sdfHead = sdfHead;
                rig.SdfFace = SdfFaceDriver.Create(sdfHead, assets.SdfFace,
                    assets.Materials[assets.FaceSlot], smr, rig.HeadSkin, assets.FaceSlot);
            }

            // attach state for the per-frame root drive
            rig._rootJoint = attachParent;
            byIndexKey.TryGetValue("頭", out rig._headRig);
            games.TryGetValue("head", out rig._headGame);
            games.TryGetValue("pelvis", out var pelvis);
            rig._pelvis = pelvis;
            rig._rootLocalAttach = root.localPosition;
            rig._rootLocalRotAttach = root.localRotation;
            rig._pelvisLocalAttach = pelvis != null ? attachParent.InverseTransformPoint(pelvis.position) : Vector3.zero;

            // F3 ground-check anchors: the rig's own toe nodes (pmx toe bones) and the
            // game skeleton's toe anchors; either side may be absent and the check then
            // stays silent. All four names are contract rows already (BoneMap toe/foot
            // spans), so no new JP literal is introduced.
            rig._charToeL = byIndexKey.TryGetValue("左足首先", out var charToeL) ? charToeL : null;
            rig._charToeR = byIndexKey.TryGetValue("右足首先", out var charToeR) ? charToeR : null;
            games.TryGetValue("toeL", out rig._gameToeL);
            games.TryGetValue("toeR", out rig._gameToeR);
            games.TryGetValue("thigh1L", out rig._gameThighL);
            games.TryGetValue("calfL", out rig._gameKneeL);
            games.TryGetValue("thigh1R", out rig._gameThighR);
            games.TryGetValue("calfR", out rig._gameKneeR);

            // 0.3.9 臂鏈 IK capture（attach 一次性）：兩側左/右腕・左/右肘・左/右手首全解析時，
            // 由 ArmRestMath.TryCalibrate 捕捉 ArmRestChain（具名掌錨＋chest-local pole＋收緊半徑）
            // 並掛 ArmIk 面；並輸出 ik: 行族（單一 attach 行，單一生產者）＋節流閘初始化
            SetupArmRest(rig, bones, anchorOf, true);
            rig._ikTargetNext = Time.unscaledTime;

            // pose-parity sample table: every driven entry with its contract key, 3 lines
            // per attach (10 s apart, past the spawn transient)
            var parity = new List<(int entry, string key)>();
            for (int k = 0; k < rig._entries.Count; k++)
            {
                if (rig._entries[k].PureDelta || rig._entries[k].PureRel) continue;   // no aim -> no pairing-axis fidelity to sample
                // 0.3.9 臂鏈 IK 面骨（左肩/右肩、左腕/右腕、左ひじ/右ひじ）不入 parity 採樣——其目標非 FK 配對方向真值
                var role = rig._entries[k].ArmIkRole;
                if (role == ArmIkRole.Upper || role == ArmIkRole.Fore || role == ArmIkRole.Clavicle) continue;
                var row = Array.Find(BoneContract.BoneMap, r => r.pmxBone == bones[rig._entries[k].PmxBone].Name);
                if (!string.IsNullOrEmpty(row.gameKey)) parity.Add((k, row.gameKey));
            }
            rig._parityKeyOf = new Dictionary<int, string>(parity.Count);
            foreach (var (entry, key) in parity) rig._parityKeyOf[rig._entries[entry].PmxBone] = key;
            rig._parityLinesLeft = 3;
            rig._parityNext = Time.unscaledTime + 10f;

            // Pose the rig once before measuring ground contact: rest-pose bindposes and
            // IK local directions are already captured above, so evaluating the initial
            // pose aligns the legs with the game skeleton's standing stance rather than
            // calibrating toe height from the splayed PMX A-pose.
            rig.EvaluateBonePlan(false, null);
            var alreadyDriven = new bool[bones.Length];
            for (int i = 0; i < bones.Length; i++)
                alreadyDriven[i] = rig._entryOf[i] != null || rig._grantParent[i] >= 0;
            rig._springPhysics = SpringPhysicsRig.Build(root, rig.Bones, bones, alreadyDriven, pmx);
            {
                float charY = 0f;
                int charN = 0;
                if (rig._charToeL != null) { charY += attachParent.InverseTransformPoint(rig._charToeL.position).y; charN++; }
                if (rig._charToeR != null) { charY += attachParent.InverseTransformPoint(rig._charToeR.position).y; charN++; }
                float gameY = 0f;
                int gameN = 0;
                if (rig._gameToeL != null) { gameY += attachParent.InverseTransformPoint(rig._gameToeL.position).y; gameN++; }
                if (rig._gameToeR != null) { gameY += attachParent.InverseTransformPoint(rig._gameToeR.position).y; gameN++; }
                if (charN > 0 && gameN > 0)
                {
                    var attachPos = root.localPosition;
                    attachPos.y += gameY / gameN - charY / charN;
                    root.localPosition = attachPos;
                    rig._rootLocalAttach = attachPos;
                }
            }
            rig._groundStopAt = Time.unscaledTime + 90f;
            rig._groundNextTry = Time.unscaledTime;
            rig._groundSamplesLeft = 3;

            Plugin.Log.LogInfo($"[DollFrame] rig: bones={bones.Length} mapped={rig._entries.Count} grants={grants}");
            return rig;
        }

        /// <summary>
        /// 臂鏈 rest 校準 Build 期捕捉（attach 一次性；0.3.9 重寫＝SetupArmIk→SetupArmRest）：
        /// 一側左/右腕・左/右肘・左/右手首三骨映射且遊戲錨全解析、四面骨 entry 均成立時，把
        /// 上臂（ArmIkRole.Upper）/前臂（ArmIkRole.Fore）面骨掛入 IK 面並由
        /// ArmRestMath.TryCalibrate 捕捉 ArmRestChain（M1 校準＋M2 具名掌錨＋chest-local pole
        /// ＋收緊半徑）。零行為面外的骨骼不受影響；cfg off（ArmIkRetarget）→ 面不掛、
        /// 行以 cfg=off 輸出。
        /// </summary>
        private static void SetupArmRest(CharacterRig rig, PmxParser.PmxBone[] bones, Transform[] anchorOf, bool cfgOn)
        {
            if (!cfgOn)
            {
                Plugin.Log.LogInfo("[DollFrame] ik: cfg=off");
                return;
            }

            // 胸腔中心＝上半身2 rig 節點（SDF 圓球心＋chest-local pole 的宿主）
            int chestIdx = IndexOfName(bones, "上半身2");
            rig._chestNode = chestIdx >= 0 ? rig.Bones[chestIdx] : null;

            var states = new string[2];
            for (int side = 0; side < 2; side++)
            {
                states[side] = "ok";
                bool isLeft = side == 0;
                int clavIdx = IndexOfName(bones, isLeft ? "左肩" : "右肩");
                int upperIdx = IndexOfName(bones, isLeft ? "左腕" : "右腕");
                int foreIdx = IndexOfName(bones, isLeft ? "左ひじ" : "右ひじ");
                int wristIdx = IndexOfName(bones, isLeft ? "左手首" : "右手首");
                int midIdx = IndexOfName(bones, isLeft ? "左中指１" : "右中指１");
                if (midIdx < 0) midIdx = IndexOfName(bones, isLeft ? "左人指１" : "右人指１");
                int indexIdx = IndexOfName(bones, isLeft ? "左人指１" : "右人指１");
                int pinkyIdx = IndexOfName(bones, isLeft ? "左小指１" : "右小指１");
                var ce = clavIdx >= 0 ? rig._entryOf[clavIdx] : null;
                var ue = upperIdx >= 0 ? rig._entryOf[upperIdx] : null;
                var fe = foreIdx >= 0 ? rig._entryOf[foreIdx] : null;
                var we = wristIdx >= 0 ? rig._entryOf[wristIdx] : null;
                if (upperIdx < 0 || foreIdx < 0 || wristIdx < 0) states[side] = "skip:model";
                else if (anchorOf[upperIdx] == null || anchorOf[foreIdx] == null || anchorOf[wristIdx] == null) states[side] = "skip:anchors";
                else if (ue == null || fe == null) states[side] = "skip:pairing";
                else if (!ArmRestMath.TryCalibrate(rig.Root, rig.Bones, upperIdx, foreIdx, wristIdx, midIdx,
                            anchorOf[wristIdx], VecS(ue.DirC), VecS(fe.DirC),
                            QuatS(ue.WorldRotAttach), QuatS(fe.WorldRotAttach),
                            rig._chestNode, isLeft, out var rest))
                    states[side] = "skip:degenerate";
                else
                {
                    rig._armRestSide[side] = rest;
                    if (ce != null)
                    {
                        ce.ArmIkRole = ArmIkRole.Clavicle;
                        ce.ArmIkSide = side;
                    }
                    ue.ArmIkRole = ArmIkRole.Upper;
                    ue.ArmIkSide = side;
                    fe.ArmIkRole = ArmIkRole.Fore;
                    fe.ArmIkSide = side;
                    if (we != null)
                    {
                        we.ArmIkRole = ArmIkRole.Wrist;
                        we.ArmIkSide = side;
                    }
                    var palmInWorld = VecS(-rig.Root.up);
                    var acrossWorld = VecS(isLeft ? rig.Root.forward : -rig.Root.forward);
                    if (midIdx >= 0 && indexIdx >= 0 && pinkyIdx >= 0
                        && rig.Bones[midIdx] != null && rig.Bones[indexIdx] != null && rig.Bones[pinkyIdx] != null)
                    {
                        var handFwd = VecS(rig.Bones[midIdx].position) - VecS(rig.Bones[wristIdx].position);
                        var i2p = VecS(rig.Bones[pinkyIdx].position) - VecS(rig.Bones[indexIdx].position);
                        if (handFwd.LengthSquared() > 1e-8f && i2p.LengthSquared() > 1e-8f)
                        {
                            handFwd = Snx.Vector3.Normalize(handFwd);
                            acrossWorld = Snx.Vector3.Normalize(i2p);
                            var c = isLeft ? Snx.Vector3.Cross(handFwd, acrossWorld) : Snx.Vector3.Cross(acrossWorld, handFwd);
                            if (c.LengthSquared() > 1e-8f)
                                palmInWorld = Snx.Vector3.Normalize(c);
                        }
                    }
                    if (we != null && rig.Bones[wristIdx] != null)
                    {
                        var wristCurlWorld = Snx.Vector3.Cross(VecS(we.DirC), palmInWorld);
                        if (wristCurlWorld.LengthSquared() > 1e-8f)
                        {
                            wristCurlWorld = Snx.Vector3.Normalize(wristCurlWorld);
                            var wristCurlLocal = Snx.Vector3.Transform(wristCurlWorld, Snx.Quaternion.Inverse(QuatS(rig.Bones[wristIdx].rotation)));
                            if (wristCurlLocal.LengthSquared() > 1e-8f)
                            {
                                we.RelaxedWristLocal = Snx.Quaternion.Normalize(
                                    Snx.Quaternion.CreateFromAxisAngle(Snx.Vector3.Normalize(wristCurlLocal), 7f * (MathF.PI / 180f)));
                                we.HasRelaxedWrist = true;
                            }
                        }
                    }
                    string suffix = isLeft ? "L" : "R";
                    foreach (var (pmxBone, gameKey) in BoneContract.BoneMap)
                    {
                        if (!gameKey.StartsWith("digit", StringComparison.Ordinal) || !gameKey.EndsWith(suffix, StringComparison.Ordinal))
                            continue;
                        int dIdx = IndexOfName(bones, pmxBone);
                        var de = dIdx >= 0 ? rig._entryOf[dIdx] : null;
                        if (de != null)
                        {
                            de.ArmIkRole = ArmIkRole.Digit;
                            de.ArmIkSide = side;
                            ComputeFistCurl(rig.Bones[dIdx], de, gameKey, palmInWorld, acrossWorld);
                        }
                    }
                }
            }

            // 行面（單一 attach 行）：ok 側帶 palm/ratio/len；skip 側僅狀態（skip:<reason>）
            var line = $"[DollFrame] ik: cfg=on L={states[0]}";
            if (rig._armRestSide[0] is ArmRestChain lRest)
                line += $" palm={lRest.PalmSource} ratio={lRest.Chain.ArmRatio:0.##} len={lRest.Chain.L1 + lRest.Chain.L2:0.##}m";
            line += $" R={states[1]}";
            if (rig._armRestSide[1] is ArmRestChain rRest)
                line += $" palm={rRest.PalmSource} ratio={rRest.Chain.ArmRatio:0.##} len={rRest.Chain.L1 + rRest.Chain.L2:0.##}m";
            Plugin.Log.LogInfo(line);
        }

        private static void ComputeFistCurl(
            Transform rigBone, DriveEntry de, string gameKey,
            Snx.Vector3 palmInWorld, Snx.Vector3 acrossWorld)
        {
            if (rigBone == null || de == null) return;
            bool isThumb = gameKey.StartsWith("digit1_", StringComparison.Ordinal);
            float deg;
            if (gameKey.Contains("_1", StringComparison.Ordinal))
                deg = isThumb ? 12f : 26f;
            else if (gameKey.Contains("_2", StringComparison.Ordinal))
                deg = isThumb ? 18f : 34f;
            else
                deg = isThumb ? 18f : 22f;

            var targetDir = isThumb
                ? Snx.Vector3.Normalize(acrossWorld * 0.75f + palmInWorld * 0.65f)
                : palmInWorld;
            var fDir = VecS(de.DirC);
            var curlAxisWorld = Snx.Vector3.Cross(fDir, targetDir);
            if (curlAxisWorld.LengthSquared() <= 1e-8f) return;

            curlAxisWorld = Snx.Vector3.Normalize(curlAxisWorld);
            var curlAxisLocal = Snx.Vector3.Transform(curlAxisWorld, Snx.Quaternion.Inverse(QuatS(rigBone.rotation)));
            if (curlAxisLocal.LengthSquared() <= 1e-8f) return;

            curlAxisLocal = Snx.Vector3.Normalize(curlAxisLocal);
            de.FistCurlLocal = Snx.Quaternion.Normalize(
                Snx.Quaternion.CreateFromAxisAngle(curlAxisLocal, deg * (MathF.PI / 180f)));
            de.HasFistCurl = true;

            if (isThumb)
            {
                de.DigitSnapWeight = 0.30f;
                de.DigitHangWeight = 0.35f;
            }
            else if (gameKey.StartsWith("digit2_", StringComparison.Ordinal))
            {
                de.DigitSnapWeight = 0.25f;
                de.DigitHangWeight = 0.40f;
            }
            else
            {
                de.DigitSnapWeight = gameKey.Contains("_1", StringComparison.Ordinal) ? 0.35f
                    : (gameKey.Contains("_2", StringComparison.Ordinal) ? 0.50f : 0.55f);
                de.DigitHangWeight = gameKey.StartsWith("digit3_", StringComparison.Ordinal) ? 0.55f
                    : (gameKey.StartsWith("digit4_", StringComparison.Ordinal) ? 0.68f : 0.80f);
            }
        }

        private static int IndexOfName(PmxParser.PmxBone[] bones, string name)
        {
            for (int i = 0; i < bones.Length; i++)
                if (string.Equals(bones[i].Name, name, StringComparison.Ordinal)) return i;
            return -1;
        }

        private static void ResolveAnchor(string key, Dictionary<string, Transform> games,
            Dictionary<string, Transform> digits, out Transform t)
        {
            if (Array.IndexOf(BoneContract.Digits, key) >= 0)
                digits.TryGetValue(key, out t);
            else
                games.TryGetValue(key, out t);
        }

        #endregion
        #region 驅動 Drive — 每幀 root 位移跟蹤＋父先順序 localRotation 驅動計畫
        /// <summary>
        /// Per-frame driver (call from LateUpdate, after the game animator posed this
        /// frame). Root drive first (decree: position = attachPos + pelvis delta, world
        /// yaw tracks the game root), then the retarget parent-first over the pmx bone
        /// order. Only local rotations are written - pivots and scales never move, so the
        /// rig is zero-stretch every frame by construction.
        /// </summary>
        internal void Drive()
        {
            if (Root == null || _rootJoint == null) return;

            try
            {
                // root drive in Root_Joint space (the rig root is its child): the placement is
                // re-asserted every frame and only the game pelvis translation (crouch, lean,
                // prone, hip sway) is added - the old world-space delta drifted sideways as
                // soon as the player turned (attach offset not rotated with the player).
                var pos = _rootLocalAttach;
                if (_pelvis != null)
                    pos += _rootJoint.InverseTransformPoint(_pelvis.position) - _pelvisLocalAttach;

                Root.localPosition = pos;
                Root.localRotation = _rootLocalRotAttach;

                // 0.3.9 臂鏈 IK：幀內前臂面喂入通道與目標節流取樣清空（鎖骨→上臂→前臂順序）
                _armIkForeTarget[0] = null;
                _armIkForeTarget[1] = null;
                _ikTargetDir[0] = null;
                _ikTargetDir[1] = null;
                for (int s = 0; s < 2; s++)
                {
                    _armHangSide[s] = false;
                    if (_armRestSide[s] is ArmRestChain r)
                    {
                        if (Bones[r.UpperIdx] != null) Bones[r.UpperIdx].localPosition = r.UpperLocalPos0;
                        if (Bones[r.ForeIdx] != null) Bones[r.ForeIdx].localPosition = r.ForeLocalPos0;
                        if (Bones[r.WristIdx] != null) Bones[r.WristIdx].localPosition = r.WristLocalPos0;
                        if (Alive && !ArmIkActive && !HoldingNonWeaponItem && UnarmedHangFallback)
                            _armHangSide[s] = true;
                    }
                }
                if ((_armHangSide[0] || _armHangSide[1]) && _chestNode != null)
                    ComputeUnarmedHangTargets();

                bool paritySampling = _parityLinesLeft > 0 && GroundTelemetry && Time.unscaledTime >= _parityNext;
                var parityLine = paritySampling ? new System.Text.StringBuilder() : null;

                EvaluateBonePlan(paritySampling, parityLine);
                EmitIkTargetLine();
                _springPhysics?.StepAndApply(Time.deltaTime, Time.frameCount, Plugin.SecondaryPhysics == null || Plugin.SecondaryPhysics.Value, InFirstPerson);

                if (paritySampling)
                {
                    _parityLinesLeft--;
                    _parityNext = Time.unscaledTime + 30f;
                    Plugin.Log.LogInfo($"[DollFrame] pose-parity baked={_driveFallback}/{_driveTotal}{parityLine}");
                }

                GroundCheck();
            }
            catch (Exception ex)
            {
                if (Time.unscaledTime - _lastDriveErrorTime > 3.0f)
                {
                    _lastDriveErrorTime = Time.unscaledTime;
                    Plugin.Log.LogWarning($"[DollFrame] CharacterRig.Drive error: {ex.Message}");
                }
            }
        }

        private void ComputeUnarmedHangTargets()
        {
            Snx.Vector3 leftOutward;
            if (_armRestSide[0] is ArmRestChain lR && _armRestSide[1] is ArmRestChain rR
                && Bones[lR.UpperIdx] != null && Bones[rR.UpperIdx] != null)
            {
                var span = VecS(Bones[lR.UpperIdx].position - Bones[rR.UpperIdx].position);
                leftOutward = span.LengthSquared() > 1e-6f
                    ? Snx.Vector3.Normalize(span)
                    : VecS(-_chestNode.right);
            }
            else
            {
                leftOutward = VecS(-_chestNode.right);
            }
            var worldDown = new Snx.Vector3(0f, -1f, 0f);
            var worldUp = new Snx.Vector3(0f, 1f, 0f);
            var charFwd = Snx.Vector3.Cross(worldUp, leftOutward);
            charFwd = charFwd.LengthSquared() > 1e-6f
                ? Snx.Vector3.Normalize(charFwd)
                : VecS(_chestNode.forward);

            float legSwing = 0f;
            if (_gameThighL != null && _gameKneeL != null && _gameThighR != null && _gameKneeR != null)
            {
                var tL = VecS(_gameKneeL.position - _gameThighL.position);
                var tR = VecS(_gameKneeR.position - _gameThighR.position);
                if (tL.LengthSquared() > 1e-6f && tR.LengthSquared() > 1e-6f)
                {
                    float fwdL = Snx.Vector3.Dot(Snx.Vector3.Normalize(tL), charFwd);
                    float fwdR = Snx.Vector3.Dot(Snx.Vector3.Normalize(tR), charFwd);
                    legSwing = Math.Clamp((fwdR - fwdL) * 0.70f, -0.55f, 0.55f);
                }
            }
            float breath = MathF.Sin(Time.time * 1.6f) * 0.022f;

            for (int s = 0; s < 2; s++)
            {
                if (!_armHangSide[s] || !(_armRestSide[s] is ArmRestChain r) || Bones[r.UpperIdx] == null) continue;
                float swing = (s == 0 ? legSwing : -legSwing) + breath * (s == 0 ? 1f : -1f);
                float liftUp = MathF.Abs(swing) * 0.14f;
                var outward = s == 0 ? leftOutward : -leftOutward;
                var hangDir = Snx.Vector3.Normalize(
                    worldDown * (1f - liftUp) + outward * 0.08f + charFwd * (0.14f + swing));
                float hangReach = (r.Chain.L1 + r.Chain.L2) * (0.86f - MathF.Abs(swing) * 0.04f);
                _armHangTarget[s] = VecS(Bones[r.UpperIdx].position) + hangDir * hangReach;
                _armHangPole[s] = Snx.Vector3.Normalize(-charFwd * 0.85f + outward * 0.45f + worldDown * 0.25f);
                _armHangSwing[s] = swing;
            }
        }

        internal void UpdateSdfFace()
        {
            if (SdfFace != null && !InFirstPerson) SdfFace.Update();
        }

        internal void TerminateSdf()
        {
            if (SdfFace == null) return;
            SdfFace.Terminate();
            SdfFace = null;
        }

        private void EvaluateBonePlan(bool paritySampling, System.Text.StringBuilder parityLine)
        {
            for (int k = 0; k < _plan.Length; k++)
            {
                int i = _plan[k];
                var bone = Bones[i];
                if (bone == null) continue;
                var en = _entryOf[i];
                if (en != null && en.GameBone != null)
                {
                    var parentT = en.ParentIndex >= 0 && en.ParentIndex < i ? Bones[en.ParentIndex] : null;
                    var parentWorld = QuatS(parentT != null ? parentT.rotation : Root.rotation);
                    var gameRot = QuatS(en.GameBone.rotation);
                    Snx.Quaternion target = Snx.Quaternion.Identity;   // ikDriven 中間分支沿用上臂面已填值
                    // 面骨驅動（0.3.9 起混合）：一般骨＝FK 配對複製；臂鏈面骨（Upper/Fore）＝兩骨 IK，退化/死亡回退 FK。
                    bool pureRelWrite = false;
                    bool sampleThisBone = false;
                    Snx.Vector3 dGNowSample = Snx.Vector3.Zero;
                    Snx.Quaternion pureRelLocal = Snx.Quaternion.Identity;
                    // 0.3.9 混合 FK＋臂鏈 IK：上臂/前臂面骨由 TwoBoneIkMath 兩骨 IK 求解（純旋轉，零
                    // localPosition 寫入）；ArmIkActive=false（死亡/布娃娃）或求解退化（Ok=false）→
                    // 回退既有 baked/live FK 複製路徑。IK 面骨刻意不入 parity 採樣與
                    // _driveTotal/_driveFallback 計數——其目標由 IK 幾何給出，非 FK 配對方向真值。
                    bool ikDriven = Alive && en.ArmIkRole != ArmIkRole.None && TryEvaluateArmIkRole(
                        bone, en, parentWorld, ref target, ref pureRelLocal, ref pureRelWrite);

                    if (en.PureRel)
                    {
                        if (en.GameParent != null)
                        {
                            // PureRel writes the local rotation straight in the rig-parent frame
                            // (LocalFromWorld(parentWorld, parentWorld∘rel) == rel): the authored
                            // rotation vs parent stays, and the game bone's rotation delta relative
                            // to its own parent is composed through the attach convention difference.
                            var qrel = Snx.Quaternion.Inverse(QuatS(en.GameParent.rotation)) * gameRot;
                            var delta = Snx.Quaternion.Inverse(QuatS(en.GameRel0)) * qrel;
                            pureRelLocal = Snx.Quaternion.Normalize(QuatS(en.AuthoredRel) * delta);
                            pureRelWrite = true;
                            target = Snx.Quaternion.Identity;   // unused; target-shaped for the shared tail
                        }
                        else
                        {
                            target = RigAim.CopyTarget(gameRot, QuatS(en.RestOffset));
                        }
                    }
                    else if (en.PureDelta)
                    {
                        // pure delta-copy pelvis: the game anchor's rotation delta composed onto
                        // the authored attach pose; no aim decision exists here, so the aim-fidelity
                        // counters deliberately do not see this bone.
                        target = RigAim.CopyTarget(gameRot, QuatS(en.RestOffset));
                    }
                    else if (ikDriven || pureRelWrite)
                    {
                        // 臂鏈 IK / 空手放鬆面：target 或 pureRelLocal 已由 TryEvaluateArmIkRole 填入
                    }
                    else
                    {
                        bool hasMate = en.GameMate != null;
                        var dGNow = hasMate
                            ? (en.Leaf ? VecS(en.GameBone.position - en.GameMate.position)
                                       : VecS(en.GameMate.position - en.GameBone.position))
                            : Snx.Vector3.Zero;
                        var live = Snx.Quaternion.Identity;
                        bool liveOk = hasMate && !en.Leaf && en.GameHops > 1 &&
                            RigAim.TryLiveTarget(VecS(en.DirC), VecS(en.DirCPerp), VecS(en.GameSecLocal),
                                QuatS(en.WorldRotAttach), gameRot, dGNow, out live);
                        target = liveOk ? live : RigAim.CopyTarget(gameRot, QuatS(en.RestOffset));
                        if (en.ArmIkRole != ArmIkRole.None && en.ArmIkSide >= 0 && en.ArmIkSide < _armRestSide.Length
                            && _armRestSide[en.ArmIkSide] is ArmRestChain armRest && _chestNode != null)
                        {
                            // 空手純 FK 鏡像比例防穿模守衛：僅在肘／腕因肩寬／胸厚比例差異穿入胸腔 SDF 球時
                            // 沿球面擠出並保持段長不變；未穿入時 100% 維持純 FK 鏡像。
                            if (en.ArmIkRole == ArmIkRole.Upper || en.ArmIkRole == ArmIkRole.Fore)
                            {
                                var lChild = Snx.Vector3.Transform(VecS(en.DirC), Snx.Quaternion.Inverse(QuatS(en.WorldRotAttach)));
                                var childDir = Snx.Vector3.Transform(lChild, target);
                                float segLen = en.ArmIkRole == ArmIkRole.Upper ? armRest.Chain.L1 : armRest.Chain.L2;
                                if (TwoBoneIkMath.TryGuardChestClip(VecS(bone.position), childDir, segLen,
                                    VecS(_chestNode.position), armRest.ChestRadius, target, out var guarded))
                                    target = guarded;
                            }
                        }
                        _driveTotal++;
                        if (!liveOk) _driveFallback++;
                        sampleThisBone = paritySampling && parityLine != null && _parityKeyOf != null;
                        dGNowSample = dGNow;
                    }

                    if (pureRelWrite)
                        bone.localRotation = QuatU(pureRelLocal);
                    else
                        bone.localRotation = QuatU(RigAim.LocalFromWorld(parentWorld, target));

                    if (Alive && (ArmIkActive || HoldingNonWeaponItem) && en.ArmIkRole == ArmIkRole.Digit && en.HasFistCurl && en.DigitSnapWeight > 0f)
                    {
                        var extraCurl = Snx.Quaternion.Slerp(Snx.Quaternion.Identity, en.FistCurlLocal, en.DigitSnapWeight);
                        bone.localRotation = bone.localRotation * QuatU(extraCurl);
                    }

                    // 抓取精準度收斂（誤差 < 0.05cm）：持槍或持非槍械道具 IK 啟用時套用。
                    if (Alive && (ArmIkActive || HoldingNonWeaponItem) && en.ArmIkRole == ArmIkRole.Wrist)
                    {
                        var sideW = en.ArmIkSide;
                        var restW = sideW >= 0 && sideW < _armRestSide.Length ? _armRestSide[sideW] : null;
                        if (restW != null && restW.Value.PalmBone != null)
                        {
                            var actualGrip = VecS(bone.position) + Snx.Vector3.Transform(restW.Value.RigPalmGripLocal, QuatS(bone.rotation));
                            var desiredGrip = PalmGripPos(restW.Value.PalmBone, restW.Value.PalmMidBone);
                            var err = desiredGrip - actualGrip;
                            float errLen = err.Length();
                            if (errLen > 0.00005f && errLen < 0.35f)
                            {
                                var errU = VecU(err);
                                var uBone = Bones[restW.Value.UpperIdx];
                                var fBone = Bones[restW.Value.ForeIdx];
                                if (uBone != null && fBone != null)
                                {
                                    uBone.position += errU * 0.30f;
                                    fBone.position += errU * 0.40f;
                                    bone.position += errU * 0.30f;
                                }
                                else
                                {
                                    bone.position += errU;
                                }
                            }
                        }
                    }

                    // Sample pose-parity AFTER writing bone.localRotation so child mate positions reflect this frame's pose.
                    if (sampleThisBone && _parityKeyOf.TryGetValue(i, out var pKey))
                    {
                        var rigAxis = en.Leaf ? VecS(en.RigBone.position - Bones[en.PmzMate].position)
                                              : VecS(Bones[en.PmzMate].position - en.RigBone.position);
                        var baked = RigAim.CopyTarget(gameRot, QuatS(en.RestOffset));
                        float aDeg = 0f;
                        if (rigAxis.LengthSquared() > 1e-10f && dGNowSample.LengthSquared() > 1e-10f)
                        {
                            var dot = Snx.Vector3.Dot(Snx.Vector3.Normalize(rigAxis), Snx.Vector3.Normalize(dGNowSample));
                            aDeg = MathF.Acos(Math.Clamp(dot, -1f, 1f)) * (180f / MathF.PI);
                        }
                        var qd = target * Snx.Quaternion.Inverse(baked);
                        if (qd.W < 0f) { qd.X = -qd.X; qd.Y = -qd.Y; qd.Z = -qd.Z; qd.W = -qd.W; }
                        float bDeg = 2f * MathF.Acos(Math.Clamp(qd.W, -1f, 1f)) * (180f / MathF.PI);
                        parityLine.Append($" {pKey} a{aDeg:0.#} b{bDeg:0.#}");
                    }
                }
                int gp = _grantParent[i];
                if (gp >= 0 && Bones[gp] != null)
                {
                    var own = en != null ? QuatS(bone.localRotation) : Snx.Quaternion.Identity;
                    bone.localRotation = QuatU(RigAim.Grant(own, QuatS(Bones[gp].localRotation), _grantWeight[i]));
                }
            }
        }

        private bool TryEvaluateArmIkRole(
            Transform bone, DriveEntry en, Snx.Quaternion parentWorld,
            ref Snx.Quaternion target, ref Snx.Quaternion pureRelLocal, ref bool pureRelWrite)
        {
            var side = en.ArmIkSide;
            var rest = side >= 0 && side < _armRestSide.Length ? _armRestSide[side] : null;
            bool isHang = side >= 0 && side < 2 && _armHangSide[side];
            bool runGrabIk = ArmIkActive || HoldingNonWeaponItem;
            bool runHangIk = !runGrabIk && isHang;
            if ((!runGrabIk && !runHangIk) || rest == null || rest.Value.ChestNode == null || _chestNode == null || en.GameBone == null)
                return false;

            switch (en.ArmIkRole)
            {
                case ArmIkRole.Clavicle:
                    if (runHangIk)
                    {
                        float swingRad = _armHangSwing[side] * 0.18f;
                        pureRelLocal = Snx.Quaternion.CreateFromAxisAngle(new Snx.Vector3(0f, side == 0 ? 1f : -1f, 0f), swingRad);
                        pureRelWrite = true;
                        target = Snx.Quaternion.Identity;
                        return false;
                    }
                    {
                        var palm = rest.Value.PalmBone != null ? rest.Value.PalmBone : en.GameBone;
                        var clavPos = VecS(bone.position);
                        var wristTargetPos = ComputePreciseWristTarget(palm, rest.Value);
                        var lChild = Snx.Vector3.Transform(VecS(en.DirC), Snx.Quaternion.Inverse(QuatS(en.WorldRotAttach)));
                        var clavSpan = en.PmzMate >= 0 && en.PmzMate < Bones.Length && Bones[en.PmzMate] != null
                            ? Snx.Vector3.Distance(VecS(Bones[en.PmzMate].position), clavPos)
                            : 0.10f;
                        var chain = rest.Value.Chain;
                        target = DollFrame.Character.Kinematics.ArmGrabIkMath.SolveClavicle(
                            in chain, clavPos, parentWorld, lChild, clavSpan, wristTargetPos);
                        return true;
                    }

                case ArmIkRole.Upper:
                    {
                        var chain = rest.Value.Chain;
                        if (runHangIk)
                        {
                            var res = DollFrame.Character.Kinematics.ArmGrabIkMath.SolveRelaxedHang(
                                in chain, VecS(bone.position), _armHangTarget[side], _armHangPole[side],
                                VecS(_chestNode.position), rest.Value.ChestRadius, parentWorld);
                            if (!res.Ok) return false;
                            target = res.UpperWorld;
                            _armIkForeTarget[side] = res.ForeWorld;
                            return true;
                        }
                        var palm = rest.Value.PalmBone != null ? rest.Value.PalmBone : en.GameBone;
                        var wristTargetPos = ComputePreciseWristTarget(palm, rest.Value);
                        var grabRes = DollFrame.Character.Kinematics.ArmGrabIkMath.SolveTwoBone(
                            in chain, VecS(bone.position), wristTargetPos, QuatS(palm.rotation),
                            VecS(_chestNode.position), QuatS(_chestNode.rotation),
                            rest.Value.ChestRadius * 0.70f, parentWorld);
                        if (!grabRes.Ok) return false;
                        target = grabRes.UpperWorld;
                        _armIkForeTarget[side] = grabRes.ForeWorld;
                        var dRaw = wristTargetPos - VecS(bone.position);
                        if (dRaw.LengthSquared() >= TwoBoneIkMath.MinDirEps * TwoBoneIkMath.MinDirEps)
                        {
                            _ikTargetDir[side] = Snx.Vector3.Normalize(dRaw);
                            _ikTargetReach[side] = Snx.Vector3.Distance(VecS(bone.position), wristTargetPos);
                        }
                        return true;
                    }

                case ArmIkRole.Fore:
                    if (side >= 0 && side < _armIkForeTarget.Length && _armIkForeTarget[side].HasValue)
                    {
                        target = _armIkForeTarget[side].Value;
                        return true;
                    }
                    return false;

                case ArmIkRole.Wrist:
                    if (runHangIk)
                    {
                        pureRelLocal = en.HasRelaxedWrist ? en.RelaxedWristLocal : Snx.Quaternion.Identity;
                        pureRelWrite = true;
                        target = Snx.Quaternion.Identity;
                        return false;
                    }
                    {
                        var palm = rest.Value.PalmBone != null ? rest.Value.PalmBone : en.GameBone;
                        target = RigAim.CopyTarget(QuatS(palm.rotation), QuatS(en.RestOffset));
                        return true;
                    }

                case ArmIkRole.Digit:
                    if (runHangIk && en.HasFistCurl)
                    {
                        float w = en.DigitHangWeight > 0f ? en.DigitHangWeight : 0.55f;
                        pureRelLocal = Snx.Quaternion.Slerp(Snx.Quaternion.Identity, en.FistCurlLocal, w);
                        pureRelWrite = true;
                        target = Snx.Quaternion.Identity;
                    }
                    return false;

                default:
                    return false;
            }
        }

        /// <summary>
        /// 0.3.9 臂鏈 IK 目標節流行（單一生產者）：本幀至少一側上臂面 IK 依驅動（ArmIkActive 且
        /// 求解 Ok）時，≥0.25s 間隔（unscaled time）輸出一次
        /// [DollFrame] ik target: L dir=(x,y,z) reach=…m R dir=… reach=…m（未驅動側省略 token；純觀測零行為）。
        /// </summary>
        private void EmitIkTargetLine()
        {
            if (_ikTargetDir[0] == null && _ikTargetDir[1] == null) return;
            float now = Time.unscaledTime;
            if (now < _ikTargetNext) return;
            _ikTargetNext = now + 0.25f;
            var line = new System.Text.StringBuilder("[DollFrame] ik target:");
            AppendTargetSide(line, "L", 0);
            AppendTargetSide(line, "R", 1);
            Plugin.Log.LogInfo(line.ToString());
        }

        private void AppendTargetSide(System.Text.StringBuilder line, string label, int side)
        {
            if (_ikTargetDir[side] is Snx.Vector3 dir)
                line.Append($" {label} dir=({dir.X:0.##},{dir.Y:0.##},{dir.Z:0.##}) reach={_ikTargetReach[side]:0.##}m");
        }

        #endregion

        #region 接地 GroundCheck — F3 腳趾接地 telemetry（每 attach 3 取樣、90s 後停止）
        /// <summary>
        /// F3 foot grounding telemetry, read-only over the drive: averages the available
        /// rig toe-node world Y (char side) against the available game toe-anchor world Y
        /// (game side), 3 samples per attach (the at-attach one + two steady-state ones
        /// 15 s, 30 s in, separating a spawn-frame transient from a real offset). Samples
        /// stop after 90 s unscaled; the window expiring without a resolvable sample stays
        /// silent (no invented line).
        /// </summary>
        private void GroundCheck()
        {
            if (!GroundTelemetry || _groundSamplesLeft <= 0) return;
            var now = Time.unscaledTime;
            if (now >= _groundStopAt)
            {
                _groundSamplesLeft = 0;
                return;
            }
            if (now < _groundNextTry) return;
            _groundNextTry = now + 15f;

            bool charOk = (_charToeL != null || _charToeR != null) && (_gameToeL != null || _gameToeR != null);
            if (!charOk) return;
            float cy = 0f;
            int cn = 0;
            if (_charToeL != null) { cy += _charToeL.position.y; cn++; }
            if (_charToeR != null) { cy += _charToeR.position.y; cn++; }
            float gy = 0f;
            int gn = 0;
            if (_gameToeL != null) { gy += _gameToeL.position.y; gn++; }
            if (_gameToeR != null) { gy += _gameToeR.position.y; gn++; }
            cy /= cn;
            gy /= gn;
            _groundSamplesLeft--;
            Plugin.Log.LogInfo($"[DollFrame] ground-check: char-toe {cy:0.###}m game-toe {gy:0.###}m delta {(cy - gy) * 100f:+0.#;-0.#}cm");
        }



        #endregion
        // boundary relabels: both frameworks store xyzw / xyz and share Hamilton product
        // with column-vector application - the conversions are exact field copies
        private static Snx.Quaternion QuatS(Quaternion q) => new Snx.Quaternion(q.x, q.y, q.z, q.w);
        private static Quaternion QuatU(Snx.Quaternion q) => new Quaternion(q.X, q.Y, q.Z, q.W);
        private static Snx.Vector3 VecS(Vector3 v) => new Snx.Vector3(v.x, v.y, v.z);
        private static Vector3 VecU(Snx.Vector3 v) => new Vector3(v.X, v.Y, v.Z);

        // 掌骨原點在腕關節，握把／前握把實際抓點在掌心（約往中指根 Digit31 偏移 45%）。
        private static Snx.Vector3 PalmGripPos(Transform palm, Transform mid)
        {
            var p = VecS(palm.position);
            if (mid != null)
                p += (VecS(mid.position) - p) * 0.45f;
            return p;
        }

        // 精準抓取位置計算（誤差 < 0.2cm）：
        // 1) 算出遊戲掌心抓點（PalmGripPos，直接取世界座標以同時對應 ThirdPerson 與 FreeCamera 可見武器）；
        // 2) 減去 PMX 手腕在該幀世界旋轉下到 PMX 掌心抓點（RigPalmGripLocal）的向量，
        //    使 PMX 掌心（而非手腕關節）精準重合遊戲掌心握持點。
        private Snx.Vector3 ComputePreciseWristTarget(Transform palm, in ArmRestChain rest)
        {
            var gameGripPos = PalmGripPos(palm, rest.PalmMidBone);
            var wristEntry = rest.WristIdx >= 0 && rest.WristIdx < _entryOf.Length ? _entryOf[rest.WristIdx] : null;
            var wristWorldRot = wristEntry != null
                ? RigAim.CopyTarget(QuatS(palm.rotation), QuatS(wristEntry.RestOffset))
                : QuatS(palm.rotation);
            var rigGripOffset = Snx.Vector3.Transform(rest.RigPalmGripLocal, wristWorldRot);
            return gameGripPos - rigGripOffset;
        }

        /// <summary>
        /// games + the digit anchors resolved from the palms (AddDigitBones): the one anchor
        /// surface both the FP slot resolution and the boot-time contract self-check digit
        /// lookups run against.
        /// </summary>
        internal static Dictionary<string, Transform> DigitAnchors(Dictionary<string, Transform> games)
        {
            var anchors = new Dictionary<string, Transform>();
            foreach (var kv in games) anchors[kv.Key] = kv.Value;
            AddDigitBones(games.TryGetValue("palmL", out var palmL) ? palmL : null, "L", anchors);
            AddDigitBones(games.TryGetValue("palmR", out var palmR) ? palmR : null, "R", anchors);
            return anchors;
        }

        // Digit chains of one palm: the anchor keys are the BoneContract.Digits rows
        // ("digit{f}_{s}{side}"); segment s+1 hangs off segment s (Base HumanLDigitNN).
        private static void AddDigitBones(Transform palm, string side, Dictionary<string, Transform> into)
        {
            if (palm == null) return;
            foreach (var key in BoneContract.Digits)
            {
                if (!key.EndsWith(side, StringComparison.Ordinal)) continue;
                int f = key[5] - '0', s = key[7] - '0';
                if (s == 1)
                {
                    into[key] = FindChildContaining(palm, "Digit" + f + "1");
                }
                else
                {
                    Transform prev;
                    bool prevFound = into.TryGetValue("digit" + f + "_" + (s - 1) + side, out prev);
                    into[key] = prevFound && prev != null ? FindChildContaining(prev, "Digit" + f + s) : null;
                }
            }
        }

        private static Transform FindChildContaining(Transform root, string snippet)
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

    }
#endif
}
