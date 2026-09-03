// Based on Sprites/Default-HueShift. Extended with property-driven blend states
// for Photoshop-style blend modes (Screen, Linear Dodge, Lighten).
// Unlit shader - does not require 2D lights. All modes use hardware blending.

Shader "Sprites/Default-HueShift-BlendModes"
{
	Properties
	{
		_MainTex ("Sprite Texture", 2D) = "white" {}
		_NormalMap ("Normal Map", 2D) = "bump" {}
		_Color ("Hue Shift", Color) = (1,1,1,1)

		_StencilComp ("Stencil Comparison", Float) = 8
		_Stencil ("Stencil ID", Float) = 0
		_StencilOp ("Stencil Operation", Float) = 0
		_StencilWriteMask ("Stencil Write Mask", Float) = 255
		_StencilReadMask ("Stencil Read Mask", Float) = 255
		_ColorMask ("Color Mask", Float) = 15

		[HideInInspector] _RendererColor ("RendererColor", Color) = (1,1,1,1)
		[HideInInspector] _Flip ("Flip", Vector) = (1,1,1,1)
		[PerRendererData] _AlphaTex ("External Alpha", 2D) = "white" {}
		[PerRendererData] _EnableExternalAlpha ("Enable External Alpha", Float) = 0

		[HideInInspector] _HueRangeMin("Min Hue Range", Range(0, 1)) = 0
		[HideInInspector] _HueRangeMax("Max Hue Range", Range(0, 1)) = 1

		// Blend mode properties (set from C# via Background.cs)
		[HideInInspector] _BlendMode ("Blend Mode", Float) = 0
		[HideInInspector] _BlendOp ("Blend Operation", Float) = 0
		[HideInInspector] _SrcBlend ("Source Blend", Float) = 1
		[HideInInspector] _DstBlend ("Destination Blend", Float) = 10
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
			"RenderPipeline" = "UniversalPipeline"
		}

		Stencil
		{
			Ref [_Stencil]
			Comp [_StencilComp]
			Pass [_StencilOp]
			ReadMask [_StencilReadMask]
			WriteMask [_StencilWriteMask]
		}
		ColorMask [_ColorMask]

		Cull Off
		Lighting Off
		ZWrite Off

		Pass
		{
		Tags { "LightMode" = "Universal2D" }
		BlendOp [_BlendOp]
		Blend [_SrcBlend] [_DstBlend], One OneMinusSrcAlpha
		HLSLPROGRAM
			#include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"

			#pragma vertex UnlitVertex
			#pragma fragment UnlitFragment

			#pragma multi_compile __ PS_HSV_ALPHAMASK_ON
			#pragma multi_compile __ PS_HSV_HUERANGE_ON

			struct Attributes
			{
				float3 positionOS   : POSITION;
				float4 color        : COLOR;
				float2  uv          : TEXCOORD0;
				UNITY_VERTEX_INPUT_INSTANCE_ID
			};

			struct Varyings
			{
				float4  positionCS  : SV_POSITION;
				half4   color       : COLOR;
				float2  uv          : TEXCOORD0;
				UNITY_VERTEX_OUTPUT_STEREO
			};

			#include "../_Thirdparty/PS-HueShiftShader/Shaders/HueShift.cginc"

			TEXTURE2D(_MainTex);
			SAMPLER(sampler_MainTex);
			half4 _MainTex_ST;
			float4 _Color;
			half4 _RendererColor;

			float _HueRangeMin;
			float _HueRangeMax;

			Varyings UnlitVertex(Attributes v)
			{
				Varyings o = (Varyings)0;
				UNITY_SETUP_INSTANCE_ID(v);
				UNITY_INITIALIZE_VERTEX_OUTPUT_STEREO(o);

				o.positionCS = TransformObjectToHClip(v.positionOS);
				o.uv = TRANSFORM_TEX(v.uv, _MainTex);
				o.color = v.color * _Color * _RendererColor;
				return o;
			}

			half4 UnlitFragment(Varyings i) : SV_Target
			{
				const half4 main = SAMPLE_TEXTURE2D(_MainTex, sampler_MainTex, i.uv);

				half4 c = applyHSV(main, i.color, _HueRangeMin, _HueRangeMax);

				// Premultiply alpha (matches original Sprites-Default-HueShift behavior)
				c.rgb *= c.a;

				return c;
			}
		ENDHLSL
		}

		Pass
		{
			Tags { "LightMode" = "NormalsRendering"}
			Blend SrcAlpha OneMinusSrcAlpha, One OneMinusSrcAlpha

			HLSLPROGRAM
			#include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"

			#pragma vertex NormalsRenderingVertex
			#pragma fragment NormalsRenderingFragment

			struct Attributes
			{
				float3 positionOS   : POSITION;
				float4 color        : COLOR;
				float2 uv           : TEXCOORD0;
				float4 tangent      : TANGENT;
				UNITY_VERTEX_INPUT_INSTANCE_ID
			};

			struct Varyings
			{
				float4  positionCS      : SV_POSITION;
				half4   color           : COLOR;
				float2  uv              : TEXCOORD0;
				half3   normalWS        : TEXCOORD1;
				half3   tangentWS       : TEXCOORD2;
				half3   bitangentWS     : TEXCOORD3;
				UNITY_VERTEX_OUTPUT_STEREO
			};

			TEXTURE2D(_MainTex);
			SAMPLER(sampler_MainTex);
			TEXTURE2D(_NormalMap);
			SAMPLER(sampler_NormalMap);
			half4 _NormalMap_ST;
			float4 _MainTex_TexelSize;

			Varyings NormalsRenderingVertex(Attributes attributes)
			{
				Varyings o = (Varyings)0;
				UNITY_SETUP_INSTANCE_ID(attributes);
				UNITY_INITIALIZE_VERTEX_OUTPUT_STEREO(o);

				o.positionCS = TransformObjectToHClip(attributes.positionOS);
				o.uv = TRANSFORM_TEX(attributes.uv, _NormalMap);
				o.color = attributes.color;
				o.normalWS = -GetViewForwardDir();
				o.tangentWS = TransformObjectToWorldDir(attributes.tangent.xyz);
				o.bitangentWS = cross(o.normalWS, o.tangentWS) * attributes.tangent.w;
				return o;
			}

			#include "Packages/com.unity.render-pipelines.universal/Shaders/2D/Include/NormalsRenderingShared.hlsl"

			half4 NormalsRenderingFragment(Varyings i) : SV_Target
			{
				const half4 mainTex = i.color * SAMPLE_TEXTURE2D(_MainTex, sampler_MainTex, i.uv);
				const half3 normalTS = UnpackNormal(SAMPLE_TEXTURE2D(_NormalMap, sampler_NormalMap, i.uv));

				return NormalsRenderingShared(mainTex, normalTS, i.tangentWS.xyz, i.bitangentWS.xyz, i.normalWS.xyz);
			}
			ENDHLSL
		}

		Pass
		{
			Tags { "LightMode" = "UniversalForward" "Queue"="Transparent" "RenderType"="Transparent"}
			Blend SrcAlpha OneMinusSrcAlpha, One OneMinusSrcAlpha

			HLSLPROGRAM
			#include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"

			#pragma vertex UnlitVertex
			#pragma fragment UnlitFragment

			struct Attributes
			{
				float3 positionOS   : POSITION;
				float4 color        : COLOR;
				float2 uv           : TEXCOORD0;
				UNITY_VERTEX_INPUT_INSTANCE_ID
			};

			struct Varyings
			{
				float4  positionCS      : SV_POSITION;
				float4  color           : COLOR;
				float2  uv              : TEXCOORD0;
				#if defined(DEBUG_DISPLAY)
				float3  positionWS  : TEXCOORD2;
				#endif
				UNITY_VERTEX_OUTPUT_STEREO
			};

			TEXTURE2D(_MainTex);
			SAMPLER(sampler_MainTex);
			float4 _MainTex_ST;
			float4 _MainTex_TexelSize;

			Varyings UnlitVertex(Attributes attributes)
			{
				Varyings o = (Varyings)0;
				UNITY_SETUP_INSTANCE_ID(attributes);
				UNITY_INITIALIZE_VERTEX_OUTPUT_STEREO(o);

				o.positionCS = TransformObjectToHClip(attributes.positionOS);
				#if defined(DEBUG_DISPLAY)
				o.positionWS = TransformObjectToWorld(attributes.positionOS);
				#endif
				o.uv = TRANSFORM_TEX(attributes.uv, _MainTex);
				o.color = attributes.color;
				return o;
			}

			float4 UnlitFragment(Varyings i) : SV_Target
			{
				float4 mainTex = i.color * SAMPLE_TEXTURE2D(_MainTex, sampler_MainTex, i.uv);

				#if defined(DEBUG_DISPLAY)
				SurfaceData2D surfaceData;
				InputData2D inputData;
				half4 debugColor = 0;

				InitializeSurfaceData(mainTex.rgb, mainTex.a, surfaceData);
				InitializeInputData(i.uv, inputData);
				SETUP_DEBUG_DATA_2D(inputData, i.positionWS);

				if(CanDebugOverrideOutputColor(surfaceData, inputData, debugColor))
				{
					return debugColor;
				}
				#endif

				return mainTex;
			}
			ENDHLSL
		}
	}
	CustomEditor "HueShiftMaterialInspector"
}
