using UnityEngine;

/// <summary>
/// Analyzes audio spectrum and outputs normalized amplitude values (0-1) for 6 frequency groups.
/// Attach to a GameObject with an AudioSource playing audio to analyze.
/// </summary>
public class FrequencyAnalyzer : MonoBehaviour
{
    [Header("Audio Source")]
    [SerializeField] private AudioSource audioSource;
    [SerializeField] private bool useMicrophone;
    [SerializeField] private string microphoneDevice = null;

    [Header("FFT Settings")]
    [SerializeField] private FFTWindow fftWindow = FFTWindow.Hamming;
    [SerializeField] private int spectrumSize = 4096;

    [Header("Normalization")]
    [SerializeField] [Range(0.5f, 0.98f)] private float smoothing = 0.92f;
    [SerializeField] private float sensitivity = 1f;
    [SerializeField] [Range(0.3f, 2f)] private float responseCurve = 0.7f;
    [Header("Adaptive Peaks")]
    [SerializeField] private float peakRiseSpeed = 8f;
    [SerializeField] private float peakDecaySpeed = 0.5f;
    [Tooltip("Bass bands (SubBass, Bass) output multiplier. >1 = more punch, <1 = subtler.")]
    [SerializeField] [Range(0.5f, 2f)] private float bassMultiplier = 1.1f;
    [Tooltip("Treble bands (HighMid, High) output multiplier. >1 = more sparkle, <1 = subtler.")]
    [SerializeField] [Range(0.5f, 2f)] private float trebleMultiplier = 1.2f;

    // Output values (0-1) for each frequency group
    public float SubBass { get; private set; }
    public float Bass { get; private set; }
    public float LowMid { get; private set; }
    public float Mid { get; private set; }
    public float HighMid { get; private set; }
    public float High { get; private set; }

    public enum FrequencyBand
    {
        SubBass,    // 20-60 Hz
        Bass,       // 60-250 Hz
        LowMid,     // 250-500 Hz
        Mid,        // 500-2000 Hz
        HighMid,    // 2000-4000 Hz
        High        // 4000-20000 Hz
    }

    private float[] _spectrumData;
    private float[] _smoothedValues;
    private float[] _peakValues;
    private int _sampleRate;

    private static readonly (float min, float max)[] BandRanges =
    {
        (20f, 60f),     // SubBass
        (60f, 250f),    // Bass
        (250f, 500f),   // LowMid
        (500f, 2000f),  // Mid
        (2000f, 4000f), // HighMid
        (4000f, 20000f) // High
    };

    private void Awake()
    {
        if (audioSource == null)
            audioSource = GetComponent<AudioSource>();

        if (audioSource == null)
        {
            audioSource = gameObject.AddComponent<AudioSource>();
            Debug.LogWarning("FrequencyAnalyzer: No AudioSource found. Added one. Assign an AudioClip or enable Use Microphone.");
        }

        _spectrumData = new float[spectrumSize];
        _smoothedValues = new float[6];
        _peakValues = new float[6];

        if (useMicrophone)
        {
            if (Microphone.devices.Length > 0)
            {
                _sampleRate = AudioSettings.outputSampleRate;
                audioSource.clip = Microphone.Start(microphoneDevice ?? Microphone.devices[0], true, 10, _sampleRate);
                audioSource.loop = true;
                while (Microphone.GetPosition(microphoneDevice ?? Microphone.devices[0]) <= 0) { }
                audioSource.Play();
            }
            else
            {
                Debug.LogError("FrequencyAnalyzer: No microphone found.");
            }
        }
        else
        {
            _sampleRate = audioSource.clip != null ? audioSource.clip.frequency : AudioSettings.outputSampleRate;
        }
    }

    private void Update()
    {
        if (audioSource == null || !audioSource.isPlaying) return;

        _sampleRate = AudioSettings.outputSampleRate;
        audioSource.GetSpectrumData(_spectrumData, 0, fftWindow);

        float nyquist = _sampleRate * 0.5f;

        for (int band = 0; band < 6; band++)
        {
            float bandAmplitude = GetBandAmplitude(band, nyquist);
            bandAmplitude *= sensitivity;

            float minPeak = 0.0001f;
            if (bandAmplitude > _peakValues[band])
                _peakValues[band] = Mathf.MoveTowards(_peakValues[band], bandAmplitude, peakRiseSpeed * bandAmplitude * Time.deltaTime);
            else
                _peakValues[band] = Mathf.Max(minPeak, _peakValues[band] * (1f - peakDecaySpeed * Time.deltaTime));

            float peak = Mathf.Max(_peakValues[band], minPeak);
            float normalized = Mathf.Clamp01(bandAmplitude / peak);
            normalized = Mathf.Pow(normalized, responseCurve);
            float bandMult = (band <= 1) ? bassMultiplier : ((band >= 4) ? trebleMultiplier : 1f);
            _smoothedValues[band] = Mathf.Clamp01(Mathf.Lerp(_smoothedValues[band], normalized * bandMult, 1f - smoothing));

            switch ((FrequencyBand)band)
            {
                case FrequencyBand.SubBass: SubBass = _smoothedValues[band]; break;
                case FrequencyBand.Bass: Bass = _smoothedValues[band]; break;
                case FrequencyBand.LowMid: LowMid = _smoothedValues[band]; break;
                case FrequencyBand.Mid: Mid = _smoothedValues[band]; break;
                case FrequencyBand.HighMid: HighMid = _smoothedValues[band]; break;
                case FrequencyBand.High: High = _smoothedValues[band]; break;
            }
        }
    }

    private float GetBandAmplitude(int bandIndex, float nyquist)
    {
        var (minFreq, maxFreq) = BandRanges[bandIndex];
        int startIndex = FrequencyToSpectrumIndex(minFreq, nyquist);
        int endIndex = FrequencyToSpectrumIndex(maxFreq, nyquist);
        endIndex = Mathf.Min(endIndex, _spectrumData.Length - 1);

        float sum = 0f;
        int count = 0;
        for (int i = startIndex; i <= endIndex; i++)
        {
            sum += _spectrumData[i];
            count++;
        }

        return count > 0 ? sum / count : 0f;
    }

    private int FrequencyToSpectrumIndex(float frequency, float nyquist)
    {
        return Mathf.FloorToInt(frequency / nyquist * _spectrumData.Length);
    }

    /// <summary>
    /// Get normalized value (0-1) for a specific frequency band.
    /// </summary>
    public float GetBandValue(FrequencyBand band)
    {
        return band switch
        {
            FrequencyBand.SubBass => SubBass,
            FrequencyBand.Bass => Bass,
            FrequencyBand.LowMid => LowMid,
            FrequencyBand.Mid => Mid,
            FrequencyBand.HighMid => HighMid,
            FrequencyBand.High => High,
            _ => 0f
        };
    }

    /// <summary>
    /// Reset adaptive peaks. Call when switching audio.
    /// </summary>
    public void ResetCalibration()
    {
        for (int i = 0; i < 6; i++)
            _peakValues[i] = 0f;
    }
}
