using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text.RegularExpressions;
using UnityEngine;
using GF2;

// `proportions`: model-side uniform-scale gate (2026-11-14-6 leg-anchored uniform redesign). Every
// .gfc package's frozen 12-span factors (FactorGroups, ModelMetrics authority) are folded into the
// ONE whole-model uniform scale ratio k = game leg chain / model leg chain through the shared
// ModelScaleMath (Character/ModelScaler.cs, one math home, pinned here). The arm-chain k gate is
// RETIRED together with the arm-chain scaling mechanism (2026-11-14-6): no pivot scale is applied
// to arms/fingers anymore, so a package's arm ratio can no longer deform anything and survives only
// as frozen proportion data (proportion_scale.json armK). Assertions per package:
//   1. analysis/proportion_scale.json has a legK pin (frozen from the audit chain table),
//      reproduced within 2e-3 — the uniform k IS the legK value;
//   2. raw k within [KLegMin, KLegMax] — the leg-anchored uniform factor band;
//   3. the 克萊妲原皮 pin leg 1.0794 holds within 1e-3, cross-checking FactorsCheck via the
//      model_matrix factor path.
// Packages dir override: GF2_PACKAGES_DIR. Failure QA knob: GF2_SCALE_DELTA=1 replaces
// the 克萊妲原皮 leg pin with 9.99 so the run must FAIL with `expected 9.99`.
internal static class ProportionsCheck
{
    private const string PinnedPackage = "克萊妲原皮.gfc";
    private const string DefaultPackagesDir = "C:\SPT/BepInEx/plugins/kukki.GF2/GF2_Characters";
    private const float Tol = 2e-3f;
    private const float PinnedTol = 1e-3f;

    internal static int Run()
    {
        var packagesDir = Environment.GetEnvironmentVariable("GF2_PACKAGES_DIR");
        if (string.IsNullOrEmpty(packagesDir)) packagesDir = DefaultPackagesDir;
        if (!Directory.Exists(packagesDir))
        {
            Console.Error.WriteLine($"proportions: packages directory not found: {packagesDir} (override with GF2_PACKAGES_DIR)");
            return 1;
        }

        var table = ParseScaleTable(RepoFile(Path.Combine("analysis", "proportion_scale.json")));
        var games = LoadGameSkeleton(RepoFile(Path.Combine("analysis", "eft_skeleton.json")));
        var files = Directory.GetFiles(packagesDir, "*.gfc");
        Array.Sort(files, StringComparer.Ordinal);

        if (DeltaMode())
        {
            table[PinnedPackage] = (9.99f, table[PinnedPackage].Item2);
            Console.Error.WriteLine("proportions: GF2_SCALE_DELTA=1 -> pinned leg K forced to 9.99 (failure QA)");
        }

        var fail = new List<string>();
        int pinnedOk = 0, pinSeen = 0, pkgOk = 0;
        foreach (var f in files)
        {
            var pkg = Path.GetFileName(f);
            PmxParser.PmxModel model;
            try
            {
                using (var fs = File.OpenRead(f))
                {
                    var data = PmxContainer.Read(fs);
                    model = PmxParser.Parse(data.Pmx);
                }
            }
            catch (Exception e)
            {
                fail.Add($"{pkg}: parse exception: {e.Message}");
                continue;
            }

            var m = ModelMetrics.Compute(model, 0.078f, games);
            var plan = ModelScaleMath.Solve(m.Factors);
            if (!plan.HasValue)
            {
                fail.Add($"{pkg}: uniform scale unsolvable (missing thigh/shank/foot factors)");
                continue;
            }

            if (!table.TryGetValue(pkg, out var pin))
            {
                fail.Add($"{pkg}: no legK pin in analysis/proportion_scale.json (regen the frozen table after repack)");
                continue;
            }
            var dL = plan.Value.K - pin.legK;
            if (MathF.Abs(dL) > Tol)
            {
                fail.Add($"{pkg}: uniform k expected {pin.legK:0.####} actual {plan.Value.K:0.####} delta {dL:+0.####;-0.####} (tol 2e-3)");
                continue;
            }

            if (plan.Value.Raw > ModelScaleMath.KLegMax + Tol || plan.Value.Raw < ModelScaleMath.KLegMin - Tol)
            {
                fail.Add($"{pkg}: raw uniform k {plan.Value.Raw:0.####} outside [{ModelScaleMath.KLegMin:0.##}, {ModelScaleMath.KLegMax:0.##}]");
                continue;
            }

            if (pkg == PinnedPackage)
            {
                pinSeen = 1;
                var legRef = Math.Abs(plan.Value.K - pin.legK);
                if (legRef <= PinnedTol) pinnedOk++;
                else fail.Add($"{pkg}: pinned uniform k {pin.legK:0.####} (原始幾何等比 k) not reproduced (delta {legRef:0.####}, tol 1e-3)");
            }
            pkgOk++;
        }

        if (pinSeen == 0)
            fail.Add($"{PinnedPackage}: not found among the {files.Length} packages in {packagesDir}");

        if (fail.Count > 0)
        {
            foreach (var line in fail) Console.Error.WriteLine($"FAIL: {line}");
            Console.Error.WriteLine($"proportions: FAILED (pinned {pinnedOk}/1, packages {pkgOk}/{files.Length})");
            return 1;
        }

        Console.WriteLine($"proportions: leg-anchored uniform k OK, pinned 1/1 OK, {pkgOk}/{files.Length} packages OK");
        return 0;
    }

    /// <summary>Schema: {\"unitScale\":0.078,\"packages\":[{\"pkg\":\"X.gfc\"|\"X\",\"legK\":a,\"armK\":b},...]}.</summary>
    private static Dictionary<string, (float legK, float armK)> ParseScaleTable(string path)
    {
        var text = File.ReadAllText(path);
        var table = new Dictionary<string, (float, float)>(StringComparer.Ordinal);
        var body = Regex.Match(text, "\"packages\"\\s*:\\s*\\[");
        if (!body.Success) throw new InvalidDataException("no packages array in proportion_scale.json");
        foreach (Match m in Regex.Matches(text, @"\{[^{}]*?""pkg""\s*:\s*""([^""]+)""[^{}]*?""legK""\s*:\s*([-+0-9.eE]+)\s*,\s*""armK""\s*:\s*([-+0-9.eE]+)[^{}]*\}"))
        {
            var name = m.Groups[1].Value;
            if (!name.EndsWith(".gfc", StringComparison.Ordinal)) name += ".gfc";
            table[name] = (float.Parse(m.Groups[2].Value, CultureInfo.InvariantCulture),
                           float.Parse(m.Groups[3].Value, CultureInfo.InvariantCulture));
        }
        if (table.Count == 0) throw new InvalidDataException("proportion_scale.json parsed 0 package pins");
        return table;
    }

    private static Dictionary<string, Vector3> LoadGameSkeleton(string path)
    {
        var text = File.ReadAllText(path);
        var rx = new Regex("\"([^\"]+)\"\\s*:\\s*\\{[^{}]*?\"pos\"\\s*:\\s*\\[\\s*([^,\\]]+)\\s*,\\s*([^,\\]]+)\\s*,\\s*([^,\\]]+)\\s*\\]");
        var games = new Dictionary<string, Vector3>(StringComparer.Ordinal);
        foreach (Match match in rx.Matches(text))
            games[match.Groups[1].Value] = new Vector3(
                float.Parse(match.Groups[2].Value, CultureInfo.InvariantCulture),
                float.Parse(match.Groups[3].Value, CultureInfo.InvariantCulture),
                float.Parse(match.Groups[4].Value, CultureInfo.InvariantCulture));
        if (games.Count < 79)
            throw new InvalidDataException($"analysis/eft_skeleton.json parsed {games.Count}/79 metric bones");
        return games;
    }

    private static string RepoFile(string relative)
    {
        var p = Path.GetFullPath(Path.Combine(Directory.GetCurrentDirectory(), relative));
        if (File.Exists(p)) return p;
        var repo = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", relative));
        if (File.Exists(repo)) return repo;
        return p;
    }

    private static bool DeltaMode()
    {
        var v = Environment.GetEnvironmentVariable("GF2_SCALE_DELTA");
        return !string.IsNullOrEmpty(v) && v != "0";
    }
}
