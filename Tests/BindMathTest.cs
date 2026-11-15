using System;
using System.Numerics;
using GF2;

// Bindpose invariant harness v3 for the SHARED BindMath.ComposeRetarget (the exact
// composition CharacterRig.BuildBindPoses drives - no duplicated formula). Everything is
// Unity-numeric here: matrices store the offset in column 4 (M14/M24/M34) exactly as
// UnityEngine.Matrix4x4 does, products are the standard product (both frameworks'
// operator* compute the same product on the same indices), and points apply as
// x' = M*x via BindMath.ApplyColumn == Unity MultiplyPoint3x4. The old suite asserted
// the flawed +p formula through the row-vector SN convention - tautological with the
// obsolete legacy composition; v3 pins the three real invariants with INDEPENDENT expectations:
//   placed   R = identity: world(v, t0) = modelRootWorld * v (the placed model - the
//            corrected rest identity; the old bindSpace * Translate(p_bone) would add
//            root(p_bone) per bone and FAILS the delta pin). Also pins the Unity shape
//            of the BindMath factors (offset column / cos,-sin,sin rotation entries vs
//            hand formula) and pivot-neutrality of the conjugated identity TRS.
//   retarget world(v, t0) = pivot + R * (modelRootWorld(v) - pivot): the pivot stays
//            fixed and the pmx direction sample (mesh point one segment past the pivot;
//            placed it is pivot + dirP) lands at pivot + dirG, expectation computed
//            directly as pivot + Vector3.Transform(dirP, q) - never by re-running
//            BindMath. Plus the parent-swing mirror (pivot - dirG), a full 5-factor
//            standard-product replica of the composition, and the row-vs-column
//            application divergence pin.
//   delta    rotating the game bone +90deg Y at runtime (its new worldToLocal is the
//            numeric inverse of the new localToWorld) rigid-transforms every bind-pose
//            world point by the bone's world delta about the game bone origin,
//            expectation gb + Vector3.Transform(world0 - gb, qy), for both an R=identity
//            slot and a retarget slot - the exact SkinnedMeshRenderer contract
//            bones[i].localToWorldMatrix * bindpose.
internal static class BindMathTest
{
    private const float Tol = 1e-6f;
    private const float Scale = 0.078f;

    private static readonly Vector3 RootT = new(0f, 0.01f, 0f);
    private static readonly Vector3 Mid = new(0.5f, 0.5f, 0.5f);
    // modelRootWorld as Unity numerics: attachParent.l2w * TRS(0, yOffset, yaw=0, scale)
    private static readonly Matrix4x4 Root = new(
        Scale, 0, 0, RootT.X,
        0, Scale, 0, RootT.Y,
        0, 0, Scale, RootT.Z,
        0, 0, 0, 1f);

    // rig: pmx mesh-space points (absolute pmx units), BoneMap-row names keep the
    // BoneContract linkage the old suite pinned
    private static readonly (string pmx, Vector3 pos)[] Bones =
    {
        ("下半身", new Vector3(1.2f, 0.5f, 3.3f)),
        ("左腕", new Vector3(-0.4f, 2.2f, 1.1f)),
        ("左手首", new Vector3(3.0f, -1.5f, 0.7f)),
    };
    // retarget slot: pmx joint pb with pmx parent joint pp (pmx segment direction down -Y)
    private static readonly Vector3 Pb = new(1.2f, 0.5f, 3.3f);
    private static readonly Vector3 Pp = new(1.2f, 1.0f, 3.3f);

    private static int fails;
    private static int sectionFails;

    internal static int Run()
    {
        fails = 0;

        PlacedSection();
        RetargetSection();
        DeltaSection();

        if (fails > 0)
        {
            Console.Error.WriteLine($"bindmath: FAILED ({fails} checks)");
            return 1;
        }
        Console.WriteLine("bindpose: 3/3 OK");
        Console.WriteLine("bindmath: 3/3 OK");
        return 0;
    }

    // ---- Unity-numeric factor replication (independent of BindMath internals) -------

    // UnityEngine.Matrix4x4.Translate(p) numerics: offset in column 4, row 4 = 0,0,0,1
    private static Matrix4x4 UTri(Vector3 t) => Matrix4x4.Transpose(Matrix4x4.CreateTranslation(t));

    // UnityEngine column-vector rotation matrix x' = R*x: numeric transpose of the SN
    // row-vector mirror CreateFromQuaternion (CreateFromQuaternion satisfies v*M rotation
    // as written, so its transpose is Unity's R)
    private static Matrix4x4 URot(Quaternion q) => Matrix4x4.Transpose(Matrix4x4.CreateFromQuaternion(q));

    // static Quaternion relabel Unity -> System.Numerics (both store xyzw)
    private static Quaternion Q(Vector3 axis, float rad) => Quaternion.CreateFromAxisAngle(axis, rad);

    private static void PlacedSection()
    {
        sectionFails = 0;
        foreach (var (pmx, _) in Bones)
        {
            var mapped = false;
            foreach (var (mapPmx, _) in BoneContract.BoneMap) mapped |= mapPmx == pmx;
            Check(mapped, $"placed: rig bone '{pmx}' is a BoneContract.BoneMap row");
        }
        Check(BoneContract.MirrorPmx("左腕") == "右腕", "placed: BoneContract.MirrorPmx 左->右");

        // BindMath factor shape pins (Unity numerics, independent hand formula)
        var t = UTri(new Vector3(1.2f, 0.5f, 3.3f));
        Check(t.M14 == 1.2f && t.M24 == 0.5f && t.M34 == 3.3f
              && t.M41 == 0f && t.M42 == 0f && t.M43 == 0f && t.M44 == 1f,
            "placed: unity translation shape - offset in M14/M24/M34, row 4 neutral");
        var qz = Q(Vector3.UnitZ, MathF.PI / 2f);
        var rz = URot(qz);
        CheckV(ColApply(rz, new Vector3(1f, 0f, 0f)), new Vector3(0f, 1f, 0f),
            "placed: unity rotation maps x-axis to y-axis (column-vector application)");
        Check(MathF.Abs(rz.M11 - MathF.Cos(MathF.PI / 2f)) <= Tol
              && MathF.Abs(rz.M12 + MathF.Sin(MathF.PI / 2f)) <= Tol
              && MathF.Abs(rz.M21 - MathF.Sin(MathF.PI / 2f)) <= Tol,
            "placed: unity rotation entries match the hand rotation matrix (cos,-sin,sin)");
        CheckV(BindMath.ApplyColumn(rz, new Vector3(1f, 0f, 0f)), ColApply(rz, new Vector3(1f, 0f, 0f)),
            "placed: BindMath.ApplyColumn == column-vector application replica");

        // invariant (a): R = identity => composed world of pmx vertex v == modelRootWorld * v
        var (_, p0) = Bones[0];
        var gb = Scale * p0 + RootT;             // game bone at the placed pivot itself
        var w2l = UTri(-gb);
                var compose = BindMath.ComposeRetarget(w2l, Root, gb, Quaternion.Identity);

        Check(MaxDiff(compose, StdMul(w2l, Root)) <= Tol,
            "placed: bindpose == boneWorldToLocal * modelRootWorld entries (no T(p) term)");
        var withP = StdMul(w2l, StdMul(Root, UTri(p0)));
        Check(MaxDiff(compose, withP) > 1e-4f,
            "placed: the old +p composition (bindSpace = root * Translate(p_bone)) differs - +p double-count gone");
        foreach (var (pmx, v) in Bones)
        {
            var chain = StdMul(UTri(gb), compose);   // [G.l2w x G.w2l] cancels at attach
            CheckV(ColApply(chain, v), Scale * v + RootT,
                $"placed: R=I composed world of pmx vertex {Fmt(v)} == modelRootWorld * v (bone {pmx})");
        }
        CheckV(ColApply(StdMul(UTri(gb), compose), Mid), Scale * Mid + RootT,
            "placed: arbitrary pmx vertex stays on the placed model");

        // R = identity makes the pivot argument neutral: T(p)*I*T(-p) == identity exactly
        var composeOtherPivot = BindMath.ComposeRetarget(w2l, Root, Scale * Bones[2].pos + RootT, Quaternion.Identity);
        Check(MaxDiff(compose, composeOtherPivot) <= Tol,
            "placed: pivot-neutral identity retarget (T(p)*I*T(-p) == I exactly)");

        EndSection("placed", "R=I world == placed model, factor shapes + old-+p delta pin, 1e-6");
    }

    private static void RetargetSection()
    {
        sectionFails = 0;
        // retarget input: the rotation that takes the placed pmx segment direction onto
        // the game hierarchy's direction (in production Unity's FromToRotation(dirP, dirG));
        // here q is supplied directly and the game side is synthesized from it, so the
        // assertions below never re-derive R through BindMath
        var q = Q(Vector3.UnitZ, MathF.PI / 2f);

        var pivot = Scale * Pb + RootT;                       // placedPivotW
        var parentPlaced = Scale * Pp + RootT;                // placed pmx parent point
        var dirP = pivot - parentPlaced;                      // placed pmx segment direction
        var dirG = Vector3.Transform(dirP, q);                // game segment direction
        Check(MathF.Abs(dirP.Length() - dirG.Length()) <= Tol,
            "retarget: input sanity - pure rotation preserves the segment length");
        var gb = pivot;                                       // near-bind: game anchor at the pivot
        var gp = gb - dirG;                                   // synthesized game parent (dirG = gb - gp)
        var w2l = UTri(-gb);
        var bindpose = BindMath.ComposeRetarget(w2l, Root, pivot, q);
        var chain = StdMul(UTri(gb), bindpose);               // [G.l2w x G.w2l] cancels

        // independent 5-factor standard-product replica of the composition
        var expected = StdMul(w2l,
            StdMul(StdMul(UTri(pivot), URot(q)),
                StdMul(UTri(-pivot), Root)));
        Check(MaxDiff(bindpose, expected) <= Tol,
            "retarget: bindpose == w2l * T(pivot) * R * T(-pivot) * root (replica, entries 1e-6)");

        CheckV(ColApply(chain, Pb), pivot,
            "retarget: the pivot's own joint stays fixed (world == placedPivotW)");
        var sample = Pb + (Pb - Pp);                          // one pmx segment out; placed = pivot + dirP
        CheckV(Scale * sample + RootT, pivot + dirP,
            "retarget: the sample's placed position is pivot + dirP (pmx direction sample point)");
        CheckV(ColApply(chain, sample), pivot + dirG,
            "retarget: the pmx direction ROTATES onto the game direction (world == pivot + dirG)");
        CheckV(ColApply(chain, Pp), pivot - dirG,
            "retarget: the parent joint swings to the mirrored segment end (pivot - dirG)");

        var v = new Vector3(2f, -1f, 4f);
        CheckV(ColApply(chain, v), pivot + Vector3.Transform(Scale * (v - Pb), q),
            "retarget: general conjugation world(v) = pivot + R(root(v) - pivot), arbitrary vertex");
        CheckV(ColApply(chain, Mid), pivot + Vector3.Transform(Scale * (Mid - Pb), q),
            "retarget: general conjugation world(v) = pivot + R(root(v) - pivot), mid vertex");

        // convention pin: on Unity-numeric matrices the row-vector application diverges
        var rowVsCol = BindMath.ApplyPoint(bindpose, sample) - ColApply(bindpose, sample);
        Check(rowVsCol.Length() > Tol,
            "retarget: row-vector vs column application differ on Unity numerics (ApplyColumn is the Unity one)");
        Console.WriteLine($"retarget: row-vs-column point delta = {Fmt(rowVsCol)}");

        EndSection("retarget", "pivot fixed + dirP rotates onto dirG + conjugation + replica, 1e-6");
    }

    private static void DeltaSection()
    {
        sectionFails = 0;
        var qy = Q(Vector3.UnitY, MathF.PI / 2f);
        var ry = URot(qy);

        // (1) R=identity slot: bone at its placed pivot, rotated +90deg Y in place
        {
            var (_, p0) = Bones[0];
            var gb = Scale * p0 + RootT;
            var bindpose = BindMath.ComposeRetarget(UTri(-gb), Root, gb, Quaternion.Identity);
            var l2wT = StdMul(UTri(gb), ry);                  // bone l2w after the runtime rotation
            var w2lT = InvertEntries(l2wT);
            Check(MaxDiff(StdMul(l2wT, w2lT), Identity()) <= Tol,
                "delta: the rotated bone's new worldToLocal is the inverse of its new localToWorld");

            var chainT = StdMul(l2wT, bindpose);
            var delta = StdMul(StdMul(UTri(gb), ry), UTri(-gb));
            Check(MaxDiff(chainT, StdMul(delta, Root)) <= Tol,
                "delta: world chain == (bone delta) * bindSpace - rigid motion about the game bone origin");
            foreach (var (pmx, v) in Bones)
            {
                var world0 = Scale * v + RootT;               // bind world = placed model (R=I slot)
                CheckV(ColApply(chainT, v), gb + Vector3.Transform(world0 - gb, qy),
                    $"delta: vertex rigid-transforms with the bone delta about the game bone origin (bone {pmx})");
            }
        }

        // (2) retarget slot: same runtime rotation on top of the pivot retarget
        {
            var q = Q(Vector3.UnitZ, MathF.PI / 2f);
            var pivot = Scale * Pb + RootT;
            var dirP = pivot - (Scale * Pp + RootT);
            var dirG = Vector3.Transform(dirP, q);
            var gb = pivot;
            var bindpose = BindMath.ComposeRetarget(UTri(-gb), Root, pivot, q);
            var l2wT = StdMul(UTri(gb), ry);
            var chainT = StdMul(l2wT, bindpose);
            var v = Pb + (Pb - Pp);                           // pmx direction sample point
            var world0 = pivot + dirG;                        // attached world (from the retarget section)
            CheckV(ColApply(chainT, v), gb + Vector3.Transform(world0 - gb, qy),
                "delta: retarget slot also rigid-transforms with the bone delta about the game bone origin");
        }

        EndSection("delta", "90deg Y runtime rotation: rigid delta about the game bone origin, 1e-6");
    }

    private static void EndSection(string name, string detail)
    {
        if (sectionFails == 0)
        {
            Console.WriteLine($"bindpose: {name} OK ({detail})");
        }
        else
        {
            Console.Error.WriteLine($"bindpose: {name} FAILED ({sectionFails} checks)");
        }
    }

    private static void Check(bool ok, string what)
    {
        if (ok) return;
        sectionFails++;
        fails++;
        Console.Error.WriteLine($"FAIL: {what}");
    }

    private static void CheckV(Vector3 actual, Vector3 expected, string what)
    {
        var ok = MathF.Abs(actual.X - expected.X) <= Tol
              && MathF.Abs(actual.Y - expected.Y) <= Tol
              && MathF.Abs(actual.Z - expected.Z) <= Tol;
        if (ok) return;
        sectionFails++;
        fails++;
        Console.Error.WriteLine($"FAIL: {what}: expected {Fmt(expected)} actual {Fmt(actual)} delta {Fmt(actual - expected)}");
    }

    private static string Fmt(Vector3 v) => "(" + string.Join(",", v.X.ToString("R"), v.Y.ToString("R"), v.Z.ToString("R")) + ")";

    // Independent replica: standard product over the stored mRC indices, as both
    // UnityEngine.Matrix4x4 and System.Numerics operator* are documented to compute.
    private static Matrix4x4 StdMul(Matrix4x4 a, Matrix4x4 b)
    {
        var av = ToArray(a);
        var bv = ToArray(b);
        var r = new float[16];
        for (int i = 0; i < 4; i++)
        {
            for (int j = 0; j < 4; j++)
            {
                float s = 0;
                for (int k = 0; k < 4; k++) s += av[i * 4 + k] * bv[k * 4 + j];
                r[i * 4 + j] = s;
            }
        }
        return FromArray(r);
    }

    // Column-vector application of a matrix's stored entries (unity MultiplyPoint3x4 shape).
    private static Vector3 ColApply(Matrix4x4 m, Vector3 p)
        => new(m.M11 * p.X + m.M12 * p.Y + m.M13 * p.Z + m.M14,
               m.M21 * p.X + m.M22 * p.Y + m.M23 * p.Z + m.M24,
               m.M31 * p.X + m.M32 * p.Y + m.M33 * p.Z + m.M34);

    private static Matrix4x4 InvertEntries(Matrix4x4 m)
    {
        Matrix4x4.Invert(m, out var inv);
        return inv;
    }

    private static Matrix4x4 Identity() => Matrix4x4.Identity;


    private static float[] ToArray(Matrix4x4 m)
        => new[] { m.M11, m.M12, m.M13, m.M14, m.M21, m.M22, m.M23, m.M24,
                   m.M31, m.M32, m.M33, m.M34, m.M41, m.M42, m.M43, m.M44 };

    private static Matrix4x4 FromArray(float[] r) => new(
        r[0], r[1], r[2], r[3], r[4], r[5], r[6], r[7],
        r[8], r[9], r[10], r[11], r[12], r[13], r[14], r[15]);

    private static float MaxDiff(Matrix4x4 a, Matrix4x4 b)
    {
        float d = 0;
        var av = ToArray(a);
        var bv = ToArray(b);
        for (int i = 0; i < 16; i++) d = MathF.Max(d, MathF.Abs(av[i] - bv[i]));
        return d;
    }
}
