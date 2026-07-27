// Copyright (c) 2026
// SPDX-License-Identifier: MIT

using System;
using System.Collections.Generic;
using UnityEngine;

namespace Gsplat
{
    internal static class GsplatRuntimeRegistry
    {
        static readonly HashSet<GsplatRenderer> s_renderers = new();
        static readonly HashSet<GsplatOmniViewer> s_viewers = new();
        static Camera[] s_cameras = Array.Empty<Camera>();
        static int s_cameraCount;
        static int s_cameraFrame = -1;

        internal static HashSet<GsplatRenderer> Renderers => s_renderers;

        internal static void Register(GsplatRenderer renderer)
        {
            if (renderer)
                s_renderers.Add(renderer);
        }

        internal static void Unregister(GsplatRenderer renderer)
        {
            if (renderer)
                s_renderers.Remove(renderer);
        }

        internal static void Register(GsplatOmniViewer viewer)
        {
            if (viewer)
                s_viewers.Add(viewer);
        }

        internal static void Unregister(GsplatOmniViewer viewer)
        {
            if (viewer)
                s_viewers.Remove(viewer);
        }

        internal static bool HasActiveViewer()
        {
            foreach (var viewer in s_viewers)
                if (viewer && viewer.isActiveAndEnabled)
                    return true;
            return false;
        }

        internal static bool TryGetActiveViewer(out GsplatOmniViewer viewer)
        {
            foreach (var candidate in s_viewers)
            {
                if (!candidate || !candidate.isActiveAndEnabled)
                    continue;
                viewer = candidate;
                return true;
            }

            viewer = null;
            return false;
        }

        internal static void CopyActiveRenderers(List<GsplatRenderer> destination)
        {
            destination.Clear();
            foreach (var renderer in s_renderers)
                if (renderer && renderer.isActiveAndEnabled)
                    destination.Add(renderer);
        }

        internal static void GetCameras(out Camera[] cameras, out int count)
        {
            if (s_cameraFrame != Time.frameCount)
            {
                int required = Camera.allCamerasCount;
                if (s_cameras.Length < required)
                    s_cameras = new Camera[Mathf.NextPowerOfTwo(Mathf.Max(required, 1))];
                s_cameraCount = Camera.GetAllCameras(s_cameras);
                s_cameraFrame = Time.frameCount;
            }

            cameras = s_cameras;
            count = s_cameraCount;
        }

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        static void Reset()
        {
            s_renderers.Clear();
            s_viewers.Clear();
            s_cameras = Array.Empty<Camera>();
            s_cameraCount = 0;
            s_cameraFrame = -1;
        }

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        static void RebuildAfterSceneLoad()
        {
            foreach (var renderer in UnityEngine.Object.FindObjectsOfType<GsplatRenderer>())
                if (renderer && renderer.isActiveAndEnabled)
                    s_renderers.Add(renderer);
            foreach (var viewer in UnityEngine.Object.FindObjectsOfType<GsplatOmniViewer>())
                if (viewer && viewer.isActiveAndEnabled)
                    s_viewers.Add(viewer);
        }
    }
}
