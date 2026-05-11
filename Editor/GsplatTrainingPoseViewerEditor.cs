// Copyright (c) 2026
// SPDX-License-Identifier: MIT

using System.Linq;
using UnityEditor;
using UnityEngine;

namespace Gsplat.Editor
{
    [CustomEditor(typeof(GsplatTrainingPoseViewer))]
    public class GsplatTrainingPoseViewerEditor : UnityEditor.Editor
    {
        public override void OnInspectorGUI()
        {
            serializedObject.Update();

            DrawPropertiesExcluding(serializedObject, "m_Script",
                nameof(GsplatTrainingPoseViewer.SelectedPoseIndex),
                "m_poses",
                "m_status",
                "m_loadedSuccessfully");

            var viewer = (GsplatTrainingPoseViewer)target;

            using (new EditorGUILayout.HorizontalScope())
            {
                if (GUILayout.Button("Browse Views"))
                    SetPath(nameof(GsplatTrainingPoseViewer.ViewsJsonPath), "Select data_views.json");
                if (GUILayout.Button("Browse Extrinsics"))
                    SetPath(nameof(GsplatTrainingPoseViewer.ExtrinsicsJsonPath), "Select data_extrinsics.json");
            }

            using (new EditorGUILayout.HorizontalScope())
            {
                if (GUILayout.Button("Load JSON"))
                {
                    Undo.RecordObject(viewer, "Load OpenMVG Training Poses");
                    viewer.LoadJson();
                    EditorUtility.SetDirty(viewer);
                }

                EditorGUI.BeginDisabledGroup(viewer.PoseCount == 0);
                if (GUILayout.Button("Apply Selected Pose"))
                    ApplySelectedPose(viewer);
                EditorGUI.EndDisabledGroup();
            }

            DrawPoseControls(viewer);
            DrawStatus(viewer);

            serializedObject.ApplyModifiedProperties();
        }

        void DrawPoseControls(GsplatTrainingPoseViewer viewer)
        {
            EditorGUI.BeginDisabledGroup(viewer.PoseCount == 0);

            if (viewer.PoseCount > 0)
            {
                string[] labels = Enumerable.Range(0, viewer.PoseCount)
                    .Select(viewer.GetPoseLabel)
                    .ToArray();
                int newIndex = EditorGUILayout.Popup("Loaded Pose", viewer.SelectedPoseIndex, labels);
                if (newIndex != viewer.SelectedPoseIndex)
                {
                    Undo.RecordObject(viewer, "Select OpenMVG Training Pose");
                    viewer.SelectedPoseIndex = newIndex;
                    EditorUtility.SetDirty(viewer);
                }
            }

            using (new EditorGUILayout.HorizontalScope())
            {
                if (GUILayout.Button("Previous"))
                {
                    Undo.RecordObject(viewer, "Select Previous OpenMVG Training Pose");
                    viewer.PreviousPose(false);
                    EditorUtility.SetDirty(viewer);
                }

                if (GUILayout.Button("Next"))
                {
                    Undo.RecordObject(viewer, "Select Next OpenMVG Training Pose");
                    viewer.NextPose(false);
                    EditorUtility.SetDirty(viewer);
                }
            }

            using (new EditorGUILayout.HorizontalScope())
            {
                if (GUILayout.Button("Previous + Apply"))
                {
                    Undo.RecordObject(viewer, "Apply Previous OpenMVG Training Pose");
                    RecordCameraUndo(viewer);
                    viewer.PreviousPose(true);
                    EditorUtility.SetDirty(viewer);
                }

                if (GUILayout.Button("Next + Apply"))
                {
                    Undo.RecordObject(viewer, "Apply Next OpenMVG Training Pose");
                    RecordCameraUndo(viewer);
                    viewer.NextPose(true);
                    EditorUtility.SetDirty(viewer);
                }
            }

            EditorGUI.EndDisabledGroup();
        }

        void DrawStatus(GsplatTrainingPoseViewer viewer)
        {
            MessageType type = MessageType.Info;
            if (!viewer.LoadedSuccessfully && viewer.PoseCount == 0)
                type = MessageType.Warning;
            if (!string.IsNullOrEmpty(viewer.Status) && viewer.Status.StartsWith("Failed"))
                type = MessageType.Error;
            EditorGUILayout.HelpBox(viewer.Status, type);
        }

        void SetPath(string propertyName, string title)
        {
            var property = serializedObject.FindProperty(propertyName);
            string startDirectory = string.IsNullOrEmpty(property.stringValue)
                ? Application.dataPath
                : System.IO.Path.GetDirectoryName(property.stringValue);
            if (string.IsNullOrEmpty(startDirectory))
                startDirectory = Application.dataPath;
            string path = EditorUtility.OpenFilePanel(title, startDirectory, "json");
            if (string.IsNullOrEmpty(path))
                return;
            property.stringValue = path;
            serializedObject.ApplyModifiedProperties();
        }

        static void ApplySelectedPose(GsplatTrainingPoseViewer viewer)
        {
            Undo.RecordObject(viewer, "Apply OpenMVG Training Pose");
            RecordCameraUndo(viewer);
            viewer.ApplySelectedPose();
            EditorUtility.SetDirty(viewer);
        }

        static void RecordCameraUndo(GsplatTrainingPoseViewer viewer)
        {
            Camera camera = viewer.TargetCamera;
            if (!camera)
                camera = viewer.GetComponent<Camera>();
            if (!camera)
                camera = Camera.main;
            if (!camera)
                return;
            Undo.RecordObject(camera.transform, "Apply OpenMVG Training Pose");
            Undo.RecordObject(camera, "Apply OpenMVG Training Pose");
        }
    }
}
