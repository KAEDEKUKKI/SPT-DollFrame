using System.IO;
using UnityEditor;
using UnityEngine;

public static class BuildShaderBundle
{
    public static void Build()
    {
        var outDir = Path.GetFullPath(Path.Combine(Application.dataPath, "..", ".."));
        var build = new AssetBundleBuild
        {
            assetBundleName = "gf2_shaders.bundle",
            assetNames = new[]
            {
                "Assets/Shaders/GF2CharacterPBR.shader",
                "Assets/Shaders/GF2CharacterPBRTransparent.shader",
            }
        };

        var manifest = BuildPipeline.BuildAssetBundles(
            outDir,
            new[] { build },
            BuildAssetBundleOptions.ChunkBasedCompression,
            BuildTarget.StandaloneWindows64);

        if (manifest == null)
        {
            Debug.LogError("GF2 BuildShaderBundle failed!");
            EditorApplication.Exit(1);
            return;
        }
        Debug.Log("GF2 BuildShaderBundle succeeded: " + Path.Combine(outDir, "gf2_shaders.bundle"));
        EditorApplication.Exit(0);
    }
}
