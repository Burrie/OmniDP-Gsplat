// Copyright (c) 2026
// SPDX-License-Identifier: MIT

using System;
using UnityEngine;
#if GSPLAT_ENABLE_INPUT_SYSTEM
using UnityEngine.InputSystem;
#endif

namespace Gsplat
{
    [AddComponentMenu("Gsplat/PVG Time Controller")]
    public class GsplatPvgTimeController : MonoBehaviour
    {
        [Tooltip("Renderers whose PvgTime value will be animated. Leave empty with Auto Find Renderers enabled to control all active gsplat renderers.")]
        public GsplatRenderer[] Renderers = Array.Empty<GsplatRenderer>();
        [Tooltip("If enabled and Renderers is empty, all active GsplatRenderer components are controlled.")]
        public bool AutoFindRenderers = true;
        [Tooltip("Press this key in Play Mode to start or pause PVG time animation.")]
        public KeyCode ToggleKey = KeyCode.K;
        [Tooltip("PVG time units added per second while playing. Positive moves forward; negative moves backward.")]
        public float TimeSpeed = 1.0f;
        [Tooltip("Start animating immediately when entering Play Mode.")]
        public bool PlayOnStart;
        [Tooltip("Use unscaled time so animation keeps moving even if Time.timeScale is changed.")]
        public bool UseUnscaledDeltaTime;

        [SerializeField] bool m_playing;

        public bool Playing
        {
            get => m_playing;
            set => m_playing = value;
        }

        void OnEnable()
        {
            if (Application.isPlaying)
                m_playing = PlayOnStart;
        }

        void Update()
        {
            if (!Application.isPlaying)
                return;

            if (ToggleKeyPressed())
                m_playing = !m_playing;

            if (!m_playing || Mathf.Approximately(TimeSpeed, 0.0f))
                return;

            float delta = (UseUnscaledDeltaTime ? Time.unscaledDeltaTime : Time.deltaTime) * TimeSpeed;
            foreach (var renderer in GetControlledRenderers())
            {
                if (!renderer)
                    continue;
                renderer.PvgTime += delta;
            }
        }

        GsplatRenderer[] GetControlledRenderers()
        {
            if (Renderers != null && Renderers.Length > 0)
                return Renderers;
            if (!AutoFindRenderers)
                return Array.Empty<GsplatRenderer>();
            return FindObjectsOfType<GsplatRenderer>();
        }

        bool ToggleKeyPressed()
        {
#if GSPLAT_ENABLE_INPUT_SYSTEM
            if (Keyboard.current != null &&
                Enum.TryParse(ToggleKey.ToString(), out Key inputSystemKey) &&
                Keyboard.current[inputSystemKey].wasPressedThisFrame)
                return true;
#endif

#if ENABLE_LEGACY_INPUT_MANAGER
            return Input.GetKeyDown(ToggleKey);
#else
            return false;
#endif
        }
    }
}
