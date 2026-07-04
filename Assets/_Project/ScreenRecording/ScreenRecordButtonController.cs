using System;
using System.Collections;
using UnityEngine;
using UnityEngine.UI;

/// <summary>
/// Bottom-center record button.
///
/// Idle:      shows <see cref="idleIconSprite"/> (red circle). Tap starts recording.
/// Recording: shows <see cref="recordingIconSprite"/> (red square). <see cref="ringFillImage"/>
///            radially fills over <see cref="maxRecordSeconds"/> seconds. Tap again to stop early,
///            or it auto-stops when the ring completes.
///
/// On stop, hands the finished file path to <see cref="OnRecordingFinished"/> and hides itself —
/// <see cref="RecordingPreviewController"/> takes over from there (looping preview + Share button
/// in this button's place). Call <see cref="ResetToIdle"/> to bring the button back.
///
/// Requires a "Screen Recorder" GameObject with <see cref="SmileSoftScreenRecordController"/>
/// already present in the scene (see Assets/Plugins/SunShine Native Screen Recorder/Prefab).
///
/// Android uses <see cref="InAppScreenRecorder"/> instead of SmileSoft/MediaProjection — that's
/// what avoids the "Share your screen with FutureArtsWay?" system dialog. iOS still uses
/// SmileSoftScreenRecordController/ReplayKit, since ReplayKit doesn't show that blocking consent
/// dialog in the first place. Requires an InAppScreenRecorder component somewhere in the scene.
/// </summary>
public class ScreenRecordButtonController : MonoBehaviour
{
    [Header("Required refs")]
    [SerializeField] private Button recordButton;
    [SerializeField] private Image buttonIconImage;
    [Tooltip("Image.Type must be Filled / Radial 360 for the fill animation to read correctly.")]
    [SerializeField] private Image ringFillImage;

    [Header("Art — provided by design")]
    [SerializeField] private Sprite idleIconSprite;      // red circle
    [SerializeField] private Sprite recordingIconSprite; // red square

    [Header("Optional")]
    [Tooltip("Shown while iOS is encoding the file after stop (brief). Leave empty to skip.")]
    [SerializeField] private GameObject processingIndicator;

    [Header("Config")]
    [SerializeField] private float maxRecordSeconds = 30f;

    /// <summary>Fired once the file is ready (or empty string on failure). Path is a local filesystem path.</summary>
    public event Action<string> OnRecordingFinished;

    private enum State { Idle, Recording, Stopping }
    private State _state = State.Idle;
    private Coroutine _fillRoutine;

    private void Awake()
    {
        ValidateRefs();
    }

    private void OnEnable()
    {
        if (recordButton != null)
            recordButton.onClick.AddListener(OnButtonTapped);

        SmileSoftScreenRecordController.OnIosRecordingProcessing += HandleIosProcessing;
    }

    private void OnDisable()
    {
        if (recordButton != null)
            recordButton.onClick.RemoveListener(OnButtonTapped);

        SmileSoftScreenRecordController.OnIosRecordingProcessing -= HandleIosProcessing;
    }

    private void Start()
    {
        if (InAppScreenRecorder.instance == null)
        {
            Debug.LogWarning("[ScreenRecordButtonController] No InAppScreenRecorder in the scene — " +
                "Android recording will fail. This is expected only if you haven't added that " +
                "component yet (see InAppScreenRecorder.cs header comment).");
        }

        // Force gallery/Photos auto-save on for iOS, regardless of whatever is currently configured
        // on the "Screen Recorder" prefab instance in the scene. The Save-to-Phone popup step
        // assumes the clip is already in the camera roll by the time it's shown (see
        // SharePopupController) — SmileSoft/ReplayKit only exposes a save-to-gallery flag at
        // record-start, not on demand. (Android's InAppScreenRecorder saves to the gallery itself,
        // right after StopRecording() finishes — see InAppScreenRecorder.FinishAndSave().)
        var ctrl = SmileSoftScreenRecordController.instance;
        if (ctrl == null)
        {
            Debug.LogError("[ScreenRecordButtonController] SmileSoftScreenRecordController.instance " +
                "is null. Make sure the plugin's \"Screen Recorder\" prefab is in the scene.");
            return;
        }
        ctrl.SetGalleryAddingCapabilities(true);
        ctrl.SetIosSaveToPhotos(true);

        bool isAndroid = ctrl.IsAndroidPlatform();
        bool isIos = ctrl.IsIosPlatform();
        if (!isAndroid && !isIos)
        {
            Debug.LogWarning("[ScreenRecordButtonController] Neither IsAndroidPlatform() nor " +
                "IsIosPlatform() is true — this is almost always because you're running in the " +
                "Unity Editor / Play Mode. SmileSoftScreenRecordController.StartRecording()/" +
                "StopRecording() are silent no-ops here (no ReplayKit / MediaProjection in the " +
                "Editor process), so the recording flow will not run past the ring-fill animation. " +
                "Build to a real Android or iOS device to test recording end-to-end.");
        }

        SetIdleVisuals();
    }

    // ── Input ────────────────────────────────────────────────────────────────

    private void OnButtonTapped()
    {
        switch (_state)
        {
            case State.Idle:
                BeginRecording();
                break;
            case State.Recording:
                EndRecording();
                break;
            case State.Stopping:
                break;
        }
    }

    // ── Recording lifecycle ─────────────────────────────────────────────────

    private void BeginRecording()
    {
        if (Application.platform == RuntimePlatform.Android)
        {
            if (InAppScreenRecorder.instance == null)
            {
                Debug.LogError("[ScreenRecordButtonController] Cannot start recording — " +
                    "InAppScreenRecorder.instance is null. Add an InAppScreenRecorder component to the scene.");
                return;
            }
            InAppScreenRecorder.instance.StartRecording();
        }
        else
        {
            var ctrl = SmileSoftScreenRecordController.instance;
            if (ctrl == null)
            {
                Debug.LogError("[ScreenRecordButtonController] Cannot start recording — " +
                    "SmileSoftScreenRecordController.instance is null.");
                return;
            }

            if (ctrl.IsIosPlatform() && !ctrl.IsRecordingAvailable())
            {
                Debug.LogWarning("[ScreenRecordButtonController] Recording unavailable on this device.");
                return;
            }

            string videoName = "Record_" + DateTime.Now.ToString("yyyy_MM_dd_HH_mm_ss");
            ctrl.SetVideoName(videoName);
            ctrl.StartRecording();
        }

        _state = State.Recording;
        SetRecordingVisuals();

        if (_fillRoutine != null)
            StopCoroutine(_fillRoutine);
        _fillRoutine = StartCoroutine(FillRoutine());
    }

    private void EndRecording()
    {
        if (_fillRoutine != null)
        {
            StopCoroutine(_fillRoutine);
            _fillRoutine = null;
        }

        _state = State.Stopping;

        if (Application.platform == RuntimePlatform.Android)
        {
            if (InAppScreenRecorder.instance == null)
            {
                Debug.LogError("[ScreenRecordButtonController] Cannot stop recording — " +
                    "InAppScreenRecorder.instance is null.");
                _state = State.Idle;
                SetIdleVisuals();
                return;
            }
            InAppScreenRecorder.instance.StopRecording(HandleRecordingStopped);
            return;
        }

        var ctrl = SmileSoftScreenRecordController.instance;
        if (ctrl == null)
        {
            Debug.LogError("[ScreenRecordButtonController] Cannot stop recording — " +
                "SmileSoftScreenRecordController.instance is null.");
            _state = State.Idle;
            SetIdleVisuals();
            return;
        }

        ctrl.StopRecording(HandleRecordingStopped);
    }

    private void HandleRecordingStopped(string path)
    {
        if (processingIndicator != null)
            processingIndicator.SetActive(false);

        if (string.IsNullOrEmpty(path))
            Debug.LogError("[ScreenRecordButtonController] Recording finished with an empty path — the recording failed.");

        if (OnRecordingFinished == null)
        {
            Debug.LogWarning("[ScreenRecordButtonController] OnRecordingFinished has no subscribers — " +
                "RecordingPreviewController's Record Button Controller field probably isn't pointed at this " +
                "GameObject (or that component/GameObject is disabled), so the preview will never show.");
        }

        // Hand off to the preview flow. This button hides itself; ResetToIdle() brings it back
        // after Discard, or after Save/Share completes.
        //
        // Note: "screenrecordRing" is a separate sibling GameObject in the scene, not a child of
        // this button's GameObject — gameObject.SetActive(false) below does NOT hide it. Without
        // this explicit reset, the ring stays frozen on screen (at whatever fill it had) even
        // after the button disappears and the preview takes over.
        if (ringFillImage != null)
        {
            ringFillImage.fillAmount = 0f;
            ringFillImage.gameObject.SetActive(false);
        }
        gameObject.SetActive(false);
        _state = State.Idle;
        OnRecordingFinished?.Invoke(path);
    }

    private void HandleIosProcessing()
    {
        if (processingIndicator != null)
            processingIndicator.SetActive(true);
    }

    /// <summary>Call after Discard, or after the share popup finishes, to show the button again.</summary>
    public void ResetToIdle()
    {
        if (_fillRoutine != null)
        {
            StopCoroutine(_fillRoutine);
            _fillRoutine = null;
        }
        _state = State.Idle;
        gameObject.SetActive(true);
        if (ringFillImage != null)
            ringFillImage.gameObject.SetActive(true);
        SetIdleVisuals();
    }

    // ── Ring fill ────────────────────────────────────────────────────────────

    private IEnumerator FillRoutine()
    {
        float elapsed = 0f;
        if (ringFillImage != null)
            ringFillImage.fillAmount = 0f;

        while (elapsed < maxRecordSeconds)
        {
            elapsed += Time.deltaTime;
            if (ringFillImage != null)
                ringFillImage.fillAmount = Mathf.Clamp01(elapsed / maxRecordSeconds);
            yield return null;
        }

        _fillRoutine = null;
        EndRecording(); // auto-stop at the cap
    }

    // ── Visual state ─────────────────────────────────────────────────────────

    private void SetIdleVisuals()
    {
        if (buttonIconImage == null)
        {
            Debug.LogError("[ScreenRecordButtonController] SetIdleVisuals(): Button Icon Image is not " +
                "assigned, so there's nothing to swap the sprite on.");
        }
        else if (idleIconSprite == null)
        {
            Debug.LogError("[ScreenRecordButtonController] Idle Icon Sprite is not assigned — " +
                "drag the red-circle icon into the Idle Icon Sprite field.");
        }
        else
        {
            buttonIconImage.sprite = idleIconSprite;
        }

        if (ringFillImage != null)
            ringFillImage.fillAmount = 0f;
    }

    private void SetRecordingVisuals()
    {
        if (buttonIconImage == null)
        {
            Debug.LogError("[ScreenRecordButtonController] SetRecordingVisuals(): Button Icon Image is not " +
                "assigned, so the icon can't switch to the recording (square) sprite.");
        }
        else if (recordingIconSprite == null)
        {
            Debug.LogError("[ScreenRecordButtonController] Recording Icon Sprite is not assigned — " +
                "drag the red-square icon into the Recording Icon Sprite field. This is why the icon " +
                "isn't switching when you start recording.");
        }
        else
        {
            buttonIconImage.sprite = recordingIconSprite;
        }
    }

    // ── Validation ───────────────────────────────────────────────────────────

    private void ValidateRefs()
    {
        if (recordButton == null)
            Debug.LogError("[ScreenRecordButtonController] Record Button is not assigned.");
        if (buttonIconImage == null)
            Debug.LogError("[ScreenRecordButtonController] Button Icon Image is not assigned.");
        if (ringFillImage == null)
            Debug.LogError("[ScreenRecordButtonController] Ring Fill Image is not assigned.");
        else if (ringFillImage.type != Image.Type.Filled)
            Debug.LogError("[ScreenRecordButtonController] Ring Fill Image's Image.Type must be set " +
                "to \"Filled\" (Radial 360) in the Inspector for the recording ring to animate.");
    }
}
