Shader "GF2/CharacterPBR"
{
    Properties
    {
        _Color ("Color", Color) = (1,1,1,1)
        _MainTex ("Albedo (RGB)", 2D) = "white" {}
        _Cutoff ("Alpha Cutoff", Range(0.0, 1.0)) = 0.45
        _Glossiness ("Smoothness", Range(0.0, 1.0)) = 0.25
        _Metallic ("Metallic", Range(0.0, 1.0)) = 0.0
        _MetallicGlossMap ("Metallic (R) Smoothness (A)", 2D) = "white" {}
        _BumpMap ("Normal Map", 2D) = "bump" {}
        _BumpScale ("Normal Scale", Float) = 1.0
        _EmissionColor ("Emission Color", Color) = (0,0,0,1)
        _EmissionMap ("Emission Map", 2D) = "black" {}
        _RimColor ("Rim Color", Color) = (0.75, 0.88, 1.0, 1.0)
        _RimPower ("Rim Power", Range(0.5, 8.0)) = 3.2
        _RimIntensity ("Rim Intensity", Range(0.0, 3.0)) = 0.65
        _RimAmbientAdapt ("Rim Ambient Adaptation", Range(0.0, 1.0)) = 0.85
        _SheenColor ("Velvet Sheen Color", Color) = (1,1,1,1)
        _SheenIntensity ("Velvet Sheen Intensity", Range(0.0, 1.0)) = 0.12
    }

    SubShader
    {
        Tags { "RenderType"="Opaque" }
        LOD 200

        CGPROGRAM
        #pragma surface surf Standard fullforwardshadows addshadow
        #pragma multi_compile_local _ _ALPHATEST_ON
        #pragma multi_compile_local _ _EMISSION
        #pragma shader_feature_local _NORMALMAP
        #pragma shader_feature_local _METALLICGLOSSMAP
        #pragma target 3.0

        sampler2D _MainTex;
        sampler2D _MetallicGlossMap;
        sampler2D _BumpMap;
        sampler2D _EmissionMap;

        fixed4 _Color;
        half _Cutoff;
        half _Glossiness;
        half _Metallic;
        half _BumpScale;
        fixed4 _EmissionColor;
        fixed4 _RimColor;
        half _RimPower;
        half _RimIntensity;
        half _RimAmbientAdapt;
        fixed4 _SheenColor;
        half _SheenIntensity;

        // Global runtime controls driven by Plugin config (zero per-material update cost)
        half _GF2RimScale = 1.0;
        half _GF2RimPowerScale = 1.0;
        fixed4 _GF2RimTint = fixed4(1, 1, 1, 1);

        struct Input
        {
            float2 uv_MainTex;
            float3 viewDir;
        };

        void surf(Input IN, inout SurfaceOutputStandard o)
        {
            fixed4 c = tex2D(_MainTex, IN.uv_MainTex) * _Color;

            #ifdef _ALPHATEST_ON
            clip(c.a - _Cutoff);
            #endif

            #ifdef _NORMALMAP
            o.Normal = UnpackScaleNormal(tex2D(_BumpMap, IN.uv_MainTex), _BumpScale);
            #endif

            #ifdef _METALLICGLOSSMAP
            fixed4 mg = tex2D(_MetallicGlossMap, IN.uv_MainTex);
            o.Metallic = mg.r * _Metallic;
            o.Smoothness = mg.a * _Glossiness;
            #else
            o.Metallic = _Metallic;
            o.Smoothness = _Glossiness;
            #endif

            half ndotv = saturate(dot(normalize(IN.viewDir), o.Normal));
            half oneMinusNdotV = 1.0 - ndotv;

            // Subtle fabric/skin velvet sheen at grazing angles
            half sheen = pow(oneMinusNdotV, 2.5) * _SheenIntensity;
            o.Albedo = saturate(c.rgb + _SheenColor.rgb * sheen);
            o.Alpha = c.a;

            // Emission + Ambient-adaptive Rim Light (Deferred-safe via Emission G-Buffer)
            half3 em = half3(0, 0, 0);
            #ifdef _EMISSION
            em += tex2D(_EmissionMap, IN.uv_MainTex).rgb * _EmissionColor.rgb;
            #endif

            half3 shAmb = ShadeSH9(half4(0, 1, 0, 1));
            half ambLum = saturate(dot(shAmb, half3(0.299, 0.587, 0.114)) * 1.4 + 0.15);
            half ambFactor = lerp(1.0, ambLum, _RimAmbientAdapt);
            half rimPow = max(0.5, _RimPower * max(0.1, _GF2RimPowerScale));
            half rim = pow(oneMinusNdotV, rimPow) * _RimIntensity * _GF2RimScale * ambFactor;
            em += (_RimColor.rgb * _GF2RimTint.rgb) * rim;

            o.Emission = em;
        }
        ENDCG
    }
    FallBack "Standard"
}
