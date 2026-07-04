using System;
using System.Collections;
using System.IO;
using UnityEngine;

/// <summary>
/// Android in-app screen recorder — replaces SmileSoftScreenRecordController on Android only.
///
/// Captures Unity's own already-rendered frames (ScreenCapture.CaptureScreenshotIntoRenderTexture
/// + AsyncGPUReadback, i.e. a copy of Unity's own back buffer) and encodes them on-device with a
/// custom H.264 encoder (see InAppFrameEncoder.java). This never calls Android's MediaProjection
/// screen-capture broker, so the "Share your screen with FutureArtsWay?" system consent dialog
/// never appears — the app is only encoding pixels it already produced itself, not asking the OS
/// for a mirror of the display compositor.
///
/// iOS keeps using SmileSoftScreenRecordController/ReplayKit — ReplayKit's in-app capture doesn't
/// show a blocking consent dialog (just a small red status-bar indicator), so there was never a
/// dialog problem to solve there.
///
/// Video only in this version — no audio track. On stop, the finished clip is immediately copied
/// into the public Movies/FutureArtsWay gallery folder via MediaStore (see MediaStoreSaver.java) so
/// SharePopupController's existing "already saved by the time the popup opens" design holds, same
/// as it did with SmileSoft's auto-save-at-record-start behavior.
///
/// Add a GameObject with this component anywhere persistent in the scene (e.g. next to the
/// existing "Screen Recorder" SmileSoft prefab). No Inspector wiring is required beyond the
/// optional tuning fields below — ScreenRecordButtonController/SharePopupController find it via
/// InAppScreenRecorder.instance.
/// </summary>
public class InAppScreenRecorder : MonoBehaviour
{
    public static InAppScreenRecorder instance;

    [Header("Config")]
    [Tooltip("Capture is downscaled to this max height to keep the per-frame color conversion " +
        "cheap on the CPU. Width is derived to preserve the screen's aspect ratio. Lower this if " +
        "recording causes visible stutter on test devices.")]
    [SerializeField] private int maxCaptureHeight = 1280;
    [Tooltip("Lower this (e.g. 15) if recording causes stutter; raise it (e.g. 30) if devices " +
        "handle it fine and you want smoother playback.")]
    [SerializeField] private int frameRate = 20;
    [SerializeField] private int bitrate = 6_000_000;
    [Tooltip("Some Android encoders report the input Image's chroma planes in the opposite order " +
        "from the documented convention for COLOR_FormatYUV420Flexible. Leave this on unless a " +
        "recording looks wrong without it.")]
    [SerializeField] private bool swapUAndVPlanes = true;

    private const string EncoderClassName = "com.FutureArts.FutureArtsWay.inapprecorder.InAppFrameEncoder";
    private const string SaverClassName = "com.FutureArts.FutureArtsWay.inapprecorder.MediaStoreSaver";

    /// <summary>content:// URI of the clip most recently saved to the gallery, or null. Used by
    /// SharePopupController's Android branch to hand off to the OS share sheet.</summary>
    public string LastSavedContentUri { get; private set; }

    public bool IsRecording { get; private set; }

#if UNITY_ANDROID && !UNITY_EDITOR
    // CaptureScreenshotIntoRenderTexture requires its destination RT to match the screen's actual
    // resolution exactly (Unity does not scale for you) — so _fullResRT is always full screen size
    // and is what the OS-free "screenshot" capture writes into. _captureRT is the (usually smaller)
    // encode target; we GPU-Blit from _fullResRT down into it every frame.
    private RenderTexture _fullResRT;
    private RenderTexture _captureRT;
    private AndroidJavaObject _encoder;
    private Coroutine _captureRoutine;
    private double _recordingStartRealtime;
    private long _frameIndex;
    private int _fullResWidth;
    private int _fullResHeight;
    private int _captureWidth;
    private int _captureHeight;
    private int _pushFailCount;
    private int _readbackErrorCount;
#endif
    private Action<string> _pendingStopCallback;

    private void Awake()
    {
        if (instance != null && instance != this)
        {
            Debug.LogWarning("[InAppScreenRecorder] Duplicate instance in scene — destroying this one.");
            Destroy(gameObject);
            return;
        }
        instance = this;
    }

    // ── Public API (mirrors SmileSoftScreenRecordController's shape used by the button/popup) ──

    public void StartRecording()
    {
        if (IsRecording)
        {
            Debug.LogWarning("[InAppScreenRecorder] StartRecording() called while already recording — ignoring.");
            return;
        }

#if !UNITY_ANDROID || UNITY_EDITOR
        Debug.LogWarning("[InAppScreenRecorder] StartRecording() is only implemented for Android device " +
            "builds — no-op in the Editor or on other platforms.");
#else
        _fullResWidth = RoundToEven(Screen.width);
        _fullResHeight = RoundToEven(Screen.height);

        _captureWidth = _fullResWidth;
        _captureHeight = _fullResHeight;
        if (_captureHeight > maxCaptureHeight)
        {
            float scale = (float)maxCaptureHeight / _captureHeight;
            _captureHeight = RoundToEven(maxCaptureHeight);
            _captureWidth = RoundToEven(Mathf.RoundToInt(_captureWidth * scale));
        }

        string fileName = "InAppRecord_" + DateTime.Now.ToString("yyyy_MM_dd_HH_mm_ss") + ".mp4";
        string outputPath = Path.Combine(Application.temporaryCachePath, fileName);

        try
        {
            _encoder = new AndroidJavaObject(EncoderClassName);
        }
        catch (Exception e)
        {
            Debug.LogError("[InAppScreenRecorder] Failed to construct native encoder (" + EncoderClassName +
                "). Make sure InAppFrameEncoder.java is present under Assets/Plugins/Android/com/FutureArts/" +
                "FutureArtsWay/inapprecorder/ and the project has been rebuilt since adding it. Exception: " + e);
            return;
        }

        bool configured = _encoder.Call<bool>("configure", outputPath, _captureWidth, _captureHeight, frameRate,
            bitrate, swapUAndVPlanes);
        if (!configured)
        {
            Debug.LogError("[InAppScreenRecorder] Native configure() returned false — check logcat for the " +
                "\"InAppFrameEncoder\" tag for the underlying exception.");
            _encoder.Dispose();
            _encoder = null;
            return;
        }

        if (_fullResRT == null || _fullResRT.width != _fullResWidth || _fullResRT.height != _fullResHeight)
        {
            if (_fullResRT != null)
            {
                _fullResRT.Release();
                Destroy(_fullResRT);
            }
            _fullResRT = new RenderTexture(_fullResWidth, _fullResHeight, 0, RenderTextureFormat.ARGB32);
        }

        if (_captureRT == null || _captureRT.width != _captureWidth || _captureRT.height != _captureHeight)
        {
            if (_captureRT != null)
            {
                _captureRT.Release();
                Destroy(_captureRT);
            }
            _captureRT = new RenderTexture(_captureWidth, _captureHeight, 0, RenderTextureFormat.ARGB32);
        }

        _frameIndex = 0;
        _pushFailCount = 0;
        _readbackErrorCount = 0;
        _recordingStartRealtime = Time.realtimeSinceStartupAsDouble;
        IsRecording = true;
        _captureRoutine = StartCoroutine(CaptureLoop(outputPath));
#endif
    }

    public void StopRecording(Action<string> callback)
    {
        _pendingStopCallback = callback;

#if !UNITY_ANDROID || UNITY_EDITOR
        Debug.LogWarning("[InAppScreenRecorder] StopRecording() is only implemented for Android device builds.");
        InvokePendingCallback(string.Empty);
#else
        if (!IsRecording)
        {
            Debug.LogWarning("[InAppScreenRecorder] StopRecording() called but not currently recording.");
            InvokePendingCallback(string.Empty);
            return;
        }

        IsRecording = false;
        if (_captureRoutine != null)
        {
            StopCoroutine(_captureRoutine);
            _captureRoutine = null;
        }

        StartCoroutine(FinishAndSave());
#endif
    }

    /// <summary>Android-only: hands LastSavedContentUri off to the native OS share sheet.</summary>
    public void ShareLastRecording(string chooserTitle)
    {
#if !UNITY_ANDROID || UNITY_EDITOR
        Debug.LogWarning("[InAppScreenRecorder] ShareLastRecording() is Android-only.");
#else
        if (string.IsNullOrEmpty(LastSavedContentUri))
        {
            Debug.LogError("[InAppScreenRecorder] ShareLastRecording(): no saved content URI available — " +
                "the gallery copy step must have failed. Check logcat for \"MediaStoreSaver\".");
            return;
        }
        try
        {
            using var saver = new AndroidJavaObject(SaverClassName);
            using var activity = GetCurrentActivity();
            saver.Call<bool>("shareContentUri", activity, LastSavedContentUri, "video/mp4", chooserTitle);
        }
        catch (Exception e)
        {
            Debug.LogError("[InAppScreenRecorder] ShareLastRecording() failed: " + e);
        }
#endif
    }

    private void InvokePendingCallback(string path)
    {
        var cb = _pendingStopCallback;
        _pendingStopCallback = null;
        cb?.Invoke(path);
    }

#if UNITY_ANDROID && !UNITY_EDITOR
    private IEnumerator CaptureLoop(string outputPath)
    {
        float minFrameInterval = 1f / frameRate;
        float nextCaptureTime = 0f;

        while (IsRecording)
        {
            yield return new WaitForEndOfFrame();

            if (Time.unscaledTime < nextCaptureTime)
                continue;
            nextCaptureTime = Time.unscaledTime + minFrameInterval;

            ScreenCapture.CaptureScreenshotIntoRenderTexture(_fullResRT);
            Graphics.Blit(_fullResRT, _captureRT); // GPU-side downscale (or 1:1 copy) — cheap, unlike a CPU resize
            var request = UnityEngine.Rendering.AsyncGPUReadback.Request(_captureRT, 0, TextureFormat.RGBA32);

            while (!request.done)
                yield return null;

            if (!IsRecording || _encoder == null)
                yield break;

            if (request.hasError)
            {
                _readbackErrorCount++;
                if (_readbackErrorCount <= 3 || _readbackErrorCount % 30 == 0)
                {
                    Debug.LogWarning($"[InAppScreenRecorder] AsyncGPUReadback error (count={_readbackErrorCount}) — dropping this frame.");
                }
                continue;
            }

            var data = request.GetData<byte>();
            sbyte[] i420 = RgbaToI420(data, _captureWidth, _captureHeight);
            // Use actual measured elapsed wall-clock time for the presentation timestamp rather
            // than an idealized frameIndex * (1/frameRate) constant. If the device can't actually
            // sustain the configured frameRate (GPU readback + CPU YUV conversion cost per frame
            // exceeds 1/frameRate), frames really do land slower in real time than that constant
            // assumes — using the constant anyway compresses real elapsed time into a shorter
            // video timeline, which plays back sped up. Real elapsed time is correct regardless of
            // whatever throughput the device actually achieves.
            long pts = (long)((Time.realtimeSinceStartupAsDouble - _recordingStartRealtime) * 1_000_000.0);
            bool pushed = _encoder.Call<bool>("pushFrame", i420, pts);
            if (!pushed)
            {
                _pushFailCount++;
                if (_pushFailCount <= 3 || _pushFailCount % 30 == 0)
                {
                    Debug.LogWarning($"[InAppScreenRecorder] pushFrame() returned false at frame {_frameIndex} " +
                        $"(pushFailCount={_pushFailCount}) — check logcat \"InAppFrameEncoder\" tag.");
                }
            }
            _frameIndex++;
        }
    }

    private IEnumerator FinishAndSave()
    {
        string path = null;
        long framesEncoded = _frameIndex;
        if (_encoder != null)
        {
            path = _encoder.Call<string>("stop");
            _encoder.Dispose();
            _encoder = null;
        }
        else
        {
            Debug.LogError("[InAppScreenRecorder] FinishAndSave(): _encoder was already null — nothing to stop.");
        }
        yield return null; // let native stop()/muxer.release() settle before touching the file

        bool fileExists = !string.IsNullOrEmpty(path) && File.Exists(path);
        long fileLength = fileExists ? new FileInfo(path).Length : 0;
        bool validFile = fileExists && fileLength > 0;

        if (!validFile)
        {
            Debug.LogError($"[InAppScreenRecorder] Recording produced no usable file (framesEncoded={framesEncoded}, " +
                "path=\"" + path + "\", fileExists=" + fileExists + ", fileLength=" + fileLength +
                ") — check logcat for \"InAppFrameEncoder\".");
            InvokePendingCallback(string.Empty);
            yield break;
        }

        LastSavedContentUri = null;
        try
        {
            using var saver = new AndroidJavaObject(SaverClassName);
            using var activity = GetCurrentActivity();
            LastSavedContentUri = saver.Call<string>("saveMp4ToMovies", activity, path, Path.GetFileName(path));
        }
        catch (Exception e)
        {
            Debug.LogError("[InAppScreenRecorder] Failed to copy the recording into the gallery (Movies) — " +
                "the file still exists locally at " + path + ". Share-to-Instagram will fail until this is fixed. " +
                "Exception: " + e);
        }

        // Local path is still what gets handed to RecordingPreviewController's VideoPlayer for the
        // looping preview — the gallery copy above is a separate file purely for Share/Instagram.
        InvokePendingCallback(path);
    }

    private static AndroidJavaObject GetCurrentActivity()
    {
        using var unityPlayer = new AndroidJavaClass("com.unity3d.player.UnityPlayer");
        return unityPlayer.GetStatic<AndroidJavaObject>("currentActivity");
    }

    private static int RoundToEven(int value) => value % 2 == 0 ? value : value + 1;

    /// <summary>
    /// RGBA32 (from AsyncGPUReadback — rows are bottom-to-top, like all Unity texture readbacks)
    /// converted to a tightly packed I420 buffer (full-res Y, then half-res U, then half-res V),
    /// flipping to top-to-bottom row order so the resulting video isn't upside down. BT.601
    /// studio-swing (16-235) integer coefficients — the standard conversion MediaCodec expects.
    ///
    /// Returns sbyte[] rather than byte[] purely to match Java's signed byte type: passing a C#
    /// byte[] across the AndroidJavaObject bridge goes through a legacy/obsolete JNI conversion
    /// path (logs "AndroidJNIHelper: converting Byte array is obsolete, use SByte array instead"
    /// once per frame — harmless but very noisy at 20fps). sbyte[] marshals directly to Java's
    /// byte[] with no conversion, silencing the warning. The bit pattern is identical either way
    /// (Java only reads these as raw pixel bytes, never as signed magnitudes), so a plain
    /// unchecked (sbyte) cast of the 0-255 value is correct.
    /// </summary>
    private static sbyte[] RgbaToI420(Unity.Collections.NativeArray<byte> rgba, int width, int height)
    {
        int ySize = width * height;
        int uvWidth = width / 2;
        int uvHeight = height / 2;
        int uvSize = uvWidth * uvHeight;
        sbyte[] result = new sbyte[ySize + uvSize * 2];

        for (int row = 0; row < height; row++)
        {
            int srcRow = height - 1 - row; // flip vertically
            int srcRowOffset = srcRow * width * 4;
            int dstRowOffset = row * width;

            for (int col = 0; col < width; col++)
            {
                int srcIdx = srcRowOffset + col * 4;
                int r = rgba[srcIdx];
                int g = rgba[srcIdx + 1];
                int b = rgba[srcIdx + 2];

                int y = ((66 * r + 129 * g + 25 * b + 128) >> 8) + 16;
                result[dstRowOffset + col] = unchecked((sbyte)(byte)Mathf.Clamp(y, 0, 255));

                if ((row % 2 == 0) && (col % 2 == 0))
                {
                    int u = ((-38 * r - 74 * g + 112 * b + 128) >> 8) + 128;
                    int v = ((112 * r - 94 * g - 18 * b + 128) >> 8) + 128;
                    int uvRow = row / 2;
                    int uvCol = col / 2;
                    result[ySize + uvRow * uvWidth + uvCol] = unchecked((sbyte)(byte)Mathf.Clamp(u, 0, 255));
                    result[ySize + uvSize + uvRow * uvWidth + uvCol] = unchecked((sbyte)(byte)Mathf.Clamp(v, 0, 255));
                }
            }
        }
        return result;
    }
#endif

    private void OnDestroy()
    {
#if UNITY_ANDROID && !UNITY_EDITOR
        if (_fullResRT != null)
        {
            _fullResRT.Release();
            Destroy(_fullResRT);
        }
        if (_captureRT != null)
        {
            _captureRT.Release();
            Destroy(_captureRT);
        }
        _encoder?.Dispose();
#endif
    }
}
