using System;
using System.Collections;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.Serialization;

public enum SceneLoadState
{
    Idle,
    Ready,
    Loading,
    Failed
}

/// <summary>
/// Prepares and loads experience scenes from the Editor build list via <see cref="SceneManager"/>.
/// </summary>
public class ExperienceSceneLoadingManager : MonoBehaviour
{
    [FormerlySerializedAs("addressableLabel")]
    [Tooltip("Legacy field ignored — scenes are resolved by name from geofence definitions.")]
    [SerializeField] private string legacySceneLabel;

    [FormerlySerializedAs("postDownloadDelaySeconds")]
    [SerializeField] private float postLoadDelaySeconds;

    [FormerlySerializedAs("debugLogAddressables")]
    [SerializeField] private bool debugLogScenes = true;

    private string _activeSceneName;
    private SceneLoadState _state = SceneLoadState.Idle;
    private Coroutine _loadRoutine;

    public string ActiveSceneName => _activeSceneName;
    public SceneLoadState State => _state;
    public bool IsSceneLoadInProgress => _loadRoutine != null;

    public event Action<string, string> OnNotify;
    public event Action<string> OnSceneLoadSucceeded;

    public void InitiateDownload()
    {
        if (!string.IsNullOrEmpty(legacySceneLabel))
            DownloadAndLoad(legacySceneLabel);
    }

    /// <summary>Legacy UI entry — <paramref name="sceneOrLabel"/> is treated as a scene name when possible.</summary>
    public void DownloadAndLoad(string sceneOrLabel)
    {
        var sceneName = ResolveSceneName(sceneOrLabel);
        if (string.IsNullOrEmpty(sceneName))
        {
            Notify("Missing scene name.", "DownloadAndLoad scene name is empty.");
            return;
        }

        PrepareScene(sceneName);
        if (_loadRoutine != null)
            StopCoroutine(_loadRoutine);
        _loadRoutine = StartCoroutine(LoadAfterPrepareLegacy(sceneName));
    }

    public void PrepareScene(string sceneName)
    {
        if (string.IsNullOrEmpty(sceneName))
        {
            SceneDebug("PrepareScene ignored: scene name is empty");
            return;
        }

        if (!IsSceneInBuildSettings(sceneName))
        {
            Fail(sceneName, "Scene is not in Build Settings.", $"Add '{sceneName}' to File → Build Settings.");
            return;
        }

        if (string.Equals(_activeSceneName, sceneName, StringComparison.Ordinal) &&
            _state == SceneLoadState.Ready)
        {
            SceneDebug($"PrepareScene skipped scene='{sceneName}' (already Ready)");
            return;
        }

        _activeSceneName = sceneName;
        _state = SceneLoadState.Ready;
        SceneDebug($"PrepareScene scene='{sceneName}' → Ready");
    }

    public string BuildStartBlockedSummary(string nearestSceneName, bool inRange)
    {
        var ready = IsReadyForScene(nearestSceneName);
        var parts =
            $"inRange={inRange} sceneReady={ready} managerState={_state} activeScene='{_activeSceneName ?? "(none)"}' " +
            $"nearestScene='{nearestSceneName}' loadRoutine={(_loadRoutine != null ? "running" : "null")}";

        if (!string.IsNullOrEmpty(nearestSceneName) &&
            !string.Equals(_activeSceneName, nearestSceneName, StringComparison.Ordinal))
        {
            parts +=
                " | SCENE MISMATCH: START UI waits for ActiveSceneName; manager may still be switching targets";
        }

        if (_state == SceneLoadState.Failed)
            parts += " | scene load FAILED — check Build Settings and scene name spelling";
        else if (_state == SceneLoadState.Loading)
            parts += " | scene load in progress";
        else if (_state == SceneLoadState.Idle && !ready)
            parts += " | Idle — call PrepareScene for the nearest experience";

        return parts;
    }

    public bool IsReadyForScene(string sceneName)
    {
        return _state == SceneLoadState.Ready &&
               string.Equals(_activeSceneName, sceneName, StringComparison.Ordinal);
    }

    public bool IsFailedForScene(string sceneName)
    {
        return _state == SceneLoadState.Failed &&
               string.Equals(_activeSceneName, sceneName, StringComparison.Ordinal);
    }

    public void LoadSceneIfReady(string sceneName)
    {
        sceneName = ResolveSceneName(sceneName);
        if (!IsReadyForScene(sceneName))
            return;

        if (_loadRoutine != null)
            StopCoroutine(_loadRoutine);
        _loadRoutine = StartCoroutine(LoadSceneRoutine(sceneName));
    }

    private IEnumerator LoadAfterPrepareLegacy(string sceneName)
    {
        while (!IsReadyForScene(sceneName) && _state != SceneLoadState.Failed)
            yield return null;

        if (!IsReadyForScene(sceneName))
        {
            if (IsFailedForScene(sceneName))
                Notify("Could not prepare experience.", $"Prepare failed for scene '{sceneName}'.");
            yield break;
        }

        if (postLoadDelaySeconds > 0f)
            yield return new WaitForSeconds(postLoadDelaySeconds);
        yield return LoadSceneRoutine(sceneName);
    }

    private IEnumerator LoadSceneRoutine(string sceneName)
    {
        if (SceneManager.GetActiveScene().name == sceneName)
        {
            SceneDebug($"LoadScene skipped (already in target) scene='{sceneName}'");
            _loadRoutine = null;
            yield break;
        }

        _state = SceneLoadState.Loading;
        SceneDebug($"LoadSceneAsync begin scene='{sceneName}' from active='{SceneManager.GetActiveScene().name}'");

        AsyncOperation loadOp;
        try
        {
            loadOp = SceneManager.LoadSceneAsync(sceneName, LoadSceneMode.Single);
        }
        catch (Exception e)
        {
            Fail(sceneName, "Could not open experience.", $"LoadSceneAsync threw for '{sceneName}': {e}");
            _loadRoutine = null;
            yield break;
        }

        if (loadOp == null)
        {
            Fail(sceneName, "Could not open experience.", $"LoadSceneAsync returned null for '{sceneName}'.");
            _loadRoutine = null;
            yield break;
        }

        while (!loadOp.isDone)
            yield return null;

        var active = SceneManager.GetActiveScene().name;
        if (active == sceneName)
        {
            SceneDebug($"scene load OK scene='{sceneName}' activeScene='{active}'");
            _state = SceneLoadState.Ready;
            OnSceneLoadSucceeded?.Invoke(sceneName);
        }
        else
        {
            Fail(sceneName, "Experience failed to open.",
                $"LoadSceneAsync finished but active scene is '{active}', expected '{sceneName}'.");
        }

        _loadRoutine = null;
    }

    private static string ResolveSceneName(string sceneOrLabel)
    {
        if (string.IsNullOrEmpty(sceneOrLabel))
            return string.Empty;

        if (IsSceneInBuildSettings(sceneOrLabel))
            return sceneOrLabel;

        foreach (var def in ExperienceGeofenceDefinition.All)
        {
            if (string.Equals(def.SceneName, sceneOrLabel, StringComparison.Ordinal))
                return def.SceneName;
        }

        if (ExperienceGeofenceDefinition.TryGetSceneNameForLegacyLabel(sceneOrLabel, out var legacyScene))
            return legacyScene;

        if (string.Equals(sceneOrLabel, "dev", StringComparison.Ordinal))
            return DeveloperSceneUnlockButton.DevSceneName;

        return sceneOrLabel;
    }

    private static bool IsSceneInBuildSettings(string sceneName)
    {
        for (var i = 0; i < SceneManager.sceneCountInBuildSettings; i++)
        {
            var path = SceneUtility.GetScenePathByBuildIndex(i);
            if (string.IsNullOrEmpty(path))
                continue;
            var name = System.IO.Path.GetFileNameWithoutExtension(path);
            if (string.Equals(name, sceneName, StringComparison.Ordinal))
                return true;
        }

        return false;
    }

    private void Fail(string sceneName, string user, object debug)
    {
        _state = SceneLoadState.Failed;
        var detail = $"Scene '{sceneName}': {debug}";
        Debug.LogError($"[FAW] Scene load: FAIL {user} | {detail}");
        OnNotify?.Invoke(user, detail);
    }

    private void SceneDebug(string message)
    {
        if (debugLogScenes)
            Debug.Log("[FAW] " + message);
    }

    private void Notify(string user, string debug)
    {
        if (!string.IsNullOrEmpty(debug))
            Debug.LogWarning($"[FAW] Scene load: {user} | {debug}");
        else
            Debug.LogWarning($"[FAW] Scene load: {user}");
        OnNotify?.Invoke(user, debug);
    }
}
