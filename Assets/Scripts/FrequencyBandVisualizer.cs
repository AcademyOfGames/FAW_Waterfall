using System;
using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// Scales objects per band from <see cref="FrequencyAnalyzer"/>.
/// Uses SmoothDamp for fluid, jitter-free motion.
/// </summary>
public class FrequencyBandVisualizer : MonoBehaviour
{
    public enum TimedScaleInClock
    {
        [Tooltip("Seconds since this component was enabled.")]
        SinceVisualizerEnabled,
        [Tooltip("AudioSource.time from the analyzer's source (sync to music).")]
        AudioSourceTime
    }

    [Serializable]
    public class TimedScaleInGroup
    {
        [Tooltip("Objects scale from 0 to their authored local scale over Ramp Duration, starting at Start Time.")]
        public List<GameObject> objects = new List<GameObject>();
        [Tooltip("When the scale-in begins (seconds), on the clock chosen below.")]
        public float startTimeSeconds = 30f;
        [Tooltip("How long (seconds) to go from zero scale to the original scale captured in Awake.")]
        public float rampDurationSeconds = 5f;
    }

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

    [Header("Timed scale-in (extra object groups)")]
    [Tooltip("Each group: objects start at zero scale and grow to their pre-play size between Start Time and Start + Ramp.")]
    [SerializeField] private List<TimedScaleInGroup> timedScaleInGroups = new List<TimedScaleInGroup>();
    [SerializeField] private TimedScaleInClock timedScaleInClock = TimedScaleInClock.SinceVisualizerEnabled;

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
    [Tooltip("Drive blend shape weight on band objects that have a valid blend index on a SkinnedMeshRenderer.")]
    [SerializeField] private bool driveBlendShapes = true;
    [Tooltip("Blend shape index on each SkinnedMeshRenderer mesh. Clamped per mesh if the mesh has fewer shapes.")]
    [SerializeField] private int blendShapeIndex;
    [Tooltip("When off: weight follows audio (inverse to band scale). When on: weight stays 100 until Start, then eases to 0 over Duration (same clock as timed scale-in).")]
    [SerializeField] private bool blendShapeTimelineEnabled = true;
    [SerializeField] private float blendShapeTimelineStartSeconds = 38f;
    [SerializeField] private float blendShapeTimelineDurationSeconds = 7f;
    [Tooltip("Used only when Blend Shape Timeline is disabled. Blend weight hits 0 when the smoothed scale multiplier reaches this (same scale as maxScale).")]
    [SerializeField] private float blendWeightZeroAtMul = 3f;

    [Header("Glow material & lights")]
    [Tooltip("Renderer using a transparent Universal Unlit material — _BaseColor alpha is driven by song amplitude.")]
    [SerializeField] private Renderer glowRenderer;
    [Tooltip("When glow begins (seconds), on the same clock as timed scale-in.")]
    [SerializeField] private float glowStartTimeSeconds = 30f;
    [Tooltip("Seconds to lerp from no glow to full amplitude-driven glow.")]
    [SerializeField] private float glowRampDurationSeconds = 4f;
    [Tooltip("Song amplitude at or below this → fully transparent glow / baseline light intensity.")]
    [SerializeField] private float amplitudeTransparentAt = 0.4f;
    [Tooltip("Song amplitude at or above this → full opaque glow / authored light intensity.")]
    [SerializeField] private float amplitudeOpaqueAt = 1f;
    [Tooltip("Minimum light intensity as a fraction of each light's authored intensity (always on once the glow timeline is active).")]
    [Range(0f, 1f)]
    [SerializeField] private float lightBaselineIntensityFraction = 0.4f;
    [Tooltip("Up to three lights — baseline intensity plus amplitude-driven boost, scaled by the glow timeline.")]
    [SerializeField] private List<Light> glowLights = new List<Light>(3);

    [Header("Debug")]
    [Tooltip("Logs blend-shape clock, weights, skin collection, and analyzer issues (throttled in Update).")]
    [SerializeField] private bool blendShapeDebugLogging;

    private const int BandCount = 6;
    private static readonly int GlowBaseColorId = Shader.PropertyToID("_BaseColor");
    private List<GameObject>[] _bands;
    private List<Vector3>[] _initialScales;
    private List<SkinnedMeshRenderer>[] _bandSkinMeshes;
    /// <summary>Per entry in <see cref="_bandSkinMeshes"/>, resolved blend index (clamped to mesh).</summary>
    private List<int>[] _bandBlendShapeIndices;

    private float[] _current;
    private float[] _velocityBuffer; // Required for SmoothDamp
    private float _startupRampTimeBase;
    private float _timedScaleClockBase;

    private List<Vector3>[] _timedGroupInitialScales;
    private HashSet<GameObject> _objectsInBandLists;
    private readonly Dictionary<GameObject, float> _timedScaleInUByObject = new Dictionary<GameObject, float>();

    private float _nextBlendDebugLogUnscaledTime;
    private bool _blendDebugLoggedNullAnalyzer;

    private Color _glowMaterialBaseColor;
    private Material _glowMaterialInstance;
    private float[] _glowLightBaseIntensities;

    private void OnEnable()
    {
        _startupRampTimeBase = Time.time;
        _timedScaleClockBase = Time.time;
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

    private float GetTimedScaleClockSeconds()
    {
        if (timedScaleInClock == TimedScaleInClock.AudioSourceTime && frequencyAnalyzer != null)
            return frequencyAnalyzer.PlaybackTimeSeconds;
        return Time.time - _timedScaleClockBase;
    }

    private void Awake()
    {
        if (frequencyAnalyzer == null)
            frequencyAnalyzer = FindObjectOfType<FrequencyAnalyzer>();

        _bands = new[] { subBass, bass, lowMid, mid, highMid, high };
        _initialScales = new List<Vector3>[BandCount];
        _bandSkinMeshes = new List<SkinnedMeshRenderer>[BandCount];
        _bandBlendShapeIndices = new List<int>[BandCount];
        _current = new float[BandCount];
        _velocityBuffer = new float[BandCount];
        _objectsInBandLists = new HashSet<GameObject>();

        for (int i = 0; i < BandCount; i++)
        {
            _initialScales[i] = new List<Vector3>();
            _bandSkinMeshes[i] = new List<SkinnedMeshRenderer>();
            _bandBlendShapeIndices[i] = new List<int>();
            var list = _bands[i];
            if (list == null) continue;

            foreach (GameObject obj in list)
            {
                if (obj != null)
                {
                    _objectsInBandLists.Add(obj);
                    _initialScales[i].Add(obj.transform.localScale);
                    if (driveBlendShapes)
                        CollectSkinnedMeshesForBlendShapes(obj, _bandSkinMeshes[i], _bandBlendShapeIndices[i]);
                }
            }
        }

        int groupCount = timedScaleInGroups != null ? timedScaleInGroups.Count : 0;
        _timedGroupInitialScales = new List<Vector3>[groupCount];
        for (int g = 0; g < groupCount; g++)
        {
            _timedGroupInitialScales[g] = new List<Vector3>();
            TimedScaleInGroup group = timedScaleInGroups[g];
            if (group?.objects == null) continue;

            foreach (GameObject obj in group.objects)
            {
                if (obj == null) continue;
                _timedGroupInitialScales[g].Add(obj.transform.localScale);
                obj.transform.localScale = Vector3.zero;
            }
        }

        ApplyInitialBlendShapeWeightsForTimeline();
        CacheGlowAndLightBaselines();
        LogBlendShapeAwakeSummary();
    }

    private void CacheGlowAndLightBaselines()
    {
        if (glowRenderer != null)
        {
            _glowMaterialInstance = glowRenderer.material;
            _glowMaterialBaseColor = _glowMaterialInstance.GetColor(GlowBaseColorId);
        }

        int lightCount = glowLights != null ? glowLights.Count : 0;
        _glowLightBaseIntensities = new float[lightCount];
        for (int i = 0; i < lightCount; i++)
        {
            Light light = glowLights[i];
            _glowLightBaseIntensities[i] = light != null ? light.intensity : 0f;
        }
    }

    private void LogBlendShapeAwakeSummary()
    {
        if (!blendShapeDebugLogging)
            return;

        int totalSkins = 0;
        for (int i = 0; i < BandCount; i++)
        {
            var skins = _bandSkinMeshes[i];
            if (skins == null) continue;
            totalSkins += skins.Count;
        }

        Debug.Log(
            $"[FrequencyBandVisualizer] Blend Awake: driveBlendShapes={driveBlendShapes}, timelineEnabled={blendShapeTimelineEnabled}, " +
            $"blendIndex={blendShapeIndex}, timelineStart={blendShapeTimelineStartSeconds}s, timelineDur={blendShapeTimelineDurationSeconds}s, " +
            $"clock={timedScaleInClock}, skinsCollected={totalSkins}",
            this);

        if (driveBlendShapes && totalSkins == 0)
            Debug.LogWarning(
                "[FrequencyBandVisualizer] driveBlendShapes is on but no SkinnedMeshRenderer was collected. " +
                "Band roots need a SkinnedMeshRenderer (self or child), a mesh import with blend shapes, and blendShapeIndex in range (or clamped).",
                this);
    }

    private void ApplyInitialBlendShapeWeightsForTimeline()
    {
        if (!driveBlendShapes || !blendShapeTimelineEnabled)
            return;

        for (int i = 0; i < BandCount; i++)
        {
            var skins = _bandSkinMeshes[i];
            if (skins == null) continue;
            for (int s = 0; s < skins.Count; s++)
            {
                if (skins[s] == null || s >= _bandBlendShapeIndices[i].Count) continue;
                skins[s].SetBlendShapeWeight(_bandBlendShapeIndices[i][s], 100f);
            }
        }
    }

    private void CollectSkinnedMeshesForBlendShapes(GameObject root, List<SkinnedMeshRenderer> skinsOut, List<int> blendIndicesOut)
    {
        var skins = root.GetComponentsInChildren<SkinnedMeshRenderer>(true);
        for (int s = 0; s < skins.Length; s++)
        {
            SkinnedMeshRenderer skin = skins[s];
            Mesh mesh = skin.sharedMesh;
            if (mesh == null || mesh.blendShapeCount <= 0)
            {
                if (blendShapeDebugLogging)
                {
                    int count = mesh != null ? mesh.blendShapeCount : -1;
                    Debug.LogWarning(
                        $"[FrequencyBandVisualizer] Skipping '{skin.name}' under '{root.name}': " +
                        $"mesh={(mesh == null ? "null" : mesh.name)}, blendShapeCount={count}",
                        skin);
                }
                continue;
            }

            int resolved = Mathf.Clamp(blendShapeIndex, 0, mesh.blendShapeCount - 1);
            if (resolved != blendShapeIndex)
            {
                Debug.LogWarning(
                    $"[FrequencyBandVisualizer] blendShapeIndex {blendShapeIndex} out of range on '{skin.name}' " +
                    $"(mesh '{mesh.name}' has {mesh.blendShapeCount} shapes). Using index {resolved}.",
                    skin);
            }

            skinsOut.Add(skin);
            blendIndicesOut.Add(resolved);
        }
    }

    private void Update()
    {
        float clock = GetTimedScaleClockSeconds();
        FillTimedScaleInFactorMap(clock);
        ApplyTimedScaleInGroupsOnlyForNonBandObjects(clock);

        float songAmplitude = frequencyAnalyzer != null ? GetSongAmplitude() : 0f;
        ApplyGlowAndLights(clock, songAmplitude);

        if (frequencyAnalyzer == null)
        {
            if (blendShapeDebugLogging && !_blendDebugLoggedNullAnalyzer)
            {
                _blendDebugLoggedNullAnalyzer = true;
                Debug.LogWarning(
                    "[FrequencyBandVisualizer] frequencyAnalyzer is null — entire band loop (including blend timeline) is skipped every frame. " +
                    "Assign an analyzer or blend weights only update from Awake once.",
                    this);
            }
            return;
        }

        _blendDebugLoggedNullAnalyzer = false;

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

                float timedU = 1f;
                if (_timedScaleInUByObject.TryGetValue(obj, out float u))
                    timedU = u;

                // Timed scale-in u (0→1) scales the authored baseline; audio adds on top of idle fraction.
                obj.transform.localScale = _initialScales[i][idx] * timedU * (idleScaleFraction + mul);
                idx++;
            }

            // 6. Blend shape: timeline 100→0, or (if timeline off) inverse to band scale. Unity uses 0–100.
            if (driveBlendShapes && _bandSkinMeshes[i].Count > 0)
            {
                float blendUnity = blendShapeTimelineEnabled
                    ? GetBlendShapeTimelineWeight100(clock)
                    : GetBlendShapeAudioReactiveWeight100(mul);

                var skins = _bandSkinMeshes[i];
                for (int s = 0; s < skins.Count; s++)
                {
                    if (skins[s] == null || s >= _bandBlendShapeIndices[i].Count) continue;
                    skins[s].SetBlendShapeWeight(_bandBlendShapeIndices[i][s], blendUnity);
                }

                if (blendShapeDebugLogging && i == 0 && Time.unscaledTime >= _nextBlendDebugLogUnscaledTime)
                {
                    _nextBlendDebugLogUnscaledTime = Time.unscaledTime + 2f;
                    Debug.Log(
                        $"[FrequencyBandVisualizer] Blend (band 0): clock={clock:F2}s → weight={blendUnity:F1} " +
                        $"(timeline={blendShapeTimelineEnabled}, skins={skins.Count}, mul={mul:F3})",
                        this);
                }
            }
            else if (blendShapeDebugLogging && i == 0 && Time.unscaledTime >= _nextBlendDebugLogUnscaledTime)
            {
                _nextBlendDebugLogUnscaledTime = Time.unscaledTime + 2f;
                Debug.Log(
                    $"[FrequencyBandVisualizer] Blend (band 0): driveBlendShapes={driveBlendShapes}, skinCount={_bandSkinMeshes[i].Count} — no writes this band.",
                    this);
            }
        }
    }

    private float GetSongAmplitude()
    {
        float sum = 0f;
        for (int i = 0; i < BandCount; i++)
            sum += frequencyAnalyzer.GetBandValue((FrequencyAnalyzer.FrequencyBand)i);
        return sum / BandCount;
    }

    private float GetGlowTimelineRamp01(float clockSeconds)
    {
        if (clockSeconds < glowStartTimeSeconds)
            return 0f;
        if (glowRampDurationSeconds <= 0f)
            return 1f;
        return Mathf.Clamp01((clockSeconds - glowStartTimeSeconds) / glowRampDurationSeconds);
    }

    private float GetAmplitudeDrive01(float amplitude)
    {
        if (amplitudeOpaqueAt <= amplitudeTransparentAt)
            return amplitude >= amplitudeOpaqueAt ? 1f : 0f;
        return Mathf.Clamp01(Mathf.InverseLerp(amplitudeTransparentAt, amplitudeOpaqueAt, amplitude));
    }

    private void ApplyGlowAndLights(float clockSeconds, float songAmplitude)
    {
        float timelineRamp = GetGlowTimelineRamp01(clockSeconds);
        float amplitudeDrive = GetAmplitudeDrive01(songAmplitude);
        float glowDrive = timelineRamp * amplitudeDrive;
        float baseline = Mathf.Clamp01(lightBaselineIntensityFraction);
        float lightDrive = timelineRamp * (baseline + (1f - baseline) * amplitudeDrive);

        if (_glowMaterialInstance != null)
        {
            Color c = _glowMaterialBaseColor;
            c.a = _glowMaterialBaseColor.a * glowDrive;
            _glowMaterialInstance.SetColor(GlowBaseColorId, c);
        }

        if (glowLights == null || _glowLightBaseIntensities == null)
            return;

        int count = Mathf.Min(glowLights.Count, _glowLightBaseIntensities.Length);
        for (int i = 0; i < count; i++)
        {
            Light light = glowLights[i];
            if (light == null)
                continue;
            light.intensity = _glowLightBaseIntensities[i] * lightDrive;
        }
    }

    /// <summary>
    /// Blend weight 100 before start time, then linear 100→0 over duration (same seconds clock as timed scale-in).
    /// </summary>
    private float GetBlendShapeTimelineWeight100(float clockSeconds)
    {
        if (clockSeconds < blendShapeTimelineStartSeconds)
            return 100f;
        if (blendShapeTimelineDurationSeconds <= 0f)
            return 0f;
        float t = Mathf.Clamp01((clockSeconds - blendShapeTimelineStartSeconds) / blendShapeTimelineDurationSeconds);
        return Mathf.Lerp(100f, 0f, t);
    }

    private float GetBlendShapeAudioReactiveWeight100(float mul)
    {
        if (blendWeightZeroAtMul <= 0f)
            return 100f;
        float blend01 = 1f - Mathf.Clamp01(mul / blendWeightZeroAtMul);
        return blend01 * 100f;
    }

    private static float ComputeTimedScaleInU(float clockSeconds, TimedScaleInGroup group)
    {
        float ramp = group.rampDurationSeconds;
        if (ramp > 0f)
        {
            float elapsed = clockSeconds - group.startTimeSeconds;
            return Mathf.Clamp01(elapsed / ramp);
        }

        return clockSeconds < group.startTimeSeconds ? 0f : 1f;
    }

    private void FillTimedScaleInFactorMap(float clockSeconds)
    {
        _timedScaleInUByObject.Clear();
        if (timedScaleInGroups == null || timedScaleInGroups.Count == 0)
            return;

        for (int g = 0; g < timedScaleInGroups.Count; g++)
        {
            TimedScaleInGroup group = timedScaleInGroups[g];
            if (group == null) continue;

            float u = ComputeTimedScaleInU(clockSeconds, group);
            if (group.objects == null) continue;

            for (int o = 0; o < group.objects.Count; o++)
            {
                GameObject obj = group.objects[o];
                if (obj == null) continue;
                _timedScaleInUByObject[obj] = u;
            }
        }
    }

    /// <summary>
    /// Objects that are also in band lists get scale from the band loop (initial × timedU × audio).
    /// </summary>
    private void ApplyTimedScaleInGroupsOnlyForNonBandObjects(float clockSeconds)
    {
        if (timedScaleInGroups == null || timedScaleInGroups.Count == 0 || _timedGroupInitialScales == null || _objectsInBandLists == null)
            return;

        for (int g = 0; g < timedScaleInGroups.Count; g++)
        {
            TimedScaleInGroup group = timedScaleInGroups[g];
            if (group?.objects == null) continue;

            float u = ComputeTimedScaleInU(clockSeconds, group);

            int idx = 0;
            foreach (GameObject obj in group.objects)
            {
                if (obj == null || idx >= _timedGroupInitialScales[g].Count) continue;
                if (!_objectsInBandLists.Contains(obj))
                    obj.transform.localScale = _timedGroupInitialScales[g][idx] * u;
                idx++;
            }
        }
    }
}