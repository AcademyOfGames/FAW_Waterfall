using System;
using System.Collections;
using UnityEngine;
using UnityEngine.UI;

/// <summary>
/// Bottom-sheet / modal shown when the user taps Share in <see cref="RecordingPreviewController"/>.
/// Two actions: Save to Phone, Share to Instagram. Plus an optional Cancel/close that returns to
/// the preview without doing anything.
///
/// Save-to-Phone design note: on iOS, SmileSoft/ReplayKit only exposes a save-to-gallery flag at
/// record-start (see ScreenRecordButtonController.Start(), which forces that flag on) — so by the
/// time this popup is open, the clip is already in the camera roll, and Save is a confirmation
/// beat rather than a separate write. On Android, InAppScreenRecorder copies the finished clip
/// into the gallery itself right after StopRecording() completes (see
/// InAppScreenRecorder.FinishAndSave()) — same "already saved by popup time" behavior, just
/// implemented differently under the hood. Either way, OnSaveTapped() below doesn't need to know
/// which platform it's on.
///
/// Share-to-Instagram: iOS uses the plugin's generic ShareVideo() (OS share sheet) — confirmed
/// working. Android uses InAppScreenRecorder.ShareLastRecording(), which hands the already-saved
/// gallery content:// URI to the native OS share sheet.
/// </summary>
public class SharePopupController : MonoBehaviour
{
    [Header("Required refs")]
    [SerializeField] private GameObject popupRoot;
    [SerializeField] private Button saveButton;
    [SerializeField] private Button instagramButton;

    [Header("Optional")]
    [Tooltip("Close/Cancel button — dismisses the popup without taking any action.")]
    [SerializeField] private Button closeButton;
    [Tooltip("Shown briefly after tapping Save to Phone, e.g. a \"Saved!\" checkmark/toast.")]
    [SerializeField] private GameObject savedConfirmation;
    [SerializeField] private float confirmationDisplaySeconds = 1f;

    /// <summary>Fired after a Save or Instagram-share action completes — the caller should close the preview.</summary>
    public event Action OnActionCompleted;

    private string _filePath;
    private Coroutine _confirmRoutine;

    private void Awake()
    {
        ValidateRefs();
        if (popupRoot != null)
            popupRoot.SetActive(false);
        if (savedConfirmation != null)
            savedConfirmation.SetActive(false);
    }

    private void OnEnable()
    {
        if (saveButton != null)
            saveButton.onClick.AddListener(OnSaveTapped);
        if (instagramButton != null)
            instagramButton.onClick.AddListener(OnInstagramTapped);
        if (closeButton != null)
            closeButton.onClick.AddListener(OnCloseTapped);
    }

    private void OnDisable()
    {
        if (saveButton != null)
            saveButton.onClick.RemoveListener(OnSaveTapped);
        if (instagramButton != null)
            instagramButton.onClick.RemoveListener(OnInstagramTapped);
        if (closeButton != null)
            closeButton.onClick.RemoveListener(OnCloseTapped);
    }

    // ── Public API ───────────────────────────────────────────────────────────

    public void Open(string filePath)
    {
        if (string.IsNullOrEmpty(filePath))
            Debug.LogError("[SharePopupController] Open() called with an empty file path.");

        _filePath = filePath;

        if (popupRoot == null)
        {
            Debug.LogError("[SharePopupController] Popup Root is not assigned — cannot show the popup.");
            return;
        }
        popupRoot.SetActive(true);
    }

    public void Close()
    {
        if (_confirmRoutine != null)
        {
            StopCoroutine(_confirmRoutine);
            _confirmRoutine = null;
        }
        if (savedConfirmation != null)
            savedConfirmation.SetActive(false);
        if (popupRoot != null)
            popupRoot.SetActive(false);
    }

    // ── Actions ──────────────────────────────────────────────────────────────

    private void OnSaveTapped()
    {
        // Already saved at record-start (see class comment) — this is a confirmation beat.
        if (savedConfirmation == null)
        {
            Debug.LogError("[SharePopupController] Saved Confirmation is not assigned — " +
                "drag a \"Saved!\" toast/checkmark object into that field. Completing the action anyway.");
            Close();
            OnActionCompleted?.Invoke();
            return;
        }

        if (_confirmRoutine != null)
            StopCoroutine(_confirmRoutine);
        _confirmRoutine = StartCoroutine(ShowConfirmationThenComplete());
    }

    private IEnumerator ShowConfirmationThenComplete()
    {
        savedConfirmation.SetActive(true);
        yield return new WaitForSeconds(confirmationDisplaySeconds);
        savedConfirmation.SetActive(false);
        _confirmRoutine = null;
        Close();
        OnActionCompleted?.Invoke();
    }

    private void OnInstagramTapped()
    {
        if (string.IsNullOrEmpty(_filePath))
        {
            Debug.LogError("[SharePopupController] Cannot share — no file path was set (Open() was called with an empty path).");
            return;
        }

        if (Application.platform == RuntimePlatform.Android)
        {
            if (InAppScreenRecorder.instance == null)
            {
                Debug.LogError("[SharePopupController] Cannot share — InAppScreenRecorder.instance is null.");
                return;
            }
            InAppScreenRecorder.instance.ShareLastRecording("Share");
        }
        else
        {
            var ctrl = SmileSoftScreenRecordController.instance;
            if (ctrl == null)
            {
                Debug.LogError("[SharePopupController] Cannot share — SmileSoftScreenRecordController.instance is null.");
                return;
            }
            ctrl.ShareVideo(_filePath, "Check out my AR moment!", "Share");
        }

        Close();
        OnActionCompleted?.Invoke();
    }

    private void OnCloseTapped()
    {
        // Cancel — no completion event, just return to the still-visible preview.
        Close();
    }

    // ── Validation ───────────────────────────────────────────────────────────

    private void ValidateRefs()
    {
        if (popupRoot == null)
            Debug.LogError("[SharePopupController] Popup Root is not assigned.");
        if (saveButton == null)
            Debug.LogError("[SharePopupController] Save Button is not assigned.");
        if (instagramButton == null)
            Debug.LogError("[SharePopupController] Instagram Button is not assigned.");
    }
}
