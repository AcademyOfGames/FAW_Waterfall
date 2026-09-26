using System;
using System.Collections.Generic;
using System.Text;
using Google.XR.ARCoreExtensions;
using Google.XR.ARCoreExtensions.GeospatialCreator;
using UnityEngine;
#if UNITY_ANDROID
using UnityEngine.Android;
#endif
using UnityEngine.XR.ARFoundation;
using UnityEngine.XR.ARSubsystems;

/// <summary>
/// On-device diagnostics overlay for the Geospatial demo.
///
/// Draws a live snapshot of session / Earth / anchor state above a scrollable capture of
/// everything written to the Unity log, and copies the whole report to the system clipboard
/// on demand so it can be pasted into a bug report.
///
/// Uses IMGUI deliberately: it needs no Canvas, no font asset and no scene wiring, so it
/// still renders when the rest of the scene has failed to initialize.
/// </summary>
public class GeospatialDebugOverlay : MonoBehaviour
{
    [Header("References (found automatically if left empty)")]
    [SerializeField]
    private AREarthManager earthManager;

    [SerializeField]
    private ARCoreExtensions arcoreExtensions;

    [Header("Overlay")]
    [Tooltip("Draw the panel on screen. Log capture keeps running either way, so anything " +
             "recorded while hidden is still there when it is shown again.")]
    [SerializeField]
    private bool showOverlay = true;

    [Tooltip("Maximum number of captured log lines held in memory.")]
    [SerializeField]
    private int maxLines = 600;

    [Tooltip("How often the status block at the top is rebuilt, in seconds.")]
    [SerializeField]
    private float statusRefreshSeconds = 0.25f;

    [Tooltip("Logical width the overlay is laid out against. Lower means larger text.")]
    [SerializeField]
    private float referenceWidth = 640f;

    [Tooltip("Multiplier applied to every font size in the overlay. 1 is the original size.")]
    [SerializeField]
    private float textScale = 2f;

    [Tooltip("Add the on-screen arrow that points at the geospatial anchors.")]
    [SerializeField]
    private bool showTargetPointer = true;

    private readonly List<string> _lines = new List<string>();
    private readonly StringBuilder _builder = new StringBuilder();

    private ARGeospatialCreatorAnchor[] _anchors = Array.Empty<ARGeospatialCreatorAnchor>();
    private Camera _camera;
    private string _status = "(collecting…)";
    private Vector2 _scroll;
    private bool _autoScroll = true;
    private float _nextStatusTime;
    private float _nextAnchorScanTime;
    private float _copyToastUntil;

    // Previous values, so transitions can be written into the log as a timeline.
    private string _lastTransitionKey = string.Empty;

    // Consecutive identical messages are collapsed into one line with a counter, so a chatty
    // per-frame warning cannot push everything useful out of the buffer.
    private string _lastCondition = string.Empty;
    private int _repeatCount;

    private GUIStyle _statusStyle;
    private GUIStyle _logStyle;
    private GUIStyle _buttonStyle;
    private float _stylesBuiltForScale;

    // Accuracy history, so the report shows how the localization converged rather than only
    // whatever it happens to read at the instant the report is copied.
    private float _earthTrackingSinceTime = -1f;
    private double _bestHorizontalAccuracy = double.MaxValue;
    private double _worstHorizontalAccuracy;
    private double _bestYawAccuracy = double.MaxValue;
    private int _poseSampleCount;
    private double _poseLatitudeSum;
    private double _poseLongitudeSum;
    private double _lastPoseLatitude;
    private double _lastPoseLongitude;
    private double _poseJitterMetres;

    /// <summary>
    /// Whether the panel is drawn. Settable at runtime so other scripts can hide the overlay
    /// for a screenshot or a demo without losing the captured log.
    /// </summary>
    public bool ShowOverlay
    {
        get => showOverlay;
        set => showOverlay = value;
    }

    private void Awake()
    {
        if (earthManager == null)
        {
            earthManager = FindAnyObjectByType<AREarthManager>();
        }

        if (arcoreExtensions == null)
        {
            arcoreExtensions = FindAnyObjectByType<ARCoreExtensions>();
        }

        _camera = Camera.main != null ? Camera.main : FindAnyObjectByType<Camera>();

        // Attached in code rather than in the scene so the pointer needs no wiring and cannot
        // be lost when the scene is re-saved from an older copy.
        if (showTargetPointer && FindAnyObjectByType<GeospatialTargetPointer>() == null)
        {
            gameObject.AddComponent<GeospatialTargetPointer>();
        }

        Append($"Overlay started. Unity {Application.unityVersion}, {Application.platform}, " +
               $"devBuild={Debug.isDebugBuild}");
    }

    private void OnEnable()
    {
        Application.logMessageReceived += HandleLog;
    }

    private void OnDisable()
    {
        Application.logMessageReceived -= HandleLog;
    }

    private void Update()
    {
        if (Time.realtimeSinceStartup >= _nextAnchorScanTime)
        {
            _nextAnchorScanTime = Time.realtimeSinceStartup + 2f;
            _anchors = FindObjectsByType<ARGeospatialCreatorAnchor>(
                FindObjectsInactive.Include, FindObjectsSortMode.None);
        }

        if (Time.realtimeSinceStartup >= _nextStatusTime)
        {
            _nextStatusTime = Time.realtimeSinceStartup + statusRefreshSeconds;
            RebuildStatus();
            LogTransitions();
        }
    }

    /// <summary>Adds a line to the buffer, trimming the oldest entries past the cap.</summary>
    private void Append(string line)
    {
        _lines.Add(line);
        if (_lines.Count > maxLines)
        {
            _lines.RemoveRange(0, _lines.Count - maxLines);
        }

        if (_autoScroll)
        {
            _scroll.y = float.MaxValue;
        }
    }

    private void HandleLog(string condition, string stackTrace, LogType type)
    {
        string tag;
        switch (type)
        {
            case LogType.Error:
                tag = "ERROR";
                break;
            case LogType.Exception:
                tag = "EXCEPTION";
                break;
            case LogType.Warning:
                tag = "WARN";
                break;
            case LogType.Assert:
                tag = "ASSERT";
                break;
            default:
                tag = "LOG";
                break;
        }

        if (condition == _lastCondition && _lines.Count > 0)
        {
            _repeatCount++;
            _lines[_lines.Count - 1] =
                $"[{Time.realtimeSinceStartup,7:F1}] {tag}: {condition}  (x{_repeatCount})";
            return;
        }

        _lastCondition = condition;
        _repeatCount = 1;
        Append($"[{Time.realtimeSinceStartup,7:F1}] {tag}: {condition}");

        // Only errors carry a stack trace worth keeping, and only the top few frames matter.
        if (type == LogType.Exception || type == LogType.Error)
        {
            string[] frames = stackTrace.Split('\n');
            for (int i = 0; i < frames.Length && i < 4; i++)
            {
                if (!string.IsNullOrWhiteSpace(frames[i]))
                {
                    Append($"                  at {frames[i].Trim()}");
                }
            }
        }
    }

    /// <summary>
    /// Writes a log entry whenever one of the interesting state values changes, so the copied
    /// report contains a timeline rather than just a final snapshot.
    /// </summary>
    private void LogTransitions()
    {
        string geospatialMode = "n/a";
        if (arcoreExtensions != null && arcoreExtensions.ARCoreExtensionsConfig != null)
        {
            geospatialMode = arcoreExtensions.ARCoreExtensionsConfig.GeospatialMode.ToString();
        }

        string earthState = "n/a";
        string earthTracking = "n/a";
        if (earthManager != null)
        {
            earthState = SafeEarthState();
            earthTracking = SafeEarthTracking();
        }

        string key = $"{ARSession.state}|{Input.location.status}|{geospatialMode}|" +
                     $"{earthState}|{earthTracking}";

        if (key == _lastTransitionKey)
        {
            return;
        }

        _lastTransitionKey = key;
        _lastCondition = string.Empty;
        Append($"[{Time.realtimeSinceStartup,7:F1}] STATE: session={ARSession.state} " +
               $"location={Input.location.status} geospatialMode={geospatialMode} " +
               $"earth={earthState} earthTracking={earthTracking}");
    }

    private string SafeEarthState()
    {
        try
        {
            return earthManager.EarthState.ToString();
        }
        catch (Exception e)
        {
            return $"<threw {e.GetType().Name}>";
        }
    }

    private string SafeEarthTracking()
    {
        try
        {
            return earthManager.EarthTrackingState.ToString();
        }
        catch (Exception e)
        {
            return $"<threw {e.GetType().Name}>";
        }
    }

    /// <summary>
    /// Folds one pose sample into the running accuracy history. Called once per status
    /// refresh, which is also the sampling rate for the jitter figure.
    /// </summary>
    private void TrackAccuracy(GeospatialPose pose)
    {
        if (_earthTrackingSinceTime < 0f)
        {
            _earthTrackingSinceTime = Time.realtimeSinceStartup;
        }

        _bestHorizontalAccuracy = Math.Min(_bestHorizontalAccuracy, pose.HorizontalAccuracy);
        _worstHorizontalAccuracy = Math.Max(_worstHorizontalAccuracy, pose.HorizontalAccuracy);
        _bestYawAccuracy = Math.Min(_bestYawAccuracy, pose.OrientationYawAccuracy);

        if (_poseSampleCount > 0)
        {
            _poseJitterMetres = DistanceMetres(
                _lastPoseLatitude, _lastPoseLongitude, pose.Latitude, pose.Longitude);
        }

        _lastPoseLatitude = pose.Latitude;
        _lastPoseLongitude = pose.Longitude;
        _poseLatitudeSum += pose.Latitude;
        _poseLongitudeSum += pose.Longitude;
        _poseSampleCount++;
    }

    /// <summary>
    /// Plain-language read on whether the pose is good enough to place content believably.
    /// The thresholds are the ones Google's own guidance uses for street-level AR.
    /// </summary>
    private static string AccuracyVerdict(GeospatialPose pose)
    {
        if (pose.HorizontalAccuracy <= 1.5d && pose.OrientationYawAccuracy <= 5d)
        {
            return "EXCELLENT — VPS localized, content should land within a metre or two";
        }

        if (pose.HorizontalAccuracy <= 5d && pose.OrientationYawAccuracy <= 15d)
        {
            return "OK — usable, expect a few metres of drift";
        }

        if (pose.OrientationYawAccuracy > 15d)
        {
            return "POOR HEADING — content will be rotated away from where you expect it; " +
                   "pan slowly across buildings to let VPS localize";
        }

        return "POOR — GPS-grade only, content may be tens of metres off";
    }

    private static string Compass16(double heading)
    {
        string[] points =
        {
            "N", "NNE", "NE", "ENE", "E", "ESE", "SE", "SSE",
            "S", "SSW", "SW", "WSW", "W", "WNW", "NW", "NNW",
        };
        int index = (int)Math.Round(heading / 22.5d) % 16;
        return points[(index + 16) % 16];
    }

    private void RebuildStatus()
    {
        _builder.Clear();
        _builder.AppendLine($"=== Geospatial diagnostics  {DateTime.Now:HH:mm:ss} ===");
        _builder.AppendLine($"Unity {Application.unityVersion} | {Application.platform} | " +
                            $"devBuild={Debug.isDebugBuild} | v{Application.version}");
        _builder.AppendLine(
            $"uptime={Time.realtimeSinceStartup:F0} s  " +
            $"fps={(Time.unscaledDeltaTime > 0f ? 1f / Time.unscaledDeltaTime : 0f):F0}  " +
            $"screen={Screen.width}x{Screen.height} @{Screen.dpi:F0}dpi");

#if UNITY_ANDROID
        if (!Application.isEditor)
        {
            _builder.AppendLine(
                $"Permissions: fineLocation={Permission.HasUserAuthorizedPermission(Permission.FineLocation)} " +
                $"camera={Permission.HasUserAuthorizedPermission(Permission.Camera)}");
        }
#endif

        _builder.AppendLine($"LocationService: enabledByUser={Input.location.isEnabledByUser} " +
                            $"status={Input.location.status}");
        if (Input.location.status == LocationServiceStatus.Running)
        {
            LocationInfo data = Input.location.lastData;
            _builder.AppendLine($"  device GPS: {data.latitude:F6}, {data.longitude:F6} " +
                                $"(±{data.horizontalAccuracy:F0} m)");
        }

        _builder.AppendLine($"ARSession: state={ARSession.state} " +
                            $"notTracking={ARSession.notTrackingReason}");

        if (arcoreExtensions == null)
        {
            _builder.AppendLine("ARCoreExtensions: *** NOT FOUND IN SCENE ***");
        }
        else if (arcoreExtensions.ARCoreExtensionsConfig == null)
        {
            _builder.AppendLine("ARCoreExtensionsConfig: *** NULL ***");
        }
        else
        {
            ARCoreExtensionsConfig config = arcoreExtensions.ARCoreExtensionsConfig;
            _builder.AppendLine($"Config: geospatialMode={config.GeospatialMode} " +
                                $"streetscapeGeometry={config.StreetscapeGeometryMode}");
        }

        bool havePose = false;
        double poseLatitude = 0;
        double poseLongitude = 0;
        double poseAltitude = 0;

        if (earthManager == null)
        {
            _builder.AppendLine("AREarthManager: *** NOT FOUND IN SCENE ***");
        }
        else
        {
            _builder.AppendLine($"Earth: state={SafeEarthState()} tracking={SafeEarthTracking()}");

            try
            {
                _builder.AppendLine(
                    $"  supported={earthManager.IsGeospatialModeSupported(GeospatialMode.Enabled)}");

                if (earthManager.EarthTrackingState == TrackingState.Tracking)
                {
                    GeospatialPose pose = earthManager.CameraGeospatialPose;
                    havePose = true;
                    poseLatitude = pose.Latitude;
                    poseLongitude = pose.Longitude;
                    poseAltitude = pose.Altitude;

                    TrackAccuracy(pose);

                    _builder.AppendLine($"  HERE: {pose.Latitude:F7}, {pose.Longitude:F7}");
                    _builder.AppendLine($"  alt={pose.Altitude:F1} m  hAcc={pose.HorizontalAccuracy:F1} m " +
                                        $"vAcc={pose.VerticalAccuracy:F1} m " +
                                        $"yawAcc={pose.OrientationYawAccuracy:F1}°");
                    _builder.AppendLine($"  quality: {AccuracyVerdict(pose)}");
                    _builder.AppendLine(
                        $"  hAcc best/worst={_bestHorizontalAccuracy:F1}/{_worstHorizontalAccuracy:F1} m  " +
                        $"yawAcc best={_bestYawAccuracy:F1}°  " +
                        $"tracking for {Time.realtimeSinceStartup - _earthTrackingSinceTime:F0} s");

                    // Jitter is what actually moves content between frames. A tight hAcc with
                    // metres of jitter means the pose is still being pulled around.
                    double meanDrift = _poseSampleCount > 1
                        ? DistanceMetres(
                            _poseLatitudeSum / _poseSampleCount,
                            _poseLongitudeSum / _poseSampleCount,
                            pose.Latitude, pose.Longitude)
                        : 0d;
                    _builder.AppendLine(
                        $"  pose jitter (last sample)={_poseJitterMetres:F2} m  " +
                        $"offset from session mean={meanDrift:F2} m  samples={_poseSampleCount}");

                    // The heading is what decides whether content lands in front of you or
                    // behind you; a bad yaw is the usual reason for "it is not where I put it".
                    Vector3 eunForward = pose.EunRotation * Vector3.forward;
                    double heading =
                        (Math.Atan2(eunForward.x, eunForward.z) * 180d / Math.PI + 360d) % 360d;
                    _builder.AppendLine(
                        $"  heading={heading:F0}° ({Compass16(heading)})  " +
                        $"eun=({eunForward.x:F2}, {eunForward.y:F2}, {eunForward.z:F2})");

                    if (Input.location.status == LocationServiceStatus.Running)
                    {
                        LocationInfo gps = Input.location.lastData;
                        double gpsDelta = DistanceMetres(
                            pose.Latitude, pose.Longitude, gps.latitude, gps.longitude);

                        // A large gap here is normal and healthy: it means VPS has corrected a
                        // coarse GPS fix. A gap near zero with a poor hAcc means VPS never
                        // localized and the pose is just the GPS.
                        _builder.AppendLine(
                            $"  Earth vs device GPS: {gpsDelta:F1} m apart  " +
                            $"altDelta={pose.Altitude - gps.altitude:F1} m  " +
                            $"({(gpsDelta > 3d ? "VPS is correcting GPS" : "no meaningful VPS correction")})");
                    }
                }
                else
                {
                    _builder.AppendLine("  (no geospatial pose yet — Earth is not tracking)");
                }
            }
            catch (Exception e)
            {
                _builder.AppendLine($"  <error reading earth state: {e.GetType().Name}: {e.Message}>");
            }
        }

        _builder.AppendLine($"Geospatial Creator anchors: {_anchors.Length}");
        for (int i = 0; i < _anchors.Length; i++)
        {
            ARGeospatialCreatorAnchor anchor = _anchors[i];
            if (anchor == null)
            {
                continue;
            }

            string state = anchor.isActiveAndEnabled ? "enabled" : "DISABLED";
            _builder.Append($"  [{i}] {anchor.name} ({state}) " +
                            $"{anchor.Latitude:F6}, {anchor.Longitude:F6} " +
                            $"alt={anchor.Altitude:F1} ({anchor.AltitudeType})");

            if (havePose)
            {
                double metres = DistanceMetres(
                    poseLatitude, poseLongitude, anchor.Latitude, anchor.Longitude);
                _builder.Append(metres >= 1000d
                    ? $"  -> {metres / 1000d:F1} km away"
                    : $"  -> {metres:F0} m away");

                double bearing = BearingDegrees(
                    poseLatitude, poseLongitude, anchor.Latitude, anchor.Longitude);
                _builder.AppendLine();
                _builder.AppendLine(
                    $"      geodetic: bearing={bearing:F0}° ({Compass16(bearing)})  " +
                    $"altVsCamera={anchor.Altitude - poseAltitude:F1} m");

                AppendAltitudeSanityCheck(anchor, poseAltitude);
            }
            else
            {
                _builder.AppendLine();
            }

            AppendContentInventory(anchor);
        }

        _status = _builder.ToString();
    }

    /// <summary>
    /// Reports where an anchor actually landed in world space and what is hanging off it.
    /// This is the half of the picture the geospatial state cannot show: an anchor can resolve
    /// perfectly and still draw nothing if its content is inactive, culled, scaled to zero, or
    /// using a shader that did not survive the build.
    /// </summary>
    private void AppendContentInventory(ARGeospatialCreatorAnchor anchor)
    {
        Transform anchorTransform = anchor.transform;
        Vector3 world = anchorTransform.position;
        bool degenerate =
            float.IsNaN(world.x) || float.IsNaN(world.y) || float.IsNaN(world.z) ||
            float.IsInfinity(world.x) || float.IsInfinity(world.y) || float.IsInfinity(world.z);

        _builder.Append($"      world=({world.x:F2}, {world.y:F2}, {world.z:F2})");
        if (degenerate)
        {
            _builder.Append("  *** NaN/Inf POSITION ***");
        }

        if (_camera != null)
        {
            _builder.Append(
                $"  camDist={Vector3.Distance(_camera.transform.position, world):F1} m");
        }

        _builder.AppendLine($"  children={anchorTransform.childCount}" +
                            $"  scale={anchorTransform.lossyScale.x:F2}");

        // Whether the runtime anchor the creator anchor was re-parented under is still being
        // tracked. An untracked anchor keeps its last pose and quietly stops following reality.
        ARAnchor runtimeAnchor = anchorTransform.parent != null
            ? anchorTransform.parent.GetComponentInParent<ARAnchor>()
            : null;
        _builder.AppendLine(runtimeAnchor == null
            ? "      runtimeAnchor: *** NOT RESOLVED YET (still a loose scene object) ***"
            : $"      runtimeAnchor={runtimeAnchor.GetType().Name} " +
              $"tracking={runtimeAnchor.trackingState} id={runtimeAnchor.trackableId}");

        AppendCameraRelativeView(world);

        Renderer[] renderers = anchor.GetComponentsInChildren<Renderer>(true);
        if (renderers.Length == 0)
        {
            _builder.AppendLine("      renderers: *** NONE UNDER THIS ANCHOR ***");
            return;
        }

        int active = 0;
        int visible = 0;
        Bounds bounds = new Bounds(world, Vector3.zero);
        HashSet<string> shaders = new HashSet<string>();
        HashSet<int> layers = new HashSet<int>();

        for (int i = 0; i < renderers.Length; i++)
        {
            Renderer renderer = renderers[i];
            if (renderer.enabled && renderer.gameObject.activeInHierarchy)
            {
                active++;
                bounds.Encapsulate(renderer.bounds);
            }

            // True only if the renderer was drawn by some camera last frame, which separates
            // "culled or mispositioned" from "drawn but invisible to the eye".
            if (renderer.isVisible)
            {
                visible++;
            }

            layers.Add(renderer.gameObject.layer);

            Material material = renderer.sharedMaterial;
            if (material == null)
            {
                shaders.Add("<no material>");
            }
            else
            {
                shaders.Add(material.shader == null ? "<no shader>" : material.shader.name);
            }
        }

        _builder.AppendLine($"      renderers={renderers.Length} active={active} " +
                            $"visibleToCamera={visible}");
        _builder.AppendLine($"      bounds c=({bounds.center.x:F1}, {bounds.center.y:F1}, " +
                            $"{bounds.center.z:F1}) size=({bounds.size.x:F1}, " +
                            $"{bounds.size.y:F1}, {bounds.size.z:F1})");
        _builder.AppendLine($"      layers=[{string.Join(",", layers)}]");

        if (_camera != null)
        {
            foreach (int layer in layers)
            {
                if ((_camera.cullingMask & (1 << layer)) == 0)
                {
                    _builder.AppendLine(
                        $"      *** layer {layer} ({LayerMask.LayerToName(layer)}) is NOT in the " +
                        "camera culling mask — those renderers can never be drawn ***");
                }
            }
        }
        _builder.AppendLine($"      shaders=[{string.Join(" | ", shaders)}]");

        ParticleSystem[] particleSystems = anchor.GetComponentsInChildren<ParticleSystem>(true);
        if (particleSystems.Length > 0)
        {
            int playing = 0;
            int alive = 0;
            for (int i = 0; i < particleSystems.Length; i++)
            {
                if (particleSystems[i].isPlaying)
                {
                    playing++;
                }

                alive += particleSystems[i].particleCount;
            }

            _builder.AppendLine($"      particleSystems={particleSystems.Length} " +
                                $"playing={playing} liveParticles={alive}");
        }
    }

    /// <summary>
    /// Where the anchor sits relative to where the phone is actually pointed, in the same
    /// terms the on-screen pointer uses: turn this far, look this far up, and it should be
    /// dead centre. This is the line that separates "it is not there" from "it is there and I
    /// am not looking at it".
    /// </summary>
    private void AppendCameraRelativeView(Vector3 world)
    {
        if (_camera == null)
        {
            _builder.AppendLine("      view: *** NO CAMERA FOUND ***");
            return;
        }

        Transform cameraTransform = _camera.transform;
        Vector3 delta = world - cameraTransform.position;
        Vector3 flat = new Vector3(delta.x, 0f, delta.z);
        Vector3 flatForward = new Vector3(cameraTransform.forward.x, 0f, cameraTransform.forward.z);

        float yaw = flat.sqrMagnitude < 1e-6f || flatForward.sqrMagnitude < 1e-6f
            ? 0f
            : Vector3.SignedAngle(flatForward.normalized, flat.normalized, Vector3.up);
        float elevation = delta.sqrMagnitude < 1e-8f
            ? 0f
            : Mathf.Asin(Mathf.Clamp(delta.y / delta.magnitude, -1f, 1f)) * Mathf.Rad2Deg;

        Vector3 screen = _camera.WorldToScreenPoint(world);
        bool behind = screen.z <= 0f;
        bool onScreen = !behind &&
                        screen.x >= 0f && screen.x <= Screen.width &&
                        screen.y >= 0f && screen.y <= Screen.height;

        string where = behind ? "BEHIND CAMERA" : onScreen ? "on screen" : "off screen";

        _builder.AppendLine(
            $"      view: {where}  turn={(yaw >= 0f ? "right" : "left")} {Mathf.Abs(yaw):F0}°  " +
            $"look {(elevation >= 0f ? "up" : "down")} {Mathf.Abs(elevation):F0}°");
        _builder.AppendLine(
            $"      screen=({screen.x:F0}, {screen.y:F0}, z={screen.z:F1}) " +
            $"of {Screen.width}x{Screen.height}  " +
            $"clip near/far={_camera.nearClipPlane:F2}/{_camera.farClipPlane:F0} m" +
            (delta.magnitude > _camera.farClipPlane
                ? "  *** BEYOND FAR CLIP — increase the camera far plane ***"
                : string.Empty));

        // Culling mask is the other silent killer: content on a layer the camera does not
        // render is present, correct and completely invisible.
        _builder.AppendLine($"      camera cullingMask=0x{_camera.cullingMask:X}");
    }

    /// <summary>
    /// Flags the single most common reason geospatial content is invisible: a Terrain or
    /// Rooftop anchor whose Altitude field still holds an absolute WGS84 elevation. Those two
    /// anchor types treat Altitude as an offset *from* the terrain or roof, so an absolute
    /// value launches the content that many metres into the sky, where nothing will ever walk
    /// into frame.
    /// </summary>
    private void AppendAltitudeSanityCheck(ARGeospatialCreatorAnchor anchor, double cameraAltitude)
    {
        if (anchor.AltitudeType == AnchorAltitudeType.WGS84)
        {
            return;
        }

        double offset = anchor.Altitude;
        if (Math.Abs(offset) <= 30d)
        {
            return;
        }

        // If the "offset" is within a stone's throw of the real ground elevation here, it is
        // almost certainly an absolute altitude that was never converted.
        bool looksAbsolute = Math.Abs(offset - cameraAltitude) < 30d;

        _builder.AppendLine(
            $"      *** {anchor.AltitudeType} anchor Altitude={offset:F1} is an OFFSET from " +
            $"{anchor.AltitudeType.ToString().ToLowerInvariant()}, not an absolute elevation ***");
        _builder.AppendLine(
            $"      *** content is being placed ~{offset:F0} m above the ground" +
            (looksAbsolute
                ? $"; the value matches the local elevation ({cameraAltitude:F1} m), so this " +
                  "looks like an absolute WGS84 altitude. Set Altitude near 0 (or switch the " +
                  "anchor to WGS84). ***"
                : ". ***"));
    }

    /// <summary>Initial great-circle bearing from A to B, in degrees clockwise from north.</summary>
    private static double BearingDegrees(
        double latitudeA, double longitudeA, double latitudeB, double longitudeB)
    {
        double phiA = latitudeA * Math.PI / 180d;
        double phiB = latitudeB * Math.PI / 180d;
        double deltaLambda = (longitudeB - longitudeA) * Math.PI / 180d;
        double y = Math.Sin(deltaLambda) * Math.Cos(phiB);
        double x = (Math.Cos(phiA) * Math.Sin(phiB)) -
                   (Math.Sin(phiA) * Math.Cos(phiB) * Math.Cos(deltaLambda));
        return ((Math.Atan2(y, x) * 180d / Math.PI) + 360d) % 360d;
    }

    /// <summary>Great-circle distance between two lat/long pairs, in metres.</summary>
    private static double DistanceMetres(
        double latitudeA, double longitudeA, double latitudeB, double longitudeB)
    {
        const double earthRadiusMetres = 6371000d;
        double deltaLatitude = (latitudeB - latitudeA) * Math.PI / 180d;
        double deltaLongitude = (longitudeB - longitudeA) * Math.PI / 180d;
        double a =
            Math.Sin(deltaLatitude / 2d) * Math.Sin(deltaLatitude / 2d) +
            Math.Cos(latitudeA * Math.PI / 180d) * Math.Cos(latitudeB * Math.PI / 180d) *
            Math.Sin(deltaLongitude / 2d) * Math.Sin(deltaLongitude / 2d);
        return earthRadiusMetres * 2d * Math.Atan2(Math.Sqrt(a), Math.Sqrt(1d - a));
    }

    private void CopyReport()
    {
        _builder.Clear();
        _builder.AppendLine(_status);
        _builder.AppendLine();
        _builder.AppendLine($"=== Log ({_lines.Count} lines) ===");
        for (int i = 0; i < _lines.Count; i++)
        {
            _builder.AppendLine(_lines[i]);
        }

        GUIUtility.systemCopyBuffer = _builder.ToString();
        _copyToastUntil = Time.realtimeSinceStartup + 2f;
    }

    private void EnsureStyles()
    {
        float scale = Mathf.Max(0.5f, textScale);
        if (_logStyle != null && Mathf.Approximately(_stylesBuiltForScale, scale))
        {
            return;
        }

        _stylesBuiltForScale = scale;

        _statusStyle = new GUIStyle(GUI.skin.label)
        {
            fontSize = Mathf.RoundToInt(13f * scale),
            wordWrap = true,
            richText = false,
        };
        _statusStyle.normal.textColor = Color.white;

        _logStyle = new GUIStyle(GUI.skin.label)
        {
            fontSize = Mathf.RoundToInt(11f * scale),
            wordWrap = true,
            richText = false,
        };
        _logStyle.normal.textColor = new Color(0.82f, 0.86f, 0.9f);

        _buttonStyle = new GUIStyle(GUI.skin.button)
        {
            fontSize = Mathf.RoundToInt(15f * scale),
        };
    }

    private void OnGUI()
    {
        EnsureStyles();

        float scale = Mathf.Max(1f, Screen.width / referenceWidth);
        Matrix4x4 previousMatrix = GUI.matrix;
        GUI.matrix = Matrix4x4.TRS(Vector3.zero, Quaternion.identity, new Vector3(scale, scale, 1f));

        float width = Screen.width / scale;
        float height = Screen.height / scale;

        // Controls grow with the text so nothing gets clipped at larger text scales.
        float buttonHeight = 40f * Mathf.Max(0.5f, textScale) * 0.8f;

        if (!showOverlay)
        {
            if (GUI.Button(
                    new Rect(width - (74f * textScale) - 6f, 6f, 68f * textScale, buttonHeight),
                    "debug", _buttonStyle))
            {
                showOverlay = true;
            }

            GUI.matrix = previousMatrix;
            return;
        }

        GUILayout.BeginArea(new Rect(4f, 4f, width - 8f, height - 8f), GUI.skin.box);

        GUILayout.BeginHorizontal();
        if (GUILayout.Button("Copy report", _buttonStyle, GUILayout.Height(buttonHeight)))
        {
            CopyReport();
        }

        if (GUILayout.Button("Clear", _buttonStyle, GUILayout.Height(buttonHeight),
                GUILayout.Width(80f * textScale)))
        {
            _lines.Clear();
        }

        _autoScroll = GUILayout.Toggle(
            _autoScroll, " follow", _buttonStyle, GUILayout.Height(buttonHeight),
            GUILayout.Width(90f * textScale));

        if (GUILayout.Button("Hide", _buttonStyle, GUILayout.Height(buttonHeight),
                GUILayout.Width(70f * textScale)))
        {
            showOverlay = false;
        }

        GUILayout.EndHorizontal();

        if (Time.realtimeSinceStartup < _copyToastUntil)
        {
            GUILayout.Label("Copied to clipboard — paste it anywhere.", _statusStyle);
        }

        GUILayout.Label(_status, _statusStyle);

        _scroll = GUILayout.BeginScrollView(_scroll);
        GUILayout.Label(string.Join("\n", _lines), _logStyle);
        GUILayout.EndScrollView();

        GUILayout.EndArea();
        GUI.matrix = previousMatrix;
    }
}
