// Gaussian Splatting helper functions & structs
// most of these are from https://github.com/playcanvas/engine/tree/main/src/scene/shader-lib/glsl/chunks/gsplat
// Copyright (c) 2011-2024 PlayCanvas Ltd
// Copyright (c) 2025 Yize Wu
// SPDX-License-Identifier: MIT

#ifndef GSPLAT_INCLUDED
#define GSPLAT_INCLUDED

struct SplatSource
{
    uint order;
    uint id;
    float2 cornerUV;
};

struct SplatCenter
{
    float3 view;
    float3 omniView;
    float4 proj;
    float4x4 modelView;
    float projMat00;
    float lon;
    float lat;
    float dist;
};

struct SplatCovariance
{
    float3 covA;
    float3 covB;
};

// stores the offset from center for the current gaussian
struct SplatCorner
{
    float2 offset; // corner offset from center in clip space
    float2 uv; // corner uv
    #if GSPLAT_AA
    float aaFactor; // for scenes generated with antialiasing
    #endif
};

const float4 discardVec = float4(0.0, 0.0, 2.0, 1.0);

int _GsplatProjectionMode;
float _GsplatOmniNearDistance;
float _GsplatOmniWrapOffset;
float4 _GsplatTargetSize;
int _PvgDynamic;
float _PvgTime;
float _PvgPeriod;
StructuredBuffer<float2> _PvgTimeBuffer;
StructuredBuffer<float3> _PvgVelocityBuffer;

#define GSPLAT_PROJECTION_PERSPECTIVE 0
#define GSPLAT_PROJECTION_HYBRID_OMNI 1
#define GSPLAT_EPSILON 0.0000001

float PvgAngularFrequency()
{
    return 2.0 * UNITY_PI / max(abs(_PvgPeriod), GSPLAT_EPSILON);
}

float3 ApplyPvgPosition(uint splatId, float3 modelCenter)
{
    if (_PvgDynamic == 0)
        return modelCenter;

    float2 pvgTimeData = _PvgTimeBuffer[splatId];
    float3 velocity = _PvgVelocityBuffer[splatId];
    float a = PvgAngularFrequency();
    return modelCenter + velocity * (sin((_PvgTime - pvgTimeData.x) * a) / a);
}

float ApplyPvgOpacity(uint splatId, float opacity)
{
    if (_PvgDynamic == 0)
        return opacity;

    float2 pvgTimeData = _PvgTimeBuffer[splatId];
    float beta = max(exp(pvgTimeData.y), GSPLAT_EPSILON);
    float dt = pvgTimeData.x - _PvgTime;
    return opacity * exp(-0.5 * dt * dt / (beta * beta));
}

bool InitCenter(float4x4 modelView, float3 modelCenter, out SplatCenter center)
{
    float4 centerView = mul(modelView, float4(modelCenter, 1.0));
    center.view = centerView.xyz / centerView.w;
    center.omniView = float3(center.view.x, center.view.y, -center.view.z);
    center.modelView = modelView;
    center.projMat00 = UNITY_MATRIX_P[0][0];
    center.lon = 0.0;
    center.lat = 0.0;
    center.dist = 0.0;

    if (_GsplatProjectionMode == GSPLAT_PROJECTION_HYBRID_OMNI)
    {
        center.dist = max(length(center.omniView), GSPLAT_EPSILON);
        if (center.dist <= max(_GsplatOmniNearDistance, GSPLAT_EPSILON))
        {
            return false;
        }

        float distXZ = max(length(center.omniView.xz), GSPLAT_EPSILON);
        center.lat = atan2(center.omniView.y, distXZ);
        center.lon = atan2(center.omniView.x, center.omniView.z);

        float2 uv = float2(center.lon / (2.0 * UNITY_PI) + 0.5 + _GsplatOmniWrapOffset,
            0.5 - center.lat / UNITY_PI);
        center.proj = float4(uv.x * 2.0 - 1.0, 1.0 - uv.y * 2.0, 0.0, 1.0);
        return true;
    }

    if (centerView.z > 0.0)
        return false;

    float4 centerProj = mul(UNITY_MATRIX_P, centerView);
    centerProj.z = clamp(centerProj.z, -abs(centerProj.w), abs(centerProj.w));
    center.proj = centerProj;
    return true;
}

float3x3 QuatToMat3(float4 R)
{
    float4 R2 = R + R;
    float X = R2.x * R.w;
    float4 Y = R2.y * R;
    float4 Z = R2.z * R;
    float W = R2.w * R.w;

    return float3x3(
        1.0 - Z.z - W,
        Y.z + X,
        Y.w - Z.x,
        Y.z - X,
        1.0 - Y.y - W,
        Z.w + Y.x,
        Y.w + Z.x,
        Z.w - Y.x,
        1.0 - Y.y - Z.z
    );
}

// quat format: w, x, y, z
SplatCovariance CalcCovariance(float4 quat, float3 scale)
{
    float3x3 rot = QuatToMat3(quat);

    // M = S * R
    float3x3 M = transpose(float3x3(
        scale.x * rot[0],
        scale.y * rot[1],
        scale.z * rot[2]
    ));

    SplatCovariance cov;
    cov.covA = float3(dot(M[0], M[0]), dot(M[0], M[1]), dot(M[0], M[2]));
    cov.covB = float3(dot(M[1], M[1]), dot(M[1], M[2]), dot(M[2], M[2]));
    return cov;
}

bool InitCornerFromCov(SplatSource source, float3x3 cov, SplatCenter center, out SplatCorner corner)
{
    #if GSPLAT_AA
    // calculate AA factor
    float detOrig = cov[0][0] * cov[1][1] - cov[0][1] * cov[0][1];
    float detBlur = (cov[0][0] + 0.3) * (cov[1][1] + 0.3) - cov[0][1] * cov[0][1];
    corner.aaFactor = sqrt(max(detOrig / detBlur, 0.0));
    #endif

    float diagonal1 = cov[0][0] + 0.3;
    float offDiagonal = cov[0][1];
    float diagonal2 = cov[1][1] + 0.3;

    float mid = 0.5 * (diagonal1 + diagonal2);
    float radius = length(float2((diagonal1 - diagonal2) / 2.0, offDiagonal));
    float lambda1 = mid + radius;
    float lambda2 = max(mid - radius, 0.1);

    float2 targetSize = _GsplatProjectionMode == GSPLAT_PROJECTION_HYBRID_OMNI
        ? _GsplatTargetSize.xy
        : _ScreenParams.xy;

    // Use the smaller viewport dimension to limit the kernel size relative to the render resolution.
    float vmin = min(1024.0, min(targetSize.x, targetSize.y));

    float l1 = 2.0 * min(sqrt(2.0 * lambda1), vmin);
    float l2 = 2.0 * min(sqrt(2.0 * lambda2), vmin);

    // early-out gaussians smaller than 2 pixels
    if (l1 < 2.0 && l2 < 2.0)
    {
        return false;
    }

    float2 c = _GsplatProjectionMode == GSPLAT_PROJECTION_HYBRID_OMNI
        ? 2.0 * _GsplatTargetSize.zw
        : center.proj.ww / _ScreenParams.xy;

    // cull against frustum x/y axes
    float maxL = max(l1, l2);
    float2 clipLimit = _GsplatProjectionMode == GSPLAT_PROJECTION_HYBRID_OMNI
        ? float2(1.0, 1.0)
        : center.proj.ww;
    if (any(abs(center.proj.xy) - float2(maxL, maxL) * c > clipLimit))
    {
        return false;
    }

    float2 diagonalVector = normalize(float2(offDiagonal, lambda1 - diagonal1));
    float2 v1 = l1 * diagonalVector;
    float2 v2 = l2 * float2(diagonalVector.y, -diagonalVector.x);

    corner.offset = (source.cornerUV.x * v1 + source.cornerUV.y * v2) * c;
    corner.uv = source.cornerUV;

    return true;
}

// calculate the clip-space offset from the center for this gaussian
bool InitCorner(SplatSource source, SplatCovariance covariance, SplatCenter center, out SplatCorner corner)
{
    float3 covA = covariance.covA;
    float3 covB = covariance.covB;
    float3x3 Vrk = float3x3(
        covA.x, covA.y, covA.z,
        covA.y, covB.x, covB.y,
        covA.z, covB.y, covB.z
    );

    if (_GsplatProjectionMode == GSPLAT_PROJECTION_HYBRID_OMNI)
    {
        float xScale = _GsplatTargetSize.x / (2.0 * UNITY_PI);
        float yScale = _GsplatTargetSize.y / UNITY_PI;
        float sinLat, cosLat, sinLon, cosLon;
        sincos(center.lat, sinLat, cosLat);
        sincos(center.lon, sinLon, cosLon);

        float3x3 sqj = float3x3(
            xScale / ((cosLat + GSPLAT_EPSILON) * center.dist), 0.0, 0.0,
            0.0, yScale / center.dist, 0.0,
            0.0, 0.0, 0.0
        );

        float3x3 sphericalFrame = float3x3(
            cosLon, 0.0, -sinLon,
            sinLat * sinLon, cosLat, sinLat * cosLon,
            cosLat * sinLon, -sinLat, cosLat * cosLon
        );

        float3x3 WUnity = (float3x3)center.modelView;
        float3x3 W = float3x3(
            WUnity[0][0], WUnity[0][1], WUnity[0][2],
            WUnity[1][0], WUnity[1][1], WUnity[1][2],
            -WUnity[2][0], -WUnity[2][1], -WUnity[2][2]
        );
        float3x3 jo = mul(mul(W, sphericalFrame), sqj);
        float3x3 cov = mul(mul(transpose(jo), Vrk), jo);
        return InitCornerFromCov(source, cov, center, corner);
    }

    float focal = _ScreenParams.x * center.projMat00;

    float3 v = unity_OrthoParams.w == 1.0 ? float3(0.0, 0.0, 1.0) : center.view.xyz;

    float J1 = focal / v.z;
    float2 J2 = -J1 / v.z * v.xy;
    float3x3 J = float3x3(
        J1, 0.0, J2.x,
        0.0, J1, J2.y,
        0.0, 0.0, 0.0
    );

    float3x3 W = (float3x3)center.modelView;
    float3x3 T = mul(J, W);
    float3x3 cov = mul(mul(T, Vrk), transpose(T));
    return InitCornerFromCov(source, cov, center, corner);
}

void ClipCorner(inout SplatCorner corner, float alpha)
{
    float clip = min(1.0, sqrt(-log(1.0 / 255.0 / alpha)) / 2.0);
    corner.offset *= clip;
    corner.uv *= clip;
}

// spherical Harmonics
#ifdef SH_BANDS_1
#define SH_COEFFS 3
#elif defined(SH_BANDS_2)
#define SH_COEFFS 8
#elif defined(SH_BANDS_3)
#define SH_COEFFS 15
#else
#define SH_COEFFS 0
#endif

#define SH_C0 0.28209479177387814f

#ifndef SH_BANDS_0
#define SH_C1 0.4886025119029199f
#define SH_C2_0 1.0925484305920792f
#define SH_C2_1 -1.0925484305920792f
#define SH_C2_2 0.31539156525252005f
#define SH_C2_3 -1.0925484305920792f
#define SH_C2_4 0.5462742152960396f
#define SH_C3_0 -0.5900435899266435f
#define SH_C3_1 2.890611442640554f
#define SH_C3_2 -0.4570457994644658f
#define SH_C3_3 0.3731763325901154f
#define SH_C3_4 -0.4570457994644658f
#define SH_C3_5 1.445305721320277f
#define SH_C3_6 -0.5900435899266435f

// see https://github.com/graphdeco-inria/gaussian-splatting/blob/main/utils/sh_utils.py
float3 EvalSH(const inout float3 sh[SH_COEFFS], float3 dir, int degree = 3)
{
    if (degree == 0)
        return float3(0, 0, 0);

    float x = dir.x;
    float y = dir.y;
    float z = dir.z;

    // 1st degree
    float3 result = SH_C1 * (-sh[0] * y + sh[1] * z - sh[2] * x);
    if (degree == 1)
        return result;

    #if defined(SH_BANDS_2) || defined(SH_BANDS_3)
    // 2nd degree
    float xx = x * x;
    float yy = y * y;
    float zz = z * z;
    float xy = x * y;
    float yz = y * z;
    float xz = x * z;

    result = result + (
        sh[3] * (SH_C2_0 * xy) +
        sh[4] * (SH_C2_1 * yz) +
        sh[5] * (SH_C2_2 * (2.0 * zz - xx - yy)) +
        sh[6] * (SH_C2_3 * xz) +
        sh[7] * (SH_C2_4 * (xx - yy))
    );

    if (degree == 2)
        return result;
    #endif

    #ifdef SH_BANDS_3
    // 3rd degree
    result = result + (
        sh[8] * (SH_C3_0 * y * (3.0 * xx - yy)) +
        sh[9] * (SH_C3_1 * xy * z) +
        sh[10] * (SH_C3_2 * y * (4.0 * zz - xx - yy)) +
        sh[11] * (SH_C3_3 * z * (2.0 * zz - 3.0 * xx - 3.0 * yy)) +
        sh[12] * (SH_C3_4 * x * (4.0 * zz - xx - yy)) +
        sh[13] * (SH_C3_5 * z * (xx - yy)) +
        sh[14] * (SH_C3_6 * x * (xx - 3.0 * yy))
    );
    #endif

    return result;
}
#endif

#endif
