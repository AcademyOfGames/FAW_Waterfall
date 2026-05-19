using System.Collections;
using UnityEngine;
using UnityEngine.SceneManagement;
#if UNITY_ANDROID
using UnityEngine.Android;
#endif

/// <summary>
/// Keeps nearest experience prefetching via Addressables. On the menu / non-experience scene, when the user is
/// inside the enter radius and dependencies are ready, UI calls <see cref="LoadNearestExperienceFromUi"/> (START).
/// When already inside a known experience scene (from that flow), entering another experience's radius with
/// dependencies ready loads that scene automatically—no return to the menu.
/// Expects an <see cref="AddressableLoadingManager"/> on the same GameObject (or assign explicitly).
/// </summary>
[DisallowMultipleComponent]
public class GeofenceExperienceCoordinator : MonoBehaviour
{
    [SerializeField] private AddressableLoadingManager addressables;
    [SerializeField] private GeofenceHudView hud;
    [SerializeField] private AddressableLabeledSceneButton developerSceneUnlock;
    [Tooltip("Optional: drag the main menu Canvas (e.g. child of this object) to hide after an experience scene loads. Auto-find skips the runtime GeofenceHudCanvas.")]
    [SerializeField] private Canvas menuCanvasToHideAfterLoad;
    [Tooltip("Shown while in geofence and nearest bundle is not ready. Often the same object as Geofence Hud View → Loading Widget Root.")]
    [SerializeField] private GameObject loadingWidget;
    [SerializeField] private float pollSeconds = 0.75f;
    [SerializeField] private bool dontDestroyOnLoad = true;
    [SerializeField] private bool buildRuntimeUiIfMissing = true;
    [Tooltip("UnityEngine.Debug.Log — filter logcat with \"FAW\" for distance, Addressables, START, scene transitions.")]
    [SerializeField] private bool debugLogGeofence = true;
    [Tooltip("While GPS is valid, log nearest experience / distance at most this often (state changes still log immediately).")]
    [SerializeField] private float experienceFindLogSeconds = 5f;
    [Tooltip("While in geofence but Addressables not ready, log why START is blocked on this interval.")]
    [SerializeField] private float startBlockedLogSeconds = 5f;
    [Tooltip("TMP rich-text color for the nearest experience name (bold is applied in code).")]
    [SerializeField] private Color nearestExperienceNameColor = new Color32(0x2E, 0x8B, 0xFF, 0xFF);

    [Header("Editor-only location simulator")]
    [Tooltip("When enabled, Play Mode in the Unity Editor uses simulated lat/lon instead of Input.location. Ignored in all player builds (Application.isEditor is false).")]
    [SerializeField] private bool simulateLocationInEditor = true;
    [SerializeField] private double simulatedLatitude = 47.608;
    [SerializeField] private double simulatedLongitude = -122.3362;
    [SerializeField] private float simulatedHorizontalAccuracyMeters = 5f;

    private float _nextPoll;
    private float _nextLocationWaitLog;
    private float _nextInvalidGpsLog;
    private Coroutine _locationStart;
    private string _lastPrefetchLabel;
    private ExperienceGeofenceDefinition _lastNearest;
    private Canvas _hostCanvas;
    private Canvas _menuCanvasCapturedAtStartClick;
    private float _nextExperienceFindLog;
    private float _nextStartBlockedLog;
    private bool _hadGpsFixForState;
    private bool _prevInRange;
    private bool _prevAddrReady;

    /// <summary>
    /// Updated each successful location poll: true when distance to the nearest definition is within <see cref="ExperienceGeofenceDefinition.EnterGeofenceKm"/>.
    /// </summary>
    public bool IsNearestWithinEnterRadius { get; private set; }

    /// <summary>Last polled nearest experience label (for START UI diagnostics).</summary>
    public bool TryGetLastNearestLabel(out string label)
    {
        label = _lastNearest.AddressableLabel;
        return !string.IsNullOrEmpty(label);
    }

    /// <summary>Shared Addressables instance used by geofence prefetch, START, and developer unlock.</summary>
    public AddressableLoadingManager SharedAddressables => addressables;

    /// <summary>Wires the hidden dev unlock button to this coordinator's Addressables manager.</summary>
    public void BindDeveloperSceneUnlock(AddressableLabeledSceneButton unlock)
    {
        if (unlock == null)
            return;
        developerSceneUnlock = unlock;
        unlock.Bind(this, addressables, hud);
    }

    private void Awake()
    {
        var canvasAbove = GetComponentInParent<Canvas>();

        if (dontDestroyOnLoad)
            DontDestroyOnLoad(gameObject);

        if (addressables == null)
            addressables = GetComponent<AddressableLoadingManager>();
        if (addressables == null)
        {
            Debug.LogError("[FAW] Geofence: AddressableLoadingManager missing — coordinator disabled.");
            enabled = false;
            return;
        }

        addressables.OnNotify += OnAddressablesNotify;
        addressables.OnSceneLoadSucceeded += OnExperienceSceneLoaded;

        if (hud == null && buildRuntimeUiIfMissing)
            hud = GeofenceRuntimeUiBuilder.Build(transform);

        if (developerSceneUnlock == null)
            developerSceneUnlock = FindFirstObjectByType<AddressableLabeledSceneButton>();
        if (developerSceneUnlock != null)
            BindDeveloperSceneUnlock(developerSceneUnlock);

        _hostCanvas = canvasAbove != null ? canvasAbove : FindPreferredMenuCanvas();

        if (loadingWidget == null && hud != null)
            loadingWidget = hud.LoadingWidgetRoot;

        GeoDebug(
            $"init scene={SceneManager.GetActiveScene().name} enterRadiusKm={ExperienceGeofenceDefinition.EnterGeofenceKm:F4} " +
            $"editorLocSim={(UseSimulatedLocation ? "on" : "off")}");
    }

    private void OnDestroy()
    {
        if (addressables != null)
        {
            addressables.OnNotify -= OnAddressablesNotify;
            addressables.OnSceneLoadSucceeded -= OnExperienceSceneLoaded;
        }
    }

    private void OnExperienceSceneLoaded(string label)
    {
        HideHostMenuCanvas();
    }

    /// <summary>Button-friendly alias for <see cref="LoadNearestExperienceFromUi"/>.</summary>
    public void LoadNearestExperience() => LoadNearestExperienceFromUi();

    private void Start()
    {
        if (_locationStart == null)
            _locationStart = StartCoroutine(StartLocationService());
    }

    /// <summary>
    /// Call from the START button when <see cref="IsNearestWithinEnterRadius"/> and Addressables report ready for the nearest label.
    /// </summary>
    public void LoadNearestExperienceFromUi()
    {
        if (addressables == null)
        {
            GeoDebug("START: ignored (AddressableLoadingManager missing)");
            return;
        }

        if (developerSceneUnlock != null && developerSceneUnlock.IsUnlocked)
        {
            if (!addressables.IsReadyForLabel(developerSceneUnlock.AddressableLabel))
            {
                GeoDebug(
                    $"START (dev): ignored (not ready) label='{developerSceneUnlock.AddressableLabel}' state={addressables.State}");
                return;
            }

            var devActive = SceneManager.GetActiveScene().name;
            if (devActive == AddressableLabeledSceneButton.DevSceneName)
            {
                GeoDebug($"START (dev): ignored (already in scene) active='{devActive}'");
                return;
            }

            GeoDebug(
                $"START (dev): loading label='{developerSceneUnlock.AddressableLabel}' from activeScene='{devActive}'");
            RefreshMenuCanvasCaptureAtStartClick();
            StartCoroutine(HideHostMenuCanvasWhenSceneLeaves(devActive));
            addressables.LoadSceneIfReady(developerSceneUnlock.AddressableLabel);
            return;
        }

        var label = _lastNearest.AddressableLabel;
        if (string.IsNullOrEmpty(label))
        {
            GeoDebug("START: ignored (no nearest label yet — wait for GPS fix)");
            return;
        }

        if (!IsNearestWithinEnterRadius)
        {
            GeoDebug($"START: ignored (outside enter radius) label='{label}'");
            return;
        }

        if (!addressables.IsReadyForLabel(label))
        {
            GeoDebug($"START: ignored (Addressables not ready) label='{label}' state={addressables.State}");
            return;
        }

        var activeScene = SceneManager.GetActiveScene().name;
        if (activeScene == _lastNearest.SceneName)
        {
            GeoDebug($"START: ignored (already in scene) active='{activeScene}'");
            return;
        }

        GeoDebug($"START: loading scene label='{label}' from activeScene='{activeScene}' → target='{_lastNearest.SceneName}'");
        RefreshMenuCanvasCaptureAtStartClick();
        StartCoroutine(HideHostMenuCanvasWhenSceneLeaves(activeScene));
        addressables.LoadSceneIfReady(label);
    }

    /// <summary>
    /// Addressables scene handles do not always report Succeeded even when the load completes; hiding when the
    /// active scene actually changes is reliable for DontDestroyOnLoad menu roots.
    /// </summary>
    private IEnumerator HideHostMenuCanvasWhenSceneLeaves(string sceneNameBeforeLoad)
    {
        const float timeoutSec = 45f;
        var t0 = Time.unscaledTime;
        while (SceneManager.GetActiveScene().name == sceneNameBeforeLoad && Time.unscaledTime - t0 < timeoutSec)
            yield return null;

        if (SceneManager.GetActiveScene().name != sceneNameBeforeLoad && HideHostMenuCanvas())
            GeoDebug($"scene left '{sceneNameBeforeLoad}' → '{SceneManager.GetActiveScene().name}' (menu hidden)");
    }

    private static bool IsRuntimeHudCanvas(Canvas c)
    {
        return c != null && c.gameObject.name == GeofenceRuntimeUiBuilder.RuntimeHudCanvasObjectName;
    }

    /// <summary>
    /// Prefers inspector override, then a parent canvas, then any child canvas except the runtime Geofence HUD canvas.
    /// </summary>
    private Canvas FindPreferredMenuCanvas()
    {
        if (menuCanvasToHideAfterLoad != null)
            return menuCanvasToHideAfterLoad;

        var above = GetComponentInParent<Canvas>();
        if (above != null)
            return above;

        for (var i = 0; i < transform.childCount; i++)
        {
            var c = transform.GetChild(i).GetComponent<Canvas>();
            if (c != null && !IsRuntimeHudCanvas(c))
                return c;
        }

        foreach (var c in GetComponentsInChildren<Canvas>(true))
        {
            if (c != null && !IsRuntimeHudCanvas(c))
                return c;
        }

        return null;
    }

    private void RefreshMenuCanvasCaptureAtStartClick()
    {
        if (menuCanvasToHideAfterLoad != null)
            _menuCanvasCapturedAtStartClick = menuCanvasToHideAfterLoad;
        else if (_hostCanvas != null && _hostCanvas)
            _menuCanvasCapturedAtStartClick = _hostCanvas;
        else
            _menuCanvasCapturedAtStartClick = FindPreferredMenuCanvas();

    }

    private Canvas ResolveHostMenuCanvas()
    {
        if (_menuCanvasCapturedAtStartClick != null)
            return _menuCanvasCapturedAtStartClick;

        if (_hostCanvas != null && _hostCanvas)
            return _hostCanvas;

        return _hostCanvas = FindPreferredMenuCanvas();
    }

    private bool HideHostMenuCanvas()
    {
        var canvas = ResolveHostMenuCanvas();
        if (canvas == null)
            return false;
        canvas.gameObject.SetActive(false);
        return true;
    }

    private void Update()
    {
        if (!IsLocationServiceRunning())
        {
            IsNearestWithinEnterRadius = false;
            if (debugLogGeofence && Time.unscaledTime >= _nextLocationWaitLog)
            {
                _nextLocationWaitLog = Time.unscaledTime + 5f;
                GeoDebug(
                    UseSimulatedLocation
                        ? "Waiting (unexpected): editor sim should be running."
                        : $"Waiting for GPS: LocationServiceStatus={Input.location.status} (polls begin when Running)");
            }

            return;
        }

        if (Time.unscaledTime < _nextPoll)
            return;
        _nextPoll = Time.unscaledTime + pollSeconds;

        if (!TryGetLastLocation(out var lat, out var lon, out var hAccM))
        {
            IsNearestWithinEnterRadius = false;
            if (debugLogGeofence && Time.unscaledTime >= _nextInvalidGpsLog)
            {
                _nextInvalidGpsLog = Time.unscaledTime + experienceFindLogSeconds;
                GeoDebug(
                    UseSimulatedLocation
                        ? "Simulated lat/lon are 0,0 — set non-zero values in the Inspector."
                        : "lastData lat/lon are 0,0 — skipping distance until device reports a fix.");
            }

            return;
        }

        var nearest = FindNearest(lat, lon, out var distanceKm);
        _lastNearest = nearest;
        hud?.SetNearestExperienceStatus(distanceKm, nearest.ExperienceName, nearestExperienceNameColor);

        var devUnlocked = developerSceneUnlock != null && developerSceneUnlock.IsUnlocked;
        if (!devUnlocked &&
            !string.Equals(_lastPrefetchLabel, nearest.AddressableLabel, System.StringComparison.Ordinal))
        {
            _lastPrefetchLabel = nearest.AddressableLabel;
            GeoDebug(
                $"prefetch label='{nearest.AddressableLabel}' scene='{nearest.SceneName}' (Addressables download)");
            addressables.BeginOrContinueDownload(nearest.AddressableLabel);
        }

        var inRange = distanceKm <= ExperienceGeofenceDefinition.EnterGeofenceKm;
        IsNearestWithinEnterRadius = inRange;
        var ready = addressables.IsReadyForLabel(nearest.AddressableLabel);
        var activeScene = SceneManager.GetActiveScene().name;

        if (!_hadGpsFixForState)
        {
            _hadGpsFixForState = true;
            _prevInRange = inRange;
            _prevAddrReady = ready;
            _nextExperienceFindLog = Time.unscaledTime;
            _nextStartBlockedLog = Time.unscaledTime;
        }

        var rangeChanged = inRange != _prevInRange;
        var readyChanged = ready != _prevAddrReady;
        if (rangeChanged || readyChanged)
        {
            var wasIn = _prevInRange;
            var wasReady = _prevAddrReady;
            GeoDebug(
                $"geofence state nearest='{nearest.ExperienceName}' distKm={distanceKm:F3} " +
                $"inRange={wasIn}→{inRange} addrReady={wasReady}→{ready} activeScene='{activeScene}' label='{nearest.AddressableLabel}'");
            _prevInRange = inRange;
            _prevAddrReady = ready;
        }

        if (debugLogGeofence && Time.unscaledTime >= _nextExperienceFindLog)
        {
            _nextExperienceFindLog = Time.unscaledTime + experienceFindLogSeconds;
            GeoDebug(
                $"nearest experience lat={lat:F5} lon={lon:F5} hAcc={hAccM:F0}m sim={UseSimulatedLocation} " +
                $"name='{nearest.ExperienceName}' distKm={distanceKm:F3} inRange={inRange} addrReady={ready} " +
                $"scene='{activeScene}' label='{nearest.AddressableLabel}'");
        }

        if (devUnlocked)
        {
            var devReady = addressables.IsReadyForLabel(developerSceneUnlock.AddressableLabel);
            if (loadingWidget != null)
                loadingWidget.SetActive(!devReady);

            if (debugLogGeofence && !devReady && Time.unscaledTime >= _nextStartBlockedLog)
            {
                _nextStartBlockedLog = Time.unscaledTime + startBlockedLogSeconds;
                GeoDebug(
                    "START (dev) blocked (still downloading): " +
                    addressables.BuildStartBlockedSummary(developerSceneUnlock.AddressableLabel, inRange: true));
            }
        }
        else if (inRange)
        {
            if (ready)
            {
                if (loadingWidget != null)
                    loadingWidget.SetActive(false);

                var onMenuOrUnknownScene = !IsKnownExperienceScene(activeScene);
                var alreadyInNearestScene =
                    string.Equals(activeScene, nearest.SceneName, System.StringComparison.Ordinal);

                if (!onMenuOrUnknownScene && !alreadyInNearestScene && !addressables.IsSceneLoadInProgress)
                {
                    GeoDebug(
                        $"auto-load (in experience scene) label='{nearest.AddressableLabel}' '{activeScene}' → '{nearest.SceneName}'");
                    RefreshMenuCanvasCaptureAtStartClick();
                    StartCoroutine(HideHostMenuCanvasWhenSceneLeaves(activeScene));
                    addressables.LoadSceneIfReady(nearest.AddressableLabel);
                }
            }
            else
            {
                if (loadingWidget != null)
                    loadingWidget.SetActive(true);

                if (debugLogGeofence && Time.unscaledTime >= _nextStartBlockedLog)
                {
                    _nextStartBlockedLog = Time.unscaledTime + startBlockedLogSeconds;
                    GeoDebug(
                        "START blocked (in geofence, still downloading): " +
                        addressables.BuildStartBlockedSummary(nearest.AddressableLabel, inRange));
                }
            }
        }
        else if (loadingWidget != null)
        {
            loadingWidget.SetActive(false);
        }
    }

    private static bool IsKnownExperienceScene(string sceneName)
    {
        if (string.IsNullOrEmpty(sceneName))
            return false;

        foreach (var def in ExperienceGeofenceDefinition.All)
        {
            if (string.Equals(def.SceneName, sceneName, System.StringComparison.Ordinal))
                return true;
        }

        return false;
    }

    private static ExperienceGeofenceDefinition FindNearest(double lat, double lon, out double distanceKm)
    {
        var best = ExperienceGeofenceDefinition.All[0];
        var bestDist = double.MaxValue;
        foreach (var def in ExperienceGeofenceDefinition.All)
        {
            var d = ExperienceGeofenceDefinition.HaversineKm(lat, lon, def.Latitude, def.Longitude);
            if (d < bestDist)
            {
                bestDist = d;
                best = def;
            }
        }

        distanceKm = bestDist;
        return best;
    }

    private IEnumerator StartLocationService()
    {
        if (UseSimulatedLocation)
        {
            GeoDebug(
                $"location (editor sim) lat={simulatedLatitude:F6} lon={simulatedLongitude:F6} hAcc={simulatedHorizontalAccuracyMeters:F1}m");
            hud?.SetUserMessage(string.Empty, null);
            yield break;
        }

#if UNITY_ANDROID
        if (!Permission.HasUserAuthorizedPermission(Permission.FineLocation))
        {
            GeoDebug("Android: requesting FineLocation permission");
            Permission.RequestUserPermission(Permission.FineLocation);
            yield return new WaitForSeconds(2f);
        }
#endif
        if (!Input.location.isEnabledByUser)
        {
            GeoDebug("Location blocked: OS location off or denied (isEnabledByUser=false)");
            hud?.SetUserMessage("Location is off. Turn on location to find experiences.",
                null);
            yield break;
        }

        GeoDebug("Location Input.location.Start …");
        Input.location.Start(10f, 10f);
        var wait = 0;
        while (Input.location.status == LocationServiceStatus.Initializing && wait < 30)
        {
            wait++;
            yield return new WaitForSeconds(1f);
        }

        if (Input.location.status != LocationServiceStatus.Running)
        {
            GeoDebug($"Location failed status={Input.location.status}");
            hud?.SetUserMessage("Could not start location.",
                null);
            yield break;
        }

        GeoDebug("Location running — geofence polling active");
        hud?.SetUserMessage(string.Empty, null);
    }

    private void OnAddressablesNotify(string user, string debug)
    {
        hud?.SetUserMessage(user, null);
    }

    private void GeoDebug(string message)
    {
        if (debugLogGeofence)
            Debug.Log("[FAW] " + message);
    }

    /// <summary>True only in Unity Editor when <see cref="simulateLocationInEditor"/> is checked.</summary>
    private bool UseSimulatedLocation => Application.isEditor && simulateLocationInEditor;

    private bool IsLocationServiceRunning()
    {
        if (UseSimulatedLocation)
            return true;
        return Input.location.status == LocationServiceStatus.Running;
    }

    private bool TryGetLastLocation(out double latitude, out double longitude, out float horizontalAccuracyMeters)
    {
        if (UseSimulatedLocation)
        {
            latitude = simulatedLatitude;
            longitude = simulatedLongitude;
            horizontalAccuracyMeters = simulatedHorizontalAccuracyMeters;
            return !(latitude == 0d && longitude == 0d);
        }

        var loc = Input.location.lastData;
        latitude = loc.latitude;
        longitude = loc.longitude;
        horizontalAccuracyMeters = loc.horizontalAccuracy;
        return !(latitude == 0d && longitude == 0d);
    }
}
