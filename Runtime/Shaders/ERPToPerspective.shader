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

                float4 splat = tex2D(_GsplatOmniTex, erpUv);
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
            float4 _GsplatCompositeCameraForward;
            float4 _GsplatCompositeCameraRight;
            float4 _GsplatCompositeCameraUp;
            float4 _GsplatCompositeProjectionData;
            float4x4 _GsplatOmniWorldToCamera;

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

                float4 splat = tex2D(_GsplatOmniTex, erpUv);
                return float4(splat.rgb + scene.rgb * (1.0 - splat.a),
                    saturate(splat.a + scene.a * (1.0 - splat.a)));
            }
            ENDHLSL
        }
    }
}
