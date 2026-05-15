using System.Collections;
using UnityEngine;
using UnityEngine.SceneManagement;
#if UNITY_ANDROID
using UnityEngine.Android;
#endif

/// <summary>
/// Keeps nearest experience prefetching via Addressables and loads when the user enters the geofence radius.
/// Expects an <see cref="AddressableLoadingManager"/> on the same GameObject (or assign explicitly).
/// </summary>
[DisallowMultipleComponent]
public class GeofenceExperienceCoordinator : MonoBehaviour
{
    [SerializeField] private AddressableLoadingManager addressables;
    [SerializeField] private GeofenceHudView hud;
    [Tooltip("Shown while in geofence and nearest bundle is not ready. Often the same object as Geofence Hud View → Loading Widget Root.")]
    [SerializeField] private GameObject loadingWidget;
    [SerializeField] private float pollSeconds = 0.75f;
    [SerializeField] private bool dontDestroyOnLoad = true;
    [SerializeField] private bool buildRuntimeUiIfMissing = true;
    [Tooltip("UnityEngine.Debug.Log — visible in Android logcat / Xcode device console.")]
    [SerializeField] private bool debugLogGeofence = true;
    [Tooltip("TMP rich-text color for the nearest experience name (bold is applied in code).")]
    [SerializeField] private Color nearestExperienceNameColor = new Color32(0x2E, 0x8B, 0xFF, 0xFF);

    private float _nextPoll;
    private float _nextLocationWaitLog;
    private float _nextInvalidGpsLog;
    private Coroutine _locationStart;
    private string _lastPrefetchLabel;

    private void Awake()
    {
        if (dontDestroyOnLoad)
            DontDestroyOnLoad(gameObject);

        if (addressables == null)
            addressables = GetComponent<AddressableLoadingManager>();
        if (addressables == null)
        {
            Debug.LogError("[Geofence] AddressableLoadingManager missing on GeofenceRuntime.");
            enabled = false;
            return;
        }

        addressables.OnNotify += OnAddressablesNotify;

        if (hud == null && buildRuntimeUiIfMissing)
            hud = GeofenceRuntimeUiBuilder.Build(transform);

        if (loadingWidget == null && hud != null)
            loadingWidget = hud.LoadingWidgetRoot;

        GeoDebug(
            $"Awake scene={SceneManager.GetActiveScene().name} addressables=ok hud={(hud != null ? hud.name : "null")} " +
            $"buildRuntimeUiIfMissing={buildRuntimeUiIfMissing} loadingWidget={(loadingWidget != null ? "ok" : "null")} " +
            $"pollSeconds={pollSeconds} enterRadiusKm={ExperienceGeofenceDefinition.EnterGeofenceKm}");
    }

    private void OnDestroy()
    {
        if (addressables != null)
            addressables.OnNotify -= OnAddressablesNotify;
    }

    private void Start()
    {
        if (_locationStart == null)
            _locationStart = StartCoroutine(StartLocationService());
        GeoDebug("Start: location coroutine scheduled.");
    }

    private void Update()
    {
        if (Input.location.status != LocationServiceStatus.Running)
        {
            if (debugLogGeofence && Time.unscaledTime >= _nextLocationWaitLog)
            {
                _nextLocationWaitLog = Time.unscaledTime + 2f;
                GeoDebug($"Waiting for GPS: LocationServiceStatus={Input.location.status} (polls begin when Running)");
            }

            return;
        }

        if (Time.unscaledTime < _nextPoll)
            return;
        _nextPoll = Time.unscaledTime + pollSeconds;

        var loc = Input.location.lastData;
        if (loc.latitude == 0f && loc.longitude == 0f)
        {
            if (debugLogGeofence && Time.unscaledTime >= _nextInvalidGpsLog)
            {
                _nextInvalidGpsLog = Time.unscaledTime + 3f;
                GeoDebug("lastData lat/lon are 0,0 — skipping distance until device reports a fix.");
            }

            return;
        }

        var nearest = FindNearest(loc.latitude, loc.longitude, out var distanceKm);
        hud?.SetNearestExperienceStatus(distanceKm, nearest.ExperienceName, nearestExperienceNameColor);

        if (!string.Equals(_lastPrefetchLabel, nearest.AddressableLabel, System.StringComparison.Ordinal))
        {
            _lastPrefetchLabel = nearest.AddressableLabel;
            GeoDebug($"Prefetch target changed → label='{nearest.AddressableLabel}' scene='{nearest.SceneName}'");
            addressables.BeginOrContinueDownload(nearest.AddressableLabel);
        }

        var inRange = distanceKm <= ExperienceGeofenceDefinition.EnterGeofenceKm;
        var ready = addressables.IsReadyForLabel(nearest.AddressableLabel);
        var activeScene = SceneManager.GetActiveScene().name;
        GeoDebug(
            $"tick lat={loc.latitude:F6} lon={loc.longitude:F6} hAcc={loc.horizontalAccuracy:F1}m " +
            $"nearest={nearest.ExperienceName} distKm={distanceKm:F3} enterKm={ExperienceGeofenceDefinition.EnterGeofenceKm:F4} " +
            $"inRange={inRange} addrState={addressables.State} addrLabel='{addressables.ActiveLabel}' ready={ready} " +
            $"activeScene='{activeScene}' targetScene='{nearest.SceneName}'");

        if (inRange)
        {
            if (ready)
            {
                if (loadingWidget != null)
                    loadingWidget.SetActive(false);

                if (activeScene != nearest.SceneName)
                {
                    GeoDebug($"In range + ready → LoadSceneIfReady('{nearest.AddressableLabel}')");
                    addressables.LoadSceneIfReady(nearest.AddressableLabel);
                }
                else
                    GeoDebug("In range + ready but already in target scene; no load.");
            }
            else
            {
                GeoDebug("In range but dependencies not ready — showing loading widget if assigned.");
                if (loadingWidget != null)
                    loadingWidget.SetActive(true);
            }
        }
        else if (loadingWidget != null)
        {
            loadingWidget.SetActive(false);
        }
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
        GeoDebug("StartLocationService: begin.");
#if UNITY_ANDROID
        if (!Permission.HasUserAuthorizedPermission(Permission.FineLocation))
        {
            GeoDebug("Android: requesting FineLocation permission.");
            Permission.RequestUserPermission(Permission.FineLocation);
            yield return new WaitForSeconds(2f);
        }
        else
            GeoDebug("Android: FineLocation already granted.");
#endif
        if (!Input.location.isEnabledByUser)
        {
            GeoDebug("Location blocked: Input.location.isEnabledByUser is false (OS location off or denied).");
            hud?.SetUserMessage("Location is off. Turn on location to find experiences.",
                "Input.location.isEnabledByUser is false.");
            yield break;
        }

        GeoDebug("Input.location.Start(10m, 10m) …");
        Input.location.Start(10f, 10f);
        var wait = 0;
        while (Input.location.status == LocationServiceStatus.Initializing && wait < 30)
        {
            wait++;
            GeoDebug($"Location initializing… {wait}s status={Input.location.status}");
            yield return new WaitForSeconds(1f);
        }

        if (Input.location.status != LocationServiceStatus.Running)
        {
            GeoDebug($"Location failed to start. Final status={Input.location.status}");
            hud?.SetUserMessage("Could not start location.",
                $"LocationServiceStatus: {Input.location.status}");
            yield break;
        }

        GeoDebug("Location service Running — Update() will poll for geofences.");
        hud?.SetUserMessage(string.Empty, "Location service running.");
    }

    private void OnAddressablesNotify(string user, string debug)
    {
        GeoDebug($"Addressables notify user='{user}' debug='{debug}'");
        hud?.SetUserMessage(user, debug);
    }

    private void GeoDebug(string message)
    {
        if (debugLogGeofence)
            Debug.Log("[Geofence] " + message);
    }
}
