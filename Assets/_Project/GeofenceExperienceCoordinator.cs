using System.Collections;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.Serialization;
#if UNITY_ANDROID
using UnityEngine.Android;
#endif

/// <summary>
/// Tracks the nearest experience geofence, prepares its scene for loading, and opens it from START
/// or automatically when switching between experience scenes in range.
/// Expects an <see cref="ExperienceSceneLoadingManager"/> on the same GameObject (or assign explicitly).
/// </summary>
[DisallowMultipleComponent]
public class GeofenceExperienceCoordinator : MonoBehaviour
{
    [FormerlySerializedAs("addressables")]
    [SerializeField] private ExperienceSceneLoadingManager sceneLoader;
    [SerializeField] private GeofenceHudView hud;
    [SerializeField] private DeveloperSceneUnlockButton developerSceneUnlock;
    [Tooltip("Optional: drag the main menu Canvas (e.g. child of this object) to hide after an experience scene loads. Auto-find skips the runtime GeofenceHudCanvas.")]
    [SerializeField] private Canvas menuCanvasToHideAfterLoad;
    [Tooltip("Shown while in geofence and nearest scene is not ready. Often the same object as Geofence Hud View → Loading Widget Root.")]
    [SerializeField] private GameObject loadingWidget;
    [SerializeField] private float pollSeconds = 0.75f;
    [SerializeField] private bool dontDestroyOnLoad = true;
    [SerializeField] private bool buildRuntimeUiIfMissing = true;
    [Tooltip("UnityEngine.Debug.Log — filter logcat with \"FAW\" for distance, scenes, START, scene transitions.")]
    [SerializeField] private bool debugLogGeofence = false;
    [Tooltip("While GPS is valid, log nearest experience / distance at most this often (state changes still log immediately).")]
    [SerializeField] private float experienceFindLogSeconds = 10f;
    [Tooltip("While in geofence but scene is not ready, log why START is blocked on this interval.")]
    [SerializeField] private float startBlockedLogSeconds = 5f;
    [Tooltip("TMP rich-text color for the nearest experience name (bold is applied in code).")]
    [SerializeField] private Color nearestExperienceNameColor = new Color32(0x2E, 0x8B, 0xFF, 0xFF);

    [Header("Debug / testing — force geofence")]
    [Tooltip("Pretend GPS is at this experience's lat/lon. If more than one is checked, the first in list order below wins (Benaroya → Alina → Divine → Chenoa → Dan).")]
    [SerializeField] private bool forceBenaroyaGeofence;
    [FormerlySerializedAs("forceAtAlinaGeofence")]
    [SerializeField] private bool forceAlinaGeofence;
    [FormerlySerializedAs("forceSampleSceneGeofence")]
    [SerializeField] private bool forceDivineSceneGeofence;
    [SerializeField] private bool forceChenoaGeofence;
    [SerializeField] private bool forceDanGeofence;
    [Tooltip("When runtime HUD is built, add on-screen toggles for force-geofence overrides.")]
    [SerializeField] private bool buildRuntimeForceGeofenceToggles = true;

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
    private string _lastPrefetchSceneName;
    private ExperienceGeofenceDefinition _lastNearest;
    private Canvas _hostCanvas;
    private Canvas _menuCanvasCapturedAtStartClick;
    private float _nextExperienceFindLog;
    private float _nextStartBlockedLog;
    private bool _hadGpsFixForState;
    private bool _prevInRange;
    private bool _prevSceneReady;

    public bool IsNearestWithinEnterRadius { get; private set; }

    public bool TryGetLastNearestSceneName(out string sceneName)
    {
        sceneName = _lastNearest.SceneName;
        return !string.IsNullOrEmpty(sceneName);
    }

    public ExperienceSceneLoadingManager SharedSceneLoader => sceneLoader;

    public bool GetForceGeofence(string sceneName)
    {
        return IsForceGeofenceEnabledForScene(sceneName);
    }

    public void SetForceGeofence(string sceneName, bool value)
    {
        if (!TrySetForceGeofenceForScene(sceneName, value))
            return;
        _lastPrefetchSceneName = null;
        GeoDebug(value ? $"Force geofence: ON ({sceneName})" : $"Force geofence: OFF ({sceneName})");
    }

    public void BindDeveloperSceneUnlock(DeveloperSceneUnlockButton unlock)
    {
        if (unlock == null)
            return;
        developerSceneUnlock = unlock;
        unlock.Bind(this, sceneLoader, hud);
    }

    private void Awake()
    {
        var canvasAbove = GetComponentInParent<Canvas>();

        if (dontDestroyOnLoad)
            DontDestroyOnLoad(gameObject);

        if (sceneLoader == null)
            sceneLoader = GetComponent<ExperienceSceneLoadingManager>();
        if (sceneLoader == null)
        {
            Debug.LogError("[FAW] Geofence: ExperienceSceneLoadingManager missing — coordinator disabled.");
            enabled = false;
            return;
        }

        sceneLoader.OnNotify += OnSceneLoaderNotify;
        sceneLoader.OnSceneLoadSucceeded += OnExperienceSceneLoaded;

        if (hud == null && buildRuntimeUiIfMissing)
            hud = GeofenceRuntimeUiBuilder.Build(transform, this, buildRuntimeForceGeofenceToggles);

        if (developerSceneUnlock == null)
            developerSceneUnlock = FindFirstObjectByType<DeveloperSceneUnlockButton>();
        if (developerSceneUnlock != null)
            BindDeveloperSceneUnlock(developerSceneUnlock);

        _hostCanvas = canvasAbove != null ? canvasAbove : FindPreferredMenuCanvas();

        if (loadingWidget == null && hud != null)
            loadingWidget = hud.LoadingWidgetRoot;

        GeoDebug(
            $"init scene={SceneManager.GetActiveScene().name} enterRadiusKm={ExperienceGeofenceDefinition.EnterGeofenceKm:F4} " +
            $"editorLocSim={(UseSimulatedLocation ? "on" : "off")} forceGeofence={(TryGetForcedGeofence(out var forced) ? forced.SceneName : "off")}");
    }

    private void OnDestroy()
    {
        if (sceneLoader != null)
        {
            sceneLoader.OnNotify -= OnSceneLoaderNotify;
            sceneLoader.OnSceneLoadSucceeded -= OnExperienceSceneLoaded;
        }
    }

    private void OnExperienceSceneLoaded(string sceneName)
    {
        HideHostMenuCanvas();
    }

    public void LoadNearestExperience() => LoadNearestExperienceFromUi();

    private void Start()
    {
        if (_locationStart == null)
            _locationStart = StartCoroutine(StartLocationService());
    }

    public void LoadNearestExperienceFromUi()
    {
        if (sceneLoader == null)
        {
            GeoDebug("START: ignored (ExperienceSceneLoadingManager missing)");
            return;
        }

        if (developerSceneUnlock != null && developerSceneUnlock.IsUnlocked)
        {
            var devScene = developerSceneUnlock.DeveloperSceneName;
            if (!sceneLoader.IsReadyForScene(devScene))
            {
                GeoDebug($"START (dev): ignored (not ready) scene='{devScene}' state={sceneLoader.State}");
                return;
            }

            var devActive = SceneManager.GetActiveScene().name;
            if (devActive == DeveloperSceneUnlockButton.DevSceneName)
            {
                GeoDebug($"START (dev): ignored (already in scene) active='{devActive}'");
                return;
            }

            GeoDebug($"START (dev): loading scene='{devScene}' from activeScene='{devActive}'");
            RefreshMenuCanvasCaptureAtStartClick();
            StartCoroutine(HideHostMenuCanvasWhenSceneLeaves(devActive));
            sceneLoader.LoadSceneIfReady(devScene);
            return;
        }

        var sceneName = _lastNearest.SceneName;
        if (string.IsNullOrEmpty(sceneName))
        {
            GeoDebug("START: ignored (no nearest scene yet — wait for GPS fix)");
            return;
        }

        if (!IsNearestWithinEnterRadius)
        {
            GeoDebug($"START: ignored (outside enter radius) scene='{sceneName}'");
            return;
        }

        if (!sceneLoader.IsReadyForScene(sceneName))
        {
            GeoDebug($"START: ignored (scene not ready) scene='{sceneName}' state={sceneLoader.State}");
            return;
        }

        var activeScene = SceneManager.GetActiveScene().name;
        if (activeScene == sceneName)
        {
            GeoDebug($"START: ignored (already in scene) active='{activeScene}'");
            return;
        }

        GeoDebug($"START: loading scene='{sceneName}' from activeScene='{activeScene}'");
        RefreshMenuCanvasCaptureAtStartClick();
        StartCoroutine(HideHostMenuCanvasWhenSceneLeaves(activeScene));
        sceneLoader.LoadSceneIfReady(sceneName);
    }

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
                _nextLocationWaitLog = Time.unscaledTime + experienceFindLogSeconds;
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
            !string.Equals(_lastPrefetchSceneName, nearest.SceneName, System.StringComparison.Ordinal))
        {
            _lastPrefetchSceneName = nearest.SceneName;
            GeoDebug($"prepare nearest scene='{nearest.SceneName}' ({nearest.ExperienceName})");
            sceneLoader.PrepareScene(nearest.SceneName);
        }

        var inRange = distanceKm <= ExperienceGeofenceDefinition.EnterGeofenceKm;
        IsNearestWithinEnterRadius = inRange;
        var ready = sceneLoader.IsReadyForScene(nearest.SceneName);
        var activeScene = SceneManager.GetActiveScene().name;

        if (!_hadGpsFixForState)
        {
            _hadGpsFixForState = true;
            _prevInRange = inRange;
            _prevSceneReady = ready;
            _nextExperienceFindLog = Time.unscaledTime;
            _nextStartBlockedLog = Time.unscaledTime;
        }

        var rangeChanged = inRange != _prevInRange;
        var readyChanged = ready != _prevSceneReady;
        if (rangeChanged || readyChanged)
        {
            var wasIn = _prevInRange;
            var wasReady = _prevSceneReady;
            GeoDebug(
                $"geofence state nearest='{nearest.ExperienceName}' distKm={distanceKm:F3} " +
                $"inRange={wasIn}→{inRange} sceneReady={wasReady}→{ready} activeScene='{activeScene}' target='{nearest.SceneName}'");
            _prevInRange = inRange;
            _prevSceneReady = ready;
        }

        if (debugLogGeofence && Time.unscaledTime >= _nextExperienceFindLog)
        {
            _nextExperienceFindLog = Time.unscaledTime + experienceFindLogSeconds;
            GeoDebug(
                $"nearest experience lat={lat:F5} lon={lon:F5} hAcc={hAccM:F0}m sim={UseSimulatedLocation} forceGeofence={(TryGetForcedGeofence(out var f) ? f.SceneName : "off")} " +
                $"name='{nearest.ExperienceName}' distKm={distanceKm:F3} inRange={inRange} sceneReady={ready} " +
                $"activeScene='{activeScene}' target='{nearest.SceneName}'");
        }

        if (devUnlocked)
        {
            var devReady = sceneLoader.IsReadyForScene(developerSceneUnlock.DeveloperSceneName);
            if (loadingWidget != null)
                loadingWidget.SetActive(!devReady);

            if (debugLogGeofence && !devReady && Time.unscaledTime >= _nextStartBlockedLog)
            {
                _nextStartBlockedLog = Time.unscaledTime + startBlockedLogSeconds;
                GeoDebug(
                    "START (dev) blocked: " +
                    sceneLoader.BuildStartBlockedSummary(developerSceneUnlock.DeveloperSceneName, inRange: true));
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

                if (!onMenuOrUnknownScene && !alreadyInNearestScene && !sceneLoader.IsSceneLoadInProgress)
                {
                    GeoDebug(
                        $"auto-load (in experience scene) '{activeScene}' → '{nearest.SceneName}'");
                    RefreshMenuCanvasCaptureAtStartClick();
                    StartCoroutine(HideHostMenuCanvasWhenSceneLeaves(activeScene));
                    sceneLoader.LoadSceneIfReady(nearest.SceneName);
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
                        "START blocked (in geofence, scene not ready): " +
                        sceneLoader.BuildStartBlockedSummary(nearest.SceneName, inRange));
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

    private void OnSceneLoaderNotify(string user, string debug)
    {
        hud?.SetUserMessage(user, null);
    }

    private void GeoDebug(string message)
    {
        if (debugLogGeofence)
            Debug.Log("[FAW] " + message);
    }

    private bool UseSimulatedLocation => Application.isEditor && simulateLocationInEditor;

    private bool IsLocationServiceRunning()
    {
        if (AnyForceGeofenceEnabled || UseSimulatedLocation)
            return true;
        return Input.location.status == LocationServiceStatus.Running;
    }

    private bool TryGetLastLocation(out double latitude, out double longitude, out float horizontalAccuracyMeters)
    {
        if (TryGetForcedGeofence(out var forced))
        {
            latitude = forced.Latitude;
            longitude = forced.Longitude;
            horizontalAccuracyMeters = 5f;
            return true;
        }

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

    private bool AnyForceGeofenceEnabled =>
        forceBenaroyaGeofence || forceAlinaGeofence || forceDivineSceneGeofence ||
        forceChenoaGeofence || forceDanGeofence;

    private bool TryGetForcedGeofence(out ExperienceGeofenceDefinition definition)
    {
        foreach (var def in ExperienceGeofenceDefinition.All)
        {
            if (IsForceGeofenceEnabledForScene(def.SceneName))
            {
                definition = def;
                return true;
            }
        }

        definition = default;
        return false;
    }

    private bool IsForceGeofenceEnabledForScene(string sceneName)
    {
        if (string.IsNullOrEmpty(sceneName))
            return false;

        foreach (var def in ExperienceGeofenceDefinition.All)
        {
            if (!string.Equals(def.SceneName, sceneName, System.StringComparison.Ordinal))
                continue;

            return def.SceneName switch
            {
                "benaroyaScene" => forceBenaroyaGeofence,
                "AlinaScene" => forceAlinaGeofence,
                "DivineScene" => forceDivineSceneGeofence,
                "ChenoaScene" => forceChenoaGeofence,
                "DanScene" => forceDanGeofence,
                _ => false
            };
        }

        return false;
    }

    private bool TrySetForceGeofenceForScene(string sceneName, bool value)
    {
        if (string.IsNullOrEmpty(sceneName))
            return false;

        foreach (var def in ExperienceGeofenceDefinition.All)
        {
            if (!string.Equals(def.SceneName, sceneName, System.StringComparison.Ordinal))
                continue;

            switch (def.SceneName)
            {
                case "benaroyaScene":
                    if (forceBenaroyaGeofence == value) return false;
                    forceBenaroyaGeofence = value;
                    return true;
                case "AlinaScene":
                    if (forceAlinaGeofence == value) return false;
                    forceAlinaGeofence = value;
                    return true;
                case "DivineScene":
                    if (forceDivineSceneGeofence == value) return false;
                    forceDivineSceneGeofence = value;
                    return true;
                case "ChenoaScene":
                    if (forceChenoaGeofence == value) return false;
                    forceChenoaGeofence = value;
                    return true;
                case "DanScene":
                    if (forceDanGeofence == value) return false;
                    forceDanGeofence = value;
                    return true;
            }
        }

        return false;
    }
}
