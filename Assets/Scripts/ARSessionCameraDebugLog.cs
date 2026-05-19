using Google.XR.ARCoreExtensions;
using UnityEngine;
using UnityEngine.XR.ARFoundation;

#if UNITY_ANDROID
using UnityEngine.Android;
#endif

/// <summary>
/// Attach in AR Geospatial scenes. Logs Earth tracking and AR session state on an interval (filter logcat with "FAW").
/// </summary>
[DefaultExecutionOrder(-100)]
public class ARSessionCameraDebugLog : MonoBehaviour
{
    [SerializeField]
    private float logIntervalSeconds = 5f;

    [SerializeField]
    private bool logOnSessionStateChanged = true;

    [SerializeField]
    [Tooltip("Android only: log Permission.Camera on Awake / resume (enable if camera stays black).")]
    private bool logCameraPermissionExplicitly = false;

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
            Debug.LogWarning("[FAW] AR Geospatial: No ARCoreExtensions in scene.");

        if (_session == null)
            Debug.LogWarning("[FAW] AR Geospatial: No ARSession in scene.");

        if (_cameraManager == null)
            Debug.LogWarning("[FAW] AR Geospatial: No ARCameraManager (no passthrough).");
        else if (_cameraBackground == null)
            Debug.LogWarning("[FAW] AR Geospatial: ARCameraBackground missing on AR camera (passthrough usually needs it).");

        if (_earthManager == null)
            Debug.LogWarning("[FAW] AR Geospatial: No AREarthManager (Earth / Geospatial API unavailable).");

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
            return;

        bool granted = Permission.HasUserAuthorizedPermission(Permission.Camera);
        Debug.Log(
            "[FAW] AR Geospatial: Android Permission.Camera (" + context + ") = " +
            (granted ? "GRANTED" : "NOT_GRANTED"));

        if (_lastLoggedCameraPermission.HasValue && _lastLoggedCameraPermission.Value != granted)
        {
            Debug.LogWarning(
                "[FAW] AR Geospatial: Camera permission changed " +
                (_lastLoggedCameraPermission.Value ? "GRANTED" : "NOT_GRANTED") + " -> " +
                (granted ? "GRANTED" : "NOT_GRANTED"));
        }

        _lastLoggedCameraPermission = granted;
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
            $"[FAW] AR Geospatial: ARSession.state={args.state} notTrackingReason={ARSession.notTrackingReason}");
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
            $"[FAW] AR Geospatial: Earth={earthLine} | ARSession.state={ARSession.state} notTracking={ARSession.notTrackingReason} " +
            $"xrRunning={sessionSubsystemRunning} cam={cameraSubsystem} camBg={(_cameraBackground != null && _cameraBackground.enabled)}" +
            perm);
    }
}
