using System;
using System.Collections;
using UnityEngine;
using UnityEngine.AddressableAssets;
using UnityEngine.ResourceManagement.AsyncOperations;
using UnityEngine.ResourceManagement.ResourceProviders;
using UnityEngine.SceneManagement;

public enum AddressableDownloadState
{
    Idle,
    CheckingSize,
    Downloading,
    Ready,
    Failed
}

public class AddressableLoadingManager : MonoBehaviour
{
    [Tooltip("Used by InitiateDownload() for legacy UI buttons.")]
    public string addressableLabel;

    [SerializeField] private float postDownloadDelaySeconds = 2f;

    private string _activeLabel;
    private AddressableDownloadState _state = AddressableDownloadState.Idle;
    private AsyncOperationHandle _downloadHandle;
    private Coroutine _downloadRoutine;
    private Coroutine _loadRoutine;

    public string ActiveLabel => _activeLabel;
    public AddressableDownloadState State => _state;

    public event Action<string, string> OnNotify;

    private void OnDestroy()
    {
        ReleaseDownloadHandleIfValid();
    }

    public void InitiateDownload()
    {
        if (string.IsNullOrEmpty(addressableLabel))
        {
            Notify("Missing addressable label on this button.", "AddressableLoadingManager.addressableLabel is empty.");
            return;
        }

        BeginOrContinueDownload(addressableLabel);
        if (_loadRoutine != null)
            StopCoroutine(_loadRoutine);
        _loadRoutine = StartCoroutine(LoadAfterDownloadLegacy(addressableLabel));
    }

    public void BeginOrContinueDownload(string label)
    {
        if (string.IsNullOrEmpty(label))
            return;

        if (string.Equals(_activeLabel, label, StringComparison.Ordinal) &&
            _state == AddressableDownloadState.Ready)
            return;

        if (string.Equals(_activeLabel, label, StringComparison.Ordinal) && _downloadRoutine != null)
            return;

        if (_downloadRoutine != null)
        {
            StopCoroutine(_downloadRoutine);
            _downloadRoutine = null;
        }

        ReleaseDownloadHandleIfValid();
        _activeLabel = label;
        _state = AddressableDownloadState.Idle;
        _downloadRoutine = StartCoroutine(DownloadDependenciesRoutine(label));
    }

    public bool IsReadyForLabel(string label)
    {
        return _state == AddressableDownloadState.Ready &&
               string.Equals(_activeLabel, label, StringComparison.Ordinal);
    }

    public bool IsFailedForLabel(string label)
    {
        return _state == AddressableDownloadState.Failed &&
               string.Equals(_activeLabel, label, StringComparison.Ordinal);
    }

    public void LoadSceneIfReady(string label)
    {
        if (!IsReadyForLabel(label))
            return;

        if (_loadRoutine != null)
            StopCoroutine(_loadRoutine);
        _loadRoutine = StartCoroutine(LoadSceneRoutine(label));
    }

    private IEnumerator LoadAfterDownloadLegacy(string label)
    {
        while (_activeLabel == label &&
               (_state == AddressableDownloadState.Idle ||
                _state == AddressableDownloadState.CheckingSize ||
                _state == AddressableDownloadState.Downloading))
        {
            yield return null;
        }

        if (!IsReadyForLabel(label))
        {
            if (IsFailedForLabel(label))
                Notify("Could not prepare experience.", $"Download failed for label '{label}'.");
            yield break;
        }

        yield return new WaitForSeconds(postDownloadDelaySeconds);
        yield return LoadSceneRoutine(label);
    }

    private IEnumerator DownloadDependenciesRoutine(string label)
    {
        _state = AddressableDownloadState.CheckingSize;
        AsyncOperationHandle<long> sizeHandle = default;
        try
        {
            sizeHandle = Addressables.GetDownloadSizeAsync(label);
        }
        catch (Exception e)
        {
            Fail(label, "Addressables check failed.", e);
            _downloadRoutine = null;
            yield break;
        }

        yield return sizeHandle;

        if (sizeHandle.Status != AsyncOperationStatus.Succeeded)
        {
            var msg = sizeHandle.OperationException != null ? sizeHandle.OperationException.Message : "unknown";
            Addressables.Release(sizeHandle);
            Fail(label, "Could not check download size.", msg);
            _downloadRoutine = null;
            yield break;
        }

        var bytes = sizeHandle.Result;
        Addressables.Release(sizeHandle);

        if (bytes == 0)
        {
            Debug.Log($"[AddressableLoadingManager] No download required for label '{label}' (0 bytes).");
            _state = AddressableDownloadState.Ready;
            _downloadRoutine = null;
            yield break;
        }

        _state = AddressableDownloadState.Downloading;
        try
        {
            _downloadHandle = Addressables.DownloadDependenciesAsync(label);
        }
        catch (Exception e)
        {
            Fail(label, "Could not start download.", e);
            _downloadRoutine = null;
            yield break;
        }

        yield return _downloadHandle;

        if (_downloadHandle.Status != AsyncOperationStatus.Succeeded)
        {
            var msg = _downloadHandle.OperationException != null
                ? _downloadHandle.OperationException.Message
                : "unknown";
            ReleaseDownloadHandleIfValid();
            Fail(label, "Download did not finish successfully.", msg);
            _downloadRoutine = null;
            yield break;
        }

        ReleaseDownloadHandleIfValid();
        _state = AddressableDownloadState.Ready;
        Debug.Log($"[AddressableLoadingManager] Dependencies ready for label '{label}'.");
        _downloadRoutine = null;
    }

    private IEnumerator LoadSceneRoutine(string label)
    {
        if (SceneManager.GetActiveScene().name == GetSceneNameForActiveLabel(label))
        {
            Debug.Log($"[AddressableLoadingManager] Already in scene for '{label}', skipping load.");
            _loadRoutine = null;
            yield break;
        }

        AsyncOperationHandle<SceneInstance> loadHandle = default;
        try
        {
            loadHandle = Addressables.LoadSceneAsync(label, LoadSceneMode.Single);
        }
        catch (Exception e)
        {
            Notify("Could not open experience.", $"LoadSceneAsync threw for label '{label}': {e}");
            _loadRoutine = null;
            yield break;
        }

        yield return loadHandle;

        if (loadHandle.Status != AsyncOperationStatus.Succeeded)
        {
            var msg = loadHandle.OperationException != null ? loadHandle.OperationException.Message : "unknown";
            if (loadHandle.IsValid())
                Addressables.Release(loadHandle);
            Notify("Experience failed to open.", $"LoadSceneAsync failed for label '{label}': {msg}");
        }

        _loadRoutine = null;
    }

    private static string GetSceneNameForActiveLabel(string label)
    {
        foreach (var def in ExperienceGeofenceDefinition.All)
        {
            if (string.Equals(def.AddressableLabel, label, StringComparison.Ordinal))
                return def.SceneName;
        }

        return string.Empty;
    }

    private void Fail(string label, string user, object debug)
    {
        _state = AddressableDownloadState.Failed;
        Notify(user, $"Label '{label}': {debug}");
    }

    private void Notify(string user, string debug)
    {
        OnNotify?.Invoke(user, debug);
    }

    private void ReleaseDownloadHandleIfValid()
    {
        if (_downloadHandle.IsValid())
        {
            Addressables.Release(_downloadHandle);
            _downloadHandle = default;
        }
    }
}
