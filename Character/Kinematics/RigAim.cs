using System;
using Snx = System.Numerics;

namespace DollFrame
{
    /// <summary>
    /// Pure retarget/aim math for the per-character rig driver (System.Numerics only,
    /// parser-stub-safe). The Unity driver (CharacterRig.Drive) delegates EVERY rotation
    /// decision here and relabels the 4/3 floats at the Transform boundary; both UnityEngine
    /// and System.Numerics quaternions are standard Hamilton products under column-vector
    /// application (a * b applies b first), so the relabel is an exact field copy - no
    /// duplicated formula anywhere: Tests/DollFrame.Verify's `zerostretch` subcommand drives the
    /// SAME RigAim over a simulated transform tree.
    /// Derivation (the pivot math, user-decreed). The character is an INDEPENDENT rig
    /// instance whose bone pivots never move (fixed pmx local offsets, fixed scales), so
    /// motion is rotation-only by construction - the game-bone reskin span-stretch this
    /// replaces is structurally impossible. Per mapped rig bone:
    ///   dC = unit attach-world direction to its contract child (first mapped pmx
    ///        descendant; leaf bones: the bone's own parent-to-bone direction),
    ///   dG = the same pairing's game-anchor direction THIS frame,
    ///   qAim = FromToRotation(dC, dG) - aim the character's child axis at the game axis,
    ///   targetWorld = qAim ∘ worldRot_attach  (aim composes ONTO the attach world rotation),
///   localRotation = (parentWorldRot_now)⁻¹ ∘ targetWorld  (applied parent-first over
///        the pmx bone order, which is parent-first).
/// Per frame Drive re-measures the pairing direction LIVE (TryLiveTarget) instead of
/// replaying the baked attach axis, so pairing spans that skip animated intermediate
/// game bones (Spine2 under spine1→spine3, Thigh2 under thigh1→calf, Forearm2/3 under
/// forearm1→palm) still track the true game axis; the roll keeps coming from the game
/// bone's own frame (TryBindLiveFrame), and on degenerate inputs the frame falls back
/// to the baked CopyTarget.
/// Root drive: position = attachPos + (gamePelvis.pos - attachPos); world yaw = attach
/// yaw rotated by the tracked game-root yaw delta (player facing). Leaves and unmapped
/// aux bones are children of mapped bones in the rig tree and inherit rigidly.
    /// </summary>
    public static class RigAim
    {
        /// <summary>Directions shorter than this are degenerate aim inputs (fail closed: keep current rotation).</summary>
        internal const float DirEps = 1e-6f;

        /// <summary>
        /// Unity Quaternion.FromToRotation semantics: the rotation taking unit(from) onto
        /// unit(to) about their cross axis. Parallel inputs return identity; antiparallel
        /// inputs rotate 180 deg about the world axis least aligned with `from`
        /// (deterministic; Unity leaves the axis unspecified in this degenerate case).
        /// </summary>
        internal static Snx.Quaternion FromTo(Snx.Vector3 from, Snx.Vector3 to)
        {
            float fl = from.Length(), tl = to.Length();
            if (fl < DirEps || tl < DirEps) return Snx.Quaternion.Identity;
            var f = from / fl;
            var t = to / tl;
            var axis = Snx.Vector3.Cross(f, t);
            float axisLen = axis.Length();
            float dot = Snx.Vector3.Dot(f, t);
            if (axisLen < DirEps)
            {
                if (dot > 0f) return Snx.Quaternion.Identity;
                return Snx.Quaternion.CreateFromAxisAngle(Perpendicular(f), MathF.PI);
            }
            float angle = MathF.Acos(Math.Clamp(dot / (1f), -1f, 1f));
            return Snx.Quaternion.CreateFromAxisAngle(axis / axisLen, angle);
        }

        /// <summary>Deterministic perpendicular unit vector: cross(v, world axis least aligned with v).</summary>
        internal static Snx.Vector3 Perpendicular(Snx.Vector3 v)
        {
            float ax = MathF.Abs(v.X), ay = MathF.Abs(v.Y), az = MathF.Abs(v.Z);
            var world = ax <= ay && ax <= az ? Snx.Vector3.UnitX : (ay <= az ? Snx.Vector3.UnitY : Snx.Vector3.UnitZ);
            var cross = Snx.Vector3.Cross(v, world);
            float len = cross.Length();
            return len < DirEps ? Snx.Vector3.UnitY : cross / len;
        }

        /// <summary>
        /// Parent-first retarget of one rig bone: targetWorld = FromTo(dC, dG) ∘ attach.
        /// Returns false (identity target, caller keeps the current rotation) when either
        /// direction is degenerate - a zero-length game direction means the pair's game
        /// anchors coincide this frame and no aim is defined.
        /// </summary>
        internal static bool TryAim(Snx.Quaternion worldRotAttach, Snx.Vector3 dC, Snx.Vector3 dG, out Snx.Quaternion targetWorld)
        {
            if (dC.LengthSquared() < DirEps * DirEps || dG.LengthSquared() < DirEps * DirEps)
            {
                targetWorld = Snx.Quaternion.Identity;
                return false;
            }
            targetWorld = FromTo(dC, dG) * worldRotAttach;
            return true;
        }

        /// <summary>Parent-first local composition: localRotation = parentWorldNow⁻¹ ∘ targetWorld.</summary>
        internal static Snx.Quaternion LocalFromWorld(Snx.Quaternion parentWorldNow, Snx.Quaternion targetWorld)
            => Snx.Quaternion.Inverse(parentWorldNow) * targetWorld;

        /// <summary>
        /// Rest offset of the rotation-copy driver, extended with the LIVE two-vector roll
        /// channels (this fixes the baked-copy direction error on pairing spans that skip
        /// animated intermediate game bones: spine1→spine3 skips Spine2, thigh1→calf skips
        /// Thigh2, forearm1→palm skips Forearm2/3 - the baked copy then misses exactly the
        /// skipped bone's relative motion, which showed as the 腰部扭轉 + X-stance knees):
        ///   R_off = G0⁻¹ ∘ FromTo(dC, dG0) ∘ C0,
        /// G0 = game bone world rotation at attach, dG0 = its pairing direction at attach,
        /// C0 = rig bone world rotation at attach, dC = rig pairing direction. Also outputs
        ///   dCp = the deterministic perpendicular of dC (rig attach-frame roll reference),
        ///   vSec = Inverse(G0) ∘ FromTo(dC, dG0) ∘ dCp - the same roll reference expressed
        /// in the GAME bone's own local frame (rigid in it), so per frame the game bone's
        /// frame image of vSec reconstructs the attach aim exactly (TryLiveTarget).
        /// Per frame the baked target is G_now ∘ R_off: at attach it equals the aim result
        /// and the rig bone carries the game bone's full rotation delta (direction AND
        /// roll); TryLiveTarget instead re-measures the pairing direction live and adds the
        /// game roll, both constraints satisfied exactly - identical to this baked target
        /// whenever the pairing axis is rigid in the game bone, and correct above it.
        /// False when either direction is degenerate.
        /// </summary>
        internal static bool TryBindLiveFrame(Snx.Quaternion g0, Snx.Quaternion c0, Snx.Vector3 dC, Snx.Vector3 dG0,
            out Snx.Quaternion rOff, out Snx.Vector3 dCp, out Snx.Vector3 vSec)
        {
            dCp = Snx.Vector3.Zero;
            vSec = Snx.Vector3.Zero;
            if (!TryAim(c0, dC, dG0, out var aimed)) { rOff = Snx.Quaternion.Identity; return false; }
            rOff = Snx.Quaternion.Normalize(Snx.Quaternion.Inverse(g0) * aimed);
            dCp = Perpendicular(dC);
            // aimed = FromTo(dC, dG0) ∘ C0, so aimed applied to Inverse(c0) ∘ dCp is
            // exactly FromTo(dC, dG0) ∘ dCp (the swing alone); pull it into game-local.
            var rollRefLocal = Snx.Vector3.Transform(Snx.Vector3.Transform(dCp, Snx.Quaternion.Inverse(c0)), aimed);
            vSec = Snx.Vector3.Normalize(Snx.Vector3.Transform(rollRefLocal, Snx.Quaternion.Inverse(g0)));
            return true;
        }

        /// <summary>Roll-reference quaternion-vector sandwich: rotates a direction by q.</summary>
        private static Snx.Vector3 RotateDir(Snx.Quaternion q, Snx.Vector3 v)
            => Snx.Vector3.Transform(v, q);

        /// <summary>Per-frame rotation-copy target: G_now ∘ R_off.</summary>
        internal static Snx.Quaternion CopyTarget(Snx.Quaternion gNow, Snx.Quaternion rOff)
            => Snx.Quaternion.Normalize(gNow * rOff);

        /// <summary>
        /// Per-frame LIVE two-vector target (the production path): constraints
        ///   worldTarget ∘ Inverse(C0) maps dC -> the LIVE pairing direction dGNow
        ///   (mate − anchor positions this frame, so any intermediate-bone motion between
        ///   the anchor and its mate is absorbed) and dCp -> the game bone's in-frame roll
        /// image of vSec, orthogonalized against dGNow. Composed in three exact steps:
        ///   S = FromTo(dC, dGNow) (swing at the constraint), then the residual roll
        ///   FromTo(S ∘ dCp, dGp) - both vectors ⊥ dGNow, so it is a pure roll around the
        /// axis - and finally targetWorld = S ∘ C0. At attach this reproduces the baked
        /// aim exactly (the roll step degenerates to identity); with a rigid pairing axis
        /// it equals the baked copy (proved by zerostretch sim); with a skipped animated
        /// intermediate it tracks what the baked copy cannot see.
        /// False (caller falls back to the baked copy target) on degenerate inputs or when
        /// the roll image collapses onto the live axis (&lt; LiveEps - position noise).
        /// </summary>
        internal static bool TryLiveTarget(Snx.Vector3 dC, Snx.Vector3 dCp, Snx.Vector3 vSec,
            Snx.Quaternion c0, Snx.Quaternion gNow, Snx.Vector3 dGNow, out Snx.Quaternion targetWorld)
        {
            targetWorld = Snx.Quaternion.Identity;
            if (dCp.LengthSquared() < DirEps * DirEps || vSec.LengthSquared() < DirEps * DirEps)
                return false;
            if (dGNow.LengthSquared() < DirEps * DirEps) return false;
            var dG = Snx.Vector3.Normalize(dGNow);
            var gameSec = RotateDir(gNow, vSec);
            var proj = gameSec - dG * Snx.Vector3.Dot(gameSec, dG);
            if (proj.LengthSquared() < LiveEps * LiveEps) return false;
            var dGp = Snx.Vector3.Normalize(proj);
            var swing = FromTo(dC, dGNow);
            var swung = RotateDir(swing, dCp);
            swing = FromTo(swung, dGp) * swing;
            targetWorld = swing * c0;
            return true;
        }

        /// <summary>Roll reference collapse threshold: below this the projection is position noise.</summary>
        internal const float LiveEps = 1e-4f;

        /// <summary>
        /// MMD rotate-append: weight × the append parent's local rotation (rest = identity
        /// in this rig), composed onto the bone's own local rotation. Weight 1 copies,
        /// negative weights invert (腰キャンセル uses -1).
        /// </summary>
        internal static Snx.Quaternion Grant(Snx.Quaternion own, Snx.Quaternion parentLocal, float weight)
        {
            var q = parentLocal;
            if (weight < 0f) { q = Snx.Quaternion.Inverse(q); weight = -weight; }
            var g = MathF.Abs(weight - 1f) < 1e-6f ? q : Snx.Quaternion.Slerp(Snx.Quaternion.Identity, q, weight);
            return Snx.Quaternion.Normalize(g * own);
        }

    }
}
