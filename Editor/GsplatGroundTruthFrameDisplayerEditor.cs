// Copyright (c) 2026
// SPDX-License-Identifier: MIT

using UnityEditor;
using UnityEngine;

namespace Gsplat.Editor
{
    [CustomEditor(typeof(GsplatGroundTruthFrameDisplayer))]
    public class GsplatGroundTruthFrameDisplayerEditor : UnityEditor.Editor
    {
        public override void OnInspectorGUI()
        {
            serializedObject.Update();
            DrawPropertiesExcluding(serializedObject, "m_Script", "m_currentFilename", "m_status");

            var displayer = (GsplatGroundTruthFrameDisplayer)target;
            if (GUILayout.Button("Browse Images Folder"))
            {
                string initialPath = string.IsNullOrWhiteSpace(displayer.ImagesFolderPath)
                    ? Application.dataPath
                    : displayer.ImagesFolderPath;
                string path = EditorUtility.OpenFolderPanel("Select ground-truth ERP image folder", initialPath, string.Empty);
                if (!string.IsNullOrWhiteSpace(path))
                {
                    serializedObject.FindProperty(nameof(GsplatGroundTruthFrameDisplayer.ImagesFolderPath)).stringValue = path;
                    serializedObject.ApplyModifiedProperties();
                }
            }

            EditorGUILayout.HelpBox(displayer.Status, string.IsNullOrEmpty(displayer.CurrentFilename)
                ? MessageType.Info
                : MessageType.None);
            serializedObject.ApplyModifiedProperties();
        }
    }
}
