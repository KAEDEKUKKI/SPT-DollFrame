using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using UnityEngine;
using GF2;

internal static class PackagerProgram
{
    private static readonly string[] UnusableMainTex =
        { ".spa", ".sph", ".bmp", ".tga" };

    // default package output: the installed mod's package folder (BepInEx client-mod
    // format plugins/kukki.GF2/ - the plugin probes <dll dir>/GF2_Characters)
    private static string OutDir = "C:\SPT/BepInEx/plugins/kukki.GF2/GF2_Characters";

    // pinned unit scale (analysis/fit_expectations.json); meters per PMX unit
    private const float UnitScale = 0.078f;

    private static int Main(string[] args)
    {
        var inputs = new List<string>();
        string verifyDir = null;
        bool strict = false;
        for (int i = 0; i < args.Length; i++)
        {
            if (args[i] == "-o") { if (i + 1 < args.Length) OutDir = args[i + 1]; i++; continue; }
            if (args[i] == "--verify") { verifyDir = i + 1 < args.Length ? args[i + 1] : null; i++; continue; }
            if (args[i] == "--strict") { strict = true; continue; }
            inputs.Add(args[i]);
        }
        if (verifyDir != null)
        {
            if (!Directory.Exists(verifyDir))
            {
                Console.Error.WriteLine($"verify: directory not found: {verifyDir}");
                return 1;
            }
            return RunVerify(verifyDir, strict);
        }
        if (inputs.Count == 0)
        {
            Console.WriteLine("usage: GF2Packager <pmx-file-or-character-folder ...> [-o <outdir>]");
            Console.WriteLine("       writes <character-name>.gfc (self-contained PMX + textures) and verifies each.");
            Console.WriteLine("       GF2Packager --verify <gfc-dir> [--strict]  builds analysis/model_matrix.json.");
            return 1;
        }
        Directory.CreateDirectory(OutDir);
        int ok = 0, bad = 0;
        foreach (var input in inputs)
        {
            try
            {
                if (Directory.Exists(input))
                    foreach (var pmx in Directory.GetFiles(input, "*.pmx"))
                    { PackageOne(pmx, OutDir); ok++; }
                else if (File.Exists(input) && input.EndsWith(".pmx", StringComparison.OrdinalIgnoreCase))
                { PackageOne(input, OutDir); ok++; }
                else
                    Console.WriteLine($"SKIP {input} (not found or not a .pmx)");
            }
            catch (Exception e)
            {
                bad++;
                Console.Error.WriteLine($"FAIL {input}: {e}");
            }
        }
        Console.WriteLine($"\ndone: {ok} packaged, {bad} failed -> {OutDir}");
        return bad == 0 ? 0 : 2;
    }

    private static void PackageOne(string pmxPath, string outDir)
    {
        pmxPath = Path.GetFullPath(pmxPath);
        var folder = Path.GetDirectoryName(pmxPath);
        var characterName = Path.GetFileName(folder);
        Console.WriteLine($"== {characterName} ({Path.GetFileName(pmxPath)})");

        var pmx = GF2.PmxParser.Parse(pmxPath);

        // resolve every referenced main texture against the character folder by basename
        var byBasename = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var f in Directory.EnumerateFiles(folder, "*", SearchOption.AllDirectories))
            byBasename.TryAdd(Path.GetFileName(f), f);

        var textures = new (string key, byte[] bytes)[0];
        var texList = new List<(string key, byte[] bytes)>();
        var seenTex = new HashSet<int>();
        var misses = new List<string>();
        foreach (var mat in pmx.Mats)
        {
            if (mat.TexIdx < 0 || mat.TexIdx >= pmx.TexPaths.Length || !seenTex.Add(mat.TexIdx)) continue;
            var rel = pmx.TexPaths[mat.TexIdx];
            var lower = rel.ToLowerInvariant();
            if (EndsWithAny(lower, UnusableMainTex) || (!lower.EndsWith(".png") && !lower.EndsWith(".jpg") && !lower.EndsWith(".jpeg")))
                continue;
            if (!byBasename.TryGetValue(Path.GetFileName(rel), out var src))
            {
                misses.Add(rel);
                continue;
            }
            texList.Add((rel, File.ReadAllBytes(src)));
        }
        // baked SDF face-shadow map (unreferenced by PMX materials): packed as an extra
        // texture entry with a reserved unique key; readers are count-driven and stay
        // version-1 compatible. Skip silently when the package has no bake.
        const string SdfKey = "sdf_face.png";
        var sdfPath = Path.Combine(folder!, "sdf_face.png");
        if (File.Exists(sdfPath))
        {
            if (texList.Exists(t => t.key == SdfKey))
                throw new InvalidDataException("texture key collision with sdf_face.png");
            texList.Add((SdfKey, File.ReadAllBytes(sdfPath)));
        }
        textures = texList.ToArray();

        var outFile = Path.Combine(outDir, characterName + ".gfc");
        using (var w = new BinaryWriter(File.Create(outFile)))
        {
            w.Write(Encoding.ASCII.GetBytes(GF2.PmxContainer.Magic));
            w.Write(GF2.PmxContainer.FormatVersion);
            WriteU32Str(w, Encoding.UTF8.GetBytes(characterName));
            WriteU32Str(w, Encoding.UTF8.GetBytes(Path.GetFileName(pmxPath)));
            var pmxBytes = File.ReadAllBytes(pmxPath);
            w.Write(pmxBytes.Length);
            w.Write(pmxBytes);
            w.Write(textures.Length);
            foreach (var t in textures)
            {
                WriteU32Str(w, Encoding.UTF8.GetBytes(t.key));
                w.Write(t.bytes.Length);
                w.Write(t.bytes);
            }
        }

        // verify through the exact code paths the plugin uses
        using (var fs = File.OpenRead(outFile))
        {
            var data = GF2.PmxContainer.Read(fs);
            var repmx = GF2.PmxParser.Parse(data.Pmx);
            if (data.Name != characterName || data.PmxFilename != Path.GetFileName(pmxPath))
                throw new InvalidDataException("readback header mismatch");
            if (repmx.Bones.Length != pmx.Bones.Length || repmx.Pos.Length != pmx.Pos.Length)
                throw new InvalidDataException("readback model mismatch");
            foreach (var t in textures)
                if (!data.Textures.TryGetValue(t.key, out var b) || !b.AsSpan().SequenceEqual(t.bytes))
                    throw new InvalidDataException($"readback texture mismatch: {t.key}");
        }
        var mb = new FileInfo(outFile).Length / (1024f * 1024f);
        Console.WriteLine($"   ok: bones={pmx.Bones.Length} verts={pmx.Pos.Length} mats={pmx.Mats.Length} textures={textures.Length} misses={misses.Count} size={mb:0.#}MB");
        var (bodyMesh, headMesh) = MeshSplitter.SplitMeshByBones(pmx, null);
        Console.WriteLine($"   split: body(verts={bodyMesh.VertexCount}, tris={bodyMesh.TriangleCount}) head(verts={headMesh.VertexCount}, tris={headMesh.TriangleCount})");
        if (misses.Count > 0)
            Console.WriteLine($"   missing textures: {string.Join(", ", misses)}");
    }

    private static void WriteU32Str(BinaryWriter w, byte[] data)
    {
        w.Write(data.Length);
        w.Write(data);
    }

    // ---- verify mode (todo 6): parse every .gfc and dump the pinned matrix schema ----

    private static int RunVerify(string dir, bool strict)
    {
        var games = LoadGameSkeleton();
        var files = Directory.GetFiles(dir, "*.gfc");
        if (files.Length == 0)
        {
            Console.Error.WriteLine($"verify: no .gfc packages in {dir}");
            return 1;
        }
        Array.Sort(files, StringComparer.Ordinal);

        var entries = new List<string>(files.Length);
        var exitMissing = new List<string>();
        var exitStrict = new List<string>();
        var failed = new List<string>();
        foreach (var f in files)
        {
            var pkg = Path.GetFileName(f);
            try
            {
                PmxParser.PmxModel model;
                using (var fs = File.OpenRead(f))
                {
                    var data = PmxContainer.Read(fs);
                    model = PmxParser.Parse(data.Pmx);
                }
                var m = ModelMetrics.Compute(model, UnitScale, games);
                entries.Add(EntryJson(pkg, m));
                foreach (var name in m.Missing)
                    exitMissing.Add($"{pkg}: missing bone '{name}'");
                if (m.LrWarn)
                    foreach (var g in m.LrWarnGroups)
                    {
                        var p = m.PairFactors[g];
                        Console.Error.WriteLine($"warn: {pkg} {g} lr divergence L={Fmt(p[0])} R={Fmt(p[1])} (>1%)");
                    }
                if (strict)
                {
                    if (m.ScaleFlag)
                    {
                        exitStrict.Add($"{pkg}: scaleFlag true (heightU={Fmt(m.HeightU)} outside [14,26])");
                        Console.Error.WriteLine($"fail: {exitStrict[exitStrict.Count - 1]}");
                    }
                    foreach (var g in m.Factors)
                        if (g.Value > 0f && (g.Value < 0.05f || g.Value > 3.0f))
                        {
                            exitStrict.Add($"{pkg}: factor {g.Key}={Fmt(g.Value)} outside [0.05,3.0]");
                            Console.Error.WriteLine($"fail: {exitStrict[exitStrict.Count - 1]}");
                        }
                }
                Console.WriteLine($"verify: {pkg} bones={m.Bones} verts={m.Verts} skinBones={m.SkinBones} heightU={Fmt(m.HeightU)} heightM={Fmt(m.HeightM)} hipM={Fmt(m.HipM)} groundOffsetM={Fmt(m.GroundOffsetM)} scaleFlag={(m.ScaleFlag ? "true" : "false")} missing={m.Missing.Length} dups={m.Dups} accessoryMats={m.AccessoryMats}");
            }
            catch (Exception e)
            {
                failed.Add(pkg);
                Console.Error.WriteLine($"fail: {pkg}: {e.Message}");
            }
        }

        var sb = new StringBuilder();
        sb.Append("{\"unitScale\":0.078,\"packages\":[");
        for (int i = 0; i < entries.Count; i++)
        {
            if (i > 0) sb.Append(',');
            sb.Append('\n').Append(entries[i]);
        }
        sb.Append("\n]}\n");
        var outPath = RepoFile(Path.Combine("analysis", "model_matrix.json"));
        Directory.CreateDirectory(Path.GetDirectoryName(outPath));
        File.WriteAllText(outPath, sb.ToString());

        int exit = 0;
        if (exitMissing.Count > 0)
        {
            exit = 2;
            Console.Error.WriteLine($"fail: {exitMissing.Count} missing canonical bones ({failed.Count} unreadable)");
            foreach (var line in exitMissing) Console.Error.WriteLine(line);
        }
        else if (strict && exitStrict.Count > 0)
        {
            exit = 3;
            Console.Error.WriteLine($"fail: {exitStrict.Count} strict violations");
            foreach (var line in exitStrict) Console.Error.WriteLine(line);
        }
        Console.WriteLine($"\nverify: {entries.Count} packages, exit={exit} -> {outPath}");
        return exit;
    }

    private static string EntryJson(string pkg, ModelMetrics.Result m)
    {
        var sb = new StringBuilder();
        sb.Append("{\"pkg\":").Append(Js(pkg));
        sb.Append(",\"bones\":").Append(m.Bones);
        sb.Append(",\"verts\":").Append(m.Verts);
        sb.Append(",\"skinBones\":").Append(m.SkinBones);
        sb.Append(",\"heightU\":").Append(Fmt(m.HeightU));
        sb.Append(",\"heightM\":").Append(Fmt(m.HeightM));
        sb.Append(",\"hipM\":").Append(Fmt(m.HipM));
        sb.Append(",\"groundOffsetM\":").Append(Fmt(m.GroundOffsetM));
        sb.Append(",\"scaleFlag\":").Append(m.ScaleFlag ? "true" : "false");
        sb.Append(",\"missing\":[");
        for (int i = 0; i < m.Missing.Length; i++)
        {
            if (i > 0) sb.Append(',');
            sb.Append(Js(m.Missing[i]));
        }
        sb.Append("],\"dups\":").Append(m.Dups);
        sb.Append(",\"accessoryMats\":").Append(m.AccessoryMats);
        sb.Append(",\"factors\":{");
        bool first = true;
        foreach (var g in ModelMetrics.FactorGroups)
        {
            if (!first) sb.Append(',');
            first = false;
            sb.Append(Js(g)).Append(':').Append(Fmt(m.Factors[g]));
        }
        sb.Append("}}");
        return sb.ToString();
    }

    private static string Fmt(float v)
    {
        var s = v.ToString("0.####", System.Globalization.CultureInfo.InvariantCulture);
        return s == "-0" ? "0" : s;
    }

    private static string Js(string s)
    {
        var sb = new StringBuilder("\"");
        foreach (var c in s)
        {
            if (c == '"' || c == '\\') sb.Append('\\').Append(c);
            else if (c < ' ') sb.Append('\\').Append(((int)c).ToString("x4"));
            else sb.Append(c);
        }
        return sb.Append('"').ToString();
    }

    private static Dictionary<string, Vector3> LoadGameSkeleton()
    {
        var path = RepoFile(Path.Combine("analysis", "eft_skeleton.json"));
        var text = File.ReadAllText(path);
        var rx = new System.Text.RegularExpressions.Regex(
            "\"([^\"]+)\"\\s*:\\s*\\{[^{}]*?\"pos\"\\s*:\\s*\\[\\s*([^,\\]]+)\\s*,\\s*([^,\\]]+)\\s*,\\s*([^,\\]]+)\\s*\\]");
        var games = new Dictionary<string, Vector3>(StringComparer.Ordinal);
        foreach (System.Text.RegularExpressions.Match match in rx.Matches(text))
            games[match.Groups[1].Value] = new Vector3(
                float.Parse(match.Groups[2].Value, System.Globalization.CultureInfo.InvariantCulture),
                float.Parse(match.Groups[3].Value, System.Globalization.CultureInfo.InvariantCulture),
                float.Parse(match.Groups[4].Value, System.Globalization.CultureInfo.InvariantCulture));
        if (games.Count < 79)
        {
            Console.Error.WriteLine($"verify: analysis/eft_skeleton.json parsed {games.Count}/79 bones");
            Environment.Exit(1);
        }
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

    private static bool EndsWithAny(string s, string[] suffixes)
    {
        foreach (var x in suffixes)
            if (s.EndsWith(x, StringComparison.Ordinal)) return true;
        return false;
    }
}
