using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// Scales objects per band from <see cref="FrequencyAnalyzer"/>.
/// Uses SmoothDamp for fluid, jitter-free motion.
/// </summary>
public class FrequencyBandVisualizer : MonoBehaviour
{
    [Header("References")]
    [SerializeField] private FrequencyAnalyzer frequencyAnalyzer;

    [Header("Band Assignment")]
    [SerializeField] private List<GameObject> subBass = new List<GameObject>();
    [SerializeField] private List<GameObject> bass = new List<GameObject>();
    [SerializeField] private List<GameObject> lowMid = new List<GameObject>();
    [SerializeField] private List<GameObject> mid = new List<GameObject>();
    [SerializeField] private List<GameObject> highMid = new List<GameObject>();
    [SerializeField] private List<GameObject> high = new List<GameObject>();

    [Header("Sensitivity & Logic")]
    [Tooltip("Individual multipliers for each band. Elements 0-5 (Sub to High).")]
    [SerializeField] private float[] bandSensitivities = new float[6] { 0.7f, 1.0f, 1.2f, 1.5f, 2.0f, 2.8f };

    [SerializeField] private float maxScale = 3f;
    [Tooltip("Scale when the band is quiet (mul = 0), as a fraction of authored scale. 0.8 ≈ 20% smaller than full authored size.")]
    [SerializeField] private float idleScaleFraction = 0.8f;
    [Range(1f, 4f)]
    [SerializeField] private float visibilityPower = 1.8f; // Higher = sharper peaks, less jitter
    [SerializeField] private float deadZone = 0.05f;       // Volume floor

    [Header("Startup band multiplier")]
    [Tooltip("Scales audio-driven reaction from start → end over ramp duration (then stays at end).")]
    [SerializeField] private bool startupBandMultiplierEnabled = true;
    [SerializeField] private float startupRampDurationSeconds = 180f;
    [SerializeField] private float startupBandMultiplierStart = 0.3f;
    [SerializeField] private float startupBandMultiplierEnd = 1f;

    [Header("Physics (SmoothDamp)")]
    [Tooltip("Lower = snappier/faster. Higher = smoother/heavier.")]
    [SerializeField] private float smoothTime = 0.12f;
    [SerializeField] private float maxSpeed = 20f;

    [Header("Blend shapes")]
    [Tooltip("Drive blend shape weight inversely to band scale: quiet/small → full weight, loud/big → zero.")]
    [SerializeField] private bool driveBlendShapes = true;
    [SerializeField] private int blendShapeIndex = 1;
    [Tooltip("Blend weight hits 0 when the smoothed scale multiplier reaches this (same scale as maxScale).")]
    [SerializeField] private float blendWeightZeroAtMul = 3f;

    private const int BandCount = 6;
    private List<GameObject>[] _bands;
    private List<Vector3>[] _initialScales;
    private List<SkinnedMeshRenderer>[] _bandSkinMeshes;

    private float[] _current;
    private float[] _velocityBuffer; // Required for SmoothDamp
    private float _startupRampTimeBase;

    private void OnEnable()
    {
        _startupRampTimeBase = Time.time;
    }

    private float GetStartupBandMultiplier()
    {
        if (!startupBandMultiplierEnabled)
            return 1f;
        if (startupRampDurationSeconds <= 0f)
            return startupBandMultiplierEnd;
        float t = Mathf.Clamp01((Time.time - _startupRampTimeBase) / startupRampDurationSeconds);
        return Mathf.Lerp(startupBandMultiplierStart, startupBandMultiplierEnd, t);
    }

    private void Awake()
    {
        if (frequencyAnalyzer == null)
            frequencyAnalyzer = FindObjectOfType<FrequencyAnalyzer>();

        _bands = new[] { subBass, bass, lowMid, mid, highMid, high };
        _initialScales = new List<Vector3>[BandCount];
        _bandSkinMeshes = new List<SkinnedMeshRenderer>[BandCount];
        _current = new float[BandCount];
        _velocityBuffer = new float[BandCount];

        for (int i = 0; i < BandCount; i++)
        {
            _initialScales[i] = new List<Vector3>();
            _bandSkinMeshes[i] = new List<SkinnedMeshRenderer>();
            var list = _bands[i];
            if (list == null) continue;

            foreach (GameObject obj in list)
            {
                if (obj != null)
                {
                    _initialScales[i].Add(obj.transform.localScale);
                    if (driveBlendShapes)
                        CollectSkinnedMeshesForBlendShapes(obj, _bandSkinMeshes[i]);
                }
            }
        }
    }

    private void CollectSkinnedMeshesForBlendShapes(GameObject root, List<SkinnedMeshRenderer> into)
    {
        var skins = root.GetComponentsInChildren<SkinnedMeshRenderer>(true);
        for (int s = 0; s < skins.Length; s++)
        {
            SkinnedMeshRenderer skin = skins[s];
            Mesh mesh = skin.sharedMesh;
            if (mesh == null || blendShapeIndex >= mesh.blendShapeCount)
                continue;
            into.Add(skin);
        }
    }

    private void Update()
    {
        if (frequencyAnalyzer == null) return;

        float bandRamp = GetStartupBandMultiplier();

        for (int i = 0; i < BandCount; i++)
        {
            var list = _bands[i];
            if (list == null) continue;

            // 1. Get raw audio data
            float rawV = frequencyAnalyzer.GetBandValue((FrequencyAnalyzer.FrequencyBand)i);

            // 2. Apply Sensitivity Multiplier
            float v = rawV * bandSensitivities[i];

            // 3. Apply DeadZone and exponential curve to sharpen the reaction
            if (v < deadZone) v = 0f;
            float targetDrive = Mathf.Pow(v, visibilityPower) * maxScale * bandRamp;

            // 4. SMOOTHDAMP: This is the 'secret sauce' for smooth decay.
            // It calculates velocity to ensure the motion doesn't snap or jitter.
            _current[i] = Mathf.SmoothDamp(
                _current[i],
                targetDrive,
                ref _velocityBuffer[i],
                smoothTime,
                maxSpeed
            );

            // 5. Apply the scale multiplier to the objects
            float mul = _current[i];
            int idx = 0;
            foreach (GameObject obj in list)
            {
                if (obj == null || idx >= _initialScales[i].Count) continue;

                // idleScaleFraction + mul: quiet baseline below authored scale, then grow with audio.
                obj.transform.localScale = _initialScales[i][idx] * (idleScaleFraction + mul);
                idx++;
            }

            // 6. Blend shape: small band (low mul) → weight 100; big band (high mul) → 0. Unity uses 0–100.
            if (driveBlendShapes && blendWeightZeroAtMul > 0f && _bandSkinMeshes[i].Count > 0)
            {
                float blend01 = 1f - Mathf.Clamp01(mul / blendWeightZeroAtMul);
                float blendUnity = blend01 * 100f;
                var skins = _bandSkinMeshes[i];
                for (int s = 0; s < skins.Count; s++)
                {
                    if (skins[s] != null)
                        skins[s].SetBlendShapeWeight(blendShapeIndex, blendUnity);
                }
            }
        }
    }
}