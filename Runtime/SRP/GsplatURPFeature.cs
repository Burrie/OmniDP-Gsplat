// Originated from the GaussianSplatHDRPPass in aras-p/UnityGaussianSplatting by Aras Pranckevičius
// https://github.com/aras-p/UnityGaussianSplatting/blob/main/package/Runtime/GaussianSplatHDRPPass.cs
// Copyright (c) 2023 Aras Pranckevičius
// Modified by Yize Wu
// Copyright (c) 2025 Yize Wu
// SPDX-License-Identifier: MIT

#if GSPLAT_ENABLE_URP

using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;
#if UNITY_6000_0_OR_NEWER
using UnityEngine.Rendering.RenderGraphModule;
using UnityEngine.Rendering.RenderGraphModule.Util;
#endif

namespace Gsplat
{
    public class GsplatURPFeature : ScriptableRendererFeature
    {
        class GsplatRenderPass : ScriptableRenderPass
        {
#if UNITY_6000_0_OR_NEWER
            class PassData
            {
                public UniversalCameraData CameraData;
            }

            public override void RecordRenderGraph(RenderGraph renderGraph, ContextContainer frameData)
            {
                using var builder = renderGraph.AddUnsafePass(GsplatSorter.k_PassName, out PassData passData);
                passData.CameraData = frameData.Get<UniversalCameraData>();
                builder.AllowPassCulling(false);
                builder.SetRenderFunc(static (PassData data, UnsafeGraphContext context) =>
                {
                    var commandBuffer = CommandBufferHelpers.GetNativeCommandBuffer(context.cmd);
                    GsplatSorter.Instance.DispatchSort(commandBuffer, data.CameraData.camera);
                });
            }
#else
            public CommandBuffer CommandBuffer;
            public override void Execute(ScriptableRenderContext context, ref RenderingData renderingData)
            {
                GsplatSorter.Instance.DispatchSort(CommandBuffer, renderingData.cameraData.camera);
                context.ExecuteCommandBuffer(CommandBuffer);
            }
#endif
        }

        class GsplatOmniErpPass : ScriptableRenderPass
        {
            const string k_PassName = "Gsplat Hybrid Omni ERP";

#if UNITY_6000_0_OR_NEWER
            class PassData
            {
                public UniversalCameraData CameraData;
            }

            public override void RecordRenderGraph(RenderGraph renderGraph, ContextContainer frameData)
            {
                using var builder = renderGraph.AddUnsafePass(k_PassName, out PassData passData);
                passData.CameraData = frameData.Get<UniversalCameraData>();
                builder.AllowPassCulling(false);
                builder.SetRenderFunc(static (PassData data, UnsafeGraphContext context) =>
                {
                    if (!TryGetActiveViewer(data.CameraData.camera, out var viewer) || !viewer.ShouldRenderErp())
                        return;

                    var commandBuffer = CommandBufferHelpers.GetNativeCommandBuffer(context.cmd);
                    viewer.RecordErpRender(commandBuffer, true);
                });
            }
#else
            RTHandle m_cameraColorTarget;

            public void Setup(RTHandle cameraColorTarget)
            {
                m_cameraColorTarget = cameraColorTarget;
            }

            public override void Execute(ScriptableRenderContext context, ref RenderingData renderingData)
            {
                if (!TryGetActiveViewer(renderingData.cameraData.camera, out var viewer) || !viewer.ShouldRenderErp())
                    return;

                var cmd = CommandBufferPool.Get(k_PassName);
                viewer.RecordErpRender(cmd, true);
                if (m_cameraColorTarget != null)
                    CoreUtils.SetRenderTarget(cmd, m_cameraColorTarget);
                context.ExecuteCommandBuffer(cmd);
                CommandBufferPool.Release(cmd);
            }
#endif
        }

        class GsplatOmniCompositePass : ScriptableRenderPass
        {
            const string k_PassName = "Gsplat Hybrid Omni Composite";

#if UNITY_6000_0_OR_NEWER
            public override void RecordRenderGraph(RenderGraph renderGraph, ContextContainer frameData)
            {
                var cameraData = frameData.Get<UniversalCameraData>();
                if (!TryGetActiveViewer(cameraData.camera, out var viewer) ||
                    !viewer.TryPrepareCompositeMaterial(cameraData.camera, out var material))
                    return;

                var resourceData = frameData.Get<UniversalResourceData>();
                if (resourceData.isActiveTargetBackBuffer)
                    return;

                var source = resourceData.activeColorTexture;
                var destinationDesc = renderGraph.GetTextureDesc(source);
                destinationDesc.name = "CameraColor-GsplatHybridOmni";
                destinationDesc.clearBuffer = false;
                destinationDesc.depthBufferBits = 0;
                var destination = renderGraph.CreateTexture(destinationDesc);

                var blitParams = new RenderGraphUtils.BlitMaterialParameters(source, destination, material, 0);
                renderGraph.AddBlitPass(blitParams, k_PassName);
                resourceData.cameraColor = destination;
            }
#else
            RTHandle m_cameraColorTarget;
            RTHandle m_tempColor;

            public void Setup(RTHandle cameraColorTarget)
            {
                m_cameraColorTarget = cameraColorTarget;
            }

            public override void OnCameraSetup(CommandBuffer cmd, ref RenderingData renderingData)
            {
                var descriptor = renderingData.cameraData.cameraTargetDescriptor;
                descriptor.depthBufferBits = 0;
                descriptor.msaaSamples = 1;
                RenderingUtils.ReAllocateIfNeeded(ref m_tempColor, descriptor, FilterMode.Bilinear,
                    TextureWrapMode.Clamp, name: "_GsplatHybridOmniComposite");
            }

            public override void Execute(ScriptableRenderContext context, ref RenderingData renderingData)
            {
                if (m_cameraColorTarget == null ||
                    !TryGetActiveViewer(renderingData.cameraData.camera, out var viewer) ||
                    !viewer.TryPrepareCompositeMaterial(renderingData.cameraData.camera, out var material))
                    return;

                var cmd = CommandBufferPool.Get(k_PassName);
                Blitter.BlitCameraTexture(cmd, m_cameraColorTarget, m_tempColor, material, 0);
                Blitter.BlitCameraTexture(cmd, m_tempColor, m_cameraColorTarget);
                context.ExecuteCommandBuffer(cmd);
                CommandBufferPool.Release(cmd);
            }

            public void Dispose()
            {
                m_tempColor?.Release();
                m_tempColor = null;
                m_cameraColorTarget = null;
            }
#endif
        }

        GsplatRenderPass m_pass;
        GsplatOmniErpPass m_erpPass;
        GsplatOmniCompositePass m_compositePass;
        bool m_hasGsplats;

        public override void Create()
        {
            m_pass = new GsplatRenderPass { renderPassEvent = RenderPassEvent.BeforeRenderingTransparents };
            m_erpPass = new GsplatOmniErpPass { renderPassEvent = RenderPassEvent.BeforeRenderingTransparents };
            m_compositePass = new GsplatOmniCompositePass
            {
                renderPassEvent = RenderPassEvent.BeforeRenderingPostProcessing
            };
        }

        public override void OnCameraPreCull(ScriptableRenderer renderer, in CameraData cameraData)
        {
            m_hasGsplats = GsplatSorter.Instance.GatherGsplatsForCamera(cameraData.camera);
#if !UNITY_6000_0_OR_NEWER
            m_pass.CommandBuffer ??= new CommandBuffer { name = "SortGsplats" };
            m_pass.CommandBuffer.Clear();
#endif
        }

        public override void AddRenderPasses(ScriptableRenderer renderer, ref RenderingData renderingData)
        {
            if (GsplatSorter.Instance.Valid && GsplatSettings.Instance.Valid && m_hasGsplats)
                renderer.EnqueuePass(m_pass);
            if (TryGetActiveViewer(renderingData.cameraData.camera, out _))
            {
                renderer.EnqueuePass(m_erpPass);
                m_compositePass.ConfigureInput(ScriptableRenderPassInput.Color);
                renderer.EnqueuePass(m_compositePass);
            }
        }

#if !UNITY_6000_0_OR_NEWER
        public override void SetupRenderPasses(ScriptableRenderer renderer, in RenderingData renderingData)
        {
            if (TryGetActiveViewer(renderingData.cameraData.camera, out _))
            {
                m_erpPass.Setup(renderer.cameraColorTargetHandle);
                m_compositePass.Setup(renderer.cameraColorTargetHandle);
            }
        }
#endif

        protected override void Dispose(bool disposing)
        {
#if !UNITY_6000_0_OR_NEWER
            m_pass.CommandBuffer?.Dispose();
            m_pass.CommandBuffer = null;
            m_compositePass?.Dispose();
#endif
            m_pass = null;
            m_erpPass = null;
            m_compositePass = null;
        }

        static bool TryGetActiveViewer(Camera camera, out GsplatOmniViewer viewer)
        {
            viewer = camera ? camera.GetComponent<GsplatOmniViewer>() : null;
            return viewer && viewer.isActiveAndEnabled;
        }
    }
}

#endif
