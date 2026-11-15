// Character/Kinematics/TwoBoneIk.cs: 兩骨 IK 純數學（2026-11-14-9 之 0.3.9 內部重寫，版面依
// 4 模塊分工：M3 本檔＝求解權威 TwoBoneIkMath，取代退役的 Kinematics/ArmIk）——
// 上臂/前臂由兩骨 IK 求解，手端自然伸展至遊戲手掌錨（目標＝遊戲肩→掌方向 × gameReach×ArmRatio，
// 模型臂短即以模型臂長比收短伸距），握把對位回復；每幀只產出 world 旋轉
// （Drive 經 RigAim.LocalFromWorld 寫 localRotation），零 localPosition/localScale 幀寫。
// 0.3.9 重寫差異：pole 改記 **chest 局部空間**（PoleLocalChest＝Build 捕捉、每幀經 chestRotNow
// 升回世界——胸腔彎曲/前傾時彎曲平面跟隨胸腔，不再錨定 rig 根）；胸腔 SDF 半徑 Build 期由
// ArmRest 收緊（clamp(0.6×|chest→上臂 attach|＋0.02, 0.09, 0.18)，缺量測 0.13）。
// 純 System.Numerics（無 Unity 型別），雙編譯進 Tests（DOLLFRAME_PMX_PARSER_ONLY）——zerostretch 同款共用。
using System;
using Snx = System.Numerics;

namespace DollFrame
{
    /// <summary>
    /// 兩骨 IK 純數學權威（雙編譯）。遊戲跨距常數來自 analysis/eft_skeleton.json
    /// （pristine FBAK 基線，凍結）：臂鏈（shoulder→palm）＝
    /// |LForearm1|+|LForearm2|+|LForearm3|+|LPalm| = 0.29651+0.08365+0.08365+0.08365 = 0.54746
    /// （|LUpperarm| 0.13337 為 clavicle→shoulder 偏移，不屬臂鏈）。
    /// </summary>
    public static class TwoBoneIkMath
    {
        /// <summary>遊戲臂鏈長（m；|LForearm1|+|LForearm2|+|LForearm3|+|LPalm|，harness `ik` constants pin 對本值重現）。</summary>
        internal const float GameArmChainM = 0.54746f;

        /// <summary>目標方向退化帶：|gamePalm−gameUpper| 低於此值時求解失敗（呼叫端回退 FK 複製）。</summary>
        internal const float MinDirEps = 1e-3f;

        /// <summary>bend 平面退化帶：sin(dir↔pole 夾角) 低於此值（≈7°）時彎曲平面改走決定性 fallback
        /// （RigAim.Perpendicular(dir)）， 杜絕近退化 normalize 的 1/|cross| 方向放大（逐幀翻面＝自轉）。</summary>
        internal const float PoleMinCross = 0.12f;

        private const float SdfEps = 1e-6f;

        /// <summary>
        /// 單側臂鏈 Build 捕捉常數（M3；＝退役 ArmIk.ArmChain 的 M1 改名重列，Unity 側包裝為
        /// ArmRest.cs 的 ArmRestChain；全部 attach 世界座標、post uniform 縮放後量測／捕捉）：
        /// L1＝模型 |左腕→左ひじ|、L2＝模型 |左ひじ→左手首|、ArmRatio＝(L1+L2)/GameArmChainM、
        /// ChainGamePalm0＝遊戲掌錨 attach 旋轉（扭量基準；＝舊 C0WristGame）、
        /// InitRotUpper/InitRotFore＝上臂/前臂 attach 世界旋轉（＝舊 C0Upper/C0Fore）、
        /// PoleLocalChest＝**上半身2 chest 局部空間**的肘 pole 方向（每幀 Transform 升世界；
        /// Build 由 rig 根空間規約 pole 經 chest attach 世界旋轉反變換捕捉）、
        /// DirC*/＝兩面骨的 attach 子方向（＝DriveEntry.DirC，aim 組成與 LocalFromWorld 消費面一致）。
        /// </summary>
        public readonly struct TwoBoneIkChain
        {
            public readonly float L1, L2, ArmRatio;
            public readonly Snx.Quaternion ChainGamePalm0, InitRotUpper, InitRotFore;
            public readonly Snx.Vector3 PoleLocalChest, DirCUpper, DirCFore;

            public TwoBoneIkChain(float L1, float L2, float ArmRatio,
                Snx.Quaternion ChainGamePalm0, Snx.Quaternion InitRotUpper, Snx.Quaternion InitRotFore,
                Snx.Vector3 PoleLocalChest, Snx.Vector3 DirCUpper, Snx.Vector3 DirCFore)
            {
                this.L1 = L1;
                this.L2 = L2;
                this.ArmRatio = ArmRatio;
                this.ChainGamePalm0 = ChainGamePalm0;
                this.InitRotUpper = InitRotUpper;
                this.InitRotFore = InitRotFore;
                this.PoleLocalChest = PoleLocalChest;
                this.DirCUpper = DirCUpper;
                this.DirCFore = DirCFore;
            }
        }

        /// <summary>
        /// 單次兩骨 IK 求解結果：Ok=false＝退化（呼叫端回退既有 baked FK 複製路徑）；
        /// UpperWorld/ForeWorld＝上臂/前臂節點的目標 world 旋轉（Drive 經 LocalFromWorld 落地）；
        /// ElbowDir/ForeDir＝肘/掌方向（單位）；ReachUsed＝箝制後伸距；TwistRad＝前臂承載的扭量
        /// （掌扭量的 50%）；ClampCount＝reach 箝制次數；SdfIters/SdfRejected＝胸腔 SDF 擠出統計。
        /// </summary>
        public readonly struct TwoBoneIkResult
        {
            public readonly bool Ok;
            public readonly Snx.Quaternion UpperWorld, ForeWorld;
            public readonly Snx.Vector3 ElbowDir, ForeDir;
            public readonly float ReachUsed, TwistRad;
            public readonly int ClampCount, SdfIters;
            public readonly bool SdfRejected;

            public TwoBoneIkResult(bool ok, Snx.Quaternion upperWorld, Snx.Quaternion foreWorld,
                Snx.Vector3 elbowDir, Snx.Vector3 foreDir, float reachUsed, float twistRad,
                int clampCount, int sdfIters, bool sdfRejected)
            {
                Ok = ok;
                UpperWorld = upperWorld;
                ForeWorld = foreWorld;
                ElbowDir = elbowDir;
                ForeDir = foreDir;
                ReachUsed = reachUsed;
                TwistRad = twistRad;
                ClampCount = clampCount;
                SdfIters = sdfIters;
                SdfRejected = sdfRejected;
            }
        }

        /// <summary>
        /// 臂鏈兩骨 IK 求解：目標＝shoulder + unit(gamePalm−gameUpper) × (|gamePalm−gameUpper| × ArmRatio)，
        /// reach 箝制 [max(|L1−L2|+0.005, 0.02), (L1+L2)×0.9995]；law of cosines 取肘角（bend 軸
        /// n＝unit(cross(dir, poleWorld))，pole 由 chestRotNow 升世界（chest-local pole），pole 與 dir
        /// 平行時以 RigAim.Perpendicular 決定性 fallback，肘朝 pole 側彎）；胸腔 SDF（≤3 迭代：
        /// 肘入球→沿球面擠出＋重投影 |E−S|=L1，掌入球→同處理）；扭轉重分配＝gamePalm 相對 attach 掌
        /// 繞臂軸扭量的 50% 落前臂（gamePalmRotNow 為具名掌錨本幀世界旋轉）。
        /// 臂角公式（頂點 S 夾角）：cosθ = (L1² + |H−S|² − L2²) / (2·L1·|H−S|)。
        /// 幀純性（0.3.9）：輸入僅＝Build 常數＋本幀遊戲錨＋本幀父鏈世界旋轉（parent 先驅動，
        /// 不含任何被 IK 寫過的臂節點狀態）；localRotation＝parentWorld⁻¹ ∘ targetWorld。
        /// 父鏈錨定：swing/rest 軸錨定 parentWorld ∘ localChildAxis（localChildAxis＝
        /// InitRot⁻¹ ∘ DirC，attach 延伸軸基準），扭量基準沿父鏈像共軛——站立不動旋轉逐位恆定；
        /// 轉動人物（父鏈轉 R）整臂剛性跟轉 R、相對軀幹零自旋。
        /// </summary>
        public static TwoBoneIkResult Solve(in TwoBoneIkChain c, Snx.Vector3 shoulder,
            Snx.Vector3 gameUpper, Snx.Vector3 gamePalm, Snx.Quaternion gamePalmRotNow,
            Snx.Vector3 chestPos, Snx.Quaternion chestRotNow, float chestRadius,
            Snx.Quaternion parentWorldUpper)
        {
            var fail = default(TwoBoneIkResult);
            float L1 = c.L1, L2 = c.L2;
            if (L1 <= SdfEps || L2 <= SdfEps || c.ArmRatio <= SdfEps) return fail;
            if (c.DirCUpper.LengthSquared() < RigAim.DirEps * RigAim.DirEps
                || c.DirCFore.LengthSquared() < RigAim.DirEps * RigAim.DirEps) return fail;

            var dirRaw = gamePalm - shoulder;
            if (dirRaw.LengthSquared() < MinDirEps * MinDirEps) return fail;
            var dir = Snx.Vector3.Normalize(dirRaw);

            // 直接對準遊戲實際掌錨（消除原以 gameUpper 為起點且乘以 ArmRatio 導致的空抓落差）：
            // 當目標在模型臂長內時手掌精準貼合握把；超出臂長時沿 shoulder→gamePalm 直線伸展至極限（零拉伸）。
            float reach = dirRaw.Length();
            int clampCount = 0;
            float minReach = MathF.Max(MathF.Abs(L1 - L2) + 0.005f, 0.02f);
            float maxReach = (L1 + L2) * 0.9995f;
            if (reach < minReach) { reach = minReach; clampCount++; }
            else if (reach > maxReach) { reach = maxReach; clampCount++; }
            var target = shoulder + dir * reach;

            // chest-local pole（0.3.9 重寫）：poleWorld＝本幀胸腔世界旋轉 ∘ Build 捕捉的 chest 局部
            // pole 偏移——胸腔彎曲/前傾時彎曲平面跟隨胸腔。
            var poleWorld = Snx.Vector3.Transform(c.PoleLocalChest, chestRotNow);

            // 兩骨解：cosθ = (L1² + reach² − L2²) / (2·L1·reach)（頂點 S；肘朝 pole 側彎）。
            // 平面退化箝：pole 與 dir 近平行時 cross→0，normalize 後方向被 1/|cross| 放大＝彎曲
            // 平面逐幀劇烈翻面（直立 idle 即觸發「瘋狂自轉」）；|cross| < 0.12 一律走決定性
            // Perpendicular fallback（無滯迴、無時間輸入，兩幀相同輸入逐位相同解）。
            var nRaw = Snx.Vector3.Cross(dir, poleWorld);
            Snx.Vector3 n = nRaw.LengthSquared() < PoleMinCross * PoleMinCross
                ? RigAim.Perpendicular(dir)
                : Snx.Vector3.Normalize(nRaw);
            float cosT = (L1 * L1 + reach * reach - L2 * L2) / (2f * L1 * reach);
            float theta1 = MathF.Acos(Math.Clamp(cosT, -1f, 1f));
            var elbowDir = Snx.Vector3.Transform(dir, Snx.Quaternion.CreateFromAxisAngle(n, theta1));
            var elbow = shoulder + elbowDir * L1;

            // 胸腔 SDF（≤3 擠出迭代）：肘入球→球面擠出＋重投影 |E−S|=L1；掌入球→擠出＋重取 ForeDir
            var foreDir = Snx.Vector3.Zero;
            var hand = Snx.Vector3.Zero;
            int sdfIters = 0;
            for (int i = 0; i < 3; i++)
            {
                bool pushed = false;
                if (Snx.Vector3.Distance(elbow, chestPos) < chestRadius)
                {
                    var away = elbow - chestPos;
                    away = away.LengthSquared() < SdfEps * SdfEps ? shoulder - chestPos : away;
                    elbow = chestPos + Snx.Vector3.Normalize(away) * chestRadius;
                    elbow = shoulder + Snx.Vector3.Normalize(elbow - shoulder) * L1;
                    pushed = true;
                    sdfIters++;
                }
                foreDir = Snx.Vector3.Normalize(target - elbow);
                hand = elbow + foreDir * L2;
                if (Snx.Vector3.Distance(hand, chestPos) < chestRadius)
                {
                    var awayH = hand - chestPos;
                    awayH = awayH.LengthSquared() < SdfEps * SdfEps ? target - chestPos : awayH;
                    hand = chestPos + Snx.Vector3.Normalize(awayH) * chestRadius;
                    foreDir = Snx.Vector3.Normalize(hand - elbow);
                    hand = elbow + foreDir * L2;   // |H−E|＝L2 恆等（不變量優先）
                    pushed = true;
                    sdfIters++;
                }
                if (!pushed) break;
            }
            elbowDir = Snx.Vector3.Normalize(elbow - shoulder);

            // 扭轉重分配（父鏈錨定）：基準掌旋轉＝attach 掌慣例沿本幀父鏈的像
            // parentWorld ∘ InitRotUpper⁻¹ ∘ ChainGamePalm0（只攜父鏈、不含 IK swing）——直立靜止＝
            // ChainGamePalm0 扭量為零；角色剛體轉 R 時基準與 gamePalmRotNow 同為 R 的像，delta 為
            // 共軛式、扭量不變（僅軸隨轉），杜絕「轉動人物→前臂隨朝向累加 roll」。
            // 父鏈錨定（旋轉協變）：swing 的 from 軸＝延伸軸的本幀父鏈像 parentWorld ∘ localChildAxis
            // （localChildAxis＝InitRotUpper⁻¹ ∘ DirCUpper，attach 常數），目標 world＝swing ∘ parentWorld——
            // 直立靜止（parentWorld==InitRotUpper）與 attach 錨定逐位等價；角色轉 R 時 from／解方向同為
            // R 的像 → 目標＝R ∘ 未轉目標，整臂剛性跟轉，相對軀幹零扭轉、零自旋。
            // 前臂父鏈＝上臂解（pmx 實證：左腕捩 33 之 parent＝32 左腕、rig 佈置 identity locals）。
            var lChildUpper = Snx.Vector3.Transform(c.DirCUpper, Snx.Quaternion.Inverse(c.InitRotUpper));
            var fromUpper = Snx.Vector3.Transform(lChildUpper, parentWorldUpper);
            var upperWorld2 = Snx.Quaternion.Normalize(RigAim.FromTo(fromUpper, elbowDir) * parentWorldUpper);
            var chainImagePalm = parentWorldUpper * Snx.Quaternion.Inverse(c.InitRotUpper) * c.ChainGamePalm0;
            var palmDelta = gamePalmRotNow * Snx.Quaternion.Inverse(chainImagePalm);
            float twistRad = 0.5f * TwistAngle(palmDelta, foreDir);
            var lChildFore = Snx.Vector3.Transform(c.DirCFore, Snx.Quaternion.Inverse(c.InitRotFore));
            // 前臂 rest 慣例以 attach 相對幀（InitRotUpper⁻¹∘InitRotFore）重錨本幀父鏈像：attach 時
            // 逐位等價舊式，角色轉 R 時 fromFore＝R∘DirCFore（協變）、foreAim＝R ∘ 舊式 foreAim。
            var attachRelFore = Snx.Quaternion.Inverse(c.InitRotUpper) * c.InitRotFore;
            var fromFore = Snx.Vector3.Transform(lChildFore, upperWorld2 * attachRelFore);
            // foreAim 必須錨定 parent∘attachRel（與 fromFore 同基準）：target∘lChildFore＝foreDir 恆等式。
            var foreAim = RigAim.FromTo(fromFore, foreDir) * (upperWorld2 * attachRelFore);
            var foreWorld = Snx.Quaternion.Normalize(Snx.Quaternion.CreateFromAxisAngle(foreDir, twistRad) * foreAim);

            return new TwoBoneIkResult(true, upperWorld2, foreWorld, elbowDir, foreDir,
                reach, twistRad, clampCount, sdfIters, sdfIters > 0);
        }

        /// <summary>
        /// 比例防穿模守衛（純鏡像空手態用）：檢查純 FK 鏡像後的臂段端點（肘或腕）是否因
        /// 模型肩寬／胸厚比例差異而穿入胸腔 SDF 球（chestPos, chestRadius）。
        /// 未穿入時回 false 並原樣保留 worldTarget（100% 純 FK 鏡像）；
        /// 穿入時以兩球面交線解析解（|P−S|=segLen 與 |P−C|=chestRadius）將方向最小角度
        /// 擺正至球面外緣（零拉伸、零迭代振盪）。
        /// </summary>
        public static bool TryGuardChestClip(Snx.Vector3 segmentOrigin, Snx.Vector3 childDir, float segLen,
            Snx.Vector3 chestPos, float chestRadius, Snx.Quaternion worldTarget, out Snx.Quaternion guardedWorld)
        {
            guardedWorld = worldTarget;
            if (segLen <= SdfEps || chestRadius <= SdfEps || childDir.LengthSquared() < RigAim.DirEps * RigAim.DirEps)
                return false;

            var unitDir = Snx.Vector3.Normalize(childDir);
            var endPos = segmentOrigin + unitDir * segLen;
            if (Snx.Vector3.Distance(endPos, chestPos) >= chestRadius)
                return false;

            var toChest = chestPos - segmentOrigin;
            float dSC = toChest.Length();
            if (dSC < SdfEps) return false;
            var uSC = toChest / dSC;

            // 兩球面交線：|S + u*L - C|^2 >= R^2  <=>  dot(u, uSC) <= (L^2 + dSC^2 - R^2) / (2*L*dSC)
            float cosMax = (segLen * segLen + dSC * dSC - chestRadius * chestRadius) / (2f * segLen * dSC);
            if (cosMax <= -1f) return false; // 整顆 segLen 球都在胸腔內，無解則保持原樣
            cosMax = Math.Clamp(cosMax, -1f, 1f);
            float sinMax = MathF.Sqrt(MathF.Max(0f, 1f - cosMax * cosMax));

            var perpRaw = unitDir - uSC * Snx.Vector3.Dot(unitDir, uSC);
            var perp = perpRaw.LengthSquared() < SdfEps * SdfEps
                ? RigAim.Perpendicular(uSC)
                : Snx.Vector3.Normalize(perpRaw);
            var newDir = Snx.Vector3.Normalize(uSC * cosMax + perp * sinMax);
            guardedWorld = Snx.Quaternion.Normalize(RigAim.FromTo(unitDir, newDir) * worldTarget);
            return true;
        }

        /// <summary>
        /// 四元數在 unitAxis 軸上的有符扭角（rad，(−π, π]）：w&lt;0 先翻轉 q，再
        /// θ = 2·atan2(dot(vec, axis), w)，出帶纏回 (−π, π]。零旋轉回 0。
        /// </summary>
        public static float TwistAngle(Snx.Quaternion delta, Snx.Vector3 unitAxis)
        {
            if (delta.W < 0f) delta = Snx.Quaternion.Negate(delta);
            float vec = Snx.Vector3.Dot(new Snx.Vector3(delta.X, delta.Y, delta.Z), unitAxis);
            float t = 2f * MathF.Atan2(vec, delta.W);
            if (t > MathF.PI) t -= 2f * MathF.PI;
            else if (t <= -MathF.PI) t += 2f * MathF.PI;
            return t;
        }

        /// <summary>
        /// M1 FrameMap（純 helper，harness/註解消費）：單骨 delta 映射基準
        /// FrameMap_b ＝ M_b⁰ ∘ (G_b⁰)⁻¹（模型 attach 標定 ∘ 遊戲 attach 反變換）——
        /// M4 手腕/十指 delta 映射的語義真值：遊戲幀差 ∘ 模型 attach ＝ 模型幀差。
        /// </summary>
        public static Snx.Quaternion FrameMap(Snx.Quaternion modelAttach, Snx.Quaternion gameAttach)
            => Snx.Quaternion.Normalize(modelAttach * Snx.Quaternion.Inverse(gameAttach));

        /// <summary>
        /// M1 AxisMap（純 helper，harness/註解消費）：attach 幀軸向映射 G_ext₀ → M_ext₀
        /// （RigAim.FromToRotation 語義）。
        /// </summary>
        public static Snx.Quaternion AxisMap(Snx.Vector3 gameAxis0, Snx.Vector3 modelAxis0)
            => RigAim.FromTo(gameAxis0, modelAxis0);
    }
}
