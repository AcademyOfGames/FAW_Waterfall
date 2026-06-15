using System.Collections;
using UnityEngine;

/// <summary>
/// Looped river bed + shimmer background layer: both fade in together, fade out when Stage 4 ends.
/// </summary>
[DisallowMultipleComponent]
public class ExperienceRiverAmbience : MonoBehaviour
{
    private const string DefaultRiverClipPath = "Assets/_artAssets/Alina/sound/river.mp3";
    private const string DefaultShimmerClipPath =
        "Assets/_artAssets/Alina/sound/Ethereal_shimmering__#4-1780507487369.mp3";

    [Header("River")]
    [SerializeField] private AudioClip riverClip;
    [Tooltip("Begin the river fade-in this many seconds before fish followers activate.")]
    [SerializeField] private float leadSecondsBeforeFish = 10f;
    [Tooltip("Seconds to ramp river from start volume to target volume.")]
    [SerializeField] private float fadeInDurationSeconds = 10f;
    [Range(0f, 1f)]
    [SerializeField] private float startVolume;
    [Range(0f, 1f)]
    [SerializeField] private float targetVolume = 1f;
    [SerializeField] private bool loopRiver = true;

    [Header("Shimmer")]
    [SerializeField] private AudioClip shimmerClip;
    [Tooltip("Seconds to ramp shimmer from start volume to target volume (starts with the river lead-in).")]
    [SerializeField] private float shimmerFadeInDurationSeconds = 10f;
    [Range(0f, 1f)]
    [SerializeField] private float shimmerStartVolume;
    [Range(0f, 1f)]
    [SerializeField] private float shimmerTargetVolume = 0.3f;
    [SerializeField] private bool loopShimmer = true;

    [Header("Shared")]
    [SerializeField] private float fadeOutDurationSeconds = 10f;
    [Range(0f, 1f)]
    [SerializeField] private float spatialBlend;

    [Header("End Detection")]
    [Tooltip("When the fish orchestrator loops, stop ambience after the first A->B cycle.")]
    [SerializeField] private bool stopAfterOneCycleIfOrchestratorLoops = true;

    private AudioSource _riverSource;
    private AudioSource _shimmerSource;
    private Coroutine _riverFadeRoutine;
    private Coroutine _shimmerFadeRoutine;
    private Coroutine _fadeOutRoutine;
    private Coroutine _endMonitorRoutine;
    private bool _riverFadeInStarted;
    private bool _shimmerFadeInStarted;
    private SplineFishGroupOrchestrator _subscribedOrchestrator;
    private System.Action _cycleCompleteHandler;

    public float LeadSecondsBeforeFish => leadSecondsBeforeFish;

    private void Awake()
    {
        EnsureRiverClipAssigned();
    }

    private void OnValidate()
    {
        leadSecondsBeforeFish = Mathf.Max(0f, leadSecondsBeforeFish);
        fadeInDurationSeconds = Mathf.Max(0.01f, fadeInDurationSeconds);
        shimmerFadeInDurationSeconds = Mathf.Max(0.01f, shimmerFadeInDurationSeconds);
        fadeOutDurationSeconds = Mathf.Max(0.01f, fadeOutDurationSeconds);
        startVolume = Mathf.Clamp01(startVolume);
        targetVolume = Mathf.Clamp01(targetVolume);
        shimmerStartVolume = Mathf.Clamp01(shimmerStartVolume);
        shimmerTargetVolume = Mathf.Clamp01(shimmerTargetVolume);
        spatialBlend = Mathf.Clamp01(spatialBlend);
#if UNITY_EDITOR
        TryAssignDefaultClipsInEditor();
#endif
    }

    private void OnDisable()
    {
        UnsubscribeOrchestrator();
        StopAllAmbienceCoroutines();
        StopSourcesImmediate();
    }

    /// <summary>Fades river and shimmer out (e.g. after Stage 4 viewer encounter ends).</summary>
    public void RequestFadeOut()
    {
        StopFadeOutRoutine();
        _fadeOutRoutine = StartCoroutine(FadeOutAndStopRoutine());
    }

    /// <summary>Starts fade-out if needed and yields until river and shimmer have fully stopped.</summary>
    public IEnumerator FadeOutAndWait()
    {
        if (_fadeOutRoutine == null)
        {
            RequestFadeOut();
        }

        while (_fadeOutRoutine != null)
        {
            yield return null;
        }
    }

    /// <summary>Starts river + shimmer fade-in together once (plant growth lead-in).</summary>
    public void BeginFadeIn()
    {
        if (!_riverFadeInStarted && riverClip != null)
        {
            _riverFadeInStarted = true;
            EnsureRiverSource();
            StopRiverFadeRoutine();
            _riverFadeRoutine = StartCoroutine(RiverFadeInRoutine());
        }

        BeginShimmerFadeIn();
    }

    /// <summary>Starts shimmer loop fade-in once (also called from BeginFadeIn).</summary>
    public void BeginShimmerFadeIn()
    {
        if (_shimmerFadeInStarted || shimmerClip == null)
        {
            return;
        }

        _shimmerFadeInStarted = true;
        EnsureShimmerSource();
        StopShimmerFadeRoutine();
        _shimmerFadeRoutine = StartCoroutine(ShimmerFadeInRoutine());
    }

    /// <summary>Ensures layered ambience is playing when Stage 2 starts (no-op if lead-in already ran).</summary>
    public void BeginStage2Ambience()
    {
        BeginFadeIn();
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

    private IEnumerator RiverFadeInRoutine()
    {
        EnsureRiverClipAssigned();
        if (riverClip == null)
        {
            _riverFadeRoutine = null;
            yield break;
        }

        EnsureRiverSource();
        PrepareClipForPlayback(riverClip);
        _riverSource.clip = riverClip;
        _riverSource.loop = loopRiver;
        _riverSource.volume = startVolume;
        if (!_riverSource.isPlaying)
        {
            _riverSource.Play();
        }

        float elapsed = 0f;
        while (elapsed < fadeInDurationSeconds)
        {
            elapsed += Time.deltaTime;
            _riverSource.volume = Mathf.Lerp(startVolume, targetVolume, elapsed / fadeInDurationSeconds);
            yield return null;
        }

        _riverSource.volume = targetVolume;
        _riverFadeRoutine = null;
    }

    private IEnumerator ShimmerFadeInRoutine()
    {
        EnsureShimmerSource();
        PrepareClipForPlayback(shimmerClip);
        _shimmerSource.clip = shimmerClip;
        _shimmerSource.loop = loopShimmer;
        _shimmerSource.volume = shimmerStartVolume;
        if (!_shimmerSource.isPlaying)
        {
            _shimmerSource.Play();
        }

        float elapsed = 0f;
        while (elapsed < shimmerFadeInDurationSeconds)
        {
            elapsed += Time.deltaTime;
            _shimmerSource.volume = Mathf.Lerp(
                shimmerStartVolume,
                shimmerTargetVolume,
                elapsed / shimmerFadeInDurationSeconds);
            yield return null;
        }

        _shimmerSource.volume = shimmerTargetVolume;
        _shimmerFadeRoutine = null;
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

        // Single-swarm experiences keep fish on the path with no global end — ambience plays until disabled.
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
        StopRiverFadeRoutine();
        StopShimmerFadeRoutine();

        float riverFrom = _riverSource != null && _riverSource.isPlaying ? _riverSource.volume : 0f;
        float shimmerFrom = _shimmerSource != null && _shimmerSource.isPlaying ? _shimmerSource.volume : 0f;
        bool fadeRiver = _riverSource != null && _riverSource.isPlaying && riverFrom > 0f;
        bool fadeShimmer = _shimmerSource != null && _shimmerSource.isPlaying && shimmerFrom > 0f;

        if (!fadeRiver && !fadeShimmer)
        {
            StopSourcesImmediate();
            _fadeOutRoutine = null;
            yield break;
        }

        float elapsed = 0f;
        while (elapsed < fadeOutDurationSeconds)
        {
            elapsed += Time.deltaTime;
            float t = elapsed / fadeOutDurationSeconds;

            if (fadeRiver)
            {
                _riverSource.volume = Mathf.Lerp(riverFrom, 0f, t);
            }

            if (fadeShimmer)
            {
                _shimmerSource.volume = Mathf.Lerp(shimmerFrom, 0f, t);
            }

            yield return null;
        }

        StopSourcesImmediate();
        _fadeOutRoutine = null;
    }

    private void EnsureRiverSource()
    {
        if (_riverSource != null)
        {
            return;
        }

        AudioSource[] sources = GetComponents<AudioSource>();
        if (sources.Length > 0)
        {
            _riverSource = sources[0];
        }
        else
        {
            _riverSource = gameObject.AddComponent<AudioSource>();
        }

        ConfigureSource(_riverSource, loopRiver, startVolume);
    }

    private void EnsureShimmerSource()
    {
        if (_shimmerSource != null)
        {
            return;
        }

        AudioSource[] sources = GetComponents<AudioSource>();
        if (sources.Length > 1)
        {
            _shimmerSource = sources[1];
        }
        else
        {
            _shimmerSource = gameObject.AddComponent<AudioSource>();
        }

        ConfigureSource(_shimmerSource, loopShimmer, shimmerStartVolume);
    }

    private void ConfigureSource(AudioSource source, bool loop, float volume)
    {
        source.playOnAwake = false;
        source.loop = loop;
        source.spatialBlend = spatialBlend;
        source.volume = volume;
    }

    private void StopRiverFadeRoutine()
    {
        if (_riverFadeRoutine == null)
        {
            return;
        }

        StopCoroutine(_riverFadeRoutine);
        _riverFadeRoutine = null;
    }

    private void StopShimmerFadeRoutine()
    {
        if (_shimmerFadeRoutine == null)
        {
            return;
        }

        StopCoroutine(_shimmerFadeRoutine);
        _shimmerFadeRoutine = null;
    }

    private void StopFadeOutRoutine()
    {
        if (_fadeOutRoutine == null)
        {
            return;
        }

        StopCoroutine(_fadeOutRoutine);
        _fadeOutRoutine = null;
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

    private void StopAllAmbienceCoroutines()
    {
        StopRiverFadeRoutine();
        StopShimmerFadeRoutine();
        StopFadeOutRoutine();
        StopEndMonitor();
    }

    private void StopSourcesImmediate()
    {
        if (_riverSource != null)
        {
            _riverSource.Stop();
            _riverSource.volume = 0f;
        }

        if (_shimmerSource != null)
        {
            _shimmerSource.Stop();
            _shimmerSource.volume = 0f;
        }
    }

    private void EnsureRiverClipAssigned()
    {
#if UNITY_EDITOR
        if (riverClip == null)
        {
            riverClip = UnityEditor.AssetDatabase.LoadAssetAtPath<AudioClip>(DefaultRiverClipPath);
        }
#endif
    }

    private static void PrepareClipForPlayback(AudioClip clip)
    {
        if (clip == null)
        {
            return;
        }

        if (clip.loadState == AudioDataLoadState.Unloaded)
        {
            clip.LoadAudioData();
        }
    }

#if UNITY_EDITOR
    private void TryAssignDefaultClipsInEditor()
    {
        EnsureRiverClipAssigned();

        if (shimmerClip == null)
        {
            shimmerClip = UnityEditor.AssetDatabase.LoadAssetAtPath<AudioClip>(DefaultShimmerClipPath);
        }
    }
#endif
}
