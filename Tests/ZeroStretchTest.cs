using System;
using System.IO;
using Snx = System.Numerics;
using GF2;

// Zero-stretch invariant harness (the pivot's core deliverable). Three sections:
//
// 1. SIM - the decree test: a synthetic 3-bone rig (parent -> child -> grandchild with
//    non-collinear pmx-unit offsets, placement root scale 0.078 like the real unit scale)
//    against a synthetic 3-anchor game rig that rotates 90 deg around Y over 60 frames
//    while its pelvis translates. Every frame drives the SHARED RigAim math (the exact
//    code CharacterRig.Drive delegates to, no duplicated formula) through a transform
//    sim that replicates Unity semantics exactly (worldRot = parentWorldRot o localRot;
//    worldPos = parentWorldPos + Rotate(parentWorldRot, worldScale * localPos); world
//    scale = the placement root's scale; only the root is driven directly). Asserts per
//    frame:
//      (a) every parent->child pivot distance is CONSTANT within 1e-5 - rotation-only
//          movement: the driver writes local rotations only, so the span-stretch of the
//          retired game-bone reskin is structurally impossible, and
//      (b) the end-effector FOLLOWS the game end-effector: the rig's bone0->bone1 and
//          bone1->bone2 segment unit directions track the game's (G1-G0) / (G2-G1) world
//          directions within 1e-4 (TryLiveTarget two-vector drive, the baked CopyTarget
//          as the degenerate-frame fallback, applied parent-first via LocalFromWorld), and
//      (c) the rig bone's world rotation delta equals the game bone's (twist kept).
// 2. SKEW - the pairing-span regression: same rig and drive, but the game chain carries
//    a NON-AXIAL intermediate-bone flex (a Thigh2/Spine2-class bone between the anchor
//    and its pairing mate) that 30 deg-swinging flexes the mate's offset direction. The
//    LIVE pairing axis re-measurement must track the swung axis every frame (the baked
//    copy cannot and is asserted to diverge at the peak - the sensitivity pin against a
//    deaf test, and the regression pin for the 腰部扭轉 + X-stance knee class).
// 3. PAIRING PIN - the retarget pairing rule (RigPairing.FirstMappedDescendant /
//    NearestMappedAncestor, the exact Build code) over every packaged pmx table: no
//    mapped bone may be pairing-orphaned (no mapped descendant AND no mapped ancestor);
//    on the pinned character (克萊妲) the exact first-mapped-descendant picks, the leaf
//    pairs and the two self-anchored pairs (腰, センター -> same pelvis key as their
//    mapped descendant) are asserted verbatim.
// Success prints `zerostretch: sim OK (...)`, `zerostretch: directions OK (...)`,
// `zerostretch: skew OK (...)` and `zerostretch: pairing OK (N/N packages ...)`.
internal static class ZeroStretchTest
{
    private const float DistTol = 1e-5f;
    private const float DirTol = 1e-4f;
    private const float Scale = 0.078f;
    private const int Frames = 60;
    private const float GameRotationDeg = 90f;

    private static readonly Snx.Vector3[] Pmx =
    {
        new(0f, 12.7764f, -0.2196f),  // root pivot (pmx units, hip height)
        new(1.4f, 11.5f, 0.02f),      // non-collinear offset -> child
        new(1.44f, 6.9f, 0.07f),      // grandchild
    };
    private static readonly Snx.Vector3[] GameBase =
    {
        new(0f, 0f, 0f),              // pelvis/root anchor
        new(0.13f, -0.35f, 0.06f),
        new(0.15f, -0.62f, 0.12f),
    };
    private static readonly Snx.Vector3 AttachPos = new(0.5f, 0.02f, 1.0f);

    internal static int Run() => Sim() + SkewSim() + PairingPin();

    // ---- transform-tree sim mirroring Unity Transform semantics ------------------------

    private sealed class Node
    {
        internal int Parent;
        internal Snx.Vector3 LocalPos;
        internal Snx.Quaternion LocalRot = Snx.Quaternion.Identity;
        internal Snx.Vector3 WorldPos;
        internal Snx.Quaternion WorldRot = Snx.Quaternion.Identity;
        internal float WorldScale;
    }

    private static void UpdateWorld(Node[] nodes, int i)
    {
        var n = nodes[i];
        if (n.Parent < 0) return; // placement root: driven directly (pos/rot), scale fixed
        var p = nodes[n.Parent];
        n.WorldScale = p.WorldScale;
        n.WorldRot = p.WorldRot * n.LocalRot;
        n.WorldPos = p.WorldPos + Snx.Vector3.Transform(p.WorldScale * n.LocalPos, p.WorldRot);
    }

    private static Snx.Vector3 Unit(Snx.Vector3 v) => v / v.Length();

    private static int Sim()
    {
        // Build exactly like CharacterRig.Build: placement root carries yaw + unit scale,
        // bone nodes use pmx difference local positions with identity rotations.
        var rootRot = Snx.Quaternion.CreateFromAxisAngle(Snx.Vector3.UnitY, MathF.PI / 6f);
        var nodes = new Node[4];
        nodes[0] = new Node { Parent = -1, LocalPos = AttachPos, WorldPos = AttachPos, WorldRot = rootRot, WorldScale = Scale };
        nodes[1] = new Node { Parent = 0, LocalPos = Pmx[0] };
        nodes[2] = new Node { Parent = 1, LocalPos = Pmx[1] - Pmx[0] };
        nodes[3] = new Node { Parent = 2, LocalPos = Pmx[2] - Pmx[1] };
        for (int i = 1; i < nodes.Length; i++) UpdateWorld(nodes, i);

        var attach = new[] { nodes[1].WorldPos, nodes[2].WorldPos, nodes[3].WorldPos };
        var dist01 = Snx.Vector3.Distance(attach[1], attach[0]);
        var dist12 = Snx.Vector3.Distance(attach[2], attach[1]);

        // game bone attach rotations (arbitrary, non-identity: the rest offset must absorb them)
        var g0Rot = new[]
        {
            Snx.Quaternion.CreateFromYawPitchRoll(0.3f, -1.2f, 0.4f),
            Snx.Quaternion.CreateFromYawPitchRoll(-0.7f, 0.2f, 1.1f),
            Snx.Quaternion.CreateFromYawPitchRoll(1.4f, 0.5f, -0.6f),
        };

        // driver entries paired exactly as Build pairs them: the first mapped descendant
        // supplies the child anchor; the leaf (bone3) pairs its parent's anchor. Build
        // computes the rotation-copy rest offset AND the live two-vector roll channels
        // from the attach pose (frame 0 game pose).
        var entries = new (int bone, Snx.Quaternion rOff, Snx.Vector3 dC, Snx.Vector3 dCp,
            Snx.Vector3 vSec, Snx.Quaternion c0, int anchor, int mate, bool leaf)[3];
        var raw = new (int bone, Snx.Vector3 dC, int anchor, int mate, bool leaf)[]
        {
            (1, Unit(attach[1] - attach[0]), 0, 1, false),
            (2, Unit(attach[2] - attach[1]), 1, 2, false),
            (3, Unit(attach[2] - attach[1]), 2, 1, true),
        };
        var rigAttachRot = new Snx.Quaternion[nodes.Length];
        for (int k = 0; k < raw.Length; k++)
        {
            var (bone, dC, anchor, mate, leaf) = raw[k];
            var dG0 = leaf ? GameBase[anchor] - GameBase[mate] : GameBase[mate] - GameBase[anchor];
            if (!RigAim.TryBindLiveFrame(g0Rot[anchor], nodes[bone].WorldRot, dC, dG0,
                    out var rOff, out var dCp, out var vSec))
            {
                Console.Error.WriteLine("zerostretch FAIL: degenerate rest offset");
                return 1;
            }
            entries[k] = (bone, rOff, dC, dCp, vSec, nodes[bone].WorldRot, anchor, mate, leaf);
        }

        int badDist = 0, badDir = 0, badTwist = 0;
        for (int f = 0; f <= Frames; f++)
        {
            float t = (float)f / Frames;
            var qg = Snx.Quaternion.CreateFromAxisAngle(Snx.Vector3.UnitY, GameRotationDeg * t * MathF.PI / 180f);
            var pelvisDelta = new Snx.Vector3(0.3f * t, 0f, 0.1f * t);
            var g = new Snx.Vector3[GameBase.Length];
            var gRot = new Snx.Quaternion[GameBase.Length];
            for (int i = 0; i < g.Length; i++)
            {
                g[i] = Snx.Vector3.Transform(GameBase[i], qg) + pelvisDelta;
                gRot[i] = qg * g0Rot[i];
            }

            // ROOT DRIVE (production): the rig root is a child of Root_Joint; its local
            // placement is constant and only the pelvis translation in Root_Joint space is
            // added (zero here: the game moves rigidly with its root)
            nodes[0].WorldRot = qg * rootRot;
            nodes[0].WorldPos = Snx.Vector3.Transform(AttachPos, qg) + pelvisDelta;

            // live two-vector drive parent-first (CharacterRig.Drive): target =
            // TryLiveTarget(dC, dCp, vSec, G_now, live axis), baked CopyTarget fallback
            foreach (var (bone, rOff, dC, dCp, vSec, c0, anchor, mate, leaf) in entries)
            {
                var dGNow = leaf ? g[anchor] - g[mate] : g[mate] - g[anchor];
                var target = RigAim.TryLiveTarget(dC, dCp, vSec, c0, gRot[anchor], dGNow, out var live)
                    ? live
                    : RigAim.CopyTarget(gRot[anchor], rOff);
                var parentRot = nodes[bone - 1].WorldRot; // parents are processed first
                nodes[bone].LocalRot = RigAim.LocalFromWorld(parentRot, target);
                nodes[bone].WorldRot = parentRot * nodes[bone].LocalRot; // == target
                if (f == 0) rigAttachRot[bone] = target;
                // (c) twist: the rig bone carries the game bone's full rotation delta
                var delta = target * Snx.Quaternion.Inverse(rigAttachRot[bone]);
                if (MathF.Abs(Snx.Quaternion.Dot(Snx.Quaternion.Normalize(delta), qg)) < 1f - DirTol)
                {
                    badTwist++;
                    if (badTwist == 1)
                        Console.Error.WriteLine($"zerostretch FAIL: rotation delta (twist) diverged at frame {f} bone {bone}");
                }
            }
            for (int i = 1; i < nodes.Length; i++) UpdateWorld(nodes, i);

            // (a) zero-stretch: pivot distances constant in every frame of the drive
            var p1 = nodes[1].WorldPos;
            var p2 = nodes[2].WorldPos;
            var p3 = nodes[3].WorldPos;
            if (MathF.Abs(Snx.Vector3.Distance(p2, p1) - dist01) > DistTol ||
                MathF.Abs(Snx.Vector3.Distance(p3, p2) - dist12) > DistTol)
            {
                badDist++;
                if (badDist == 1)
                    Console.Error.WriteLine($"zerostretch FAIL: pivot distances changed at frame {f} " +
                        $"(|12| {Snx.Vector3.Distance(p2, p1)} vs {dist01}, |23| {Snx.Vector3.Distance(p3, p2)} vs {dist12})");
            }

            // (b) end-effector follows the game end-effector direction
            var dot01 = Snx.Vector3.Dot(Unit(p2 - p1), Unit(g[1] - g[0]));
            var dot12 = Snx.Vector3.Dot(Unit(p3 - p2), Unit(g[2] - g[1]));
            if (dot01 < 1f - DirTol || dot12 < 1f - DirTol)
            {
                badDir++;
                if (badDir == 1)
                    Console.Error.WriteLine($"zerostretch FAIL: end-effector direction diverged at frame {f} " +
                        $"(dot01 {dot01}, dot12 {dot12})");
            }
        }

        int fails = badDist + badDir + badTwist;
        if (fails > 0)
        {
            Console.Error.WriteLine($"zerostretch: sim FAILED ({fails} failures, {badDist} distance, {badDir} direction)");
            return 1;
        }
        Console.WriteLine($"zerostretch: sim OK ({Frames} frames, {GameRotationDeg:0} deg game rotation + pelvis drive, " +
                          $"pivot distances constant within {DistTol})");
        Console.WriteLine($"zerostretch: directions OK (segment directions track the game rig within {DirTol})");
        Console.WriteLine($"zerostretch: twist OK (rig bones carry the game bones' full rotation delta within {DirTol})");
        return 0;
    }

    // ---- skew regression: non-axial intermediate-bone flex in the game chain ---------

    private const float SkewFlexDeg = 30f;

    private static int SkewSim()
    {
        // Identical rig build to Sim(); the game chain gains a Thigh2/Spine2-class
        // INTERMEDIATE bone whose relative flex swings the mate's offset direction
        // (non-axial: the flex axis is perpendicular to the pairing axis), so the baked
        // attach axis no longer tracks the game. Entry pairing: bone1 pairs anchors 0->1
        // across the flexing bone, bone2 pairs 1->2, bone3 is the leaf (2, mate 1).
        var rootRot = Snx.Quaternion.CreateFromAxisAngle(Snx.Vector3.UnitY, MathF.PI / 6f);
        var nodes = new Node[4];
        nodes[0] = new Node { Parent = -1, LocalPos = AttachPos, WorldPos = AttachPos, WorldRot = rootRot, WorldScale = Scale };
        nodes[1] = new Node { Parent = 0, LocalPos = Pmx[0] };
        nodes[2] = new Node { Parent = 1, LocalPos = Pmx[1] - Pmx[0] };
        nodes[3] = new Node { Parent = 2, LocalPos = Pmx[2] - Pmx[1] };
        for (int i = 1; i < nodes.Length; i++) UpdateWorld(nodes, i);

        var attach = new[] { nodes[1].WorldPos, nodes[2].WorldPos, nodes[3].WorldPos };
        var dist01 = Snx.Vector3.Distance(attach[1], attach[0]);
        var dist12 = Snx.Vector3.Distance(attach[2], attach[1]);

        var g0Rot = new[]
        {
            Snx.Quaternion.CreateFromYawPitchRoll(0.3f, -1.2f, 0.4f),
            Snx.Quaternion.CreateFromYawPitchRoll(-0.7f, 0.2f, 1.1f),
            Snx.Quaternion.CreateFromYawPitchRoll(1.4f, 0.5f, -0.6f),
        };

        var entries = new (int bone, Snx.Quaternion rOff, Snx.Vector3 dC, Snx.Vector3 dCp,
            Snx.Vector3 vSec, Snx.Quaternion c0, int anchor, int mate, bool leaf)[3];
        var raw = new (int bone, Snx.Vector3 dC, int anchor, int mate, bool leaf)[]
        {
            (1, Unit(attach[1] - attach[0]), 0, 1, false),
            (2, Unit(attach[2] - attach[1]), 1, 2, false),
            (3, Unit(attach[2] - attach[1]), 2, 1, true),
        };
        for (int k = 0; k < raw.Length; k++)
        {
            var (bone, dC, anchor, mate, leaf) = raw[k];
            var dG0 = leaf ? GameBase[anchor] - GameBase[mate] : GameBase[mate] - GameBase[anchor];
            if (!RigAim.TryBindLiveFrame(g0Rot[anchor], nodes[bone].WorldRot, dC, dG0,
                    out var rOff, out var dCp, out var vSec))
            {
                Console.Error.WriteLine("zerostretch FAIL: skew degenerate rest offset");
                return 1;
            }
            entries[k] = (bone, rOff, dC, dCp, vSec, nodes[bone].WorldRot, anchor, mate, leaf);
        }

        // the intermediate bone's flex axis: ⊥ to anchor0->mate1 in anchor0's local frame
        var off01 = GameBase[1] - GameBase[0];
        var off01Local = Snx.Vector3.Transform(off01, Snx.Quaternion.Inverse(g0Rot[0]));
        var axisLocal = Unit(Snx.Vector3.Cross(off01Local, Snx.Vector3.UnitY));
        var off12Local = Snx.Vector3.Transform(GameBase[2] - GameBase[1], Snx.Quaternion.Inverse(g0Rot[1]));
        var gLoc2 = Snx.Quaternion.Inverse(g0Rot[1]) * g0Rot[2]; // foot's local rotation relative to calf

        int badDist = 0, badDir = 0;
        float bakedDotAtPeak = 1f;
        for (int f = 0; f <= Frames; f++)
        {
            float t = (float)f / Frames;
            var qg = Snx.Quaternion.CreateFromAxisAngle(Snx.Vector3.UnitY, GameRotationDeg * t * MathF.PI / 180f);
            var pelvisDelta = new Snx.Vector3(0.3f * t, 0f, 0.1f * t);
            var flexRad = SkewFlexDeg * MathF.Sin(2f * MathF.PI * t) * MathF.PI / 180f;
            var qFlex = Snx.Quaternion.CreateFromAxisAngle(axisLocal, flexRad);

            var rot0 = qg * g0Rot[0];
            var g = new Snx.Vector3[3];
            var gRot = new Snx.Quaternion[3];
            g[0] = Snx.Vector3.Transform(GameBase[0], qg) + pelvisDelta;
            gRot[0] = rot0;
            gRot[1] = rot0 * qFlex; // calf anchor carries the intermediate flex
            g[1] = g[0] + Snx.Vector3.Transform(Snx.Vector3.Transform(off01Local, qFlex), rot0);
            gRot[2] = gRot[1] * gLoc2; // foot: rigid below, keeps its own local rotation
            g[2] = g[1] + Snx.Vector3.Transform(off12Local, gRot[1]);

            nodes[0].WorldRot = qg * rootRot;
            nodes[0].WorldPos = Snx.Vector3.Transform(AttachPos, qg) + pelvisDelta;

            foreach (var (bone, rOff, dC, dCp, vSec, c0, anchor, mate, leaf) in entries)
            {
                var dGNow = leaf ? g[anchor] - g[mate] : g[mate] - g[anchor];
                var target = RigAim.TryLiveTarget(dC, dCp, vSec, c0, gRot[anchor], dGNow, out var live)
                    ? live
                    : RigAim.CopyTarget(gRot[anchor], rOff);
                var parentRot = nodes[bone - 1].WorldRot;
                nodes[bone].LocalRot = RigAim.LocalFromWorld(parentRot, target);
                nodes[bone].WorldRot = parentRot * nodes[bone].LocalRot;
            }
            for (int i = 1; i < nodes.Length; i++) UpdateWorld(nodes, i);

            var p1 = nodes[1].WorldPos;
            var p2 = nodes[2].WorldPos;
            var p3 = nodes[3].WorldPos;
            if (MathF.Abs(Snx.Vector3.Distance(p2, p1) - dist01) > DistTol ||
                MathF.Abs(Snx.Vector3.Distance(p3, p2) - dist12) > DistTol)
            {
                badDist++;
                if (badDist == 1)
                    Console.Error.WriteLine($"zerostretch FAIL: skew pivot distances changed at frame {f}");
            }

            var dot01 = Snx.Vector3.Dot(Unit(p2 - p1), Unit(g[1] - g[0]));
            var dot12 = Snx.Vector3.Dot(Unit(p3 - p2), Unit(g[2] - g[1]));
            if (dot01 < 1f - DirTol || dot12 < 1f - DirTol)
            {
                badDir++;
                if (badDir == 1)
                    Console.Error.WriteLine($"zerostretch FAIL: skew segment direction diverged at frame {f} " +
                        $"(dot01 {dot01}, dot12 {dot12})");
            }

            // sensitivity pin: the BAKED copy of bone1 must diverge from the swung axis
            // at the flex peak (otherwise the sim could not see the bug class at all)
            float absFlex = MathF.Abs(flexRad);
            if (absFlex > 0.5f * SkewFlexDeg * MathF.PI / 180f)
            {
                var bakedTarget = RigAim.CopyTarget(gRot[0], entries[0].rOff);
                var bakedDir = Snx.Vector3.Transform(Unit(Pmx[1] - Pmx[0]), bakedTarget);
                var bakedDot = Snx.Vector3.Dot(Unit(bakedDir), Unit(g[1] - g[0]));
                if (bakedDot < bakedDotAtPeak) bakedDotAtPeak = bakedDot;
            }
        }

        int fails = badDist + badDir;
        if (fails > 0 || bakedDotAtPeak > 1f - 1e-2f)
        {
            if (fails == 0)
                Console.Error.WriteLine($"zerostretch FAIL: skew sim is deaf - the baked copy dot at flex peak is {bakedDotAtPeak}");
            Console.Error.WriteLine($"zerostretch: skew FAILED ({fails} failures, baked-copy peak dot {bakedDotAtPeak})");
            return 1;
        }
        Console.WriteLine($"zerostretch: skew OK ({Frames} frames, {SkewFlexDeg:0} deg non-axial intermediate flex: " +
                          $"live pairing tracks the swung axis within {DirTol}, baked copy diverges to dot {bakedDotAtPeak:0.###})");
        return 0;
    }

    // ---- pairing pin over every packaged pmx table -----------------------------------

    private const string DefaultPackagesDir = "C:\SPT/BepInEx/plugins/kukki.GF2/GF2_Characters";

    private static readonly (string bone, string boneKey, string child, string childKey)[] PinnedChildPicks =
    {
        ("下半身", "pelvis", "左足", "thigh1L"), ("上半身", "spine1", "上半身2", "spine3"),
        ("上半身2", "spine3", "首", "neck"), ("首", "neck", "頭", "head"),
        ("左肩", "collarL", "左腕", "upperarmL"), ("左腕", "upperarmL", "左ひじ", "forearmL"),
        ("左ひじ", "forearmL", "左手首", "palmL"), ("左手首", "palmL", "左中指１", "digit3_1L"),
        ("左足", "thigh1L", "左ひざ", "calfL"), ("左ひざ", "calfL", "左足首", "footL"),
        ("左足首", "footL", "左足首先", "toeL"),
    };
    private static readonly (string bone, string boneKey, string parent, string parentKey)[] PinnedLeafPicks =
    {
        ("頭", "head", "首", "neck"), ("左足首先", "toeL", "左足首", "footL"),
        ("左親指２", "digit1_3L", "左親指１", "digit1_2L"),
    };

    private static int PairingPin()
    {
        var dir = Environment.GetEnvironmentVariable("GF2_PACKAGES_DIR");
        if (string.IsNullOrEmpty(dir)) dir = DefaultPackagesDir;
        if (!Directory.Exists(dir))
        {
            Console.Error.WriteLine($"zerostretch: packages directory not found: {dir} (override with GF2_PACKAGES_DIR)");
            return 1;
        }
        var files = Directory.GetFiles(dir, "*.gfc");
        Array.Sort(files, StringComparer.Ordinal);
        if (files.Length == 0)
        {
            Console.Error.WriteLine($"zerostretch: no .gfc packages in {dir}");
            return 1;
        }

        int fails = 0, selfAnchorsTotal = 0, pinnedChecked = 0;
        foreach (var file in files)
        {
            var pkg = Path.GetFileName(file);
            PmxParser.PmxModel model;
            try
            {
                using var fs = File.OpenRead(file);
                model = PmxParser.Parse(PmxContainer.Read(fs).Pmx);
            }
            catch (Exception e)
            {
                Console.Error.WriteLine($"FAIL: zerostretch pairing {pkg}: parse exception: {e.Message}");
                fails++;
                continue;
            }

            int n = model.Bones.Length;
            var names = new string[n];
            var parents = new int[n];
            var keyOf = new string[n];
            for (int i = 0; i < n; i++)
            {
                names[i] = model.Bones[i].Name ?? "";
                parents[i] = model.Bones[i].Parent;
                foreach (var row in BoneContract.BoneMap)
                    if (row.pmxBone == names[i]) { keyOf[i] = row.gameKey; break; }
            }
            bool Mapped(int i) => i >= 0 && i < n && keyOf[i] != null;
            var desc = RigPairing.FirstMappedDescendant(parents, Mapped);

            int selfAnchors = 0;
            for (int i = 0; i < n; i++)
            {
                if (!Mapped(i)) continue;
                var d = desc[i];
                if (d >= 0)
                {
                    if (keyOf[d] == keyOf[i]) selfAnchors++;
                    continue;
                }
                int anc = RigPairing.NearestMappedAncestor(parents, i, Mapped);
                if (anc < 0)
                {
                    Console.Error.WriteLine($"FAIL: zerostretch pairing {pkg}: mapped bone '{names[i]}' ('{keyOf[i]}') is pairing-orphaned");
                    fails++;
                }
            }
            selfAnchorsTotal += selfAnchors;

            if (pkg.Contains("克萊妲"))
            {
                pinnedChecked++;
                fails += PinPicks(pkg, names, keyOf, parents, desc, selfAnchors);
            }
        }
        if (pinnedChecked == 0)
        {
            Console.Error.WriteLine("FAIL: zerostretch pairing: no 克萊妲 package found - the pinned pairing table was never checked");
            fails++;
        }

        if (fails > 0)
        {
            Console.Error.WriteLine($"zerostretch: pairing FAILED ({fails} failures)");
            return 1;
        }
        Console.WriteLine($"zerostretch: pairing OK ({files.Length}/{files.Length} packages, " +
                          $"{selfAnchorsTotal} self-anchored pairs across the set, no pairing orphans, pinned {pinnedChecked})");
        return 0;
    }

    private static int PinPicks(string pkg, string[] names, string[] keyOf, int[] parents, int[] desc, int selfAnchors)
    {
        int fails = 0;
        foreach (var (bone, boneKey, child, childKey) in PinnedChildPicks)
        {
            int bi = IndexOf(names, bone), ci = IndexOf(names, child);
            if (bi < 0 || ci < 0 || keyOf[bi] != boneKey || keyOf[ci] != childKey)
            {
                Console.Error.WriteLine($"FAIL: zerostretch pairing {pkg}: pinned pick '{bone}'->'{child}' not resolvable in the package table");
                fails++;
                continue;
            }
            if (desc[bi] != ci)
            {
                Console.Error.WriteLine($"FAIL: zerostretch pairing {pkg}: '{bone}' expected first mapped descendant '{child}', " +
                    $"got {(desc[bi] >= 0 ? names[desc[bi]] : "(none)")}");
                fails++;
            }
        }
        foreach (var (bone, boneKey, parentName, parentKey) in PinnedLeafPicks)
        {
            int bi = IndexOf(names, bone), pi = IndexOf(names, parentName);
            if (bi < 0 || pi < 0 || keyOf[bi] != boneKey || keyOf[pi] != parentKey)
            {
                Console.Error.WriteLine($"FAIL: zerostretch pairing {pkg}: pinned leaf '{bone}'/'{parentName}' not resolvable");
                fails++;
                continue;
            }
            if (desc[bi] >= 0)
            {
                Console.Error.WriteLine($"FAIL: zerostretch pairing {pkg}: '{bone}' expected LEAF, has mapped descendant '{names[desc[bi]]}'");
                fails++;
                continue;
            }
            int anc = RigPairing.NearestMappedAncestor(parents, bi, x => keyOf[x] != null);
            if (anc != pi)
            {
                Console.Error.WriteLine($"FAIL: zerostretch pairing {pkg}: '{bone}' nearest mapped ancestor expected '{parentName}', " +
                    $"got {(anc >= 0 ? names[anc] : "(none)")}");
                fails++;
            }
        }
        if (selfAnchors != 2)
        {
            Console.Error.WriteLine($"FAIL: zerostretch pairing {pkg}: expected exactly 2 self-anchored mapped pairs (腰, センター), got {selfAnchors}");
            fails++;
        }
        return fails;
    }

    private static int IndexOf(string[] names, string name) => Array.IndexOf(names, name);
}
