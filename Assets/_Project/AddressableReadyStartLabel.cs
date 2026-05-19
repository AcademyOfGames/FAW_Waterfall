using TMPro;
using UnityEngine;
using UnityEngine.UI;

/// <summary>
/// Keeps <see cref="LoadingSequenceAnimator"/> running on the label until Addressables are ready for
/// <see cref="AddressableLoadingManager.ActiveLabel"/> and, when a <see cref="GeofenceExperienceCoordinator"/> is present,
/// the user is within the nearest experience enter radius. Then disables the sequence, sets the label to START, and
/// enables the optional <see cref="Button"/>.
/// </summary>
[DisallowMultipleComponent]
public class AddressableReadyStartLabel : MonoBehaviour
{
    [SerializeField] private AddressableLoadingManager addressables;
    [Tooltip("If set (or found on this GameObject), START is shown only when within geofence enter radius.")]
    [SerializeField] private GeofenceExperienceCoordinator geofenceCoordinator;
    [Tooltip("When unlocked, START is shown when the dev label is ready (no geofence required).")]
    [SerializeField] private AddressableLabeledSceneButton developerSceneUnlock;
    [SerializeField] private LoadingSequenceAnimator loadingSequence;
    [Tooltip("If null, uses LoadingSequenceAnimator's loading text field.")]
    [SerializeField] private TextMeshProUGUI label;
    [SerializeField] private string readyText = "START";
    [Tooltip("If set, interactable only when dependencies are ready and (if geofence is present) within enter radius.")]
    [SerializeField] private Button optionalButton;
    [Tooltip("Log when START appears/disappears (filter logcat with \"FAW\").")]
    [SerializeField] private bool debugLogStartLabel = true;

    private bool _showingReady;
    private string _lastNearestLabelForLog;

    private void Awake()
    {
        if (addressables == null)
            addressables = GetComponent<AddressableLoadingManager>();
        if (addressables == null)
            addressables = GetComponentInParent<AddressableLoadingManager>();

        if (geofenceCoordinator == null)
            geofenceCoordinator = GetComponent<GeofenceExperienceCoordinator>();
        if (geofenceCoordinator == null)
            geofenceCoordinator = GetComponentInParent<GeofenceExperienceCoordinator>();

        if (geofenceCoordinator != null && addressables == null)
            addressables = geofenceCoordinator.SharedAddressables;

        if (developerSceneUnlock == null)
            developerSceneUnlock = FindFirstObjectByType<AddressableLabeledSceneButton>();

        if (loadingSequence != null && label == null)
            label = loadingSequence.LoadingText;

        if (optionalButton != null)
            optionalButton.interactable = false;
    }

    private void LateUpdate()
    {
        if (addressables == null)
            return;

        var devUnlocked = developerSceneUnlock != null && developerSceneUnlock.IsUnlocked;
        var inRange = geofenceCoordinator == null || geofenceCoordinator.IsNearestWithinEnterRadius;

        bool showStartUi;
        if (devUnlocked)
        {
            showStartUi = addressables.IsReadyForLabel(developerSceneUnlock.AddressableLabel);
        }
        else
        {
            var active = addressables.ActiveLabel;
            var addressablesReady = !string.IsNullOrEmpty(active) && addressables.IsReadyForLabel(active);
            showStartUi = addressablesReady && inRange;
        }

        if (showStartUi == _showingReady)
            return;

        _showingReady = showStartUi;
        if (debugLogStartLabel && addressables != null)
        {
            if (showStartUi)
            {
                Debug.Log(devUnlocked
                    ? "[FAW] START UI: showing START (dev scene ready)"
                    : "[FAW] START UI: showing START (Addressables ready + in geofence)");
            }
            else
            {
                var summaryLabel = devUnlocked
                    ? developerSceneUnlock.AddressableLabel
                    : addressables.ActiveLabel;
                if (!devUnlocked)
                {
                    if (geofenceCoordinator != null && geofenceCoordinator.TryGetLastNearestLabel(out var nl))
                    {
                        summaryLabel = nl;
                        _lastNearestLabelForLog = nl;
                    }
                    else if (!string.IsNullOrEmpty(_lastNearestLabelForLog))
                        summaryLabel = _lastNearestLabelForLog;
                }

                Debug.Log(
                    "[FAW] START UI: hiding START — " +
                    addressables.BuildStartBlockedSummary(summaryLabel, devUnlocked || inRange));
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
