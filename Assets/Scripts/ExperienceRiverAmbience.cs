using System.Collections;
using UnityEngine;

/// <summary>
/// Looped river bed: fades in before fish followers release, plays through the experience, fades out at the end.
/// </summary>
[DisallowMultipleComponent]
public class ExperienceRiverAmbience : MonoBehaviour
{
    private const string DefaultRiverClipPath = "Assets/_artAssets/Alina/sound/TualatinRiverrecording.WAV";

    [SerializeField] private AudioClip riverClip;
    [Tooltip("Begin the river fade-in this many seconds before fish followers activate.")]
    [SerializeField] private float leadSecondsBeforeFish = 10f;
    [Tooltip("Seconds to ramp from start volume to target volume.")]
    [SerializeField] private float fadeInDurationSeconds = 10f;
    [Range(0f, 1f)]
    [SerializeField] private float startVolume;
    [Range(0f, 1f)]
    [SerializeField] private float targetVolume = 0.95f;
    [SerializeField] private float fadeOutDurationSeconds = 2.5f;
    [SerializeField] private bool loopRiver = true;
    [Range(0f, 1f)]
    [SerializeField] private float spatialBlend;

    [Header("End Detection")]
    [Tooltip("When the fish orchestrator loops, stop the river after the first A->B cycle.")]
    [SerializeField] private bool stopAfterOneCycleIfOrchestratorLoops = true;

    private AudioSource _source;
    private Coroutine _fadeRoutine;
    private Coroutine _endMonitorRoutine;
    private bool _fadeInStarted;
    private SplineFishGroupOrchestrator _subscribedOrchestrator;
    private System.Action _cycleCompleteHandler;

    public float LeadSecondsBeforeFish => leadSecondsBeforeFish;

    private void OnValidate()
    {
        leadSecondsBeforeFish = Mathf.Max(0f, leadSecondsBeforeFish);
        fadeInDurationSeconds = Mathf.Max(0.01f, fadeInDurationSeconds);
        fadeOutDurationSeconds = Mathf.Max(0.01f, fadeOutDurationSeconds);
        startVolume = Mathf.Clamp01(startVolume);
        targetVolume = Mathf.Clamp01(targetVolume);
        spatialBlend = Mathf.Clamp01(spatialBlend);
#if UNITY_EDITOR
        TryAssignDefaultRiverClipInEditor();
#endif
    }

    private void OnDisable()
    {
        UnsubscribeOrchestrator();
        StopAllRiverCoroutines();
        StopSourceImmediate();
    }

    /// <summary>Starts the quiet-to-full fade-in once (called when plant growth is near finished).</summary>
    /// <summary>Fades river audio out (e.g. after Stage 4 viewer encounter ends).</summary>
    public void RequestFadeOut()
    {
        StopFadeRoutine();
        _fadeRoutine = StartCoroutine(FadeOutAndStopRoutine());
    }

    public void BeginFadeIn()
    {
        if (_fadeInStarted || riverClip == null)
        {
            return;
        }

        _fadeInStarted = true;
        EnsureAudioSource();
        StopFadeRoutine();
        _fadeRoutine = StartCoroutine(FadeInRoutine());
    }

    /// <summary>Call when fish release begins to track when the experience ends.</summary>
    public void BeginExperienceEndMonitoring(
        SplineFishGroupOrchestrator orchestrator,
        VertexPathSwarmFollower singleSwarm,
        float followerStaggerSeconds,
        float extraFishDelaySeconds)
    {
        StopEndMonitor();
        _endMonitorRoutine = StartCoroutine(
            MonitorExperienceEndRoutine(orchestrator, singleSwarm, followerStaggerSeconds, extraFishDelaySeconds)
        );
    }

    private IEnumerator FadeInRoutine()
    {
        EnsureAudioSource();
        _source.clip = riverClip;
        _source.loop = loopRiver;
        _source.volume = startVolume;
        if (!_source.isPlaying)
        {
            _source.Play();
        }

        float elapsed = 0f;
        while (elapsed < fadeInDurationSeconds)
        {
            elapsed += Time.deltaTime;
            _source.volume = Mathf.Lerp(startVolume, targetVolume, elapsed / fadeInDurationSeconds);
            yield return null;
        }

        _source.volume = targetVolume;
        _fadeRoutine = null;
    }

    private IEnumerator MonitorExperienceEndRoutine(
        SplineFishGroupOrchestrator orchestrator,
        VertexPathSwarmFollower singleSwarm,
        float followerStaggerSeconds,
        float extraFishDelaySeconds)
    {
        if (orchestrator != null)
        {
            yield return MonitorOrchestratorEndRoutine(orchestrator);
            yield break;
        }

        if (singleSwarm != null)
        {
            float wait = Mathf.Max(0f, extraFishDelaySeconds) + Mathf.Max(0f, followerStaggerSeconds);
            if (wait > 0f)
            {
                yield return new WaitForSeconds(wait);
            }
        }

        // Single-swarm experiences keep fish on the path with no global end — river plays until disabled.
    }

    private IEnumerator MonitorOrchestratorEndRoutine(SplineFishGroupOrchestrator orchestrator)
    {
        if (!orchestrator.LoopForever)
        {
            while (orchestrator.IsSequenceRunning)
            {
                yield return null;
            }

            // Stage 4 viewer encounter calls RequestFadeOut() when the orbit phase ends.
            yield break;
        }

        if (!stopAfterOneCycleIfOrchestratorLoops)
        {
            yield break;
        }

        bool cycleFinished = false;
        void OnCycleComplete()
        {
            cycleFinished = true;
        }

        SubscribeOrchestrator(orchestrator, OnCycleComplete);

        while (!cycleFinished && orchestrator.IsSequenceRunning)
        {
            yield return null;
        }

        if (!cycleFinished && !orchestrator.IsSequenceRunning)
        {
            cycleFinished = true;
        }

        UnsubscribeOrchestrator();
        yield return FadeOutAndStopRoutine();
    }

    private void SubscribeOrchestrator(SplineFishGroupOrchestrator orchestrator, System.Action handler)
    {
        UnsubscribeOrchestrator();
        _subscribedOrchestrator = orchestrator;
        _cycleCompleteHandler = handler;
        _subscribedOrchestrator.SequenceCycleCompleted += handler;
    }

    private void UnsubscribeOrchestrator()
    {
        if (_subscribedOrchestrator != null && _cycleCompleteHandler != null)
        {
            _subscribedOrchestrator.SequenceCycleCompleted -= _cycleCompleteHandler;
        }

        _subscribedOrchestrator = null;
        _cycleCompleteHandler = null;
    }

    private IEnumerator FadeOutAndStopRoutine()
    {
        if (_source == null || !_source.isPlaying)
        {
            yield break;
        }

        float fromVolume = _source.volume;
        float elapsed = 0f;
        while (elapsed < fadeOutDurationSeconds)
        {
            elapsed += Time.deltaTime;
            _source.volume = Mathf.Lerp(fromVolume, 0f, elapsed / fadeOutDurationSeconds);
            yield return null;
        }

        StopSourceImmediate();
    }

    private void EnsureAudioSource()
    {
        if (_source != null)
        {
            return;
        }

        _source = GetComponent<AudioSource>();
        if (_source == null)
        {
            _source = gameObject.AddComponent<AudioSource>();
        }

        _source.playOnAwake = false;
        _source.loop = loopRiver;
        _source.spatialBlend = spatialBlend;
        _source.volume = startVolume;
    }

    private void StopFadeRoutine()
    {
        if (_fadeRoutine == null)
        {
            return;
        }

        StopCoroutine(_fadeRoutine);
        _fadeRoutine = null;
    }

    private void StopEndMonitor()
    {
        if (_endMonitorRoutine == null)
        {
            return;
        }

        StopCoroutine(_endMonitorRoutine);
        _endMonitorRoutine = null;
    }

    private void StopAllRiverCoroutines()
    {
        StopFadeRoutine();
        StopEndMonitor();
    }

    private void StopSourceImmediate()
    {
        if (_source == null)
        {
            return;
        }

        _source.Stop();
        _source.volume = 0f;
    }

#if UNITY_EDITOR
    private void TryAssignDefaultRiverClipInEditor()
    {
        if (riverClip != null)
        {
            return;
        }

        riverClip = UnityEditor.AssetDatabase.LoadAssetAtPath<AudioClip>(DefaultRiverClipPath);
    }
#endif
}
