using System;
using UnityEngine;
using UnityEngine.UI;
using UnityEngine.Video;

/// <summary>
/// Full-screen looping preview shown after a recording finishes.
///
/// Owns the X (discard, top-right) and Share (bottom-center, in the record button's spot)
/// buttons. Does NOT use SmileSoftScreenRecordController.PreviewVideo() — that opens the OS's
/// native full-screen player, which can't have custom buttons overlaid on it. Instead this plays
/// the file with a looping <see cref="VideoPlayer"/> onto <see cref="previewImage"/>.
///
/// Lives on a GameObject that stays permanently active (e.g. RecordCanvas itself) rather than on
/// the video/preview object — it needs to keep listening to
/// <see cref="ScreenRecordButtonController.OnRecordingFinished"/> even while previewRoot is
/// hidden, so it can't be on the thing that gets toggled off (that would unsubscribe itself the
/// first time the preview closes, and miss every recording after the first).
/// </summary>
public class RecordingPreviewController : MonoBehaviour
{
    [Header("Wiring")]
    [SerializeField] private ScreenRecordButtonController recordButtonController;
    [SerializeField] private SharePopupController sharePopupController;

    [Header("Required refs")]
    [Tooltip("Full-screen container shown/hidden for the preview. Usually the parent of previewImage.")]
    [SerializeField] private GameObject previewRoot;
    [SerializeField] private RawImage previewImage;
    [SerializeField] private VideoPlayer videoPlayer;
    [SerializeField] private Button discardButton;   // top-right X
    [SerializeField] private Button shareButton;      // bottom-center, replaces record button

    private RenderTexture _renderTexture;
    private string _currentFilePath;

    private void Awake()
    {
        ValidateRefs();

        if (videoPlayer != null)
        {
            videoPlayer.playOnAwake = false;
            videoPlayer.isLooping = true;
            videoPlayer.renderMode = VideoRenderMode.RenderTexture;
            videoPlayer.prepareCompleted += HandlePrepared;
            videoPlayer.errorReceived += HandleVideoError;
        }

        if (previewRoot != null)
            previewRoot.SetActive(false);
        if (shareButton != null) shareButton.gameObject.SetActive(false);
        if (discardButton != null) discardButton.gameObject.SetActive(false);
    }

    private void OnEnable()
    {
        if (recordButtonController != null)
        {
            recordButtonController.OnRecordingFinished += HandleRecordingFinished;
        }
        else
        {
            Debug.LogError("[RecordingPreviewController] OnEnable(): Record Button Controller is not " +
                "assigned — this preview will never be told a recording finished.");
        }

        if (discardButton != null)
            discardButton.onClick.AddListener(OnDiscardTapped);
        if (shareButton != null)
            shareButton.onClick.AddListener(OnShareTapped);
        if (sharePopupController != null)
            sharePopupController.OnActionCompleted += HandlePopupActionCompleted;
    }

    private void OnDisable()
    {
        if (recordButtonController != null)
            recordButtonController.OnRecordingFinished -= HandleRecordingFinished;
        if (discardButton != null)
            discardButton.onClick.RemoveListener(OnDiscardTapped);
        if (shareButton != null)
            shareButton.onClick.RemoveListener(OnShareTapped);
        if (sharePopupController != null)
            sharePopupController.OnActionCompleted -= HandlePopupActionCompleted;
    }

    private void OnDestroy()
    {
        if (_renderTexture != null)
        {
            if (videoPlayer != null && videoPlayer.targetTexture == _renderTexture)
                videoPlayer.targetTexture = null;
            _renderTexture.Release();
            Destroy(_renderTexture);
        }
    }

    // ── Recording finished → show preview ───────────────────────────────────

    private void HandleRecordingFinished(string path)
    {
        if (string.IsNullOrEmpty(path))
        {
            // ScreenRecordButtonController already logged the failure and reset itself
            // via gameObject.SetActive(false)+OnRecordingFinished — bring it back.
            Debug.LogWarning("[RecordingPreviewController] Empty path — resetting the record button instead of showing a preview.");
            recordButtonController?.ResetToIdle();
            return;
        }

        _currentFilePath = path;
        ShowPreview(path);
    }

    private void ShowPreview(string path)
    {
        if (previewRoot == null || videoPlayer == null || previewImage == null)
        {
            Debug.LogError("[RecordingPreviewController] Cannot show preview — required refs are missing " +
                $"(Preview Root null={previewRoot == null}, Video Player null={videoPlayer == null}, " +
                $"Preview Image null={previewImage == null}).");
            return;
        }

        EnsureRenderTexture();

        previewRoot.SetActive(true);
        // Share/Discard aren't necessarily children of previewRoot in the scene hierarchy
        // (they can be siblings), so show them explicitly rather than assuming nesting.
        if (shareButton != null) shareButton.gameObject.SetActive(true);
        if (discardButton != null) discardButton.gameObject.SetActive(true);
        videoPlayer.url = NormalizeToFileUrl(path);
        videoPlayer.Prepare();
    }

    private void HandlePrepared(VideoPlayer vp)
    {
        vp.Play();
    }

    private void HandleVideoError(VideoPlayer vp, string message)
    {
        Debug.LogError($"[RecordingPreviewController] VideoPlayer error for '{_currentFilePath}': {message}");
    }

    private void EnsureRenderTexture()
    {
        int w = Mathf.Max(2, Screen.width);
        int h = Mathf.Max(2, Screen.height);

        if (_renderTexture != null && (_renderTexture.width != w || _renderTexture.height != h))
        {
            videoPlayer.targetTexture = null;
            _renderTexture.Release();
            Destroy(_renderTexture);
            _renderTexture = null;
        }

        if (_renderTexture == null)
        {
            _renderTexture = new RenderTexture(w, h, 0);
            videoPlayer.targetTexture = _renderTexture;
            previewImage.texture = _renderTexture;
        }
    }

    // ── Buttons ──────────────────────────────────────────────────────────────

    private void OnDiscardTapped()
    {
        ClosePreview();
        recordButtonController?.ResetToIdle();
    }

    private void OnShareTapped()
    {
        if (sharePopupController == null)
        {
            Debug.LogError("[RecordingPreviewController] Share Popup Controller is not assigned.");
            return;
        }
        sharePopupController.Open(_currentFilePath);
    }

    /// <summary>Called when the share popup finishes a Save or Instagram-share action.</summary>
    private void HandlePopupActionCompleted()
    {
        ClosePreview();
        recordButtonController?.ResetToIdle();
    }

    private void ClosePreview()
    {
        if (videoPlayer != null && videoPlayer.isPlaying)
            videoPlayer.Stop();
        if (previewRoot != null)
            previewRoot.SetActive(false);
        if (shareButton != null) shareButton.gameObject.SetActive(false);
        if (discardButton != null) discardButton.gameObject.SetActive(false);
        _currentFilePath = null;
    }

    // ── Helpers ──────────────────────────────────────────────────────────────

    private static string NormalizeToFileUrl(string path)
    {
        if (string.IsNullOrEmpty(path))
            return string.Empty;
        if (path.StartsWith("file://", StringComparison.OrdinalIgnoreCase) ||
            path.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
            path.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
            return path;
        return "file://" + path;
    }

    // ── Validation ───────────────────────────────────────────────────────────

    private void ValidateRefs()
    {
        if (recordButtonController == null)
            Debug.LogError("[RecordingPreviewController] Record Button Controller is not assigned.");
        if (sharePopupController == null)
            Debug.LogError("[RecordingPreviewController] Share Popup Controller is not assigned.");
        if (previewRoot == null)
            Debug.LogError("[RecordingPreviewController] Preview Root is not assigned.");
        if (previewImage == null)
            Debug.LogError("[RecordingPreviewController] Preview Image (RawImage) is not assigned.");
        if (videoPlayer == null)
            Debug.LogError("[RecordingPreviewController] Video Player is not assigned.");
        if (discardButton == null)
            Debug.LogError("[RecordingPreviewController] Discard Button (X) is not assigned.");
        if (shareButton == null)
            Debug.LogError("[RecordingPreviewController] Share Button is not assigned.");
    }
}
