// Copyright (c) 2026
// SPDX-License-Identifier: MIT

Shader "Hidden/Gsplat/Ground Truth ERP Backdrop"
{
    Properties
    {
        _MainTex ("ERP Frame", 2D) = "black" {}
        _LongitudeOffsetDegrees ("Longitude Offset Degrees", Float) = 0
        _FlipVertical ("Flip Vertical", Float) = 0
        _Exposure ("Exposure", Float) = 1
        [Enum(UnityEngine.Rendering.CompareFunction)] _DepthTest ("Depth Test", Float) = 4
    }
    SubShader
    {
        Tags
        {
            "RenderType"="Background"
            // Render immediately after the skybox. A Background-queue sphere is overwritten by a camera skybox
            // in Game/VR views even though it remains visible in the Scene view.
            "Queue"="Transparent-499"
            "IgnoreProjector"="True"
        }

        Pass
        {
            Cull Front
            ZWrite Off
            ZTest [_DepthTest]

            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #include "UnityCG.cginc"

            #ifndef UNITY_PI
            #define UNITY_PI 3.14159265358979323846
            #endif

            sampler2D _MainTex;
            float _LongitudeOffsetDegrees;
            float _FlipVertical;
            float _Exposure;

            struct appdata
            {
                float4 vertex : POSITION;
                UNITY_VERTEX_INPUT_INSTANCE_ID
            };

            struct v2f
            {
                float4 vertex : SV_POSITION;
                float3 localDirection : TEXCOORD0;
                UNITY_VERTEX_OUTPUT_STEREO
            };

            v2f vert(appdata input)
            {
                v2f output;
                UNITY_SETUP_INSTANCE_ID(input);
                UNITY_INITIALIZE_OUTPUT(v2f, output);
                UNITY_INITIALIZE_VERTEX_OUTPUT_STEREO(output);
                output.vertex = UnityObjectToClipPos(input.vertex);
                // Treat the ERP as an infinitely distant background. This lets normal opaque geometry remain in
                // front while the frame still passes after the skybox in the transparent render queue.
                #if defined(UNITY_REVERSED_Z)
                output.vertex.z = 0.0;
                #else
                output.vertex.z = output.vertex.w;
                #endif
                output.localDirection = input.vertex.xyz;
                return output;
            }

            float4 frag(v2f input) : SV_Target
            {
                UNITY_SETUP_STEREO_EYE_INDEX_POST_VERTEX(input);
                float3 direction = normalize(input.localDirection);
                float distXZ = max(length(direction.xz), 0.0000001);
                float longitude = atan2(direction.x, direction.z);
                // direction is Unity camera-local (+Y up). This deliberately matches the OpenMVG/CUDA ERP
                // convention after its required Y-axis conversion, and must match Gsplat/ERPToPerspective.
                float latitude = atan2(direction.y, distXZ);
                float2 uv = float2(
                    longitude / (2.0 * UNITY_PI) + 0.5 + _LongitudeOffsetDegrees / 360.0,
                    0.5 - latitude / UNITY_PI);
                if (_FlipVertical > 0.5)
                    uv.y = 1.0 - uv.y;
                float4 color = tex2D(_MainTex, uv);
                return float4(color.rgb * _Exposure, 1.0);
            }
            ENDCG
        }
    }
}
