// Copyright (c) 2025 Yize Wu
// SPDX-License-Identifier: MIT

using System.Linq;
#if UNITY_EDITOR
using UnityEditor;
#endif
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Serialization;

namespace Gsplat
{
    [ExecuteAlways]
    public class GsplatRenderer : MonoBehaviour, IGsplat
    {
        public enum GsplatSortMode
        {
            Always,
            SortEveryNFrames,
            CutoutsEveryNSorts,
        }

        public enum GsplatRenderMode
        {
            Perspective = 0,
            HybridOmniPerspective = 1,
        }

        public const float k_MinPvgPeriod = 0.0001f;

        public GsplatAsset GsplatAsset;
        [FormerlySerializedAs("RenderMode")]
        [SerializeField] GsplatRenderMode m_renderMode = GsplatRenderMode.Perspective;
        [Range(0, 3)] public int SHDegree = 3;
        [HideInInspector] public uint RenderOrder = 0;
        public float Brightness = 1.0f;

        [Tooltip(
            "Improves rendering speed by shrinking Gaussian splats while trying to keep the impact on visual quality as small as possible.")]
        [Range(0, 1)]
        public float SplatDownscaleFactor = 0.0f;

        public bool GammaToLinear;
        public bool AsyncUpload;
        public bool RenderBeforeUploadComplete = true;
        [Tooltip("Current PVG timestamp. Used only when the assigned PLY asset contains t, scale_t, and v_* properties.")]
        public float PvgTime = 0.0f;
        [Tooltip("PVG cycle period l. Used only when the assigned PLY asset contains t, scale_t, and v_* properties.")]
        [Min(k_MinPvgPeriod)] public float PvgPeriod = 0.2f;

        [Tooltip("Does cutouts update the Gsplat world bounds? (Costly on moving cutouts)")]
        public bool CutoutsUpdateBounds = true;

        GsplatAsset m_prevAsset;
        GsplatRendererImpl m_renderer;
        static bool s_warnedMissingOmniViewer;
        float m_prevPvgTime = float.NaN;
        float m_prevPvgPeriod = float.NaN;

        public bool Valid => GsplatAsset &&
                             (RenderBeforeUploadComplete ? SplatCount > 0 : SplatCount == GsplatAsset.SplatCount);

        public uint SplatCount => m_renderer != null ? m_renderer.GsplatResource?.UploadedCount ?? 0 : 0;

        public ISorterResource SorterResource => m_renderer.SorterResource;

        public uint RemainingCount
        {
            get => m_renderer.m_remainingCount;
            set => m_renderer.m_remainingCount = value;
        }

        public Bounds Bounds
        {
            get => m_renderer.m_bounds;
            set => m_renderer.m_bounds = value;
        }

        public GsplatCutout[] Cutouts
        {
            get
            {
                var cutouts = GsplatCutout.m_RegisteredCutouts
                    .Where(component => component.enabled)
                    .Where(component =>
                        component.m_Target == GsplatCutout.Target.All ||
                        (component.m_Target == GsplatCutout.Target.Parent && component.transform.parent == transform) ||
                        (component.m_Target == GsplatCutout.Target.Specific && component.m_SpecifcRenderer == this)
                    );
                return cutouts.ToArray();
            }
        }

        public bool ComputeSortRequired => m_renderer.ComputeSortRequired;
        public bool ComputeCutoutsRequired => m_renderer.ComputeCutoutsRequired;
        public float CurrentPvgTime => PvgTime;
        public float CurrentPvgPeriod => Mathf.Max(k_MinPvgPeriod, PvgPeriod);
        public GsplatRenderMode RenderMode
        {
            get => m_renderMode;
            set
            {
                if (m_renderMode == value)
                    return;
                m_renderMode = value;
                ForceRefresh();
            }
        }

        public GsplatSortMode SortMode = GsplatSortMode.Always;
        [HideInInspector] public uint SortRefreshRate = 1;
        [HideInInspector] public uint CutoutsRefreshRate = 1;

        public void ComputeDepth(CommandBuffer cmd, Matrix4x4 matrixMv, GsplatRenderMode renderMode,
            float pvgTime, float pvgPeriod) =>
            m_renderer.ComputeDepth(cmd, matrixMv, renderMode, pvgTime, pvgPeriod);

        void OnEnable()
        {
            GsplatSorter.Instance.RegisterGsplat(this);
            m_prevAsset = null;
        }

        void OnDisable()
        {
            GsplatSorter.Instance.UnregisterGsplat(this);
            m_renderer?.Dispose();
            m_renderer = null;
        }

        public void ForceRefresh()
        {
            m_renderer?.ForceRefresh();
        }

#if UNITY_EDITOR
        public void OnDrawGizmos()
        {
            if (GsplatSettings.Instance.DisplayBoundingBoxes && Valid && isActiveAndEnabled)
            {
                Gizmos.matrix = transform.localToWorldMatrix;
                Gizmos.color = Color.green;
                Gizmos.DrawWireCube(Bounds.center, Bounds.size);
            }
        }

        [SerializeField, HideInInspector] string m_assetGuid;
        public string AssetGuid => m_assetGuid;
#endif // #if UNITY_EDITOR

        void OnValidate()
        {
            PvgPeriod = Mathf.Max(k_MinPvgPeriod, PvgPeriod);
            ForceRefresh();
#if UNITY_EDITOR
            long localId;
            if (GsplatAsset &&
                AssetDatabase.TryGetGUIDAndLocalFileIdentifier(GsplatAsset, out var guid, out localId))
                m_assetGuid = guid;
#endif // #if UNITY_EDITOR
        }

        public void ReloadAsset()
        {
            m_prevAsset = null;
        }

        public bool PrepareRenderer(bool forceRefresh = false)
        {
            if (!GsplatAsset)
                m_prevAsset = null;
            if (GsplatAsset && !GsplatSettings.Instance.Valid)
                return false;

            if (m_prevAsset != GsplatAsset)
            {
                m_renderer?.ReleaseGsplatAsset();
                m_prevAsset = GsplatAsset;
                if (GsplatAsset)
                {
                    if (m_renderer == null)
                        m_renderer = new GsplatRendererImpl(GsplatAsset.SplatCount);
                    else
                        m_renderer.RecreateResources(GsplatAsset.SplatCount);
                    m_prevPvgTime = float.NaN;
                    m_prevPvgPeriod = float.NaN;
#if UNITY_EDITOR
                    var asyncUpload = AsyncUpload && Application.isPlaying;
#else
                    var asyncUpload = AsyncUpload;
#endif
                    m_renderer.BindGsplatAsset(GsplatAsset, asyncUpload);
                }
            }

            if (!Valid || !GsplatSorter.Instance.Valid)
                return false;

            if (forceRefresh)
                ForceRefresh();

            PvgPeriod = Mathf.Max(k_MinPvgPeriod, PvgPeriod);
            if (GsplatAsset.IsPvgDynamic &&
                (!Mathf.Approximately(PvgTime, m_prevPvgTime) ||
                 !Mathf.Approximately(PvgPeriod, m_prevPvgPeriod)))
            {
                ForceRefresh();
                m_prevPvgTime = PvgTime;
                m_prevPvgPeriod = PvgPeriod;
            }

            m_renderer.EvaluateRefreshRequired(SortMode, SortRefreshRate - 1, CutoutsRefreshRate - 1);
            m_renderer.DispatchInitOrder(Cutouts, transform.localToWorldMatrix, CutoutsUpdateBounds);
            return true;
        }

        public void RenderPerspective()
        {
            m_renderer.Render(transform, gameObject.layer, GammaToLinear, SHDegree, Brightness,
                1.0f - SplatDownscaleFactor, RenderOrder, PvgTime, CurrentPvgPeriod);
        }

        public void RenderOmni(CommandBuffer cmd, float nearDistance, int targetWidth, int targetHeight,
            GsplatOmniViewer.OmniRasterizer rasterizer)
        {
            m_renderer.RenderOmni(cmd, transform, GammaToLinear, SHDegree, Brightness,
                1.0f - SplatDownscaleFactor, RenderOrder, nearDistance, targetWidth, targetHeight,
                PvgTime, CurrentPvgPeriod, rasterizer);
        }

        public void Update()
        {
            if (!PrepareRenderer())
                return;

            if (RenderMode == GsplatRenderMode.Perspective)
                RenderPerspective();
            else
                WarnIfHybridViewerMissing();
        }

        void WarnIfHybridViewerMissing()
        {
            if (s_warnedMissingOmniViewer)
                return;

            var viewers = FindObjectsOfType<GsplatOmniViewer>();
            if (viewers.Any(viewer => viewer && viewer.isActiveAndEnabled))
                return;

            Debug.LogWarning(
                "Gsplat Renderer is set to Hybrid Omni Perspective, but no active Gsplat Omni Viewer is present on a camera. Hybrid renderers only appear after the viewer renders and composites the ERP texture.",
                this);
            s_warnedMissingOmniViewer = true;
        }
    }
}
