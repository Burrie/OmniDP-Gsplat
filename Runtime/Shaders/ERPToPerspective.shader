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

        Pass
        {
            Name "SRPBlit"
            ZWrite Off
            ZTest Always
            Cull Off

            HLSLPROGRAM
            #pragma vertex vert
            #pragma fragment frag

            #include "UnityCG.cginc"

            #ifndef UNITY_PI
            #define UNITY_PI 3.14159265358979323846
            #endif

            sampler2D _BlitTexture;
            sampler2D _GsplatOmniTex;
            float4 _GsplatOmniTex_TexelSize;
            float4 _GsplatCompositeCameraForward;
            float4 _GsplatCompositeCameraRight;
            float4 _GsplatCompositeCameraUp;
            float4 _GsplatCompositeProjectionData;
            float4x4 _GsplatOmniWorldToCamera;

            struct v2f
            {
                float2 uv : TEXCOORD0;
                float4 vertex : SV_POSITION;
                UNITY_VERTEX_OUTPUT_STEREO
            };

            v2f vert(uint vertexID : SV_VertexID)
            {
                v2f o;
                UNITY_INITIALIZE_OUTPUT(v2f, o);
                UNITY_INITIALIZE_VERTEX_OUTPUT_STEREO(o);

                float2 uv = float2((vertexID << 1) & 2, vertexID & 2);
                o.vertex = float4(uv * 2.0 - 1.0, 0.0, 1.0);
                o.uv = uv;
                return o;
            }

            float Wrap01(float value)
            {
                return value - floor(value);
            }

            float4 SampleErpWrapped(float2 uv)
            {
                float2 texelSize = _GsplatOmniTex_TexelSize.xy;
                float2 texSize = _GsplatOmniTex_TexelSize.zw;
                float2 pixel = float2(Wrap01(uv.x) * texSize.x - 0.5,
                    saturate(uv.y) * texSize.y - 0.5);
                float2 basePixel = floor(pixel);
                float2 blend = pixel - basePixel;

                float x0 = basePixel.x;
                float x1 = x0 + 1.0;
                float y0 = clamp(basePixel.y, 0.0, texSize.y - 1.0);
                float y1 = clamp(basePixel.y + 1.0, 0.0, texSize.y - 1.0);

                float u0 = Wrap01((x0 + 0.5) * texelSize.x);
                float u1 = Wrap01((x1 + 0.5) * texelSize.x);
                float v0 = (y0 + 0.5) * texelSize.y;
                float v1 = (y1 + 0.5) * texelSize.y;

                float4 s00 = tex2D(_GsplatOmniTex, float2(u0, v0));
                float4 s10 = tex2D(_GsplatOmniTex, float2(u1, v0));
                float4 s01 = tex2D(_GsplatOmniTex, float2(u0, v1));
                float4 s11 = tex2D(_GsplatOmniTex, float2(u1, v1));
                return lerp(lerp(s00, s10, blend.x), lerp(s01, s11, blend.x), blend.y);
            }

            float4 frag(v2f i) : SV_Target
            {
                UNITY_SETUP_STEREO_EYE_INDEX_POST_VERTEX(i);
                float4 scene = tex2D(_BlitTexture, i.uv);

                float2 ndc = i.uv * 2.0 - 1.0;
                float tanHalfVerticalFov = _GsplatCompositeProjectionData.x;
                float aspect = _GsplatCompositeProjectionData.y;
                float3 worldDir = normalize(
                    _GsplatCompositeCameraForward.xyz +
                    _GsplatCompositeCameraRight.xyz * ndc.x * tanHalfVerticalFov * aspect +
                    _GsplatCompositeCameraUp.xyz * ndc.y * tanHalfVerticalFov);
                float3 erpView = mul((float3x3)_GsplatOmniWorldToCamera, worldDir);
                float3 omniDir = normalize(float3(erpView.x, erpView.y, -erpView.z));

                float distXZ = max(length(omniDir.xz), 0.0000001);
                float lat = atan2(-omniDir.y, distXZ);
                float lon = atan2(omniDir.x, omniDir.z);
                float2 erpUv = float2(lon / (2.0 * UNITY_PI) + 0.5, 0.5 - lat / UNITY_PI);

                float4 splat = SampleErpWrapped(erpUv);
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

            #include "UnityCG.cginc"

            #ifndef UNITY_PI
            #define UNITY_PI 3.14159265358979323846
            #endif

            sampler2D _BlitTexture;
            sampler2D _GsplatOmniTex;
            float4 _GsplatOmniTex_TexelSize;
            float4 _GsplatCompositeCameraForward;
            float4 _GsplatCompositeCameraRight;
            float4 _GsplatCompositeCameraUp;
            float4 _GsplatCompositeProjectionData;
            float4x4 _GsplatOmniWorldToCamera;

            float Wrap01(float value)
            {
                return value - floor(value);
            }

            float4 SampleErpWrapped(float2 uv)
            {
                float2 texelSize = _GsplatOmniTex_TexelSize.xy;
                float2 texSize = _GsplatOmniTex_TexelSize.zw;
                float2 pixel = float2(Wrap01(uv.x) * texSize.x - 0.5,
                    saturate(uv.y) * texSize.y - 0.5);
                float2 basePixel = floor(pixel);
                float2 blend = pixel - basePixel;

                float x0 = basePixel.x;
                float x1 = x0 + 1.0;
                float y0 = clamp(basePixel.y, 0.0, texSize.y - 1.0);
                float y1 = clamp(basePixel.y + 1.0, 0.0, texSize.y - 1.0);

                float u0 = Wrap01((x0 + 0.5) * texelSize.x);
                float u1 = Wrap01((x1 + 0.5) * texelSize.x);
                float v0 = (y0 + 0.5) * texelSize.y;
                float v1 = (y1 + 0.5) * texelSize.y;

                float4 s00 = tex2D(_GsplatOmniTex, float2(u0, v0));
                float4 s10 = tex2D(_GsplatOmniTex, float2(u1, v0));
                float4 s01 = tex2D(_GsplatOmniTex, float2(u0, v1));
                float4 s11 = tex2D(_GsplatOmniTex, float2(u1, v1));
                return lerp(lerp(s00, s10, blend.x), lerp(s01, s11, blend.x), blend.y);
            }

            float4 frag(v2f_img i) : SV_Target
            {
                float4 scene = tex2D(_BlitTexture, i.uv);

                float2 ndc = i.uv * 2.0 - 1.0;
                float tanHalfVerticalFov = _GsplatCompositeProjectionData.x;
                float aspect = _GsplatCompositeProjectionData.y;
                float3 worldDir = normalize(
                    _GsplatCompositeCameraForward.xyz +
                    _GsplatCompositeCameraRight.xyz * ndc.x * tanHalfVerticalFov * aspect +
                    _GsplatCompositeCameraUp.xyz * ndc.y * tanHalfVerticalFov);
                float3 erpView = mul((float3x3)_GsplatOmniWorldToCamera, worldDir);
                float3 omniDir = normalize(float3(erpView.x, erpView.y, -erpView.z));

                float distXZ = max(length(omniDir.xz), 0.0000001);
                float lat = atan2(-omniDir.y, distXZ);
                float lon = atan2(omniDir.x, omniDir.z);
                float2 erpUv = float2(lon / (2.0 * UNITY_PI) + 0.5, 0.5 - lat / UNITY_PI);

                float4 splat = SampleErpWrapped(erpUv);
                return float4(splat.rgb + scene.rgb * (1.0 - splat.a),
                    saturate(splat.a + scene.a * (1.0 - splat.a)));
            }
            ENDHLSL
        }
    }
}
