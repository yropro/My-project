// Writen by Martin Nerurkar ( www.playful.systems). MIT license (see license.txt)
// Based on Unity built-in shader source. Copyright (c) 2016 Unity Technologies. MIT license (see license.txt)
// Inspired by HSV Shader for Unity from Gregg Tavares (https://github.com/greggman/hsva-unity). MIT License (see license.txt)

Shader "Sprites/Default-HueShift"
{
	Properties
	{
		[PerRendererData] _MainTex ("Sprite Texture", 2D) = "white" {}
		_Color ("Tint", Color) = (1,1,1,1)

		// CUSTOM: Added to allow use of shader in UI images
        _StencilComp ("Stencil Comparison", Float) = 8
        _Stencil ("Stencil ID", Float) = 0
        _StencilOp ("Stencil Operation", Float) = 0
        _StencilWriteMask ("Stencil Write Mask", Float) = 255
        _StencilReadMask ("Stencil Read Mask", Float) = 255
        _ColorMask ("Color Mask", Float) = 15
		// End CUSTOM

		[MaterialToggle] PixelSnap ("Pixel snap", Float) = 0
		[HideInInspector] _RendererColor ("RendererColor", Color) = (1,1,1,1)
		[HideInInspector] _Flip ("Flip", Vector) = (1,1,1,1)
		[PerRendererData] _AlphaTex ("External Alpha", 2D) = "white" {}
		[PerRendererData] _EnableExternalAlpha ("Enable External Alpha", Float) = 0

		[HideInInspector] _HueRangeMin("Min Hue Range", Range(0, 1)) = 0
		[HideInInspector] _HueRangeMax("Max Hue Range", Range(0, 1)) = 1

		// Debuff tint overlay (matches Sprites/Lit-Default-HueShift so snapshot captures status effect tint)
		[HideInInspector] _DebuffTint("Debuff Tint", Color) = (0,0,0,0)
		[HideInInspector] _DebuffBlend("Debuff Blend", Float) = 0
		[HideInInspector] _DebuffNoiseScale("Debuff Noise Scale", Float) = 0
		[HideInInspector] _DebuffSplatHardness("Debuff Splat Hardness", Float) = 0
	}

	SubShader
	{
		Tags
		{
			"Queue"="Transparent"
			"IgnoreProjector"="True"
			"RenderType"="Transparent"
			"PreviewType"="Plane"
			"CanUseSpriteAtlas"="True"
		}
		
		// CUSTOM: Maskable
		Stencil
		{
			Ref [_Stencil]
			Comp [_StencilComp]
			Pass [_StencilOp]
			ReadMask [_StencilReadMask]
			WriteMask [_StencilWriteMask]
		}
		// END CUSTOM

		Cull Off
		Lighting Off
		ZWrite Off
		Blend One OneMinusSrcAlpha

		// CUSTOM: Maskable
		ColorMask [_ColorMask]
		// END CUSTOM

		Pass
		{
		CGPROGRAM
			#pragma vertex SpriteVert
			#pragma fragment SpriteFragHSV
			#pragma target 2.0
			#pragma multi_compile_instancing
			#pragma multi_compile _ PIXELSNAP_ON
			#pragma multi_compile _ ETC1_EXTERNAL_ALPHA
			#pragma multi_compile __ PS_HSV_ALPHAMASK_ON
			#pragma multi_compile __ PS_HSV_HUERANGE_ON
			#include "UnitySprites.cginc"
			#include "HueShift.cginc"

            float _HueRangeMin;
            float _HueRangeMax;

			float4 _DebuffTint;
			float _DebuffBlend;
			float _DebuffNoiseScale;
			float _DebuffSplatHardness;

			float debuffHash(float2 p)
			{
				float3 p3 = frac(float3(p.xyx) * 0.1031);
				p3 += dot(p3, p3.yzx + 33.33);
				return frac((p3.x + p3.y) * p3.z);
			}

			float debuffNoise(float2 p)
			{
				float2 i = floor(p);
				float2 f = frac(p);
				f = f * f * (3.0 - 2.0 * f);

				float a = debuffHash(i);
				float b = debuffHash(i + float2(1.0, 0.0));
				float c = debuffHash(i + float2(0.0, 1.0));
				float d = debuffHash(i + float2(1.0, 1.0));

				return lerp(lerp(a, b, f.x), lerp(c, d, f.x), f.y);
			}

			float debuffFbm(float2 p)
			{
				float value = 0.0;
				value += 0.5 * debuffNoise(p); p *= 2.0;
				value += 0.25 * debuffNoise(p); p *= 2.0;
				value += 0.125 * debuffNoise(p);
				return value;
			}

			fixed4 SpriteFragHSV(v2f IN) : SV_Target
			{
			    fixed4 main = SampleSpriteTexture(IN.texcoord);
			    fixed4 c = applyHSV(main, IN.color, _HueRangeMin, _HueRangeMax);

			    // Debuff tint overlay (burning, overheated, corroding, frozen, etc.)
			    if (_DebuffBlend > 0.001)
			    {
			        float blendMask = _DebuffBlend * main.a;

			        if (_DebuffNoiseScale > 0.001)
			        {
			            float2 noiseCoord = IN.texcoord * _DebuffNoiseScale;

			            if (_DebuffSplatHardness > 0.5)
			            {
			                float n1 = debuffNoise(noiseCoord);
			                float n2 = debuffNoise(noiseCoord * 0.5 + float2(31.7, 17.3));
			                float splat = max(n1, n2);
			                blendMask *= step(0.68, splat);
			            }
			            else
			            {
			                float n = debuffFbm(noiseCoord);
			                blendMask *= saturate((n - 0.3) * 3.0);
			            }
			        }

			        c.rgb = lerp(c.rgb, _DebuffTint.rgb, blendMask);
			    }

			    c.rgb *= c.a;
			    return c;
			}
		ENDCG
		}
	}
	CustomEditor "HueShiftMaterialInspector"
}
