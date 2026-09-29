using System.Collections;
using UnityEngine;

/// <summary>
/// Plays a random clip from a list, then waits a random gap before playing the next.
/// Put it on the heron with an AudioSource (one is added automatically if missing).
/// </summary>
[RequireComponent(typeof(AudioSource))]
public class RandomSoundPlayer : MonoBehaviour
{
    [Tooltip("Clips to pick from. One is chosen at random each time.")]
    public AudioClip[] clips;
    [Tooltip("Shortest pause between the end of one clip and the start of the next, in seconds.")]
    public float minGapSeconds = 3f;
    [Tooltip("Longest pause between the end of one clip and the start of the next, in seconds.")]
    public float maxGapSeconds = 6f;
    [Tooltip("Never play the same clip twice in a row (ignored when there is only one clip).")]
    public bool avoidRepeats = true;
    [Tooltip("Start playing as soon as the object is enabled. Otherwise call Play() from another script or event.")]
    public bool playOnEnable = true;
    [Tooltip("Wait a random gap before the very first clip instead of playing it immediately.")]
    public bool randomGapBeforeFirst = true;
    [Tooltip("Random pitch variation applied per clip. 0 = always the source's pitch.")]
    [Range(0f, 0.5f)] public float pitchVariation = 0f;

    [Header("Volume")]
    [Tooltip("Volume applied to every clip this player triggers (0 = silent, 1 = the AudioSource's own volume).")]
    [Range(0f, 1f)] public float volume = 1f;
    [Tooltip("Random volume variation applied per clip, subtracted from the volume above. 0 = always the same level.")]
    [Range(0f, 1f)] public float volumeVariation = 0f;

    AudioSource source;
    Coroutine loop;
    int lastIndex = -1;

    void Awake()
    {
        source = GetComponent<AudioSource>();
        source.playOnAwake = false;
        source.loop = false;
    }

    void OnEnable()
    {
        if (playOnEnable) Play();
    }

    void OnDisable()
    {
        Stop();
    }

    /// <summary>Starts (or restarts) the random playback loop.</summary>
    public void Play()
    {
        Stop();
        loop = StartCoroutine(Loop());
    }

    /// <summary>Stops the loop and any clip that is currently playing.</summary>
    public void Stop()
    {
        if (loop != null)
        {
            StopCoroutine(loop);
            loop = null;
        }
        if (source != null && source.isPlaying) source.Stop();
    }

    IEnumerator Loop()
    {
        if (clips == null || clips.Length == 0)
        {
            Debug.LogWarning("RandomSoundPlayer: no clips assigned.", this);
            yield break;
        }

        if (randomGapBeforeFirst)
            yield return new WaitForSeconds(RandomGap());

        while (true)
        {
            AudioClip clip = PickClip();
            if (clip != null)
            {
                float basePitch = source.pitch;
                source.pitch = pitchVariation > 0f ? basePitch + Random.Range(-pitchVariation, pitchVariation) : basePitch;
                float level = Mathf.Clamp01(volume - (volumeVariation > 0f ? Random.Range(0f, volumeVariation) : 0f));
                source.PlayOneShot(clip, level);
                // Wait for the clip to finish (accounting for pitch) before starting the gap.
                yield return new WaitForSeconds(clip.length / Mathf.Max(Mathf.Abs(source.pitch), 0.01f));
                source.pitch = basePitch;
            }
            yield return new WaitForSeconds(RandomGap());
        }
    }

    float RandomGap()
    {
        float min = Mathf.Max(0f, Mathf.Min(minGapSeconds, maxGapSeconds));
        float max = Mathf.Max(minGapSeconds, maxGapSeconds);
        return Random.Range(min, max);
    }

    AudioClip PickClip()
    {
        // Skip empty slots.
        int valid = 0;
        foreach (var c in clips) if (c != null) valid++;
        if (valid == 0) return null;

        int index;
        int guard = 0;
        do
        {
            index = Random.Range(0, clips.Length);
            guard++;
        }
        while ((clips[index] == null || (avoidRepeats && valid > 1 && index == lastIndex)) && guard < 32);

        if (clips[index] == null) return null;
        lastIndex = index;
        return clips[index];
    }
}
