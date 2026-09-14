Shader "Leviathan/Orrery Void Gallery"
{
    Properties
    {
        _StarTint ("Star Tint", Color) = (0.82, 0.90, 1.0, 1.0)
        _VoidLift ("Void Lift", Range(0, 0.05)) = 0
        _Density ("Star Density", Range(0, 0.25)) = 0.03
        _StarSize ("Star Size", Range(0.005, 0.15)) = 0.035
        _Brightness ("Star Brightness", Range(0, 3)) = 0.8
        _RareStars ("Rare Star Strength", Range(0, 2)) = 0.25
        _WorldLock ("World Lock", Range(0, 1)) = 0.15
        _SpatialScale ("Spatial Scale", Range(0.25, 3)) = 1
        _NebulaStrength ("Dark Structure", Range(0, 0.08)) = 0
        _Seed ("Seed", Float) = 1
        _CellIndex ("Cell Index", Vector) = (0, 0, 0, 0)
        _Drift ("Drift", Vector) = (0.0005, 0.0002, 0, 0)
    }

    SubShader
    {
        Tags
        {
            "RenderType" = "Transparent"
            "Queue" = "Transparent-50"
            "RenderPipeline" = "UniversalPipeline"
            "IgnoreProjector" = "True"
        }

        Pass
        {
            Name "VoidGallery2D"
            Tags { "LightMode" = "Universal2D" }

            Blend SrcAlpha OneMinusSrcAlpha
            Cull Off
            ZWrite Off
            ZTest LEqual

            HLSLPROGRAM
            #pragma vertex Vert
            #pragma fragment Frag
            #pragma target 3.0

            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"

            struct Attributes
            {
                float4 positionOS : POSITION;
                float2 uv : TEXCOORD0;
            };

            struct Varyings
            {
                float4 positionHCS : SV_POSITION;
                float2 uv : TEXCOORD0;
                float2 worldXY : TEXCOORD1;
            };

            CBUFFER_START(UnityPerMaterial)
                float4 _StarTint;
                float4 _CellIndex;
                float4 _Drift;
                float _VoidLift;
                float _Density;
                float _StarSize;
                float _Brightness;
                float _RareStars;
                float _WorldLock;
                float _SpatialScale;
                float _NebulaStrength;
                float _Seed;
            CBUFFER_END

            float Hash21(float2 p)
            {
                p = frac(p * float2(123.34, 456.21));
                p += dot(p, p + 45.32);
                return frac(p.x * p.y);
            }

            float2 Hash22(float2 p)
            {
                float n = Hash21(p);
                return frac(float2(n, Hash21(p + n + 19.19)) * float2(437.13, 231.71));
            }

            float ValueNoise(float2 p)
            {
                float2 i = floor(p);
                float2 f = frac(p);
                f = f * f * (3.0 - 2.0 * f);

                float a = Hash21(i);
                float b = Hash21(i + float2(1, 0));
                float c = Hash21(i + float2(0, 1));
                float d = Hash21(i + float2(1, 1));
                return lerp(lerp(a, b, f.x), lerp(c, d, f.x), f.y);
            }

            float StarLayer(
                float2 coordinate,
                float scale,
                float density,
                float radius,
                float seed,
                out float sparkle)
            {
                float2 p = coordinate * scale;
                float2 id = floor(p);
                float2 cell = frac(p) - 0.5;

                float existence = Hash21(id + seed * float2(11.71, 37.29));
                float present = step(1.0 - saturate(density), existence);

                float2 offset = (Hash22(id + seed * float2(53.17, 7.91)) - 0.5) * 0.72;
                float shape = Hash21(id + seed * float2(3.73, 83.41));
                float starRadius = radius * lerp(0.55, 1.25, shape);
                float distanceToStar = length(cell - offset);
                float antialias = max(fwidth(distanceToStar), 0.0025);
                float core = 1.0 - smoothstep(starRadius, starRadius + antialias, distanceToStar);

                sparkle = present * core * lerp(0.30, 1.0, Hash21(id + seed * 101.7));
                return sparkle;
            }

            Varyings Vert(Attributes input)
            {
                Varyings output;
                VertexPositionInputs positionInputs = GetVertexPositionInputs(input.positionOS.xyz);
                output.positionHCS = positionInputs.positionCS;
                output.worldXY = positionInputs.positionWS.xy;
                output.uv = input.uv;
                return output;
            }

            half4 Frag(Varyings input) : SV_Target
            {
                // UV + cell index gives a continuous 4x4 gallery coordinate when
                // WorldLock is zero. At one, the stars are fully world anchored.
                // Intermediate values deliberately create the portal/parallax feel.
                float2 panelCoordinate = input.uv + _CellIndex.xy;
                float2 worldCoordinate = input.worldXY * 0.5;
                float2 coordinate = lerp(panelCoordinate, worldCoordinate, saturate(_WorldLock));
                coordinate *= max(0.01, _SpatialScale);
                coordinate += _Time.y * _Drift.xy;

                float farSparkle;
                float farStars = StarLayer(
                    coordinate,
                    17.0,
                    _Density,
                    _StarSize * 0.72,
                    _Seed + 3.1,
                    farSparkle);

                // A second, much sparser population gives occasional scale cues.
                // It drifts more slowly than the already-slow far field.
                float rareSparkle;
                float2 rareCoordinate = coordinate * 0.61 - _Time.y * _Drift.xy * 0.73;
                float rareStars = StarLayer(
                    rareCoordinate,
                    10.0,
                    _Density * 0.13,
                    _StarSize * 1.35,
                    _Seed + 91.7,
                    rareSparkle);

                float starLight = farStars * _Brightness + rareStars * _RareStars;

                // This is intentionally structure, not a colorful nebula. Most
                // presets keep it at zero; the few that use it should still read
                // as nearly black space rather than gas clouds.
                float structure = ValueNoise(coordinate * 0.42 + _Seed * 0.17);
                structure = pow(saturate(structure - 0.48) * 1.92, 3.0);
                float coldStructure = structure * _NebulaStrength;

                float3 voidColor = _VoidLift.xxx;
                voidColor += coldStructure * float3(0.055, 0.070, 0.090);
                float3 color = voidColor + _StarTint.rgb * starLight;
                return half4(color, 1.0);
            }
            ENDHLSL
        }
    }

    FallBack Off
}
