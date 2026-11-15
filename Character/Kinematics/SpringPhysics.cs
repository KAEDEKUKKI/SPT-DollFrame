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
                        Stiffness = 42f,
                        Drag = 6.5f,
                        GravityScale = 0.35f,
                        MaxAngleDeg = 20f,
                        Radius = 0.015f,
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
        public float[] ColliderRadii; // 每個膠囊對此節點的有效半徑（靜止時若重疊則縮減而不跳過，確保持續防穿模）
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
            float minColliderRatio = 0f)
        {
            var restWorldRot = Snx.Quaternion.Normalize(parentWorldRot * boneRestLocalRot);
            var restDirWorld = Snx.Vector3.Normalize(Snx.Vector3.Transform(restDirLocal, restWorldRot));
            var targetPos = pivotPos + restDirWorld * restLen;

            // 初始化或瞬移保護（進入戰局／傳送／切角色時歸位，並依靜止姿態貼合每個膠囊的有效半徑）
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
                        ref readonly var cap = ref colliders[c];
                        var ab = cap.B - cap.A;
                        float abLenSq = ab.LengthSquared();
                        float t = abLenSq > 1e-8f ? Math.Clamp(Snx.Vector3.Dot(targetPos - cap.A, ab) / abLenSq, 0f, 1f) : 0f;
                        float dist = (targetPos - (cap.A + ab * t)).Length();
                        float maxAllowed = dist - p.Radius - 0.002f;
                        float floor = Math.Max(0.005f, cap.Radius * minColliderRatio);
                        state.ColliderRadii[c] = maxAllowed < cap.Radius ? Math.Max(floor, maxAllowed) : cap.Radius;
                    }
                }
                state.Initialized = true;
                return boneRestLocalRot;
            }

            float clampedDt = Math.Clamp(dt, 0f, MaxDt);
            if (clampedDt > 1e-6f)
            {
                int steps = clampedDt > SubstepThreshold ? 2 : 1;
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

                    // 1) 硬距離約束 + 2) 圓錐角度限幅（先限角再碰撞，避免 ClampCone 把已推離的裙擺/髮絲拉回身體內部）
                    var d1 = nextPos - pivotPos;
                    float d1Len = d1.Length();
                    var dir1 = d1Len > 1e-6f ? (d1 / d1Len) : restDirWorld;
                    dir1 = ClampCone(dir1, restDirWorld, p.MaxAngleDeg);
                    nextPos = pivotPos + dir1 * restLen;

                    // 3) 身體膠囊碰撞推離（尾端＋中點同時防穿模）
                    bool hitCollider = false;
                    if (colliders != null && colliderCount > 0)
                    {
                        bool checkMid = restLen > 0.04f;
                        for (int c = 0; c < colliderCount; c++)
                        {
                            var cap = colliders[c];
                            if (state.ColliderRadii != null && c < state.ColliderRadii.Length)
                                cap.Radius = Math.Min(cap.Radius, state.ColliderRadii[c]);
                            if (cap.Radius <= 0.004f) continue;

                            var before = nextPos;
                            nextPos = PushOutCapsule(nextPos, p.Radius, in cap);
                            if ((nextPos - before).LengthSquared() > 1e-8f) hitCollider = true;

                            if (checkMid)
                            {
                                var mid = (pivotPos + nextPos) * 0.5f;
                                var midOut = PushOutCapsule(mid, p.Radius * 0.8f, in cap);
                                var midDelta = midOut - mid;
                                if (midDelta.LengthSquared() > 1e-8f)
                                {
                                    nextPos += midDelta;
                                    hitCollider = true;
                                }
                            }
                        }
                    }

                    // 4) 後髮額狀面防護：防止後腦勺長髮翻越肩膀穿到胸前或臉上
                    if (hasBackPlane)
                    {
                        float fwdDot = Snx.Vector3.Dot(nextPos - backPlaneOrigin, backPlaneNormal);
                        if (fwdDot > 0f)
                        {
                            nextPos -= backPlaneNormal * fwdDot;
                            hitCollider = true;
                        }
                    }

                    // 5) 最終硬距離投影（確保嚴格零拉伸，且碰撞優先於角度限幅）
                    var d2 = nextPos - pivotPos;
                    float d2Len = d2.Length();
                    var dir2 = d2Len > 1e-6f ? (d2 / d2Len) : restDirWorld;
                    nextPos = pivotPos + dir2 * restLen;

                    // 僅保留切向阻尼後速度，禁止由碰撞推離產生自激震盪爆炸
                    if (hitCollider) vel *= 0.25f;
                    state.TipPos = nextPos;
                    state.Velocity = vel;
                }
            }

            state.PrevPivotPos = pivotPos;

            var solvedDir = state.TipPos - pivotPos;
            float solvedLen = solvedDir.Length();
            if (solvedLen < 1e-6f) return boneRestLocalRot;
            solvedDir /= solvedLen;

            var deltaWorld = FromTo(restDirWorld, solvedDir);
            var targetWorldRot = Snx.Quaternion.Normalize(deltaWorld * restWorldRot);
            return RigAim.LocalFromWorld(parentWorldRot, targetWorldRot);
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
        private struct NodeEntry
        {
            public Transform Bone;
            public Transform Parent;
            public Snx.Vector3 RestDirLocal;
            public float RestLenModel;
            public SpringParams Params;
            public SpringNodeState State;
            public Quaternion SolvedLocalRot;
            public bool IsBackHair;
            public bool IsSkirt;
            public float MaxHeadLocalDot;
        }

        private struct ColliderLink
        {
            public Transform A;
            public Transform B;
            public float BaseRadius; // 相對於 1.57m 標準身高（root.lossyScale.x ≈ 0.078）
            public float TopOffset;  // 若 A == B（如頭部球體），沿 A.up 延伸偏移
        }

        private readonly Transform _root;
        private readonly Transform _head;
        private readonly Vector3 _headForwardLocal;
        private readonly NodeEntry[] _nodes;
        private readonly ColliderLink[] _colliderLinks;
        private readonly BodyCapsule[] _capsules;
        private int _lastFrame = -1;

        private SpringPhysicsRig(Transform root, Transform head, Vector3 headForwardLocal, NodeEntry[] nodes, ColliderLink[] colliderLinks)
        {
            _root = root;
            _head = head;
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
            bool[] alreadyDriven)
        {
            if (root == null || rigBones == null || pmxBones == null || pmxBones.Length == 0)
                return null;

            int n = pmxBones.Length;
            var childrenOf = new List<int>[n];
            for (int i = 0; i < n; i++) childrenOf[i] = new List<int>(2);
            for (int i = 0; i < n; i++)
            {
                int p = pmxBones[i].Parent;
                if (p >= 0 && p < n) childrenOf[p].Add(i);
            }

            var byName = new Dictionary<string, Transform>(n, StringComparer.Ordinal);
            for (int i = 0; i < n; i++)
            {
                if (rigBones[i] != null && !string.IsNullOrEmpty(pmxBones[i].Name) && !byName.ContainsKey(pmxBones[i].Name))
                    byName[pmxBones[i].Name] = rigBones[i];
            }

            byName.TryGetValue("頭", out var headT);
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

                var q = new Queue<int>();
                q.Enqueue(i);
                while (q.Count > 0)
                {
                    int idx = q.Dequeue();
                    if (visited[idx]) continue;
                    visited[idx] = true;

                    foreach (int ch in childrenOf[idx]) q.Enqueue(ch);

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
                        // 末端葉骨：沿父→自方向延伸虛擬尾端（約 1 PMX 單位 ≈ 7.8cm）
                        var d = pmxBones[idx].Pos - pmxBones[pmxBones[idx].Parent].Pos;
                        if (d.sqrMagnitude > 1e-6f)
                        {
                            dirModel = d.normalized;
                            lenModel = 1.0f;
                        }
                    }

                    if (lenModel <= 1e-4f || dirModel.sqrMagnitude < 0.5f) continue;

                    var parentT = rigBones[idx].parent != null ? rigBones[idx].parent : root;
                    bool isBackHair = false;
                    float maxHeadDot = 0f;
                    var nodeParams = sp;
                    if (cat == SpringCategory.Hair && headT != null)
                    {
                        var tipWorld = rigBones[idx].position + parentT.TransformDirection(dirModel) * (lenModel * Mathf.Max(root.lossyScale.x, 1e-4f));
                        var tipHeadLocal = headT.InverseTransformPoint(tipWorld);
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
                    }

                    nodes.Add(new NodeEntry
                    {
                        Bone = rigBones[idx],
                        Parent = parentT,
                        RestDirLocal = new Snx.Vector3(dirModel.x, dirModel.y, dirModel.z),
                        RestLenModel = lenModel,
                        Params = nodeParams,
                        State = default,
                        SolvedLocalRot = Quaternion.identity,
                        IsBackHair = isBackHair,
                        IsSkirt = cat == SpringCategory.Skirt,
                        MaxHeadLocalDot = maxHeadDot,
                    });
                }
            }

            if (nodes.Count == 0) return null;

            // 建立隨動身體膠囊碰撞體（綁定實際角色骨骼）
            var links = new List<ColliderLink>(14);
            void AddLink(string a, string b, float r, float topOff = 0f)
            {
                if (!byName.TryGetValue(a, out var ta) || ta == null) return;
                Transform tb = !string.IsNullOrEmpty(b) && byName.TryGetValue(b, out var foundB) ? foundB : ta;
                links.Add(new ColliderLink { A = ta, B = tb, BaseRadius = r, TopOffset = topOff });
            }

            AddLink("頭", "頭", 0.105f, 0.11f);
            AddLink("首", "頭", 0.055f);
            AddLink("上半身2", "首", 0.115f);
            AddLink("上半身", "上半身2", 0.105f);
            AddLink("下半身", "上半身", 0.095f);
            AddLink("上半身2", "左肩", 0.065f);
            AddLink("上半身2", "右肩", 0.065f);
            AddLink("左肩", "左腕", 0.055f);
            AddLink("右肩", "右腕", 0.055f);
            AddLink("左腕", "左ひじ", 0.048f);
            AddLink("右腕", "右ひじ", 0.048f);
            AddLink("左ひじ", "左手首", 0.040f);
            AddLink("右ひじ", "右手首", 0.040f);
            AddLink("左足", "左ひざ", 0.070f);
            AddLink("右足", "右ひざ", 0.070f);
            AddLink("左ひざ", "左足首", 0.055f);
            AddLink("右ひざ", "右足首", 0.055f);

            return new SpringPhysicsRig(root, headT, headFwdLocal, nodes.ToArray(), links.ToArray());
        }

        internal void StepAndApply(float dt, int frameCount, bool enabled)
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
            Vector3 headFwdW = _head != null ? _head.TransformDirection(_headForwardLocal).normalized : Vector3.forward;

            // 依父優先順序逐節點求解並即時寫入 localRotation（子節點即可讀取最新父世界變換）
            for (int i = 0; i < _nodes.Length; i++)
            {
                ref var node = ref _nodes[i];
                if (node.Bone == null || node.Parent == null) continue;

                var pPos = node.Bone.position;
                var pRot = node.Parent.rotation;
                float restLenWorld = node.RestLenModel * worldScale;

                bool hasBack = node.IsBackHair && _head != null;
                Snx.Vector3 backOrig = default, backNorm = default;
                if (hasBack)
                {
                    var o = headPosW + headFwdW * (node.MaxHeadLocalDot * worldScale);
                    backOrig = new Snx.Vector3(o.x, o.y, o.z);
                    backNorm = new Snx.Vector3(headFwdW.x, headFwdW.y, headFwdW.z);
                }

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
                    hasBack,
                    backOrig,
                    backNorm,
                    node.IsSkirt ? 0.50f : 0f);

                var uRot = new Quaternion(qLocal.X, qLocal.Y, qLocal.Z, qLocal.W);
                node.SolvedLocalRot = uRot;
                node.Bone.localRotation = uRot;
            }
        }
    }
}
#endif
