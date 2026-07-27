// Copyright (c) 2026
// SPDX-License-Identifier: MIT

using System.Collections.Generic;
using System.IO;
using UnityEditor;
using UnityEditor.Build;
using UnityEditor.Build.Reporting;
using UnityEngine;

namespace Gsplat.Editor
{
    public sealed class GsplatStreamBuildProcessor : BuildPlayerProcessor
    {
        public override void PrepareForBuild(BuildPlayerContext buildPlayerContext)
        {
            if (buildPlayerContext.BuildPlayerOptions.target != BuildTarget.StandaloneWindows &&
                buildPlayerContext.BuildPlayerOptions.target != BuildTarget.StandaloneWindows64 &&
                buildPlayerContext.BuildPlayerOptions.target != BuildTarget.StandaloneLinux64 &&
                buildPlayerContext.BuildPlayerOptions.target != BuildTarget.StandaloneOSX)
                return;

            var includedIds = new HashSet<string>();
            foreach (string guid in AssetDatabase.FindAssets("t:GsplatAsset"))
            {
                string assetPath = AssetDatabase.GUIDToAssetPath(guid);
                var asset = AssetDatabase.LoadAssetAtPath<GsplatAsset>(assetPath);
                if (!asset || asset.RuntimeStorage != GsplatRuntimeStorage.StreamedPlayerData)
                    continue;
                if (string.IsNullOrEmpty(asset.StreamDataId))
                    throw new BuildFailedException(
                        $"Streamed gsplat asset '{assetPath}' has no data id. Reimport the source PLY.");
                if (!includedIds.Add(asset.StreamDataId))
                    continue;

                string sourcePath = GsplatStreamData.GetEditorCachePath(asset.StreamDataId);
                if (!File.Exists(sourcePath))
                {
                    AssetDatabase.ImportAsset(assetPath, ImportAssetOptions.ForceUpdate);
                    asset = AssetDatabase.LoadAssetAtPath<GsplatAsset>(assetPath);
                    sourcePath = GsplatStreamData.GetEditorCachePath(asset.StreamDataId);
                }

                if (!File.Exists(sourcePath))
                    throw new BuildFailedException(
                        $"Missing streamed data for gsplat asset '{assetPath}'. Reimport the source PLY.");

                buildPlayerContext.AddAdditionalPathToStreamingAssets(sourcePath,
                    GsplatStreamData.GetBuildRelativePath(asset.StreamDataId));
            }
        }
    }
}
