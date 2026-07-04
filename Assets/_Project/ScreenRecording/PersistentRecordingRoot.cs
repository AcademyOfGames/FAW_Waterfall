using System;
using UnityEngine;
using UnityEngine.SceneManagement;

/// <summary>
/// Root for the whole screen-recording system: the plugin's "Screen Recorder" GameObject
/// (SmileSoftScreenRecordController) plus the recording Canvas (record button / preview / popup).
///
/// Why this exists: <see cref="ExperienceSceneLoadingManager"/> loads every experience with
/// <c>SceneManager.LoadSceneAsync(sceneName, LoadSceneMode.Single)</c>, which destroys everything
/// in the previously active scene. Recording needs to actually work *inside* the AR experience
/// scenes (Alina/Divine/Benaroya/etc.), not just on StartScene, so this whole subtree is marked
/// DontDestroyOnLoad and survives every scene swap instead of dying with StartScene.
///
/// Visibility: the recording Canvas is hidden on <see cref="hiddenOnScenes"/> (StartScene by
/// default — the main menu) and shown on every other scene, driven by
/// <c>SceneManager.activeSceneChanged</c>.
///
/// Setup: put this on an empty GameObject at the root of StartScene. Move the existing
/// "Screen Recorder" prefab instance and the recording Canvas underneath it, then drag the
/// Canvas into <see cref="recordingCanvasRoot"/>.
/// </summary>
public class PersistentRecordingRoot : MonoBehaviour
{
    [Tooltip("The recording Canvas (record button / preview / popup). The \"Screen Recorder\" " +
        "GameObject has no visuals and doesn't need to be listed here — it just rides along as a child.")]
    [SerializeField] private GameObject recordingCanvasRoot;

    [Tooltip("Scene names where the recording Canvas should be hidden. Defaults to just the main menu.")]
    [SerializeField] private string[] hiddenOnScenes = { "StartScene" };

    private static PersistentRecordingRoot _instance;

    private void Awake()
    {
        if (_instance != null && _instance != this)
        {
            Debug.Log("[PersistentRecordingRoot] Duplicate instance found (StartScene was reloaded) — " +
                "destroying the new one, keeping the original persistent instance.");
            Destroy(gameObject);
            return;
        }

        _instance = this;
        DontDestroyOnLoad(gameObject);
        Debug.Log("[PersistentRecordingRoot] Marked DontDestroyOnLoad — will survive scene loads.");

        if (recordingCanvasRoot == null)
            Debug.LogError("[PersistentRecordingRoot] Recording Canvas Root is not assigned — " +
                "visibility can't be controlled, the canvas will stay in whatever state it's saved as.");
    }

    private void OnEnable()
    {
        SceneManager.activeSceneChanged += HandleActiveSceneChanged;
        ApplyVisibility(SceneManager.GetActiveScene().name);
    }

    private void OnDisable()
    {
        SceneManager.activeSceneChanged -= HandleActiveSceneChanged;
    }

    private void HandleActiveSceneChanged(Scene previous, Scene next)
    {
        Debug.Log($"[PersistentRecordingRoot] Active scene changed: '{previous.name}' -> '{next.name}'.");
        ApplyVisibility(next.name);
    }

    private void ApplyVisibility(string sceneName)
    {
        bool hide = IsHiddenScene(sceneName);
        Debug.Log($"[PersistentRecordingRoot] Scene '{sceneName}': {(hide ? "hiding" : "showing")} the recording canvas.");

        if (recordingCanvasRoot != null)
            recordingCanvasRoot.SetActive(!hide);
    }

    private bool IsHiddenScene(string sceneName)
    {
        if (string.IsNullOrEmpty(sceneName) || hiddenOnScenes == null)
            return false;

        foreach (var hidden in hiddenOnScenes)
        {
            if (string.Equals(hidden, sceneName, StringComparison.Ordinal))
                return true;
        }
        return false;
    }
}
