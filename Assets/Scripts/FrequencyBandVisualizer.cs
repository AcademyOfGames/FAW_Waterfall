using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// Scales objects per band from <see cref="FrequencyAnalyzer"/>, left list = lowest frequencies → right = highest.
/// Order: SubBass, Bass, LowMid, Mid, HighMid, High.
/// </summary>
public class FrequencyBandVisualizer : MonoBehaviour
{
    [SerializeField] private FrequencyAnalyzer frequencyAnalyzer;

    [SerializeField] private List<GameObject> subBass = new List<GameObject>();
    [SerializeField] private List<GameObject> bass = new List<GameObject>();
    [SerializeField] private List<GameObject> lowMid = new List<GameObject>();
    [SerializeField] private List<GameObject> mid = new List<GameObject>();
    [SerializeField] private List<GameObject> highMid = new List<GameObject>();
    [SerializeField] private List<GameObject> high = new List<GameObject>();

    private const float MaxScale = 3f;
    private const float VisibilityPower = 0.65f;
    private const float DeadZone = 0.025f;
    private const float RestEpsilon = 0.006f;
    private const float DecaySeconds = 0.22f;
    private const float GrowSpeed = 22f;
    private const float TargetSmooth = 14f;

    private const int BandCount = 6;

    private List<GameObject>[] _bands;
    private List<Vector3>[] _initialScales;
    private float[] _current;
    private float[] _targetSmoothed;

    private void Awake()
    {
        if (frequencyAnalyzer == null)
            frequencyAnalyzer = FindObjectOfType<FrequencyAnalyzer>();

        _bands = new[] { subBass, bass, lowMid, mid, highMid, high };
        _initialScales = new List<Vector3>[BandCount];
        _current = new float[BandCount];
        _targetSmoothed = new float[BandCount];

        for (int i = 0; i < BandCount; i++)
        {
            _initialScales[i] = new List<Vector3>();
            var list = _bands[i];
            if (list == null) continue;

            foreach (GameObject obj in list)
            {
                if (obj != null)
                    _initialScales[i].Add(obj.transform.localScale);
            }
        }
    }

    private void Update()
    {
        if (frequencyAnalyzer == null)
            return;

        for (int i = 0; i < BandCount; i++)
        {
            var list = _bands[i];
            if (list == null)
                continue;

            float v = frequencyAnalyzer.GetBandValue((FrequencyAnalyzer.FrequencyBand)i);
            if (v < DeadZone)
                v = 0f;

            float drive = Mathf.Pow(v, VisibilityPower) * MaxScale;
            _targetSmoothed[i] = Mathf.Lerp(_targetSmoothed[i], drive, TargetSmooth * Time.deltaTime);

            if (_targetSmoothed[i] > _current[i])
                _current[i] = Mathf.Lerp(_current[i], _targetSmoothed[i], GrowSpeed * Time.deltaTime);
            else
            {
                float k = Mathf.Exp(-4.6f * Time.deltaTime / DecaySeconds);
                _current[i] *= k;
                if (_current[i] < RestEpsilon)
                    _current[i] = 0f;
            }

            bool rest = _current[i] <= 0f;
            float mul = rest ? 0f : _current[i];

            int idx = 0;
            foreach (GameObject obj in list)
            {
                if (obj == null)
                    continue;
                if (idx >= _initialScales[i].Count)
                    break;

                obj.transform.localScale = rest
                    ? _initialScales[i][idx]
                    : _initialScales[i][idx] * mul;

                idx++;
            }
        }
    }
}
