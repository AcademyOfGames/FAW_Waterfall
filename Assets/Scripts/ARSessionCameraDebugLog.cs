using Google.XR.ARCoreExtensions;
using UnityEngine;
using UnityEngine.XR.ARFoundation;

#if UNITY_ANDROID
using UnityEngine.Android;
#endif

/// <summary>
/// Attach to any active GameObject in AR Geospatial scenes. Logs AR session lifecycle, Earth
/// localization state, and camera subsystem status at a fixed interval (and on session changes)
/// to help diagnose black camera / stuck "waiting for AR session" issues on device.
/// </summary>
[DefaultExecutionOrder(-100)]
public class ARSessionCameraDebugLog : MonoBehaviour
{
    [SerializeField]
    private float logIntervalSeconds = 2f;

    [SerializeField]
    private bool logOnSessionStateChanged = true;

    [SerializeField]
    [Tooltip("Logs UnityEngine.Android.Permission.Camera on Android builds (see logcat on device).")]
    private bool logCameraPermissionExplicitly = true;

    private ARCoreExtensions _arCoreExtensions;
    private AREarthManager _earthManager;
    private ARCameraManager _cameraManager;
    private ARCameraBackground _cameraBackground;
    private ARSession _session;

    private float _nextPeriodicLogTime;

#if UNITY_ANDROID
    private bool? _lastLoggedCameraPermission;
#endif

    private void Awake()
    {
        _arCoreExtensions = FindObjectOfType<ARCoreExtensions>();
        _earthManager = FindObjectOfType<AREarthManager>();
        _cameraManager = FindObjectOfType<ARCameraManager>();
        _cameraBackground = FindObjectOfType<ARCameraBackground>();
        _session = FindObjectOfType<ARSession>();

        if (_arCoreExtensions == null)
        {
            Debug.LogWarning("[ARSessionCameraDebugLog] No ARCoreExtensions in scene.");
        }

        if (_session == null)
        {
            Debug.LogWarning("[ARSessionCameraDebugLog] No ARSession in scene.");
        }

        if (_cameraManager == null)
        {
            Debug.LogWarning("[ARSessionCameraDebugLog] No ARCameraManager in scene (camera feed will not work).");
        }
        else if (_cameraBackground == null)
        {
            Debug.LogWarning(
                "[ARSessionCameraDebugLog] ARCameraManager present but no ARCameraBackground on the " +
                "AR camera — the passthrough feed usually requires ARCameraBackground.");
        }

        if (_earthManager == null)
        {
            Debug.LogWarning("[ARSessionCameraDebugLog] No AREarthManager in scene (Geospatial Earth API unavailable).");
        }

        if (logCameraPermissionExplicitly)
        {
            LogCameraPermissionStatus("Awake");
        }
    }

    private void Start()
    {
        if (logCameraPermissionExplicitly)
        {
            LogCameraPermissionStatus("Start");
        }
    }

    private void OnApplicationPause(bool pauseStatus)
    {
        if (pauseStatus || !logCameraPermissionExplicitly)
        {
            return;
        }

        LogCameraPermissionStatus("AppResumed");
    }

    private void LogCameraPermissionStatus(string context)
    {
#if UNITY_ANDROID
        if (Application.isEditor)
        {
            Debug.Log(
                "[ARSessionCameraDebugLog] (" + context + ") Camera permission: check an installed " +
                "APK with adb logcat — UnityEngine.Android.Permission is not meaningful in the Editor.");
            return;
        }

        bool granted = Permission.HasUserAuthorizedPermission(Permission.Camera);
        Debug.Log(
            "[ARSessionCameraDebugLog] (" + context + ") Android Permission.Camera = " +
            (granted ? "GRANTED" : "NOT GRANTED") +
            " (if NOT GRANTED, AR camera feed will stay black until the user allows camera access).");

        if (_lastLoggedCameraPermission.HasValue && _lastLoggedCameraPermission.Value != granted)
        {
            Debug.LogWarning(
                "[ARSessionCameraDebugLog] Camera permission changed: " +
                (_lastLoggedCameraPermission.Value ? "GRANTED" : "NOT GRANTED") + " -> " +
                (granted ? "GRANTED" : "NOT GRANTED"));
        }

        _lastLoggedCameraPermission = granted;
#else
        Debug.Log(
            "[ARSessionCameraDebugLog] (" + context + ") Camera permission: Android-only check " +
            "(UnityEngine.Android.Permission.Camera); this build target does not use that API.");
#endif
    }

    private void OnEnable()
    {
        if (logOnSessionStateChanged)
        {
            ARSession.stateChanged += OnARSessionStateChanged;
        }
    }

    private void OnDisable()
    {
        ARSession.stateChanged -= OnARSessionStateChanged;
    }

    private void OnARSessionStateChanged(ARSessionStateChangedEventArgs args)
    {
        Debug.Log(
            $"[ARSessionCameraDebugLog] ARSession.state changed: {args.state}, " +
            $"notTrackingReason={ARSession.notTrackingReason}");
    }

    private void Update()
    {
        if (logIntervalSeconds <= 0f)
        {
            return;
        }

        if (Time.unscaledTime < _nextPeriodicLogTime)
        {
            return;
        }

        _nextPeriodicLogTime = Time.unscaledTime + logIntervalSeconds;
        LogPeriodicSnapshot();
    }

    private void LogPeriodicSnapshot()
    {
        bool extensionsOk = _arCoreExtensions != null && _arCoreExtensions.Session != null;
        bool sessionSubsystemRunning =
            _session != null && _session.subsystem != null && _session.subsystem.running;

        string cameraSubsystem = "no ARCameraManager";
        if (_cameraManager != null && _cameraManager.subsystem != null)
        {
            cameraSubsystem = $"running={_cameraManager.subsystem.running}";
        }

        string earthLine = "AREarthManager missing";
        if (_earthManager != null)
        {
            try
            {
                earthLine =
                    $"EarthState={_earthManager.EarthState}, EarthTracking={_earthManager.EarthTrackingState}";
            }
            catch (System.Exception ex)
            {
                earthLine = $"Earth query failed: {ex.Message}";
            }
        }

#if UNITY_ANDROID
        bool camPerm = Permission.HasUserAuthorizedPermission(Permission.Camera);
        string perm = $", androidCameraPermission={camPerm}";
#else
        string perm = string.Empty;
#endif

        Debug.Log(
            $"[ARSessionCameraDebugLog] ARSession.state={ARSession.state}, notTrackingReason={ARSession.notTrackingReason}, " +
            $"ARCoreExtensions.sessionAssigned={extensionsOk}, XRSessionSubsystem.running={sessionSubsystemRunning}, " +
            $"{earthLine}, {cameraSubsystem}, " +
            $"ARCameraBackground={(_cameraBackground != null && _cameraBackground.enabled)}, " +
            $"ARSession.enabled={(_session != null && _session.enabled)}" +
            perm);
    }
}
