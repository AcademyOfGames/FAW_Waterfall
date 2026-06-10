using UnityEngine;

/// <summary>
/// Reuses hidden AudioSources for plant grow one-shots (avoids dozens of scene speaker icons).
/// </summary>
public class PlantGrowAudioPool : MonoBehaviour
{
    private const int PoolSize = 12;

    private static PlantGrowAudioPool _instance;

    private AudioSource[] _sources;
    private int _nextIndex;

    public static void Play(Vector3 position, AudioClip clip, float volume, float spatialBlend)
    {
        if (clip == null)
        {
            return;
        }

        PlantGrowAudioPool pool = GetOrCreate();
        pool.PlayInternal(position, clip, volume, spatialBlend);
    }

    private static PlantGrowAudioPool GetOrCreate()
    {
        if (_instance != null)
        {
            return _instance;
        }

        var root = new GameObject(nameof(PlantGrowAudioPool));
        root.hideFlags = HideFlags.HideAndDontSave;
        _instance = root.AddComponent<PlantGrowAudioPool>();
        return _instance;
    }

    private void Awake()
    {
        if (_instance != null && _instance != this)
        {
            Destroy(gameObject);
            return;
        }

        _instance = this;
        _sources = new AudioSource[PoolSize];

        for (int i = 0; i < PoolSize; i++)
        {
            var child = new GameObject($"PooledSource_{i}");
            child.transform.SetParent(transform, false);
            child.hideFlags = HideFlags.HideAndDontSave;

            AudioSource source = child.AddComponent<AudioSource>();
            source.playOnAwake = false;
            source.loop = false;
            _sources[i] = source;
        }
    }

    private void OnDestroy()
    {
        if (_instance == this)
        {
            _instance = null;
        }
    }

    private void PlayInternal(Vector3 position, AudioClip clip, float volume, float spatialBlend)
    {
        AudioSource source = GetAvailableSource();
        source.transform.position = position;
        source.clip = clip;
        source.volume = volume;
        source.spatialBlend = spatialBlend;
        source.spatialize = spatialBlend > 0.01f;
        source.Play();
    }

    private AudioSource GetAvailableSource()
    {
        for (int i = 0; i < _sources.Length; i++)
        {
            AudioSource source = _sources[i];
            if (source != null && !source.isPlaying)
            {
                return source;
            }
        }

        AudioSource fallback = _sources[_nextIndex];
        _nextIndex = (_nextIndex + 1) % _sources.Length;
        fallback.Stop();
        return fallback;
    }
}
