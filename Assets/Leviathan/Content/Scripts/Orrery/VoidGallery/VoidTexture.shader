Shader "Leviathan/Test/Void DinV"
{
    Properties
    {
        _SmallStars("DinV small stars", 2D) = "black" {}
        _BigStars("DinV large stars", 2D) = "black" {}
        _Nebula("DinV blue nebula", 2D) = "black" {}
        _Brightness("Brightness", Float) = 0.4
        _Motion("Relative motion", Float) = 0.01
        _Void("Central emptiness", Float) = 0
        _Seed("Seed", Float) = 0
        _TextureWeight("Texture weight", Float) = 1
        _Haze("Haze", Float) = 0
        _CameraTravel("Camera travel", Vector) = (0,0,0,0)
        _Elapsed("Elapsed", Float) = 0
    }
    SubShader
    {
        Tags { "Queue"="Transparent" "RenderType"="Transparent" }
        Cull Off ZWrite Off
        Blend SrcAlpha OneMinusSrcAlpha
        Pass
        {
            Tags { "LightMode"="SRPDefaultUnlit" }
            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #include "UnityCG.cginc"
            sampler2D _SmallStars, _BigStars, _Nebula;
            float _Brightness, _Motion, _Void, _Seed, _TextureWeight, _Haze, _Elapsed;
            float4 _CameraTravel;
            struct appdata { float4 vertex : POSITION; float2 uv : TEXCOORD0; };
            struct v2f { float4 vertex : SV_POSITION; float2 uv : TEXCOORD0; };
            v2f vert(appdata v)
            {
                v2f o; o.vertex = UnityObjectToClipPos(v.vertex); o.uv = v.uv; return o;
            }
            // Mirroring joins clamp-imported textures continuously without editing vendor importers.
            float2 mirrorUV(float2 p) { return 1 - abs(frac(p * 0.5) * 2 - 1); }
            float luminance(float3 c) { return dot(c, float3(0.2126, 0.7152, 0.0722)); }
            fixed4 frag(v2f i) : SV_Target
            {
                float2 farUV = i.uv - _CameraTravel.xy * (1 - _Motion);
                float2 nearUV = i.uv - _CameraTravel.xy * (1 - _Motion * 4);
                farUV += float2(_Seed, _Seed * 2.71) + _Elapsed * float2(0.000035, -0.000012);
                nearUV += float2(_Seed * 1.77, _Seed) + _Elapsed * float2(0.000075, 0.000023);
                float4 small = tex2D(_SmallStars, mirrorUV(farUV * 1.8));
                float4 big = tex2D(_BigStars, mirrorUV(nearUV * 1.15));
                float smallLight = pow(saturate(luminance(small.rgb)), 1.5) * small.a;
                float bigLight = pow(saturate(luminance(big.rgb)), 2.2) * big.a;
                float radius = length((i.uv - 0.5) * float2(1.0, 1.14));
                float emptiness = lerp(1, smoothstep(0.18, 0.49, radius), _Void);
                float3 stars = (smallLight * float3(0.57,0.73,1) + bigLight * float3(0.85,0.92,1) * 0.65);
                float4 nebula = tex2D(_Nebula, mirrorUV(farUV * 0.7));
                float haze = luminance(nebula.rgb) * nebula.a * _Haze;
                float3 color = float3(0.0006,0.0012,0.003) +
                    (stars * _Brightness * _TextureWeight + haze * float3(0.18,0.37,0.8)) * emptiness;
                float edge = 1 - smoothstep(0.48, 0.5, max(abs(i.uv.x - 0.5), abs(i.uv.y - 0.5)));
                return float4(color, edge);
            }
            ENDCG
        }
    }
}
