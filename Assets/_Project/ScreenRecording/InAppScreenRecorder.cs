using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using UnityEngine;
using UnityEngine.Rendering;

/// <summary>
/// Cross-platform in-app screen recorder (Android + iOS) — the single recording path for both.
///
/// Captures Unity's own already-rendered frames (ScreenCapture.CaptureScreenshotIntoRenderTexture
/// + AsyncGPUReadback, i.e. a copy of Unity's own back buffer) and encodes them on-device with a
/// native H.264 encoder — InAppFrameEncoder.java (Android, MediaCodec) or InAppFrameEncoder.mm
/// (iOS, AVAssetWriter). The frame capture, RGBA→I420 conversion and audio tap are all shared C#;
/// only the four EncoderConfigure/PushFrame/PushAudio/Stop calls differ per platform.
///
/// Recording Unity's OWN frames (rather than the composited screen) is what keeps native overlays
/// out of the video: the in-app record button (a native Android View / iOS UIView, see
/// FawRecordButton.mm and NativeRecordButton.java) is never rendered into Unity's RenderTexture, so
/// it can't appear in the recording. On iOS this replaced ReplayKit, whose whole-screen system
/// capture had no way to exclude that button. It also means no OS screen-capture broker is involved
/// on either platform — no "Share your screen with FutureArtsWay?" consent dialog (Android's
/// MediaProjection) and no ReplayKit consent alert (iOS): the app only encodes pixels it already
/// produced itself.
///
/// Also captures Unity's own audio output (see AudioCaptureTap.cs) and muxes it into the same mp4
/// alongside the video track — again without MediaProjection/AudioPlaybackCaptureConfiguration,
/// since Unity hands us a copy of PCM data it already computed for its own mixer via
/// OnAudioFilterRead. On stop, the finished clip is immediately copied into the public
/// Movies/FutureArtsWay gallery folder via MediaStore (see MediaStoreSaver.java) so
/// SharePopupController's existing "already saved by the time the popup opens" design holds, same
/// as it did with SmileSoft's auto-save-at-record-start behavior.
///
/// Threading / frame pacing (this is what keeps recording from tanking the framerate):
/// the main thread does as little per captured frame as possible — a GPU screen copy, a GPU
/// downscale blit, and one memcpy of the readback result into a pooled buffer. Everything
/// genuinely expensive (RGBA→I420 color conversion, the JNI marshal of a multi-megabyte frame
/// buffer, and MediaCodec's queue/drain calls) happens on a dedicated background encode thread,
/// which is attached to the JVM for the lifetime of the recording. Captures are also pipelined
/// rather than serialized: the loop no longer stalls waiting for each AsyncGPUReadback to land
/// before scheduling the next capture, so frames go out on an even cadence instead of at
/// "whatever rate the slowest link happened to allow". Presentation timestamps are stamped at
/// capture time rather than at push time for the same reason — readback latency varies by a
/// couple of frames, and letting that variance into the PTS is what makes playback look jittery
/// even when every frame was captured.
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
    [Tooltip("Capture is downscaled to this max height to keep per-frame cost down. Width is " +
        "derived to preserve the screen's aspect ratio. This is the single biggest cost dial: " +
        "GPU readback bandwidth and color-conversion time both scale with pixel count, so 1080 " +
        "costs ~2.25x what 720 does. Lower it if recording still costs framerate on test devices.")]
    [SerializeField] private int maxCaptureHeight = 1080;
    [Tooltip("Capture rate. 30 is the sweet spot — it reads as smooth on playback and leaves the " +
        "encode thread plenty of headroom. Going above 30 mostly buys bandwidth cost rather than " +
        "visible smoothness, and 24 or below starts to look choppy in the recorded clip.")]
    [SerializeField] private int frameRate = 30;
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

#if (UNITY_ANDROID || UNITY_IOS) && !UNITY_EDITOR
    /// <summary>How many captured frames may be somewhere between "readback requested" and
    /// "handed to MediaCodec" at once. This is the pipeline's backpressure valve: AsyncGPUReadback
    /// on mobile GLES3 typically lands 2-3 render frames after the request, so anything less than
    /// ~3 would serialize the pipeline again and cap the real capture rate well below frameRate.
    /// Anything much more just adds latency and memory without adding throughput.</summary>
    private const int MaxPendingFrames = 3;

    // CaptureScreenshotIntoRenderTexture requires its destination RT to match the screen's actual
    // resolution exactly (Unity does not scale for you) — so _fullResRT is always full screen size
    // and is what the OS-free "screenshot" capture writes into. _captureRT is the (usually smaller)
    // encode target; we GPU-Blit from _fullResRT down into it every frame.
    private RenderTexture _fullResRT;
    private RenderTexture _captureRT;
#if UNITY_ANDROID
    private AndroidJavaObject _encoder;
#endif
    private Coroutine _captureRoutine;
    private double _recordingStartRealtime;
    private int _fullResWidth;
    private int _fullResHeight;
    private int _captureWidth;
    private int _captureHeight;
    private int _rgbaBufferSize;
    // Whether the AsyncGPUReadback rows come back bottom-to-top (and so need flipping to be upright).
    // True on OpenGL/GLES (Android), false on Metal/Vulkan/D3D (iOS). Snapshotted at StartRecording
    // so the encode thread reads a plain bool rather than calling into the engine off the main thread.
    private bool _flipCaptureVertically;
    private AudioCaptureTap _audioTap;
    // Snapshotted at StartRecording so the encode thread never has to touch the AudioCaptureTap
    // component: `unityObject != null` is an overloaded operator that calls into native engine code
    // and is not safe off the main thread. These are also exactly the values configure() was given,
    // so the audio PTS math can't drift from what the encoder was actually set up for.
    private int _audioSampleRate;
    private int _audioChannelCount;

    // ── Encode-thread plumbing ───────────────────────────────────────────────
    // Only the encode thread ever touches _encoder once recording is under way. The main thread
    // configures it before the thread starts and stops it after the thread has been joined, so no
    // locking around the AndroidJavaObject itself is needed.
    private readonly object _queueLock = new object();
    private readonly Queue<WorkItem> _workQueue = new Queue<WorkItem>();
    private readonly Stack<byte[]> _rgbaPool = new Stack<byte[]>();
    private Thread _encodeThread;
    private volatile bool _workerRunning;
    private sbyte[] _i420Scratch;     // encode thread only
    private long _lastVideoPts;       // encode thread only
    private long _audioSamplesWritten; // encode thread only

    // Diagnostics — written from both threads via Interlocked, read on the main thread at stop.
    private int _pendingVideoFrames;
    private int _framesEncoded;
    private int _framesThrottled;
    private int _readbackErrorCount;
    private int _pushFailCount;
    private int _audioPushFailCount;

    /// <summary>One unit of work for the encode thread: either a video frame (Rgba set) or a chunk
    /// of PCM audio (Pcm set), never both.</summary>
    private struct WorkItem
    {
        public byte[] Rgba;
        public short[] Pcm;
        public long Pts; // video only; audio PTS is derived from the running sample count
    }
#endif
    private Action<string> _pendingStopCallback;

#if (UNITY_ANDROID || UNITY_IOS) && !UNITY_EDITOR
#if UNITY_IOS
    // Absolute path of the clip being written this session. iOS's native encoder stop() returns
    // only success/failure (not the path, the way the Android one does), so we remember it.
    private string _outputPath;
    // Local file path of the most recently finished clip — used by the iOS share sheet.
    private string _lastLocalPath;
    private bool _encoderConfigured;

    [DllImport("__Internal")] private static extern bool IAFE_Configure(string outPath, int width, int height, int fps, int bitRate, int audioSampleRate, int audioChannelCount);
    [DllImport("__Internal")] private static extern bool IAFE_PushFrame(sbyte[] i420, int length, long ptsUs);
    [DllImport("__Internal")] private static extern bool IAFE_PushAudio(short[] pcm, int sampleCount, long ptsUs);
    [DllImport("__Internal")] private static extern bool IAFE_Stop();
    [DllImport("__Internal")] private static extern void IAFE_SaveToPhotos(string path);
    [DllImport("__Internal")] private static extern void IAFE_ShareVideo(string path, string title);
#endif

    // ── Native encoder abstraction ────────────────────────────────────────────
    // The capture pipeline, RGBA→I420 conversion and audio tap are all shared; only these calls
    // differ per platform. Android talks to InAppFrameEncoder.java over JNI; iOS talks to
    // InAppFrameEncoder.mm over [DllImport("__Internal")].

    private bool EncoderConfigure(string outPath, int width, int height, int fps, int bitrate,
        bool swapUV, int audioSampleRate, int audioChannelCount)
    {
#if UNITY_ANDROID
        try
        {
            _encoder = new AndroidJavaObject(EncoderClassName);
        }
        catch (Exception e)
        {
            Debug.LogError("[InAppScreenRecorder] Failed to construct native encoder (" + EncoderClassName +
                "). Make sure InAppFrameEncoder.java is present under Assets/Plugins/Android/com/FutureArts/" +
                "FutureArtsWay/inapprecorder/ and the project has been rebuilt since adding it. Exception: " + e);
            return false;
        }
        bool ok = _encoder.Call<bool>("configure", outPath, width, height, fps, bitrate, swapUV, audioSampleRate, audioChannelCount);
        if (!ok)
        {
            _encoder.Dispose();
            _encoder = null;
        }
        return ok;
#elif UNITY_IOS
        _outputPath = outPath;
        _encoderConfigured = IAFE_Configure(outPath, width, height, fps, bitrate, audioSampleRate, audioChannelCount);
        return _encoderConfigured;
#else
        return false;
#endif
    }

    private bool EncoderReady()
    {
#if UNITY_ANDROID
        return _encoder != null;
#elif UNITY_IOS
        return _encoderConfigured;
#else
        return false;
#endif
    }

    private bool EncoderPushFrame(sbyte[] i420, long pts)
    {
#if UNITY_ANDROID
        return _encoder != null && _encoder.Call<bool>("pushFrame", i420, pts);
#elif UNITY_IOS
        return _encoderConfigured && IAFE_PushFrame(i420, i420.Length, pts);
#else
        return false;
#endif
    }

    private bool EncoderPushAudio(short[] pcm, long pts)
    {
#if UNITY_ANDROID
        return _encoder != null && _encoder.Call<bool>("pushAudioSamples", pcm, pts);
#elif UNITY_IOS
        return _encoderConfigured && IAFE_PushAudio(pcm, pcm.Length, pts);
#else
        return false;
#endif
    }

    /// <summary>Finalizes the mp4 and returns its path (or null on failure).</summary>
    private string EncoderStop()
    {
#if UNITY_ANDROID
        if (_encoder == null) return null;
        string path = _encoder.Call<string>("stop");
        _encoder.Dispose();
        _encoder = null;
        return path;
#elif UNITY_IOS
        if (!_encoderConfigured) return null;
        bool ok = IAFE_Stop();
        _encoderConfigured = false;
        return ok ? _outputPath : null;
#else
        return null;
#endif
    }
#endif

    private void Awake()
    {
        if (instance != null && instance != this)
        {
            Debug.LogWarning("[InAppScreenRecorder] Duplicate instance in scene — destroying this one.");
            Destroy(gameObject);
            return;
        }
        instance = this;

#if (UNITY_ANDROID || UNITY_IOS) && !UNITY_EDITOR
        SetupAudioTap();
#endif
    }

#if (UNITY_ANDROID || UNITY_IOS) && !UNITY_EDITOR
    /// <summary>
    /// Finds the currently loaded scene's AudioListener and attaches an AudioCaptureTap to the same
    /// GameObject — that's what lets OnAudioFilterRead see the fully mixed, post-everything audio
    /// for the whole scene. No manual scene wiring needed; if no AudioListener exists, recording
    /// simply proceeds video-only.
    ///
    /// Must be re-run before every recording, not just once in Awake(): this component lives under
    /// PersistentRecordingRoot (DontDestroyOnLoad) but the AudioListener does not — it sits on the
    /// AR camera inside each scene, and ExperienceSceneLoadingManager loads experiences with
    /// LoadSceneMode.Single, which destroys it. Awake() only ever runs once, so a tap resolved
    /// there points at a destroyed component from StartScene the moment you enter an experience,
    /// and every recording in Alina/Divine/Benaroya would silently come out video-only.
    /// </summary>
    private void SetupAudioTap()
    {
        var listener = FindObjectOfType<AudioListener>();
        if (listener == null)
        {
            _audioTap = null;
            Debug.LogWarning("[InAppScreenRecorder] No AudioListener found in the scene — recordings will have no audio.");
            return;
        }
        _audioTap = listener.GetComponent<AudioCaptureTap>();
        if (_audioTap == null)
            _audioTap = listener.gameObject.AddComponent<AudioCaptureTap>();
    }
#endif

    private void Update()
    {
#if (UNITY_ANDROID || UNITY_IOS) && !UNITY_EDITOR
        if (!IsRecording || _audioTap == null)
            return;

        // Drain-and-hand-off only: the actual PCM→AAC push is a JNI call and is done on the encode
        // thread. Audio chunks are small (a few KB) so this costs nothing measurable here.
        short[] samples = _audioTap.DrainSamples();
        if (samples != null && samples.Length > 0)
            EnqueueAudio(samples);
#endif
    }

    // ── Public API (mirrors SmileSoftScreenRecordController's shape used by the button/popup) ──

    public void StartRecording()
    {
        if (IsRecording)
        {
            Debug.LogWarning("[InAppScreenRecorder] StartRecording() called while already recording — ignoring.");
            return;
        }

#if (!UNITY_ANDROID && !UNITY_IOS) || UNITY_EDITOR
        Debug.LogWarning("[InAppScreenRecorder] StartRecording() is only implemented for Android/iOS device " +
            "builds — no-op in the Editor or on other platforms.");
#else
        if (!SystemInfo.supportsAsyncGPUReadback)
        {
            Debug.LogError("[InAppScreenRecorder] This device/graphics API reports no AsyncGPUReadback support — " +
                "the capture pipeline can't run. Recording aborted.");
            return;
        }

        _fullResWidth = RoundToEven(Screen.width);
        _fullResHeight = RoundToEven(Screen.height);

        // AsyncGPUReadback returns rows in the graphics API's native texture order: bottom-to-top on
        // OpenGL/GLES (Android), top-to-bottom on Metal/Vulkan/D3D (iOS). graphicsUVStartsAtTop is
        // false for the former, true for the latter — so we flip only when it starts at the bottom,
        // otherwise the video comes out upside-down (GLES stays flipped as before; Metal doesn't).
        _flipCaptureVertically = !SystemInfo.graphicsUVStartsAtTop;

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

        // Re-resolve against whatever scene is loaded right now — see SetupAudioTap()'s remarks on
        // why a tap cached in Awake() is stale as soon as the first experience scene loads.
        SetupAudioTap();

        _audioSampleRate = 0;
        _audioChannelCount = 0;
        if (_audioTap != null)
        {
            _audioSampleRate = _audioTap.SampleRate;
            _audioChannelCount = Mathf.Max(1, _audioTap.ChannelCount);
        }

        if (!EncoderConfigure(outputPath, _captureWidth, _captureHeight, frameRate,
                bitrate, swapUAndVPlanes, _audioSampleRate, _audioChannelCount))
        {
            Debug.LogError("[InAppScreenRecorder] Native encoder configure() failed — aborting. " +
                "Android: check logcat \"InAppFrameEncoder\". iOS: check the Xcode console \"[InAppFrameEncoder]\" logs.");
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
            _fullResRT.filterMode = FilterMode.Bilinear; // the Blit below downscales by sampling this
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

        _framesEncoded = 0;
        _framesThrottled = 0;
        _pendingVideoFrames = 0;
        _pushFailCount = 0;
        _readbackErrorCount = 0;
        _audioPushFailCount = 0;
        if (_audioTap != null)
        {
            _audioTap.ResetBuffer();
            _audioTap.IsCapturing = true;
        }
        _recordingStartRealtime = Time.realtimeSinceStartupAsDouble;
        IsRecording = true;
        StartEncodeThread();
        _captureRoutine = StartCoroutine(CaptureLoop());
#endif
    }

    public void StopRecording(Action<string> callback)
    {
        _pendingStopCallback = callback;

#if (!UNITY_ANDROID && !UNITY_IOS) || UNITY_EDITOR
        Debug.LogWarning("[InAppScreenRecorder] StopRecording() is only implemented for Android/iOS device builds.");
        InvokePendingCallback(string.Empty);
#else
        if (!IsRecording)
        {
            Debug.LogWarning("[InAppScreenRecorder] StopRecording() called but not currently recording.");
            InvokePendingCallback(string.Empty);
            return;
        }

        IsRecording = false;
        if (_audioTap != null)
            _audioTap.IsCapturing = false;
        if (_captureRoutine != null)
        {
            StopCoroutine(_captureRoutine);
            _captureRoutine = null;
        }

        StartCoroutine(FinishAndSave());
#endif
    }

    /// <summary>Hands the most recent recording off to the native OS share sheet
    /// (Android: the saved gallery content:// URI; iOS: the local file via UIActivityViewController).</summary>
    public void ShareLastRecording(string chooserTitle)
    {
#if UNITY_ANDROID && !UNITY_EDITOR
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
#elif UNITY_IOS && !UNITY_EDITOR
        if (string.IsNullOrEmpty(_lastLocalPath))
        {
            Debug.LogError("[InAppScreenRecorder] ShareLastRecording(): no recorded file path available.");
            return;
        }
        IAFE_ShareVideo(_lastLocalPath, chooserTitle);
#else
        Debug.LogWarning("[InAppScreenRecorder] ShareLastRecording() is only implemented for Android/iOS device builds.");
#endif
    }

    private void InvokePendingCallback(string path)
    {
        var cb = _pendingStopCallback;
        _pendingStopCallback = null;
        cb?.Invoke(path);
    }

#if (UNITY_ANDROID || UNITY_IOS) && !UNITY_EDITOR

    // ── Capture (main thread) ────────────────────────────────────────────────

    /// <summary>
    /// Paces captures on an even wall-clock grid and fires off a non-blocking readback for each.
    /// Deliberately does NOT wait for a readback to complete before scheduling the next capture:
    /// AsyncGPUReadback needs a couple of render frames to land, and waiting on it turned the
    /// nominal frame interval into "readback latency + conversion + JNI push", which both capped
    /// the real rate far below the configured frameRate and made the gaps between frames uneven.
    /// </summary>
    private IEnumerator CaptureLoop()
    {
        var endOfFrame = new WaitForEndOfFrame();
        double interval = 1.0 / Mathf.Max(1, frameRate);
        double nextCaptureTime = Time.realtimeSinceStartupAsDouble;

        while (IsRecording)
        {
            yield return endOfFrame;

            double now = Time.realtimeSinceStartupAsDouble;
            if (now < nextCaptureTime)
                continue;

            // Advance by exactly one interval so captures stay on a regular grid rather than
            // drifting to wherever the render frame boundary happened to fall. If we've slipped a
            // whole interval behind (app hitch), resync instead of firing a catch-up burst.
            nextCaptureTime += interval;
            if (nextCaptureTime < now)
                nextCaptureTime = now + interval;

            if (Volatile.Read(ref _pendingVideoFrames) >= MaxPendingFrames)
            {
                // Pipeline is full — the encode thread or the GPU is the bottleneck right now.
                // Skipping is correct: PTS is real wall-clock time, so a skipped frame just means
                // a slightly longer gap in the clip, never a speed-up or a desync.
                Interlocked.Increment(ref _framesThrottled);
                continue;
            }

            ScreenCapture.CaptureScreenshotIntoRenderTexture(_fullResRT);
            Graphics.Blit(_fullResRT, _captureRT); // GPU-side downscale (or 1:1 copy) — cheap, unlike a CPU resize

            // Stamp the PTS now, at the moment these pixels existed on screen — not when the
            // readback eventually lands. Readback latency varies by a frame or two, and letting
            // that variance into the timeline is exactly what makes playback look jittery even
            // when the capture cadence itself was perfectly even.
            long pts = (long)((now - _recordingStartRealtime) * 1_000_000.0);
            Interlocked.Increment(ref _pendingVideoFrames);
            AsyncGPUReadback.Request(_captureRT, 0, TextureFormat.RGBA32, req => OnFrameReadback(req, pts));
        }
    }

    /// <summary>
    /// Main thread, called by Unity when a readback lands. Does exactly one memcpy out of the
    /// readback buffer into a pooled array and hands it to the encode thread — the copy is needed
    /// because the NativeArray behind the request is only valid for the duration of this callback.
    /// </summary>
    private void OnFrameReadback(AsyncGPUReadbackRequest request, long pts)
    {
        bool handedOff = false;
        try
        {
            if (!_workerRunning || !EncoderReady())
                return;

            if (request.hasError)
            {
                int errors = Interlocked.Increment(ref _readbackErrorCount);
                if (errors <= 3 || errors % 30 == 0)
                    Debug.LogWarning($"[InAppScreenRecorder] AsyncGPUReadback error (count={errors}) — dropping this frame.");
                return;
            }

            var data = request.GetData<byte>();
            if (data.Length != _rgbaBufferSize)
            {
                Debug.LogError($"[InAppScreenRecorder] Readback returned {data.Length} bytes, expected " +
                    $"{_rgbaBufferSize} for {_captureWidth}x{_captureHeight} RGBA32 — dropping this frame.");
                return;
            }

            byte[] rgba = RentRgbaBuffer();
            data.CopyTo(rgba);
            handedOff = TryEnqueueFrame(rgba, pts);
            if (!handedOff)
                ReturnRgbaBuffer(rgba);
        }
        finally
        {
            // On the hand-off path the encode thread releases this pipeline slot once the frame has
            // actually been encoded; every other path has to release it right here or the capture
            // loop would throttle itself permanently.
            if (!handedOff)
                Interlocked.Decrement(ref _pendingVideoFrames);
        }
    }

    private byte[] RentRgbaBuffer()
    {
        lock (_queueLock)
        {
            if (_rgbaPool.Count > 0)
                return _rgbaPool.Pop();
        }
        return new byte[_rgbaBufferSize];
    }

    private void ReturnRgbaBuffer(byte[] buffer)
    {
        if (buffer == null || buffer.Length != _rgbaBufferSize)
            return;
        lock (_queueLock)
        {
            if (_rgbaPool.Count < MaxPendingFrames + 1)
                _rgbaPool.Push(buffer);
        }
    }

    /// <summary>Returns false if the encode thread is shutting down and refused the frame — the
    /// caller then still owns the buffer and the pipeline slot.</summary>
    private bool TryEnqueueFrame(byte[] rgba, long pts)
    {
        lock (_queueLock)
        {
            if (!_workerRunning)
                return false;
            _workQueue.Enqueue(new WorkItem { Rgba = rgba, Pts = pts });
            Monitor.Pulse(_queueLock);
            return true;
        }
    }

    private void EnqueueAudio(short[] pcm)
    {
        lock (_queueLock)
        {
            if (!_workerRunning)
                return;
            _workQueue.Enqueue(new WorkItem { Pcm = pcm });
            Monitor.Pulse(_queueLock);
        }
    }

    // ── Encode thread ────────────────────────────────────────────────────────

    private void StartEncodeThread()
    {
        _rgbaBufferSize = _captureWidth * _captureHeight * 4;
        _i420Scratch = new sbyte[_captureWidth * _captureHeight + 2 * ((_captureWidth / 2) * (_captureHeight / 2))];
        _lastVideoPts = -1;
        _audioSamplesWritten = 0;

        lock (_queueLock)
        {
            _workQueue.Clear();
            _rgbaPool.Clear();
            _workerRunning = true;
        }

        _encodeThread = new Thread(EncodeThreadMain)
        {
            Name = "InAppRecorderEncode",
            IsBackground = true,
            // Below the main/render threads on purpose: if the device is genuinely saturated we'd
            // rather the recording drop a frame than the app drop one.
            Priority = System.Threading.ThreadPriority.BelowNormal,
        };
        _encodeThread.Start();
    }

    /// <summary>
    /// Owns every native encoder call for the duration of a recording. On Android that means every
    /// JNI call into InAppFrameEncoder, which requires the thread to be attached to the JVM (an
    /// unattached thread would crash the process); iOS's DllImport calls have no such requirement.
    /// The main thread only touches the encoder before this thread starts and after it has exited.
    /// </summary>
    private void EncodeThreadMain()
    {
#if UNITY_ANDROID
        // AndroidJavaObject calls from an unattached thread crash the process; iOS's DllImport calls
        // have no such requirement, so the attach/detach is Android-only.
        AndroidJNI.AttachCurrentThread();
#endif
        try
        {
            while (true)
            {
                WorkItem item;
                lock (_queueLock)
                {
                    while (_workQueue.Count == 0 && _workerRunning)
                        Monitor.Wait(_queueLock);
                    if (_workQueue.Count == 0)
                        break; // shutdown requested and the backlog is fully drained
                    item = _workQueue.Dequeue();
                }
                ProcessWorkItem(item);
            }
        }
        catch (Exception e)
        {
            Debug.LogError("[InAppScreenRecorder] Encode thread aborted: " + e);
        }
        finally
        {
#if UNITY_ANDROID
            AndroidJNI.DetachCurrentThread();
#endif
        }
    }

    private void ProcessWorkItem(WorkItem item)
    {
        if (item.Rgba != null)
        {
            try
            {
                // _i420Scratch is nulled by ReleaseEncodeBuffers() once this thread is believed to
                // have exited; if it hadn't quite, drop the frame rather than dereference null.
                var scratch = _i420Scratch;
                if (!EncoderReady() || scratch == null)
                    return;

                RgbaToI420(item.Rgba, scratch, _captureWidth, _captureHeight, _flipCaptureVertically);

                // Both encoders require strictly increasing presentation timestamps. Readback
                // callbacks arrive in request order so this should never trigger, but a duplicate
                // timestamp would poison the whole track, so clamp rather than trust.
                long pts = item.Pts;
                if (pts <= _lastVideoPts)
                    pts = _lastVideoPts + 1;
                _lastVideoPts = pts;

                if (EncoderPushFrame(scratch, pts))
                {
                    Interlocked.Increment(ref _framesEncoded);
                }
                else
                {
                    int fails = Interlocked.Increment(ref _pushFailCount);
                    if (fails <= 3 || fails % 30 == 0)
                    {
                        Debug.LogWarning($"[InAppScreenRecorder] pushFrame() returned false " +
                            $"(pushFailCount={fails}) — check the native \"InAppFrameEncoder\" logs.");
                    }
                }
            }
            finally
            {
                ReturnRgbaBuffer(item.Rgba);
                Interlocked.Decrement(ref _pendingVideoFrames);
            }
            return;
        }

        if (item.Pcm == null || item.Pcm.Length == 0 || !EncoderReady() || _audioSampleRate <= 0)
            return;

        long audioPts = (long)((double)_audioSamplesWritten / _audioSampleRate * 1_000_000.0);
        if (EncoderPushAudio(item.Pcm, audioPts))
        {
            _audioSamplesWritten += item.Pcm.Length / Mathf.Max(1, _audioChannelCount);
        }
        else
        {
            int fails = Interlocked.Increment(ref _audioPushFailCount);
            if (fails <= 3 || fails % 30 == 0)
            {
                Debug.LogWarning($"[InAppScreenRecorder] pushAudioSamples() returned false " +
                    $"(audioPushFailCount={fails}) — check the native \"InAppFrameEncoder\" logs.");
            }
        }
    }

    /// <summary>Signals shutdown and wakes the encode thread; it exits once its backlog is drained.
    /// Does not block — callers that need the thread gone should wait on <see cref="_encodeThread"/>.</summary>
    private void SignalEncodeThreadStop()
    {
        lock (_queueLock)
        {
            _workerRunning = false;
            Monitor.PulseAll(_queueLock);
        }
    }

    /// <summary>Drops the pooled frame buffers once the encode thread is gone. At 1080-tall capture
    /// that's ~10MB of managed arrays that would otherwise sit resident between recordings, on a
    /// device that also has an AR session and Cesium tiles competing for memory.</summary>
    private void ReleaseEncodeBuffers()
    {
        lock (_queueLock)
        {
            _workQueue.Clear();
            _rgbaPool.Clear();
        }
        _i420Scratch = null;
    }

    // ── Stop / save ──────────────────────────────────────────────────────────

    private IEnumerator FinishAndSave()
    {
        // Flush any audio captured on the audio thread since the last Update() drain — Update()
        // won't necessarily run again between StopRecording() setting IsRecording=false and this
        // coroutine running, so without this final drain the last few tens of milliseconds of
        // audio could be dropped.
        if (_audioTap != null)
        {
            short[] finalSamples = _audioTap.DrainSamples();
            if (finalSamples != null && finalSamples.Length > 0)
                EnqueueAudio(finalSamples);
        }

        // Let the frames that were still in flight when the user hit stop finish landing, so the
        // clip doesn't lose its last ~100ms. Bounded, because a wedged readback must not hang stop.
        double waitDeadline = Time.realtimeSinceStartupAsDouble + 0.5;
        while (Volatile.Read(ref _pendingVideoFrames) > 0 && Time.realtimeSinceStartupAsDouble < waitDeadline)
            yield return null;

        // Yield rather than Join so the encode thread can chew through its backlog without
        // freezing the frame the user just tapped Stop on.
        SignalEncodeThreadStop();
        waitDeadline = Time.realtimeSinceStartupAsDouble + 3.0;
        while (_encodeThread != null && _encodeThread.IsAlive && Time.realtimeSinceStartupAsDouble < waitDeadline)
            yield return null;

        if (_encodeThread != null && _encodeThread.IsAlive)
            Debug.LogWarning("[InAppScreenRecorder] Encode thread still running after 3s — stopping the encoder anyway.");
        _encodeThread = null;
        ReleaseEncodeBuffers();

        int framesEncoded = _framesEncoded;
        int framesThrottled = _framesThrottled;
        Debug.Log($"[InAppScreenRecorder] Recording finished: {framesEncoded} frames encoded at " +
            $"{_captureWidth}x{_captureHeight}, {framesThrottled} skipped for backpressure, " +
            $"{_readbackErrorCount} readback errors, {_pushFailCount} encoder rejections.");

        string path = EncoderStop();
        if (string.IsNullOrEmpty(path))
            Debug.LogError("[InAppScreenRecorder] FinishAndSave(): encoder stop returned no path — nothing to save.");
        yield return null; // let the native writer/muxer finalize the file before touching it

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

#if UNITY_ANDROID
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
#elif UNITY_IOS
        _lastLocalPath = path;
        // Copy into the Photos camera roll (async, fire-and-forget). The local temp file remains and
        // is what the preview/share use — matching Android's "already saved by popup time" behavior.
        IAFE_SaveToPhotos(path);
#endif

        // Local path is what gets handed to RecordingPreviewController's VideoPlayer for the looping
        // preview — the gallery/Photos copy above is separate, purely for Save/Share.
        InvokePendingCallback(path);
    }

#if UNITY_ANDROID
    private static AndroidJavaObject GetCurrentActivity()
    {
        using var unityPlayer = new AndroidJavaClass("com.unity3d.player.UnityPlayer");
        return unityPlayer.GetStatic<AndroidJavaObject>("currentActivity");
    }
#endif

    private static int RoundToEven(int value) => value % 2 == 0 ? value : value + 1;

    /// <summary>
    /// RGBA32 (from AsyncGPUReadback) converted into a caller-owned, tightly packed I420 buffer
    /// (full-res Y, then half-res U, then half-res V), emitting top-to-bottom row order so the video
    /// isn't upside down. The readback's own row order is graphics-API dependent — bottom-to-top on
    /// OpenGL/GLES, top-to-bottom on Metal/Vulkan/D3D — so <paramref name="flipVertically"/> (derived
    /// from !SystemInfo.graphicsUVStartsAtTop by the caller) says whether to reverse rows here.
    /// BT.601 studio-swing (16-235) integer coefficients — the standard conversion MediaCodec
    /// expects. Runs on the encode thread; the destination buffer is reused across frames so this
    /// allocates nothing (the previous version allocated a fresh multi-megabyte array per frame,
    /// which at 30-45fps was a GC spike generator all by itself).
    ///
    /// Luma and chroma are separate loops rather than one loop with an `if (row % 2 == 0 &amp;&amp;
    /// col % 2 == 0)` inside it — the chroma test was evaluated once per pixel to do work on a
    /// quarter of them, and hoisting it out removes a branch and two modulos from the hot path.
    ///
    /// The destination is sbyte[] rather than byte[] purely to match Java's signed byte type:
    /// passing a C# byte[] across the AndroidJavaObject bridge goes through a legacy/obsolete JNI
    /// conversion path (logs "AndroidJNIHelper: converting Byte array is obsolete, use SByte array
    /// instead" once per frame — harmless but very noisy). sbyte[] marshals directly to Java's
    /// byte[] with no conversion. The bit pattern is identical either way (Java only reads these as
    /// raw pixel bytes, never as signed magnitudes), so a plain unchecked (sbyte) cast of the 0-255
    /// value is correct.
    /// </summary>
    private static void RgbaToI420(byte[] rgba, sbyte[] dst, int width, int height, bool flipVertically)
    {
        int ySize = width * height;
        int uvWidth = width / 2;
        int uvHeight = height / 2;
        int uPlane = ySize;
        int vPlane = ySize + uvWidth * uvHeight;

        for (int row = 0; row < height; row++)
        {
            int srcRow = flipVertically ? (height - 1 - row) : row;
            int src = srcRow * width * 4;
            int dstRow = row * width;
            for (int col = 0; col < width; col++)
            {
                int r = rgba[src];
                int g = rgba[src + 1];
                int b = rgba[src + 2];
                src += 4;

                int y = ((66 * r + 129 * g + 25 * b + 128) >> 8) + 16;
                dst[dstRow + col] = unchecked((sbyte)(byte)(y < 0 ? 0 : (y > 255 ? 255 : y)));
            }
        }

        // One chroma sample per 2x2 block, taken from the block's top-left pixel — the same pixels
        // the old combined loop selected, so output is byte-identical to the previous version.
        for (int uvRow = 0; uvRow < uvHeight; uvRow++)
        {
            int srcPixRow = flipVertically ? (height - 1 - uvRow * 2) : (uvRow * 2);
            int src = srcPixRow * width * 4;
            int dstRow = uvRow * uvWidth;
            for (int uvCol = 0; uvCol < uvWidth; uvCol++)
            {
                int r = rgba[src];
                int g = rgba[src + 1];
                int b = rgba[src + 2];
                src += 8; // every other pixel

                int u = ((-38 * r - 74 * g + 112 * b + 128) >> 8) + 128;
                int v = ((112 * r - 94 * g - 18 * b + 128) >> 8) + 128;
                dst[uPlane + dstRow + uvCol] = unchecked((sbyte)(byte)(u < 0 ? 0 : (u > 255 ? 255 : u)));
                dst[vPlane + dstRow + uvCol] = unchecked((sbyte)(byte)(v < 0 ? 0 : (v > 255 ? 255 : v)));
            }
        }
    }
#endif

    private void OnDestroy()
    {
#if (UNITY_ANDROID || UNITY_IOS) && !UNITY_EDITOR
        IsRecording = false;
        if (_encodeThread != null)
        {
            SignalEncodeThreadStop();
            if (!_encodeThread.Join(2000))
                Debug.LogWarning("[InAppScreenRecorder] Encode thread didn't exit within 2s on teardown.");
            _encodeThread = null;
            ReleaseEncodeBuffers();
        }
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
        if (_audioTap != null)
            _audioTap.IsCapturing = false;
#if UNITY_ANDROID
        _encoder?.Dispose();
        _encoder = null;
#elif UNITY_IOS
        if (_encoderConfigured) { IAFE_Stop(); _encoderConfigured = false; }
#endif
#endif
    }
}
