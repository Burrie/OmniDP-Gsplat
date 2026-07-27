// Copyright (c) 2025 Yize Wu
// SPDX-License-Identifier: MIT

using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using UnityEngine;
using UnityEngine.Rendering;

namespace Gsplat
{
    public class GsplatAssetUncompressed : GsplatAsset
    {
        public override CompressionMode Compression => CompressionMode.Uncompressed;
        public override long CpuDataBytes =>
            (Positions?.LongLength ?? 0) * 12L +
            (Colors?.LongLength ?? 0) * 16L +
            (SHs?.LongLength ?? 0) * 12L +
            (Scales?.LongLength ?? 0) * 12L +
            (Rotations?.LongLength ?? 0) * 16L +
            (PvgTimeData?.LongLength ?? 0) * 8L +
            (PvgVelocities?.LongLength ?? 0) * 12L;

        [HideInInspector] public Vector3[] Positions;
        [HideInInspector] public Vector4[] Colors; // RGB, Opacity
        [HideInInspector] public Vector3[] SHs;
        [HideInInspector] public Vector3[] Scales;
        [HideInInspector] public Vector4[] Rotations; // Quaternion, wxyz

        static readonly int k_positionBuffer = Shader.PropertyToID("_PositionBuffer");
        static readonly int k_scaleBuffer = Shader.PropertyToID("_ScaleBuffer");
        static readonly int k_rotationBuffer = Shader.PropertyToID("_RotationBuffer");
        static readonly int k_colorBuffer = Shader.PropertyToID("_ColorBuffer");
        static readonly int k_shBuffer = Shader.PropertyToID("_SHBuffer");
        static readonly int k_splatCount = Shader.PropertyToID("_SplatCount");
        static readonly int k_matrixMv = Shader.PropertyToID("_MatrixMV");
        static readonly int k_depthBuffer = Shader.PropertyToID("_DepthBuffer");
        static readonly int k_orderBuffer = Shader.PropertyToID("_OrderBuffer");
        static readonly int k_gsplatProjectionMode = Shader.PropertyToID("_GsplatProjectionMode");

        public override void Allocate()
        {
            AllocatePvgData();
            Positions = new Vector3[SplatCount];
            Colors = new Vector4[SplatCount];
            Scales = new Vector3[SplatCount];
            Rotations = new Vector4[SplatCount];
            if (SHBands > 0)
                SHs = new Vector3[SplatCount * GsplatUtils.SHBandsToCoefficientCount(SHBands)];
        }

        public override GsplatResource CreateResource()
        {
            return new GsplatResourceUncompressed(SplatCount, SHBands, IsPvgDynamic);
        }

        protected override void _UploadData(GsplatResource resource)
        {
            var res = (GsplatResourceUncompressed)resource;
            if (RuntimeStorage == GsplatRuntimeStorage.StreamedPlayerData)
            {
                UploadStreamed(res, false).GetAwaiter().GetResult();
                return;
            }

            res.PositionBuffer.SetData(Positions);
            res.ScaleBuffer.SetData(Scales);
            res.RotationBuffer.SetData(Rotations);
            res.ColorBuffer.SetData(Colors);
            if (SHBands > 0)
                res.SHBuffer.SetData(SHs);
            UploadPvgData(resource);
        }

        protected override async Task _UploadDataAsync(GsplatResource resource)
        {
            var res = (GsplatResourceUncompressed)resource;
            if (RuntimeStorage == GsplatRuntimeStorage.StreamedPlayerData)
            {
                await UploadStreamed(res, true);
                return;
            }

            while (res.UploadedCount < SplatCount)
            {
                var batchSize = (int)Math.Min(GsplatSettings.Instance.UploadBatchSize, SplatCount - res.UploadedCount);
                res.PositionBuffer.SetData(Positions, (int)res.UploadedCount, (int)res.UploadedCount, batchSize);
                res.ScaleBuffer.SetData(Scales, (int)res.UploadedCount, (int)res.UploadedCount, batchSize);
                res.RotationBuffer.SetData(Rotations, (int)res.UploadedCount, (int)res.UploadedCount, batchSize);
                res.ColorBuffer.SetData(Colors, (int)res.UploadedCount, (int)res.UploadedCount, batchSize);
                if (IsPvgDynamic)
                {
                    res.PvgTimeBuffer.SetData(PvgTimeData, (int)res.UploadedCount,
                        (int)res.UploadedCount, batchSize);
                    res.PvgVelocityBuffer.SetData(PvgVelocities, (int)res.UploadedCount,
                        (int)res.UploadedCount, batchSize);
                }

                if (SHBands > 0)
                {
                    var coefficientCount = GsplatUtils.SHBandsToCoefficientCount(SHBands);
                    res.SHBuffer.SetData(SHs, coefficientCount * (int)res.UploadedCount,
                        coefficientCount * (int)res.UploadedCount, coefficientCount * batchSize);
                }

                res.UploadedCount += (uint)batchSize;
                await Task.Yield();
            }
        }

        async Task UploadStreamed(GsplatResourceUncompressed res, bool yieldBetweenBatches)
        {
            using var reader = new GsplatStreamData.Reader(this);
            int coefficientCount = GsplatUtils.SHBandsToCoefficientCount(SHBands);
            uint requestedBatch = Math.Max(1u, Math.Min(GsplatSettings.Instance.UploadBatchSize, SplatCount));
            int maxBatch = int.MaxValue / Math.Max(coefficientCount, 1);
            int batchCapacity = (int)Math.Min(requestedBatch, (uint)maxBatch);
            var positions = new Vector3[batchCapacity];
            var scales = new Vector3[batchCapacity];
            var rotations = new Vector4[batchCapacity];
            var colors = new Vector4[batchCapacity];
            var sh = SHBands > 0 ? new Vector3[batchCapacity * coefficientCount] : null;
            var pvgTime = IsPvgDynamic ? new Vector2[batchCapacity] : null;
            var pvgVelocity = IsPvgDynamic ? new Vector3[batchCapacity] : null;

            while (res.UploadedCount < SplatCount)
            {
                using (k_streamedUploadBatchMarker.Auto())
                {
                    int destination = (int)res.UploadedCount;
                    int count = (int)Math.Min((uint)batchCapacity, SplatCount - res.UploadedCount);
                    reader.Read(GsplatStreamSection.Positions, positions, destination, count);
                    reader.Read(GsplatStreamSection.Scales, scales, destination, count);
                    reader.Read(GsplatStreamSection.Rotations, rotations, destination, count);
                    reader.Read(GsplatStreamSection.Colors, colors, destination, count);
                    res.PositionBuffer.SetData(positions, 0, destination, count);
                    res.ScaleBuffer.SetData(scales, 0, destination, count);
                    res.RotationBuffer.SetData(rotations, 0, destination, count);
                    res.ColorBuffer.SetData(colors, 0, destination, count);

                    if (SHBands > 0)
                    {
                        int shDestination = destination * coefficientCount;
                        int shCount = count * coefficientCount;
                        reader.Read(GsplatStreamSection.SH, sh, shDestination, shCount);
                        res.SHBuffer.SetData(sh, 0, shDestination, shCount);
                    }
                    if (IsPvgDynamic)
                    {
                        reader.Read(GsplatStreamSection.PvgTime, pvgTime, destination, count);
                        reader.Read(GsplatStreamSection.PvgVelocity, pvgVelocity, destination, count);
                        res.PvgTimeBuffer.SetData(pvgTime, 0, destination, count);
                        res.PvgVelocityBuffer.SetData(pvgVelocity, 0, destination, count);
                    }

                    res.UploadedCount += (uint)count;
                }
                if (yieldBetweenBatches)
                    await Task.Yield();
            }

            reader.Validate();
        }

        public override void ReleaseCpuData()
        {
            Positions = null;
            Colors = null;
            SHs = null;
            Scales = null;
            Rotations = null;
            ReleasePvgCpuData();
        }

        public override void SetupMaterialPropertyBlock(MaterialPropertyBlock propertyBlock,
            GsplatResource resource)
        {
            var cs = GsplatMaterial.InitOrderShader;
            m_kernelInitOrder = cs.FindKernel("InitOrder");

            var res = (GsplatResourceUncompressed)resource;
            propertyBlock.SetBuffer(k_positionBuffer, res.PositionBuffer);
            propertyBlock.SetBuffer(k_scaleBuffer, res.ScaleBuffer);
            propertyBlock.SetBuffer(k_rotationBuffer, res.RotationBuffer);
            propertyBlock.SetBuffer(k_colorBuffer, res.ColorBuffer);
            if (SHBands > 0)
                propertyBlock.SetBuffer(k_shBuffer, res.SHBuffer);
            SetupPvgMaterialPropertyBlock(propertyBlock, resource);
        }

        public override void ComputeDepth(CommandBuffer cmd, Matrix4x4 matrixMv,
            ISorterResource sorterResource, GsplatResource resource,
            GsplatRenderer.GsplatRenderMode renderMode, float pvgTime, float pvgPeriod)
        {
            var res = (GsplatResourceUncompressed)resource;
            var cs = GsplatMaterial.CalcDepthShader;
            var kernelCalcDepth = 0;
            cmd.SetComputeIntParam(cs, k_splatCount, (int)res.UploadedCount);
            cmd.SetComputeMatrixParam(cs, k_matrixMv, matrixMv);
            cmd.SetComputeIntParam(cs, k_gsplatProjectionMode, (int)renderMode);
            SetPvgComputeParams(cmd, cs, kernelCalcDepth, resource, pvgTime, pvgPeriod);
            cmd.SetComputeBufferParam(cs, kernelCalcDepth, k_positionBuffer, res.PositionBuffer);
            cmd.SetComputeBufferParam(cs, kernelCalcDepth, k_depthBuffer, sorterResource.InputKeys);
            cmd.SetComputeBufferParam(cs, kernelCalcDepth, k_orderBuffer, sorterResource.OrderBuffer);
            cmd.DispatchCompute(cs, kernelCalcDepth, (int)GsplatUtils.DivRoundUp(res.UploadedCount, 1024), 1, 1);
        }

        public override void InitOrder(ISorterResource sorterResource, GsplatResource resource, bool updateBounds)
        {
            var cs = GsplatMaterial.InitOrderShader;
            var res = (GsplatResourceUncompressed)resource;
            sorterResource.OrderBuffer.SetCounterValue(0);
            cs.SetInt(k_splatCount, (int)res.UploadedCount);
            cs.SetBuffer(m_kernelInitOrder, k_orderBuffer, sorterResource.OrderBuffer);
            cs.SetBuffer(m_kernelInitOrder, k_positionBuffer, res.PositionBuffer);
            if (updateBounds)
                cs.EnableKeyword("UPDATE_BOUNDS");
            else
                cs.DisableKeyword("UPDATE_BOUNDS");
            cs.Dispatch(m_kernelInitOrder, (int)GsplatUtils.DivRoundUp(res.UploadedCount, 1024), 1, 1);
        }

        public override void LoadFromPly(string plyPath, ProgressCallback progressCallback = null)
        {
            using var fs = new FileStream(plyPath, FileMode.Open, FileAccess.Read);
            var plyInfo = new PlyHeaderInfo(fs);
            plyInfo.ValidatePvgProperties();
            var shCoeffs = plyInfo.SHPropertyCount / 3;
            SplatCount = plyInfo.VertexCount;
            SHBands = GsplatUtils.CalcSHBandsFromSHPropertyCount(plyInfo.SHPropertyCount);
            IsPvgDynamic = plyInfo.IsPvgDynamic;

            if (SHBands > 3 || GsplatUtils.SHBandsToCoefficientCount(SHBands) * 3 != plyInfo.SHPropertyCount)
                throw new NotSupportedException($"unexpected SH property count {plyInfo.SHPropertyCount}");

            if (plyInfo.PositionOffset == -1 || plyInfo.ColorOffset == -1 || plyInfo.OpacityOffset == -1 ||
                plyInfo.ScaleOffset == -1 || plyInfo.RotationOffset == -1)
                throw new NotSupportedException("missing required properties in PLY header");

            ValidateImportSize(plyInfo, SHBands, Compression);
            Allocate();
            var buffer = new byte[plyInfo.PropertyCount * sizeof(float)];
            for (uint i = 0; i < plyInfo.VertexCount; i++)
            {
                var readBytes = fs.Read(buffer);
                if (readBytes != buffer.Length)
                    throw new EndOfStreamException($"unexpected end of file, got {readBytes} bytes at vertex {i}");

                var properties = MemoryMarshal.Cast<byte, float>(buffer);
                Positions[i] = new Vector3(
                    properties[plyInfo.PositionOffset],
                    properties[plyInfo.PositionOffset + 1],
                    properties[plyInfo.PositionOffset + 2]);
                Colors[i] = new Vector4(
                    properties[plyInfo.ColorOffset],
                    properties[plyInfo.ColorOffset + 1],
                    properties[plyInfo.ColorOffset + 2],
                    IsPvgDynamic
                        ? properties[plyInfo.OpacityOffset]
                        : GsplatUtils.Sigmoid(properties[plyInfo.OpacityOffset]));
                for (int j = 0; j < shCoeffs; j++)
                    SHs[i * shCoeffs + j] = new Vector3(
                        properties[plyInfo.SHOffset + j * 3],
                        properties[plyInfo.SHOffset + j * 3 + 1],
                        properties[plyInfo.SHOffset + j * 3 + 2]);
                Scales[i] = new Vector3(
                    Mathf.Exp(properties[plyInfo.ScaleOffset]),
                    Mathf.Exp(properties[plyInfo.ScaleOffset + 1]),
                    Mathf.Exp(properties[plyInfo.ScaleOffset + 2]));
                Rotations[i] = new Vector4(
                    properties[plyInfo.RotationOffset],
                    properties[plyInfo.RotationOffset + 1],
                    properties[plyInfo.RotationOffset + 2],
                    properties[plyInfo.RotationOffset + 3]).normalized;

                if (IsPvgDynamic)
                {
                    var pvgTimeData = new Vector2(
                        properties[plyInfo.PvgTOffset],
                        properties[plyInfo.PvgScaleTOffset]);
                    var pvgVelocity = new Vector3(
                        properties[plyInfo.PvgVelocity0Offset],
                        properties[plyInfo.PvgVelocity1Offset],
                        properties[plyInfo.PvgVelocity2Offset]);
                    SetPvgData(i, pvgTimeData, pvgVelocity);
                }

                if (i == 0) Bounds = new Bounds(Positions[i], Vector3.zero);
                else Bounds.Encapsulate(Positions[i]);

                progressCallback?.Invoke("Reading vertices", i / (float)plyInfo.VertexCount);
            }
        }
    }
}
