using System;
using System.IO;
using UnityEngine;
using UnityEngine.Rendering;

namespace GF2
{
    /// <summary>
    /// 載入並管理自訂 GF2 角色 PBR AssetBundle（gf2_shaders.bundle）：
    /// 提供 Deferred/Forward 相容之環境自適應邊緣光（Rim Light）、布料/絲襪掠射光澤（Sheen）、
    /// Alpha Cutout 與材質分類參數；若 bundle 遺失則自動退回 Unity 內建 Standard Shader。
    /// </summary>
    internal static class ShaderManager
    {
        private static readonly int PropRimScale = Shader.PropertyToID("_GF2RimScale");
        private static readonly int PropRimPowerScale = Shader.PropertyToID("_GF2RimPowerScale");
        private static readonly int PropRimTint = Shader.PropertyToID("_GF2RimTint");

        private static bool _initialized;
        private static AssetBundle _bundle;
        private static Shader _opaqueShader;
        private static Shader _transparentShader;
        private static Shader _fallbackStandard;

        internal static bool HasCustomShaders
        {
            get
            {
                EnsureLoaded();
                return _opaqueShader != null;
            }
        }

        internal static Shader ResolveOpaqueShader()
        {
            EnsureLoaded();
            return _opaqueShader ?? _fallbackStandard;
        }

        internal static Shader ResolveTransparentShader()
        {
            EnsureLoaded();
            return _transparentShader ?? _opaqueShader ?? _fallbackStandard;
        }

        internal static void EnsureLoaded()
        {
            if (_initialized) return;
            _initialized = true;
            _fallbackStandard = Shader.Find("Standard");

            try
            {
                string dir = Path.GetDirectoryName(typeof(Plugin).Assembly.Location);
                string bundlePath = string.IsNullOrEmpty(dir) ? null : Path.Combine(dir, "gf2_shaders.bundle");
                if (string.IsNullOrEmpty(bundlePath) || !File.Exists(bundlePath))
                {
                    Plugin.Log.LogInfo("[GF2] shader: gf2_shaders.bundle not found, using Unity Standard fallback");
                    return;
                }

                _bundle = AssetBundle.LoadFromFile(bundlePath);
                if (_bundle == null)
                {
                    Plugin.Log.LogWarning($"[GF2] shader: failed to load '{bundlePath}', using Standard fallback");
                    return;
                }

                _opaqueShader = _bundle.LoadAsset<Shader>("GF2CharacterPBR") ?? _bundle.LoadAsset<Shader>("GF2/CharacterPBR");
                _transparentShader = _bundle.LoadAsset<Shader>("GF2CharacterPBRTransparent") ?? _bundle.LoadAsset<Shader>("GF2/CharacterPBRTransparent");

                if (_opaqueShader != null && !_opaqueShader.isSupported) _opaqueShader = null;
                if (_transparentShader != null && !_transparentShader.isSupported) _transparentShader = null;

                Plugin.Log.LogInfo($"[GF2] shader: loaded gf2_shaders.bundle (opaque={(_opaqueShader != null ? _opaqueShader.name : "fallback")}, transparent={(_transparentShader != null ? _transparentShader.name : "fallback")})");
                ApplyGlobalRimSettings();
            }
            catch (Exception ex)
            {
                Plugin.Log.LogWarning($"[GF2] shader: bundle load error ({ex.Message}), using Standard fallback");
            }
        }

        /// <summary>將 Plugin 的邊緣光設定即時同步至全域 Shader Uniform（零每幀材質遍歷開銷）。</summary>
        internal static void ApplyGlobalRimSettings()
        {
            bool enabled = Plugin.EnableRimLight == null || Plugin.EnableRimLight.Value;
            float intensity = Plugin.RimLightIntensity != null ? Mathf.Max(0f, Plugin.RimLightIntensity.Value) : 0.65f;
            float power = Plugin.RimLightPower != null ? Mathf.Clamp(Plugin.RimLightPower.Value, 0.5f, 8.0f) : 3.2f;
            Color tint = Plugin.RimLightColor != null ? Plugin.RimLightColor.Value : Color.white;

            Shader.SetGlobalFloat(PropRimScale, enabled ? intensity : 0f);
            Shader.SetGlobalFloat(PropRimPowerScale, power / 3.2f);
            Shader.SetGlobalColor(PropRimTint, tint);
        }

        /// <summary>依材質類別套用 PBR 參數、Cutout/Transparent 混合模式與邊緣光／掠射光澤權重。</summary>
        internal static void ConfigureMaterial(
            Material m, string matName, float metallic, float smoothness, float alpha, bool isTransparent, bool isCutout = false)
        {
            if (m == null) return;

            m.color = new Color(1f, 1f, 1f, isTransparent ? alpha : 1f);
            m.SetFloat("_Glossiness", smoothness);
            m.SetFloat("_Metallic", metallic);

            if (isTransparent)
            {
                m.SetOverrideTag("RenderType", "Transparent");
                m.SetFloat("_Mode", 3f);
                m.SetInt("_SrcBlend", (int)BlendMode.SrcAlpha);
                m.SetInt("_DstBlend", (int)BlendMode.OneMinusSrcAlpha);
                m.SetInt("_ZWrite", 0);
                m.EnableKeyword("_ALPHABLEND_ON");
                m.renderQueue = 3000;
            }
            else if (isCutout)
            {
                m.SetOverrideTag("RenderType", "TransparentCutout");
                m.SetFloat("_Mode", 1f);
                m.SetFloat("_Cutoff", 0.35f);
                m.EnableKeyword("_ALPHATEST_ON");
                m.renderQueue = 2450;
            }

            if (!HasCustomShaders) return;

            var (rimColor, rimIntensity, rimPower, sheenIntensity) = ClassifyStylizedLook(matName);
            m.SetColor("_RimColor", rimColor);
            m.SetFloat("_RimIntensity", rimIntensity);
            m.SetFloat("_RimPower", rimPower);
            m.SetFloat("_RimAmbientAdapt", 0.85f);
            m.SetColor("_SheenColor", Color.white);
            m.SetFloat("_SheenIntensity", sheenIntensity);
        }

        private static (Color rimColor, float rimIntensity, float rimPower, float sheenIntensity) ClassifyStylizedLook(string name)
        {
            if (string.IsNullOrEmpty(name))
                return (new Color(0.78f, 0.88f, 1.0f, 1f), 0.90f, 3.2f, 0.12f);

            if (Contains(name, "eye") || Contains(name, "瞳") || Contains(name, "iris"))
                return (new Color(0.85f, 0.92f, 1.0f, 1f), 0.18f, 4.0f, 0.04f);

            if (Contains(name, "face") || Contains(name, "skin") || Contains(name, "body") || Contains(name, "肌") || Contains(name, "顔"))
                return (new Color(1.0f, 0.88f, 0.82f, 1f), 0.55f, 3.6f, 0.10f);

            if (Contains(name, "hair") || Contains(name, "髪"))
                return (new Color(0.85f, 0.93f, 1.0f, 1f), 0.95f, 3.0f, 0.18f);

            if (Contains(name, "metal") || Contains(name, "steel") || Contains(name, "gold") || Contains(name, "silver") || Contains(name, "armor") || Contains(name, "plate") || Contains(name, "buck"))
                return (new Color(0.82f, 0.90f, 1.0f, 1f), 1.05f, 2.8f, 0.06f);

            if (Contains(name, "leather") || Contains(name, "belt") || Contains(name, "boot") || Contains(name, "glove") || Contains(name, "shoe") || Contains(name, "stocking") || Contains(name, "tight"))
                return (new Color(0.80f, 0.88f, 1.0f, 1f), 1.00f, 3.0f, 0.22f);

            return (new Color(0.78f, 0.88f, 1.0f, 1f), 0.88f, 3.2f, 0.14f);
        }

        private static bool Contains(string haystack, string needle) =>
            haystack.IndexOf(needle, StringComparison.OrdinalIgnoreCase) >= 0;
    }
}
