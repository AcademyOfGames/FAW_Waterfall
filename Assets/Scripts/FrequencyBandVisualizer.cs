using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// Scales each object in the band lists based on frequency analyzer amplitude: from initial scale to max scale and back.
/// </summary>
public class FrequencyBandVisualizer : MonoBehaviour
{
    [System.Serializable]
    public class BandObjectList
    {
        public List<GameObject> objects = new List<GameObject>();
    }

    [Header("Frequency Source")]
    [SerializeField] private FrequencyAnalyzer frequencyAnalyzer;

    [Header("Band Objects")]
    [Tooltip("SubBass, Bass, LowMid, Mid, HighMid, High. Each band can have multiple objects.")]
    [SerializeField] private BandObjectList[] bands = new BandObjectList[6];

    [Header("Scale")]
    [SerializeField] private float maxScale = 3.5f;
    [SerializeField] [Range(0.3f, 2f)] private float visibilityCurve = 0.65f;
    [SerializeField] private float decayTime = 0.2f;
    [SerializeField] private float growSpeed = 25f;
    [Tooltip("How quickly target values smooth. Lower = smoother but slower response.")]
    [SerializeField] [Range(2f, 30f)] private float smoothness = 12f;

    private float[] _currentScales;
    private float[] _smoothedTargets;
    private List<Vector3>[] _initialScales;

    private void Awake()
    {
        if (frequencyAnalyzer == null)
            frequencyAnalyzer = FindObjectOfType<FrequencyAnalyzer>();

        _currentScales = new float[6];
        _smoothedTargets = new float[6];
        _initialScales = new List<Vector3>[6];

        if (bands == null) bands = new BandObjectList[6];

        for (int i = 0; i < 6; i++)
        {
            _initialScales[i] = new List<Vector3>();
            if (i >= bands.Length || bands[i] == null || bands[i].objects == null) continue;

            foreach (GameObject obj in bands[i].objects)
            {
                if (obj != null)
                    _initialScales[i].Add(obj.transform.localScale);
            }
        }
    }

    private void Update()
    {
        if (frequencyAnalyzer == null) return;

        for (int i = 0; i < 6; i++)
        {
            if (bands == null || i >= bands.Length || bands[i] == null || bands[i].objects == null) continue;

            float bandValue = frequencyAnalyzer.GetBandValue((FrequencyAnalyzer.FrequencyBand)i);
            float boosted = Mathf.Pow(bandValue, visibilityCurve);
            float targetScale = boosted * maxScale;

            _smoothedTargets[i] = Mathf.Lerp(_smoothedTargets[i], targetScale, smoothness * Time.deltaTime);

            if (_smoothedTargets[i] > _currentScales[i])
                _currentScales[i] = Mathf.Lerp(_currentScales[i], _smoothedTargets[i], growSpeed * Time.deltaTime);
            else
            {
                float decayFactor = Mathf.Exp(-4.6f * Time.deltaTime / decayTime);
                _currentScales[i] *= decayFactor;
                if (_currentScales[i] < 0.001f) _currentScales[i] = 0f;
            }

            float scaleFactor = Mathf.Max(0.001f, _currentScales[i]);

            int scaleIdx = 0;
            foreach (GameObject obj in bands[i].objects)
            {
                if (obj == null) continue;
                if (scaleIdx >= _initialScales[i].Count) break;

                obj.transform.localScale = _initialScales[i][scaleIdx] * scaleFactor;
                scaleIdx++;
            }
        }
    }
}
