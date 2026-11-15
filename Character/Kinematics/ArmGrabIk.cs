using System;
using Snx = System.Numerics;

namespace GF2.Character.Kinematics
{
    /// <summary>
    /// 獨立手臂抓取 IK 求解模塊（左右手共用）：
    /// 1) SolveClavicle：依目標世界座標（握把／護木）計算鎖骨抬升與前伸角度（最多 28°），
    ///    使肩關節靠近目標，由 CharacterRig 先寫入鎖骨旋轉；
    /// 2) SolveTwoBone：從鎖骨寫入後的新肩關節世界座標出發，以餘弦定理計算上臂抬高與
    ///    手肘彎曲角度，使手腕精準貼合目標世界座標（左右手皆適用）。
    /// 全程僅輸出旋轉（零 localPosition / localScale 寫入）。
    /// </summary>
    public static class ArmGrabIkMath
    {
        public readonly struct TwoBoneResult
        {
            public readonly bool Ok;
            public readonly Snx.Quaternion UpperWorld;
            public readonly Snx.Quaternion ForeWorld;
            public readonly float ReachErrorM;

            public TwoBoneResult(bool ok, Snx.Quaternion upperWorld, Snx.Quaternion foreWorld, float reachErrorM)
            {
                Ok = ok;
                UpperWorld = upperWorld;
                ForeWorld = foreWorld;
                ReachErrorM = reachErrorM;
            }
        }

        public static Snx.Quaternion SolveClavicle(
            in TwoBoneIkMath.TwoBoneIkChain chain,
            Snx.Vector3 claviclePos,
            Snx.Quaternion clavicleFkWorld,
            Snx.Vector3 clavLocalChildAxis,
            float clavSpan,
            Snx.Vector3 targetWorld)
        {
            var fkClavDir = Snx.Vector3.Transform(clavLocalChildAxis, clavicleFkWorld);
            if (fkClavDir.LengthSquared() <= 1e-6f || clavSpan <= 1e-4f)
                return clavicleFkWorld;

            fkClavDir = Snx.Vector3.Normalize(fkClavDir);
            var shoulderEst = claviclePos + fkClavDir * clavSpan;
            float restDist = Snx.Vector3.Distance(targetWorld, shoulderEst);
            float maxArm = chain.L1 + chain.L2;

            if (restDist <= maxArm * 0.78f)
                return clavicleFkWorld;

            var toTargetFromClav = targetWorld - claviclePos;
            float clavToTargetDist = toTargetFromClav.Length();
            if (clavToTargetDist <= 1e-4f)
                return clavicleFkWorld;

            var desiredClavDir = toTargetFromClav / clavToTargetDist;
            float ang = MathF.Acos(Math.Clamp(Snx.Vector3.Dot(fkClavDir, desiredClavDir), -1f, 1f));
            float maxRad = 45f * (MathF.PI / 180f);
            float needFactor = Math.Clamp((restDist - maxArm * 0.78f) / (maxArm * 0.20f), 0f, 1f);
            float pullRad = Math.Min(ang, maxRad) * needFactor;
            if (ang <= 1e-4f || pullRad <= 1e-4f)
                return clavicleFkWorld;

            var pull = Snx.Quaternion.Slerp(Snx.Quaternion.Identity,
                RigAim.FromTo(fkClavDir, desiredClavDir), pullRad / ang);
            return Snx.Quaternion.Normalize(pull * clavicleFkWorld);
        }

        public static TwoBoneResult SolveTwoBone(
            in TwoBoneIkMath.TwoBoneIkChain chain,
            Snx.Vector3 shoulderPos,
            Snx.Vector3 targetWorld,
            Snx.Quaternion gamePalmRotNow,
            Snx.Vector3 chestPos,
            Snx.Quaternion chestRotNow,
            float chestRadius,
            Snx.Quaternion parentWorldUpper)
        {
            float l1 = chain.L1;
            float l2 = chain.L2;
            if (l1 <= 1e-4f || l2 <= 1e-4f) return default;

            var dirRaw = targetWorld - shoulderPos;
            float dist = dirRaw.Length();
            if (dist < TwoBoneIkMath.MinDirEps) return default;
            var dir = dirRaw / dist;

            float minReach = MathF.Max(MathF.Abs(l1 - l2) + 0.005f, 0.02f);
            float maxReach = (l1 + l2) * 0.9998f;
            float reach = Math.Clamp(dist, minReach, maxReach);

            var poleWorld = Snx.Vector3.Transform(chain.PoleLocalChest, chestRotNow);
            var nRaw = Snx.Vector3.Cross(dir, poleWorld);
            var n = nRaw.LengthSquared() < TwoBoneIkMath.PoleMinCross * TwoBoneIkMath.PoleMinCross
                ? RigAim.Perpendicular(dir)
                : Snx.Vector3.Normalize(nRaw);

            float cosT1 = (l1 * l1 + reach * reach - l2 * l2) / (2f * l1 * reach);
            float theta1 = MathF.Acos(Math.Clamp(cosT1, -1f, 1f));
            var upperDir = Snx.Vector3.Normalize(
                Snx.Vector3.Transform(dir, Snx.Quaternion.CreateFromAxisAngle(n, theta1)));

            var elbowPos = shoulderPos + upperDir * l1;
            var clampedEnd = shoulderPos + dir * reach;
            PushElbowOutOfChest(shoulderPos, dir, clampedEnd, chestPos, chestRadius, ref elbowPos);

            upperDir = Snx.Vector3.Normalize(elbowPos - shoulderPos);
            var foreRaw = clampedEnd - elbowPos;
            if (foreRaw.LengthSquared() < 1e-8f) return default;
            var foreDir = Snx.Vector3.Normalize(foreRaw);

            var lChildUpper = Snx.Vector3.Transform(chain.DirCUpper, Snx.Quaternion.Inverse(chain.InitRotUpper));
            var fromUpper = Snx.Vector3.Transform(lChildUpper, parentWorldUpper);
            var upperWorld = Snx.Quaternion.Normalize(RigAim.FromTo(fromUpper, upperDir) * parentWorldUpper);

            var chainImagePalm = parentWorldUpper * Snx.Quaternion.Inverse(chain.InitRotUpper) * chain.ChainGamePalm0;
            var palmDelta = gamePalmRotNow * Snx.Quaternion.Inverse(chainImagePalm);
            float twistRad = 0.5f * TwoBoneIkMath.TwistAngle(palmDelta, foreDir);

            var lChildFore = Snx.Vector3.Transform(chain.DirCFore, Snx.Quaternion.Inverse(chain.InitRotFore));
            var attachRelFore = Snx.Quaternion.Inverse(chain.InitRotUpper) * chain.InitRotFore;
            var fromFore = Snx.Vector3.Transform(lChildFore, upperWorld * attachRelFore);
            var foreAim = RigAim.FromTo(fromFore, foreDir) * (upperWorld * attachRelFore);
            var foreWorld = Snx.Quaternion.Normalize(Snx.Quaternion.CreateFromAxisAngle(foreDir, twistRad) * foreAim);

            return new TwoBoneResult(true, upperWorld, foreWorld, MathF.Abs(dist - reach));
        }

        /// <summary>
        /// 空手放鬆垂手（由步態擺臂動態給定 targetWorld 與 poleWorld，完全覆寫原版 FK 鏡射）。
        /// </summary>
        public static TwoBoneResult SolveRelaxedHang(
            in TwoBoneIkMath.TwoBoneIkChain chain,
            Snx.Vector3 shoulderPos,
            Snx.Vector3 targetWorld,
            Snx.Vector3 poleWorld,
            Snx.Vector3 chestPos,
            float chestRadius,
            Snx.Quaternion parentWorldUpper)
        {
            if (chain.L1 <= 1e-4f || chain.L2 <= 1e-4f) return default;

            var dirRaw = targetWorld - shoulderPos;
            float dist = dirRaw.Length();
            if (dist < TwoBoneIkMath.MinDirEps) return default;
            var dir = dirRaw / dist;

            float minReach = MathF.Max(MathF.Abs(chain.L1 - chain.L2) + 0.005f, 0.02f);
            float maxReach = (chain.L1 + chain.L2) * 0.9998f;
            float reach = Math.Clamp(dist, minReach, maxReach);

            var nRaw = Snx.Vector3.Cross(dir, poleWorld);
            var n = nRaw.LengthSquared() < TwoBoneIkMath.PoleMinCross * TwoBoneIkMath.PoleMinCross
                ? RigAim.Perpendicular(dir)
                : Snx.Vector3.Normalize(nRaw);

            float cosT1 = (chain.L1 * chain.L1 + reach * reach - chain.L2 * chain.L2) / (2f * chain.L1 * reach);
            float theta1 = MathF.Acos(Math.Clamp(cosT1, -1f, 1f));
            var upperDir = Snx.Vector3.Normalize(
                Snx.Vector3.Transform(dir, Snx.Quaternion.CreateFromAxisAngle(n, theta1)));

            var elbowPos = shoulderPos + upperDir * chain.L1;
            var clampedEnd = shoulderPos + dir * reach;
            ApplyChestSdf(shoulderPos, chain.L1, chestPos, chestRadius, ref elbowPos, ref clampedEnd, chain.L2);

            upperDir = Snx.Vector3.Normalize(elbowPos - shoulderPos);
            var foreRaw = clampedEnd - elbowPos;
            if (foreRaw.LengthSquared() < 1e-8f) return default;
            var foreDir = Snx.Vector3.Normalize(foreRaw);

            var lChildUpper = Snx.Vector3.Transform(chain.DirCUpper, Snx.Quaternion.Inverse(chain.InitRotUpper));
            var fromUpper = Snx.Vector3.Transform(lChildUpper, parentWorldUpper);
            var upperWorld = Snx.Quaternion.Normalize(RigAim.FromTo(fromUpper, upperDir) * parentWorldUpper);

            var lChildFore = Snx.Vector3.Transform(chain.DirCFore, Snx.Quaternion.Inverse(chain.InitRotFore));
            var attachRelFore = Snx.Quaternion.Inverse(chain.InitRotUpper) * chain.InitRotFore;
            var fromFore = Snx.Vector3.Transform(lChildFore, upperWorld * attachRelFore);
            var foreWorld = Snx.Quaternion.Normalize(RigAim.FromTo(fromFore, foreDir) * (upperWorld * attachRelFore));

            return new TwoBoneResult(true, upperWorld, foreWorld, 0f);
        }

        private static void PushElbowOutOfChest(
            Snx.Vector3 shoulderPos, Snx.Vector3 dir, Snx.Vector3 endPos,
            Snx.Vector3 chestPos, float chestRadius, ref Snx.Vector3 elbowPos)
        {
            if (chestRadius <= 1e-5f) return;
            if (Snx.Vector3.Distance(elbowPos, chestPos) >= chestRadius) return;

            // 繞 shoulder→end 軸旋轉肘部，嚴格保持 |E−S|=L1 與 |H−E|=L2 不變（手腕目標零偏移）
            float projLen = Snx.Vector3.Dot(elbowPos - shoulderPos, dir);
            var proj = shoulderPos + dir * projLen;
            var radial = elbowPos - proj;
            float r = radial.Length();
            if (r <= 1e-5f) return;

            var toChest = chestPos - proj;
            var cPerp = toChest - dir * Snx.Vector3.Dot(toChest, dir);
            float cLen = cPerp.Length();
            if (cLen <= 1e-5f) return;

            var awayDir = -cPerp / cLen;
            float axialDistSq = MathF.Max(0f, Snx.Vector3.DistanceSquared(chestPos, proj) - cLen * cLen);
            float neededPerpSq = chestRadius * chestRadius - axialDistSq;
            if (neededPerpSq <= 0f) return;

            // |u*r - cPerp|^2 = r^2 + cLen^2 - 2*r*cLen*cos(phi) >= neededPerpSq
            float cosMax = (r * r + cLen * cLen - neededPerpSq) / (2f * r * cLen);
            if (cosMax >= 1f) return;
            var uCur = radial / r;
            if (cosMax <= -1f)
            {
                elbowPos = proj + awayDir * r;
                return;
            }
            var cUnit = cPerp / cLen;
            float curCos = Math.Clamp(Snx.Vector3.Dot(uCur, cUnit), -1f, 1f);
            if (curCos <= cosMax) return;

            var perpOrtho = uCur - cUnit * curCos;
            var orthoUnit = perpOrtho.LengthSquared() > 1e-8f
                ? Snx.Vector3.Normalize(perpOrtho)
                : Snx.Vector3.Normalize(Snx.Vector3.Cross(dir, cUnit));
            float sinPhi = MathF.Sqrt(MathF.Max(0f, 1f - cosMax * cosMax));
            elbowPos = proj + (cUnit * cosMax + orthoUnit * sinPhi) * r;
        }

        private static void ApplyChestSdf(
            Snx.Vector3 shoulderPos, float l1,
            Snx.Vector3 chestPos, float chestRadius,
            ref Snx.Vector3 elbowPos, ref Snx.Vector3 endPos, float l2)
        {
            if (chestRadius <= 1e-5f) return;
            for (int iter = 0; iter < 3; iter++)
            {
                bool pushed = false;
                var dE = elbowPos - chestPos;
                float lenE = dE.Length();
                if (lenE < chestRadius && lenE > 1e-6f)
                {
                    elbowPos = chestPos + (dE / lenE) * chestRadius;
                    var se = elbowPos - shoulderPos;
                    if (se.LengthSquared() > 1e-8f)
                        elbowPos = shoulderPos + Snx.Vector3.Normalize(se) * l1;
                    pushed = true;
                }
                var dH = endPos - chestPos;
                float lenH = dH.Length();
                if (lenH < chestRadius && lenH > 1e-6f)
                {
                    endPos = chestPos + (dH / lenH) * chestRadius;
                    var eh = endPos - elbowPos;
                    if (eh.LengthSquared() > 1e-8f)
                        endPos = elbowPos + Snx.Vector3.Normalize(eh) * l2;
                    pushed = true;
                }
                if (!pushed) break;
            }
        }
    }
}
