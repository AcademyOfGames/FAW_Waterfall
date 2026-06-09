using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Serialization;

/// <summary>
/// Activates plant targets in random order, evenly spread across a fixed duration.
/// After all targets are active and their animations are near completion,
/// releases fish swarms through an orchestrator or a single follower component.
/// </summary>
[DefaultExecutionOrder(50)]
public class RandomOneAtATimeActivator : MonoBehaviour
{
    [SerializeField] private List<GameObject> targets = new List<GameObject>();
    [Tooltip("All targets are activated within this many seconds (even spacing + jitter).")]
    [SerializeField] private float totalDurationSeconds = 70f;
    [Tooltip("Minimum seconds between consecutive plant activations.")]
    [SerializeField] private float minSwitchIntervalSeconds = 0.4f;
    [Tooltip("Maximum random jitter added to each scheduled activation time.")]
    [SerializeField] private float maxSwitchIntervalSeconds = 0.8f;

    [Header("Plant Animation")]
    [Tooltip("Rebind animators when each plant is enabled so the default grow clip starts from 0.")]
    [SerializeField] private bool restartAnimatorOnActivate = true;

    [Header("Plant Grow Audio")]
    [Tooltip("Play a grow sound when each plant is activated (synced with grow animation restart).")]
    [SerializeField] private bool playSoundOnPlantActivate = true;
    [SerializeField] private AudioClip[] plantGrowSounds;
    [Range(0f, 1f)]
    [SerializeField] private float plantGrowSoundVolume = 0.3f;
    [Range(0f, 1f)]
    [Tooltip("0 = 2D, 1 = 3D at the plant position.")]
    [SerializeField] private float plantGrowSoundSpatialBlend = 1f;

    [Header("Plant Sway")]
    [Tooltip("Adds PlantUnderwaterSway when each plant activates (rigid base-anchored wobble after grow).")]
    [SerializeField] private bool enableUnderwaterSway = true;
    [SerializeField] private PlantUnderwaterSway.SwayDefaults underwaterSwayDefaults = PlantUnderwaterSway.SwayDefaults.CreateBuiltIn();

    [Header("River Ambience")]
    [SerializeField] private ExperienceRiverAmbience riverAmbience;

    [Header("Fish Release")]
    [SerializeField] private SplineFishGroupOrchestrator fishOrchestrator;
    [SerializeField] private VertexPathSwarmFollower vertexPathSwarmFollower;
    [SerializeField] private float followerActivateStaggerSeconds = 3f;
    [FormerlySerializedAs("swarmStartDelaySeconds")]
    [SerializeField] private float extraFishStartDelaySeconds;

    [Header("Animation Sync")]
    [Tooltip("Start fish when target animations have this many seconds remaining.")]
    [SerializeField] private float animationLeadTimeSeconds = 1f;
    [SerializeField] private int animatorLayer;

    private Coroutine _activationRoutine;
    private bool _riverLeadInTriggered;

    public IReadOnlyList<GameObject> PlantTargets => targets;
    public PlantUnderwaterSway.SwayDefaults UnderwaterSwayDefaults => underwaterSwayDefaults;

    public void ConfigureUnderwaterSway(GameObject target)
    {
        PlantUnderwaterSway.EnsureConfigured(target, underwaterSwayDefaults, enableUnderwaterSway);
    }

    private void Awake()
    {
        if (riverAmbience == null)
        {
            riverAmbience = GetComponent<ExperienceRiverAmbience>();
        }
    }

    private void OnValidate()
    {
        totalDurationSeconds = Mathf.Max(0f, totalDurationSeconds);
        minSwitchIntervalSeconds = Mathf.Max(0f, minSwitchIntervalSeconds);
        maxSwitchIntervalSeconds = Mathf.Max(0f, maxSwitchIntervalSeconds);
        followerActivateStaggerSeconds = Mathf.Max(0f, followerActivateStaggerSeconds);
        extraFishStartDelaySeconds = Mathf.Max(0f, extraFishStartDelaySeconds);
        animationLeadTimeSeconds = Mathf.Max(0f, animationLeadTimeSeconds);
        animatorLayer = Mathf.Max(0, animatorLayer);
        plantGrowSoundVolume = Mathf.Clamp01(plantGrowSoundVolume);
        plantGrowSoundSpatialBlend = Mathf.Clamp01(plantGrowSoundSpatialBlend);
        if (underwaterSwayDefaults.poseHandoffSeconds <= 0f && underwaterSwayDefaults.primarySwayAngleDegrees <= 0f)
        {
            underwaterSwayDefaults = PlantUnderwaterSway.SwayDefaults.CreateBuiltIn();
        }

        ValidateSwayDefaults(ref underwaterSwayDefaults);
#if UNITY_EDITOR
        TryAssignDefaultPlantGrowSoundsInEditor();
#endif
    }

    private void Start()
    {
        if (_activationRoutine != null)
        {
            StopCoroutine(_activationRoutine);
        }

        _activationRoutine = StartCoroutine(ActivateRandomlyOverTime());
    }

    private IEnumerator ActivateRandomlyOverTime()
    {
        _riverLeadInTriggered = false;
        List<GameObject> validTargets = GetValidTargets();
        if (validTargets.Count > 0)
        {
            Shuffle(validTargets);
            yield return ActivateOnSchedule(validTargets);
        }

        yield return WaitUntilAnimationsNearFinish(validTargets);
        yield return StartFishAfterTargetsActivated();
        _activationRoutine = null;
    }

    private IEnumerator ActivateOnSchedule(List<GameObject> orderedTargets)
    {
        int count = orderedTargets.Count;
        float duration = Mathf.Max(0f, totalDurationSeconds);
        float minGap = minSwitchIntervalSeconds;
        float maxJitter = maxSwitchIntervalSeconds;
        float startTime = Time.time;
        float previousScheduledTime = -minGap;

        for (int i = 0; i < count; i++)
        {
            float slotTime = count <= 1 ? 0f : (i / (float)(count - 1)) * duration;
            if (i > 0 && maxJitter > 0f)
            {
                slotTime += Random.Range(-maxJitter, maxJitter);
            }

            slotTime = Mathf.Clamp(slotTime, 0f, duration);
            slotTime = Mathf.Max(slotTime, previousScheduledTime + minGap);
            previousScheduledTime = slotTime;

            float waitSeconds = slotTime - (Time.time - startTime);
            if (waitSeconds > 0f)
            {
                yield return new WaitForSeconds(waitSeconds);
            }

            ActivateTarget(orderedTargets[i], i);
        }
    }

    private IEnumerator WaitUntilAnimationsNearFinish(List<GameObject> validTargets)
    {
        if (validTargets == null || validTargets.Count == 0 || animationLeadTimeSeconds <= 0f)
        {
            TryBeginRiverLeadIn();
            yield break;
        }

        yield return null;

        while (true)
        {
            float maxRemainingSeconds = GetMaxAnimationRemainingSeconds(validTargets);
            bool hasAnimator = maxRemainingSeconds >= 0f;

            TryBeginRiverLeadIn(maxRemainingSeconds, hasAnimator);

            if (!hasAnimator || maxRemainingSeconds <= animationLeadTimeSeconds)
            {
                TryBeginRiverLeadIn();
                yield break;
            }

            yield return null;
        }
    }

    private float GetMaxAnimationRemainingSeconds(List<GameObject> validTargets)
    {
        float maxRemainingSeconds = -1f;

        for (int i = 0; i < validTargets.Count; i++)
        {
            GameObject target = validTargets[i];
            if (target == null || !target.activeInHierarchy)
            {
                continue;
            }

            Animator animator = target.GetComponentInChildren<Animator>(true);
            if (animator == null || !animator.isActiveAndEnabled)
            {
                continue;
            }

            maxRemainingSeconds = Mathf.Max(
                maxRemainingSeconds,
                GetAnimationRemainingSeconds(animator, animatorLayer)
            );
        }

        return maxRemainingSeconds;
    }

    private void TryBeginRiverLeadIn(float maxRemainingSeconds, bool hasAnimator)
    {
        if (_riverLeadInTriggered || riverAmbience == null)
        {
            return;
        }

        if (!hasAnimator)
        {
            return;
        }

        float leadThreshold = animationLeadTimeSeconds + riverAmbience.LeadSecondsBeforeFish;
        if (maxRemainingSeconds <= leadThreshold)
        {
            TryBeginRiverLeadIn();
        }
    }

    private void TryBeginRiverLeadIn()
    {
        if (_riverLeadInTriggered || riverAmbience == null)
        {
            return;
        }

        _riverLeadInTriggered = true;
        riverAmbience.BeginFadeIn(); // river + Ethereal shimmer background together
    }

    private static float GetAnimationRemainingSeconds(Animator animator, int layer)
    {
        if (animator == null || !animator.isActiveAndEnabled)
        {
            return 0f;
        }

        AnimatorStateInfo state = animator.GetCurrentAnimatorStateInfo(layer);
        if (state.loop)
        {
            return 0f;
        }

        float clipLength = state.length;
        if (clipLength <= 0f)
        {
            AnimatorClipInfo[] clips = animator.GetCurrentAnimatorClipInfo(layer);
            if (clips.Length == 0 || clips[0].clip == null)
            {
                return 0f;
            }

            clipLength = clips[0].clip.length;
        }

        if (clipLength <= 0f)
        {
            return 0f;
        }

        float normalizedTime = state.normalizedTime;
        if (normalizedTime >= 1f)
        {
            return 0f;
        }

        return (1f - normalizedTime) * clipLength;
    }

    private IEnumerator StartFishAfterTargetsActivated()
    {
        TryBeginRiverLeadIn();

        if (extraFishStartDelaySeconds > 0f)
        {
            yield return new WaitForSeconds(extraFishStartDelaySeconds);
        }

        if (fishOrchestrator != null)
        {
            fishOrchestrator.StartSequence();
            riverAmbience?.BeginStage2Ambience();
            riverAmbience?.BeginExperienceEndMonitoring(
                fishOrchestrator,
                null,
                followerActivateStaggerSeconds,
                extraFishStartDelaySeconds
            );
            yield break;
        }

        if (vertexPathSwarmFollower != null)
        {
            vertexPathSwarmFollower.ActivateFollowersAndStartSwarm(
                Mathf.Max(0f, followerActivateStaggerSeconds)
            );
            riverAmbience?.BeginStage2Ambience();
            riverAmbience?.BeginExperienceEndMonitoring(
                null,
                vertexPathSwarmFollower,
                followerActivateStaggerSeconds,
                extraFishStartDelaySeconds
            );
        }
    }

    private List<GameObject> GetValidTargets()
    {
        var validTargets = new List<GameObject>(targets.Count);
        for (int i = 0; i < targets.Count; i++)
        {
            if (targets[i] != null)
            {
                validTargets.Add(targets[i]);
            }
        }

        return validTargets;
    }

    private void ActivateTarget(GameObject target, int activationIndex)
    {
        if (target == null)
        {
            return;
        }

        bool wasAlreadyActive = target.activeSelf;
        target.SetActive(true);

        if (restartAnimatorOnActivate && !wasAlreadyActive)
        {
            Animator[] animators = target.GetComponentsInChildren<Animator>(true);
            for (int i = 0; i < animators.Length; i++)
            {
                Animator animator = animators[i];
                if (animator == null)
                {
                    continue;
                }

                animator.enabled = true;
                animator.Rebind();
                animator.Update(0f);
            }
        }

        PlayPlantGrowSound(target, activationIndex);
        EnsureUnderwaterSway(target);
    }

    private void EnsureUnderwaterSway(GameObject target)
    {
        ConfigureUnderwaterSway(target);
    }

    private static void ValidateSwayDefaults(ref PlantUnderwaterSway.SwayDefaults defaults)
    {
        defaults.poseHandoffSeconds = Mathf.Max(0f, defaults.poseHandoffSeconds);
        defaults.primarySwayAngleDegrees = Mathf.Max(0f, defaults.primarySwayAngleDegrees);
        defaults.secondarySwayAngleFactor = Mathf.Clamp01(defaults.secondarySwayAngleFactor);
        defaults.swaySpeed = Mathf.Max(0f, defaults.swaySpeed);
        defaults.secondarySwaySpeedFactor = Mathf.Max(0f, defaults.secondarySwaySpeedFactor);
        defaults.animatorLayer = Mathf.Max(0, defaults.animatorLayer);
    }

    private void PlayPlantGrowSound(GameObject target, int activationIndex)
    {
        if (!playSoundOnPlantActivate || plantGrowSounds == null || plantGrowSounds.Length == 0)
        {
            return;
        }

        AudioClip clip = plantGrowSounds[activationIndex % plantGrowSounds.Length];
        if (clip == null)
        {
            return;
        }

        Vector3 position = GetPlantAudioPosition(target);
        PlantGrowAudioPool.Play(position, clip, plantGrowSoundVolume, plantGrowSoundSpatialBlend);
    }

    private static Vector3 GetPlantAudioPosition(GameObject target)
    {
        Renderer renderer = target.GetComponentInChildren<Renderer>(true);
        if (renderer != null)
        {
            return renderer.bounds.center;
        }

        return target.transform.position;
    }

    private static void Shuffle(List<GameObject> list)
    {
        for (int i = list.Count - 1; i > 0; i--)
        {
            int swapIndex = Random.Range(0, i + 1);
            (list[i], list[swapIndex]) = (list[swapIndex], list[i]);
        }
    }

#if UNITY_EDITOR
    private const string PlantGrowSoundsFolder = "Assets/_artAssets/Alina/sound";

    private void TryAssignDefaultPlantGrowSoundsInEditor()
    {
        if (plantGrowSounds != null && plantGrowSounds.Length > 0)
        {
            return;
        }

        string[] guids = UnityEditor.AssetDatabase.FindAssets("t:AudioClip", new[] { PlantGrowSoundsFolder });
        if (guids.Length == 0)
        {
            return;
        }

        var clips = new List<AudioClip>(guids.Length);
        for (int i = 0; i < guids.Length; i++)
        {
            string path = UnityEditor.AssetDatabase.GUIDToAssetPath(guids[i]);
            AudioClip clip = UnityEditor.AssetDatabase.LoadAssetAtPath<AudioClip>(path);
            if (clip != null && clip.name.IndexOf("River", System.StringComparison.OrdinalIgnoreCase) < 0)
            {
                clips.Add(clip);
            }
        }

        clips.Sort((a, b) => string.CompareOrdinal(a.name, b.name));
        plantGrowSounds = clips.ToArray();
    }
#endif
}
