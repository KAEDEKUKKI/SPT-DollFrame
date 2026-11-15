using System;
using System.Collections.Generic;
using System.IO;
using GF2;

// `grants`: the MMD append ("付与親") links the rig must honour. GFL2 exports skin the
// legs to the D bones (足D/ひざD/足首D), which have no motion of their own - they copy
// the FK leg bone through the append link. This check parses every package with the
// plugin parser and asserts, for every SKINNED bone whose name ends in 'D':
//   - it carries a rotate-append link,
//   - the append parent is an earlier bone (parent-first evaluation is valid),
//   - the weight is 1 (full copy).
// It also asserts every append parent index across all bones precedes the bone, which
// is what CharacterRig's single forward pass relies on. Exit 2 on any violation.
internal static class GrantsCheck
{
    private const string DefaultPackagesDir = "C:\SPT/BepInEx/plugins/kukki.GF2/GF2_Characters";

    internal static int Run()
    {
        var dir = Environment.GetEnvironmentVariable("GF2_PACKAGES") ?? DefaultPackagesDir;
        if (!Directory.Exists(dir))
        {
            Console.Error.WriteLine($"grants: packages dir not found: {dir}");
            return 1;
        }
        var fail = new List<string>();
        int pkgs = 0, dBones = 0, grants = 0, forward = 0;
        foreach (var f in Directory.GetFiles(dir, "*.gfc"))
        {
            PmxContainerData pkg;
            using (var fs = File.OpenRead(f)) pkg = PmxContainer.Read(fs);
            var m = PmxParser.Parse(pkg.Pmx);
            pkgs++;
            var skinned = new HashSet<int>();
            foreach (var w in m.Weights)
            {
                if (w.weight0 > 0f) skinned.Add(w.boneIndex0);
                if (w.weight1 > 0f) skinned.Add(w.boneIndex1);
                if (w.weight2 > 0f) skinned.Add(w.boneIndex2);
                if (w.weight3 > 0f) skinned.Add(w.boneIndex3);
            }
            for (int i = 0; i < m.Bones.Length; i++)
            {
                var b = m.Bones[i];
                if (b.GrantRotate)
                {
                    grants++;
                    if (b.GrantParent >= i)
                    {
                        forward++;
                        fail.Add($"{pkg.Name}: '{b.Name}' append parent #{b.GrantParent} is not earlier than #{i}");
                    }
                }
                if (!skinned.Contains(i) || string.IsNullOrEmpty(b.Name) || !b.Name.EndsWith("D", StringComparison.Ordinal)) continue;
                if (b.Name.IndexOf("足", StringComparison.Ordinal) < 0 && b.Name.IndexOf("ひざ", StringComparison.Ordinal) < 0) continue;
                dBones++;
                if (!b.GrantRotate || b.GrantParent < 0 || b.GrantParent >= m.Bones.Length)
                    fail.Add($"{pkg.Name}: skinned '{b.Name}' has no rotate-append link");
                else if (Math.Abs(b.GrantWeight - 1f) > 1e-4f)
                    fail.Add($"{pkg.Name}: '{b.Name}' append weight {b.GrantWeight} != 1");
            }
        }
        foreach (var s in fail) Console.Error.WriteLine("grants: FAIL " + s);
        if (fail.Count > 0) return 2;
        Console.WriteLine($"grants: {pkgs} packages, {dBones} skinned leg D bones linked, {grants} append links all parent-first");
        return 0;
    }
}
