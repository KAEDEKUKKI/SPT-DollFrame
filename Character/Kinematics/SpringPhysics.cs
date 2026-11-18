// Character/Kinematics/SpringPhysics.cs: 角色次要物理引擎（頭髮、裙擺、絲帶/衣襬、胸部）。
// 雙編譯設計：純數學層 SpringPhysicsMath 使用 System.Numerics（無 Unity 相依），供 DollFrameVerify 離線驗證；
// Unity 執行層 SpringPhysicsRig 包裝於 #if !DOLLFRAME_PMX_PARSER_ONLY。
// 硬性不變量：
// 1. 零拉伸（Zero-Stretch）：每步強制投影至半徑 restLen 球面，僅寫骨節點 localRotation，零 localPosition/localScale 寫入。
// 2. 幀率無關＋防雙步：dt 限幅 <= 1/30s 並細分步進；同一 Time.frameCount（如 LateUpdate 與 OnBeforeRender）僅步進一次，重繪期僅重套已解 localRotation。
// 3. 身體膠囊碰撞：頭、頸、胸腔、腰腹、骨盆、雙臂、雙腿膠囊隨動畫每幀更新，防止頭髮與裙擺穿模。
using System;
using System.Collections.Generic;
using Snx = System.Numerics;

namespace DollFrame
{
    public enum SpringCategory
    {
        Hair = 0,
        Skirt = 1,
        Accessory = 2,
        Breast = 3,
    }

    public struct SpringParams
    {
        public float Stiffness;     // 回復剛性 (1/s^2 量級)
        public float Drag;          // 空氣阻尼係數 (1/s)
        public float GravityScale;  // 重力跟隨倍率
        public float MaxAngleDeg;   // 相對靜止軸最大偏轉角（度），防止暴力翻轉穿模
        public float Radius;        // 粒子碰撞半徑 (m)

        public static SpringParams ForCategory(SpringCategory cat)
        {
            switch (cat)
            {
                case SpringCategory.Skirt:
                    return new SpringParams
                    {
                        Stiffness = 48f,
                        Drag = 14.5f,
                        GravityScale = 0.60f,
                        MaxAngleDeg = 50f,
                        Radius = 0.015f,
                    };
                case SpringCategory.Accessory:
                    return new SpringParams
                    {
                        Stiffness = 58f,
                        Drag = 16.0f,
                        GravityScale = 0.45f,
                        MaxAngleDeg = 22f,
                        Radius = 0.018f,
                    };
                case SpringCategory.Breast:
                    return new SpringParams
                    {
                        Stiffness = 115f,
                        Drag = 18.5f,
                        GravityScale = 0.10f,
                        MaxAngleDeg = 9f,
                        Radius = 0.012f,
                    };
                case SpringCategory.Hair:
                default:
                    return new SpringParams
                    {
                        Stiffness = 78f,
                        Drag = 19.0f,
                        GravityScale = 0.30f,
                        MaxAngleDeg = 14f,
                        Radius = 0.016f,
                    };
            }
        }

        public static SpringParams ForFrontHair()
        {
            return new SpringParams
            {
                Stiffness = 140f,
                Drag = 26.0f,
                GravityScale = 0.08f,
                MaxAngleDeg = 4f,
                Radius = 0.016f,
            };
        }

        public static SpringCategory ParseCategory(string cat)
        {
            if (string.Equals(cat, "Skirt", StringComparison.OrdinalIgnoreCase)) return SpringCategory.Skirt;
            if (string.Equals(cat, "Breast", StringComparison.OrdinalIgnoreCase)) return SpringCategory.Breast;
            if (string.Equals(cat, "Accessory", StringComparison.OrdinalIgnoreCase)) return SpringCategory.Accessory;
            return SpringCategory.Hair;
        }
    }

    public struct BodyCapsule
    {
        public Snx.Vector3 A;
        public Snx.Vector3 B;
        public float Radius;

        public BodyCapsule(Snx.Vector3 a, Snx.Vector3 b, float radius)
        {
            A = a;
            B = b;
            Radius = radius;
        }
    }

    public struct SpringNodeState
    {
        public Snx.Vector3 TipPos;
        public Snx.Vector3 PrevPivotPos;
        public Snx.Vector3 Velocity;
        public float[] ColliderRadii;    // 各膠囊之靜止貼合有效半徑（保留核心最小半徑，不完全歸零）
        public bool Initialized;
    }

    /// <summary>
    /// 純數學次要物理求解器（雙編譯權威）。
    /// </summary>
    public static class SpringPhysicsMath
    {
        public static readonly Snx.Vector3 DefaultGravity = new Snx.Vector3(0f, -9.81f, 0f);
        private const float MaxDt = 1f / 30f;
        private const float SubstepThreshold = 1f / 75f;
        private const float MaxSpeed = 3.0f;
        private const float TeleportJumpDistSq = 0.45f * 0.45f;

        /// <summary>將世界空間點 p 推出膠囊體外（含粒子半徑）。</summary>
        public static Snx.Vector3 PushOutCapsule(Snx.Vector3 p, float particleRadius, in BodyCapsule cap)
        {
            var ab = cap.B - cap.A;
            float abLenSq = ab.LengthSquared();
            float t = abLenSq > 1e-8f
                ? Math.Clamp(Snx.Vector3.Dot(p - cap.A, ab) / abLenSq, 0f, 1f)
                : 0f;
            var closest = cap.A + ab * t;
            var diff = p - closest;
            float minDist = cap.Radius + particleRadius;
            float distSq = diff.LengthSquared();
            if (distSq >= minDist * minDist) return p;

            if (distSq > 1e-10f)
            {
                float dist = MathF.Sqrt(distSq);
                return closest + (diff / dist) * minDist;
            }

            // 退化重合：沿水平/向上方向推開
            var fallback = abLenSq > 1e-8f ? RigAim.Perpendicular(Snx.Vector3.Normalize(ab)) : Snx.Vector3.UnitZ;
            return closest + fallback * minDist;
        }

        /// <summary>將單位方向 dir 限制在以 restDir 為中心軸、半頂角 maxAngleDeg 圓錐內。</summary>
        public static Snx.Vector3 ClampCone(Snx.Vector3 dir, Snx.Vector3 restDir, float maxAngleDeg)
        {
            float maxRad = Math.Clamp(maxAngleDeg, 1f, 175f) * (MathF.PI / 180f);
            float cosMax = MathF.Cos(maxRad);
            float dot = Math.Clamp(Snx.Vector3.Dot(dir, restDir), -1f, 1f);
            if (dot >= cosMax) return dir;

            var ortho = dir - restDir * dot;
            float orthoLenSq = ortho.LengthSquared();
            if (orthoLenSq < 1e-10f)
            {
                ortho = RigAim.Perpendicular(restDir);
            }
            else
            {
                ortho /= MathF.Sqrt(orthoLenSq);
            }

            float sinMax = MathF.Sqrt(Math.Max(0f, 1f - cosMax * cosMax));
            return Snx.Vector3.Normalize(restDir * cosMax + ortho * sinMax);
        }

        /// <summary>求使單位向量 from 轉至單位向量 to 的最短旋轉四元數。</summary>
        public static Snx.Quaternion FromTo(Snx.Vector3 from, Snx.Vector3 to)
        {
            float dot = Math.Clamp(Snx.Vector3.Dot(from, to), -1f, 1f);
            if (dot > 0.999999f) return Snx.Quaternion.Identity;
            if (dot < -0.999999f)
            {
                var axis = RigAim.Perpendicular(from);
                return Snx.Quaternion.CreateFromAxisAngle(axis, MathF.PI);
            }
            var c = Snx.Vector3.Cross(from, to);
            var q = new Snx.Quaternion(c.X, c.Y, c.Z, 1f + dot);
            return Snx.Quaternion.Normalize(q);
        }

        /// <summary>求兩線段 [p0,p1] 與 [q0,q1] 之最近點 c1, c2 與線段參數 s, t ∈ [0,1]。</summary>
        private static void ClosestPointsSegmentSegment(
            Snx.Vector3 p0, Snx.Vector3 p1,
            Snx.Vector3 q0, Snx.Vector3 q1,
            out float s, out float t, out Snx.Vector3 c1, out Snx.Vector3 c2)
        {
            var d1 = p1 - p0;
            var d2 = q1 - q0;
            var r = p0 - q0;
            float a = d1.LengthSquared();
            float e = d2.LengthSquared();
            float f = Snx.Vector3.Dot(d2, r);

            if (a <= 1e-10f && e <= 1e-10f)
            {
                s = 0f;
                t = 0f;
                c1 = p0;
                c2 = q0;
                return;
            }
            if (a <= 1e-10f)
            {
                s = 0f;
                t = Math.Clamp(f / e, 0f, 1f);
            }
            else
            {
                float c = Snx.Vector3.Dot(d1, r);
                if (e <= 1e-10f)
                {
                    t = 0f;
                    s = Math.Clamp(-c / a, 0f, 1f);
                }
                else
                {
                    float b = Snx.Vector3.Dot(d1, d2);
                    float denom = a * e - b * b;
                    s = denom > 1e-10f ? Math.Clamp((b * f - c * e) / denom, 0f, 1f) : 0f;
                    t = (b * s + f) / e;
                    if (t < 0f)
                    {
                        t = 0f;
                        s = Math.Clamp(-c / a, 0f, 1f);
                    }
                    else if (t > 1f)
                    {
                        t = 1f;
                        s = Math.Clamp((b - c) / a, 0f, 1f);
                    }
                }
            }

            c1 = p0 + d1 * s;
            c2 = q0 + d2 * t;
        }

        private static float SegmentCapsuleDist(Snx.Vector3 pivotPos, Snx.Vector3 tipPos, in BodyCapsule cap)
        {
            // 從 0.2 倍骨長起算，避免緊貼父關節根部造成誤判
            var segStart = pivotPos + (tipPos - pivotPos) * 0.2f;
            ClosestPointsSegmentSegment(segStart, tipPos, cap.A, cap.B, out _, out _, out var c1, out var c2);
            return (c1 - c2).Length();
        }

        /// <summary>
        /// 單一物理骨節點步進並回傳本幀世界目標旋轉（relative to rest pose）。
        /// boneRestLocalRot 為骨靜止局部旋轉（PMX Rig 通常為 Identity）。
        /// </summary>
        public static Snx.Quaternion StepNode(
            ref SpringNodeState state,
            Snx.Vector3 pivotPos,
            Snx.Quaternion parentWorldRot,
            Snx.Quaternion boneRestLocalRot,
            Snx.Vector3 restDirLocal,
            float restLen,
            float dt,
            in SpringParams p,
            BodyCapsule[] colliders,
            int colliderCount,
            bool hasBackPlane = false,
            Snx.Vector3 backPlaneOrigin = default,
            Snx.Vector3 backPlaneNormal = default,
            float minColliderRatio = 0f,
            bool hasOutwardLimit = false,
            Snx.Vector3 outwardDir = default,
            float maxInwardDeg = 8f,
            float minWorldY = float.NegativeInfinity,
            uint colliderMask = uint.MaxValue)
        {
            _ = minColliderRatio; // 保留簽章相容
            var restWorldRot = Snx.Quaternion.Normalize(parentWorldRot * boneRestLocalRot);
            var restDirWorld = Snx.Vector3.Normalize(Snx.Vector3.Transform(restDirLocal, restWorldRot));
            var targetPos = pivotPos + restDirWorld * restLen;

            // 初始化或瞬移保護（進入戰局／傳送／切角色時歸位，並依靜止姿態對線段校準有效半徑）
            if (!state.Initialized || (pivotPos - state.PrevPivotPos).LengthSquared() > TeleportJumpDistSq)
            {
                state.TipPos = targetPos;
                state.PrevPivotPos = pivotPos;
                state.Velocity = Snx.Vector3.Zero;
                if (colliders != null && colliderCount > 0)
                {
                    if (state.ColliderRadii == null || state.ColliderRadii.Length < colliderCount)
                        state.ColliderRadii = new float[colliderCount];
                    for (int c = 0; c < colliderCount; c++)
                    {
                        if (c < 32 && (colliderMask & (1u << c)) == 0)
                        {
                            state.ColliderRadii[c] = 0f;
                            continue;
                        }
                        ref readonly var cap = ref colliders[c];
                        if (cap.Radius <= 1e-5f)
                        {
                            state.ColliderRadii[c] = 0f;
                            continue;
                        }
                        float segDist0 = SegmentCapsuleDist(pivotPos, targetPos, in cap);
                        float maxR = segDist0 - p.Radius - 0.0015f;
                        // 靜止重疊區不完全歸零，保留至少 4mm 核心半徑以防大動作穿過骨骼軸心
                        state.ColliderRadii[c] = Math.Clamp(maxR, Math.Min(cap.Radius, 0.004f), cap.Radius);
                    }
                }
                state.Initialized = true;
                return boneRestLocalRot;
            }

            float clampedDt = Math.Clamp(dt, 0f, MaxDt);
            if (clampedDt > 1e-6f)
            {
                // 急轉身／蹲趴切換慣性限幅：單幀樞紐位移過大時跟進部分位移，避免布料猛甩穿模
                var pivotDelta = pivotPos - state.PrevPivotPos;
                float pivotDistSq = pivotDelta.LengthSquared();
                if (pivotDistSq > 0.06f * 0.06f)
                {
                    float pivotDist = MathF.Sqrt(pivotDistSq);
                    float excessRatio = (1f - 0.06f / pivotDist) * 0.65f;
                    state.TipPos += pivotDelta * excessRatio;
                }

                bool isNearlyIdle = pivotDistSq < 1e-6f && state.Velocity.LengthSquared() < 1e-4f;
                int steps = (!isNearlyIdle && clampedDt > SubstepThreshold) ? 2 : 1;
                float subDt = clampedDt / steps;
                float damp = MathF.Exp(-p.Drag * subDt);
                var grav = DefaultGravity * p.GravityScale;

                for (int s = 0; s < steps; s++)
                {
                    var prevTip = state.TipPos;
                    var springAccel = (targetPos - prevTip) * p.Stiffness;
                    var vel = (state.Velocity + (springAccel + grav) * subDt) * damp;

                    float speedSq = vel.LengthSquared();
                    if (speedSq > MaxSpeed * MaxSpeed)
                        vel *= MaxSpeed / MathF.Sqrt(speedSq);

                    var nextPos = prevTip + vel * subDt;

                    // 1) 硬距離約束 + 2) 非對稱圓錐角度限幅（向外自由擺動，向身體內側緊縮防穿模）
                    var d1 = nextPos - pivotPos;
                    float d1Len = d1.Length();
                    var dir1 = d1Len > 1e-6f ? (d1 / d1Len) : restDirWorld;
                    float maxDeg = p.MaxAngleDeg;
                    if (hasOutwardLimit)
                    {
                        var delta = dir1 - restDirWorld;
                        float deltaSq = delta.LengthSquared();
                        if (deltaSq > 1e-8f)
                        {
                            float inwardFrac = Math.Clamp(Snx.Vector3.Dot(delta / MathF.Sqrt(deltaSq), -outwardDir), 0f, 1f);
                            maxDeg = p.MaxAngleDeg + (maxInwardDeg - p.MaxAngleDeg) * inwardFrac;
                        }
                    }
                    dir1 = ClampCone(dir1, restDirWorld, maxDeg);
                    nextPos = pivotPos + dir1 * restLen;

                    // 3) 碰撞推離（線段對膠囊 + 額狀面 + 地面）↔ 硬距離投影交替 2 輪
                    bool hitCollider = ProjectConstraints(
                        pivotPos, restDirWorld, restLen, p.Radius,
                        colliders, colliderCount, state.ColliderRadii,
                        hasBackPlane, backPlaneOrigin, backPlaneNormal, minWorldY,
                        ref nextPos, out var pushNormalAccum, colliderMask);

                    // 4) 僅消除朝膠囊內部的法向速度，保留沿表面的切向滑動（貼合身體滑動而不反彈凸起）
                    if (hitCollider)
                    {
                        float nLenSq = pushNormalAccum.LengthSquared();
                        if (nLenSq > 1e-10f)
                        {
                            var n = pushNormalAccum / MathF.Sqrt(nLenSq);
                            float vn = Snx.Vector3.Dot(vel, n);
                            if (vn < 0f) vel -= n * vn;
                        }
                        vel *= 0.85f;
                    }
                    state.TipPos = nextPos;
                    state.Velocity = vel;
                }
            }

            state.PrevPivotPos = pivotPos;
            return SolveLocalRotation(restDirWorld, restWorldRot, parentWorldRot, boneRestLocalRot, pivotPos, state.TipPos);
        }

        /// <summary>
        /// 對位置執行「線段膠囊碰撞 ＋ 後髮額狀面 ＋ 地面夾緊 ↔ 硬距離投影」2 輪交替約束。
        /// 同時供 StepNode 與裙擺相鄰平滑後處理共用。
        /// </summary>
        internal static bool ProjectConstraints(
            Snx.Vector3 pivotPos,
            Snx.Vector3 restDirWorld,
            float restLen,
            float particleRadius,
            BodyCapsule[] colliders,
            int colliderCount,
            float[] colliderRadii,
            bool hasBackPlane,
            Snx.Vector3 backPlaneOrigin,
            Snx.Vector3 backPlaneNormal,
            float minWorldY,
            ref Snx.Vector3 nextPos,
            out Snx.Vector3 pushNormalAccum,
            uint colliderMask = uint.MaxValue)
        {
            bool hitCollider = false;
            pushNormalAccum = Snx.Vector3.Zero;

            for (int iter = 0; iter < 2; iter++)
            {
                if (colliders != null && colliderCount > 0)
                {
                    for (int c = 0; c < colliderCount; c++)
                    {
                        if (c < 32 && (colliderMask & (1u << c)) == 0) continue;
                        var cap = colliders[c];
                        float effR = (colliderRadii != null && c < colliderRadii.Length)
                            ? Math.Min(cap.Radius, colliderRadii[c])
                            : cap.Radius;
                        if (effR > 1e-4f)
                        {
                            if (PushSegmentCapsule(pivotPos, ref nextPos, particleRadius, cap.A, cap.B, effR, ref pushNormalAccum))
                                hitCollider = true;
                        }
                    }
                }

                // 後髮額狀面防護：防止後腦勺長髮翻越肩膀穿到胸前或臉上
                if (hasBackPlane)
                {
                    float fwdDot = Snx.Vector3.Dot(nextPos - backPlaneOrigin, backPlaneNormal);
                    if (fwdDot > 0f)
                    {
                        nextPos -= backPlaneNormal * fwdDot;
                        pushNormalAccum -= backPlaneNormal;
                        hitCollider = true;
                    }
                }

                // 地面高度防護：防止蹲姿／趴下時長髮與長裙沉入地面
                if (!float.IsNegativeInfinity(minWorldY) && nextPos.Y < minWorldY)
                {
                    nextPos.Y = minWorldY;
                    pushNormalAccum += Snx.Vector3.UnitY;
                    hitCollider = true;
                }

                // 硬距離投影（嚴格零拉伸）
                var d = nextPos - pivotPos;
                float dLen = d.Length();
                var dir = dLen > 1e-6f ? (d / dLen) : restDirWorld;
                nextPos = pivotPos + dir * restLen;
            }

            return hitCollider;
        }

        public static Snx.Quaternion SolveLocalRotation(
            Snx.Vector3 restDirWorld, Snx.Quaternion restWorldRot, Snx.Quaternion parentWorldRot,
            Snx.Quaternion boneRestLocalRot, Snx.Vector3 pivotPos, Snx.Vector3 tipPos)
        {
            var solvedDir = tipPos - pivotPos;
            float solvedLen = solvedDir.Length();
            if (solvedLen < 1e-6f) return boneRestLocalRot;
            solvedDir /= solvedLen;

            var deltaWorld = FromTo(restDirWorld, solvedDir);
            var targetWorldRot = Snx.Quaternion.Normalize(deltaWorld * restWorldRot);
            return RigAim.LocalFromWorld(parentWorldRot, targetWorldRot);
        }

        private static bool PushSegmentCapsule(
            Snx.Vector3 pivotPos, ref Snx.Vector3 nextPos, float particleRadius,
            Snx.Vector3 capA, Snx.Vector3 capB, float capRadius,
            ref Snx.Vector3 pushNormalAccum)
        {
            var segStart = pivotPos + (nextPos - pivotPos) * 0.2f;
            ClosestPointsSegmentSegment(segStart, nextPos, capA, capB, out float s, out _, out var c1, out var c2);

            float minDist = capRadius + particleRadius;
            var diff = c1 - c2;
            float distSq = diff.LengthSquared();
            if (distSq >= minDist * minDist) return false;

            Snx.Vector3 pushDir;
            float pen;
            if (distSq > 1e-10f)
            {
                float dist = MathF.Sqrt(distSq);
                pushDir = diff / dist;
                pen = minDist - dist;
            }
            else
            {
                var ab = capB - capA;
                pushDir = ab.LengthSquared() > 1e-8f
                    ? RigAim.Perpendicular(Snx.Vector3.Normalize(ab))
                    : Snx.Vector3.UnitZ;
                pen = minDist;
            }

            // 依接觸點在線段上的比例放大末端推離量（上限 2x），確保整段骨桿離開膠囊
            float segFrac = 0.2f + 0.8f * s;
            float scale = Math.Min(2.0f, 1.0f / Math.Max(segFrac, 0.5f));
            nextPos += pushDir * (pen * scale);
            pushNormalAccum += pushDir;
            return true;
        }
    }
}

#if !DOLLFRAME_PMX_PARSER_ONLY
namespace DollFrame
{
    using UnityEngine;

    /// <summary>
    /// Unity 執行期次要物理驅動器（掛載於 CharacterRig，支援戰局與主選單預覽）。
    /// </summary>
    internal sealed class SpringPhysicsRig
    {
        private enum OutwardRef : byte
        {
            None = 0,
            Pelvis = 1,
            Head = 2,
            Anchor = 3,
        }

        private enum CapsuleRegion : byte
        {
            Head = 0,
            Torso = 1,
            Arm = 2,
            Leg = 3,
        }

        private struct NodeEntry
        {
            public Transform Bone;
            public Transform Parent;
            public Transform AnchorBone;
            public Snx.Vector3 RestDirLocal;
            public float RestLenModel;
            public SpringParams Params;
            public SpringNodeState State;
            public Quaternion SolvedLocalRot;
            public uint ColliderMask;
            public bool IsHair;
            public bool IsBackHair;
            public bool IsSkirt;
            public bool HasSurfacePlane;
            public Vector3 OutwardLocalInAnchor;
            public Vector3 SurfaceOriginLocalInAnchor;
            public Vector3 SurfaceNormalLocalInAnchor; // 指向身體內側（與 backPlaneNormal 同方向語意：dot > 0 時推回）
            public OutwardRef OutwardMode;
            public float MaxInwardDeg;
            public float MaxHeadLocalDot;
            public int Depth;
            public int NeighborRight;
            public float RestDistRightModel;
            public Vector3 RestTipModel;
        }

        private struct ColliderLink
        {
            public Transform A;
            public Transform B;
            public float BaseRadius; // 相對於 1.57m 標準身高（root.lossyScale.x ≈ 0.078）
            public float TopOffset;  // 若 A == B（如頭部球體），沿 A.up 延伸偏移
            public CapsuleRegion Region;
        }

        private readonly Transform _root;
        private readonly Transform _head;
        private readonly Transform _pelvis;
        private readonly Vector3 _headForwardLocal;
        private readonly NodeEntry[] _nodes;
        private readonly ColliderLink[] _colliderLinks;
        private readonly BodyCapsule[] _capsules;
        private int _lastFrame = -1;

        private SpringPhysicsRig(Transform root, Transform head, Transform pelvis, Vector3 headForwardLocal, NodeEntry[] nodes, ColliderLink[] colliderLinks)
        {
            _root = root;
            _head = head;
            _pelvis = pelvis;
            _headForwardLocal = headForwardLocal;
            _nodes = nodes;
            _colliderLinks = colliderLinks;
            _capsules = new BodyCapsule[colliderLinks.Length];
        }

        internal int NodeCount => _nodes.Length;

        internal static SpringPhysicsRig Build(
            Transform root,
            Transform[] rigBones,
            PmxParser.PmxBone[] pmxBones,
            bool[] alreadyDriven,
            PmxParser.PmxModel pmxModel = null)
        {
            if (root == null || rigBones == null || pmxBones == null || pmxBones.Length == 0)
                return null;

            int n = pmxBones.Length;
            float[] leafVertMaxDist = ComputeLeafVertexExtents(pmxModel, pmxBones);
            var childrenOf = new List<int>[n];
            for (int i = 0; i < n; i++) childrenOf[i] = new List<int>(2);
            for (int i = 0; i < n; i++)
            {
                int p = pmxBones[i].Parent;
                if (p >= 0 && p < n) childrenOf[p].Add(i);
            }

            var byName = new Dictionary<string, Transform>(n, StringComparer.Ordinal);
            var idxByName = new Dictionary<string, int>(n, StringComparer.Ordinal);
            for (int i = 0; i < n; i++)
            {
                if (rigBones[i] != null && !string.IsNullOrEmpty(pmxBones[i].Name) && !byName.ContainsKey(pmxBones[i].Name))
                {
                    byName[pmxBones[i].Name] = rigBones[i];
                    idxByName[pmxBones[i].Name] = i;
                }
            }

            // 標記所有次要物理鏈及其子孫骨骼，供體態膠囊擬合與依附主骨追溯排除外層衣物頂點
            var isSpringBone = new bool[n];
            for (int i = 0; i < n; i++)
            {
                var b = pmxBones[i];
                if (b == null || string.IsNullOrEmpty(b.Name)) continue;
                if (ModelProfileKeywords.CategorizeSpring(b.Name) == null) continue;
                var markQ = new Queue<int>();
                markQ.Enqueue(i);
                while (markQ.Count > 0)
                {
                    int cur = markQ.Dequeue();
                    if (isSpringBone[cur]) continue;
                    isSpringBone[cur] = true;
                    foreach (int ch in childrenOf[cur]) markQ.Enqueue(ch);
                }
            }

            // 先建立身體膠囊並依非物理頂點自動擬合半徑（避免寬大衣物或不同體型導致穿模）
            var links = new List<ColliderLink>(20);
            var linkBoneIndices = new List<(int a, int b)>(20);
            void AddLink(string a, string b, float r, CapsuleRegion region, float topOff = 0f)
            {
                if (!byName.TryGetValue(a, out var ta) || ta == null) return;
                int ia = idxByName[a];
                Transform foundB = null;
                bool hasB = !string.IsNullOrEmpty(b) && byName.TryGetValue(b, out foundB) && foundB != null;
                Transform tb = hasB ? foundB : ta;
                int ib = hasB ? idxByName[b] : ia;
                links.Add(new ColliderLink { A = ta, B = tb, BaseRadius = r, TopOffset = topOff, Region = region });
                linkBoneIndices.Add((ia, ib));
            }

            AddLink("頭", "頭", 0.105f, CapsuleRegion.Head, 0.11f);
            AddLink("首", "頭", 0.055f, CapsuleRegion.Head);
            AddLink("上半身2", "首", 0.115f, CapsuleRegion.Torso);
            AddLink("上半身", "上半身2", 0.105f, CapsuleRegion.Torso);
            AddLink("下半身", "上半身", 0.095f, CapsuleRegion.Torso);
            AddLink("上半身2", "左肩", 0.065f, CapsuleRegion.Torso);
            AddLink("上半身2", "右肩", 0.065f, CapsuleRegion.Torso);
            AddLink("左肩", "左腕", 0.055f, CapsuleRegion.Arm);
            AddLink("右肩", "右腕", 0.055f, CapsuleRegion.Arm);
            AddLink("左腕", "左ひじ", 0.048f, CapsuleRegion.Arm);
            AddLink("右腕", "右ひじ", 0.048f, CapsuleRegion.Arm);
            AddLink("左ひじ", "左手首", 0.040f, CapsuleRegion.Arm);
            AddLink("右ひじ", "右手首", 0.040f, CapsuleRegion.Arm);
            AddLink("左手首", byName.ContainsKey("左中指１") ? "左中指１" : "左手首", 0.038f, CapsuleRegion.Arm);
            AddLink("右手首", byName.ContainsKey("右中指１") ? "右中指１" : "右手首", 0.038f, CapsuleRegion.Arm);
            AddLink("左足", "左ひざ", 0.070f, CapsuleRegion.Leg);
            AddLink("右足", "右ひざ", 0.070f, CapsuleRegion.Leg);
            AddLink("左ひざ", "左足首", 0.055f, CapsuleRegion.Leg);
            AddLink("右ひざ", "右足首", 0.055f, CapsuleRegion.Leg);

            FitBodyCapsuleRadii(pmxModel, pmxBones, isSpringBone, links, linkBoneIndices);

            byName.TryGetValue("頭", out var headT);
            byName.TryGetValue("下半身", out var pelvisT);
            Vector3 pelvisPosModel = Vector3.zero;
            for (int i = 0; i < n; i++)
            {
                if (rigBones[i] == pelvisT && pelvisT != null)
                {
                    pelvisPosModel = pmxBones[i].Pos;
                    break;
                }
            }
            // PMX 角色面對 -Z 軸；若存在左目/右目則以實際眼位推算前向軸，無依賴假設
            Vector3 headFwdLocal = Vector3.back;
            if (headT != null)
            {
                Vector3 eyeSum = Vector3.zero;
                int eyeCnt = 0;
                if (byName.TryGetValue("左目", out var eyeL) && eyeL != null) { eyeSum += headT.InverseTransformPoint(eyeL.position); eyeCnt++; }
                if (byName.TryGetValue("右目", out var eyeR) && eyeR != null) { eyeSum += headT.InverseTransformPoint(eyeR.position); eyeCnt++; }
                if (eyeCnt > 0)
                {
                    var avgEye = eyeSum / eyeCnt;
                    avgEye.y = 0f;
                    if (avgEye.sqrMagnitude > 1e-4f) headFwdLocal = avgEye.normalized;
                }
            }

            // 辨識所有次要物理鏈根並以 BFS（父優先）收集節點，確保父骨一定先於子骨解算
            var visited = new bool[n];
            var nodes = new List<NodeEntry>(32);

            for (int i = 0; i < n; i++)
            {
                var b = pmxBones[i];
                if (b == null || string.IsNullOrEmpty(b.Name)) continue;
                string catStr = ModelProfileKeywords.CategorizeSpring(b.Name);
                if (catStr == null) continue;

                bool parentIsSpring = b.Parent >= 0 && b.Parent < n &&
                                      ModelProfileKeywords.CategorizeSpring(pmxBones[b.Parent].Name) != null;
                if (parentIsSpring) continue;

                var cat = SpringParams.ParseCategory(catStr);
                var sp = SpringParams.ForCategory(cat);

                var q = new Queue<(int idx, int depth)>();
                q.Enqueue((i, 0));
                while (q.Count > 0)
                {
                    var (idx, depth) = q.Dequeue();
                    if (visited[idx]) continue;
                    visited[idx] = true;

                    foreach (int ch in childrenOf[idx]) q.Enqueue((ch, depth + 1));

                    if (alreadyDriven != null && idx < alreadyDriven.Length && alreadyDriven[idx]) continue;
                    if (rigBones[idx] == null) continue;

                    // 決定此骨的靜止延伸方向（模型空間 localPosition 差值）
                    Vector3 dirModel = Vector3.zero;
                    float lenModel = 0f;
                    var chs = childrenOf[idx];
                    if (chs.Count > 0)
                    {
                        // 取第一個有效子骨作為延伸軸（或所有子骨平均）
                        Vector3 avg = Vector3.zero;
                        int validCh = 0;
                        foreach (int ch in chs)
                        {
                            var d = pmxBones[ch].Pos - pmxBones[idx].Pos;
                            if (d.sqrMagnitude > 1e-6f)
                            {
                                avg += d;
                                validCh++;
                            }
                        }
                        if (validCh > 0)
                        {
                            avg /= validCh;
                            lenModel = avg.magnitude;
                            if (lenModel > 1e-4f) dirModel = avg / lenModel;
                        }
                    }
                    else if (pmxBones[idx].Parent >= 0 && pmxBones[idx].Parent < n)
                    {
                        // 末端葉骨：沿父→自方向延伸，優先取實際蒙皮頂點最大延伸距離（fallback 1 PMX 單位 ≈ 7.8cm）
                        var d = pmxBones[idx].Pos - pmxBones[pmxBones[idx].Parent].Pos;
                        if (d.sqrMagnitude > 1e-6f)
                        {
                            dirModel = d.normalized;
                            float measured = leafVertMaxDist != null && idx < leafVertMaxDist.Length ? leafVertMaxDist[idx] : 0f;
                            lenModel = measured > 0.05f ? Mathf.Clamp(measured, 0.35f, 3.0f) : 1.0f;
                        }
                    }

                    if (lenModel <= 1e-4f || dirModel.sqrMagnitude < 0.5f) continue;

                    var parentT = rigBones[idx].parent != null ? rigBones[idx].parent : root;
                    float worldScaleInit = Mathf.Max(root.lossyScale.x, 1e-4f);
                    var tipWorldInit = rigBones[idx].position + parentT.TransformDirection(dirModel) * (lenModel * worldScaleInit);

                    // 追溯「依附主骨（Anchor Ancestor）」：第一個非物理主骨架骨
                    int anchorIdx = pmxBones[idx].Parent;
                    int hops = 0;
                    while (anchorIdx >= 0 && anchorIdx < n && hops++ < 32 && (isSpringBone[anchorIdx] || rigBones[anchorIdx] == null))
                    {
                        anchorIdx = pmxBones[anchorIdx].Parent;
                    }
                    Transform anchorT = (anchorIdx >= 0 && anchorIdx < n && rigBones[anchorIdx] != null)
                        ? rigBones[anchorIdx]
                        : (pelvisT != null ? pelvisT : root);

                    bool isBackHair = false;
                    float maxHeadDot = 0f;
                    var nodeParams = sp;
                    var outwardMode = cat == SpringCategory.Skirt ? OutwardRef.Pelvis : OutwardRef.None;
                    float maxInwardDeg = cat == SpringCategory.Skirt ? 8f : 10f;
                    bool hasSurfacePlane = false;
                    Vector3 outwardLocalInAnchor = Vector3.zero;
                    Vector3 surfOriginLocalInAnchor = Vector3.zero;
                    Vector3 surfNormalLocalInAnchor = Vector3.zero;

                    if (cat == SpringCategory.Hair && headT != null)
                    {
                        var tipHeadLocal = headT.InverseTransformPoint(tipWorldInit);
                        float fwdDot = Vector3.Dot(tipHeadLocal, headFwdLocal);
                        if (fwdDot < -0.15f) // 位於頭骨後方（後髪／馬尾／雙馬尾）
                        {
                            isBackHair = true;
                            maxHeadDot = Mathf.Min(0f, fwdDot * 0.25f);
                        }
                        else if (fwdDot > 0.02f) // 位於頭骨前方（前髮／瀏海／鬢角）：大幅收斂擺動
                        {
                            nodeParams = SpringParams.ForFrontHair();
                        }
                        else // 側髮／雙馬尾根部：限制向頭骨內側擺動，避免穿進臉頰與頸部
                        {
                            outwardMode = OutwardRef.Head;
                            maxInwardDeg = 10f;
                        }
                    }
                    else if ((cat == SpringCategory.Accessory || cat == SpringCategory.Breast) && anchorT != null)
                    {
                        // 依靜止姿態找最近身體膠囊軸線（優先依附主骨相關膠囊），建立外法線與表面防穿切平面
                        Vector3 closestBodyW = anchorT.position;
                        float bestDistSq = float.MaxValue;
                        for (int c = 0; c < links.Count; c++)
                        {
                            var lk = links[c];
                            if (lk.A == null || lk.B == null) continue;
                            var pa = lk.A.position;
                            var pb = lk.A == lk.B && lk.TopOffset > 0f
                                ? pa + lk.A.up * (lk.TopOffset * (worldScaleInit / 0.078f))
                                : lk.B.position;
                            var cp = ClosestPointOnSegmentUnity(tipWorldInit, pa, pb);
                            float dSq = (tipWorldInit - cp).sqrMagnitude;
                            if (dSq < bestDistSq)
                            {
                                bestDistSq = dSq;
                                closestBodyW = cp;
                            }
                        }

                        var outwardW = tipWorldInit - closestBodyW;
                        if (outwardW.sqrMagnitude > 1e-6f)
                        {
                            outwardW.Normalize();
                            outwardMode = OutwardRef.Anchor;
                            maxInwardDeg = cat == SpringCategory.Breast ? 3f : 8f;
                            outwardLocalInAnchor = anchorT.InverseTransformDirection(outwardW);

                            float margin = cat == SpringCategory.Breast ? 0.003f : 0.005f;
                            hasSurfacePlane = true;
                            surfOriginLocalInAnchor = anchorT.InverseTransformPoint(tipWorldInit - outwardW * margin);
                            surfNormalLocalInAnchor = anchorT.InverseTransformDirection(-outwardW);
                        }
                    }

                    // 預篩此節點可能碰到的膠囊遮罩，每幀跳過結構無關或過遠的膠囊
                    uint colliderMask = 0;
                    float restLenW = lenModel * worldScaleInit;
                    float rScaleInit = worldScaleInit / 0.078f;
                    for (int c = 0; c < links.Count && c < 32; c++)
                    {
                        var lk = links[c];
                        if (cat == SpringCategory.Hair && lk.Region == CapsuleRegion.Leg) continue;
                        if (cat == SpringCategory.Skirt && lk.Region == CapsuleRegion.Head) continue;
                        if (cat == SpringCategory.Breast && (lk.Region == CapsuleRegion.Head || lk.Region == CapsuleRegion.Leg)) continue;

                        if (lk.Region == CapsuleRegion.Arm && (cat == SpringCategory.Hair || cat == SpringCategory.Skirt || cat == SpringCategory.Breast))
                        {
                            colliderMask |= (1u << c);
                            continue;
                        }

                        var pa = lk.A.position;
                        var pb = lk.A == lk.B && lk.TopOffset > 0f
                            ? pa + lk.A.up * (lk.TopOffset * rScaleInit)
                            : lk.B.position;
                        float d = Vector3.Distance(tipWorldInit, ClosestPointOnSegmentUnity(tipWorldInit, pa, pb));
                        float reachLimit = restLenW + lk.BaseRadius * rScaleInit + 0.45f;
                        if (d <= reachLimit)
                            colliderMask |= (1u << c);
                    }
                    if (links.Count > 32) colliderMask = uint.MaxValue;

                    nodes.Add(new NodeEntry
                    {
                        Bone = rigBones[idx],
                        Parent = parentT,
                        AnchorBone = anchorT,
                        RestDirLocal = new Snx.Vector3(dirModel.x, dirModel.y, dirModel.z),
                        RestLenModel = lenModel,
                        Params = nodeParams,
                        State = default,
                        SolvedLocalRot = Quaternion.identity,
                        ColliderMask = colliderMask,
                        IsHair = cat == SpringCategory.Hair,
                        IsBackHair = isBackHair,
                        IsSkirt = cat == SpringCategory.Skirt,
                        HasSurfacePlane = hasSurfacePlane,
                        OutwardLocalInAnchor = outwardLocalInAnchor,
                        SurfaceOriginLocalInAnchor = surfOriginLocalInAnchor,
                        SurfaceNormalLocalInAnchor = surfNormalLocalInAnchor,
                        OutwardMode = outwardMode,
                        MaxInwardDeg = maxInwardDeg,
                        MaxHeadLocalDot = maxHeadDot,
                        Depth = depth,
                        NeighborRight = -1,
                        RestDistRightModel = 0f,
                        RestTipModel = pmxBones[idx].Pos + dirModel * lenModel,
                    });
                }
            }

            if (nodes.Count == 0) return null;

            // 依深度排序確保所有鏈的同層節點連續排列（先解全體 depth=0 並做同圈平滑，再解 depth=1）
            nodes.Sort((a, b) => a.Depth.CompareTo(b.Depth));
            var nodeArr = nodes.ToArray();

            // 為同深度裙擺建立「同圈相鄰平滑約束」（依骨盆中心方位角排序串聯）
            if (pelvisT != null)
            {
                int i0 = 0;
                while (i0 < nodeArr.Length)
                {
                    int d = nodeArr[i0].Depth;
                    int i1 = i0;
                    while (i1 < nodeArr.Length && nodeArr[i1].Depth == d) i1++;

                    var ring = new List<(int idx, float angle)>();
                    for (int k = i0; k < i1; k++)
                    {
                        if (!nodeArr[k].IsSkirt) continue;
                        var rel = nodeArr[k].RestTipModel - pelvisPosModel;
                        float ang = Mathf.Atan2(rel.z, rel.x);
                        ring.Add((k, ang));
                    }
                    if (ring.Count >= 2)
                    {
                        ring.Sort((a, b) => a.angle.CompareTo(b.angle));
                        for (int r = 0; r < ring.Count; r++)
                        {
                            int cur = ring[r].idx;
                            int nxtIdx = (r + 1) % ring.Count;
                            if (ring.Count == 2 && r == 1) break;
                            int nxt = ring[nxtIdx].idx;
                            float dAng = ring[nxtIdx].angle - ring[r].angle;
                            if (dAng < 0f) dAng += 2f * Mathf.PI;
                            if (dAng < 1.92f) // < 110° 才視為相鄰裙片
                            {
                                nodeArr[cur].NeighborRight = nxt;
                                nodeArr[cur].RestDistRightModel = Vector3.Distance(nodeArr[cur].RestTipModel, nodeArr[nxt].RestTipModel);
                            }
                        }
                    }
                    i0 = i1;
                }
            }

            return new SpringPhysicsRig(root, headT, pelvisT, headFwdLocal, nodeArr, links.ToArray());
        }

        private static Vector3 ClosestPointOnSegmentUnity(Vector3 p, Vector3 a, Vector3 b)
        {
            var ab = b - a;
            float lenSq = ab.sqrMagnitude;
            if (lenSq <= 1e-8f) return a;
            float t = Mathf.Clamp01(Vector3.Dot(p - a, ab) / lenSq);
            return a + ab * t;
        }

        private static void FitBodyCapsuleRadii(
            PmxParser.PmxModel pmxModel,
            PmxParser.PmxBone[] pmxBones,
            bool[] isSpringBone,
            List<ColliderLink> links,
            List<(int a, int b)> linkBones)
        {
            if (pmxModel == null || pmxModel.Pos == null || pmxModel.Weights == null || pmxBones == null)
                return;

            int boneCount = pmxBones.Length;
            int vertCount = Math.Min(pmxModel.Pos.Length, pmxModel.Weights.Length);

            // 將每個「非物理骨」的合格頂點索引分組（排除含任何物理骨權重 > 0.1 的外層衣物／頭髮頂點）
            var vertsOfBone = new List<int>[boneCount];
            for (int v = 0; v < vertCount; v++)
            {
                var bw = pmxModel.Weights[v];
                if ((bw.weight0 > 0.1f && bw.boneIndex0 >= 0 && bw.boneIndex0 < boneCount && isSpringBone[bw.boneIndex0]) ||
                    (bw.weight1 > 0.1f && bw.boneIndex1 >= 0 && bw.boneIndex1 < boneCount && isSpringBone[bw.boneIndex1]) ||
                    (bw.weight2 > 0.1f && bw.boneIndex2 >= 0 && bw.boneIndex2 < boneCount && isSpringBone[bw.boneIndex2]) ||
                    (bw.weight3 > 0.1f && bw.boneIndex3 >= 0 && bw.boneIndex3 < boneCount && isSpringBone[bw.boneIndex3]))
                    continue;

                int dom = bw.boneIndex0;
                float domW = bw.weight0;
                if (bw.weight1 > domW) { dom = bw.boneIndex1; domW = bw.weight1; }
                if (bw.weight2 > domW) { dom = bw.boneIndex2; domW = bw.weight2; }
                if (bw.weight3 > domW) { dom = bw.boneIndex3; domW = bw.weight3; }

                if (dom < 0 || dom >= boneCount || domW < 0.35f || isSpringBone[dom]) continue;
                (vertsOfBone[dom] ??= new List<int>(64)).Add(v);
            }

            var dists = new List<float>(256);
            for (int c = 0; c < links.Count; c++)
            {
                var lk = links[c];
                if (lk.TopOffset > 0f) continue; // 頭部球體維持預設半徑
                var (ia, ib) = linkBones[c];
                Vector3 pa = pmxBones[ia].Pos;
                Vector3 pb = pmxBones[ib].Pos;

                dists.Clear();
                void SampleBone(int bi)
                {
                    var list = vertsOfBone[bi];
                    if (list == null) return;
                    for (int k = 0; k < list.Count; k++)
                    {
                        var vp = pmxModel.Pos[list[k]];
                        var cp = ClosestPointOnSegmentUnity(vp, pa, pb);
                        dists.Add((vp - cp).magnitude);
                    }
                }
                SampleBone(ia);
                if (ib != ia) SampleBone(ib);

                if (dists.Count < 16) continue;
                dists.Sort();
                // 取第 45 百分位（貼近內層身體輪廓，忽略外層立領／槍套／腰包凸起）
                float modelRadius = dists[(int)(dists.Count * 0.45f)];
                float fittedBase = modelRadius * 0.078f;
                lk.BaseRadius = Mathf.Clamp(fittedBase, lk.BaseRadius * 0.75f, lk.BaseRadius * 1.40f);
                links[c] = lk;
            }
        }

        private static float[] ComputeLeafVertexExtents(PmxParser.PmxModel pmxModel, PmxParser.PmxBone[] pmxBones)
        {
            if (pmxModel == null || pmxModel.Pos == null || pmxModel.Weights == null || pmxBones == null)
                return null;

            int boneCount = pmxBones.Length;
            int vertCount = Math.Min(pmxModel.Pos.Length, pmxModel.Weights.Length);
            var maxDist = new float[boneCount];

            void Accum(int bi, float w, Vector3 vPos)
            {
                if (w < 0.25f || bi < 0 || bi >= boneCount) return;
                float d = Vector3.Distance(vPos, pmxBones[bi].Pos);
                if (d > maxDist[bi]) maxDist[bi] = d;
            }

            for (int v = 0; v < vertCount; v++)
            {
                var vPos = pmxModel.Pos[v];
                var bw = pmxModel.Weights[v];
                Accum(bw.boneIndex0, bw.weight0, vPos);
                Accum(bw.boneIndex1, bw.weight1, vPos);
                Accum(bw.boneIndex2, bw.weight2, vPos);
                Accum(bw.boneIndex3, bw.weight3, vPos);
            }
            return maxDist;
        }

        internal void StepAndApply(float dt, int frameCount, bool enabled, bool inFirstPerson = false)
        {
            if (_root == null || _nodes.Length == 0) return;

            if (!enabled)
            {
                for (int i = 0; i < _nodes.Length; i++)
                {
                    if (_nodes[i].Bone != null) _nodes[i].Bone.localRotation = Quaternion.identity;
                    _nodes[i].State.Initialized = false;
                }
                return;
            }

            // 防止同一幀內（LateUpdate + OnBeforeRender）重複推進物理模擬
            if (frameCount == _lastFrame)
            {
                for (int i = 0; i < _nodes.Length; i++)
                {
                    if (_nodes[i].Bone != null) _nodes[i].Bone.localRotation = _nodes[i].SolvedLocalRot;
                }
                return;
            }
            _lastFrame = frameCount;

            float worldScale = MathF.Max(_root.lossyScale.x, 1e-4f);
            float radiusScale = worldScale / 0.078f;

            // 更新本幀身體膠囊世界座標
            int capCount = _colliderLinks.Length;
            for (int c = 0; c < capCount; c++)
            {
                ref readonly var link = ref _colliderLinks[c];
                if (link.A == null || link.B == null)
                {
                    _capsules[c] = new BodyCapsule(Snx.Vector3.Zero, Snx.Vector3.Zero, 0f);
                    continue;
                }
                var pa = link.A.position;
                var pb = link.A == link.B && link.TopOffset > 0f
                    ? pa + link.A.up * (link.TopOffset * radiusScale)
                    : link.B.position;
                _capsules[c] = new BodyCapsule(
                    new Snx.Vector3(pa.x, pa.y, pa.z),
                    new Snx.Vector3(pb.x, pb.y, pb.z),
                    link.BaseRadius * radiusScale);
            }

            Vector3 headPosW = _head != null ? _head.position : Vector3.zero;
            Vector3 headUpW = _head != null ? _head.up : Vector3.up;
            Vector3 headFwdW = _head != null ? _head.TransformDirection(_headForwardLocal).normalized : Vector3.forward;
            bool hasPelvis = _pelvis != null;
            Vector3 pelvisPosW = hasPelvis ? _pelvis.position : Vector3.zero;
            Vector3 pelvisUpW = hasPelvis ? _pelvis.up : Vector3.up;
            float rootGroundY = _root.position.y;

            // 依深度分層求解：同層步進 → 同層裙擺相鄰平滑 → 寫入 localRotation（子層即可讀取最新父世界變換）
            int i0 = 0;
            while (i0 < _nodes.Length)
            {
                int d = _nodes[i0].Depth;
                int i1 = i0;
                while (i1 < _nodes.Length && _nodes[i1].Depth == d) i1++;

                bool anySkirtNeighbor = false;
                for (int i = i0; i < i1; i++)
                {
                    ref var node = ref _nodes[i];
                    if (node.Bone == null || node.Parent == null) continue;
                    if (inFirstPerson && node.IsHair)
                    {
                        node.State.Initialized = false;
                        node.SolvedLocalRot = Quaternion.identity;
                        continue;
                    }
                    if (node.NeighborRight >= 0) anySkirtNeighbor = true;

                    var pPos = node.Bone.position;
                    var pRot = node.Parent.rotation;
                    float restLenWorld = node.RestLenModel * worldScale;

                    bool hasClipPlane = false;
                    Snx.Vector3 clipOrig = default, clipNorm = default;
                    if (node.IsBackHair && _head != null)
                    {
                        var o = headPosW + headFwdW * (node.MaxHeadLocalDot * worldScale);
                        clipOrig = new Snx.Vector3(o.x, o.y, o.z);
                        clipNorm = new Snx.Vector3(headFwdW.x, headFwdW.y, headFwdW.z);
                        hasClipPlane = true;
                    }
                    else if (node.HasSurfacePlane && node.AnchorBone != null)
                    {
                        var o = node.AnchorBone.TransformPoint(node.SurfaceOriginLocalInAnchor);
                        var n = node.AnchorBone.TransformDirection(node.SurfaceNormalLocalInAnchor).normalized;
                        clipOrig = new Snx.Vector3(o.x, o.y, o.z);
                        clipNorm = new Snx.Vector3(n.x, n.y, n.z);
                        hasClipPlane = true;
                    }

                    bool hasOutward = false;
                    Snx.Vector3 outwardSnx = default;
                    if (node.OutwardMode == OutwardRef.Pelvis && hasPelvis)
                    {
                        var rel = pPos - pelvisPosW;
                        rel -= pelvisUpW * Vector3.Dot(rel, pelvisUpW);
                        if (rel.sqrMagnitude > 1e-6f)
                        {
                            rel.Normalize();
                            outwardSnx = new Snx.Vector3(rel.x, rel.y, rel.z);
                            hasOutward = true;
                        }
                    }
                    else if (node.OutwardMode == OutwardRef.Head && _head != null)
                    {
                        var rel = pPos - headPosW;
                        rel -= headUpW * Vector3.Dot(rel, headUpW);
                        if (rel.sqrMagnitude > 1e-6f)
                        {
                            rel.Normalize();
                            outwardSnx = new Snx.Vector3(rel.x, rel.y, rel.z);
                            hasOutward = true;
                        }
                    }
                    else if (node.OutwardMode == OutwardRef.Anchor && node.AnchorBone != null)
                    {
                        var outW = node.AnchorBone.TransformDirection(node.OutwardLocalInAnchor).normalized;
                        if (outW.sqrMagnitude > 1e-6f)
                        {
                            outwardSnx = new Snx.Vector3(outW.x, outW.y, outW.z);
                            hasOutward = true;
                        }
                    }

                    float minWorldY = rootGroundY + node.Params.Radius;
                    var qLocal = SpringPhysicsMath.StepNode(
                        ref node.State,
                        new Snx.Vector3(pPos.x, pPos.y, pPos.z),
                        new Snx.Quaternion(pRot.x, pRot.y, pRot.z, pRot.w),
                        Snx.Quaternion.Identity,
                        node.RestDirLocal,
                        restLenWorld,
                        dt,
                        in node.Params,
                        _capsules,
                        capCount,
                        hasClipPlane,
                        clipOrig,
                        clipNorm,
                        0f,
                        hasOutward,
                        outwardSnx,
                        node.MaxInwardDeg,
                        minWorldY,
                        node.ColliderMask);

                    node.SolvedLocalRot = new Quaternion(qLocal.X, qLocal.Y, qLocal.Z, qLocal.W);
                }

                // 同層裙擺橫向相鄰平滑約束（避免單片被推開形成孤立凸起）＋平滑後補做膠囊碰撞防止被拉回腿內
                if (anySkirtNeighbor)
                {
                    for (int iter = 0; iter < 2; iter++)
                    {
                        for (int i = i0; i < i1; i++)
                        {
                            int nb = _nodes[i].NeighborRight;
                            if (nb < 0 || _nodes[i].Bone == null || _nodes[nb].Bone == null) continue;
                            float restDist = _nodes[i].RestDistRightModel * worldScale;
                            if (restDist <= 1e-4f) continue;

                            var diff = _nodes[nb].State.TipPos - _nodes[i].State.TipPos;
                            float distSq = diff.LengthSquared();
                            if (distSq <= 1e-10f) continue;
                            float dist = MathF.Sqrt(distSq);
                            float minD = restDist * 0.85f;
                            float maxD = restDist * 1.15f;
                            float targetD = dist < minD ? minD : (dist > maxD ? maxD : dist);
                            if (MathF.Abs(dist - targetD) > 1e-5f)
                            {
                                var corr = (diff / dist) * ((dist - targetD) * 0.35f);
                                _nodes[i].State.TipPos += corr;
                                _nodes[nb].State.TipPos -= corr;
                            }
                        }

                        for (int i = i0; i < i1; i++)
                        {
                            ref var node = ref _nodes[i];
                            if (!node.IsSkirt || node.Bone == null || node.Parent == null) continue;
                            var pPos = node.Bone.position;
                            var pSnx = new Snx.Vector3(pPos.x, pPos.y, pPos.z);
                            var pRot = node.Parent.rotation;
                            var pRotSnx = new Snx.Quaternion(pRot.x, pRot.y, pRot.z, pRot.w);
                            var restDirWorld = Snx.Vector3.Normalize(Snx.Vector3.Transform(node.RestDirLocal, pRotSnx));
                            float restLenWorld = node.RestLenModel * worldScale;
                            SpringPhysicsMath.ProjectConstraints(
                                pSnx, restDirWorld, restLenWorld, node.Params.Radius,
                                _capsules, capCount, node.State.ColliderRadii,
                                false, default, default, rootGroundY + node.Params.Radius,
                                ref node.State.TipPos, out _, node.ColliderMask);
                        }
                    }

                    for (int i = i0; i < i1; i++)
                    {
                        ref var node = ref _nodes[i];
                        if (!node.IsSkirt || node.Bone == null || node.Parent == null) continue;
                        var pPos = node.Bone.position;
                        var pRot = node.Parent.rotation;
                        var pRotSnx = new Snx.Quaternion(pRot.x, pRot.y, pRot.z, pRot.w);
                        var restDirWorld = Snx.Vector3.Normalize(Snx.Vector3.Transform(node.RestDirLocal, pRotSnx));
                        var qLocal = SpringPhysicsMath.SolveLocalRotation(
                            restDirWorld, pRotSnx, pRotSnx, Snx.Quaternion.Identity,
                            new Snx.Vector3(pPos.x, pPos.y, pPos.z), node.State.TipPos);
                        node.SolvedLocalRot = new Quaternion(qLocal.X, qLocal.Y, qLocal.Z, qLocal.W);
                    }
                }

                // 整層求解完成後才批次寫入 localRotation，避免讀寫交錯觸發 Unity Transform 矩陣重算
                for (int i = i0; i < i1; i++)
                {
                    if (_nodes[i].Bone != null)
                        _nodes[i].Bone.localRotation = _nodes[i].SolvedLocalRot;
                }

                i0 = i1;
            }
        }
    }
}
#endif
