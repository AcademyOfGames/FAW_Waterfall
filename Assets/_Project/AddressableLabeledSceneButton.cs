using TMPro;
using UnityEngine;

/// <summary>
/// Hidden unlock: tap the wired button <see cref="tapsRequired"/> times to show a developer message,
/// prefetch the labeled Addressables scene (default <c>dev</c>), then use the normal START button when ready.
/// Must use the same <see cref="AddressableLoadingManager"/> as <see cref="GeofenceExperienceCoordinator"/>
/// (assigned via <see cref="Bind"/> — do not rely on FindFirstObjectByType, which can hit legacy menu buttons).
/// </summary>
[DisallowMultipleComponent]
public class AddressableLabeledSceneButton : MonoBehaviour
{
    public const string DevSceneName = "devScene";

    [SerializeField] private string addressableLabel = "dev";
    [SerializeField] private int tapsRequired = 5;
    [Tooltip("If > 0, tap count resets when gaps between taps exceed this many seconds.")]
    [SerializeField] private float tapSequenceTimeoutSeconds = 3f;
    [SerializeField] private string unlockMessage =
        "Developer scene unlocked. Press START when done loading addressable.";

    [SerializeField] private AddressableLoadingManager addressables;
    [SerializeField] private GeofenceHudView hud;
    [Tooltip("Used when Geofence Hud View is not assigned.")]
    [SerializeField] private TextMeshProUGUI fallbackMessageText;

    private int _tapCount;
    private float _lastTapUnscaledTime;

    public bool IsUnlocked { get; private set; }
    public string AddressableLabel => addressableLabel;

    private void Start()
    {
        EnsureBoundToGeofenceCoordinator();
    }

    /// <summary>Called by <see cref="GeofenceExperienceCoordinator"/> so downloads use the shared manager.</summary>
    public void Bind(GeofenceExperienceCoordinator coordinator, AddressableLoadingManager manager, GeofenceHudView hudView)
    {
        if (manager != null)
            addressables = manager;
        if (hudView != null)
            hud = hudView;

        if (IsUnlocked && addressables != null)
            addressables.BeginOrContinueDownload(addressableLabel);
    }

    private void EnsureBoundToGeofenceCoordinator()
    {
        if (addressables != null)
            return;

        var coordinator = FindFirstObjectByType<GeofenceExperienceCoordinator>();
        if (coordinator != null)
            coordinator.BindDeveloperSceneUnlock(this);
        else
            Debug.LogWarning("[FAW] AddressableLabeledSceneButton: GeofenceExperienceCoordinator not found.");
    }

    /// <summary>Wire to the hidden UI Button onClick (each tap).</summary>
    public void RegisterUnlockTap()
    {
        if (IsUnlocked)
            return;

        EnsureBoundToGeofenceCoordinator();

        if (tapSequenceTimeoutSeconds > 0f &&
            _tapCount > 0 &&
            Time.unscaledTime - _lastTapUnscaledTime > tapSequenceTimeoutSeconds)
        {
            _tapCount = 0;
        }

        _lastTapUnscaledTime = Time.unscaledTime;
        _tapCount++;

        if (_tapCount < tapsRequired)
            return;

        UnlockDeveloperScene();
    }

    private void UnlockDeveloperScene()
    {
        IsUnlocked = true;
        _tapCount = 0;
        EnsureBoundToGeofenceCoordinator();

        if (addressables == null)
        {
            Debug.LogError("[FAW] AddressableLabeledSceneButton: shared AddressableLoadingManager not bound.");
            ShowUnlockMessage("Developer scene unlock failed (missing Addressables).");
            return;
        }

        Debug.Log(
            $"[FAW] Developer scene unlocked — prefetch label='{addressableLabel}' " +
            $"managerId={addressables.GetInstanceID()} activeLabel='{addressables.ActiveLabel}' state={addressables.State}");
        ShowUnlockMessage(unlockMessage);
        addressables.BeginOrContinueDownload(addressableLabel);
    }

    private void ShowUnlockMessage(string text)
    {
        if (hud != null)
        {
            hud.SetUserMessage(text, null);
            return;
        }

        if (fallbackMessageText != null)
            fallbackMessageText.text = text;
    }
}
