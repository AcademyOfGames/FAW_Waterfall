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
    [Tooltip("UnityEngine.Debug.Log — filter logcat with \"FAW\" for download progress and failures.")]
    [SerializeField] private bool debugLogAddressables = true;
    [Tooltip("While CheckingSize or Downloading, log progress at most this often.")]
    [SerializeField] private float downloadProgressLogSeconds = 5f;

    private string _activeLabel;
    private float _downloadPhaseStartedAt;
    private AddressableDownloadState _state = AddressableDownloadState.Idle;
    private AsyncOperationHandle _downloadHandle;
    private Coroutine _downloadRoutine;
    private Coroutine _loadRoutine;

    public string ActiveLabel => _activeLabel;
    public AddressableDownloadState State => _state;

    /// <summary>True while a scene load started via <see cref="LoadSceneIfReady"/> (or legacy load) is still running.</summary>
    public bool IsSceneLoadInProgress => _loadRoutine != null;

    public event Action<string, string> OnNotify;

    /// <summary>Fired after <see cref="Addressables.LoadSceneAsync"/> completes successfully (label is the addressable key).</summary>
    public event Action<string> OnSceneLoadSucceeded;

    private void OnDestroy()
    {
        ReleaseDownloadHandleIfValid();
    }

    public void InitiateDownload()
    {
        DownloadAndLoad(addressableLabel);
    }

    /// <summary>Downloads dependencies for <paramref name="label"/>, then loads that Addressables scene (legacy UI + dev buttons).</summary>
    public void DownloadAndLoad(string label)
    {
        if (string.IsNullOrEmpty(label))
        {
            Notify("Missing addressable label.", "DownloadAndLoad label is empty.");
            return;
        }

        BeginOrContinueDownload(label);
        if (_loadRoutine != null)
            StopCoroutine(_loadRoutine);
        _loadRoutine = StartCoroutine(LoadAfterDownloadLegacy(label));
    }

    public void BeginOrContinueDownload(string label)
    {
        if (string.IsNullOrEmpty(label))
        {
            AddrDebug("BeginOrContinueDownload ignored: label is empty");
            return;
        }

        if (string.Equals(_activeLabel, label, StringComparison.Ordinal) &&
            _state == AddressableDownloadState.Ready)
        {
            AddrDebug($"BeginOrContinueDownload skipped label='{label}' (already Ready)");
            return;
        }

        if (string.Equals(_activeLabel, label, StringComparison.Ordinal) && _downloadRoutine != null)
        {
            AddrDebug(
                $"BeginOrContinueDownload skipped label='{label}' (download already running state={_state} {DescribeDownloadHandle()})");
            return;
        }

        if (_downloadRoutine != null)
        {
            AddrDebug(
                $"BeginOrContinueDownload: cancelling in-flight download for '{_activeLabel}' state={_state} " +
                $"→ starting '{label}'");
            StopCoroutine(_downloadRoutine);
            _downloadRoutine = null;
        }

        ReleaseDownloadHandleIfValid();
        _activeLabel = label;
        _state = AddressableDownloadState.Idle;
        AddrDebug($"BeginOrContinueDownload: starting label='{label}'");
        _downloadRoutine = StartCoroutine(DownloadDependenciesRoutine(label));
    }

    /// <summary>One-line summary for geofence logs when START cannot appear yet.</summary>
    public string BuildStartBlockedSummary(string nearestLabel, bool inRange)
    {
        var ready = IsReadyForLabel(nearestLabel);
        var parts =
            $"inRange={inRange} addrReady={ready} managerState={_state} activeLabel='{_activeLabel ?? "(none)"}' " +
            $"nearestLabel='{nearestLabel}' downloadRoutine={(_downloadRoutine != null ? "running" : "null")} " +
            DescribeDownloadHandle();
        if (!string.IsNullOrEmpty(nearestLabel) &&
            !string.Equals(_activeLabel, nearestLabel, StringComparison.Ordinal))
        {
            parts +=
                " | LABEL MISMATCH: AddressableReadyStartLabel waits for ActiveLabel; manager may still be switching bundles";
        }

        if (_state == AddressableDownloadState.Failed)
            parts += " | download FAILED — check catalog/CCD/network; tap may need retry after moving";
        else if (_state == AddressableDownloadState.Downloading || _state == AddressableDownloadState.CheckingSize)
            parts += $" | phaseElapsed={Time.unscaledTime - _downloadPhaseStartedAt:F0}s";
        else if (_state == AddressableDownloadState.Idle && _downloadRoutine == null && !ready)
            parts += " | Idle with no routine — download may not have started (prefetch?)";

        return parts;
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
        _downloadPhaseStartedAt = Time.unscaledTime;
        AddrDebug($"download phase CheckingSize begin label='{label}'");
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

        var nextSizeLog = Time.unscaledTime;
        while (!sizeHandle.IsDone)
        {
            if (debugLogAddressables && Time.unscaledTime >= nextSizeLog)
            {
                nextSizeLog = Time.unscaledTime + downloadProgressLogSeconds;
                AddrDebug(
                    $"CheckingSize label='{label}' status={sizeHandle.Status} done={sizeHandle.IsDone} " +
                    $"elapsed={Time.unscaledTime - _downloadPhaseStartedAt:F0}s (slow? catalog/network)");
            }

            yield return null;
        }

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
        AddrDebug($"CheckingSize done label='{label}' sizeBytes={bytes} elapsed={Time.unscaledTime - _downloadPhaseStartedAt:F1}s");

        if (bytes == 0)
        {
            AddrDebug($"no download required label='{label}' (0 bytes, already cached) → Ready");
            _state = AddressableDownloadState.Ready;
            _downloadRoutine = null;
            yield break;
        }

        _state = AddressableDownloadState.Downloading;
        _downloadPhaseStartedAt = Time.unscaledTime;
        AddrDebug($"Downloading begin label='{label}' sizeBytes={bytes} ({bytes / (1024f * 1024f):F1} MB)");
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

        var nextDlLog = Time.unscaledTime;
        var lastPct = -1f;
        while (!_downloadHandle.IsDone)
        {
            if (debugLogAddressables && Time.unscaledTime >= nextDlLog)
            {
                nextDlLog = Time.unscaledTime + downloadProgressLogSeconds;
                var pct = _downloadHandle.PercentComplete;
                var elapsed = Time.unscaledTime - _downloadPhaseStartedAt;
                var stuckHint = pct <= 0.001f && elapsed >= downloadProgressLogSeconds
                    ? " STUCK AT 0%? — Wi‑Fi/cellular, CCD bucket, or invalid label in catalog"
                    : pct <= lastPct + 0.001f && elapsed >= downloadProgressLogSeconds * 2f
                        ? " NO PROGRESS? — connection dropped or remote host unreachable"
                        : string.Empty;
                lastPct = pct;
                AddrDebug(
                    $"Downloading label='{label}' progress={pct:P1} status={_downloadHandle.Status} " +
                    $"elapsed={elapsed:F0}s{stuckHint}");
            }

            yield return null;
        }

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
        AddrDebug(
            $"download finished label='{label}' → Ready (START can show when in geofence) " +
            $"elapsed={Time.unscaledTime - _downloadPhaseStartedAt:F1}s");
        _downloadRoutine = null;
    }

    private IEnumerator LoadSceneRoutine(string label)
    {
        if (SceneManager.GetActiveScene().name == GetSceneNameForActiveLabel(label))
        {
            Debug.Log($"[FAW] Addressables: LoadScene skipped (already in target) label='{label}' active='{SceneManager.GetActiveScene().name}'");
            _loadRoutine = null;
            yield break;
        }

        Debug.Log($"[FAW] Addressables: LoadSceneAsync begin label='{label}' from active='{SceneManager.GetActiveScene().name}'");
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
        else
        {
            var loaded = SceneManager.GetActiveScene().name;
            Debug.Log($"[FAW] Addressables: scene load OK label='{label}' activeScene='{loaded}'");
            OnSceneLoadSucceeded?.Invoke(label);
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

        if (string.Equals(label, "dev", StringComparison.Ordinal))
            return "devScene";

        return string.Empty;
    }

    private void Fail(string label, string user, object debug)
    {
        _state = AddressableDownloadState.Failed;
        var detail = $"Label '{label}': {debug}";
        Debug.LogError($"[FAW] Addressables: FAIL {user} | {detail} | {DescribeDownloadHandle()}");
        OnNotify?.Invoke(user, detail);
    }

    private void AddrDebug(string message)
    {
        if (debugLogAddressables)
            Debug.Log("[FAW] " + message);
    }

    private string DescribeDownloadHandle()
    {
        if (!_downloadHandle.IsValid())
            return "handle=invalid";
        return
            $"handle done={_downloadHandle.IsDone} pct={_downloadHandle.PercentComplete:P1} status={_downloadHandle.Status}";
    }

    private void Notify(string user, string debug)
    {
        if (!string.IsNullOrEmpty(debug))
            Debug.LogWarning($"[FAW] Addressables: {user} | {debug}");
        else
            Debug.LogWarning($"[FAW] Addressables: {user}");
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
