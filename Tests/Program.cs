using System;

// Shared entry of the Tests/GF2.Verify offline harness: each subcommand pins one slice
// of the plugin's pure math/contract against repo files, with no UnityEngine dependency
// and no game.
//
// Registered subcommands (15 total across 2 categories; 2026-11-14 架構反轉：
// 次要動作（1C Spring／1B 姿態補償）與視角（1D 相機/FP 手臂/Sightline）模塊刪除，
// 對應子命令除名，新增 proportions；0.3.9 臂鏈 IK 新增 ik)：
// 1. Core Invariants & Baseline Geometry (13 subcommands):
//    - `bindpose`      : BindMath coordinate transformations and baseline delta invariants
//    - `factors`       : Model scaling factors and PMX parser coverage across all packages
//    - `constants`     : Bone constants vs PMX tables and EFT game anchors
//    - `proportions`   : Model-side leg/arm chain scale factors vs the frozen proportion table
//    - `zerostretch`   : Independent-rig zero-stretch retargeting simulation over simulated tree
//    - `pelvisdelta`   : Pelvis delta-copy and foot parent-frame delta invariants
//    - `grants`        : Skinned leg D bones and append links parent-first ordering
//    - `vanilla`       : Vanilla (PMC) restore path & body hiding invariants
//    - `shield`        : Visual shield timeout and stray skin protection
//    - `cache`         : PmxAssets LRU cache eviction & warm/cold loading
//    - `smrmask`       : FirstPersonSMRMask head shadow-only rendering & full-body shadow preservation
    //    - `ik`            : TwoBoneIkMath two-bone arm IK invariants (rest-identity/dynamic-track/chest-pole/covariance/clamp/degenerate/finger-delta/zero-stretch/constants)
    //    - `telemetry`     : TelemetryRegistry log contract completeness & pinned prefixes
//
// 2. Modular Open-Source Architecture Pipeline (2 subcommands):
//    - `profile`       : ModelProfile & metrics parsing, anatomical measurements, JSON roundtrip
//    - `split`         : MeshSplitter head/body mesh splitting, boundary normal preservation & UV integrity
internal static class Program
{
    internal static readonly (string name, string description, Func<int> run)[] Commands =
    {
        // --- Core Invariants & Baseline Geometry ---
        ("bindpose", "BindMath coordinate transform & delta invariants", BindMathTest.Run),
        ("factors", "Model scaling factors & PMX parser coverage", FactorsCheck.Run),
        ("constants", "Bone constants vs PMX tables & game anchors", ConstantsCheck.Run),
        ("proportions", "Model-side leg/arm chain scale vs frozen proportion table", ProportionsCheck.Run),
        ("ik", "TwoBoneIkMath two-bone arm IK invariants", ArmIkTest.Run),
        ("zerostretch", "Independent-rig zero-stretch retargeting simulation", ZeroStretchTest.Run),
        ("pelvisdelta", "Pelvis delta-copy & foot parent-frame delta invariants", PelvisDeltaCheck.Run),
        ("grants", "Skinned leg D bones & append links ordering", GrantsCheck.Run),
        ("vanilla", "Vanilla (PMC) restore path & body hiding invariants", GF2.Tests.VanillaOptionTest.Run),
        ("shield", "Visual shield timeout & stray skin protection", GF2.Tests.VisualShieldTest.Run),
        ("cache", "PmxAssets LRU cache eviction & warm/cold loading", GF2.Tests.CacheTest.Run),
        ("smrmask", "FirstPersonSMRMask head shadow-only rendering & full-body shadow preservation", GF2.Tests.FirstPersonSMRMaskTest.Run),
        ("telemetry", "TelemetryRegistry log contract completeness & pinned prefixes", GF2.Tests.TelemetryCheck.Run),

        // --- Modular Open-Source Architecture Pipeline ---
        ("profile", "ModelProfile parsing, anatomical metrics & JSON roundtrip", GF2.Tests.ModelProfileTest.Run),
        ("split", "MeshSplitter head/body splitting, normal preservation & UV integrity", GF2.Tests.MeshSplitTest.Run),
    };

    public static string[] GetCommandNames()
    {
        var names = new string[Commands.Length];
        for (var i = 0; i < Commands.Length; i++) names[i] = Commands[i].name;
        return names;
    }

    private static int Main(string[] args)
    {
        if (args.Length == 0)
        {
            foreach (var (_, _, run) in Commands)
            {
                var rc = run();
                if (rc != 0) return rc;
            }
            return 0;
        }

        if (args[0] == "--help" || args[0] == "-h" || args[0] == "help")
        {
            Console.WriteLine($"GF2.Verify offline test harness ({Commands.Length} subcommands):");
            foreach (var (name, desc, _) in Commands)
            {
                Console.WriteLine($"  {name,-16} {desc}");
            }
            return 0;
        }

        foreach (var (name, _, run) in Commands)
        {
            if (args[0] == name) return run();
        }

        var known = GetCommandNames();
        Console.Error.WriteLine($"GF2.Verify: unknown subcommand '{args[0]}' (expected: {string.Join(", ", known)})");
        return 2;
    }
}
