// Copyright (c) 2026
// SPDX-License-Identifier: MIT

using System;
using System.IO;
using UnityEngine;
using UnityEngine.Rendering;

namespace Gsplat
{
    /// <summary>
    /// Displays a source ERP image around the active camera. Its position follows the camera, while its orientation
    /// remains at the orientation of the selected training pose so the user can freely look around the 360-degree frame.
    /// </summary>
    [ExecuteAlways]
    [RequireComponent(typeof(MeshFilter), typeof(MeshRenderer))]
    [AddComponentMenu("Gsplat/Ground-Truth ERP Frame Displayer")]
    public sealed class GsplatGroundTruthFrameDisplayer : MonoBehaviour
    {
        public enum FrameDisplayerType
        {
            InwardFacingSphere,
            InwardFacingCubeMap,
        }

        [Tooltip("Camera whose translation this displayer follows. Its rotation is intentionally not followed.")]
        public Camera TargetCamera;
        [Tooltip("Absolute folder containing the ERP images named by data_views.json. The JSON's original root_path is not used.")]
        public string ImagesFolderPath;
        [Tooltip("Geometry used to display the ERP source frame.")]
        public FrameDisplayerType DisplayType = FrameDisplayerType.InwardFacingSphere;
        [Tooltip("Keep the displayer centered on the camera while preserving its training-pose orientation.")]
        public bool FollowCameraPosition = true;
        [Tooltip("Align the ERP frame to the applied training-pose orientation. Disable only when the dataset has already been reoriented to Unity world axes.")]
        public bool AlignToAppliedPose = true;
        [Tooltip("Whether the selected source frame is currently rendered.")]
        public bool ShowFrame = true;

        [Header("Geometry")]
        [Min(0.01f)] public float Radius = 10.0f;
        [Range(12, 256)] public int SphereLongitudeSegments = 96;
        [Range(6, 128)] public int SphereLatitudeSegments = 48;

        [Header("ERP orientation")]
        [Range(-180.0f, 180.0f)]
        [Tooltip("Horizontal correction applied after using the training-pose rotation. Positive values move image content to the right.")]
        public float LongitudeOffsetDegrees;
        [Tooltip("Invert the vertical coordinate only if the supplied ERP source has the opposite top/bottom convention.")]
        public bool FlipVertical;
        [Min(0.0f)] public float Exposure = 1.0f;

        [SerializeField, HideInInspector] string m_currentFilename;
        [SerializeField, HideInInspector] string m_status = "No ground-truth frame loaded.";

        MeshFilter m_meshFilter;
        MeshRenderer m_meshRenderer;
        Mesh m_generatedMesh;
        Material m_material;
        Texture2D m_loadedTexture;
        FrameDisplayerType m_generatedDisplayType;
        float m_generatedRadius = -1.0f;
        int m_generatedLongitudeSegments;
        int m_generatedLatitudeSegments;

        static readonly int k_MainTex = Shader.PropertyToID("_MainTex");
        static readonly int k_LongitudeOffset = Shader.PropertyToID("_LongitudeOffsetDegrees");
        static readonly int k_FlipVertical = Shader.PropertyToID("_FlipVertical");
        static readonly int k_Exposure = Shader.PropertyToID("_Exposure");

        public string CurrentFilename => m_currentFilename;
        public string Status => m_status;

        void Reset()
        {
            EnsureTargetCamera();
        }

        void OnEnable()
        {
            EnsureTargetCamera();
            EnsureRenderer();
            UpdateMaterialProperties();
        }

        void OnDisable()
        {
            if (m_meshRenderer)
                m_meshRenderer.enabled = false;
        }

        void OnDestroy()
        {
            DestroyUnityObject(m_generatedMesh);
            DestroyUnityObject(m_material);
            DestroyUnityObject(m_loadedTexture);
            m_generatedMesh = null;
            m_material = null;
            m_loadedTexture = null;
        }

        void OnValidate()
        {
            Radius = Mathf.Max(0.01f, Radius);
            SphereLongitudeSegments = Mathf.Clamp(SphereLongitudeSegments, 12, 256);
            SphereLatitudeSegments = Mathf.Clamp(SphereLatitudeSegments, 6, 128);
            Exposure = Mathf.Max(0.0f, Exposure);

            if (!isActiveAndEnabled)
                return;

            EnsureRenderer();
            UpdateMaterialProperties();
        }

        void LateUpdate()
        {
            if (FollowCameraPosition)
            {
                EnsureTargetCamera();
                if (TargetCamera && HasIndependentCameraTransform())
                    transform.position = TargetCamera.transform.position;
            }

            if (isActiveAndEnabled)
            {
                EnsureRenderer();
                UpdateMaterialProperties();
            }
        }

        /// <summary>
        /// Loads and displays the ERP file named by a data_views.json record. The supplied position and rotation are
        /// the already converted Unity-world training pose from <see cref="GsplatTrainingPoseViewer"/>.
        /// </summary>
        public bool ApplyTrainingFrame(string jsonFilename, Vector3 cameraPosition, Quaternion trainingPoseRotation)
        {
            EnsureRenderer();
            EnsureTargetCamera();
            if (!HasIndependentCameraTransform())
            {
                m_status = "Ground-truth displayer must be on a GameObject outside the target camera hierarchy so it can keep a fixed orientation.";
                SetRendererEnabled(false);
                return false;
            }

            transform.position = cameraPosition;
            if (AlignToAppliedPose)
                transform.rotation = trainingPoseRotation;

            if (string.IsNullOrWhiteSpace(jsonFilename))
            {
                m_status = "The selected JSON view does not contain an image filename.";
                SetRendererEnabled(false);
                return false;
            }

            if (string.IsNullOrWhiteSpace(ImagesFolderPath))
            {
                m_status = "Images Folder Path is not assigned.";
                SetRendererEnabled(false);
                return false;
            }

            string imagePath;
            try
            {
                imagePath = Path.Combine(ResolvePath(ImagesFolderPath), jsonFilename);
            }
            catch (Exception e)
            {
                m_status = $"Could not resolve the ground-truth image path: {e.Message}";
                SetRendererEnabled(false);
                return false;
            }

            if (!File.Exists(imagePath))
            {
                m_status = $"Ground-truth image was not found: {imagePath}";
                SetRendererEnabled(false);
                return false;
            }

            if (!string.Equals(m_currentFilename, imagePath, StringComparison.OrdinalIgnoreCase) || !m_loadedTexture)
            {
                if (!TryLoadTexture(imagePath, out var texture, out var error))
                {
                    m_status = error;
                    SetRendererEnabled(false);
                    return false;
                }

                DestroyUnityObject(m_loadedTexture);
                m_loadedTexture = texture;
                m_currentFilename = imagePath;
                m_material.SetTexture(k_MainTex, m_loadedTexture);
            }

            SetRendererEnabled(ShowFrame);
            m_status = $"Loaded {Path.GetFileName(imagePath)} ({m_loadedTexture.width} x {m_loadedTexture.height}).";
            return true;
        }

        public void ClearFrame()
        {
            DestroyUnityObject(m_loadedTexture);
            m_loadedTexture = null;
            m_currentFilename = string.Empty;
            m_status = "Ground-truth frame cleared.";
            if (m_material)
                m_material.SetTexture(k_MainTex, null);
            SetRendererEnabled(false);
        }

        void EnsureTargetCamera()
        {
            if (!TargetCamera)
                TargetCamera = Camera.main;
        }

        void EnsureRenderer()
        {
            m_meshFilter ??= GetComponent<MeshFilter>();
            m_meshRenderer ??= GetComponent<MeshRenderer>();
            if (!m_meshFilter || !m_meshRenderer)
                return;

            EnsureMaterial();
            if (GeometryChanged())
                RebuildGeometry();

            m_meshRenderer.sharedMaterial = m_material;
            m_meshRenderer.shadowCastingMode = ShadowCastingMode.Off;
            m_meshRenderer.receiveShadows = false;
            m_meshRenderer.motionVectorGenerationMode = MotionVectorGenerationMode.ForceNoMotion;
            m_meshRenderer.allowOcclusionWhenDynamic = false;
            SetRendererEnabled(ShowFrame && m_loadedTexture);
        }

        void EnsureMaterial()
        {
            if (m_material)
                return;

            var shader = Shader.Find("Hidden/Gsplat/Ground Truth ERP Backdrop");
            if (!shader)
            {
                m_status = "Shader 'Hidden/Gsplat/Ground Truth ERP Backdrop' could not be found.";
                return;
            }

            m_material = new Material(shader)
            {
                name = "Gsplat Ground-Truth ERP Backdrop (Runtime)",
                hideFlags = HideFlags.HideAndDontSave,
            };
        }

        bool GeometryChanged()
        {
            return !m_generatedMesh ||
                   m_generatedDisplayType != DisplayType ||
                   !Mathf.Approximately(m_generatedRadius, Radius) ||
                   m_generatedLongitudeSegments != SphereLongitudeSegments ||
                   m_generatedLatitudeSegments != SphereLatitudeSegments;
        }

        void RebuildGeometry()
        {
            DestroyUnityObject(m_generatedMesh);
            m_generatedMesh = DisplayType == FrameDisplayerType.InwardFacingSphere
                ? BuildSphereMesh(Radius, SphereLongitudeSegments, SphereLatitudeSegments)
                : BuildCubeMesh(Radius);
            m_generatedMesh.name = DisplayType == FrameDisplayerType.InwardFacingSphere
                ? "Gsplat Ground-Truth ERP Sphere"
                : "Gsplat Ground-Truth ERP Cube";
            m_generatedMesh.hideFlags = HideFlags.DontSave;
            m_meshFilter.sharedMesh = m_generatedMesh;
            m_generatedDisplayType = DisplayType;
            m_generatedRadius = Radius;
            m_generatedLongitudeSegments = SphereLongitudeSegments;
            m_generatedLatitudeSegments = SphereLatitudeSegments;
        }

        void UpdateMaterialProperties()
        {
            if (!m_material)
                return;
            m_material.SetFloat(k_LongitudeOffset, LongitudeOffsetDegrees);
            m_material.SetFloat(k_FlipVertical, FlipVertical ? 1.0f : 0.0f);
            m_material.SetFloat(k_Exposure, Exposure);
            if (m_loadedTexture)
                m_material.SetTexture(k_MainTex, m_loadedTexture);
        }

        void SetRendererEnabled(bool enabled)
        {
            if (m_meshRenderer)
                m_meshRenderer.enabled = enabled && m_material && m_loadedTexture;
        }

        bool HasIndependentCameraTransform()
        {
            if (!TargetCamera)
                return true;

            var cameraTransform = TargetCamera.transform;
            return transform != cameraTransform &&
                   !transform.IsChildOf(cameraTransform) &&
                   !cameraTransform.IsChildOf(transform);
        }

        static bool TryLoadTexture(string path, out Texture2D texture, out string error)
        {
            texture = null;
            error = null;
            try
            {
                byte[] data = File.ReadAllBytes(path);
                texture = new Texture2D(2, 2, TextureFormat.RGBA32, false, false)
                {
                    name = $"Ground Truth ERP - {Path.GetFileName(path)}",
                    wrapMode = TextureWrapMode.Repeat,
                    filterMode = FilterMode.Bilinear,
                    anisoLevel = 0,
                    hideFlags = HideFlags.HideAndDontSave,
                };
                texture.wrapModeU = TextureWrapMode.Repeat;
                texture.wrapModeV = TextureWrapMode.Clamp;
                if (!ImageConversion.LoadImage(texture, data, false))
                {
                    DestroyUnityObject(texture);
                    texture = null;
                    error = $"Unity could not decode the ground-truth image: {path}";
                    return false;
                }
                return true;
            }
            catch (Exception e)
            {
                DestroyUnityObject(texture);
                texture = null;
                error = $"Could not load ground-truth image '{path}': {e.Message}";
                return false;
            }
        }

        static Mesh BuildSphereMesh(float radius, int longitudeSegments, int latitudeSegments)
        {
            int columns = longitudeSegments + 1;
            var vertices = new Vector3[columns * (latitudeSegments + 1)];
            var triangles = new int[longitudeSegments * latitudeSegments * 6];

            int vertex = 0;
            for (int y = 0; y <= latitudeSegments; y++)
            {
                float latitude = Mathf.PI * y / latitudeSegments;
                float sinLatitude = Mathf.Sin(latitude);
                float cosLatitude = Mathf.Cos(latitude);
                for (int x = 0; x <= longitudeSegments; x++)
                {
                    float longitude = Mathf.PI * 2.0f * x / longitudeSegments;
                    vertices[vertex++] = new Vector3(
                        sinLatitude * Mathf.Sin(longitude),
                        cosLatitude,
                        sinLatitude * Mathf.Cos(longitude)) * radius;
                }
            }

            int triangle = 0;
            for (int y = 0; y < latitudeSegments; y++)
            {
                for (int x = 0; x < longitudeSegments; x++)
                {
                    int i0 = y * columns + x;
                    int i1 = i0 + 1;
                    int i2 = i0 + columns;
                    int i3 = i2 + 1;
                    triangles[triangle++] = i0;
                    triangles[triangle++] = i2;
                    triangles[triangle++] = i1;
                    triangles[triangle++] = i1;
                    triangles[triangle++] = i2;
                    triangles[triangle++] = i3;
                }
            }

            var mesh = new Mesh { indexFormat = IndexFormat.UInt32 };
            mesh.vertices = vertices;
            mesh.triangles = triangles;
            mesh.RecalculateBounds();
            return mesh;
        }

        static Mesh BuildCubeMesh(float radius)
        {
            var vertices = new[]
            {
                // +Z
                new Vector3(-radius, -radius, radius), new Vector3(radius, -radius, radius),
                new Vector3(radius, radius, radius), new Vector3(-radius, radius, radius),
                // -Z
                new Vector3(radius, -radius, -radius), new Vector3(-radius, -radius, -radius),
                new Vector3(-radius, radius, -radius), new Vector3(radius, radius, -radius),
                // +X
                new Vector3(radius, -radius, radius), new Vector3(radius, -radius, -radius),
                new Vector3(radius, radius, -radius), new Vector3(radius, radius, radius),
                // -X
                new Vector3(-radius, -radius, -radius), new Vector3(-radius, -radius, radius),
                new Vector3(-radius, radius, radius), new Vector3(-radius, radius, -radius),
                // +Y
                new Vector3(-radius, radius, radius), new Vector3(radius, radius, radius),
                new Vector3(radius, radius, -radius), new Vector3(-radius, radius, -radius),
                // -Y
                new Vector3(-radius, -radius, -radius), new Vector3(radius, -radius, -radius),
                new Vector3(radius, -radius, radius), new Vector3(-radius, -radius, radius),
            };
            var triangles = new int[36];
            for (int face = 0; face < 6; face++)
            {
                int vertex = face * 4;
                int triangle = face * 6;
                triangles[triangle] = vertex;
                triangles[triangle + 1] = vertex + 1;
                triangles[triangle + 2] = vertex + 2;
                triangles[triangle + 3] = vertex;
                triangles[triangle + 4] = vertex + 2;
                triangles[triangle + 5] = vertex + 3;
            }

            var mesh = new Mesh { indexFormat = IndexFormat.UInt32 };
            mesh.vertices = vertices;
            mesh.triangles = triangles;
            mesh.RecalculateBounds();
            return mesh;
        }

        static string ResolvePath(string path)
        {
            if (Path.IsPathRooted(path))
                return Path.GetFullPath(path);
            string projectRoot = Path.GetFullPath(Path.Combine(Application.dataPath, ".."));
            return Path.GetFullPath(Path.Combine(projectRoot, path));
        }

        static void DestroyUnityObject(UnityEngine.Object value)
        {
            if (!value)
                return;
            if (Application.isPlaying)
                Destroy(value);
            else
                DestroyImmediate(value);
        }
    }
}
