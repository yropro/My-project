// Adapted from Assets/StarfieldShaders/2D/Assets/Shaders/SS_Starfield2D.shader.
// Uses its mesh channels and soft triangular stars; adds panel clipping,
// positive wrapping and camera-relative depth for finite spell surfaces.
Shader "Leviathan/Test/Void Starfield"
{
    Properties
    {
        _Brightness("Brightness", Float) = 0.5
        _Motion("Relative motion", Float) = 0.01
        _Void("Central emptiness", Float) = 0
        _Seed("Seed", Float) = 0
        _Layer("Depth layer", Float) = 0
        _MaxSize("Maximum star size", Float) = 0.1
        _MinSize("Minimum star size", Float) = 0.016
        _CameraTravel("Camera travel", Vector) = (0,0,0,0)
        _Elapsed("Elapsed", Float) = 0
    }
    SubShader
    {
        Tags { "Queue"="Transparent" "RenderType"="Transparent" "DisableBatching"="True" }
        Cull Off ZWrite Off
        Blend SrcAlpha One
        Pass
        {
            Tags { "LightMode"="SRPDefaultUnlit" }
            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #include "UnityCG.cginc"
            float _Brightness, _Motion, _Void, _Seed, _Layer, _MaxSize, _MinSize, _Elapsed;
            float4 _CameraTravel;
            struct appdata
            {
                float4 vertex : POSITION;
                float3 offset : NORMAL;
                float4 color : COLOR;
                float2 noise : TEXCOORD0;
                float2 sizeRot : TEXCOORD1;
                float2 offsetNoise : TEXCOORD2;
            };
            struct v2f
            {
                float4 vertex : SV_POSITION;
                float3 color : COLOR;
                float2 r : TEXCOORD0;
                float2 panel : TEXCOORD1;
            };
            v2f vert(appdata v)
            {
                v2f o;
                o.r = normalize(v.offset.xy) * 2;
                float size = lerp(_MinSize, _MaxSize, v.sizeRot.x);
                // Far stars follow almost all camera travel: very little screen displacement.
                float depth = _Motion * lerp(0.65, 1.35, v.noise.x * 0.5 + 0.5) * (1 + _Layer * 3);
                v.vertex.xy += v.offsetNoise.xy * 11 + _Seed * 10;
                v.vertex.xy += _CameraTravel.xy * 10 * (1 - depth);
                v.vertex.xy += _Elapsed * float2(-0.00035,0.00012) * (1 + _Layer * 1.5);
                // Center wraps outside the clipped panel, hiding the wrap at its edges.
                v.vertex.xy = (frac(v.vertex.xy / 10.4 + 0.5) - 0.5) * 10.4;
                v.vertex.xy += v.offset.xy * max(size, 0.012);
                o.panel = v.vertex.xy / 10;
                o.vertex = UnityObjectToClipPos(v.vertex);
                o.color = v.color.rgb;
                return o;
            }
            fixed4 frag(v2f i) : SV_Target
            {
                float edgeDistance = max(abs(i.panel.x), abs(i.panel.y));
                clip(0.5 - edgeDistance);
                float edge = 1 - smoothstep(0.47, 0.5, edgeDistance);
                float empty = lerp(1, smoothstep(0.18, 0.49, length(i.panel * float2(1,1.14))), _Void);
                float tex = saturate(1 - length(i.r));
                return float4(lerp(i.color, float3(0.94,0.97,1), tex * tex) * tex,
                    _Brightness * edge * empty);
            }
            ENDCG
        }
    }
}
