// Copyright (c) 2025 Yize Wu
// SPDX-License-Identifier: MIT

using System;
using System.IO;
#if UNITY_EDITOR
using UnityEditor;
#endif
using UnityEngine;

namespace Gsplat
{
    public class GsplatSettings : ScriptableObject
    {
        const string k_gsplatSettingsResourcesPath = "GsplatSettings";

        const string k_gsplatSettingsPath =
            "Assets/Gsplat/Settings/Resources/" + k_gsplatSettingsResourcesPath + ".asset";

        static GsplatSettings s_instance;

        public static GsplatSettings Instance
        {
            get
            {
                if (s_instance)
                    return s_instance;

                var settings = Resources.Load<GsplatSettings>(k_gsplatSettingsResourcesPath);
#if UNITY_EDITOR
                if (!settings)
                {
                    var assetPath = Path.GetDirectoryName(k_gsplatSettingsPath);
                    if (!Directory.Exists(assetPath))
                        Directory.CreateDirectory(assetPath);

                    settings = CreateInstance<GsplatSettings>();
                    settings.Reset();
                    AssetDatabase.CreateAsset(settings, k_gsplatSettingsPath);
                    AssetDatabase.SaveAssets();
                }
                else if (settings.Version < new Version("1.2.0"))
                {
                    Debug.Log($"Updated GsplatSettings from version {settings.Version}.");
                    settings.Materials = DefaultMaterials;
                    settings.m_prevComputeShader = null;
                    settings.Version = GsplatUtils.k_Version;
                    settings.OnValidate();
                    EditorUtility.SetDirty(settings);
                    AssetDatabase.SaveAssets();
                }

                if (settings)
                {
                    settings.EnsureDefaultReferences();
                    settings.OnValidate();
                    EditorUtility.SetDirty(settings);
                }
#endif

                s_instance = settings;
                return s_instance;
            }
        }

        public ComputeShader ComputeShader;
        public uint SplatInstanceSize = 128;
        public uint UploadBatchSize = 100000;
        [Range(1, 20)] public uint MaxRenderOrder = 1;
        public bool DisplayBoundingBoxes = false;
        [Tooltip("If a camera moves more that this threshold, each GsplatRenderer compute sorting and cutouts regardless of refresh rate")]
        [Range(0.05f, 1f)] public float CameraTranslationRefreshTreshold = 0.2f;
        [Tooltip("If a camera rotates more that this threshold, each GsplatRenderer compute sorting and cutouts refresh regardless of refresh rate")]
        [Range(0.2f, 30f)] public float CameraRotationRefreshTreshold = 10;
        public bool ShowImportErrors = true;
        public GsplatMaterial[] Materials;
        public Mesh Mesh { get; private set; }

        public bool Valid => ComputeShader && Materials != null && Materials.Length > 0 && Mesh && SplatInstanceSize > 0 &&
                             Array.TrueForAll(Materials, mat =>
                                 mat != null && mat.DefaultMaterial != null && mat.CalcDepthShader != null &&
                                 mat.InitOrderShader != null);

        public Version Version
        {
            get => Version.Parse(m_version);
            set => m_version = value.ToString();
        }

        ComputeShader m_prevComputeShader;
        uint m_prevSplatInstanceSize;

        [HideInInspector] [SerializeField] string m_version = "1.0.0";

#if UNITY_EDITOR
        static ComputeShader DefaultComputeShader => AssetDatabase.LoadAssetAtPath<ComputeShader>(
            GsplatUtils.k_PackagePath +
            "Runtime/Shaders/Gsplat.compute");

        static GsplatMaterial[] DefaultMaterials
        {
            get
            {
                var materials = new GsplatMaterial[Enum.GetValues(typeof(CompressionMode)).Length];
                materials[(int)CompressionMode.Uncompressed] =
                    LoadDefaultMaterial("GsplatUncompressed");
                materials[(int)CompressionMode.Spark] =
                    LoadDefaultMaterial("GsplatSpark");
                return materials;
            }
        }

        static GsplatMaterial LoadDefaultMaterial(string assetName)
        {
            var material = AssetDatabase.LoadAssetAtPath<GsplatMaterial>(
                $"{GsplatUtils.k_PackagePath}Runtime/Materials/{assetName}.asset");
            if (material)
                return material;

            var guids = AssetDatabase.FindAssets($"t:GsplatMaterial {assetName}");
            foreach (var guid in guids)
            {
                material = AssetDatabase.LoadAssetAtPath<GsplatMaterial>(AssetDatabase.GUIDToAssetPath(guid));
                if (material && material.name == assetName)
                    return material;
            }

            return null;
        }

        void EnsureDefaultReferences()
        {
            if (!ComputeShader)
                ComputeShader = DefaultComputeShader;

            var requiredLength = Enum.GetValues(typeof(CompressionMode)).Length;
            if (Materials == null || Materials.Length != requiredLength)
                Materials = DefaultMaterials;
            else
            {
                var defaults = DefaultMaterials;
                for (int i = 0; i < Materials.Length; ++i)
                    if (!Materials[i] && i < defaults.Length)
                        Materials[i] = defaults[i];
            }
        }

        public void Reset()
        {
            Version = GsplatUtils.k_Version;
            ComputeShader = DefaultComputeShader;
            Materials = DefaultMaterials;
            m_prevComputeShader = null;
            m_prevSplatInstanceSize = 0;
            OnValidate();
        }
#endif

        void CreateMeshInstance()
        {
            var meshPositions = new Vector3[4 * SplatInstanceSize];
            var meshIndices = new int[6 * SplatInstanceSize];
            for (uint i = 0; i < SplatInstanceSize; ++i)
            {
                unsafe
                {
                    meshPositions[i * 4] = new Vector3(-1, -1, *(float*)&i);
                    meshPositions[i * 4 + 1] = new Vector3(1, -1, *(float*)&i);
                    meshPositions[i * 4 + 2] = new Vector3(-1, 1, *(float*)&i);
                    meshPositions[i * 4 + 3] = new Vector3(1, 1, *(float*)&i);
                }

                int b = (int)i * 4;
                Array.Copy(new[] { 0 + b, 1 + b, 2 + b, 1 + b, 3 + b, 2 + b }, 0, meshIndices, i * 6, 6);
            }

            Mesh = new Mesh
            {
                name = "GsplatMeshInstance",
                vertices = meshPositions,
                triangles = meshIndices,
                hideFlags = HideFlags.HideAndDontSave
            };
        }

        void OnValidate()
        {
#if UNITY_EDITOR
            EnsureDefaultReferences();
#endif
            if (ComputeShader != m_prevComputeShader)
            {
                GsplatSorter.Instance.InitSorter(ComputeShader);
                m_prevComputeShader = ComputeShader;
            }

            if (SplatInstanceSize == 0)
                SplatInstanceSize = 1;

            if (SplatInstanceSize != m_prevSplatInstanceSize || !Mesh)
            {
                if (Mesh)
                    DestroyImmediate(Mesh);
                CreateMeshInstance();
                m_prevSplatInstanceSize = SplatInstanceSize;
            }
#if UNITY_EDITOR
            if (Materials == null)
                return;

            foreach (var mat in Materials)
            {
                if (mat)
                    mat.Reset();
            }
#endif
        }

        void OnEnable()
        {
#if UNITY_EDITOR
            EnsureDefaultReferences();
#endif
            GsplatSorter.Instance.InitSorter(ComputeShader);
            m_prevComputeShader = ComputeShader;

            CreateMeshInstance();
            m_prevSplatInstanceSize = SplatInstanceSize;
        }
    }
}
