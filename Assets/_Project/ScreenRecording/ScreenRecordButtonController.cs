using System;
using System.Collections;
using System.Runtime.InteropServices;
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
///
/// Native-overlay wrinkle (both platforms, for different reasons): whatever records the screen
/// would otherwise capture this button/ring along with everything else. On Android,
/// InAppScreenRecorder grabs Unity's own rendered frame, so anything Unity itself draws —
/// including this button's Image/ringFillImage — would appear in the recorded video. On iOS,
/// ReplayKit is a true system-level screen recorder that captures the full composited screen, so
/// even a plain native view layered on top would still show up. To keep the button visible to the
/// user but invisible to the recording on both platforms, this component disables its own
/// Unity-rendered Image/ring and hands the actual visible button off to a native overlay,
/// positioned to match this button's existing RectTransform:
///  - Android: a genuine native Android View (NativeRecordButton.java) added alongside — never
///    inside — Unity's own rendering surface, which Unity's capture has no way to see.
///  - iOS: a UITextField with secureTextEntry = YES hosting our content in its protected internal
///    canvas view (FawRecordButton.mm) — the same content-protection mechanism iOS uses to hide
///    password fields from screenshots and screen recordings, including ReplayKit.
/// Both mirror how the FAWCurrents_WebARMain project's recording HUD lives in a plain HTML
/// element outside its captured &lt;canvas&gt;. All state-machine logic (Idle/Recording/Stopping)
/// stays here in C#; the native views only draw pixels and forward taps back via UnitySendMessage.
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

#if UNITY_ANDROID && !UNITY_EDITOR
    private const string NativeButtonClassName = "com.FutureArts.FutureArtsWay.inapprecorder.NativeRecordButton";
    private AndroidJavaObject _nativeButtonAndroid;
#endif

#if UNITY_IOS && !UNITY_EDITOR
    [DllImport("__Internal")] private static extern void FawRecordButton_Create(string gameObjectName, int x, int y, int width, int height, int unityScreenWidth);
    [DllImport("__Internal")] private static extern void FawRecordButton_Show();
    [DllImport("__Internal")] private static extern void FawRecordButton_Hide();
    [DllImport("__Internal")] private static extern void FawRecordButton_SetRecording(bool recording);
    [DllImport("__Internal")] private static extern void FawRecordButton_SetFillAmount(float amount);
    [DllImport("__Internal")] private static extern void FawRecordButton_Destroy();
#endif

#if (UNITY_ANDROID || UNITY_IOS) && !UNITY_EDITOR
    private bool _nativeButtonReady;
    private Graphic[] _hiddenGraphics;
#endif

    private void Awake()
    {
        ValidateRefs();
    }

    private void OnEnable()
    {
        if (recordButton != null)
            recordButton.onClick.AddListener(OnButtonTapped);

        SmileSoftScreenRecordController.OnIosRecordingProcessing += HandleIosProcessing;
        SmileSoftScreenRecordController.OnIosRecordStartResult += HandleIosStartResult;

        // Keep the native overlay's visibility in lockstep with this GameObject's active state.
        // PersistentRecordingRoot hides the whole recording canvas on the main menu by
        // deactivating it — but the native button is a platform view outside Unity's hierarchy,
        // so without this it would stay floating over the menu after leaving an experience scene.
        if (_state == State.Idle)
            NativeButton_Show();
    }

    private void OnDisable()
    {
        if (recordButton != null)
            recordButton.onClick.RemoveListener(OnButtonTapped);

        SmileSoftScreenRecordController.OnIosRecordingProcessing -= HandleIosProcessing;
        SmileSoftScreenRecordController.OnIosRecordStartResult -= HandleIosStartResult;

        NativeButton_Hide();
    }

    private void OnDestroy()
    {
        NativeButton_Destroy();
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

        SetupNativeOverlayButton();
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

    /// <summary>Called via UnitySendMessage from the native Android/iOS record-button overlay (see
    /// NativeRecordButton.java / FawRecordButton.mm). Do not rename without updating both.</summary>
    public void OnNativeButtonTapped()
    {
        OnButtonTapped();
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

            // Match what Android's InAppScreenRecorder records: the app's own audio, no
            // microphone. SystemAudio keeps the experience's sound in the clip without triggering
            // the iOS mic-permission prompt or picking up room noise. Set here (rather than in
            // Start()) because the plugin's EasyScreenRecordInitializer also writes this setting
            // from its own Start() — the value is only read at record-start, so this is the one
            // spot where we always win. Switch to MicAudio if narration over recordings is wanted.
            ctrl.SetAudioRecordingMode(SmileSoftScreenRecordController.AudioRecordingMode.SystemAudio);

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

        NativeButton_Hide();

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

    /// <summary>
    /// iOS only — result of the asynchronous ReplayKit start (see
    /// <see cref="SmileSoftScreenRecordController.OnIosRecordStartResult"/>). BeginRecording()
    /// flipped the UI to the recording state optimistically at tap time; this either syncs the
    /// ring timer to the moment capture actually began (the first-ever start sits behind a system
    /// consent alert, which can add seconds), or unwinds back to idle if the start failed —
    /// without this, a denied consent alert left the button stuck showing a recording that
    /// wasn't happening.
    /// </summary>
    private void HandleIosStartResult(bool success)
    {
        if (_state != State.Recording)
            return;

        if (_fillRoutine != null)
        {
            StopCoroutine(_fillRoutine);
            _fillRoutine = null;
        }

        if (success)
        {
            _fillRoutine = StartCoroutine(FillRoutine()); // restart the cap timer from the real start
            return;
        }

        Debug.LogWarning("[ScreenRecordButtonController] iOS recording failed to start (consent " +
            "denied or ReplayKit unavailable) — resetting the button to idle.");
        _state = State.Idle;
        SetIdleVisuals();
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
        NativeButton_Show();
    }

    // ── Ring fill ────────────────────────────────────────────────────────────

    private IEnumerator FillRoutine()
    {
        float elapsed = 0f;
        if (ringFillImage != null)
            ringFillImage.fillAmount = 0f;
        NativeButton_SetFillAmount(0f);

        while (elapsed < maxRecordSeconds)
        {
            elapsed += Time.deltaTime;
            float amount = Mathf.Clamp01(elapsed / maxRecordSeconds);
            if (ringFillImage != null)
                ringFillImage.fillAmount = amount;
            NativeButton_SetFillAmount(amount);
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

        NativeButton_SetRecording(false);
        NativeButton_SetFillAmount(0f);
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

        NativeButton_SetRecording(true);
    }

    // ── Native overlay (Android + iOS) ──────────────────────────────────────
    //
    // These wrapper methods are the only place that branches by platform — every call site above
    // just calls them plainly. Each is a no-op on platforms/builds where the native overlay isn't
    // applicable (Editor, standalone, etc.).

    /// <summary>
    /// Disables Unity's own rendering of this button's Image and its ring (so neither
    /// InAppScreenRecorder nor ReplayKit ever sees them — see the class doc comment above), then
    /// creates a native overlay positioned to exactly match this button's existing on-screen rect
    /// and hands visible/tap duties off to it. If anything here fails, falls back to leaving the
    /// Unity UI button visible (and interactable) — meaning the button would show up in
    /// recordings, but at least the app keeps working rather than losing the record button
    /// entirely.
    /// </summary>
    private void SetupNativeOverlayButton()
    {
#if (UNITY_ANDROID || UNITY_IOS) && !UNITY_EDITOR
        if (buttonIconImage == null || ringFillImage == null)
            return;

        // Disable every Graphic Unity would otherwise render for this button — not just
        // buttonIconImage. This GameObject also carries its own separate background Image (the
        // white circle sprite behind the icon) that isn't referenced by any field here; missing
        // it meant that background stayed Unity-rendered and still showed up in recordings even
        // after the icon and ring were hidden. GetComponentsInChildren catches all of them (self +
        // any children) in one sweep instead of needing a dedicated field per graphic.
        _hiddenGraphics = GetComponentsInChildren<Graphic>(true);
        foreach (var g in _hiddenGraphics)
            g.enabled = false;
        ringFillImage.enabled = false;
        if (recordButton != null)
            recordButton.interactable = false; // taps now come through the native view instead

        // Size/position the native overlay from the union of this button's own outer
        // RectTransform and the ring's RectTransform — NOT the (smaller, centered) icon child's
        // rect, which is what was used before and made the native ring render shrunk toward the
        // middle instead of hugging the outer edge of the button.
        Rect buttonRect = GetScreenPixelRect((RectTransform)transform);
        Rect ringRect = GetScreenPixelRect(ringFillImage.rectTransform);
        Rect pixelRect = Rect.MinMaxRect(
            Mathf.Min(buttonRect.xMin, ringRect.xMin),
            Mathf.Min(buttonRect.yMin, ringRect.yMin),
            Mathf.Max(buttonRect.xMax, ringRect.xMax),
            Mathf.Max(buttonRect.yMax, ringRect.yMax));

        try
        {
#if UNITY_ANDROID
            _nativeButtonAndroid = new AndroidJavaObject(NativeButtonClassName);
            using var activity = GetCurrentActivity();
            _nativeButtonAndroid.Call("create", activity, gameObject.name,
                (int)pixelRect.x, (int)pixelRect.y, (int)pixelRect.width, (int)pixelRect.height);
#elif UNITY_IOS
            // Screen.width rides along so the native side can convert these device-pixel values
            // into UIKit points (see FawRecordButton.mm) — without it the overlay renders 2-3x too
            // big on Retina displays.
            FawRecordButton_Create(gameObject.name,
                (int)pixelRect.x, (int)pixelRect.y, (int)pixelRect.width, (int)pixelRect.height,
                Screen.width);
#endif
            _nativeButtonReady = true;
        }
        catch (Exception e)
        {
            Debug.LogError("[ScreenRecordButtonController] Failed to create the native record button " +
                "overlay — falling back to the Unity UI button (it will show up in recordings). " +
                "Exception: " + e);
            foreach (var g in _hiddenGraphics)
                g.enabled = true;
            ringFillImage.enabled = true;
            if (recordButton != null)
                recordButton.interactable = true;
            _nativeButtonReady = false;
        }
#endif
    }

    private void NativeButton_Show()
    {
#if UNITY_ANDROID && !UNITY_EDITOR
        _nativeButtonAndroid?.Call("show");
#elif UNITY_IOS && !UNITY_EDITOR
        if (_nativeButtonReady) FawRecordButton_Show();
#endif
    }

    private void NativeButton_Hide()
    {
#if UNITY_ANDROID && !UNITY_EDITOR
        _nativeButtonAndroid?.Call("hide");
#elif UNITY_IOS && !UNITY_EDITOR
        if (_nativeButtonReady) FawRecordButton_Hide();
#endif
    }

    private void NativeButton_SetRecording(bool recording)
    {
#if UNITY_ANDROID && !UNITY_EDITOR
        _nativeButtonAndroid?.Call("setRecording", recording);
#elif UNITY_IOS && !UNITY_EDITOR
        if (_nativeButtonReady) FawRecordButton_SetRecording(recording);
#endif
    }

    private void NativeButton_SetFillAmount(float amount)
    {
#if UNITY_ANDROID && !UNITY_EDITOR
        _nativeButtonAndroid?.Call("setFillAmount", amount);
#elif UNITY_IOS && !UNITY_EDITOR
        if (_nativeButtonReady) FawRecordButton_SetFillAmount(amount);
#endif
    }

    private void NativeButton_Destroy()
    {
#if UNITY_ANDROID && !UNITY_EDITOR
        _nativeButtonAndroid?.Call("destroy");
        _nativeButtonAndroid?.Dispose();
        _nativeButtonAndroid = null;
#elif UNITY_IOS && !UNITY_EDITOR
        if (_nativeButtonReady) FawRecordButton_Destroy();
        _nativeButtonReady = false;
#endif
    }

#if (UNITY_ANDROID || UNITY_IOS) && !UNITY_EDITOR
    /// <summary>
    /// For a Screen Space - Overlay RectTransform, world corners are already screen pixel
    /// coordinates — no camera/canvas conversion needed. Unity's screen space has a bottom-left
    /// origin; both Android View and iOS UIKit coordinates have a top-left origin, hence the Y
    /// flip below.
    /// </summary>
    private static Rect GetScreenPixelRect(RectTransform rt)
    {
        Vector3[] corners = new Vector3[4];
        rt.GetWorldCorners(corners);
        float minX = Mathf.Min(corners[0].x, corners[2].x);
        float maxX = Mathf.Max(corners[0].x, corners[2].x);
        float minY = Mathf.Min(corners[0].y, corners[2].y);
        float maxY = Mathf.Max(corners[0].y, corners[2].y);
        float topY = Screen.height - maxY;
        return new Rect(minX, topY, maxX - minX, maxY - minY);
    }
#endif

#if UNITY_ANDROID && !UNITY_EDITOR
    private static AndroidJavaObject GetCurrentActivity()
    {
        using var unityPlayer = new AndroidJavaClass("com.unity3d.player.UnityPlayer");
        return unityPlayer.GetStatic<AndroidJavaObject>("currentActivity");
    }
#endif

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
