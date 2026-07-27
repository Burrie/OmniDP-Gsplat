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
        float4 _GsplatOmniTex_TexelSize;
        // x = logical display height, y = top content row, z = native content height, w = padding enabled.
        float4 _GsplatOmniDisplayData;
        float4 _GsplatCompositeCameraForward;
        float4 _GsplatCompositeCameraRight;
        float4 _GsplatCompositeCameraUp;
        float4 _GsplatCompositeProjectionData;
        float4x4 _GsplatOmniWorldToCamera;

        struct GsplatCompositeV2f
        {
            float2 uv : TEXCOORD0;
            float4 vertex : SV_POSITION;
            UNITY_VERTEX_OUTPUT_STEREO
        };

        GsplatCompositeV2f GsplatCompositeVert(uint vertexID : SV_VertexID)
        {
            GsplatCompositeV2f o;
            UNITY_INITIALIZE_OUTPUT(GsplatCompositeV2f, o);
            UNITY_INITIALIZE_VERTEX_OUTPUT_STEREO(o);

            float2 uv = float2((vertexID << 1) & 2, vertexID & 2);
            o.vertex = float4(uv * 2.0 - 1.0, 0.0, 1.0);
            o.uv = uv;
            return o;
        }

        float GsplatWrap01(float value)
        {
            return value - floor(value);
        }

        float4 GsplatSampleLogicalPixel(float logicalX, float logicalY)
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
            return tex2D(_GsplatOmniTex, float2(u, v));
        }

        float4 GsplatSampleErpWrapped(float2 uv)
        {
            float2 displaySize = float2(_GsplatOmniTex_TexelSize.z,
                max(_GsplatOmniDisplayData.x, 1.0));
            float2 pixel = float2(GsplatWrap01(uv.x) * displaySize.x - 0.5,
                saturate(uv.y) * displaySize.y - 0.5);
            float2 basePixel = floor(pixel);
            float2 blend = pixel - basePixel;

            float4 s00 = GsplatSampleLogicalPixel(basePixel.x, basePixel.y);
            float4 s10 = GsplatSampleLogicalPixel(basePixel.x + 1.0, basePixel.y);
            float4 s01 = GsplatSampleLogicalPixel(basePixel.x, basePixel.y + 1.0);
            float4 s11 = GsplatSampleLogicalPixel(basePixel.x + 1.0, basePixel.y + 1.0);
            return lerp(lerp(s00, s10, blend.x), lerp(s01, s11, blend.x), blend.y);
        }

        float2 GsplatDirectionToErpUv(float2 screenUv)
        {
            float2 ndc = screenUv * 2.0 - 1.0;
            float tanHalfVerticalFov = _GsplatCompositeProjectionData.x;
            float aspect = _GsplatCompositeProjectionData.y;
            float3 worldDir = normalize(
                _GsplatCompositeCameraForward.xyz +
                _GsplatCompositeCameraRight.xyz * ndc.x * tanHalfVerticalFov * aspect +
                _GsplatCompositeCameraUp.xyz * ndc.y * tanHalfVerticalFov);
            float3 erpView = mul((float3x3)_GsplatOmniWorldToCamera, worldDir);
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
            #pragma vertex GsplatCompositeVert
            #pragma fragment frag

            UNITY_DECLARE_SCREENSPACE_TEXTURE(_BlitTexture);
            float4 frag(GsplatCompositeV2f i) : SV_Target
            {
                UNITY_SETUP_STEREO_EYE_INDEX_POST_VERTEX(i);
                float4 scene = UNITY_SAMPLE_SCREENSPACE_TEXTURE(_BlitTexture, i.uv);
                float4 splat = GsplatSampleErpWrapped(GsplatDirectionToErpUv(i.uv));
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
            #pragma vertex vert_img
            #pragma fragment frag

            sampler2D _BlitTexture;

            float4 frag(v2f_img i) : SV_Target
            {
                float4 scene = tex2D(_BlitTexture, i.uv);
                float4 splat = GsplatSampleErpWrapped(GsplatDirectionToErpUv(i.uv));
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
            #pragma vertex GsplatCompositeVert
            #pragma fragment frag

            float4 frag(GsplatCompositeV2f i) : SV_Target
            {
                UNITY_SETUP_STEREO_EYE_INDEX_POST_VERTEX(i);
                return GsplatSampleErpWrapped(GsplatDirectionToErpUv(i.uv));
            }
            ENDHLSL
        }
    }
}
