using System;
using System.Collections;
using System.Collections.Generic;
using UnityEngine.Serialization;
using Google.XR.ARCoreExtensions;
using Google.XR.ARCoreExtensions.GeospatialCreator;
using UnityEngine;
using UnityEngine.XR.ARFoundation;
using UnityEngine.XR.ARSubsystems;

/// <summary>
/// VPS localization diagnostics for AR Geospatial scenes. Filter logcat with "FAW".
/// Logs phase transitions, periodic accuracy while settling, and VPS coverage at device + anchors.
/// </summary>
[DefaultExecutionOrder(-100)]
public class ARSessionCameraDebugLog : MonoBehaviour
{
    private enum VpsSettlePhase
    {
        WaitingSession,
        WaitingEarth,
        Settling,
        Localized,
        Degraded,
    }

    private struct GeoPoint
    {
        public string Label;
        public double Latitude;
        public double Longitude;
    }

    [SerializeField]
    [Tooltip("While settling, log accuracy at this interval (seconds). 0 = only on phase changes.")]
    private float logIntervalSeconds = 4f;

    [SerializeField]
    [Tooltip("Horizontal accuracy (m) at or below this = Localized (sample app uses 20).")]
    private double horizontalLocalizedThresholdMeters = 20.0;

    [SerializeField]
    [Tooltip("Horizontal accuracy (m) at or below this = good enough for tight anchor placement.")]
    private double horizontalGoodThresholdMeters = 10.0;

    [SerializeField]
    [Tooltip("Horizontal accuracy (m) at or below this = excellent VPS lock.")]
    private double horizontalExcellentThresholdMeters = 5.0;

    [FormerlySerializedAs("checkVpsAvailabilityAtAnchorLocation")]
    [SerializeField]
    [Tooltip("Query Google VPS coverage at the device and at each Geospatial Creator anchor in the scene.")]
    private bool checkVpsAvailability = true;

    [SerializeField]
    [Tooltip("While localized, log GPS + Unity distance from camera to each Geospatial Creator anchor.")]
    private bool logAnchorDistance = true;

    private AREarthManager _earthManager;
    private ARSession _session;

    private VpsSettlePhase _phase = VpsSettlePhase.WaitingSession;
    private ARSessionState _lastSessionState = ARSessionState.None;
    private float _nextPeriodicLogTime;
    private bool _vpsCoverageChecksStarted;
    private bool _anchorDistanceLoggedOnce;
    private readonly HashSet<string> _vpsQueriedKeys = new HashSet<string>();

    /// <summary>True when Earth is tracking and horizontal accuracy is within the localized threshold.</summary>
    public bool IsVpsLocalized =>
        _phase == VpsSettlePhase.Localized;

    /// <summary>True when horizontal accuracy is at or below the excellent threshold.</summary>
    public bool IsVpsExcellent { get; private set; }

    private void Awake()
    {
        _earthManager = FindObjectOfType<AREarthManager>();
        _session = FindObjectOfType<ARSession>();

        if (_earthManager == null)
        {
            Debug.LogWarning("[FAW] VPS: No AREarthManager — geospatial accuracy logging disabled.");
        }
    }

    private void OnEnable()
    {
        ARSession.stateChanged += OnARSessionStateChanged;
    }

    private void OnDisable()
    {
        ARSession.stateChanged -= OnARSessionStateChanged;
    }

    private void OnARSessionStateChanged(ARSessionStateChangedEventArgs args)
    {
        if (args.state == _lastSessionState)
        {
            return;
        }

        if (IsInterestingSessionTransition(_lastSessionState, args.state))
        {
            Debug.Log(
                $"[FAW] VPS: ARSession {_lastSessionState} -> {args.state} " +
                $"(notTracking={ARSession.notTrackingReason})");
        }

        _lastSessionState = args.state;
    }

    private static bool IsInterestingSessionTransition(ARSessionState from, ARSessionState to)
    {
        return to == ARSessionState.SessionTracking
            || to == ARSessionState.Ready
            || to == ARSessionState.Unsupported
            || to == ARSessionState.None
            || from == ARSessionState.SessionTracking;
    }

    private void Update()
    {
        if (_earthManager == null)
        {
            return;
        }

        VpsSettlePhase newPhase = EvaluatePhase(out GeospatialPose? pose, out string detail);

        if (newPhase != _phase)
        {
            LogPhaseChange(_phase, newPhase, pose, detail);
            _phase = newPhase;
            _nextPeriodicLogTime = Time.unscaledTime + Mathf.Max(1f, logIntervalSeconds);
        }
        else if (ShouldLogPeriodic(newPhase))
        {
            if (Time.unscaledTime >= _nextPeriodicLogTime)
            {
                _nextPeriodicLogTime = Time.unscaledTime + Mathf.Max(1f, logIntervalSeconds);
                LogAccuracySnapshot(newPhase, pose, detail, periodic: true);
            }
        }

        if (checkVpsAvailability
            && pose.HasValue
            && !_vpsCoverageChecksStarted)
        {
            _vpsCoverageChecksStarted = true;
            StartCoroutine(RunVpsCoverageChecks(pose.Value));
        }

        if (logAnchorDistance
            && pose.HasValue
            && newPhase == VpsSettlePhase.Localized
            && !_anchorDistanceLoggedOnce)
        {
            _anchorDistanceLoggedOnce = true;
            LogAnchorDistances(pose.Value);
        }
    }

    private VpsSettlePhase EvaluatePhase(out GeospatialPose? pose, out string detail)
    {
        pose = null;
        detail = string.Empty;

        if (_session == null || ARSession.state != ARSessionState.SessionTracking)
        {
            IsVpsExcellent = false;
            detail = $"ARSession.state={ARSession.state}";
            return VpsSettlePhase.WaitingSession;
        }

        EarthState earthState = _earthManager.EarthState;
        TrackingState earthTracking = _earthManager.EarthTrackingState;

        if (earthState != EarthState.Enabled)
        {
            IsVpsExcellent = false;
            detail = $"EarthState={earthState}";
            return VpsSettlePhase.WaitingEarth;
        }

        if (earthTracking != TrackingState.Tracking)
        {
            IsVpsExcellent = false;
            detail = $"EarthTracking={earthTracking}";
            return VpsSettlePhase.WaitingEarth;
        }

        GeospatialPose p = _earthManager.CameraGeospatialPose;
        pose = p;

        if (_phase == VpsSettlePhase.Localized
            && p.HorizontalAccuracy > horizontalLocalizedThresholdMeters * 1.5)
        {
            IsVpsExcellent = false;
            detail = FormatAccuracy(p) + " (accuracy worsened)";
            return VpsSettlePhase.Degraded;
        }

        if (p.HorizontalAccuracy <= horizontalExcellentThresholdMeters)
        {
            IsVpsExcellent = true;
        }
        else
        {
            IsVpsExcellent = false;
        }

        detail = FormatAccuracy(p);

        if (p.HorizontalAccuracy <= horizontalLocalizedThresholdMeters)
        {
            return VpsSettlePhase.Localized;
        }

        return VpsSettlePhase.Settling;
    }

    private bool ShouldLogPeriodic(VpsSettlePhase phase)
    {
        if (logIntervalSeconds <= 0f)
        {
            return false;
        }

        return phase == VpsSettlePhase.Settling
            || phase == VpsSettlePhase.Degraded
            || (phase == VpsSettlePhase.Localized && !IsVpsExcellent);
    }

    private void LogPhaseChange(
        VpsSettlePhase from,
        VpsSettlePhase to,
        GeospatialPose? pose,
        string detail)
    {
        string qualityHint = to switch
        {
            VpsSettlePhase.Settling =>
                "walk outdoors slowly; accuracy should improve over 30–60s",
            VpsSettlePhase.Localized =>
                IsVpsExcellent
                    ? "excellent lock — safe to trust anchor alignment"
                    : "localized — anchors OK; wait for <5m horiz. for best alignment",
            VpsSettlePhase.Degraded =>
                "VPS lock weakened — content may drift until accuracy recovers",
            VpsSettlePhase.WaitingEarth =>
                "enable Geospatial mode, check API auth, stay outdoors with clear sky",
            _ => string.Empty,
        };

        if (!string.IsNullOrEmpty(qualityHint))
        {
            detail = string.IsNullOrEmpty(detail) ? qualityHint : detail + " | " + qualityHint;
        }

        if (to == VpsSettlePhase.Localized || to == VpsSettlePhase.Settling || to == VpsSettlePhase.Degraded)
        {
            LogAccuracySnapshot(to, pose, detail, periodic: false);
            return;
        }

        Debug.Log($"[FAW] VPS: {from} -> {to}" + (string.IsNullOrEmpty(detail) ? "" : " | " + detail));
    }

    private void LogAccuracySnapshot(
        VpsSettlePhase phase,
        GeospatialPose? pose,
        string detail,
        bool periodic)
    {
        string prefix = periodic ? "[FAW] VPS (updating)" : "[FAW] VPS";
        string poseLine = pose.HasValue
            ? FormatAccuracy(pose.Value)
            : "pose unavailable";

        string tier = pose.HasValue ? AccuracyTierLabel(pose.Value.HorizontalAccuracy) : "";

        Debug.Log(
            $"{prefix}: {phase} {tier} | {poseLine}" +
            (string.IsNullOrEmpty(detail) ? "" : " | " + detail));
    }

    private string AccuracyTierLabel(double horizontalAccuracyMeters)
    {
        if (horizontalAccuracyMeters <= horizontalExcellentThresholdMeters)
        {
            return "[excellent ≤" + horizontalExcellentThresholdMeters + "m]";
        }

        if (horizontalAccuracyMeters <= horizontalGoodThresholdMeters)
        {
            return "[good ≤" + horizontalGoodThresholdMeters + "m]";
        }

        if (horizontalAccuracyMeters <= horizontalLocalizedThresholdMeters)
        {
            return "[localized ≤" + horizontalLocalizedThresholdMeters + "m]";
        }

        return "[coarse >" + horizontalLocalizedThresholdMeters + "m]";
    }

    private static string FormatAccuracy(GeospatialPose p)
    {
        return $"horiz={p.HorizontalAccuracy:F1}m vert={p.VerticalAccuracy:F1}m " +
            $"yaw={p.OrientationYawAccuracy:F1}° " +
            $"lat={p.Latitude:F6} lon={p.Longitude:F6} alt={p.Altitude:F1}m";
    }

    private static void LogAnchorDistances(GeospatialPose devicePose)
    {
        ARGeospatialCreatorAnchor[] anchors = FindGeospatialCreatorAnchors();
        if (anchors.Length == 0)
        {
            Debug.LogWarning("[FAW] VPS: no Geospatial Creator anchors in scene — content may not be earth-anchored.");
            return;
        }

        Camera cam = Camera.main;
        foreach (ARGeospatialCreatorAnchor anchor in anchors)
        {
            if (double.IsNaN(anchor.Latitude) || double.IsNaN(anchor.Longitude))
            {
                Debug.LogWarning($"[FAW] Anchor '{anchor.name}' has no lat/lon.");
                continue;
            }

            double gpsM = HaversineMeters(
                devicePose.Latitude,
                devicePose.Longitude,
                anchor.Latitude,
                anchor.Longitude);

            string worldLine = "world dist n/a (anchor not resolved yet)";
            if (cam != null)
            {
                float worldM = Vector3.Distance(cam.transform.position, anchor.transform.position);
                worldLine = $"world dist={worldM:F1}m (camera ↔ anchor transform)";
            }

            Debug.Log(
                $"[FAW] Anchor placement '{anchor.name}' ({anchor.AltitudeType}): " +
                $"anchor lat/lon=({anchor.Latitude:F5}, {anchor.Longitude:F5}) | " +
                $"GPS dist from you={gpsM:F0}m (not the horiz= accuracy line) | {worldLine}");
        }
    }

    private static double HaversineMeters(double lat1, double lon1, double lat2, double lon2)
    {
        const double earthRadiusM = 6371000.0;
        var dLat = (lat2 - lat1) * (Math.PI / 180.0);
        var dLon = (lon2 - lon1) * (Math.PI / 180.0);
        var a = Math.Sin(dLat / 2.0) * Math.Sin(dLat / 2.0) +
                Math.Cos(lat1 * (Math.PI / 180.0)) * Math.Cos(lat2 * (Math.PI / 180.0)) *
                Math.Sin(dLon / 2.0) * Math.Sin(dLon / 2.0);
        var c = 2.0 * Math.Atan2(Math.Sqrt(a), Math.Sqrt(1.0 - a));
        return earthRadiusM * c;
    }

    private IEnumerator RunVpsCoverageChecks(GeospatialPose devicePose)
    {
        var points = new List<GeoPoint>
        {
            new GeoPoint
            {
                Label = "device (where you stand)",
                Latitude = devicePose.Latitude,
                Longitude = devicePose.Longitude,
            },
        };

        ARGeospatialCreatorAnchor[] anchors = FindGeospatialCreatorAnchors();
        foreach (ARGeospatialCreatorAnchor anchor in anchors)
        {
            if (double.IsNaN(anchor.Latitude) || double.IsNaN(anchor.Longitude))
            {
                Debug.LogWarning(
                    $"[FAW] VPS coverage: anchor '{anchor.name}' has no lat/lon — skipped.");
                continue;
            }

            points.Add(new GeoPoint
            {
                Label = "anchor '" + anchor.name + "'",
                Latitude = anchor.Latitude,
                Longitude = anchor.Longitude,
            });
        }

        Debug.Log($"[FAW] VPS coverage: checking {points.Count} location(s)…");

        foreach (GeoPoint point in points)
        {
            yield return QueryAndLogVpsCoverage(point);
        }
    }

    private static ARGeospatialCreatorAnchor[] FindGeospatialCreatorAnchors()
    {
#if UNITY_2023_1_OR_NEWER
        return UnityEngine.Object.FindObjectsByType<ARGeospatialCreatorAnchor>(FindObjectsSortMode.None);
#else
        return UnityEngine.Object.FindObjectsOfType<ARGeospatialCreatorAnchor>();
#endif
    }

    private IEnumerator QueryAndLogVpsCoverage(GeoPoint point)
    {
        string key = point.Latitude.ToString("F5") + "," + point.Longitude.ToString("F5");
        if (!_vpsQueriedKeys.Add(key))
        {
            yield break;
        }

        VpsAvailabilityPromise promise =
            AREarthManager.CheckVpsAvailabilityAsync(point.Latitude, point.Longitude);

        yield return promise;

        Debug.Log(
            $"[FAW] VPS coverage at {point.Label} ({point.Latitude:F5}, {point.Longitude:F5}): " +
            FormatVpsCoverageVerdict(promise.Result));
    }

    private static string FormatVpsCoverageVerdict(VpsAvailability availability)
    {
        switch (availability)
        {
            case VpsAvailability.Available:
                return "YES — VPS data exists here (Available)";
            case VpsAvailability.Unavailable:
                return "NO — no VPS coverage here (Unavailable); GPS-only may be weaker";
            case VpsAvailability.Unknown:
                return "UNKNOWN — query incomplete or session not ready";
            case VpsAvailability.ErrorNotAuthorized:
                return "ERROR — API not authorized (check ARCore / Geospatial API setup)";
            case VpsAvailability.ErrorNetworkConnection:
                return "ERROR — network (could not reach VPS check service)";
            case VpsAvailability.ErrorResourceExhausted:
                return "ERROR — quota / too many requests";
            case VpsAvailability.ErrorInternal:
                return "ERROR — internal (see logcat)";
            default:
                return availability.ToString();
        }
    }
}
