// Copyright (c) 2026
// SPDX-License-Identifier: MIT

using System;
using UnityEngine;
using UnityEngine.Rendering;

namespace Gsplat
{
    [ExecuteAlways]
    [RequireComponent(typeof(Camera))]
    public class GsplatOmniViewer : MonoBehaviour
    {
        public enum ErpUpdatePolicy
        {
            Always,
            OnPositionChange,
            Manual,
        }

        [Min(2)] public int ErpWidth = 2048;
        [Min(1)] public int ErpHeight = 1024;
        [Min(0.001f)] public float OmniNearDistance = 0.2f;
        [Min(0.0f)] public float PositionRefreshThreshold = 0.02f;
        public ErpUpdatePolicy UpdatePolicy = ErpUpdatePolicy.OnPositionChange;
        public Color BackgroundColor = Color.clear;
        public bool AutoFindRenderers = true;
        public GsplatRenderer[] Renderers;
        public bool ShowDebugErp;

        static readonly int k_omniTex = Shader.PropertyToID("_GsplatOmniTex");
        static readonly int k_blitTexture = Shader.PropertyToID("_BlitTexture");
        static readonly int k_invProjection = Shader.PropertyToID("_GsplatCompositeInvProjection");
        static readonly int k_cameraToWorld = Shader.PropertyToID("_GsplatCompositeCameraToWorld");
        static readonly int k_omniWorldToCamera = Shader.PropertyToID("_GsplatOmniWorldToCamera");

        RenderTexture m_erpTexture;
        Material m_compositeMaterial;
        Camera m_camera;
        Vector3 m_lastRenderPosition;
        int m_lastRendererSignature;
        bool m_hasRendered;
        Matrix4x4 m_omniWorldToCamera;

        public RenderTexture ErpTexture => m_erpTexture;

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
            if (ShouldRenderErp())
                RenderErp(true);
        }

        [ContextMenu("Force Render ERP")]
        public void ForceRender()
        {
            m_hasRendered = false;
        }

        bool ShouldRenderErp()
        {
            if (UpdatePolicy == ErpUpdatePolicy.Manual)
                return !m_hasRendered;
            if (UpdatePolicy == ErpUpdatePolicy.Always || !m_hasRendered)
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
            if (!enabled || !GsplatSettings.Instance.Valid || !GsplatSorter.Instance.Valid)
                return;

            EnsureErpTexture();
            if (!m_erpTexture)
                return;

            m_omniWorldToCamera = WorldAlignedCameraMatrix(transform.position);

            var cmd = new CommandBuffer { name = "Gsplat Hybrid Omni ERP" };
            try
            {
                cmd.SetRenderTarget(m_erpTexture);
                cmd.SetViewport(new Rect(0, 0, ErpWidth, ErpHeight));
                cmd.ClearRenderTarget(false, true, BackgroundColor);
                cmd.SetViewProjectionMatrices(m_omniWorldToCamera, Matrix4x4.identity);

                var renderers = GetRenderers();
                foreach (var renderer in renderers)
                {
                    if (!renderer || renderer.RenderMode != GsplatRenderer.GsplatRenderMode.HybridOmniPerspective)
                        continue;
                    if (!renderer.PrepareRenderer(forceRendererRefresh))
                        continue;

                    var matrixMv = m_omniWorldToCamera * renderer.transform.localToWorldMatrix;
                    GsplatSorter.Instance.DispatchSort(cmd, renderer, matrixMv,
                        GsplatRenderer.GsplatRenderMode.HybridOmniPerspective);
                    renderer.RenderOmni(cmd, OmniNearDistance, ErpWidth, ErpHeight);
                }

                cmd.SetViewProjectionMatrices(Matrix4x4.identity, Matrix4x4.identity);
                Graphics.ExecuteCommandBuffer(cmd);
            }
            finally
            {
                cmd.Release();
            }

            m_lastRenderPosition = transform.position;
            m_lastRendererSignature = CalculateRendererSignature(GetRenderers());
            m_hasRendered = true;
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
            return TryPrepareCompositeMaterial(targetCamera, null, out material);
        }

        bool TryPrepareCompositeMaterial(Camera targetCamera, RenderTexture source, out Material material)
        {
            material = null;
            EnsureCompositeMaterial();
            if (!m_compositeMaterial || !m_erpTexture)
                return false;

            if (!m_hasRendered)
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
            m_compositeMaterial.SetMatrix(k_invProjection, targetCamera.projectionMatrix.inverse);
            m_compositeMaterial.SetMatrix(k_cameraToWorld, targetCamera.cameraToWorldMatrix);
            m_compositeMaterial.SetMatrix(k_omniWorldToCamera, m_omniWorldToCamera);
            material = m_compositeMaterial;
            return true;
        }

        void OnRenderImage(RenderTexture source, RenderTexture destination)
        {
            m_camera ??= GetComponent<Camera>();
            if (!TryPrepareCompositeMaterial(m_camera, source, out var material))
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
    }
}
