using TMPro;
using UnityEngine;
using UnityEngine.Serialization;
using UnityEngine.UI;

/// <summary>
/// Keeps <see cref="LoadingSequenceAnimator"/> running until the active experience scene is prepared and,
/// when a <see cref="GeofenceExperienceCoordinator"/> is present, the user is within the enter radius.
/// </summary>
[DisallowMultipleComponent]
public class ExperienceReadyStartLabel : MonoBehaviour
{
    [FormerlySerializedAs("addressables")]
    [SerializeField] private ExperienceSceneLoadingManager sceneLoader;
    [SerializeField] private GeofenceExperienceCoordinator geofenceCoordinator;
    [SerializeField] private DeveloperSceneUnlockButton developerSceneUnlock;
    [SerializeField] private LoadingSequenceAnimator loadingSequence;
    [Tooltip("If null, uses LoadingSequenceAnimator's loading text field.")]
    [SerializeField] private TextMeshProUGUI label;
    [SerializeField] private string readyText = "START";
    [SerializeField] private Button optionalButton;
    [SerializeField] private bool debugLogStartLabel = false;

    private bool _showingReady;
    private string _lastNearestSceneForLog;

    private void Awake()
    {
        if (sceneLoader == null)
            sceneLoader = GetComponent<ExperienceSceneLoadingManager>();
        if (sceneLoader == null)
            sceneLoader = GetComponentInParent<ExperienceSceneLoadingManager>();

        if (geofenceCoordinator == null)
            geofenceCoordinator = GetComponent<GeofenceExperienceCoordinator>();
        if (geofenceCoordinator == null)
            geofenceCoordinator = GetComponentInParent<GeofenceExperienceCoordinator>();

        if (geofenceCoordinator != null && sceneLoader == null)
            sceneLoader = geofenceCoordinator.SharedSceneLoader;

        if (developerSceneUnlock == null)
            developerSceneUnlock = FindFirstObjectByType<DeveloperSceneUnlockButton>();

        if (loadingSequence != null && label == null)
            label = loadingSequence.LoadingText;

        if (optionalButton != null)
            optionalButton.interactable = false;
    }

    private void LateUpdate()
    {
        if (sceneLoader == null)
            return;

        var devUnlocked = developerSceneUnlock != null && developerSceneUnlock.IsUnlocked;
        var inRange = geofenceCoordinator == null || geofenceCoordinator.IsNearestWithinEnterRadius;

        bool showStartUi;
        if (devUnlocked)
        {
            showStartUi = sceneLoader.IsReadyForScene(developerSceneUnlock.DeveloperSceneName);
        }
        else
        {
            var active = sceneLoader.ActiveSceneName;
            var sceneReady = !string.IsNullOrEmpty(active) && sceneLoader.IsReadyForScene(active);
            showStartUi = sceneReady && inRange;
        }

        if (showStartUi == _showingReady)
            return;

        _showingReady = showStartUi;
        if (debugLogStartLabel)
        {
            if (showStartUi)
            {
                Debug.Log(devUnlocked
                    ? "[FAW] START UI: showing START (dev scene ready)"
                    : "[FAW] START UI: showing START (scene ready + in geofence)");
            }
            else
            {
                var summaryScene = devUnlocked
                    ? developerSceneUnlock.DeveloperSceneName
                    : sceneLoader.ActiveSceneName;
                if (!devUnlocked)
                {
                    if (geofenceCoordinator != null && geofenceCoordinator.TryGetLastNearestSceneName(out var ns))
                    {
                        summaryScene = ns;
                        _lastNearestSceneForLog = ns;
                    }
                    else if (!string.IsNullOrEmpty(_lastNearestSceneForLog))
                        summaryScene = _lastNearestSceneForLog;
                }

                Debug.Log(
                    "[FAW] START UI: hiding START — " +
                    sceneLoader.BuildStartBlockedSummary(summaryScene, devUnlocked || inRange));
            }
        }

        ApplyVisualState(showStartUi);
    }

    private void ApplyVisualState(bool showStartUi)
    {
        if (loadingSequence != null)
            loadingSequence.enabled = !showStartUi;

        var tmp = label != null ? label : loadingSequence != null ? loadingSequence.LoadingText : null;
        if (tmp != null && showStartUi)
            tmp.text = readyText;

        if (optionalButton != null)
            optionalButton.interactable = showStartUi;
    }
}
