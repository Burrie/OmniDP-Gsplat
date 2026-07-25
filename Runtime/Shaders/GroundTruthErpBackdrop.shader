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
    }
    SubShader
    {
        Tags
        {
            "RenderType"="Background"
            "Queue"="Background"
            "IgnoreProjector"="True"
        }

        Pass
        {
            Cull Front
            ZWrite Off
            ZTest Always

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
            };

            struct v2f
            {
                float4 vertex : SV_POSITION;
                float3 localDirection : TEXCOORD0;
            };

            v2f vert(appdata input)
            {
                v2f output;
                output.vertex = UnityObjectToClipPos(input.vertex);
                output.localDirection = input.vertex.xyz;
                return output;
            }

            float4 frag(v2f input) : SV_Target
            {
                float3 direction = normalize(input.localDirection);
                float distXZ = max(length(direction.xz), 0.0000001);
                float longitude = atan2(direction.x, direction.z);
                // This deliberately matches the ERP convention used by Gsplat/ERPToPerspective.
                float latitude = atan2(-direction.y, distXZ);
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
