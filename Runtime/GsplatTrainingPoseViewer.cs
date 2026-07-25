// Copyright (c) 2026
// SPDX-License-Identifier: MIT

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using UnityEngine;
using UnityEngine.XR;

namespace Gsplat
{
    [ExecuteAlways]
    [AddComponentMenu("Gsplat/Training Pose Viewer")]
    public class GsplatTrainingPoseViewer : MonoBehaviour
    {
        [Tooltip("Center-eye camera used as the training-pose reference. Its transform is never moved while XR is active.")]
        public Camera TargetCamera;
        [Header("XR locomotion")]
        [Tooltip("World-space locomotion object to teleport when applying a pose. For Meta Quest, assign LocomotionOrigin (or the LocomotionMotion parent) of OVRCameraRig. The target CenterEye camera must be a descendant of this Transform.")]
        public Transform LocomotionRoot;
        [Tooltip("Renderer whose PVG timestamp follows the selected training pose. Defaults to a renderer on this GameObject.")]
        public GsplatRenderer TargetRenderer;
        [Tooltip("Absolute or project-relative path to data_views.json.")]
        public string ViewsJsonPath;
        [Tooltip("Absolute or project-relative path to data_extrinsics.json.")]
        public string ExtrinsicsJsonPath;
        [Tooltip("Optional coordinate root for OpenMVG poses. When empty, the current Transform of TargetRenderer (normally this Gsplat GameObject) is used automatically, including its runtime position, rotation, and scale.")]
        public Transform SceneRoot;

        [Min(0)] public int SelectedPoseIndex;
        public bool ApplyPosition = true;
        public bool ApplyRotation = true;
        public bool ApplyCameraFov = true;
        [Tooltip("Flip OpenMVG world X before applying the pose. Default off matches the training loader.")]
        public bool FlipX;
        [Tooltip("Flip OpenMVG world Y before applying the pose. Default off matches the training loader.")]
        public bool FlipY;
        [Tooltip("Flip OpenMVG world Z before applying the pose. Default off matches the training loader.")]
        public bool FlipZ;

        [Header("PVG time")]
        [Tooltip("When enabled, applying a pose also sets TargetRenderer.PvgTime from that pose's camera key.")]
        public bool ApplyPvgTime = true;
        [Tooltip("PVG time_duration[0].")]
        public float PvgTimeStart;
        [Tooltip("PVG time_duration[1].")]
        public float PvgTimeEnd = 1.0f;
        [Tooltip("Derive time_duration as [-frame_interval * (frame_num - 1) / 2, +frame_interval * (frame_num - 1) / 2] instead of using PvgTimeStart/PvgTimeEnd.")]
        public bool CalculateTimeDurationFromFrameInterval;
        [Tooltip("Frame interval used when calculating time_duration. Set this to the same --frame_interval used during training.")]
        [Min(0.000001f)] public float FrameInterval = 1.0f / 30.0f;
        [Tooltip("Number of frames used by timestamp = start + (end - start) * cam_key / (frames_len - 1). Set 0 to count loaded JSON views automatically.")]
        [Min(0)] public int FramesLength;
        [Tooltip("Use the view key as cam_key. Disable to use the matched pose key instead.")]
        public bool UseViewKeyAsCamKey = true;

        [Header("Ground-truth ERP frame")]
        [Tooltip("Optional inward-facing ERP frame displayer. When assigned, applying a training pose also loads that pose's source frame.")]
        public GsplatGroundTruthFrameDisplayer GroundTruthFrameDisplayer;
        [Tooltip("Load and display the selected pose's ground-truth ERP frame when a pose is applied.")]
        public bool ApplyGroundTruthFrame = true;

        [SerializeField] TrainingPoseInfo[] m_poses = Array.Empty<TrainingPoseInfo>();
        [SerializeField] string m_status = "No poses loaded.";
        [SerializeField] bool m_loadedSuccessfully;
        [SerializeField] int m_loadedFrameCount;

        public int PoseCount => m_poses?.Length ?? 0;
        public string Status => m_status;
        public bool LoadedSuccessfully => m_loadedSuccessfully;
        public TrainingPoseInfo[] Poses => m_poses;

        [Serializable]
        public struct TrainingPoseInfo
        {
            public int CamKey;
            public int PoseKey;
            public int ViewKey;
            public string Filename;
            public int Width;
            public int Height;
            public Vector3 Center;
            public Vector3 Right;
            public Vector3 Down;
            public Vector3 Forward;
            public float HorizontalFovDegrees;
            public float VerticalFovDegrees;
        }

        struct ViewRecord
        {
            public int CamKey;
            public int ViewKey;
            public int PoseKey;
            public string Filename;
            public int Width;
            public int Height;
        }

        struct ExtrinsicRecord
        {
            public int PoseKey;
            public double[,] Rotation;
            public Vector3 Center;
        }

        void Reset()
        {
            EnsureTargetCamera();
        }

        void OnEnable()
        {
            EnsureTargetCamera();
            EnsureTargetRenderer();
            EnsureGroundTruthFrameDisplayer();
        }

        void OnValidate()
        {
            FrameInterval = Mathf.Max(0.000001f, FrameInterval);
            if (m_poses != null && m_poses.Length > 0)
                SelectedPoseIndex = Mathf.Clamp(SelectedPoseIndex, 0, m_poses.Length - 1);
            else
                SelectedPoseIndex = 0;
        }

        public void LoadJson()
        {
            try
            {
                string viewsPath = ResolvePath(ViewsJsonPath);
                string extrinsicsPath = ResolvePath(ExtrinsicsJsonPath);

                if (string.IsNullOrWhiteSpace(viewsPath) || !File.Exists(viewsPath))
                    throw new FileNotFoundException("data_views.json was not found.", ViewsJsonPath);
                if (string.IsNullOrWhiteSpace(extrinsicsPath) || !File.Exists(extrinsicsPath))
                    throw new FileNotFoundException("data_extrinsics.json was not found.", ExtrinsicsJsonPath);

                var views = LoadViews(viewsPath);
                var extrinsics = LoadExtrinsics(extrinsicsPath);
                var poses = new List<TrainingPoseInfo>();
                int missingExtrinsics = 0;

                foreach (var view in views.OrderBy(v => Path.GetFileNameWithoutExtension(v.Filename), StringComparer.Ordinal))
                {
                    if (!extrinsics.TryGetValue(view.PoseKey, out var extrinsic))
                    {
                        missingExtrinsics++;
                        continue;
                    }

                    poses.Add(BuildPoseInfo(view, extrinsic));
                }

                m_poses = poses.ToArray();
                m_loadedFrameCount = views.Count > 0 ? views.Count : extrinsics.Count;
                SelectedPoseIndex = m_poses.Length > 0 ? Mathf.Clamp(SelectedPoseIndex, 0, m_poses.Length - 1) : 0;
                m_loadedSuccessfully = m_poses.Length > 0;
                m_status = m_poses.Length == 0
                    ? $"Loaded 0 poses. Views: {views.Count}, extrinsics: {extrinsics.Count}, missing matches: {missingExtrinsics}."
                    : $"Loaded {m_poses.Length} poses. Skipped {missingExtrinsics} view(s) without matching extrinsics.";
            }
            catch (Exception e)
            {
                m_poses = Array.Empty<TrainingPoseInfo>();
                m_loadedFrameCount = 0;
                SelectedPoseIndex = 0;
                m_loadedSuccessfully = false;
                m_status = $"Failed to load OpenMVG poses: {e.Message}";
                Debug.LogWarning(m_status, this);
            }
        }

        public bool ApplySelectedPose()
        {
            EnsureTargetCamera();
            if (!TargetCamera)
            {
                m_status = "No target camera assigned.";
                return false;
            }

            if (!CanApplyToTarget(out var targetError))
            {
                m_status = targetError;
                Debug.LogWarning(m_status, this);
                return false;
            }

            if (m_poses == null || m_poses.Length == 0)
            {
                m_status = "No loaded poses to apply.";
                return false;
            }

            SelectedPoseIndex = Mathf.Clamp(SelectedPoseIndex, 0, m_poses.Length - 1);
            return ApplyPose(m_poses[SelectedPoseIndex]);
        }

        public bool PreviousPose(bool applyAfterSelection)
        {
            if (m_poses == null || m_poses.Length == 0)
                return false;
            SelectedPoseIndex = (SelectedPoseIndex + m_poses.Length - 1) % m_poses.Length;
            return !applyAfterSelection || ApplySelectedPose();
        }

        public bool NextPose(bool applyAfterSelection)
        {
            if (m_poses == null || m_poses.Length == 0)
                return false;
            SelectedPoseIndex = (SelectedPoseIndex + 1) % m_poses.Length;
            return !applyAfterSelection || ApplySelectedPose();
        }

        public string GetPoseLabel(int index)
        {
            if (m_poses == null || index < 0 || index >= m_poses.Length)
                return "<none>";
            var pose = m_poses[index];
            string name = string.IsNullOrEmpty(pose.Filename) ? $"pose {pose.PoseKey}" : pose.Filename;
            return $"{name} (pose {pose.PoseKey})";
        }

        bool ApplyPose(TrainingPoseInfo pose)
        {
            var cameraTransform = TargetCamera.transform;
            Vector3 position = ApplyAxisCorrection(pose.Center);
            Vector3 down = ApplyAxisCorrection(pose.Down);
            Vector3 forward = ApplyAxisCorrection(pose.Forward);

            if (forward.sqrMagnitude < 1e-8f || down.sqrMagnitude < 1e-8f)
            {
                m_status = $"Pose {pose.PoseKey} has invalid camera axes.";
                return false;
            }

            Transform coordinateRoot = GetCoordinateRoot();
            if (coordinateRoot)
            {
                // TransformPoint/TransformDirection use the root's current world transform every time a pose is
                // applied. This keeps OpenMVG pose coordinates attached to a GSplat object moved at runtime.
                position = coordinateRoot.TransformPoint(position);
                forward = coordinateRoot.TransformDirection(forward);
                down = coordinateRoot.TransformDirection(down);
            }

            if (forward.sqrMagnitude < 1e-8f || down.sqrMagnitude < 1e-8f)
            {
                m_status = $"Pose {pose.PoseKey} became invalid after applying the GSplat coordinate root.";
                return false;
            }

            var rotation = Quaternion.LookRotation(forward.normalized, (-down).normalized);

            bool teleportedLocomotionRoot = LocomotionRoot;
            if (!ApplyTrainingPoseToTarget(position, rotation))
                return false;

            // An XR runtime owns the center-eye projection. Keep its FOV untouched when teleporting a VR rig.
            if (ApplyCameraFov && !teleportedLocomotionRoot && pose.VerticalFovDegrees > 0.0f)
                TargetCamera.fieldOfView = pose.VerticalFovDegrees;

            string pvgStatus = ApplyPvgTimestamp(pose);
            string frameStatus = ApplyGroundTruthTrainingFrame(pose, cameraTransform.position, cameraTransform.rotation);
            string motionStatus = teleportedLocomotionRoot
                ? $"Teleported {LocomotionRoot.name}"
                : "Applied legacy camera pose";
            string coordinateStatus = coordinateRoot ? $" using {coordinateRoot.name}" : string.Empty;

            m_status = $"{motionStatus}{coordinateStatus}: {GetPoseLabel(SelectedPoseIndex)}{pvgStatus}{frameStatus}.";
            return true;
        }

        bool ApplyTrainingPoseToTarget(Vector3 requestedPosition, Quaternion requestedRotation)
        {
            var cameraTransform = TargetCamera.transform;
            Vector3 targetPosition = ApplyPosition ? requestedPosition : cameraTransform.position;
            Quaternion targetRotation = ApplyRotation ? requestedRotation : cameraTransform.rotation;

            if (!LocomotionRoot)
            {
                // Retain non-XR use of the package, but never overwrite an XR center-eye transform.
                if (XRSettings.isDeviceActive)
                {
                    m_status = "XR is active. Assign LocomotionRoot to the LocomotionMotion parent of OVRCameraRig; CenterEye will not be moved directly.";
                    Debug.LogWarning(m_status, this);
                    return false;
                }

                cameraTransform.SetPositionAndRotation(targetPosition, targetRotation);
                return true;
            }

            // Apply a single world-space rigid delta to the locomotion root. This accounts for the current
            // tracked head offset under OVRCameraRig, so CenterEye reaches the requested OpenMVG pose exactly.
            Vector3 currentHeadPosition = cameraTransform.position;
            Quaternion currentHeadRotation = cameraTransform.rotation;
            Quaternion deltaRotation = targetRotation * Quaternion.Inverse(currentHeadRotation);
            Vector3 rootToHead = currentHeadPosition - LocomotionRoot.position;
            Quaternion newRootRotation = deltaRotation * LocomotionRoot.rotation;
            Vector3 newRootPosition = targetPosition - deltaRotation * rootToHead;
            LocomotionRoot.SetPositionAndRotation(newRootPosition, newRootRotation);
            return true;
        }

        bool CanApplyToTarget(out string error)
        {
            if (LocomotionRoot && !TargetCamera.transform.IsChildOf(LocomotionRoot))
            {
                error = "LocomotionRoot must be a parent of TargetCamera. Assign the LocomotionMotion parent of OVRCameraRig, not CenterEye itself.";
                return false;
            }

            if (!LocomotionRoot && XRSettings.isDeviceActive)
            {
                error = "XR is active but LocomotionRoot is not assigned. Assign the LocomotionMotion parent of OVRCameraRig; CenterEye will not be moved directly.";
                return false;
            }

            error = null;
            return true;
        }

        Transform GetCoordinateRoot()
        {
            if (SceneRoot)
                return SceneRoot;

            EnsureTargetRenderer();
            return TargetRenderer ? TargetRenderer.transform : null;
        }

        string ApplyGroundTruthTrainingFrame(TrainingPoseInfo pose, Vector3 cameraPosition, Quaternion cameraRotation)
        {
            if (!ApplyGroundTruthFrame)
                return string.Empty;

            EnsureGroundTruthFrameDisplayer();
            if (!GroundTruthFrameDisplayer)
                return string.Empty;

            // The backdrop must follow the same camera that receives the selected OpenMVG pose.
            GroundTruthFrameDisplayer.TargetCamera = TargetCamera;
            if (GroundTruthFrameDisplayer.ApplyTrainingFrame(pose.Filename, cameraPosition, cameraRotation))
                return $" (ground truth {pose.Filename})";
            return $" (ground truth unavailable: {GroundTruthFrameDisplayer.Status})";
        }

        string ApplyPvgTimestamp(TrainingPoseInfo pose)
        {
            if (!ApplyPvgTime)
                return string.Empty;

            EnsureTargetRenderer();
            if (!TargetRenderer)
                return " (PVG renderer not assigned)";

            int framesLength = GetFramesLength();
            if (framesLength < 2)
                return " (PVG needs at least two frames)";

            float timeStart = PvgTimeStart;
            float timeEnd = PvgTimeEnd;
            if (CalculateTimeDurationFromFrameInterval)
            {
                float halfDuration = FrameInterval * (framesLength - 1) * 0.5f;
                timeStart = -halfDuration;
                timeEnd = halfDuration;
            }

            float normalizedKey = (float)pose.CamKey / (framesLength - 1);
            TargetRenderer.PvgTime = timeStart + (timeEnd - timeStart) * normalizedKey;
            TargetRenderer.ForceRefresh();
            return $" (PVG {TargetRenderer.PvgTime:0.######}, cam_key {pose.CamKey}/{framesLength - 1}, range {timeStart:0.######}..{timeEnd:0.######})";
        }

        int GetFramesLength()
        {
            if (FramesLength > 0)
                return FramesLength;
            if (m_loadedFrameCount > 0)
                return m_loadedFrameCount;
            return m_poses?.Length ?? 0;
        }

        TrainingPoseInfo BuildPoseInfo(ViewRecord view, ExtrinsicRecord extrinsic)
        {
            var r = extrinsic.Rotation;
            Vector3 right = new((float)r[0, 0], (float)r[0, 1], (float)r[0, 2]);
            Vector3 down = new((float)r[1, 0], (float)r[1, 1], (float)r[1, 2]);
            Vector3 forward = new((float)r[2, 0], (float)r[2, 1], (float)r[2, 2]);

            const float horizontalFovDegrees = 90.0f;
            float verticalFovDegrees = horizontalFovDegrees;
            if (view.Width > 0 && view.Height > 0)
            {
                float tanHalfHorizontal = Mathf.Tan(horizontalFovDegrees * 0.5f * Mathf.Deg2Rad);
                verticalFovDegrees = 2.0f * Mathf.Atan(tanHalfHorizontal * view.Height / view.Width) * Mathf.Rad2Deg;
            }

            return new TrainingPoseInfo
            {
                CamKey = UseViewKeyAsCamKey ? view.CamKey : extrinsic.PoseKey,
                PoseKey = extrinsic.PoseKey,
                ViewKey = view.ViewKey,
                Filename = view.Filename,
                Width = view.Width,
                Height = view.Height,
                Center = extrinsic.Center,
                Right = right,
                Down = down,
                Forward = forward,
                HorizontalFovDegrees = horizontalFovDegrees,
                VerticalFovDegrees = verticalFovDegrees
            };
        }

        Vector3 ApplyAxisCorrection(Vector3 value)
        {
            return new Vector3(
                FlipX ? -value.x : value.x,
                FlipY ? -value.y : value.y,
                FlipZ ? -value.z : value.z);
        }

        void EnsureTargetCamera()
        {
            if (TargetCamera)
                return;
            TargetCamera = GetComponent<Camera>();
            if (!TargetCamera)
                TargetCamera = Camera.main;
        }

        void EnsureTargetRenderer()
        {
            if (!TargetRenderer)
                TargetRenderer = GetComponent<GsplatRenderer>();
        }

        void EnsureGroundTruthFrameDisplayer()
        {
            if (!GroundTruthFrameDisplayer)
                GroundTruthFrameDisplayer = GetComponent<GsplatGroundTruthFrameDisplayer>();
        }

        static string ResolvePath(string path)
        {
            if (string.IsNullOrWhiteSpace(path))
                return path;
            if (Path.IsPathRooted(path))
                return path;
            string projectRoot = Path.GetFullPath(Path.Combine(Application.dataPath, ".."));
            return Path.GetFullPath(Path.Combine(projectRoot, path));
        }

        static List<ViewRecord> LoadViews(string path)
        {
            var root = Json.AsObject(Json.Parse(File.ReadAllText(path)));
            var views = Json.AsList(Json.Get(root, "views"));
            var records = new List<ViewRecord>();

            foreach (var viewValue in views)
            {
                var view = Json.AsObject(viewValue);
                int viewKey = Json.ToInt(Json.Get(view, "key"));
                var value = Json.AsObject(Json.Get(view, "value"));
                var ptrWrapper = Json.AsObject(Json.Get(value, "ptr_wrapper"));
                var data = Json.AsObject(Json.Get(ptrWrapper, "data"));

                records.Add(new ViewRecord
                {
                    CamKey = viewKey,
                    ViewKey = viewKey,
                    PoseKey = data.TryGetValue("id_pose", out var idPose) ? Json.ToInt(idPose) : viewKey,
                    Filename = Json.ToString(Json.Get(data, "filename")),
                    Width = data.TryGetValue("width", out var width) ? Json.ToInt(width) : 0,
                    Height = data.TryGetValue("height", out var height) ? Json.ToInt(height) : 0
                });
            }

            return records;
        }

        static Dictionary<int, ExtrinsicRecord> LoadExtrinsics(string path)
        {
            var root = Json.AsObject(Json.Parse(File.ReadAllText(path)));
            var extrinsics = Json.AsList(Json.Get(root, "extrinsics"));
            var records = new Dictionary<int, ExtrinsicRecord>();

            foreach (var extrinsicValue in extrinsics)
            {
                var extrinsic = Json.AsObject(extrinsicValue);
                int key = Json.ToInt(Json.Get(extrinsic, "key"));
                var value = Json.AsObject(Json.Get(extrinsic, "value"));
                var rotation = ReadRotation(Json.AsList(Json.Get(value, "rotation")));
                var center = ReadVector3(Json.AsList(Json.Get(value, "center")));

                records[key] = new ExtrinsicRecord
                {
                    PoseKey = key,
                    Rotation = rotation,
                    Center = center
                };
            }

            return records;
        }

        static double[,] ReadRotation(List<object> rows)
        {
            if (rows.Count != 3)
                throw new FormatException("rotation must contain 3 rows.");

            var rotation = new double[3, 3];
            for (int y = 0; y < 3; y++)
            {
                var row = Json.AsList(rows[y]);
                if (row.Count != 3)
                    throw new FormatException("each rotation row must contain 3 values.");
                for (int x = 0; x < 3; x++)
                    rotation[y, x] = Json.ToDouble(row[x]);
            }

            return rotation;
        }

        static Vector3 ReadVector3(List<object> values)
        {
            if (values.Count != 3)
                throw new FormatException("center must contain 3 values.");
            return new Vector3((float)Json.ToDouble(values[0]), (float)Json.ToDouble(values[1]),
                (float)Json.ToDouble(values[2]));
        }

        static class Json
        {
            public static object Parse(string json)
            {
                using var parser = new Parser(json);
                return parser.ParseValue();
            }

            public static object Get(Dictionary<string, object> obj, string key)
            {
                if (!obj.TryGetValue(key, out var value))
                    throw new KeyNotFoundException($"Missing JSON property '{key}'.");
                return value;
            }

            public static Dictionary<string, object> AsObject(object value)
            {
                return value as Dictionary<string, object> ??
                       throw new FormatException("Expected JSON object.");
            }

            public static List<object> AsList(object value)
            {
                return value as List<object> ?? throw new FormatException("Expected JSON array.");
            }

            public static int ToInt(object value)
            {
                return Convert.ToInt32(ToDouble(value));
            }

            public static double ToDouble(object value)
            {
                return value switch
                {
                    double d => d,
                    long l => l,
                    int i => i,
                    float f => f,
                    string s => double.Parse(s, CultureInfo.InvariantCulture),
                    _ => throw new FormatException("Expected JSON number.")
                };
            }

            public static string ToString(object value)
            {
                return value as string ?? string.Empty;
            }

            sealed class Parser : IDisposable
            {
                readonly string m_json;
                int m_index;

                public Parser(string json)
                {
                    m_json = json ?? string.Empty;
                }

                public void Dispose()
                {
                }

                public object ParseValue()
                {
                    EatWhitespace();
                    if (m_index >= m_json.Length)
                        throw new FormatException("Unexpected end of JSON.");

                    char c = PeekChar;
                    switch (c)
                    {
                        case '{':
                            return ParseObject();
                        case '[':
                            return ParseArray();
                        case '"':
                            return ParseString();
                        case '-':
                            return ParseNumber();
                        case 't':
                            return ParseLiteral("true", true);
                        case 'f':
                            return ParseLiteral("false", false);
                        case 'n':
                            return ParseLiteral("null", null);
                        default:
                            if (c >= '0' && c <= '9')
                                return ParseNumber();
                            throw new FormatException($"Unexpected JSON token '{c}'.");
                    }
                }

                Dictionary<string, object> ParseObject()
                {
                    var obj = new Dictionary<string, object>();
                    NextChar();
                    while (true)
                    {
                        EatWhitespace();
                        if (PeekChar == '}')
                        {
                            NextChar();
                            return obj;
                        }

                        string key = ParseString();
                        EatWhitespace();
                        if (NextChar() != ':')
                            throw new FormatException("Expected ':' after object key.");
                        obj[key] = ParseValue();
                        EatWhitespace();

                        char c = NextChar();
                        if (c == '}')
                            return obj;
                        if (c != ',')
                            throw new FormatException("Expected ',' or '}' in object.");
                    }
                }

                List<object> ParseArray()
                {
                    var array = new List<object>();
                    NextChar();
                    while (true)
                    {
                        EatWhitespace();
                        if (PeekChar == ']')
                        {
                            NextChar();
                            return array;
                        }

                        array.Add(ParseValue());
                        EatWhitespace();

                        char c = NextChar();
                        if (c == ']')
                            return array;
                        if (c != ',')
                            throw new FormatException("Expected ',' or ']' in array.");
                    }
                }

                string ParseString()
                {
                    if (NextChar() != '"')
                        throw new FormatException("Expected string.");

                    var builder = new StringBuilder();
                    while (m_index < m_json.Length)
                    {
                        char c = NextChar();
                        if (c == '"')
                            return builder.ToString();
                        if (c != '\\')
                        {
                            builder.Append(c);
                            continue;
                        }

                        if (m_index >= m_json.Length)
                            throw new FormatException("Unterminated string escape.");
                        c = NextChar();
                        switch (c)
                        {
                            case '"':
                            case '\\':
                            case '/':
                                builder.Append(c);
                                break;
                            case 'b':
                                builder.Append('\b');
                                break;
                            case 'f':
                                builder.Append('\f');
                                break;
                            case 'n':
                                builder.Append('\n');
                                break;
                            case 'r':
                                builder.Append('\r');
                                break;
                            case 't':
                                builder.Append('\t');
                                break;
                            case 'u':
                                builder.Append(ParseUnicodeEscape());
                                break;
                            default:
                                throw new FormatException($"Unsupported string escape '\\{c}'.");
                        }
                    }

                    throw new FormatException("Unterminated string.");
                }

                char ParseUnicodeEscape()
                {
                    if (m_index + 4 > m_json.Length)
                        throw new FormatException("Invalid unicode escape.");
                    string hex = m_json.Substring(m_index, 4);
                    m_index += 4;
                    return (char)int.Parse(hex, NumberStyles.HexNumber, CultureInfo.InvariantCulture);
                }

                object ParseNumber()
                {
                    int start = m_index;
                    if (PeekChar == '-')
                        m_index++;
                    while (m_index < m_json.Length && char.IsDigit(PeekChar))
                        m_index++;
                    bool isFloat = false;
                    if (m_index < m_json.Length && PeekChar == '.')
                    {
                        isFloat = true;
                        m_index++;
                        while (m_index < m_json.Length && char.IsDigit(PeekChar))
                            m_index++;
                    }

                    if (m_index < m_json.Length && (PeekChar == 'e' || PeekChar == 'E'))
                    {
                        isFloat = true;
                        m_index++;
                        if (m_index < m_json.Length && (PeekChar == '+' || PeekChar == '-'))
                            m_index++;
                        while (m_index < m_json.Length && char.IsDigit(PeekChar))
                            m_index++;
                    }

                    string number = m_json.Substring(start, m_index - start);
                    if (isFloat)
                        return double.Parse(number, CultureInfo.InvariantCulture);
                    return long.Parse(number, CultureInfo.InvariantCulture);
                }

                object ParseLiteral(string literal, object value)
                {
                    if (m_index + literal.Length > m_json.Length ||
                        string.CompareOrdinal(m_json, m_index, literal, 0, literal.Length) != 0)
                        throw new FormatException($"Expected '{literal}'.");
                    m_index += literal.Length;
                    return value;
                }

                void EatWhitespace()
                {
                    while (m_index < m_json.Length && char.IsWhiteSpace(m_json[m_index]))
                        m_index++;
                }

                char PeekChar => m_index < m_json.Length ? m_json[m_index] : '\0';

                char NextChar()
                {
                    if (m_index >= m_json.Length)
                        throw new FormatException("Unexpected end of JSON.");
                    return m_json[m_index++];
                }
            }
        }
    }
}
