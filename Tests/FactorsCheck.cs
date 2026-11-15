using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text.RegularExpressions;
using UnityEngine;
using GF2;

// Todo 26: pinned fit-factor + parser-coverage check, offline (no Unity, no game).
// Every .gfc package is parsed through the exact plugin paths (PmxContainer.Read +
// PmxParser.Parse under GF2_PMX_PARSER_ONLY, same build shape as the packager) and
// fed to the SHARED ModelMetrics.Compute - the 12-span pair math lives there only,
// never duplicated here. Assertions:
//   1. analysis/fit_expectations.json parses to 12 group pins + unitScale (0.078);
//      克萊妲原皮.gfc reproduces all 12 pinned factors within 1e-3.
//   2. every package: ModelMetrics.Missing == [] (canonical bones) and every group
//      factor inside [0.05, 3.0] - stricter than the packager's strict mode, which
//      skips unmeasured 0-factors; here a lost span (factor 0) must fail.
//   3. parser coverage (todo 12/26): 0 parse exceptions across all packages (22).
// Game spans load from analysis/eft_skeleton.json with the packager's metric-rig dump
// loader (regex over "name": { ... "pos": [x,y,z] }, >= 79 bones).
// Success prints exactly `factors: 12/12 groups OK, 22/22 packages OK` (counts derived
// from the pins and the packages found). Failures print expected/actual/delta on stderr
// and exit 1. Failure QA knob: GF2_EXPECT_DELTA=1 replaces the pinned hipOffset with
// 9.99 so the run must FAIL with `expected 9.99 actual 1.2912...` (invocation and
// output documented in .omo/evidence/task-26-gf2-project-reorg.txt).
internal static class FactorsCheck
{
    private const string PinnedPackage = "克萊妲原皮.gfc";
    private const string DefaultPackagesDir = "C:\SPT/BepInEx/plugins/kukki.GF2/GF2_Characters";
    private const float Tol = 1e-3f;
    private const float MinFactor = 0.05f;
    private const float MaxFactor = 3.0f;
    private const int PinnedGroups = 12;

    internal static int Run()
    {
        var packagesDir = Environment.GetEnvironmentVariable("GF2_PACKAGES_DIR");
        if (string.IsNullOrEmpty(packagesDir)) packagesDir = DefaultPackagesDir;
        if (!Directory.Exists(packagesDir))
        {
            Console.Error.WriteLine($"factors: packages directory not found: {packagesDir} (override with GF2_PACKAGES_DIR)");
            return 1;
        }

        float unitScale;
        Dictionary<string, float> pinned;
        try
        {
            var exp = File.ReadAllText(RepoFile(Path.Combine("analysis", "fit_expectations.json")));
            (unitScale, pinned) = ParseExpectations(exp);
        }
        catch (Exception e)
        {
            Console.Error.WriteLine($"factors: analysis/fit_expectations.json unusable: {e.Message}");
            return 1;
        }
        if (pinned.Count != PinnedGroups)
        {
            Console.Error.WriteLine($"factors: fit_expectations.json carries {pinned.Count} group pins, expected {PinnedGroups}");
            return 1;
        }
        if (DeltaMode())
        {
            pinned["hipOffset"] = 9.99f;
            Console.Error.WriteLine("factors: GF2_EXPECT_DELTA=1 -> pinned hipOffset forced to 9.99 (failure QA)");
        }

        var games = LoadGameSkeleton(RepoFile(Path.Combine("analysis", "eft_skeleton.json")));

        var files = Directory.GetFiles(packagesDir, "*.gfc");
        Array.Sort(files, StringComparer.Ordinal);

        var fail = new List<string>();
        int pinnedOk = 0, pinSeen = 0, pkgOk = 0, parseFails = 0;
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
                parseFails++;
                fail.Add($"{pkg}: parse exception: {e.Message}");
                continue;
            }

            var m = ModelMetrics.Compute(model, unitScale, games);
            var ok = true;
            if (m.Missing.Length > 0)
            {
                ok = false;
                fail.Add($"{pkg}: missing canonical bones: {string.Join(", ", m.Missing)}");
            }
            foreach (var g in ModelMetrics.FactorGroups)
            {
                var v = m.Factors.TryGetValue(g, out var fv) ? fv : 0f;
                if (v >= MinFactor && v <= MaxFactor) continue;
                ok = false;
                fail.Add($"{pkg}: factor {g}={Fmt(v)} outside [0.05, 3.0]");
            }

            if (pkg == PinnedPackage)
            {
                pinSeen = 1;
                foreach (var kv in pinned)
                {
                    var v = m.Factors.TryGetValue(kv.Key, out var fv) ? fv : 0f;
                    var d = v - kv.Value;
                    if (Math.Abs(d) <= Tol) { pinnedOk++; continue; }
                    fail.Add($"{pkg}: {kv.Key} expected {Fmt(kv.Value)} actual {Fmt(v)} delta {Fmt(d)} (tol 1e-3)");
                }
            }
            if (ok) pkgOk++;
        }

        if (pinSeen == 0)
            fail.Add($"{PinnedPackage}: not found among the {files.Length} packages in {packagesDir}");
        if (parseFails != 0)
            fail.Add($"parser coverage: {parseFails} parse exception(s) across {files.Length} packages");

        if (fail.Count > 0)
        {
            foreach (var line in fail) Console.Error.WriteLine($"FAIL: {line}");
            Console.Error.WriteLine($"factors: FAILED (pinned {pinnedOk}/{PinnedGroups}, packages {pkgOk}/{files.Length}, parse exceptions {parseFails})");
            return 1;
        }

        Console.WriteLine($"factors: {pinnedOk}/{PinnedGroups} groups OK, {pkgOk}/{files.Length} packages OK");
        return 0;
    }

    private static bool DeltaMode()
    {
        var v = Environment.GetEnvironmentVariable("GF2_EXPECT_DELTA");
        return !string.IsNullOrEmpty(v) && v != "0";
    }

    private static (float unitScale, Dictionary<string, float> groups) ParseExpectations(string text)
    {
        var us = Regex.Match(text, "\"unitScale\"\\s*:\\s*([-+0-9.eE]+)");
        var gs = Regex.Match(text, "\"groups\"\\s*:\\s*\\{(.*?)\\}");
        if (!us.Success || !gs.Success)
            throw new InvalidDataException("expected schema {\"unitScale\":x,\"groups\":{\"name\":value,...}}");
        var groups = new Dictionary<string, float>(StringComparer.Ordinal);
        foreach (Match m in Regex.Matches(gs.Groups[1].Value, "\"([^\"]+)\"\\s*:\\s*([-+0-9.eE]+)"))
            groups[m.Groups[1].Value] = float.Parse(m.Groups[2].Value, CultureInfo.InvariantCulture);
        if (groups.Count == 0)
            throw new InvalidDataException("no group pins parsed");
        return (float.Parse(us.Groups[1].Value, CultureInfo.InvariantCulture), groups);
    }

    // analysis/eft_skeleton.json metric rig dump: game bone name ("Base Human*") -> pos.
    // Same loader shape as the packager's LoadGameSkeleton (todo 6) - same file, same
    // >= 79 bone completeness gate; ModelMetrics owns the bone-span pairing, not this.
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

    // cwd-relative first (matches running from the repo root), then the compiled
    // binary's repo position (Tests/bin/<cfg>/<tfm> + 4 ups), mirroring the packager.
    private static string RepoFile(string relative)
    {
        var p = Path.GetFullPath(Path.Combine(Directory.GetCurrentDirectory(), relative));
        if (File.Exists(p)) return p;
        var repo = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", relative));
        if (File.Exists(repo)) return repo;
        return p;
    }

    private static string Fmt(float v)
    {
        var s = v.ToString("R", CultureInfo.InvariantCulture);
        return s == "-0" ? "0" : s;
    }
}
