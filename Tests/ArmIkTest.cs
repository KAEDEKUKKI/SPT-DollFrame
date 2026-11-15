using System;
using System.IO;
using System.Text.RegularExpressions;
using Snx = System.Numerics;
using GF2;

// 0.3.9 臂鏈兩骨 IK harness（重寫：TwoBoneIk 純數學面 Character/Kinematics/TwoBoneIk.cs
// ＋ ArmRest 校準面 Character/Kinematics/ArmRest.cs；8 支二值場景）：
//   1. rest-identity        ：遊戲姿態＝校準（attach）姿態 → 模型上臂/前臂 world == InitRot*
//                            （1e-3 rad，解肢回 authored 標準站姿）、TwistRad 為零、手端落在目標。
//   2. dynamic-track        ：掌三點軌跡（手垂下→臂擺動→舉槍）逐幀端效器落在重定向目標（1e-3）
//                            ＋FrameMap 組成恆等：world∘lChild 軸像＝解方向（target∘lChildFore＝foreDir）。
//   3. pole chest-follow    ：胸腔彎 45°／前傾 30° → poleWorld 隨 chest 旋轉（chest 像與未轉 offset
//                            夾角＝胸腔角），肘側跟隨 chest 像極向量。
//   4. covariance+constancy ：角色剛體轉 57°（全部輸入含 chest pose 同轉）→ world 目標＝R∘未轉解；
//                            60 幀相同輸入 world 旋轉逐位恆定（站立靜止零漂移）。
//   5. pole-clamp-degenerate：近平行 pole（sin<0.12）決定性 fallback（兩次求解逐位一致）＋
//                            unreachable 箝制（ClampCount≥1、肘近直）＋退化（零長 dir／L2=0／
//                            ArmRatio=0 → Ok=false，呼叫端回退 FK 複製）。
//   6. finger-delta         ：M4 十指 delta 映射（FrameMap_b 語義）：零 delta → 模型停在 authored 姿態；
//                            遊戲手指 +40° 繞軸 → 模型 world 恰差 40°（共軛軸）；FrameMap/AxisMap helper 釘點。
//   7. zero-stretch         ：SDF 擠出前後 |E−S|=L1、|H−E|=L2 恆等（肘入球／掌入球兩幾何，≤3 迭代）。
//   8. constants pin        ：TwoBoneIkMath.GameArmChainM == analysis/eft_skeleton.json 之
//                            |LForearm1|+|LForearm2|+|LForearm3|+|LPalm|（計算值 0.54746）。
// 全數走與 Drive 共用的 TwoBoneIkMath/RigAim（同一份公式，無重複實作）。
internal static class ArmIkTest
{
    private const float PosTol = 1e-4f;
    private const float HandTol = 1e-3f;
    private const float RotTol = 1e-3f;

    internal static int Run()
    {
        int fail = 0;
        fail += RestIdentity();
        fail += DynamicTrack();
        fail += PoleChestFollow();
        fail += CovarianceAndConstancy();
        fail += PoleClampDegenerate();
        fail += FingerDelta();
        fail += ZeroStretch();
        fail += ConstantsPin();
        if (fail > 0)
        {
            Console.Error.WriteLine($"ik: FAILED ({fail} failures)");
            return 2;
        }
        Console.WriteLine("ik: OK (8 scenarios: rest-identity/dynamic-track/pole-chest-follow/covariance-constancy/pole-clamp-degenerate/finger-delta/zero-stretch/constants)");
        return 0;
    }

    // ---- helpers -----------------------------------------------------------------------

    private static Snx.Quaternion Q(float yaw, float pitch, float roll)
        => Snx.Quaternion.CreateFromYawPitchRoll(yaw, pitch, roll);

    private static Snx.Quaternion AxisDeg(Snx.Vector3 axis, float deg)
        => Snx.Quaternion.CreateFromAxisAngle(axis, deg * (MathF.PI / 180f));

    private static Snx.Vector3 Unit(Snx.Vector3 v) => v / v.Length();

    // 帶改寫參數的鏈副本（readonly struct 建構式重造；退化面用）
    private static TwoBoneIkMath.TwoBoneIkChain ChainVariant(float l2, float armRatio)
        => new TwoBoneIkMath.TwoBoneIkChain(
            L1: 0.2297f,
            L2: l2,
            ArmRatio: armRatio,
            ChainGamePalm0: Q(0.2f, 0.3f, 0.1f),
            InitRotUpper: Q(0.4f, -0.7f, 0.3f),
            InitRotFore: Q(-0.2f, 0.9f, 0.5f),
            PoleLocalChest: Unit(new Snx.Vector3(0.6f, -1f, -0.4f)),
            DirCUpper: Unit(new Snx.Vector3(0.9f, 0.2f, 0.3f)),
            DirCFore: Unit(new Snx.Vector3(0.8f, -0.1f, 0.5f)));

    // 預設鏈（克萊妲類）：L1=0.2297、L2=0.2475，ArmRatio=(L1+L2)/GameArmChainM≈0.872
    private static TwoBoneIkMath.TwoBoneIkChain Chain()
        => new TwoBoneIkMath.TwoBoneIkChain(
            L1: 0.2297f,
            L2: 0.2475f,
            ArmRatio: (0.2297f + 0.2475f) / TwoBoneIkMath.GameArmChainM,
            ChainGamePalm0: Q(0.2f, 0.3f, 0.1f),
            InitRotUpper: Q(0.4f, -0.7f, 0.3f),
            InitRotFore: Q(-0.2f, 0.9f, 0.5f),
            PoleLocalChest: Unit(new Snx.Vector3(0.6f, -1f, -0.4f)),
            DirCUpper: Unit(new Snx.Vector3(0.9f, 0.2f, 0.3f)),
            DirCFore: Unit(new Snx.Vector3(0.8f, -0.1f, 0.5f)));

    private static float QuatAngleDeg(Snx.Quaternion a, Snx.Quaternion b)
    {
        float denom = a.Length() * b.Length();
        float d = denom > 1e-12f ? MathF.Abs(Snx.Quaternion.Dot(a, b)) / denom : 1f;
        return 2f * MathF.Acos(Math.Clamp(d, -1f, 1f)) * (180f / MathF.PI);
    }

    private static bool HasNaN(TwoBoneIkMath.TwoBoneIkResult r)
        => float.IsNaN(r.UpperWorld.X) || float.IsNaN(r.ForeWorld.X) || float.IsNaN(r.ElbowDir.X)
        || float.IsNaN(r.ForeDir.X) || float.IsNaN(r.ReachUsed) || float.IsNaN(r.TwistRad);

    // pole 側計分：肘方向相對 dir 的平面分量與 pole 平面分量的同側度（>0＝肘朝 pole 側）
    private static float PoleSideDot(Snx.Vector3 dir, Snx.Vector3 pole, Snx.Vector3 elbowDir)
    {
        var poleP = pole - dir * Snx.Vector3.Dot(pole, dir);
        var elbowP = elbowDir - dir * Snx.Vector3.Dot(elbowDir, dir);
        return Snx.Vector3.Dot(Unit(elbowP), Unit(poleP));
    }

    // ---- 1. rest-identity（anti-freeze：遊戲姿態＝校準姿態 → 模型回 authored 站姿）----

    private static int RestIdentity()
    {
        int fail = 0;
        // attach 幾何：dir=+X、chest-local pole=+Y（chest 本幀 identity）、肘彎 θ1（law of cosines 一致）
        var s = new Snx.Vector3(0.12f, 1.32f, 0.02f);
        var dir = new Snx.Vector3(1f, 0f, 0f);
        const float l1 = 0.29651f, l2 = 0.25095f;   // ratio==1 鏈（模型臂長＝遊戲臂鏈 0.54746）
        float reach = 0.50f;
        float cosT = (l1 * l1 + reach * reach - l2 * l2) / (2f * l1 * reach);
        float theta1 = MathF.Acos(Math.Clamp(cosT, -1f, 1f));
        var elbowDirAttach = Snx.Vector3.Transform(dir, AxisDeg(new Snx.Vector3(0f, 0f, 1f), theta1 * (180f / MathF.PI)));
        var elbowAttach = s + elbowDirAttach * l1;
        var palmAttach = s + dir * reach;
        var dForeAttach = Unit(palmAttach - elbowAttach);

        // ratio==1 校準鏈：DirC*＝attach 子方向（「遊戲姿態＝校準姿態」）；ChainGamePalm0＝attach 掌旋轉
        var c = new TwoBoneIkMath.TwoBoneIkChain(
            L1: l1,
            L2: l2,
            ArmRatio: 1f,
            ChainGamePalm0: Q(0.2f, 0.3f, 0.1f),
            InitRotUpper: Q(0.4f, -0.7f, 0.3f),
            InitRotFore: Q(-0.2f, 0.9f, 0.5f),
            PoleLocalChest: new Snx.Vector3(0f, 1f, 0f),
            DirCUpper: elbowDirAttach,
            DirCFore: dForeAttach);

        // 遊戲姿態＝校準姿態：gameUpper＝肩（dir=+X）、掌 delta 為零（gamePalmRotNow = ChainGamePalm0）
        var r = TwoBoneIkMath.Solve(in c, s, s, palmAttach, c.ChainGamePalm0,
            s + new Snx.Vector3(0f, 0.5f, 0f), Snx.Quaternion.Identity, 0.16f, c.InitRotUpper);
        if (!r.Ok) { Console.Error.WriteLine("FAIL ik 1: solve not ok"); return 1; }

        // anti-freeze 釘：模型上臂/前臂 world 逐位回到 InitRot*（authored 標準站姿）
        if (QuatAngleDeg(r.UpperWorld, c.InitRotUpper) > RotTol)
            { Console.Error.WriteLine($"FAIL ik 1: upper not at authored rest ({QuatAngleDeg(r.UpperWorld, c.InitRotUpper):0.####}deg)"); fail++; }
        if (QuatAngleDeg(r.ForeWorld, c.InitRotFore) > RotTol)
            { Console.Error.WriteLine($"FAIL ik 1: fore not at authored rest ({QuatAngleDeg(r.ForeWorld, c.InitRotFore):0.####}deg)"); fail++; }

        // 手端落在重定向目標、掌 delta 為零時扭量為零（十指/手堂 delta 亦為零 → authored 姿態）
        var hand = s + r.ElbowDir * l1 + r.ForeDir * l2;
        var target = s + dir * r.ReachUsed;
        if (Snx.Vector3.Distance(hand, target) > HandTol)
            { Console.Error.WriteLine($"FAIL ik 1: hand {Snx.Vector3.Distance(hand, target):0.######} off target"); fail++; }
        if (MathF.Abs(r.TwistRad) > 1e-4f)
            { Console.Error.WriteLine("FAIL ik 1: nonzero twist at calibration pose"); fail++; }
        return fail;
    }

    // ---- 2. dynamic-track（掌三點軌跡逐幀重定向）----------------------------------------

    private static int DynamicTrack()
    {
        int fail = 0;
        var c = Chain();
        var s = new Snx.Vector3(0.12f, 1.32f, 0.02f);
        var gameUpper = s + new Snx.Vector3(0f, -0.05f, 0.1f);
        var chest = s + new Snx.Vector3(0f, 0.5f, 0f);
        // 三點軌跡：手垂下 → 臂擺動 → 舉槍（每幀遊戲掌錨移動，掌旋轉 delta 由軌跡端旋轉模擬）
        var dirs = new Snx.Vector3[]
        {
            Unit(new Snx.Vector3(0f, -1f, 0.3f)),
            Unit(new Snx.Vector3(0.6f, 0.2f, 0.8f)),
            Unit(new Snx.Vector3(0f, 0.35f, 0.94f)),
        };
        for (int f = 0; f < dirs.Length; f++)
        {
            float raw = 0.535f;
            var gamePalm = gameUpper + dirs[f] * raw;
            var palmRot = AxisDeg(dirs[f], 25f) * c.ChainGamePalm0;   // 掌旋轉跟隨軌跡段
            var r = TwoBoneIkMath.Solve(in c, s, gameUpper, gamePalm, palmRot, chest, Snx.Quaternion.Identity, 0.16f, c.InitRotUpper);
            if (!r.Ok) { Console.Error.WriteLine($"FAIL ik 2 frame {f}: solve not ok"); return 1; }

            // 端效器落在直接瞄準目標（s → gamePalm）
            var target = s + Unit(gamePalm - s) * r.ReachUsed;
            var hand = s + r.ElbowDir * c.L1 + r.ForeDir * c.L2;
            if (Snx.Vector3.Distance(hand, target) > HandTol)
                { Console.Error.WriteLine($"FAIL ik 2 frame {f}: hand {Snx.Vector3.Distance(hand, target):0.######} off retargeted target"); fail++; }

            // FrameMap 組成恆等：world 旋轉作用在 attach 延伸軸（lChild＝C0⁻¹∘DirC）上＝解方向
            // （target∘lChildFore＝foreDir；上臂同式）——模型子軸像逐幀指向 IK 解的肘/掌方向
            var lChildUpper = Snx.Vector3.Transform(c.DirCUpper, Snx.Quaternion.Inverse(c.InitRotUpper));
            var lChildFore = Snx.Vector3.Transform(c.DirCFore, Snx.Quaternion.Inverse(c.InitRotFore));
            var imgUpper = Snx.Vector3.Transform(lChildUpper, r.UpperWorld);
            var imgFore = Snx.Vector3.Transform(lChildFore, r.ForeWorld);
            if (Snx.Vector3.Dot(Unit(imgUpper), r.ElbowDir) < 1f - 1e-4f)
                { Console.Error.WriteLine($"FAIL ik 2 frame {f}: upper child-axis image off elbowDir"); fail++; }
            if (Snx.Vector3.Dot(Unit(imgFore), r.ForeDir) < 1f - 1e-4f)
                { Console.Error.WriteLine($"FAIL ik 2 frame {f}: fore child-axis image off foreDir (FrameMap composition)"); fail++; }

            if (HasNaN(r)) { Console.Error.WriteLine($"FAIL ik 2 frame {f}: NaN"); fail++; }
        }
        return fail;
    }

    // ---- 3. pole chest-follow（胸腔局部 pole 隨胸腔姿態旋轉）---------------------------

    private static int PoleChestFollow()
    {
        int fail = 0;
        var c = Chain();
        var s = new Snx.Vector3(0.12f, 1.32f, 0.02f);
        var gameUpper = s + new Snx.Vector3(0f, -0.05f, 0.1f);
        var gamePalm = s + Unit(new Snx.Vector3(0.05f, -0.25f, 1f)) * 0.40f;
        var chest = s + new Snx.Vector3(0f, 0.5f, 0f);
        var gameDir = Unit(gamePalm - s);

        // 胸腔未轉參考解
        var rStraight = TwoBoneIkMath.Solve(in c, s, gameUpper, gamePalm, c.ChainGamePalm0,
            chest, Snx.Quaternion.Identity, 0.16f, c.InitRotUpper);
        if (!rStraight.Ok) { Console.Error.WriteLine("FAIL ik 3: straight solve not ok"); return 1; }

        // 胸腔彎 45°（繞 Z）／前傾 30°（繞 −X）：poleWorld = Transform(PoleLocalChest, chestRotNow)
        var cases = new (string name, Snx.Quaternion chestRot, bool planeMover)[]
        {
            ("bend45", AxisDeg(new Snx.Vector3(0f, 0f, 1f), 45f), true),
            ("lean30", AxisDeg(new Snx.Vector3(1f, 0f, 0f), -30f), false),
        };
        foreach (var (name, chestRot, planeMover) in cases)
        {
            var poleImg = Snx.Vector3.Transform(c.PoleLocalChest, chestRot);
            // 解析期望：繞單位軸 a 轉 α 時，向量 p 與其旋轉像的夾角滿足
            // cosθ = (p·a)² + cosα·(1 − (p·a)²)（軸向分量保持、平面分量轉 α）——
            // 非旋轉平面內的向量之夾角必小於 α，故不得直接斷言 θ == 胸腔角
            var axis = new Snx.Vector3(chestRot.X, chestRot.Y, chestRot.Z)
                / MathF.Max(1e-6f, (float)Math.Sqrt(chestRot.X * chestRot.X + chestRot.Y * chestRot.Y + chestRot.Z * chestRot.Z));
            float cosRot = Math.Clamp(chestRot.W / (float)Math.Sqrt(chestRot.X * chestRot.X + chestRot.Y * chestRot.Y + chestRot.Z * chestRot.Z + chestRot.W * chestRot.W), -1f, 1f);
            float rotDeg = 2f * MathF.Acos(cosRot) * (180f / MathF.PI);
            float pa = Snx.Vector3.Dot(Unit(c.PoleLocalChest), axis);
            float expectDeg = MathF.Acos(Math.Clamp(pa * pa + CosDeg(rotDeg) * (1f - pa * pa), -1f, 1f)) * (180f / MathF.PI);
            float imgDeg = MathF.Acos(Math.Clamp(Snx.Vector3.Dot(Unit(poleImg), Unit(c.PoleLocalChest)), -1f, 1f)) * (180f / MathF.PI);
            if (imgDeg < 0.5f || MathF.Abs(imgDeg - expectDeg) > 1f)
                { Console.Error.WriteLine($"FAIL ik 3 {name}: chest-image pole offset {imgDeg:0.#}deg != expected {expectDeg:0.#}deg"); fail++; }

            var rBent = TwoBoneIkMath.Solve(in c, s, gameUpper, gamePalm, c.ChainGamePalm0,
                chest, chestRot, 0.16f, c.InitRotUpper);
            if (!rBent.Ok) { Console.Error.WriteLine($"FAIL ik 3 {name}: solve not ok"); return 1; }
            if (PoleSideDot(gameDir, poleImg, rBent.ElbowDir) <= 0f)
                { Console.Error.WriteLine($"FAIL ik 3 {name}: elbow side does not follow chest-image pole"); fail++; }
            // 胸腔旋轉確實改變彎曲平面（僅對真實轉動 bend 平面的情境斷言；lean-about-X 主要
            // 轉動 pole 的軸向分量，bend 平面幾乎不轉屬預期）
            if (planeMover && QuatAngleDeg(rBent.UpperWorld, rStraight.UpperWorld) < 5f)
                { Console.Error.WriteLine($"FAIL ik 3 {name}: chest rot barely moved elbow ({QuatAngleDeg(rBent.UpperWorld, rStraight.UpperWorld):0.#}deg)"); fail++; }
        }
        return fail;
    }

    private static float CosDeg(float deg) => MathF.Cos(deg * (MathF.PI / 180f));

    // ---- 4. rotation covariance（57° yaw）＋ standing constancy（60 frames）-------------

    private static int CovarianceAndConstancy()
    {
        int fail = 0;
        var c = Chain();
        var s = new Snx.Vector3(0.12f, 1.32f, 0.02f);
        var gameUpper = s + new Snx.Vector3(0f, -0.05f, 0.1f);
        var gamePalm = s + Unit(new Snx.Vector3(0.05f, -0.25f, 1f)) * 0.40f;
        var chest = s + new Snx.Vector3(0f, 0.5f, 0f);
        var palmRot = AxisDeg(new Snx.Vector3(0.3f, 0.4f, 0.8f), 20f) * c.ChainGamePalm0;

        var r0 = TwoBoneIkMath.Solve(in c, s, gameUpper, gamePalm, palmRot, chest, Snx.Quaternion.Identity, 0.16f, c.InitRotUpper);
        if (!r0.Ok) { Console.Error.WriteLine("FAIL ik 4: base solve not ok"); return 1; }

        // 站立靜止恆定釘：60 幀完全相同輸入 → world 旋轉逐位不變（疊加/翻面＝失敗）
        for (int frame = 1; frame < 60; frame++)
        {
            var rf = TwoBoneIkMath.Solve(in c, s, gameUpper, gamePalm, palmRot, chest, Snx.Quaternion.Identity, 0.16f, c.InitRotUpper);
            if (QuatAngleDeg(rf.UpperWorld, r0.UpperWorld) > RotTol || QuatAngleDeg(rf.ForeWorld, r0.ForeWorld) > RotTol)
                { Console.Error.WriteLine($"FAIL ik 4: frame {frame} rotation drift while standing still"); fail++; break; }
        }

        // 旋轉協變釘：角色剛體轉 R（父鏈、遊戲錨、chest pose/rot、掌旋轉全轉）
        // → world 目標必須整體剛性跟轉（R∘未轉解），相對軀幹零扭轉——轉動人物自旋＝失敗
        var rotY = AxisDeg(new Snx.Vector3(0f, 1f, 0f), 57f);
        var rRot = TwoBoneIkMath.Solve(in c,
            Snx.Vector3.Transform(s, rotY),
            Snx.Vector3.Transform(gameUpper, rotY),
            Snx.Vector3.Transform(gamePalm, rotY),
            rotY * palmRot,
            Snx.Vector3.Transform(chest, rotY), rotY, 0.16f, rotY * c.InitRotUpper);
        if (!rRot.Ok) { Console.Error.WriteLine("FAIL ik 4: rotated solve not ok"); return 1; }
        float covU = QuatAngleDeg(rRot.UpperWorld, rotY * r0.UpperWorld);
        float covF = QuatAngleDeg(rRot.ForeWorld, rotY * r0.ForeWorld);
        if (covU > RotTol || covF > RotTol)
            { Console.Error.WriteLine($"FAIL ik 4: rotation covariance broken (upper {covU:0.####}deg fore {covF:0.####}deg)"); fail++; }

        // TryGuardChestClip（空手純 FK 鏡像比例防穿模守衛）：
        // 1) 未穿入胸腔球時維持 100% 純 FK 旋轉不變（回 false）
        // 2) 穿入胸腔球時沿球面法線擠出至 chestRadius 並保持段長不變（零拉伸）
        var chestCenter = s + new Snx.Vector3(-0.15f, -0.05f, 0f);
        float chestR = 0.14f;
        var outDir = Unit(new Snx.Vector3(0.5f, -0.8f, 0f));
        if (TwoBoneIkMath.TryGuardChestClip(s, outDir, c.L1, chestCenter, chestR, c.InitRotUpper, out _))
            { Console.Error.WriteLine("FAIL ik 4: TryGuardChestClip modified non-clipping FK target"); fail++; }

        var clipDir = Unit(new Snx.Vector3(-0.8f, -0.3f, 0f));
        var lChild = Snx.Vector3.Transform(c.DirCUpper, Snx.Quaternion.Inverse(c.InitRotUpper));
        var clipRot = RigAim.FromTo(Snx.Vector3.Transform(lChild, c.InitRotUpper), clipDir) * c.InitRotUpper;
        if (!TwoBoneIkMath.TryGuardChestClip(s, clipDir, c.L1, chestCenter, chestR, clipRot, out var guardedRot))
            { Console.Error.WriteLine("FAIL ik 4: TryGuardChestClip missed clipping elbow"); fail++; }
        else
        {
            var guardedDir = Unit(Snx.Vector3.Transform(lChild, guardedRot));
            var pushedElbow = s + guardedDir * c.L1;
            if (MathF.Abs(Snx.Vector3.Distance(pushedElbow, chestCenter) - chestR) > PosTol)
                { Console.Error.WriteLine("FAIL ik 4: TryGuardChestClip did not land on chestRadius"); fail++; }
            if (MathF.Abs(Snx.Vector3.Distance(pushedElbow, s) - c.L1) > PosTol)
                { Console.Error.WriteLine("FAIL ik 4: TryGuardChestClip broke zero-stretch segment length"); fail++; }
        }
        return fail;
    }

    // ---- 5. pole-clamp-degenerate（近平行 fallback＋unreachable 箝制＋退化）-------------

    private static int PoleClampDegenerate()
    {
        int fail = 0;
        var c = Chain();
        var s = new Snx.Vector3(0.12f, 1.32f, 0.02f);
        var gameUpper = s + new Snx.Vector3(0f, -0.05f, 0.1f);
        var gamePalm = s + Unit(new Snx.Vector3(0.05f, -0.25f, 1f)) * 0.40f;
        var gameDir = Unit(gamePalm - s);
        var chest = s + new Snx.Vector3(0f, 0.5f, 0f);

        // pole ∥ dir（chest 旋轉把 chest-local pole 轉到與 dir 同向）：決定性 fallback，兩次求解逐位一致
        var chestRotParallel = RigAim.FromTo(c.PoleLocalChest, gameDir);
        var rP1 = TwoBoneIkMath.Solve(in c, s, gameUpper, gamePalm, c.ChainGamePalm0, chest, chestRotParallel, 0.16f, c.InitRotUpper);
        var rP2 = TwoBoneIkMath.Solve(in c, s, gameUpper, gamePalm, c.ChainGamePalm0, chest, chestRotParallel, 0.16f, c.InitRotUpper);
        if (!rP1.Ok) { Console.Error.WriteLine("FAIL ik 5: parallel-pole solve not ok"); return 1; }
        if (!AreExactlyEqual(rP1, rP2))
            { Console.Error.WriteLine("FAIL ik 5: parallel pole not deterministic"); fail++; }
        var eP = s + rP1.ElbowDir * c.L1;
        if (MathF.Abs(Snx.Vector3.Distance(eP, s) - c.L1) > PosTol || HasNaN(rP1))
            { Console.Error.WriteLine("FAIL ik 5: |E-S| != L1 / NaN with fallback axis"); fail++; }

        // 近平行 pole（與 dir 夾角 sin≈0.05<PoleMinCross=0.12）：決定性 fallback（無翻面），兩次求解逐位一致
        var nearDir = Unit(gameDir + Unit(Snx.Vector3.Cross(gameDir, new Snx.Vector3(0f, 1f, 0f))) * 0.05f);
        var chestRotNear = RigAim.FromTo(c.PoleLocalChest, nearDir);
        var rNear1 = TwoBoneIkMath.Solve(in c, s, gameUpper, gamePalm, c.ChainGamePalm0, chest, chestRotNear, 0.16f, c.InitRotUpper);
        var rNear2 = TwoBoneIkMath.Solve(in c, s, gameUpper, gamePalm, c.ChainGamePalm0, chest, chestRotNear, 0.16f, c.InitRotUpper);
        if (!rNear1.Ok || !rNear2.Ok) { Console.Error.WriteLine("FAIL ik 5: near-parallel pole solve not ok"); return 1; }
        if (QuatAngleDeg(rNear1.UpperWorld, rNear2.UpperWorld) > 1e-3f || QuatAngleDeg(rNear1.ForeWorld, rNear2.ForeWorld) > 1e-3f)
            { Console.Error.WriteLine("FAIL ik 5: near-parallel pole not deterministic (plane flip)"); fail++; }
        var eNear = s + rNear1.ElbowDir * c.L1;
        if (MathF.Abs(Snx.Vector3.Distance(eNear, s) - c.L1) > PosTol || HasNaN(rNear1))
            { Console.Error.WriteLine("FAIL ik 5: near-parallel pole invariant/NaN"); fail++; }

        // unreachable：reach 1.6× 鏈長（raw reach = 0.848）→ 箝制至 (L1+L2)×0.9995、ClampCount≥1、肘近直
        float gameReach = 1.6f * (c.L1 + c.L2) / c.ArmRatio;
        var gamePalmFar = s + gameDir * gameReach;
        var rU = TwoBoneIkMath.Solve(in c, s, gameUpper, gamePalmFar, c.ChainGamePalm0, chest, Snx.Quaternion.Identity, 0.16f, c.InitRotUpper);
        if (!rU.Ok) { Console.Error.WriteLine("FAIL ik 5: unreachable solve not ok"); return 1; }
        float maxReach = (c.L1 + c.L2) * 0.9995f;
        if (MathF.Abs(rU.ReachUsed - maxReach) > PosTol || rU.ClampCount < 1)
            { Console.Error.WriteLine($"FAIL ik 5: reach {rU.ReachUsed:0.######} not clamped to {maxReach:0.######} (ClampCount={rU.ClampCount})"); fail++; }
        float bendDeg = MathF.Acos(Math.Clamp(Snx.Vector3.Dot(rU.ElbowDir, gameDir), -1f, 1f)) * (180f / MathF.PI);
        if (bendDeg > 2f) { Console.Error.WriteLine($"FAIL ik 5: elbow bend {bendDeg:0.#}deg not near-straight"); fail++; }
        if (HasNaN(rU)) { Console.Error.WriteLine("FAIL ik 5: NaN on unreachable"); fail++; }

        // 退化：零長 dir／L2=0／ArmRatio=0 → Ok=false（呼叫端回退既有 FK 複製路徑）
        var g1 = TwoBoneIkMath.Solve(in c, s, s, s, c.ChainGamePalm0, chest, Snx.Quaternion.Identity, 0.16f, c.InitRotUpper);
        if (g1.Ok) { Console.Error.WriteLine("FAIL ik 5: zero-length dir must fail"); fail++; }
        var c2 = ChainVariant(0f, (0.2297f + 0.001f) / TwoBoneIkMath.GameArmChainM);
        var g2 = TwoBoneIkMath.Solve(in c2, s, s + new Snx.Vector3(0.1f, 0f, 0.3f), s + new Snx.Vector3(0.1f, 0f, 0.8f), c2.ChainGamePalm0, chest, Snx.Quaternion.Identity, 0.16f, c2.InitRotUpper);
        if (g2.Ok) { Console.Error.WriteLine("FAIL ik 5: L2=0 must fail"); fail++; }
        var c3 = ChainVariant(0.2475f, 0f);
        var g3 = TwoBoneIkMath.Solve(in c3, s, s + new Snx.Vector3(0.1f, 0f, 0.3f), s + new Snx.Vector3(0.1f, 0f, 0.8f), c3.ChainGamePalm0, chest, Snx.Quaternion.Identity, 0.16f, c3.InitRotUpper);
        if (g3.Ok) { Console.Error.WriteLine("FAIL ik 5: ArmRatio=0 must fail"); fail++; }
        return fail;
    }

    // readonly struct 逐欄位相等（決定性 fallback 的逐位釘）
    private static bool AreExactlyEqual(TwoBoneIkMath.TwoBoneIkResult a, TwoBoneIkMath.TwoBoneIkResult b)
        => a.UpperWorld.X == b.UpperWorld.X && a.UpperWorld.Y == b.UpperWorld.Y
        && a.UpperWorld.Z == b.UpperWorld.Z && a.UpperWorld.W == b.UpperWorld.W
        && a.ForeWorld.X == b.ForeWorld.X && a.ForeWorld.Y == b.ForeWorld.Y
        && a.ForeWorld.Z == b.ForeWorld.Z && a.ForeWorld.W == b.ForeWorld.W
        && a.ReachUsed == b.ReachUsed && a.TwistRad == b.TwistRad
        && a.ClampCount == b.ClampCount && a.SdfIters == b.SdfIters;

    // ---- 6. finger-delta（M4 十指/手腕 delta 映射＝FrameMap_b 語義）----------------------

    private static int FingerDelta()
    {
        int fail = 0;
        // 單骨情境：模型手指 attach 世界旋轉 C0（authored）、遊戲手指 attach 世界旋轉 G0；
        // 生產權威＝RigAim.CopyTarget（Drive 十指/手腕 baked copy 實路徑）＝G_n∘(G0⁻¹∘C0)。
        var c0 = Q(0.4f, 1.1f, -0.3f);
        var g0 = Q(-0.6f, 0.2f, 0.5f);
        var rOff = Snx.Quaternion.Normalize(Snx.Quaternion.Inverse(g0) * c0);

        // 零 delta：遊戲手指保持 attach 姿態 → 模型手指停在 authored 姿態
        //（CopyTarget 實路徑的 Normalize 帶 ~0.04° float 殘量，容差 0.1°＝機器精度帶）
        var target0 = RigAim.CopyTarget(g0, rOff);
        if (QuatAngleDeg(target0, c0) > 0.1f)
            { Console.Error.WriteLine($"FAIL ik 6: zero delta moved model finger {QuatAngleDeg(target0, c0):0.####}deg"); fail++; }

        // +40° 繞其軸的遊戲手指 delta：G_n = AxisAngle(axis, 40°)∘G0 → 模型 world 恰差 40°（共軛軸）
        var axis = Unit(new Snx.Vector3(0.2f, 0.9f, -0.4f));
        var gn = AxisDeg(axis, 40f) * g0;
        var targetN = RigAim.CopyTarget(gn, rOff);
        float gotDeg = QuatAngleDeg(targetN, c0);
        if (MathF.Abs(gotDeg - 40f) > 0.5f)
            { Console.Error.WriteLine($"FAIL ik 6: 40deg game delta produced {gotDeg:0.#}deg model delta"); fail++; }

        // FrameMap 钉點：FrameMap(C0, G0)∘G0 ＝ C0（遊戲幀差共軛回模型 attach 標定）
        var fm = TwoBoneIkMath.FrameMap(c0, g0);
        var probe = Snx.Vector3.Transform(new Snx.Vector3(0.3f, -0.7f, 0.5f), fm * g0);
        var probeC = Snx.Vector3.Transform(new Snx.Vector3(0.3f, -0.7f, 0.5f), c0);
        if (Snx.Vector3.Distance(probe, probeC) > 1e-4f)
            { Console.Error.WriteLine("FAIL ik 6: FrameMap∘G0 != C0"); fail++; }

        // AxisMap 钉點：attach 幀軸向映射 G_axis0 → M_axis0
        var mAxis = Unit(new Snx.Vector3(0.9f, 0.2f, 0.3f));
        var gAxis = Unit(new Snx.Vector3(0.8f, -0.1f, 0.5f));
        var am = TwoBoneIkMath.AxisMap(gAxis, mAxis);
        var mapped = Snx.Vector3.Transform(gAxis, am);
        if (Snx.Vector3.Distance(Unit(mapped), mAxis) > 1e-4f)
            { Console.Error.WriteLine("FAIL ik 6: AxisMap does not map game axis onto model axis"); fail++; }
        return fail;
    }

    // ---- 7. zero-stretch（SDF 擠出前後鏈長恆等）-----------------------------------------

    private static int ZeroStretch()
    {
        int fail = 0;
        var c = Chain();
        var s = new Snx.Vector3(0.12f, 1.32f, 0.02f);
        // 肘入球幾何：dir=+X、chest-local pole=+Y、胸腔球含原始肘位（一次肘擠出＋可解析收斂）
        var geoms = new (string name, Snx.Vector3 gamePalm, Snx.Vector3 chest, float r)[]
        {
            ("elbow-push", s + new Snx.Vector3(0.535f, 0f, 0f), s + new Snx.Vector3(0.25f, 0f, 0f), 0.14f),
            ("hand-push", s + new Snx.Vector3(0.535f, 0f, 0f), s + new Snx.Vector3(0.42f, 0f, 0f), 0.14f),
        };
        foreach (var (name, gamePalm, chest, radius) in geoms)
        {
            // chest-local pole (0,1,0) + chest identity ⇒ poleWorld = (0,1,0)
            var cG = new TwoBoneIkMath.TwoBoneIkChain(
                L1: c.L1, L2: c.L2, ArmRatio: c.ArmRatio,
                ChainGamePalm0: c.ChainGamePalm0, InitRotUpper: c.InitRotUpper, InitRotFore: c.InitRotFore,
                PoleLocalChest: new Snx.Vector3(0f, 1f, 0f),
                DirCUpper: c.DirCUpper, DirCFore: c.DirCFore);
            var r = TwoBoneIkMath.Solve(in cG, s, s, gamePalm, cG.ChainGamePalm0, chest, Snx.Quaternion.Identity, radius, cG.InitRotUpper);
            if (!r.Ok) { Console.Error.WriteLine($"FAIL ik 7 {name}: solve not ok"); return 1; }
            var elbow = s + r.ElbowDir * c.L1;
            var hand = elbow + r.ForeDir * c.L2;
            if (MathF.Abs(Snx.Vector3.Distance(elbow, s) - c.L1) > PosTol)
                { Console.Error.WriteLine($"FAIL ik 7 {name}: |E-S| != L1 after SDF push"); fail++; }
            if (MathF.Abs(Snx.Vector3.Distance(hand, elbow) - c.L2) > PosTol)
                { Console.Error.WriteLine($"FAIL ik 7 {name}: |H-E| != L2 after SDF push"); fail++; }
            if (r.SdfIters < 1 || r.SdfIters > 3)
                { Console.Error.WriteLine($"FAIL ik 7 {name}: SdfIters={r.SdfIters}"); fail++; }
            if (!r.SdfRejected)
                { Console.Error.WriteLine($"FAIL ik 7 {name}: SdfRejected not set"); fail++; }
            if (HasNaN(r)) { Console.Error.WriteLine($"FAIL ik 7 {name}: NaN in result"); fail++; }
        }
        return fail;
    }

    // ---- 8. constants pin ---------------------------------------------------------------

    private static int ConstantsPin()
    {
        var path = RepoFile(Path.Combine("analysis", "eft_skeleton.json"));
        if (!File.Exists(path))
        {
            Console.Error.WriteLine($"FAIL ik constants: analysis/eft_skeleton.json not found: {path}");
            return 1;
        }
        var text = File.ReadAllText(path);
        float sum = Length(text, "Base HumanLForearm1")
                  + Length(text, "Base HumanLForearm2")
                  + Length(text, "Base HumanLForearm3")
                  + Length(text, "Base HumanLPalm");
        if (MathF.Abs(sum - TwoBoneIkMath.GameArmChainM) > 1e-3f)
        {
            Console.Error.WriteLine($"FAIL ik constants: GameArmChainM {TwoBoneIkMath.GameArmChainM:0.#####} != dump arm chain {sum:0.#####}");
            return 1;
        }
        return 0;
    }

    // dump 骨 pos 向量長（analysis/eft_skeleton.json；姿態靜止時即段長）
    private static float Length(string text, string bone)
    {
        var rx = new Regex(Regex.Escape(bone) + @"""\s*:\s*\{[^{}]*?""pos""\s*:\s*\[\s*([-\d.eE+]+)\s*,\s*([-\d.eE+]+)\s*,\s*([-\d.eE+]+)", RegexOptions.Compiled);
        var m = rx.Match(text);
        if (!m.Success) throw new InvalidOperationException($"ik constants: no pos for '{bone}' in analysis/eft_skeleton.json");
        float x = float.Parse(m.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture);
        float y = float.Parse(m.Groups[2].Value, System.Globalization.CultureInfo.InvariantCulture);
        float z = float.Parse(m.Groups[3].Value, System.Globalization.CultureInfo.InvariantCulture);
        return MathF.Sqrt(x * x + y * y + z * z);
    }

    // cwd 起算（repo 根執行），其次 binary 站位（Tests/bin/<cfg>/<tfm> + 4 層上溯）
    private static string RepoFile(string relative)
    {
        var p = Path.GetFullPath(Path.Combine(Directory.GetCurrentDirectory(), relative));
        if (File.Exists(p)) return p;
        var repo = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", relative));
        return repo;
    }
}
