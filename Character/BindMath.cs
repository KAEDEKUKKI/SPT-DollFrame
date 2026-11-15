using System.Numerics;

namespace DollFrame
{
    /// <summary>
    /// Pure bind-pose math on System.Numerics only (no UnityEngine, parser/build
    /// stub-safe): the exact composition CharacterRig.BuildBindPoses drives per skin bone,
    ///   bindpose = boneWorldToLocal(attach) * (T(p) * R * T(-p) * modelRootWorld),
    /// kept here so the formula is not duplicated; consumers relabel the 16 numeric
    /// floats Unity-side only at the boundary (both operator* are the standard product
    /// on the same indices, so elementwise relabel + same-order multiply is exact).
    ///
    /// Derivation (v3, stretch fix). PMX vertices are ABSOLUTE pmx-space points. The old
    /// composition folded the bone position into the bind space,
    ///   bindSpace = modelRootWorld * Translate(p_bone),
    /// so at attach time the [G.l2w x G.w2l] chain reduced to
    ///   world(v, t0) = root(T(p_bone)(v)) = root(v) + S * p_bone,
    /// dislocating every vertex by its own bone's placed pmx position - a per-bone
    /// offset, i.e. the reported stretch. The translation term is dropped: with
    /// R = identity the bind space is modelRootWorld and world(v, t0) = root(v) (the
    /// placed model, the corrected rest identity). What R then supplies is the per-bone
    /// ORIENTATION retarget: a rotation about the PLACED pivot p = root * p_bone,
    ///   bindSpace = T(p) * R * T(-p) * modelRootWorld
    ///   => world(v, t0) = p + R * (modelRootWorld(v) - p),
    /// with R = FromToRotation(dirP, dirG) pre-aligning each directly-mapped pmx segment
    /// direction onto the game hierarchy's segment direction measured at attach (PMX vs
    /// EFT measured mismatches: legs 47-90deg, spine 88-177deg, arms 144deg - humanoid
    /// rotations would otherwise deform the mesh along foreign axes). The Unity idiom for
    /// the same map is TRS(placedPivot, R, one) * Translate(-placedPivot) * modelRootWorld;
    /// a bare TRS(placedPivot, R, one) * modelRootWorld would add a spurious R*p and break
    /// the R = identity case, hence the explicit conjugation.
    /// At runtime the attach-time cancellation no longer holds once the bone moves:
    ///   world(v, t) = G.l2w(t) * G.w2l(t0) * bindSpace * v,
    /// i.e. the vertex is dragged by the game bone's world delta from attach, along its
    /// authored arc because the segments were pre-aligned at bind.
    ///
    /// Numeric convention: CharacterRig hands in boneWorldToLocal/modelRootWorld already in
    /// Unity numerics (elementwise relabel; Unity stores the translation in column 4
    /// while SN CreateTranslation stores it in row 4), so the pivot/rotation factors are
    /// built in Unity numerics too - T and R are the numeric transposes of the SN
    /// row-vector mirrors CreateTranslation/CreateFromQuaternion (shape-pinned by the
    /// offline suite: offset in M14/M24/M34, cos/-sin/sin rotation row). ApplyColumn is
    /// the Unity MultiplyPoint3x4 semantics x' = M*x for these matrices; ApplyPoint stays
    /// as the row-vector application for the SN-mirror convention checks.
    /// </summary>
    internal static class BindMath
    {
        /// <summary>
        /// Composes one skinned bind pose v3: boneWorldToLocal * (T(placedPivot) * R *
        /// T(-placedPivot) * modelRootWorld) with the retarget rotation R taken about the
        /// placed pivot; R = identity reduces the bind space to modelRootWorld (the
        /// corrected base BP, the +p double-count gone). All factors Unity numerics.
        /// </summary>
        internal static Matrix4x4 ComposeRetarget(Matrix4x4 boneWorldToLocal, Matrix4x4 modelRootWorld, Vector3 placedPivot, Quaternion retarget)
        {
            var bindSpace = UnityTranslation(placedPivot) * UnityRotation(retarget) * UnityTranslation(-placedPivot)
                * modelRootWorld;
            return boneWorldToLocal * bindSpace;
        }

        // Unity-convention numerics from the SN row-vector primitives: the numeric
        // transpose flips SN's row-4 translation into Unity's column-4 offset and SN's
        // row-vector rotation into Unity's x' = R*x rotation matrix.
        private static Matrix4x4 UnityTranslation(Vector3 t) => Matrix4x4.Transpose(Matrix4x4.CreateTranslation(t));

        private static Matrix4x4 UnityRotation(Quaternion q) => Matrix4x4.Transpose(Matrix4x4.CreateFromQuaternion(q));

        /// <summary>Applies an SN row-vector-convention matrix to a point and truncates the homogeneous result to xyz (mirror-side checks only).</summary>
        internal static Vector3 ApplyPoint(Matrix4x4 m, Vector3 p)
        {
            var r = Vector4.Transform(new Vector4(p, 1f), m);
            return new Vector3(r.X, r.Y, r.Z);
        }

        /// <summary>Unity MultiplyPoint3x4 semantics for Unity-numeric matrices: x' = M*x, result_i = sum_j M[i,j]*x_j + M[i,4].</summary>
        internal static Vector3 ApplyColumn(Matrix4x4 m, Vector3 p)
            => new(
                m.M11 * p.X + m.M12 * p.Y + m.M13 * p.Z + m.M14,
                m.M21 * p.X + m.M22 * p.Y + m.M23 * p.Z + m.M24,
                m.M31 * p.X + m.M32 * p.Y + m.M33 * p.Z + m.M34);
    }
}
