// Copyright (c) 2025 Yize Wu
// SPDX-License-Identifier: MIT

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using UnityEngine;
using UnityEngine.Rendering;

namespace Gsplat
{
    public enum CompressionMode
    {
        Uncompressed,
        Spark
    }

    public class PlyHeaderInfo
    {
        public uint VertexCount = 0;
        public int PropertyCount = 0;
        public int SHPropertyCount = 0;
        public int PositionOffset = -1;
        public int ColorOffset = -1;
        public int SHOffset = -1;
        public int OpacityOffset = -1;
        public int ScaleOffset = -1;
        public int RotationOffset = -1;
        public int PvgTOffset = -1;
        public int PvgScaleTOffset = -1;
        public int PvgVelocity0Offset = -1;
        public int PvgVelocity1Offset = -1;
        public int PvgVelocity2Offset = -1;

        public bool HasPvgProperties =>
            PvgTOffset != -1 || PvgScaleTOffset != -1 ||
            PvgVelocity0Offset != -1 || PvgVelocity1Offset != -1 || PvgVelocity2Offset != -1;

        public bool IsPvgDynamic =>
            PvgTOffset != -1 && PvgScaleTOffset != -1 &&
            PvgVelocity0Offset != -1 && PvgVelocity1Offset != -1 && PvgVelocity2Offset != -1;

        /// <summary>
        /// Read each line, used for header reading.
        /// </summary>
        /// <param name="fs"></param>
        /// <returns></returns>
        static string ReadLine(FileStream fs)
        {
            List<byte> byteBuffer = new List<byte>();
            while (true)
            {
                int b = fs.ReadByte();
                if (b == -1 || b == '\n') break;
                byteBuffer.Add((byte)b);
            }

            // If line had CRLF line endings, remove the CR part
            if (byteBuffer.Count > 0 && byteBuffer.Last() == '\r')
            {
                byteBuffer.RemoveAt(byteBuffer.Count - 1);
            }

            return Encoding.UTF8.GetString(byteBuffer.ToArray());
        }

        public PlyHeaderInfo(FileStream fs)
        {
            while (ReadLine(fs) is { } line && line != "end_header")
            {
                var tokens = line.Split(' ');
                if (tokens.Length == 3 && tokens[0] == "element" && tokens[1] == "vertex")
                    VertexCount = uint.Parse(tokens[2]);
                if (tokens.Length != 3 || tokens[0] != "property") continue;
                switch (tokens[2])
                {
                    case "x":
                        PositionOffset = PropertyCount;
                        break;
                    case "f_dc_0":
                        ColorOffset = PropertyCount;
                        break;
                    case "f_rest_0":
                        SHOffset = PropertyCount;
                        break;
                    case "opacity":
                        OpacityOffset = PropertyCount;
                        break;
                    case "scale_0":
                        ScaleOffset = PropertyCount;
                        break;
                    case "rot_0":
                        RotationOffset = PropertyCount;
                        break;
                    case "t":
                        PvgTOffset = PropertyCount;
                        break;
                    case "scale_t":
                        PvgScaleTOffset = PropertyCount;
                        break;
                    case "v_0":
                        PvgVelocity0Offset = PropertyCount;
                        break;
                    case "v_1":
                        PvgVelocity1Offset = PropertyCount;
                        break;
                    case "v_2":
                        PvgVelocity2Offset = PropertyCount;
                        break;
                }

                if (tokens[2].StartsWith("f_rest_"))
                    SHPropertyCount++;
                PropertyCount++;
            }
        }

        public void ValidatePvgProperties()
        {
            if (!HasPvgProperties || IsPvgDynamic)
                return;

            var missing = new List<string>();
            if (PvgTOffset == -1) missing.Add("t");
            if (PvgScaleTOffset == -1) missing.Add("scale_t");
            if (PvgVelocity0Offset == -1) missing.Add("v_0");
            if (PvgVelocity1Offset == -1) missing.Add("v_1");
            if (PvgVelocity2Offset == -1) missing.Add("v_2");
            throw new NotSupportedException(
                $"partial PVG dynamic PLY header: missing required properties {string.Join(", ", missing)}");
        }
    }

    public delegate void ProgressCallback(string info, float progress);

    public abstract class GsplatAsset : ScriptableObject
    {
        public uint SplatCount;
        public byte SHBands; // 0, 1, 2, or 3
        public Bounds Bounds;
        [HideInInspector] public bool IsPvgDynamic;
        [HideInInspector] public float PvgMaxVelocityMagnitude;
        [HideInInspector] public Vector2[] PvgTimeData; // x = tau, y = raw/log beta
        [HideInInspector] public Vector3[] PvgVelocities;
        public abstract CompressionMode Compression { get; }

        protected int m_kernelInitOrder;
        static readonly protected int k_boundsBuffer = Shader.PropertyToID("_BoundsBuffer");
        static readonly protected int k_cutoutsBuffer = Shader.PropertyToID("_CutoutsBuffer");
        static readonly protected int k_cutoutsCount = Shader.PropertyToID("_CutoutsCount");
        static readonly protected int k_pvgDynamic = Shader.PropertyToID("_PvgDynamic");
        static readonly protected int k_pvgTime = Shader.PropertyToID("_PvgTime");
        static readonly protected int k_pvgPeriod = Shader.PropertyToID("_PvgPeriod");
        static readonly protected int k_pvgTimeBuffer = Shader.PropertyToID("_PvgTimeBuffer");
        static readonly protected int k_pvgVelocityBuffer = Shader.PropertyToID("_PvgVelocityBuffer");

        public GsplatMaterial GsplatMaterial => GsplatSettings.Instance.Materials[(int)Compression];
        public Material[] Materials => GsplatMaterial.Materials[SHBands];
        public Material[] OmniMaterials => GsplatMaterial.OmniMaterials[SHBands];

        protected void AllocatePvgData()
        {
            PvgMaxVelocityMagnitude = 0.0f;
            if (IsPvgDynamic)
            {
                PvgTimeData = new Vector2[SplatCount];
                PvgVelocities = new Vector3[SplatCount];
            }
            else
            {
                PvgTimeData = null;
                PvgVelocities = null;
            }
        }

        protected void SetPvgData(uint index, Vector2 timeData, Vector3 velocity)
        {
            if (!IsPvgDynamic)
                return;

            PvgTimeData[(int)index] = timeData;
            PvgVelocities[(int)index] = velocity;
            PvgMaxVelocityMagnitude = Mathf.Max(PvgMaxVelocityMagnitude, velocity.magnitude);
        }

        protected void UploadPvgData(GsplatResource resource)
        {
            if (!IsPvgDynamic)
                return;

            resource.PvgTimeBuffer.SetData(PvgTimeData);
            resource.PvgVelocityBuffer.SetData(PvgVelocities);
        }

        protected void SetupPvgMaterialPropertyBlock(MaterialPropertyBlock propertyBlock, GsplatResource resource)
        {
            propertyBlock.SetBuffer(k_pvgTimeBuffer, resource.PvgTimeBuffer);
            propertyBlock.SetBuffer(k_pvgVelocityBuffer, resource.PvgVelocityBuffer);
        }

        protected void SetPvgComputeParams(CommandBuffer cmd, ComputeShader cs, int kernel,
            GsplatResource resource, float pvgTime, float pvgPeriod)
        {
            cmd.SetComputeIntParam(cs, k_pvgDynamic, IsPvgDynamic ? 1 : 0);
            cmd.SetComputeFloatParam(cs, k_pvgTime, pvgTime);
            cmd.SetComputeFloatParam(cs, k_pvgPeriod, Mathf.Max(GsplatRenderer.k_MinPvgPeriod, pvgPeriod));
            cmd.SetComputeBufferParam(cs, kernel, k_pvgTimeBuffer, resource.PvgTimeBuffer);
            cmd.SetComputeBufferParam(cs, kernel, k_pvgVelocityBuffer, resource.PvgVelocityBuffer);
        }

        public abstract void Allocate();
        public abstract void LoadFromPly(string plyPath, ProgressCallback progressCallback = null);

        public abstract GsplatResource CreateResource();

        public void UploadData(GsplatResource resource)
        {
            if (resource.Uploaded) return;
            _UploadData(resource);
            resource.Uploaded = true;
            resource.UploadedCount = SplatCount;
        }

        public Task UploadDataAsync(GsplatResource resource)
        {
            if (resource.Uploaded) return Task.CompletedTask;
            resource.Uploaded = true;
            return _UploadDataAsync(resource);
        }

        public GraphicsBuffer UpdateCutoutsBuffer(GraphicsBuffer cutoutsBuffer, GsplatCutout.ShaderData[] cutoutsData)
        {
            var cs = GsplatMaterial.InitOrderShader;
            int numberOfCutouts = cutoutsData.Length;
            int bufferSize = Math.Max(numberOfCutouts, 1);

            if (cutoutsBuffer == null || cutoutsBuffer.count != bufferSize)
            {
                cutoutsBuffer?.Dispose();
                cutoutsBuffer = new GraphicsBuffer(GraphicsBuffer.Target.Structured, bufferSize, GsplatCutout.ShaderDataSize);
            }

            cutoutsBuffer.SetData(cutoutsData);
            cs.SetBuffer(m_kernelInitOrder, k_cutoutsBuffer, cutoutsBuffer);
            cs.SetInt(k_cutoutsCount, numberOfCutouts);
            return cutoutsBuffer;
        }

        public void UpdateBoundsBuffer(GraphicsBuffer BoundsBuffer)
        {
            var cs = GsplatMaterial.InitOrderShader;

            uint max = GsplatUtils.FloatToSortableUint(short.MaxValue);
            uint min = GsplatUtils.FloatToSortableUint(short.MinValue);
            uint[] array = {max, max, max, min, min, min};
            BoundsBuffer.SetData(array);

            cs.SetBuffer(m_kernelInitOrder, k_boundsBuffer, BoundsBuffer);
        }

        protected abstract Task _UploadDataAsync(GsplatResource resource);

        protected abstract void _UploadData(GsplatResource resource);

        public abstract void SetupMaterialPropertyBlock(MaterialPropertyBlock propertyBlock, GsplatResource resource);

        public abstract void ComputeDepth(CommandBuffer cmd, Matrix4x4 matrixMv,
            ISorterResource sorterResource, GsplatResource resource,
            GsplatRenderer.GsplatRenderMode renderMode, float pvgTime, float pvgPeriod);

        public abstract void InitOrder(ISorterResource sorterResource, GsplatResource resource,
            bool updateBounds);
    }
}
