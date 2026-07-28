// Copyright (c) 2026
// SPDX-License-Identifier: MIT

Shader "Gsplat/ERPToPerspective"
{
    Properties {}
    SubShader
    {
        Tags
        {
            "RenderType"="Opaque"
            "Queue"="Overlay"
        }

        HLSLINCLUDE
        #include "UnityCG.cginc"

        #ifndef UNITY_PI
        #define UNITY_PI 3.14159265358979323846
        #endif

        sampler2D _GsplatOmniTex;
        sampler2D _GsplatOmniTexRight;
        float4 _GsplatOmniTex_TexelSize;
        // x = logical display height, y = top content row, z = native content height, w = padding enabled.
        float4 _GsplatOmniDisplayData;
        int _GsplatCompositeStereoEnabled;
        float4x4 _GsplatCompositeCameraToWorld[2];
        // xy = projection diagonal, zw = asymmetric projection offsets (m02, m12).
        float4 _GsplatCompositeProjectionData[2];
        float4x4 _GsplatOmniWorldToCamera[2];

        UNITY_DECLARE_SCREENSPACE_TEXTURE(_BlitTexture);
        float4 _BlitTexture_TexelSize;

        struct GsplatCompositeV2f
        {
            float2 uv : TEXCOORD0;
            float4 vertex : SV_POSITION;
            UNITY_VERTEX_OUTPUT_STEREO
        };

        struct GsplatProceduralInput
        {
            uint vertexID : SV_VertexID;
            UNITY_VERTEX_INPUT_INSTANCE_ID
        };

        struct GsplatBlitInput
        {
            float4 vertex : POSITION;
            float2 texcoord : TEXCOORD0;
            UNITY_VERTEX_INPUT_INSTANCE_ID
        };

        GsplatCompositeV2f GsplatCompositeVert(GsplatProceduralInput input)
        {
            GsplatCompositeV2f o;
            UNITY_SETUP_INSTANCE_ID(input);
            UNITY_INITIALIZE_OUTPUT(GsplatCompositeV2f, o);
            UNITY_INITIALIZE_VERTEX_OUTPUT_STEREO(o);

            float2 uv = float2((input.vertexID << 1) & 2, input.vertexID & 2);
            o.vertex = float4(uv * 2.0 - 1.0, 0.0, 1.0);
            o.uv = uv;
            return o;
        }

        GsplatCompositeV2f GsplatCompositeBlitVert(GsplatBlitInput input)
        {
            GsplatCompositeV2f o;
            UNITY_SETUP_INSTANCE_ID(input);
            UNITY_INITIALIZE_OUTPUT(GsplatCompositeV2f, o);
            UNITY_INITIALIZE_VERTEX_OUTPUT_STEREO(o);
            o.vertex = UnityObjectToClipPos(input.vertex);
            o.uv = input.texcoord;
            #if UNITY_UV_STARTS_AT_TOP
            if (_BlitTexture_TexelSize.y < 0.0)
                o.uv.y = 1.0 - o.uv.y;
            #endif
            return o;
        }

        uint GsplatCompositeEyeIndex()
        {
            return _GsplatCompositeStereoEnabled != 0 ? min((uint)unity_StereoEyeIndex, 1u) : 0u;
        }

        float GsplatWrap01(float value)
        {
            return value - floor(value);
        }

        float4 GsplatSampleNative(uint eyeIndex, float2 uv)
        {
            return eyeIndex == 0u
                ? tex2D(_GsplatOmniTex, uv)
                : tex2D(_GsplatOmniTexRight, uv);
        }

        float4 GsplatSampleLogicalPixel(uint eyeIndex, float logicalX, float logicalY)
        {
            float nativeWidth = _GsplatOmniTex_TexelSize.z;
            float nativeHeight = _GsplatOmniTex_TexelSize.w;
            float displayHeight = max(_GsplatOmniDisplayData.x, 1.0);
            float contentOffset = _GsplatOmniDisplayData.y;

            logicalY = clamp(logicalY, 0.0, displayHeight - 1.0);
            if (logicalY < contentOffset || logicalY >= contentOffset + nativeHeight)
                return float4(0.0, 0.0, 0.0, 1.0);

            float u = GsplatWrap01((logicalX + 0.5) / nativeWidth);
            float v = (logicalY - contentOffset + 0.5) / nativeHeight;
            return GsplatSampleNative(eyeIndex, float2(u, v));
        }

        float4 GsplatSampleErpWrapped(uint eyeIndex, float2 uv)
        {
            float2 displaySize = float2(_GsplatOmniTex_TexelSize.z,
                max(_GsplatOmniDisplayData.x, 1.0));
            float2 pixel = float2(GsplatWrap01(uv.x) * displaySize.x - 0.5,
                saturate(uv.y) * displaySize.y - 0.5);
            float2 basePixel = floor(pixel);
            float2 blend = pixel - basePixel;

            float4 s00 = GsplatSampleLogicalPixel(eyeIndex, basePixel.x, basePixel.y);
            float4 s10 = GsplatSampleLogicalPixel(eyeIndex, basePixel.x + 1.0, basePixel.y);
            float4 s01 = GsplatSampleLogicalPixel(eyeIndex, basePixel.x, basePixel.y + 1.0);
            float4 s11 = GsplatSampleLogicalPixel(eyeIndex, basePixel.x + 1.0, basePixel.y + 1.0);
            return lerp(lerp(s00, s10, blend.x), lerp(s01, s11, blend.x), blend.y);
        }

        float2 GsplatDirectionToErpUv(uint eyeIndex, float2 screenUv)
        {
            float2 ndc = screenUv * 2.0 - 1.0;
            float4 projectionData = _GsplatCompositeProjectionData[eyeIndex];
            float projectionX = abs(projectionData.x) > 0.0000001 ? projectionData.x : 1.0;
            float projectionY = abs(projectionData.y) > 0.0000001 ? projectionData.y : 1.0;
            float3 viewDir = normalize(float3(
                (ndc.x + projectionData.z) / projectionX,
                (ndc.y + projectionData.w) / projectionY,
                -1.0));
            float3 worldDir = normalize(mul((float3x3)_GsplatCompositeCameraToWorld[eyeIndex], viewDir));
            float3 erpView = mul((float3x3)_GsplatOmniWorldToCamera[eyeIndex], worldDir);
            float3 omniDir = normalize(float3(erpView.x, erpView.y, -erpView.z));

            float distXZ = max(length(omniDir.xz), 0.0000001);
            float lat = atan2(omniDir.y, distXZ);
            float lon = atan2(omniDir.x, omniDir.z);
            return float2(lon / (2.0 * UNITY_PI) + 0.5, 0.5 - lat / UNITY_PI);
        }
        ENDHLSL

        Pass
        {
            Name "SRPBlit"
            ZWrite Off
            ZTest Always
            Cull Off

            HLSLPROGRAM
            #pragma target 4.5
            #pragma vertex GsplatCompositeVert
            #pragma fragment frag
            #pragma multi_compile_instancing

            float4 frag(GsplatCompositeV2f i) : SV_Target
            {
                UNITY_SETUP_STEREO_EYE_INDEX_POST_VERTEX(i);
                uint eyeIndex = GsplatCompositeEyeIndex();
                float4 scene = UNITY_SAMPLE_SCREENSPACE_TEXTURE(_BlitTexture, i.uv);
                float4 splat = GsplatSampleErpWrapped(eyeIndex,
                    GsplatDirectionToErpUv(eyeIndex, i.uv));
                return float4(splat.rgb + scene.rgb * (1.0 - splat.a),
                    saturate(splat.a + scene.a * (1.0 - splat.a)));
            }
            ENDHLSL
        }

        Pass
        {
            Name "BuiltInBlit"
            ZWrite Off
            ZTest Always
            Cull Off

            HLSLPROGRAM
            #pragma target 4.5
            #pragma vertex GsplatCompositeBlitVert
            #pragma fragment frag
            #pragma multi_compile_instancing

            float4 frag(GsplatCompositeV2f i) : SV_Target
            {
                UNITY_SETUP_STEREO_EYE_INDEX_POST_VERTEX(i);
                uint eyeIndex = GsplatCompositeEyeIndex();
                float4 scene = UNITY_SAMPLE_SCREENSPACE_TEXTURE(_BlitTexture, i.uv);
                float4 splat = GsplatSampleErpWrapped(eyeIndex,
                    GsplatDirectionToErpUv(eyeIndex, i.uv));
                return float4(splat.rgb + scene.rgb * (1.0 - splat.a),
                    saturate(splat.a + scene.a * (1.0 - splat.a)));
            }
            ENDHLSL
        }

        Pass
        {
            Name "OverlayOnly"
            ZWrite Off
            ZTest Always
            Cull Off
            Blend One OneMinusSrcAlpha

            HLSLPROGRAM
            #pragma target 4.5
            #pragma vertex GsplatCompositeVert
            #pragma fragment frag
            #pragma multi_compile_instancing

            float4 frag(GsplatCompositeV2f i) : SV_Target
            {
                UNITY_SETUP_STEREO_EYE_INDEX_POST_VERTEX(i);
                uint eyeIndex = GsplatCompositeEyeIndex();
                return GsplatSampleErpWrapped(eyeIndex,
                    GsplatDirectionToErpUv(eyeIndex, i.uv));
            }
            ENDHLSL
        }
    }
}
