// Parser-only stubs for PackagerProgram: same trick as the validation harness.
// GF2_PMX_PARSER_ONLY excludes the Unity-side classes so these minimal types suffice.
namespace UnityEngine.Rendering
{
    public enum ShadowCastingMode
    {
        Off = 0,
        On = 1,
        TwoSided = 2,
        ShadowsOnly = 3
    }
}
namespace UnityEngine
{
    public struct Vector3 { public float x, y, z; public Vector3(float x, float y, float z){this.x=x;this.y=y;this.z=z;} public override string ToString()=>$"({x:0.###},{y:0.###},{z:0.###})"; }
    public struct Vector2 { public float x, y; public Vector2(float x, float y){this.x=x;this.y=y;} }
    public struct Color { public float r, g, b, a; public Color(float r, float g, float b, float a){this.r=r;this.g=g;this.b=b;this.a=a;} }
    public struct BoneWeight { public int boneIndex0, boneIndex1, boneIndex2, boneIndex3; public float weight0, weight1, weight2, weight3; }
    public static class Mathf { public static float Min(float a, float b) => a < b ? a : b; public static float Max(float a, float b) => a > b ? a : b; }
    public class Object { public static void Destroy(object o) { } }
    public class Renderer { public bool enabled { get; set; } = true; public Rendering.ShadowCastingMode shadowCastingMode { get; set; } = Rendering.ShadowCastingMode.On; }
    public class SkinnedMeshRenderer : Renderer { }
    public class Camera { public float nearClipPlane { get; set; } = 0.05f; public static Camera main { get; set; } public int GetInstanceID() => GetHashCode(); }
}
namespace BepInEx.Logging { public class ManualLogSource { public static ManualLogSource Instance = new(); public void LogInfo(object m){System.Console.WriteLine(m);} public void LogWarning(object m){System.Console.WriteLine("W: "+m);} public void LogError(object m){System.Console.WriteLine("E: "+m);} } }
namespace GF2 { internal static class Plugin { internal static readonly BepInEx.Logging.ManualLogSource Log = BepInEx.Logging.ManualLogSource.Instance; } }
