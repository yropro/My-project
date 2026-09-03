#ifndef HUESHIFT_PER_MATERIAL_INCLUDED
#define HUESHIFT_PER_MATERIAL_INCLUDED

// Single shared UnityPerMaterial CBUFFER for the HueShift lit shader.
//
// The SRP Batcher requires every material property (everything except textures,
// samplers and [PerRendererData] values) to live in one CBUFFER named
// UnityPerMaterial, and that block must be byte-identical across all of a
// shader's passes. Previously each pass declared a different loose subset of
// these uniforms with no CBUFFER, which marked the shader "SRP Batcher: not
// compatible" — so every sprite using it broke batching and became its own
// SetPass call. Sharing this single block from all passes fixes that.
//
// Members are the union of every material uniform referenced across the
// Universal2D, NormalsRendering and UniversalForward passes. A pass that does
// not use a given member still keeps it here so the layout stays identical.
CBUFFER_START(UnityPerMaterial)
    float4 _MainTex_ST;
    float4 _NormalMap_ST;
    float4 _MainTex_TexelSize;
    float4 _Color;
    half4  _RendererColor;
    float  _HueRangeMin;
    float  _HueRangeMax;
    // Laser slice clipping
    float  _ClipEnabled;
    float4 _ClipLinePoint;
    float4 _ClipLineNormal;
    // Molten metal emission
    float  _EmissionEnabled;
    float4 _EmissionColor;
    float4 _EmissionOrigin;
    float  _EmissionRadius;
    float  _EmissionIntensity;
    float  _EmissionSeed;
    float  _ErosionProgress;
    float  _CrackDarken;
    float4x4 _DamageMatrix;
    // Debuff tint overlay
    float4 _DebuffTint;
    float  _DebuffBlend;
    float  _DebuffNoiseScale;
    float  _DebuffSplatHardness;
CBUFFER_END

#endif // HUESHIFT_PER_MATERIAL_INCLUDED
