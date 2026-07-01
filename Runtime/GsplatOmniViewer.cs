// Copyright (c) 2026
// SPDX-License-Identifier: MIT

using System;
using UnityEngine;
using UnityEngine.Rendering;

namespace Gsplat
{
    [ExecuteAlways]
    [AddComponentMenu("Gsplat/Gsplat Omni Viewer")]
    [RequireComponent(typeof(Camera))]
    public class GsplatOmniViewer : MonoBehaviour
    {
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
        public ErpUpdatePolicy UpdatePolicy = ErpUpdatePolicy.OnPositionChange;
        public Color BackgroundColor = Color.clear;
        public bool AutoFindRenderers = true;
        public GsplatRenderer[] Renderers;
        public bool ShowDebugErp;

        static readonly int k_omniTex = Shader.PropertyToID("_GsplatOmniTex");
        static readonly int k_omniTexTexelSize = Shader.PropertyToID("_GsplatOmniTex_TexelSize");
        static readonly int k_blitTexture = Shader.PropertyToID("_BlitTexture");
        static readonly int k_cameraForward = Shader.PropertyToID("_GsplatCompositeCameraForward");
        static readonly int k_cameraRight = Shader.PropertyToID("_GsplatCompositeCameraRight");
        static readonly int k_cameraUp = Shader.PropertyToID("_GsplatCompositeCameraUp");
        static readonly int k_cameraProjectionData = Shader.PropertyToID("_GsplatCompositeProjectionData");
        static readonly int k_omniWorldToCamera = Shader.PropertyToID("_GsplatOmniWorldToCamera");

        RenderTexture m_erpTexture;
        Material m_compositeMaterial;
        Camera m_camera;
        Vector3 m_lastRenderPosition;
        int m_lastRendererSignature;
        OmniRasterizer m_lastRasterizer;
        bool m_hasRendered;
        bool m_warnedNoHybridRenderers;
        bool m_warnedInvalidResources;
        bool m_warnedMissingCompositeShader;
        bool m_warnedUrpFeatureMissing;
        int m_srpFramesWaitingForFeature;
        Matrix4x4 m_omniWorldToCamera;

        public RenderTexture ErpTexture => m_erpTexture;
        public bool HasRenderedErp => m_hasRendered;

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

            foreach (var candidate in FindObjectsOfType<GsplatOmniViewer>())
            {
                if (!candidate || !candidate.isActiveAndEnabled)
                    continue;
                viewer = candidate;
                return true;
            }

            viewer = null;
            return false;
        }

        void OnEnable()
        {
            m_camera = GetComponent<Camera>();
            EnsureCompositeMaterial();
            EnsureErpTexture();
            ForceRender();
        }

        void OnDisable()
        {
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
                            "URP is active but Gsplat Omni Viewer has not received an ERP render pass. Add the GSplat URP Feature to the active Universal Renderer Data.");
                }
                return;
            }

            if (ShouldRenderErp())
                RenderErp(false);
        }

        [ContextMenu("Force Render ERP")]
        public void ForceRender()
        {
            m_hasRendered = false;
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
            if (m_erpTexture && m_erpTexture.width == ErpWidth && m_erpTexture.height == ErpHeight)
                return;

            ReleaseErpTexture();
            m_erpTexture = new RenderTexture(ErpWidth, ErpHeight, 0, RenderTextureFormat.ARGBHalf)
            {
                name = "Gsplat Hybrid Omni ERP",
                wrapMode = TextureWrapMode.Clamp,
                filterMode = FilterMode.Bilinear,
                useMipMap = false,
                autoGenerateMips = false,
            };
            m_erpTexture.wrapModeU = TextureWrapMode.Repeat;
            m_erpTexture.wrapModeV = TextureWrapMode.Clamp;
            m_erpTexture.Create();
            ForceRender();
        }

        void ReleaseErpTexture()
        {
            if (!m_erpTexture)
                return;
            m_erpTexture.Release();
            DestroyObject(m_erpTexture);
            m_erpTexture = null;
        }

        public void RenderErp(bool forceRendererRefresh)
        {
            var cmd = new CommandBuffer { name = "Gsplat Hybrid Omni ERP" };
            try
            {
                RecordErpRender(cmd, forceRendererRefresh);
                Graphics.ExecuteCommandBuffer(cmd);
            }
            finally
            {
                cmd.Release();
            }

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
            m_omniWorldToCamera = WorldAlignedCameraMatrix(transform.position);

            cmd.SetRenderTarget(m_erpTexture);
            cmd.SetViewport(new Rect(0, 0, ErpWidth, ErpHeight));
            cmd.ClearRenderTarget(false, true, BackgroundColor);
            cmd.SetViewProjectionMatrices(m_omniWorldToCamera, Matrix4x4.identity);

            foreach (var renderer in renderers)
            {
                if (!renderer || renderer.RenderMode != GsplatRenderer.GsplatRenderMode.HybridOmniPerspective)
                    continue;
                if (!renderer.PrepareRenderer(forceRendererRefresh))
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

        GsplatRenderer[] GetRenderers()
        {
            if (!AutoFindRenderers)
                return Renderers ?? Array.Empty<GsplatRenderer>();
            if (Renderers != null && Renderers.Length > 0)
                return Renderers;
            return FindObjectsOfType<GsplatRenderer>();
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

        static bool HasHybridRenderers(GsplatRenderer[] renderers)
        {
            if (renderers == null)
                return false;

            foreach (var renderer in renderers)
                if (renderer && renderer.RenderMode == GsplatRenderer.GsplatRenderMode.HybridOmniPerspective)
                    return true;

            return false;
        }

        static int CalculateRendererSignature(GsplatRenderer[] renderers)
        {
            unchecked
            {
                int hash = 17;
                if (renderers == null)
                    return hash;

                foreach (var renderer in renderers)
                {
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

        void OnRenderImage(RenderTexture source, RenderTexture destination)
        {
            m_camera ??= GetComponent<Camera>();
            if (!TryPrepareCompositeMaterial(m_camera, source, true, out var material))
            {
                Graphics.Blit(source, destination);
                return;
            }

            Graphics.Blit(source, destination, material, 1);
        }

        void OnGUI()
        {
            if (!ShowDebugErp || !m_erpTexture)
                return;
            GUI.DrawTexture(new Rect(8, 8, 256, 128), m_erpTexture, ScaleMode.ScaleToFit, false);
        }

        static void DestroyObject(UnityEngine.Object obj)
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
