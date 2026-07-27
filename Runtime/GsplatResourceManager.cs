using System.Collections.Generic;
using UnityEngine;

namespace Gsplat
{
    public static class GsplatResourceManager
    {
        class Cache
        {
            public GsplatResource Resource;
            public int RefCount;
            public bool Pinned;
        }

        static readonly Dictionary<int, Cache> k_resourceCache = new();

        public static GsplatResource Get(GsplatAsset asset)
        {
            var key = asset.GetInstanceID();
            if (k_resourceCache.TryGetValue(key, out var cache))
            {
                cache.RefCount++;
                return cache.Resource;
            }

            cache = new Cache
            {
                Resource = asset.CreateResource(),
                RefCount = 1,
                Pinned = Application.isPlaying &&
                         GsplatSettings.Instance.PlayerGpuResidency ==
                         GsplatPlayerGpuResidency.PinUntilShutdown,
            };
            k_resourceCache[key] = cache;
            return cache.Resource;
        }

        public static void Release(GsplatAsset asset)
        {
            Release(asset.GetInstanceID());
        }

        public static bool IsPinned(int instanceID)
        {
            return instanceID != 0 && k_resourceCache.TryGetValue(instanceID, out var cache) && cache.Pinned;
        }

        public static void Release(int instanceID)
        {
            if (instanceID == 0)
                return;
            if (!k_resourceCache.TryGetValue(instanceID, out var cache))
            {
                Debug.LogWarning("Trying to release a GPU resource that is not cached.");
                return;
            }

            cache.RefCount--;
            if (cache.RefCount != 0 || cache.Pinned) return;
            cache.Resource.Dispose();
            k_resourceCache.Remove(instanceID);
        }

        static void DisposeAll()
        {
            foreach (var cache in k_resourceCache.Values)
                cache.Resource?.Dispose();
            k_resourceCache.Clear();
        }

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.BeforeSceneLoad)]
        static void InstallLifetimeHooks()
        {
            Application.quitting -= DisposeAll;
            Application.quitting += DisposeAll;
        }

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        static void Reset()
        {
            DisposeAll();
        }
    }
}
