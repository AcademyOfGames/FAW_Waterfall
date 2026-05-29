using UnityEngine;

/// <summary>
/// Reads spectrum from an AudioSource and exposes six normalized levels (0–1), low → high.
/// </summary>
public class FrequencyAnalyzer : MonoBehaviour
{
    [Header("Audio")]
    [SerializeField] private AudioSource audioSource;

    public float SubBass { get; private set; }
    public float Bass { get; private set; }
    public float LowMid { get; private set; }
    public float Mid { get; private set; }
    public float HighMid { get; private set; }
    public float High { get; private set; }

    /// <summary>
    /// Current playback time in seconds (for syncing visuals). Use when the analyzer drives a playing <see cref="AudioSource"/>.
    /// </summary>
    public float PlaybackTimeSeconds => audioSource != null ? audioSource.time : 0f;

    public enum FrequencyBand
    {
        SubBass,
        Bass,
        LowMid,
        Mid,
        HighMid,
        High
    }

    private const int SpectrumSize = 4096;
    private const FFTWindow FftWindow = FFTWindow.BlackmanHarris;
    private const float Smoothing = 0.88f;
    private const float ResponseCurve = 0.72f;
    private const float PeakRiseSpeed = 8f;
    private const float PeakDecaySpeed = 0.45f;

    /// <summary>
    /// Crossovers (Hz). Narrow sub/bass on the left; high band starts ~4 kHz and runs to Nyquist so the top end moves well.
    /// </summary>
    private static readonly (float minHz, float maxHz)[] BandRanges =
    {
        (20f, 70f),
        (70f, 200f),
        (200f, 700f),
        (700f, 2200f),
        (2200f, 4000f),
        (4000f, 20000f)
    };

    private const int BandCount = 6;

    private float[] _spectrumData;
    private float[] _smoothedValues;
    private float[] _peakValues;

    private void Awake()
    {
        if (audioSource == null)
            audioSource = GetComponent<AudioSource>();

        if (audioSource == null)
        {
            audioSource = gameObject.AddComponent<AudioSource>();
            Debug.LogWarning("FrequencyAnalyzer: No AudioSource found. Added one — assign an audio clip on the AudioSource.");
        }

        _spectrumData = new float[SpectrumSize];
        _smoothedValues = new float[BandCount];
        _peakValues = new float[BandCount];
    }

    private void Update()
    {
        if (audioSource == null || !audioSource.isPlaying)
            return;

        audioSource.GetSpectrumData(_spectrumData, 0, FftWindow);

        float nyquist = AudioSettings.outputSampleRate * 0.5f;

        for (int band = 0; band < BandCount; band++)
        {
            float bandAmplitude = AverageBandAmplitude(band, nyquist);

            const float minPeak = 0.0001f;
            if (bandAmplitude > _peakValues[band])
                _peakValues[band] = Mathf.MoveTowards(_peakValues[band], bandAmplitude, PeakRiseSpeed * bandAmplitude * Time.deltaTime);
            else
                _peakValues[band] = Mathf.Max(minPeak, _peakValues[band] * (1f - PeakDecaySpeed * Time.deltaTime));

            float peak = Mathf.Max(_peakValues[band], minPeak);
            float normalized = Mathf.Clamp01(bandAmplitude / peak);
            normalized = Mathf.Pow(normalized, ResponseCurve);

            float gain = OutputGainForBand(band);
            _smoothedValues[band] = Mathf.Clamp01(Mathf.Lerp(_smoothedValues[band], normalized * gain, 1f - Smoothing));

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

    private static float OutputGainForBand(int band)
    {
        return band switch
        {
            0 or 1 => 0.98f,
            4 => 1.1f,
            5 => 1.35f,
            _ => 1f
        };
    }

    private float AverageBandAmplitude(int bandIndex, float nyquist)
    {
        var (minFreq, maxFreq) = BandRanges[bandIndex];
        int start = FrequencyToSpectrumIndex(minFreq, nyquist);
        int end = FrequencyToSpectrumIndex(maxFreq, nyquist);
        end = Mathf.Min(end, _spectrumData.Length - 1);

        float sum = 0f;
        int count = 0;
        for (int i = start; i <= end; i++)
        {
            sum += _spectrumData[i];
            count++;
        }

        return count > 0 ? sum / count : 0f;
    }

    private static int FrequencyToSpectrumIndex(float frequency, float nyquist)
    {
        return Mathf.FloorToInt(frequency / nyquist * SpectrumSize);
    }

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

    public void ResetCalibration()
    {
        for (int i = 0; i < BandCount; i++)
            _peakValues[i] = 0f;
    }
}
