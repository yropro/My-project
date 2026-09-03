Shader "Sprites/Lit-Default-HueShift"
{
	Properties
	{
		_MainTex ("Sprite Texture", 2D) = "white" {}
		_NormalMap ("Normal Map", 2D) = "bump" {}
		_MaskTex("Mask", 2D) = "white" {}
		_Color ("Hue Shift", Color) = (1,1,1,1)

        _StencilComp ("Stencil Comparison", Float) = 8
        _Stencil ("Stencil ID", Float) = 0
        _StencilOp ("Stencil Operation", Float) = 0
        _StencilWriteMask ("Stencil Write Mask", Float) = 255
        _StencilReadMask ("Stencil Read Mask", Float) = 255
        _ColorMask ("Color Mask", Float) = 15

		// Legacy properties. They're here so that materials using this shader can gracefully fallback to the legacy sprite shader.
		[HideInInspector] _RendererColor ("RendererColor", Color) = (1,1,1,1)
		[HideInInspector] _Flip ("Flip", Vector) = (1,1,1,1)
		[PerRendererData] _AlphaTex ("External Alpha", 2D) = "white" {}
		[PerRendererData] _EnableExternalAlpha ("Enable External Alpha", Float) = 0

		[HideInInspector] _HueRangeMin("Min Hue Range", Range(0, 1)) = 0
		[HideInInspector] _HueRangeMax("Max Hue Range", Range(0, 1)) = 1

		// Laser slice clipping
		[HideInInspector] _ClipEnabled("Clip Enabled", Float) = 0
		[HideInInspector] _ClipLinePoint("Clip Line Point", Vector) = (0,0,0,0)
		[HideInInspector] _ClipLineNormal("Clip Line Normal", Vector) = (0,1,0,0)

		// Molten metal emission
		[HideInInspector] _EmissionEnabled("Emission Enabled", Float) = 0
		[HideInInspector] [HDR] _EmissionColor("Emission Color", Color) = (3,1.5,0.3,1)
		[HideInInspector] _EmissionOrigin("Emission Origin", Vector) = (0,0,0,0)
		[HideInInspector] _EmissionRadius("Emission Radius", Float) = 1.0
		[HideInInspector] _EmissionIntensity("Emission Intensity", Float) = 0
		[HideInInspector] _EmissionSeed("Emission Seed", Float) = 0
		[HideInInspector] _ErosionProgress("Erosion Progress", Float) = 0
		[HideInInspector] _CrackDarken("Crack Darken", Float) = 0

		// Debuff tint overlay
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
		Blend SrcAlpha OneMinusSrcAlpha, One OneMinusSrcAlpha

		Pass
		{
		Tags { "LightMode" = "Universal2D" }
		HLSLPROGRAM
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"

			#pragma vertex CombinedShapeLightVertex
            #pragma fragment CombinedShapeLightFragment

			#pragma multi_compile USE_SHAPE_LIGHT_TYPE_0 __
            #pragma multi_compile USE_SHAPE_LIGHT_TYPE_1 __
            #pragma multi_compile USE_SHAPE_LIGHT_TYPE_2 __
            #pragma multi_compile USE_SHAPE_LIGHT_TYPE_3 __
			#pragma multi_compile _ DEBUG_DISPLAY

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
                half2   lightingUV  : TEXCOORD1;
				#if defined(DEBUG_DISPLAY)
				float3  positionWS  : TEXCOORD2;
				#endif
				float3  positionOS  : TEXCOORD3;
				float2  positionDS  : TEXCOORD4;   // damage-space position (shared across ship parts)
                UNITY_VERTEX_OUTPUT_STEREO
            };

			#include "HueShift.cginc"
			#include "Packages/com.unity.render-pipelines.universal/Shaders/2D/Include/LightingUtility.hlsl"

			TEXTURE2D(_MainTex);
			SAMPLER(sampler_MainTex);
            TEXTURE2D(_MaskTex);
			SAMPLER(sampler_MaskTex);

			// Scene glow pickup (set globally by SceneGlowFeature — blurred HDR emitters)
			TEXTURE2D(_SceneGlowTex);
			SAMPLER(sampler_SceneGlowTex);
			float _SceneGlowStrength;
			float _SceneGlowMaxBrightness;

			#include "HueShiftPerMaterial.hlsl"

			// Hash-based noise function
			float hash(float2 p)
			{
				float3 p3 = frac(float3(p.xyx) * 0.1031);
				p3 += dot(p3, p3.yzx + 33.33);
				return frac((p3.x + p3.y) * p3.z);
			}

			float noise(float2 p)
			{
				float2 i = floor(p);
				float2 f = frac(p);
				f = f * f * (3.0 - 2.0 * f); // Smoothstep

				float a = hash(i);
				float b = hash(i + float2(1.0, 0.0));
				float c = hash(i + float2(0.0, 1.0));
				float d = hash(i + float2(1.0, 1.0));

				return lerp(lerp(a, b, f.x), lerp(c, d, f.x), f.y);
			}

			float fbm(float2 p)
			{
				float value = 0.0;
				value += 0.5 * noise(p); p *= 2.0;
				value += 0.25 * noise(p); p *= 2.0;
				value += 0.125 * noise(p);
				return value;
			}

			#if USE_SHAPE_LIGHT_TYPE_0
            SHAPE_LIGHT(0)
            #endif

            #if USE_SHAPE_LIGHT_TYPE_1
            SHAPE_LIGHT(1)
            #endif

            #if USE_SHAPE_LIGHT_TYPE_2
            SHAPE_LIGHT(2)
            #endif

            #if USE_SHAPE_LIGHT_TYPE_3
            SHAPE_LIGHT(3)
            #endif

			Varyings CombinedShapeLightVertex(Attributes v)
            {
                Varyings o = (Varyings)0;
                UNITY_SETUP_INSTANCE_ID(v);
                UNITY_INITIALIZE_VERTEX_OUTPUT_STEREO(o);

                o.positionCS = TransformObjectToHClip(v.positionOS);
				#if defined(DEBUG_DISPLAY)
				o.positionWS = TransformObjectToWorld(v.positionOS);
				#endif
                o.uv = TRANSFORM_TEX(v.uv, _MainTex);
                o.lightingUV = half2(ComputeScreenPos(o.positionCS / o.positionCS.w).xy);

                o.color = v.color * _Color * _RendererColor;
				o.positionOS = v.positionOS;
				o.positionDS = mul(_DamageMatrix, float4(v.positionOS, 1.0)).xy;
                return o;
            }

			#include "Packages/com.unity.render-pipelines.universal/Shaders/2D/Include/CombinedShapeLightShared.hlsl"

			half4 CombinedShapeLightFragment(Varyings i) : SV_Target
            {
				// Laser slice: discard pixels on wrong side of cut line
				if (_ClipEnabled > 0.5)
				{
					float2 toPixel = i.positionOS.xy - _ClipLinePoint.xy;
					clip(dot(toPixel, _ClipLineNormal.xy));
				}

                const half4 main = SAMPLE_TEXTURE2D(_MainTex, sampler_MainTex, i.uv);
				const half4 mask = SAMPLE_TEXTURE2D(_MaskTex, sampler_MaskTex, i.uv);

				half4 c = applyHSV(main, i.color, _HueRangeMin, _HueRangeMax);

				// Debuff tint overlay (burning char, frozen ice, etc.)
				if (_DebuffBlend > 0.001)
				{
					float blendMask = _DebuffBlend * main.a;

					if (_DebuffNoiseScale > 0.001)
					{
						float2 noiseCoord = i.uv * _DebuffNoiseScale;

						if (_DebuffSplatHardness > 0.5)
						{
							// Hard-edged splat pattern (e.g. acid corrosion)
							float n1 = noise(noiseCoord);
							float n2 = noise(noiseCoord * 0.5 + float2(31.7, 17.3));
							float splat = max(n1, n2);
							blendMask *= step(0.68, splat);
						}
						else
						{
							// Soft speckled pattern (e.g. charring)
							float n = fbm(noiseCoord);
							blendMask *= saturate((n - 0.3) * 3.0);
						}
					}

					c.rgb = lerp(c.rgb, _DebuffTint.rgb, blendMask);
				}

				// Erosion effect - burn away near blast point (applied first, under cracks)
				if (_EmissionEnabled > 0.5 && _ErosionProgress > 0.001)
				{
					float2 erosionPos = i.positionDS.xy - _EmissionOrigin.xy;
					float erosionDist = length(erosionPos);
					float normalizedDist = erosionDist / _EmissionRadius;

					// Generate noise for irregular erosion edge
					float2 noiseCoord = i.positionDS.xy * 8.0 + _EmissionSeed;
					float erosionNoise = fbm(noiseCoord);

					// Erosion threshold: closer to origin = erodes first
					// Noise creates irregular burn patches
					float erosionThreshold = normalizedDist * 0.7 + erosionNoise * 0.2;

					// Current erosion level (linear spread)
					float erosionLevel = _ErosionProgress * 0.45;

					// Calculate how close we are to the erosion edge
					float edgeDist = erosionThreshold - erosionLevel;

					// Charring zone (just before erosion)
					float charWidth = 0.15;
					if (edgeDist < charWidth && edgeDist > 0.0)
					{
						float charAmount = 1.0 - (edgeDist / charWidth);
						charAmount = charAmount * charAmount; // Intensify near edge
						c.rgb = lerp(c.rgb, float3(0.02, 0.01, 0.005), charAmount * 0.9);
					}

					// Erode (make transparent) if past threshold
					if (edgeDist < 0.0)
					{
						c.a = 0.0;
					}
				}

				// Fracture crack pattern (applied on top of erosion)
				if (_EmissionEnabled > 0.5)
				{
					float2 pos = i.positionDS.xy - _EmissionOrigin.xy;
					float dist = length(pos);
					float falloff = saturate(1.0 - dist / _EmissionRadius);

					// Skip if outside radius
					if (falloff > 0.001)
					{
						// Convert to polar coordinates for radial cracks
						float angle = atan2(pos.y, pos.x);
						float seed = _EmissionSeed;
						float crack = 0.0;

						// Primary radial cracks (5-8 main cracks) — widened
						float numCracks = 6.0 + frac(seed * 3.7) * 2.0;
						float crackAngle = angle * numCracks + seed * 10.0;
						float radialCrack = abs(frac(crackAngle / 6.283) - 0.5) * 2.0;
						radialCrack = 1.0 - saturate(radialCrack * 2.0);

						// Secondary branching cracks — widened
						float branchFreq = 12.0 + frac(seed * 7.3) * 6.0;
						float branchAngle = angle * branchFreq + seed * 20.0 + dist * 15.0;
						float branchCrack = abs(frac(branchAngle / 6.283) - 0.5) * 2.0;
						branchCrack = 1.0 - saturate(branchCrack * 2.5);
						branchCrack *= saturate(dist / _EmissionRadius * 2.0);

						// Concentric ring cracks — widened
						float ringFreq = 3.0 + frac(seed * 5.1) * 2.0;
						float ring = frac(dist * ringFreq / _EmissionRadius + seed);
						float ringCrack = 1.0 - saturate(abs(ring - 0.5) * 5.0);
						ringCrack *= 0.7;

						// Combine crack patterns
						crack = max(radialCrack, max(branchCrack, ringCrack));
						crack *= falloff;

						// Dark crack rendering
						c.rgb *= 1.0 - crack * _CrackDarken * 0.97;

						// Additive emission glow only when intensity is active
						if (_EmissionIntensity > 0.001)
						{
							float edgeFactor = saturate(fwidth(main.a) * 10.0);
							crack = max(crack, edgeFactor * falloff);

							float emission = crack * _EmissionIntensity * 3.0;
							c.rgb += _EmissionColor.rgb * emission * main.a;
						}
					}
				}

				SurfaceData2D surfaceData;
				InputData2D inputData;

				InitializeSurfaceData(c.rgb, c.a, mask, surfaceData);
				InitializeInputData(i.uv, i.lightingUV, inputData);

				half4 lit = CombinedShapeLightShared(surfaceData, inputData);

				// Nearby HDR emitters (flames, explosions, beams) light the hull.
				// Soft-cap by luminance so boosted large emitters saturate instead of fogging;
				// modulate by hull LUMINANCE (not per-channel albedo — that kills complementary
				// colours, e.g. green light on a red hull) so the light keeps its own colour.
				half3 sceneGlow = SAMPLE_TEXTURE2D(_SceneGlowTex, sampler_SceneGlowTex, i.lightingUV).rgb;
				sceneGlow /= 1.0 + dot(sceneGlow, half3(0.2126, 0.7152, 0.0722));
				half hullLuma = dot(saturate(c.rgb), half3(0.2126, 0.7152, 0.0722));
				// Hard-cap the FINAL added light (the soft-cap bounds only luminance, and
				// the strength multiplier sits after it, so stacked emitters could still
				// add several times white). Peak-channel scale keeps the light's hue.
				half3 glowAdd = sceneGlow * _SceneGlowStrength * (0.35 + 0.65 * hullLuma);
				half glowPeak = max(glowAdd.r, max(glowAdd.g, glowAdd.b));
				glowAdd *= min(glowPeak, _SceneGlowMaxBrightness) / max(glowPeak, 0.0001);
				lit.rgb += glowAdd;

				return lit;
            }
		ENDHLSL
		}

		Pass
		{
			Tags { "LightMode" = "NormalsRendering"}

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
				float3  positionOS      : TEXCOORD4;
				UNITY_VERTEX_OUTPUT_STEREO
			};

			#include "HueShiftPerMaterial.hlsl"

			TEXTURE2D(_MainTex);
			SAMPLER(sampler_MainTex);
			TEXTURE2D(_NormalMap);
			SAMPLER(sampler_NormalMap);

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
				o.positionOS = attributes.positionOS;
				return o;
			}

			#include "Packages/com.unity.render-pipelines.universal/Shaders/2D/Include/NormalsRenderingShared.hlsl"

			half4 NormalsRenderingFragment(Varyings i) : SV_Target
			{
				// Laser slice: discard pixels on wrong side of cut line
				if (_ClipEnabled > 0.5)
				{
					float2 toPixel = i.positionOS.xy - _ClipLinePoint.xy;
					clip(dot(toPixel, _ClipLineNormal.xy));
				}

				const half4 mainTex = i.color * SAMPLE_TEXTURE2D(_MainTex, sampler_MainTex, i.uv);
				const half3 normalTS = UnpackNormal(SAMPLE_TEXTURE2D(_NormalMap, sampler_NormalMap, i.uv));

				return NormalsRenderingShared(mainTex, normalTS, i.tangentWS.xyz, i.bitangentWS.xyz, i.normalWS.xyz);
			}
			ENDHLSL
		}

		Pass
		{
			Tags { "LightMode" = "UniversalForward" "Queue"="Transparent" "RenderType"="Transparent"}

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
				float3  positionOS      : TEXCOORD3;
				UNITY_VERTEX_OUTPUT_STEREO
			};

			#include "HueShiftPerMaterial.hlsl"

			TEXTURE2D(_MainTex);
			SAMPLER(sampler_MainTex);

			Varyings UnlitVertex(Attributes attributes)
			{
				Varyings o = (Varyings)0;
				UNITY_SETUP_INSTANCE_ID(attributes);
				UNITY_INITIALIZE_VERTEX_OUTPUT_STEREO(o);

				o.positionCS = TransformObjectToHClip(attributes.positionOS);
				#if defined(DEBUG_DISPLAY)
				o.positionWS = TransformObjectToWorld(v.positionOS);
				#endif
				o.uv = TRANSFORM_TEX(attributes.uv, _MainTex);
				o.color = attributes.color;
				o.positionOS = attributes.positionOS;
				return o;
			}

			float4 UnlitFragment(Varyings i) : SV_Target
			{
				// Laser slice: discard pixels on wrong side of cut line
				if (_ClipEnabled > 0.5)
				{
					float2 toPixel = i.positionOS.xy - _ClipLinePoint.xy;
					clip(dot(toPixel, _ClipLineNormal.xy));
				}

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