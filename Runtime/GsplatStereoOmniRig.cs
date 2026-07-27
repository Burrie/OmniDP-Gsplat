// Copyright (c) 2026
// SPDX-License-Identifier: MIT

using System.Reflection;
using UnityEngine;

namespace Gsplat
{
    /// <summary>
    /// Turns a centre-eye GsplatOmniViewer configuration into two independent eye render paths.
    /// Attach this beside the source viewer on CenterEyeAnchor. At runtime it creates/configures one
    /// GsplatOmniViewer on each OVR eye camera, while the centre viewer remains the configuration and
    /// OpenMVG-training-reference owner.
    /// </summary>
    [ExecuteAlways]
    [DefaultExecutionOrder(-1000)]
    [DisallowMultipleComponent]
    [AddComponentMenu("Gsplat/Gsplat Stereo Omni Rig")]
    [RequireComponent(typeof(GsplatOmniViewer))]
    public sealed class GsplatStereoOmniRig : MonoBehaviour
    {
        [Tooltip("Enables separate left- and right-eye ERP rasterization and compositing while the application is running.")]
        public bool EnableStereo = true;
        [Tooltip("The centre-eye viewer used as the shared GSplat configuration and OpenMVG training-pose reference.")]
        public GsplatOmniViewer SourceViewer;
        [Tooltip("Optional explicit left eye anchor. When empty, LeftEyeAnchor is found beside CenterEyeAnchor.")]
        public Transform LeftEyeAnchor;
        [Tooltip("Optional explicit right eye anchor. When empty, RightEyeAnchor is found beside CenterEyeAnchor.")]
        public Transform RightEyeAnchor;
        [Tooltip("Sort separately for both eye viewpoints. This is required for correct nearby translucent Gaussian ordering.")]
        public bool ForceSortPerEye = true;
        [Tooltip("Show the desktop Debug ERP for the left eye only, avoiding two overlapping debug windows.")]
        public bool ShowDebugErpForLeftEyeOnly = true;

        [SerializeField, HideInInspector] GsplatOmniViewer m_leftEyeViewer;
        [SerializeField, HideInInspector] GsplatOmniViewer m_rightEyeViewer;
        [SerializeField, HideInInspector] string m_status;

        public GsplatOmniViewer LeftEyeViewer => m_leftEyeViewer;
        public GsplatOmniViewer RightEyeViewer => m_rightEyeViewer;
        public string Status => m_status;
        /// <summary>True only while the parent OVR rig is actually rendering separate left/right cameras.</summary>
        public bool IsStereoActive => Application.isPlaying && EnableStereo && IsOvrPerEyeModeEnabled();

        void Reset()
        {
            SourceViewer = GetComponent<GsplatOmniViewer>();
            ResolveEyeAnchors();
        }

        void OnEnable()
        {
            EnsureSourceViewer();
            ResolveEyeAnchors();
        }

        void OnDisable()
        {
            RestoreCentreEyeRendering();
        }

        void LateUpdate()
        {
            // Preserve ordinary centre-eye Scene/Game preview behaviour in the Editor. OVR creates and
            // enables its physical eye cameras during Play Mode, so eye viewers must be created only then.
            if (!Application.isPlaying || !EnableStereo || !IsOvrPerEyeModeEnabled())
            {
                RestoreCentreEyeRendering();
                m_status = Application.isPlaying ? "Centre-eye GSplat active." : "Waiting for Play Mode.";
                return;
            }

            EnsureSourceViewer();
            ResolveEyeAnchors();
            if (!SourceViewer || !LeftEyeAnchor || !RightEyeAnchor)
            {
                m_status = "Waiting for CenterEyeAnchor sibling LeftEyeAnchor and RightEyeAnchor.";
                return;
            }

            var leftCamera = LeftEyeAnchor.GetComponent<Camera>();
            var rightCamera = RightEyeAnchor.GetComponent<Camera>();
            if (!leftCamera || !rightCamera)
            {
                m_status = "Waiting for OVR eye cameras.";
                return;
            }
            m_leftEyeViewer = EnsureEyeViewer(leftCamera, m_leftEyeViewer);
            m_rightEyeViewer = EnsureEyeViewer(rightCamera, m_rightEyeViewer);
            if (!m_leftEyeViewer || !m_rightEyeViewer)
            {
                m_status = "Could not create GSplat eye viewers.";
                return;
            }

            ConfigureEyeViewer(m_leftEyeViewer, true);
            ConfigureEyeViewer(m_rightEyeViewer, false);

            // The centre camera is normally disabled by OVRCameraRig in per-eye mode. Only disable its viewer
            // after both eye viewers have been created; keeping it enabled during OVR startup prevents a
            // transient camera-order race from leaving the application with no active ERP producer.
            if (SourceViewer.enabled && (leftCamera.enabled || rightCamera.enabled))
                SourceViewer.enabled = false;

            m_status = "Stereo active: separate left/right ERP textures and composites.";
        }

        public void SetTrainingPoseReference(Quaternion rotation)
        {
            EnsureSourceViewer();
            SourceViewer?.SetTrainingPoseReference(rotation);
            if (!IsStereoActive)
                return;
            m_leftEyeViewer?.SetTrainingPoseReference(rotation);
            m_rightEyeViewer?.SetTrainingPoseReference(rotation);
        }

        public void ClearTrainingPoseReference()
        {
            EnsureSourceViewer();
            SourceViewer?.ClearTrainingPoseReference();
            if (!IsStereoActive)
                return;
            m_leftEyeViewer?.ClearTrainingPoseReference();
            m_rightEyeViewer?.ClearTrainingPoseReference();
        }

        bool IsOvrPerEyeModeEnabled()
        {
            // GSplat remains independent of Meta assemblies. Discover OVRCameraRig by name so a normal
            // centre-eye camera can never be taken over merely because left/right camera objects exist.
            foreach (var component in GetComponentsInParent<MonoBehaviour>(true))
            {
                if (!component || component.GetType().Name != "OVRCameraRig")
                    continue;

                var field = component.GetType().GetField("usePerEyeCameras",
                    BindingFlags.Instance | BindingFlags.Public);
                return field != null && field.FieldType == typeof(bool) && (bool)field.GetValue(component);
            }

            return false;
        }

        void EnsureSourceViewer()
        {
            if (!SourceViewer)
                SourceViewer = GetComponent<GsplatOmniViewer>();
        }

        void ResolveEyeAnchors()
        {
            var trackingSpace = transform.parent;
            if (!LeftEyeAnchor && trackingSpace)
                LeftEyeAnchor = trackingSpace.Find("LeftEyeAnchor");
            if (!RightEyeAnchor && trackingSpace)
                RightEyeAnchor = trackingSpace.Find("RightEyeAnchor");
        }

        static GsplatOmniViewer EnsureEyeViewer(Camera eyeCamera, GsplatOmniViewer currentViewer)
        {
            if (currentViewer && currentViewer.gameObject == eyeCamera.gameObject)
            {
                if (!currentViewer.enabled)
                    currentViewer.enabled = true;
                return currentViewer;
            }

            var viewer = eyeCamera.GetComponent<GsplatOmniViewer>();
            if (!viewer)
                viewer = eyeCamera.gameObject.AddComponent<GsplatOmniViewer>();
            if (!viewer.enabled)
                viewer.enabled = true;
            return viewer;
        }

        void ConfigureEyeViewer(GsplatOmniViewer eyeViewer, bool leftEye)
        {
            bool changed = eyeViewer.ErpWidth != SourceViewer.ErpWidth ||
                           eyeViewer.ErpHeight != SourceViewer.ErpHeight ||
                           !Mathf.Approximately(eyeViewer.OmniNearDistance, SourceViewer.OmniNearDistance) ||
                           eyeViewer.Rasterizer != SourceViewer.Rasterizer ||
                           !Mathf.Approximately(eyeViewer.PositionRefreshThreshold, SourceViewer.PositionRefreshThreshold) ||
                           eyeViewer.UseVerticalBlackPadding != SourceViewer.UseVerticalBlackPadding ||
                           eyeViewer.VerticalBlackPaddingPixels != SourceViewer.VerticalBlackPaddingPixels ||
                           eyeViewer.UpdatePolicy != SourceViewer.UpdatePolicy ||
                           eyeViewer.BackgroundColor != SourceViewer.BackgroundColor ||
                           eyeViewer.AutoFindRenderers != SourceViewer.AutoFindRenderers ||
                           eyeViewer.Renderers != SourceViewer.Renderers ||
                           eyeViewer.ForceSortPerErpRender != ForceSortPerEye ||
                           !eyeViewer.UseBuiltInCameraCommandBufferComposite;

            eyeViewer.ErpWidth = SourceViewer.ErpWidth;
            eyeViewer.ErpHeight = SourceViewer.ErpHeight;
            eyeViewer.OmniNearDistance = SourceViewer.OmniNearDistance;
            eyeViewer.Rasterizer = SourceViewer.Rasterizer;
            eyeViewer.PositionRefreshThreshold = SourceViewer.PositionRefreshThreshold;
            eyeViewer.UseVerticalBlackPadding = SourceViewer.UseVerticalBlackPadding;
            eyeViewer.VerticalBlackPaddingPixels = SourceViewer.VerticalBlackPaddingPixels;
            eyeViewer.UpdatePolicy = SourceViewer.UpdatePolicy;
            eyeViewer.BackgroundColor = SourceViewer.BackgroundColor;
            eyeViewer.AutoFindRenderers = SourceViewer.AutoFindRenderers;
            eyeViewer.Renderers = SourceViewer.Renderers;
            eyeViewer.ForceSortPerErpRender = ForceSortPerEye;
            eyeViewer.UseBuiltInCameraCommandBufferComposite = true;
            eyeViewer.ShowDebugErp = !ShowDebugErpForLeftEyeOnly || leftEye
                ? SourceViewer.ShowDebugErp
                : false;

            if (SourceViewer.UseTrainingPoseReference)
            {
                if (!eyeViewer.UseTrainingPoseReference ||
                    Quaternion.Angle(eyeViewer.TrainingReferenceRotation, SourceViewer.TrainingReferenceRotation) > 0.001f)
                    eyeViewer.SetTrainingPoseReference(SourceViewer.TrainingReferenceRotation);
            }
            else if (eyeViewer.UseTrainingPoseReference)
            {
                eyeViewer.ClearTrainingPoseReference();
            }

            if (changed)
                eyeViewer.ForceRender();
        }

        void RestoreCentreEyeRendering()
        {
            if (m_leftEyeViewer)
                m_leftEyeViewer.enabled = false;
            if (m_rightEyeViewer)
                m_rightEyeViewer.enabled = false;
            if (SourceViewer && !SourceViewer.enabled)
                SourceViewer.enabled = true;
        }
    }
}
