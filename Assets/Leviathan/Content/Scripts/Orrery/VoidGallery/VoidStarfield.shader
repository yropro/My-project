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
        _PanelTravel("Panel travel", Vector) = (0,0,0,0)
        _Elapsed("Elapsed", Float) = 0
        _Sector("Sector start, arc, radius, enabled", Vector) = (0,6.2831853,0.5,0)
        _WorldUnitsPerPixel("Pixel footprint", Float) = 0.01
        _RadiusRange("Authored radius range", Vector) = (0,0,0,0)
        _VisibilityFloor("Minimum filtered brightness", Float) = 0
    }
    SubShader
    {
        Tags { "Queue"="Transparent" "RenderType"="Transparent" "DisableBatching"="True" }
        Cull Off ZWrite Off ZTest Always
        Blend SrcAlpha One
        Pass
        {
            Tags { "LightMode"="SRPDefaultUnlit" }
            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #pragma target 3.0
            #include "UnityCG.cginc"
            #include "VoidSectorClip.cginc"
            float _Brightness, _Motion, _Void, _Seed, _Layer, _MaxSize, _MinSize, _Elapsed;
            float4 _CameraTravel, _PanelTravel;
            float _WorldUnitsPerPixel;
            float4 _RadiusRange;
            float _VisibilityFloor;
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
                // Cancel panel translation separately so following the ship does
                // not turn the distant star motion into a fast texture scroll.
                v.vertex.xy -= _PanelTravel.xy * 10;
                v.vertex.xy += _Elapsed * float2(-0.00035,0.00012) * (1 + _Layer * 1.5);
                // Center wraps outside the clipped panel, hiding the wrap at its edges.
                float radius = length(v.offset.xy) * max(size, 0.012);
                if (_RadiusRange.y > 0) radius = lerp(_RadiusRange.x, _RadiusRange.y, sqrt(saturate(v.sizeRot.x)));
                float worldScale = length(unity_ObjectToWorld._m00_m10_m20);
                float filteredRadius = max(radius, 3 * _WorldUnitsPerPixel / max(worldScale, 0.0001));
                float period = _RadiusRange.y > 0 ? 10 + 4 * filteredRadius : 10.4;
                v.vertex.xy = (frac(v.vertex.xy / period + 0.5) - 0.5) * period;
                v.vertex.xy += normalize(v.offset.xy) * filteredRadius;
                o.panel = v.vertex.xy / 10;
                o.vertex = UnityObjectToClipPos(v.vertex);
                // Widen subpixel stars without increasing their integrated brightness.
                o.color = v.color.rgb * max(_VisibilityFloor, radius * radius / (filteredRadius * filteredRadius));
                return o;
            }
            fixed4 frag(v2f i) : SV_Target
            {
                ClipVoidSector(i.panel);
                float edgeDistance = max(abs(i.panel.x), abs(i.panel.y));
                clip(0.5 - edgeDistance);
                float edge = 1 - smoothstep(0.497, 0.5, edgeDistance);
                float empty = lerp(1, smoothstep(0.18, 0.49, length(i.panel * float2(1,1.14))), _Void);
                float tex = saturate(1 - length(i.r));
                return float4(i.color * tex,
                    _Brightness * edge * empty);
            }
            ENDCG
        }
    }
}
