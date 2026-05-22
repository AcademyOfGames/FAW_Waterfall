using TMPro;
using UnityEngine;
using UnityEngine.Serialization;

/// <summary>
/// Hidden unlock: tap the wired button <see cref="tapsRequired"/> times to show a developer message,
/// then use the normal START button to open <see cref="DevSceneName"/>.
/// </summary>
[DisallowMultipleComponent]
public class DeveloperSceneUnlockButton : MonoBehaviour
{
    public const string DevSceneName = "devScene";

    [FormerlySerializedAs("addressableLabel")]
    [SerializeField] private string devSceneName = DevSceneName;

    [SerializeField] private int tapsRequired = 5;
    [Tooltip("If > 0, tap count resets when gaps between taps exceed this many seconds.")]
    [SerializeField] private float tapSequenceTimeoutSeconds = 3f;
    [SerializeField] private string unlockMessage =
        "Developer scene unlocked. Press START to open the developer scene.";

    [FormerlySerializedAs("addressables")]
    [SerializeField] private ExperienceSceneLoadingManager sceneLoader;
    [SerializeField] private GeofenceHudView hud;
    [Tooltip("Used when Geofence Hud View is not assigned.")]
    [SerializeField] private TextMeshProUGUI fallbackMessageText;

    private int _tapCount;
    private float _lastTapUnscaledTime;

    public bool IsUnlocked { get; private set; }
    public string DeveloperSceneName =>
        string.IsNullOrEmpty(devSceneName) ||
        string.Equals(devSceneName, "dev", System.StringComparison.Ordinal)
            ? DevSceneName
            : devSceneName;

    private void Start()
    {
        EnsureBoundToGeofenceCoordinator();
    }

    public void Bind(GeofenceExperienceCoordinator coordinator, ExperienceSceneLoadingManager manager, GeofenceHudView hudView)
    {
        if (manager != null)
            sceneLoader = manager;
        if (hudView != null)
            hud = hudView;

        if (IsUnlocked && sceneLoader != null)
            sceneLoader.PrepareScene(DeveloperSceneName);
    }

    private void EnsureBoundToGeofenceCoordinator()
    {
        if (sceneLoader != null)
            return;

        var coordinator = FindFirstObjectByType<GeofenceExperienceCoordinator>();
        if (coordinator != null)
            coordinator.BindDeveloperSceneUnlock(this);
        else
            Debug.LogWarning("[FAW] DeveloperSceneUnlockButton: GeofenceExperienceCoordinator not found.");
    }

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

        if (sceneLoader == null)
        {
            Debug.LogError("[FAW] DeveloperSceneUnlockButton: shared ExperienceSceneLoadingManager not bound.");
            ShowUnlockMessage("Developer scene unlock failed (missing scene loader).");
            return;
        }

        Debug.Log(
            $"[FAW] Developer scene unlocked — prepare scene='{DeveloperSceneName}' " +
            $"managerId={sceneLoader.GetInstanceID()} activeScene='{sceneLoader.ActiveSceneName}' state={sceneLoader.State}");
        ShowUnlockMessage(unlockMessage);
        sceneLoader.PrepareScene(DeveloperSceneName);
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
