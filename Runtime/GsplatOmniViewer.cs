// Copyright (c) 2026
// SPDX-License-Identifier: MIT

using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;
using Unity.Profiling;

namespace Gsplat
{
    [ExecuteAlways]
    [AddComponentMenu("Gsplat/Gsplat Omni Viewer")]
    [RequireComponent(typeof(Camera))]
    public class GsplatOmniViewer : MonoBehaviour
    {
        static readonly ProfilerMarker k_renderErpMarker = new("Gsplat.RenderERP");
        static readonly ProfilerMarker k_compositeMarker = new("Gsplat.CompositeERP");
        public enum ErpUpdatePolicy
        {
            Always,
            OnPositionChange,
            Manual,
        }

        public enum OmniRasterizer
        {
            ODGS = 0,
            OmniGS = 1,
        }

        [Min(2)] public int ErpWidth = 2048;
        [Min(1)] public int ErpHeight = 1024;
        [Min(0.001f)] public float OmniNearDistance = 0.2f;
        [Tooltip("Projection/covariance math used when rendering the hidden ERP target. ODGS preserves the previous hybrid behavior; OmniGS uses the direct ERP projection Jacobian from OmniGS.")]
        public OmniRasterizer Rasterizer = OmniRasterizer.ODGS;
        [Tooltip("Camera translation needed before the hidden ERP is re-rendered. Keep this at 0 for natural HCI/VR movement; increase only when profiling requires it.")]
        [Min(0.0f)] public float PositionRefreshThreshold = 0.0f;
        [Tooltip("Present the native ERP in the middle of a taller 2:1 ERP and make the remaining top and bottom rows opaque black. This is a display condition only; OmniGS/ODGS still rasterize at ErpWidth x ErpHeight.")]
        public bool UseVerticalBlackPadding;
        [Tooltip("Opaque black rows placed above and below the native ERP when vertical black padding is enabled. For a 1920 x 512 native ERP, 224 creates a 1920 x 960 display ERP.")]
        [Min(0)] public int VerticalBlackPaddingPixels = 224;
        [Tooltip("Use the selected OpenMVG training-pose orientation as the stable ERP reference. GsplatTrainingPoseViewer enables this when it applies a pose, so Debug ERP uses the same camera-relative orientation as the source training frame without locking the tracked VR head rotation.")]
        public bool UseTrainingPoseReference;
        [Tooltip("Force a fresh Gaussian depth sort immediately before this viewer renders its ERP. Enable this for each eye in stereo VR so the right eye never reuses the left eye's depth order.")]
        public bool ForceSortPerErpRender;
        [Tooltip("Composite with a Built-in Render Pipeline camera command buffer instead of OnRenderImage. Enable this for separate Quest left/right eye cameras, whose image-effect callbacks are not reliable on all PCVR runtimes.")]
        public bool UseBuiltInCameraCommandBufferComposite;
        public ErpUpdatePolicy UpdatePolicy = ErpUpdatePolicy.OnPositionChange;
        public Color BackgroundColor = Color.clear;
        public bool AutoFindRenderers = true;
        public GsplatRenderer[] Renderers;
        public bool ShowDebugErp;

        static readonly int k_omniTex = Shader.PropertyToID("_GsplatOmniTex");
        static readonly int k_omniTexTexelSize = Shader.PropertyToID("_GsplatOmniTex_TexelSize");
        static readonly int k_omniDisplayData = Shader.PropertyToID("_GsplatOmniDisplayData");
        static readonly int k_blitTexture = Shader.PropertyToID("_BlitTexture");
        static readonly int k_cameraForward = Shader.PropertyToID("_GsplatCompositeCameraForward");
        static readonly int k_cameraRight = Shader.PropertyToID("_GsplatCompositeCameraRight");
        static readonly int k_cameraUp = Shader.PropertyToID("_GsplatCompositeCameraUp");
        static readonly int k_cameraProjectionData = Shader.PropertyToID("_GsplatCompositeProjectionData");
        static readonly int k_omniWorldToCamera = Shader.PropertyToID("_GsplatOmniWorldToCamera");

        RenderTexture m_erpTexture;
        Material m_compositeMaterial;
        Camera m_camera;
        CommandBuffer m_erpCommandBuffer;
        CommandBuffer m_builtinCompositeCommandBuffer;
        bool m_builtinCompositeAttached;
        bool m_builtinCompositeRecorded;
        Vector3 m_lastRenderPosition;
        int m_lastRendererSignature;
        OmniRasterizer m_lastRasterizer;
        bool m_hasRendered;
        bool m_warnedNoHybridRenderers;
        bool m_warnedInvalidResources;
        bool m_warnedMissingCompositeShader;
        bool m_warnedUrpFeatureMissing;
        bool m_warnedSrpFallback;
        int m_srpFramesWaitingForFeature;
        int m_lastSrpFeatureFrame = -1;
        bool m_validateStateInitialized;
        int m_validatedErpWidth;
        int m_validatedErpHeight;
        float m_validatedNearDistance;
        OmniRasterizer m_validatedRasterizer;
        Color m_validatedBackgroundColor;
        Matrix4x4 m_omniWorldToCamera;
        readonly List<GsplatRenderer> m_rendererScratch = new();
        [SerializeField] Quaternion m_trainingReferenceRotation = Quaternion.identity;

        /// <summary>The native ERP produced directly by the OmniGS/ODGS rasterizer.</summary>
        public RenderTexture ErpTexture => m_erpTexture;
        /// <summary>
        /// Compatibility alias for the physical ERP texture. Vertical padding is now applied virtually by the
        /// composite shader, so this texture always has the native ERP dimensions.
        /// </summary>
        [Obsolete("Vertical padding is virtual. Use ErpTexture plus DisplayErpWidth/DisplayErpHeight.")]
        public RenderTexture DisplayErpTexture => m_erpTexture;
        public int DisplayErpWidth => ErpWidth;
        public int DisplayErpHeight => UseVerticalBlackPadding
            ? ErpHeight + VerticalBlackPaddingPixels * 2
            : ErpHeight;
        public bool HasRenderedErp => m_hasRendered;
        public Quaternion TrainingReferenceRotation => m_trainingReferenceRotation;

        /// <summary>
        /// Makes the hidden ERP camera-relative to a training pose while continuing to use this component's live
        /// position. The reference remains stable while an XR user's head turns, so the ERP does not need a costly
        /// re-render for every head rotation.
        /// </summary>
        public void SetTrainingPoseReference(Quaternion rotation)
        {
            UseTrainingPoseReference = true;
            m_trainingReferenceRotation = rotation;
            ForceRender();
        }

        /// <summary>Returns to the legacy world-aligned ERP reference.</summary>
        public void ClearTrainingPoseReference()
        {
            UseTrainingPoseReference = false;
            m_trainingReferenceRotation = Quaternion.identity;
            ForceRender();
        }

        public static bool TryGetActiveViewer(Camera camera, out GsplatOmniViewer viewer)
        {
            viewer = camera ? camera.GetComponent<GsplatOmniViewer>() : null;
            if (viewer && viewer.isActiveAndEnabled)
                return true;

            var main = Camera.main;
            if (main && main != camera)
            {
                viewer = main.GetComponent<GsplatOmniViewer>();
                if (viewer && viewer.isActiveAndEnabled)
                    return true;
            }

            return GsplatRuntimeRegistry.TryGetActiveViewer(out viewer);
        }

        void OnEnable()
        {
            m_camera = GetComponent<Camera>();
            GsplatRuntimeRegistry.Register(this);
            EnsureCompositeMaterial();
            EnsureErpTexture();
            EnsureBuiltInCompositeCommandBuffer();
            ForceRender();
            RenderPipelineManager.endCameraRendering += OnEndCameraRendering;
        }

        void OnDisable()
        {
            RenderPipelineManager.endCameraRendering -= OnEndCameraRendering;
            GsplatRuntimeRegistry.Unregister(this);
            ReleaseBuiltInCompositeCommandBuffer();
            ReleaseErpCommandBuffer();
            ReleaseErpTexture();
            if (m_compositeMaterial)
                DestroyObject(m_compositeMaterial);
            m_compositeMaterial = null;
            m_hasRendered = false;
        }

        void OnValidate()
        {
            ErpWidth = Mathf.Max(2, ErpWidth);
            ErpHeight = Mathf.Max(1, ErpHeight);
            OmniNearDistance = Mathf.Max(0.001f, OmniNearDistance);
            PositionRefreshThreshold = Mathf.Max(0.0f, PositionRefreshThreshold);
            VerticalBlackPaddingPixels = Mathf.Max(0, VerticalBlackPaddingPixels);

            bool erpContentChanged = !m_validateStateInitialized ||
                                     m_validatedErpWidth != ErpWidth ||
                                     m_validatedErpHeight != ErpHeight ||
                                     !Mathf.Approximately(m_validatedNearDistance, OmniNearDistance) ||
                                     m_validatedRasterizer != Rasterizer ||
                                     m_validatedBackgroundColor != BackgroundColor;
            m_validateStateInitialized = true;
            m_validatedErpWidth = ErpWidth;
            m_validatedErpHeight = ErpHeight;
            m_validatedNearDistance = OmniNearDistance;
            m_validatedRasterizer = Rasterizer;
            m_validatedBackgroundColor = BackgroundColor;

            if (erpContentChanged)
                ForceRender();
        }

        void LateUpdate()
        {
            // SRP/URP drives the ERP pass from its renderer feature. Built-in keeps the image-effect fallback.
            if (GraphicsSettings.currentRenderPipeline)
            {
                if (ShouldRenderErp() && HasHybridRenderers())
                {
                    m_srpFramesWaitingForFeature++;
                    if (m_srpFramesWaitingForFeature > 2)
                        WarnOnce(ref m_warnedUrpFeatureMissing,
                            "URP is active but Gsplat Omni Viewer has not received an ERP render pass. The SRP fallback will try to present Hybrid Omni Perspective, but adding the GSplat URP Feature to the active Universal Renderer Data is recommended.");
                }
                return;
            }

            if (ShouldRenderErp())
                RenderErp(false);

            UpdateBuiltInCompositeCommandBuffer();
        }

        [ContextMenu("Force Render ERP")]
        public void ForceRender()
        {
            m_hasRendered = false;
        }

        public void NotifySrpFeatureRendered()
        {
            m_lastSrpFeatureFrame = Time.frameCount;
            m_srpFramesWaitingForFeature = 0;
            m_warnedUrpFeatureMissing = false;
        }

        public bool ShouldRenderErp()
        {
            if (UpdatePolicy == ErpUpdatePolicy.Manual)
                return !m_hasRendered || RasterizerChanged() || RendererSignatureChanged();
            if (UpdatePolicy == ErpUpdatePolicy.Always || !m_hasRendered)
                return true;
            if (RasterizerChanged())
                return true;
            if (RendererSignatureChanged())
                return true;

            float threshold = PositionRefreshThreshold;
            return (transform.position - m_lastRenderPosition).sqrMagnitude > threshold * threshold;
        }

        void EnsureCompositeMaterial()
        {
            if (m_compositeMaterial)
                return;

            var shader = Shader.Find("Gsplat/ERPToPerspective");
            if (shader)
                m_compositeMaterial = new Material(shader) { hideFlags = HideFlags.HideAndDontSave };
            else if (!m_warnedMissingCompositeShader)
            {
                Debug.LogWarning("Gsplat Omni Viewer could not find shader 'Gsplat/ERPToPerspective'. Hybrid output cannot be composited.", this);
                m_warnedMissingCompositeShader = true;
            }
        }

        void EnsureErpTexture()
        {
            if (!m_erpTexture || m_erpTexture.width != ErpWidth || m_erpTexture.height != ErpHeight)
            {
                ReleaseErpTexture();
                m_erpTexture = CreateErpTexture(ErpWidth, ErpHeight, "Gsplat Hybrid Omni ERP");
                ForceRender();
            }
        }

        void EnsureBuiltInCompositeCommandBuffer()
        {
            if (!UseBuiltInCameraCommandBufferComposite || GraphicsSettings.currentRenderPipeline)
                return;

            m_camera ??= GetComponent<Camera>();
            if (!m_camera)
                return;

            m_builtinCompositeCommandBuffer ??= new CommandBuffer { name = "Gsplat Hybrid Omni Eye Composite" };
            if (m_builtinCompositeAttached)
                return;

            m_camera.AddCommandBuffer(CameraEvent.AfterEverything, m_builtinCompositeCommandBuffer);
            m_builtinCompositeAttached = true;
        }

        void UpdateBuiltInCompositeCommandBuffer()
        {
            if (!UseBuiltInCameraCommandBufferComposite || GraphicsSettings.currentRenderPipeline)
            {
                ReleaseBuiltInCompositeCommandBuffer();
                return;
            }

            EnsureBuiltInCompositeCommandBuffer();
            if (m_builtinCompositeCommandBuffer == null)
                return;

            if (!TryPrepareCompositeMaterial(m_camera, out var material))
            {
                m_builtinCompositeCommandBuffer.Clear();
                m_builtinCompositeRecorded = false;
                return;
            }

            if (!m_builtinCompositeRecorded)
            {
                m_builtinCompositeCommandBuffer.Clear();
                // Pass 2 is a premultiplied-alpha overlay. It preserves the eye camera's already-rendered scene
                // while avoiding OnRenderImage, which can be skipped by the Oculus per-eye presentation path.
                m_builtinCompositeCommandBuffer.DrawProcedural(Matrix4x4.identity, material, 2,
                    MeshTopology.Triangles, 3, 1);
                m_builtinCompositeRecorded = true;
            }
        }

        void ReleaseBuiltInCompositeCommandBuffer()
        {
            if (m_builtinCompositeAttached && m_camera && m_builtinCompositeCommandBuffer != null)
                m_camera.RemoveCommandBuffer(CameraEvent.AfterEverything, m_builtinCompositeCommandBuffer);
            m_builtinCompositeAttached = false;
            m_builtinCompositeRecorded = false;
            if (m_builtinCompositeCommandBuffer == null)
                return;
            m_builtinCompositeCommandBuffer.Release();
            m_builtinCompositeCommandBuffer = null;
        }

        void ReleaseErpTexture()
        {
            ReleaseTexture(ref m_erpTexture);
        }

        static RenderTexture CreateErpTexture(int width, int height, string name)
        {
            var texture = new RenderTexture(width, height, 0, RenderTextureFormat.ARGBHalf)
            {
                name = name,
                wrapMode = TextureWrapMode.Clamp,
                // ERPToPerspective performs manual four-tap filtering so horizontal samples can wrap
                // while vertical samples outside the native content resolve to opaque black.
                filterMode = FilterMode.Point,
                useMipMap = false,
                autoGenerateMips = false,
            };
            texture.wrapModeU = TextureWrapMode.Repeat;
            texture.wrapModeV = TextureWrapMode.Clamp;
            texture.Create();
            return texture;
        }

        static void ReleaseTexture(ref RenderTexture texture)
        {
            if (!texture)
                return;
            texture.Release();
            DestroyObject(texture);
            texture = null;
        }

        public void RenderErp(bool forceRendererRefresh)
        {
            using (k_renderErpMarker.Auto())
            {
                m_erpCommandBuffer ??= new CommandBuffer { name = "Gsplat Hybrid Omni ERP" };
                m_erpCommandBuffer.Clear();
                RecordErpRender(m_erpCommandBuffer, forceRendererRefresh);
                Graphics.ExecuteCommandBuffer(m_erpCommandBuffer);
            }
        }

        void ReleaseErpCommandBuffer()
        {
            if (m_erpCommandBuffer == null)
                return;
            m_erpCommandBuffer.Release();
            m_erpCommandBuffer = null;
        }

        public bool RecordErpRender(CommandBuffer cmd, bool forceRendererRefresh)
        {
            if (!enabled || !GsplatSettings.Instance.Valid || !GsplatSorter.Instance.Valid)
            {
                WarnOnce(ref m_warnedInvalidResources,
                    "Gsplat Omni Viewer cannot render ERP because Gsplat settings or sorter resources are invalid.");
                return false;
            }

            EnsureErpTexture();
            if (!m_erpTexture)
                return false;

            var renderers = GetRenderers();
            if (!HasHybridRenderers(renderers))
            {
                WarnOnce(ref m_warnedNoHybridRenderers,
                    "Gsplat Omni Viewer found no active Gsplat Renderer using Hybrid Omni Perspective.");
                return false;
            }

            m_warnedNoHybridRenderers = false;
            m_omniWorldToCamera = UseTrainingPoseReference
                ? ReferenceCameraMatrix(transform.position, m_trainingReferenceRotation)
                : WorldAlignedCameraMatrix(transform.position);

            cmd.SetRenderTarget(m_erpTexture);
            cmd.SetViewport(new Rect(0, 0, ErpWidth, ErpHeight));
            cmd.ClearRenderTarget(false, true, BackgroundColor);
            cmd.SetViewProjectionMatrices(m_omniWorldToCamera, Matrix4x4.identity);

            for (int rendererIndex = 0; rendererIndex < renderers.Count; ++rendererIndex)
            {
                var renderer = renderers[rendererIndex];
                if (!renderer || renderer.RenderMode != GsplatRenderer.GsplatRenderMode.HybridOmniPerspective)
                    continue;
                // A stereo eye must obtain an order buffer for its own viewpoint. The two eye positions are
                // close, but reusing the other eye's ordering produces visible transparency errors nearby.
                if (!renderer.PrepareRenderer(forceRendererRefresh || ForceSortPerErpRender))
                    continue;

                var matrixMv = m_omniWorldToCamera * renderer.transform.localToWorldMatrix;
                GsplatSorter.Instance.DispatchSort(cmd, renderer, matrixMv,
                    GsplatRenderer.GsplatRenderMode.HybridOmniPerspective);
                renderer.RenderOmni(cmd, OmniNearDistance, ErpWidth, ErpHeight, Rasterizer);
            }

            cmd.SetViewProjectionMatrices(Matrix4x4.identity, Matrix4x4.identity);
            m_lastRenderPosition = transform.position;
            m_lastRendererSignature = CalculateRendererSignature(renderers);
            m_lastRasterizer = Rasterizer;
            m_hasRendered = true;
            m_srpFramesWaitingForFeature = 0;
            m_warnedUrpFeatureMissing = false;
            return true;
        }

        IList<GsplatRenderer> GetRenderers()
        {
            if (!AutoFindRenderers)
                return Renderers ?? Array.Empty<GsplatRenderer>();
            if (Renderers != null && Renderers.Length > 0)
                return Renderers;
            GsplatRuntimeRegistry.CopyActiveRenderers(m_rendererScratch);
            return m_rendererScratch;
        }

        bool RendererSignatureChanged()
        {
            return CalculateRendererSignature(GetRenderers()) != m_lastRendererSignature;
        }

        bool RasterizerChanged()
        {
            return m_hasRendered && m_lastRasterizer != Rasterizer;
        }

        bool HasHybridRenderers()
        {
            return HasHybridRenderers(GetRenderers());
        }

        static bool HasHybridRenderers(IList<GsplatRenderer> renderers)
        {
            if (renderers == null)
                return false;

            for (int i = 0; i < renderers.Count; ++i)
            {
                var renderer = renderers[i];
                if (renderer && renderer.RenderMode == GsplatRenderer.GsplatRenderMode.HybridOmniPerspective)
                    return true;
            }

            return false;
        }

        static int CalculateRendererSignature(IList<GsplatRenderer> renderers)
        {
            unchecked
            {
                int hash = 17;
                if (renderers == null)
                    return hash;

                for (int i = 0; i < renderers.Count; ++i)
                {
                    var renderer = renderers[i];
                    if (!renderer || renderer.RenderMode != GsplatRenderer.GsplatRenderMode.HybridOmniPerspective)
                        continue;

                    hash = hash * 31 + renderer.GetInstanceID();
                    hash = hash * 31 + (renderer.isActiveAndEnabled ? 1 : 0);
                    hash = hash * 31 + (renderer.GsplatAsset ? renderer.GsplatAsset.GetInstanceID() : 0);
                    hash = hash * 31 + renderer.PvgTime.GetHashCode();
                    hash = hash * 31 + renderer.CurrentPvgPeriod.GetHashCode();
                    hash = hash * 31 + renderer.transform.position.GetHashCode();
                    hash = hash * 31 + renderer.transform.rotation.GetHashCode();
                    hash = hash * 31 + renderer.transform.lossyScale.GetHashCode();
                }

                return hash;
            }
        }

        static Matrix4x4 WorldAlignedCameraMatrix(Vector3 position)
        {
            return Matrix4x4.Scale(new Vector3(1.0f, 1.0f, -1.0f)) *
                   Matrix4x4.Translate(-position);
        }

        // Unity's camera-local Y is up whereas the OpenMVG/CUDA camera convention used by the training rasterizer
        // has Y down. The shaders convert that local Y component explicitly when they map an ERP latitude.
        static Matrix4x4 ReferenceCameraMatrix(Vector3 position, Quaternion rotation)
        {
            return Matrix4x4.Scale(new Vector3(1.0f, 1.0f, -1.0f)) *
                   Matrix4x4.TRS(position, rotation, Vector3.one).inverse;
        }

        public bool TryPrepareCompositeMaterial(Camera targetCamera, out Material material)
        {
            return TryPrepareCompositeMaterial(targetCamera, null, false, out material);
        }

        bool TryPrepareCompositeMaterial(Camera targetCamera, RenderTexture source, bool allowImmediateErpRender,
            out Material material)
        {
            material = null;
            EnsureCompositeMaterial();
            if (!m_compositeMaterial || !m_erpTexture)
                return false;

            if (!m_hasRendered && allowImmediateErpRender)
                RenderErp(true);
            if (!m_hasRendered)
                return false;

            targetCamera ??= m_camera;
            if (!targetCamera)
                targetCamera = GetComponent<Camera>();
            if (!targetCamera)
                return false;

            if (source)
                m_compositeMaterial.SetTexture(k_blitTexture, source);
            m_compositeMaterial.SetTexture(k_omniTex, m_erpTexture);
            m_compositeMaterial.SetVector(k_omniTexTexelSize,
                new Vector4(1.0f / m_erpTexture.width, 1.0f / m_erpTexture.height,
                    m_erpTexture.width, m_erpTexture.height));
            int padding = UseVerticalBlackPadding ? VerticalBlackPaddingPixels : 0;
            m_compositeMaterial.SetVector(k_omniDisplayData,
                new Vector4(DisplayErpHeight, padding, ErpHeight, UseVerticalBlackPadding ? 1.0f : 0.0f));
            var cameraTransform = targetCamera.transform;
            float tanHalfVerticalFov = Mathf.Tan(targetCamera.fieldOfView * 0.5f * Mathf.Deg2Rad);
            m_compositeMaterial.SetVector(k_cameraForward, cameraTransform.forward);
            m_compositeMaterial.SetVector(k_cameraRight, cameraTransform.right);
            m_compositeMaterial.SetVector(k_cameraUp, cameraTransform.up);
            m_compositeMaterial.SetVector(k_cameraProjectionData,
                new Vector4(tanHalfVerticalFov, targetCamera.aspect, 0.0f, 0.0f));
            m_compositeMaterial.SetMatrix(k_omniWorldToCamera, m_omniWorldToCamera);
            material = m_compositeMaterial;
            return true;
        }

        void OnEndCameraRendering(ScriptableRenderContext context, Camera camera)
        {
            if (!GraphicsSettings.currentRenderPipeline || !isActiveAndEnabled)
                return;
            if (m_lastSrpFeatureFrame == Time.frameCount)
                return;
            if (!camera || camera.cameraType != CameraType.Game)
                return;
            if (!TryGetActiveViewer(camera, out var viewer) || viewer != this)
                return;
            if (!HasHybridRenderers())
                return;

            WarnOnce(ref m_warnedSrpFallback,
                "Gsplat Omni Viewer is presenting Hybrid Omni Perspective through the SRP fallback. For best XR performance, add the GSplat URP Feature to the active Universal Renderer Data.");

            var cmd = GetCommandBuffer("Gsplat Hybrid Omni SRP Fallback");
            try
            {
                if (ShouldRenderErp())
                    RecordErpRender(cmd, false);
                if (TryPrepareCompositeMaterial(camera, out var material))
                {
                    if (camera.targetTexture)
                        cmd.SetRenderTarget(camera.targetTexture);
                    else
                        cmd.SetRenderTarget(BuiltinRenderTextureType.CameraTarget);
                    cmd.DrawProcedural(Matrix4x4.identity, material, 2, MeshTopology.Triangles, 3, 1);
                }

                context.ExecuteCommandBuffer(cmd);
            }
            finally
            {
                ReleaseCommandBuffer(cmd);
            }
        }

        void OnRenderImage(RenderTexture source, RenderTexture destination)
        {
            using (k_compositeMarker.Auto())
            {
                m_camera ??= GetComponent<Camera>();
                if (UseBuiltInCameraCommandBufferComposite && !GraphicsSettings.currentRenderPipeline)
                {
                    Graphics.Blit(source, destination);
                    return;
                }
                if (!TryPrepareCompositeMaterial(m_camera, source, true, out var material))
                {
                    Graphics.Blit(source, destination);
                    return;
                }

                Graphics.Blit(source, destination, material, 1);
            }
        }

        void OnGUI()
        {
            if (!ShowDebugErp || !m_erpTexture)
                return;

            var previewRect = new Rect(8, 8, 256, 128);
            GUI.DrawTexture(previewRect, Texture2D.blackTexture, ScaleMode.StretchToFill, false);
            float contentY = previewRect.y + previewRect.height *
                (UseVerticalBlackPadding ? VerticalBlackPaddingPixels / (float)DisplayErpHeight : 0.0f);
            float contentHeight = previewRect.height * ErpHeight / DisplayErpHeight;
            GUI.DrawTexture(new Rect(previewRect.x, contentY, previewRect.width, contentHeight),
                m_erpTexture, ScaleMode.StretchToFill, false);
        }

        static CommandBuffer GetCommandBuffer(string name)
        {
#if GSPLAT_ENABLE_URP || GSPLAT_ENABLE_HDRP
            return CommandBufferPool.Get(name);
#else
            return new CommandBuffer { name = name };
#endif
        }

        static void ReleaseCommandBuffer(CommandBuffer cmd)
        {
            if (cmd == null)
                return;
#if GSPLAT_ENABLE_URP || GSPLAT_ENABLE_HDRP
            CommandBufferPool.Release(cmd);
#else
            cmd.Release();
#endif
        }

        static new void DestroyObject(UnityEngine.Object obj)
        {
            if (!obj)
                return;
            if (Application.isPlaying)
                Destroy(obj);
            else
                DestroyImmediate(obj);
        }

        void WarnOnce(ref bool flag, string message)
        {
            if (flag)
                return;
            Debug.LogWarning(message, this);
            flag = true;
        }
    }
}
