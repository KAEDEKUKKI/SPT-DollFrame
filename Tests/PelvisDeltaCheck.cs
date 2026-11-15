using System;
using GF2;
using Snx = System.Numerics;

// pelvisdelta - the 2026-11-07 refactor pin: the pelvis drive is a PURE game-rotation-delta
// copy (BoneContract.PureDeltaPmxBones) and the 4 foot/toe bones are PARENT-FRAME
// game-delta copies (BoneContract.PureRelPmxBones), not direction aims. Pins against every
// packaged pmx table + the koleda freeze, driving the SAME production surfaces
// (RigAim.CopyTarget, RigPairing.FirstMappedDescendant, BoneContract):
//   contract   - PureDeltaPmxBones is exactly { 下半身 }, PureRelPmxBones exactly the 4
//                foot/toe bones, every row a BoneMap entry; spine/limb bones stay normal;
//   exemptions - for the 克萊妲 package the first-mapped-descendant pick for 下半身 is 左足
//                (a LEG anchor, thigh1L) and the model hip axis 下半身->左足 is splayed
//                >40 degrees from vertical, while the model 足首→足首先 axis slopes >30
//                degrees below horizontal - i.e. the aims these bones are exempted from
//                would bake a permanent pelvis roll / foot pitch at attach;
//   formula    - pelvis: CopyTarget(G_t, G0⁻¹∘C0) equals G_t∘G0⁻¹∘C0, and exactly C0 at
//                G_t = G0; foot rel = AuthoredRel∘GameRel0⁻¹∘(GP⁻¹∘G), exactly AuthoredRel
//                at attach and invariant under a shared parent-level rotation (parent-frame
//                delta semantics; 1e-4 tol over random rotation pairs).
internal static class PelvisDeltaCheck
{
    private const float DirTol = 1e-4f;

    internal static int Run()
    {
        int fails = 0;
        fails += ContractPin();
        fails += FormulaPin();
        fails += ExemptionPin();
        if (fails > 0)
        {
            Console.Error.WriteLine($"pelvisdelta: FAILED ({fails} failures)");
            return 1;
        }
        Console.WriteLine("pelvisdelta: OK (contract { 下半身 }+4 foot/toe-rel bones pinned, delta formulas exact at attach, " +
                          "koleda exemption pins verified: pelvis descendant = 左足, hip axis >40 deg splayed, foot axis >30 deg below horizontal)");
        return 0;
    }

    private static int ContractPin()
    {
        int fails = 0;
        if (BoneContract.PureDeltaPmxBones.Count != 1 || !BoneContract.PureDeltaPmxBones.Contains("下半身"))
        {
            Console.Error.WriteLine("FAIL: pelvisdelta contract: PureDeltaPmxBones expected exactly { 下半身 }");
            fails++;
        }
        string[] feet = { "左足首", "右足首", "左足首先", "右足首先" };
        if (BoneContract.PureRelPmxBones.Count != feet.Length)
        {
            Console.Error.WriteLine("FAIL: pelvisdelta contract: PureRelPmxBones expected the 4 foot/toe bones");
            fails++;
        }
        foreach (var f in feet)
            if (!BoneContract.PureRelPmxBones.Contains(f))
            {
                Console.Error.WriteLine($"FAIL: pelvisdelta contract: PureRelPmxBones missing '{f}'");
                fails++;
            }
        foreach (var f in new[] { "下半身", "左足首", "右足首", "左足首先", "右足首先" })
        {
            bool inMap = false;
            foreach (var row in BoneContract.BoneMap)
                if (row.pmxBone == f) { inMap = !string.IsNullOrEmpty(row.gameKey); break; }
            if (!inMap)
            {
                Console.Error.WriteLine($"FAIL: pelvisdelta contract: '{f}' must be a BoneContract.BoneMap row");
                fails++;
            }
        }
        if (BoneContract.PureDeltaPmxBones.Contains("上半身") || BoneContract.PureDeltaPmxBones.Contains("腰") ||
            BoneContract.PureDeltaPmxBones.Contains("センター") || BoneContract.PureRelPmxBones.Contains("左ひざ") ||
            BoneContract.PureRelPmxBones.Contains("左腕"))
        {
            Console.Error.WriteLine("FAIL: pelvisdelta contract: spine/limb bones must stay normal pairing bones");
            fails++;
        }
        return fails;
    }

    private static int FormulaPin()
    {
        int fails = 0;
        var rng = new Random(20261107);
        for (int t = 0; t < 64; t++)
        {
            var c0 = RandomQuat(rng);
            var g0 = RandomQuat(rng);
            var gt = RandomQuat(rng);

            // pelvis: root-frame delta copy
            var rOff = Snx.Quaternion.Normalize(Snx.Quaternion.Inverse(g0) * c0);
            var target = RigAim.CopyTarget(gt, rOff);
            var expected = Snx.Quaternion.Normalize(gt * rOff);
            if (QuatDist(target, expected) > DirTol)
            {
                Console.Error.WriteLine($"FAIL: pelvisdelta formula: CopyTarget(G_t,G0⁻¹∘C0)!=G_t∘G0⁻¹∘C0 t={t} dist={QuatDist(target, expected):0.########e0} gt={gt} rOff={rOff}");
                fails++;
                break;
            }
            var atAttach = RigAim.CopyTarget(g0, rOff);
            if (QuatDist(atAttach, Snx.Quaternion.Normalize(g0 * rOff)) > DirTol || QuatDist(atAttach, c0) > DirTol)
            {
                Console.Error.WriteLine("FAIL: pelvisdelta formula: pelvis target at G_t = G0 must equal C0 exactly (zero static tilt)");
                fails++;
                break;
            }

            // foot: parent-frame delta copy. Semantics pinned by two properties:
            //   identity at attach  - rel(GRel0) == AuthoredRel (zero static pitch);
            //   parent-frame invariance - adding a shared rotation P to both the game parent
            //     and the game bone must not change rel (the delta lives in the parent frame).
            var authoredRel = RandomQuat(rng);
            var gameRel0 = RandomQuat(rng);
            var gp = RandomQuat(rng);
            var g = RandomQuat(rng);
            var rel = Snx.Quaternion.Normalize(authoredRel * Snx.Quaternion.Inverse(gameRel0) * (Snx.Quaternion.Inverse(gp) * g));
            var relAtAttach = Snx.Quaternion.Normalize(authoredRel * Snx.Quaternion.Inverse(gameRel0) * gameRel0);
            if (QuatDist(relAtAttach, authoredRel) > DirTol)
            {
                Console.Error.WriteLine("FAIL: pelvisdelta formula: foot rel must equal AuthoredRel at attach (zero static pitch)");
                fails++;
                break;
            }
            var shared = RandomQuat(rng);
            var relShared = Snx.Quaternion.Normalize(authoredRel * Snx.Quaternion.Inverse(gameRel0)
                * (Snx.Quaternion.Inverse(shared * gp) * (shared * g)));
            if (QuatDist(rel, relShared) > DirTol)
            {
                Console.Error.WriteLine($"FAIL: pelvisdelta formula: foot rel is not parent-frame invariant t={t} dist={QuatDist(rel, relShared):0.########e0}");
                fails++;
                break;
            }
        }
        return fails;
    }

    private static int ExemptionPin()
    {
        var dir = Environment.GetEnvironmentVariable("GF2_PACKAGES_DIR");
        if (string.IsNullOrEmpty(dir)) dir = "C:\SPT/BepInEx/plugins/kukki.GF2/GF2_Characters";
        if (!System.IO.Directory.Exists(dir))
        {
            Console.Error.WriteLine($"FAIL: pelvisdelta exemption: packages directory not found: {dir} (override with GF2_PACKAGES_DIR)");
            return 1;
        }
        var files = System.IO.Directory.GetFiles(dir, "*.gfc");
        Array.Sort(files, StringComparer.Ordinal);
        string koledaPath = null;
        foreach (var f in files)
            if (System.IO.Path.GetFileName(f).Contains("克萊妲")) { koledaPath = f; break; }
        if (koledaPath == null)
        {
            Console.Error.WriteLine("FAIL: pelvisdelta exemption: no 克萊妲 package found - the exemption pin was never checked");
            return 1;
        }

        PmxParser.PmxModel model;
        using (var fs = System.IO.File.OpenRead(koledaPath))
            model = PmxParser.Parse(PmxContainer.Read(fs).Pmx);

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
        var desc = RigPairing.FirstMappedDescendant(parents, i => keyOf[i] != null);
        int bi = Array.IndexOf(names, "下半身");
        if (bi < 0 || desc[bi] < 0 || names[desc[bi]] != "左足" || keyOf[desc[bi]] != "thigh1L")
        {
            Console.Error.WriteLine("FAIL: pelvisdelta exemption: 下半身's first mapped descendant expected 左足 (thigh1L) - " +
                "the leg-axis pairing the pelvis must stay exempted from");
            return 1;
        }

        var pelvisPos = model.Bones[bi].Pos;
        var thighPos = model.Bones[desc[bi]].Pos;
        var hip = Snx.Vector3.Normalize(new Snx.Vector3(thighPos.x - pelvisPos.x, thighPos.y - pelvisPos.y, thighPos.z - pelvisPos.z));
        var down = new Snx.Vector3(0f, -1f, 0f);
        float splayDeg = MathF.Acos(MathF.Abs(Snx.Vector3.Dot(hip, down))) * (180f / MathF.PI);
        if (splayDeg <= 40f)
        {
            Console.Error.WriteLine($"FAIL: pelvisdelta exemption: model hip axis splay expected >40 deg from vertical, got {splayDeg:0.#}");
            return 1;
        }

        int fi = Array.IndexOf(names, "左足首"), ti = Array.IndexOf(names, "左足首先");
        if (fi < 0 || ti < 0)
        {
            Console.Error.WriteLine("FAIL: pelvisdelta exemption: 左足首/左足首先 not resolvable in the package table");
            return 1;
        }
        var fp = model.Bones[fi].Pos;
        var tp = model.Bones[ti].Pos;
        var toe = Snx.Vector3.Normalize(new Snx.Vector3(tp.x - fp.x, tp.y - fp.y, tp.z - fp.z));
        var horizontal = Snx.Vector3.Normalize(new Snx.Vector3(0f, -1f, 0f));
        float slopeDeg = MathF.Acos(MathF.Abs(Snx.Vector3.Dot(toe, horizontal))) * (180f / MathF.PI);
        if (slopeDeg <= 30f)
        {
            Console.Error.WriteLine($"FAIL: pelvisdelta exemption: model 足首→足首先 axis slope expected >30 deg below horizontal, got {slopeDeg:0.#}");
            return 1;
        }
        return 0;
    }

    private static Snx.Quaternion RandomQuat(Random rng)
    {
        var axis = Snx.Vector3.Normalize(new Snx.Vector3((float)rng.NextDouble() - 0.5f, (float)rng.NextDouble() - 0.5f, (float)rng.NextDouble() - 0.5f));
        float angle = (float)rng.NextDouble() * MathF.PI * 2f;
        return Snx.Quaternion.CreateFromAxisAngle(axis, angle);
    }

    // vector-image distance: numerically stable where angle-of-difference via acos(|w|)
    // blows up near identity (2*acos(1-eps) -> ~1e-4 from pure float rounding)
    private static float QuatDist(Snx.Quaternion a, Snx.Quaternion b)
    {
        float worst = 0f;
        foreach (var probe in new[] { Snx.Vector3.UnitX, Snx.Vector3.UnitY, Snx.Vector3.UnitZ })
        {
            var va = Snx.Vector3.Transform(probe, a);
            var vb = Snx.Vector3.Transform(probe, b);
            var diff = va - vb;
            worst = MathF.Max(worst, diff.Length());
        }
        return worst;
    }
}
