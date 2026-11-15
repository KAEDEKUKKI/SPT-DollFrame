using System;
using System.Collections.Generic;
using System.IO;
using System.Text.RegularExpressions;
using GF2;

// Todo 28: every code-side bone constant asserted against the real data, offline.
// The JAPANESE PMX bone-name literals are swept from the contract-bearing sources
// (BoneContract/ModelMetrics/CharacterRig/GameBones) as source text - a quoted
// CJK-starting literal is a bone name the code hard-requires - and unioned with the
// compiled BoneContract rows (SpanGroups pairs, BoneMap, RequiredAnchors) so a contract
// row can never dodge the sweep. GAME-SIDE names are short resolver keys, not JP: every
// BoneContract anchor key must be a key GameBones.cs actually assigns (its g["..."]
// resolver literals; GameBones cannot compile here - EFT types), the 30 digit anchor
// keys must stay the documented digit{f}_{s}{side} pattern produced by
// CharacterRig.AddDigitBones, and every ModelMetrics game-rig span name must exist in the
// real metric dump analysis/eft_skeleton.json. The JP set must ALSO be present in EACH
// packaged PMX bone table (PmxContainer.Read + PmxParser.Parse, the exact plugin parser
// paths) - no whitelist, no "expected missing" entries.
// Success prints exactly `constants: <N> checked, 0 missing` (N = distinct JP literal
// count) under the package-count info line. Any miss prints FAIL lines on stderr and
// exits 2. Failure QA knob: temporarily seeding a bogus historical toe spelling into the
// collected set flips the run to exit 2 (demonstrated and reverted in
// .omo/evidence/task-28-gf2-project-reorg.txt).
internal static class ConstantsCheck
{
    private const string DefaultPackagesDir = "C:\SPT/BepInEx/plugins/kukki.GF2/GF2_Characters";
    private static readonly string[] LiteralSources =
        { "BoneContract.cs", "ModelMetrics.cs", "CharacterRig.cs", "GameBones.cs" };
    private static readonly Regex JpLiteral =
        new Regex(@"""([\u30A0-\u30FF\u4E00-\u9FFF][^""]*)""", RegexOptions.Compiled);
    private static readonly Regex DigitKey = new Regex(@"^digit[1-5]_[1-3][LR]$", RegexOptions.Compiled);

    internal static int Run()
    {
        var fail = new List<string>();

        // ---- collect: JP literals from source text + contract rows as loaded data ----
        var lits = new SortedSet<string>(StringComparer.Ordinal);
        var src = new Dictionary<string, string>(LiteralSources.Length, StringComparer.Ordinal);
        foreach (var f in LiteralSources)
        {
            var p = RepoFile(f);
            if (!File.Exists(p))
            {
                Console.Error.WriteLine($"constants: source not found: {f}");
                return 1;
            }
            src[f] = File.ReadAllText(p);
            foreach (Match m in JpLiteral.Matches(src[f])) lits.Add(m.Groups[1].Value);
        }
        foreach (var row in BoneContract.SpanGroups) { lits.Add(row.pmxA); lits.Add(row.pmxB); }
        foreach (var row in BoneContract.BoneMap) lits.Add(row.pmxBone);
        foreach (var a in BoneContract.RequiredAnchors) lits.Add(a);

        // game anchor keys of the contract (digits are separate palm-built keys)
        var gameKeys = new SortedSet<string>(StringComparer.Ordinal);
        foreach (var row in BoneContract.SpanGroups) { gameKeys.Add(row.anchorL); gameKeys.Add(row.anchorR); }
        foreach (var row in BoneContract.BoneMap)
            if (Array.IndexOf(BoneContract.Digits, row.gameKey) < 0) gameKeys.Add(row.gameKey);

        // ---- (b) game side: anchor keys produced by the GameBones resolver ----
        var produced = new SortedSet<string>(StringComparer.Ordinal);
        foreach (Match m in Regex.Matches(src["GameBones.cs"], @"g\[""([^""]+)""\]"))
            produced.Add(m.Groups[1].Value);
        foreach (var k in gameKeys)
            if (!produced.Contains(k))
                fail.Add($"game anchor '{k}' never assigned by the GameBones resolver");

        // digits: contract data must stay the pattern CharacterRig.AddDigitBones builds from palms
        var digitSet = new HashSet<string>(BoneContract.Digits, StringComparer.Ordinal);
        if (digitSet.Count != 30)
            fail.Add($"digit anchor set holds {digitSet.Count} distinct keys, expected 30");
        foreach (var d in BoneContract.Digits)
            if (!DigitKey.IsMatch(d))
                fail.Add($"digit key '{d}' off the digit{{f}}_{{s}}{{side}} pattern (CharacterRig.AddDigitBones)");
        if (!src["CharacterRig.cs"].Contains("AddDigitBones") || src["CharacterRig.cs"].IndexOf("\"Digit\"", StringComparison.Ordinal) < 0)
            fail.Add("CharacterRig digit producer (AddDigitBones + \"Digit\" child snippet) missing from source");

        // metric-rig dump: every ModelMetrics game-side span name must exist for real
        var rigNames = new SortedSet<string>(StringComparer.Ordinal);
        foreach (Match m in Regex.Matches(src["ModelMetrics.cs"], @"Game[LR]\s*=\s*new\[\]\s*\{([^}]*)\}"))
            foreach (Match n in Regex.Matches(m.Groups[1].Value, @"""([^""]+)"""))
                rigNames.Add(n.Groups[1].Value);
        var pfxM = Regex.Match(src["ModelMetrics.cs"], @"GamePrefix\s*=\s*""([^""]+)""");
        if (rigNames.Count == 0 || !pfxM.Success)
        {
            fail.Add("ModelMetrics game span table / GamePrefix not found in source");
            rigNames.Clear();
        }
        else
        {
            var dumpKeys = ParseDumpKeys(RepoFile(Path.Combine("analysis", "eft_skeleton.json")));
            if (dumpKeys.Count < 79)
                fail.Add($"analysis/eft_skeleton.json parsed {dumpKeys.Count} metric bones (<79)");
            foreach (var n in rigNames)
                if (!dumpKeys.Contains(pfxM.Groups[1].Value + n))
                    fail.Add($"game bone '{pfxM.Groups[1].Value + n}' missing from analysis/eft_skeleton.json");

            // numeric pin: the spine fit's rest pelvis height constant vs the real dump
            float pelvisY = DumpBonePos(RepoFile(Path.Combine("analysis", "eft_skeleton.json")),
                pfxM.Groups[1].Value + "Pelvis");
            if (float.IsNaN(pelvisY))
                fail.Add("analysis/eft_skeleton.json has no pos for 'Base HumanPelvis' (spine-fit rest height pin)");
            else if (MathF.Abs(pelvisY - BoneContract.GamePelvisRestY) > 1e-3f)
                fail.Add($"BoneContract.GamePelvisRestY {BoneContract.GamePelvisRestY} != dump 'Base HumanPelvis'.pos.y {pelvisY}");
        }

        // ---- (a) PMX side: every JP literal in EVERY package bone table ----
        var packagesDir = Environment.GetEnvironmentVariable("GF2_PACKAGES_DIR");
        if (string.IsNullOrEmpty(packagesDir)) packagesDir = DefaultPackagesDir;
        if (!Directory.Exists(packagesDir))
        {
            Console.Error.WriteLine($"constants: packages directory not found: {packagesDir} (override with GF2_PACKAGES_DIR)");
            return 1;
        }
        var files = Directory.GetFiles(packagesDir, "*.gfc");
        Array.Sort(files, StringComparer.Ordinal);
        if (files.Length == 0)
        {
            Console.Error.WriteLine($"constants: no .gfc packages in {packagesDir}");
            return 1;
        }

        var missing = new SortedDictionary<string, List<string>>(StringComparer.Ordinal);
        foreach (var f in files)
        {
            var pkg = Path.GetFileName(f);
            HashSet<string> table;
            try
            {
                using (var fs = File.OpenRead(f))
                {
                    var data = PmxContainer.Read(fs);
                    var model = PmxParser.Parse(data.Pmx);
                    table = new HashSet<string>(StringComparer.Ordinal);
                    foreach (var b in model.Bones)
                        if (!string.IsNullOrEmpty(b.Name)) table.Add(b.Name);
                }
            }
            catch (Exception e)
            {
                fail.Add($"{pkg}: parse exception: {e.Message}");
                continue;
            }
            foreach (var lit in lits)
                if (!table.Contains(lit))
                {
                    if (!missing.TryGetValue(lit, out var pkgs)) missing[lit] = pkgs = new List<string>();
                    pkgs.Add(pkg);
                }
        }
        foreach (var kv in missing)
            fail.Add($"JP bone '{kv.Key}' missing from {kv.Value.Count}/{files.Length} package tables ({string.Join(", ", kv.Value)})");

        if (fail.Count > 0)
        {
            foreach (var line in fail) Console.Error.WriteLine($"FAIL: {line}");
            Console.Error.WriteLine("constants: FAILED (missing bone constants - see FAIL lines)");
            return 2;
        }

        Console.WriteLine($"constants: {files.Length} packages parsed, {lits.Count} JP bone literals, {gameKeys.Count} game anchors, {rigNames.Count} game-side names");
        Console.WriteLine($"constants: {lits.Count} checked, 0 missing");
        return 0;
    }

    // metric-rig dump keys: game bone name -> { ... "pos": [x,y,z] }, the same loader
    // shape FactorsCheck.LoadGameSkeleton pins (>= 79 bones) - names only here
    private static HashSet<string> ParseDumpKeys(string path)
    {
        var rx = new Regex(@"""([^""]+)""\s*:\s*\{[^{}]*?""pos""\s*:\s*\[", RegexOptions.Compiled);
        var keys = new HashSet<string>(StringComparer.Ordinal);
        foreach (Match m in rx.Matches(File.ReadAllText(path))) keys.Add(m.Groups[1].Value);
        return keys;
    }

    // the Y component of one dump bone entry's pos (NaN when the bone is absent)
    private static float DumpBonePos(string path, string boneName)
    {
        var rx = new Regex(Regex.Escape(boneName) + @"""\s*:\s*\{[^{}]*?""pos""\s*:\s*\[\s*([-\d.eE+]+)\s*,\s*([-\d.eE+]+)", RegexOptions.Compiled);
        var m = rx.Match(File.ReadAllText(path));
        return m.Success ? float.Parse(m.Groups[2].Value, System.Globalization.CultureInfo.InvariantCulture) : float.NaN;
    }

    // cwd-relative first (matches running from the repo root), then the compiled
    // binary's repo position (Tests/bin/<cfg>/<tfm> + 4 ups), mirroring the packager
    private static string RepoFile(string relative)
    {
        var p = Path.GetFullPath(Path.Combine(Directory.GetCurrentDirectory(), relative));
        if (File.Exists(p)) return p;
        var pChar = Path.GetFullPath(Path.Combine(Directory.GetCurrentDirectory(), "Character", relative));
        if (File.Exists(pChar)) return pChar;
        var repo = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", relative));
        if (File.Exists(repo)) return repo;
        var repoChar = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "Character", relative));
        if (File.Exists(repoChar)) return repoChar;
        return p;
    }
}
