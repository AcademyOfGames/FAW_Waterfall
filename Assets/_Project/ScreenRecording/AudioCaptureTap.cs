using System;
using UnityEngine;

/// <summary>
/// Captures Unity's own final mixed audio output — everything routed through the scene's
/// AudioListener — via OnAudioFilterRead, and hands it to InAppScreenRecorder for muxing into the
/// recorded mp4 alongside the video track.
///
/// Deliberately does NOT use Android's AudioPlaybackCaptureConfiguration (the official "capture
/// this app's own audio output" API added in API 29). That API requires a live MediaProjection
/// token — i.e. exactly the system consent dialog ("Share your screen with FutureArtsWay?") this
/// whole custom in-app recorder exists to avoid. Capturing at the Unity audio-thread level instead
/// needs no OS permission at all: Unity is simply handing us a copy of PCM data it already computed
/// internally for its own mixer, the same way ScreenCapture.CaptureScreenshotIntoRenderTexture hands
/// us pixels Unity already rendered instead of asking the OS for a display-compositor mirror.
///
/// Attach this to the SAME GameObject as the scene's AudioListener (InAppScreenRecorder does this
/// automatically at runtime via AddComponent — no manual scene wiring needed). Being on the
/// AudioListener's GameObject specifically is what makes OnAudioFilterRead receive the fully mixed,
/// post-everything buffer for the whole scene (every AudioSource combined), rather than just one
/// source's output.
///
/// Threading: OnAudioFilterRead fires on Unity's internal audio thread, not the main thread.
/// Calling into an Android/iOS native plugin directly from that thread is unsafe, so samples are
/// only ever written into a lock-protected buffer here; InAppScreenRecorder drains it once per
/// frame from Update() (main thread) and pushes the drained chunk to the native encoder there.
///
/// This never modifies the `data` array it's given, only reads it — audio still plays normally
/// through the device speaker exactly as if this component didn't exist; it's a passive tap, not a
/// filter that changes what's heard.
/// </summary>
public class AudioCaptureTap : MonoBehaviour
{
    /// <summary>Set by InAppScreenRecorder to start/stop accumulating samples. When false,
    /// OnAudioFilterRead still fires (Unity keeps running its DSP graph) but does no work.</summary>
    public bool IsCapturing { get; set; }

    public int SampleRate { get; private set; }

    /// <summary>Channel count of the mixed output, updated from the first real callback. Defaults
    /// to 2 (stereo) before that, since SampleRate is known immediately but channel count is only
    /// reported once OnAudioFilterRead actually fires.</summary>
    public int ChannelCount { get; private set; } = 2;

    private readonly object _lock = new object();
    private short[] _buffer = new short[4096];
    private int _bufferCount;

    private void Awake()
    {
        SampleRate = AudioSettings.outputSampleRate;
    }

    // Called by Unity's audio engine on the audio thread — must stay allocation-light and must
    // never block on anything the main thread might be holding.
    private void OnAudioFilterRead(float[] data, int channels)
    {
        if (!IsCapturing)
            return;

        ChannelCount = channels;

        lock (_lock)
        {
            int needed = _bufferCount + data.Length;
            if (_buffer.Length < needed)
            {
                int newSize = Mathf.Max(needed, _buffer.Length * 2);
                Array.Resize(ref _buffer, newSize);
            }

            for (int i = 0; i < data.Length; i++)
            {
                float clamped = Mathf.Clamp(data[i], -1f, 1f);
                _buffer[_bufferCount + i] = (short)Mathf.RoundToInt(clamped * short.MaxValue);
            }
            _bufferCount += data.Length;
        }
    }

    /// <summary>
    /// Main-thread only. Copies out and clears whatever samples have accumulated since the last
    /// call, or returns null if none are pending yet. Interleaved 16-bit PCM
    /// (e.g. [L,R,L,R,...] for stereo) at <see cref="SampleRate"/> / <see cref="ChannelCount"/>.
    /// </summary>
    public short[] DrainSamples()
    {
        lock (_lock)
        {
            if (_bufferCount == 0)
                return null;
            short[] result = new short[_bufferCount];
            Array.Copy(_buffer, result, _bufferCount);
            _bufferCount = 0;
            return result;
        }
    }

    /// <summary>Discards any buffered samples without returning them — used when starting a fresh
    /// recording so leftover audio captured while idle (if any) never bleeds into the next clip.</summary>
    public void ResetBuffer()
    {
        lock (_lock)
        {
            _bufferCount = 0;
        }
    }
}
