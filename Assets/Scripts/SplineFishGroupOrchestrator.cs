using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Events;
using UnityEngine.Serialization;

/// <summary>
/// Sequences two VPSF groups: A swims, shrinks, then B swims, shrinks, repeat.
/// The next group can be released before the current group starts shrinking (independent of shrink duration).
/// </summary>
public class SplineFishGroupOrchestrator : MonoBehaviour
{
    [Header("Groups")]
    [SerializeField] private List<VertexPathSwarmFollower> groupA = new List<VertexPathSwarmFollower>();
    [SerializeField] private List<VertexPathSwarmFollower> groupB = new List<VertexPathSwarmFollower>();

    [Header("Group A Timing")]
    [Tooltip("How long group A swims at full size after its last swarm has started, before shrinking.")]
    [SerializeField] private float groupAActiveDurationSeconds = 6f;
    [Tooltip("How long group A takes to shrink away (independent of overlap).")]
    [SerializeField] private float groupAShrinkDurationSeconds = 1f;

    [Header("Group B Timing")]
    [Tooltip("How long group B swims at full size after its last swarm has started, before shrinking.")]
    [SerializeField] private float groupBActiveDurationSeconds = 6f;
    [Tooltip("How long group B takes to shrink away (independent of overlap).")]
    [SerializeField] private float groupBShrinkDurationSeconds = 1f;

    [Header("Overlap")]
    [Tooltip("Release group B this many seconds before group A starts shrinking (during A's swim tail when > 0).")]
    [FormerlySerializedAs("groupBReleaseOverlapSeconds")]
    [SerializeField] private float groupBReleaseBeforeAShrinkSeconds = 3f;
    [Tooltip("On loop, release group A this many seconds before group B starts shrinking.")]
    [FormerlySerializedAs("groupAReleaseOverlapSeconds")]
    [SerializeField] private float groupAReleaseBeforeBShrinkSeconds = 3f;

    [Header("Startup")]
    [Tooltip("When on, begins the A->B sequence on Play. Leave off when RandomOneAtATimeActivator (or another trigger) calls StartSequence().")]
    [SerializeField] private bool startOnPlay;

    [Header("Ambience")]
    [Tooltip("Optional looped river bed when fish begin swimming. Auto-resolved on this object or in the scene when empty.")]
    [SerializeField] private ExperienceRiverAmbience riverAmbience;

    [Header("Looping")]
    [Tooltip("When on, after group B shrinks the sequence returns to group A and repeats forever.")]
    [SerializeField] private bool loopForever = true;
    [Tooltip("When off, group B stays visible after its swim phase for Stage 4 viewer encounter handoff.")]
    [SerializeField] private bool shrinkGroupBAtEndOfCycle = true;

    [Header("Events")]
    [Tooltip("Invoked after each full A->B cycle (or once at completion if looping is off).")]
    [SerializeField] private UnityEvent onSequenceComplete;

    private Coroutine _sequenceRoutine;

    public bool IsSequenceRunning => _sequenceRoutine != null;
    public bool LoopForever => loopForever;

    public event System.Action SequenceCycleCompleted;
    public event System.Action<IReadOnlyList<VertexPathSwarmFollower>> GroupBReadyForViewerEncounter;

    private void OnValidate()
    {
        groupAActiveDurationSeconds = Mathf.Max(0f, groupAActiveDurationSeconds);
        groupAShrinkDurationSeconds = Mathf.Max(0.01f, groupAShrinkDurationSeconds);
        groupBActiveDurationSeconds = Mathf.Max(0f, groupBActiveDurationSeconds);
        groupBShrinkDurationSeconds = Mathf.Max(0.01f, groupBShrinkDurationSeconds);
        groupBReleaseBeforeAShrinkSeconds = Mathf.Max(0f, groupBReleaseBeforeAShrinkSeconds);
        groupAReleaseBeforeBShrinkSeconds = Mathf.Max(0f, groupAReleaseBeforeBShrinkSeconds);
    }

    private void Awake()
    {
        EnsureRiverAmbience();
    }

    private void Start()
    {
        PrepareGroupForSequence(groupB);

        if (!startOnPlay)
        {
            PrepareGroupForSequence(groupA);
        }

        if (startOnPlay)
        {
            StartSequence();
        }
    }

    public void StartSequence()
    {
        if (_sequenceRoutine != null)
        {
            StopCoroutine(_sequenceRoutine);
        }

        EnsureRiverAmbience();
        riverAmbience?.BeginStage2Ambience();
        riverAmbience?.BeginExperienceEndMonitoring(this, null, 0f, 0f);

        _sequenceRoutine = StartCoroutine(PlaySequenceRoutine());
    }

    private void EnsureRiverAmbience()
    {
        if (riverAmbience != null)
        {
            return;
        }

        riverAmbience = GetComponent<ExperienceRiverAmbience>();
        if (riverAmbience != null)
        {
            return;
        }

        riverAmbience = GetComponentInParent<ExperienceRiverAmbience>();
        if (riverAmbience != null)
        {
            return;
        }

        Transform sceneRoot = transform.root;
        if (sceneRoot != null)
        {
            riverAmbience = sceneRoot.GetComponentInChildren<ExperienceRiverAmbience>(true);
        }
    }

    /// <summary>Legacy alias for <see cref="StartSequence"/>.</summary>
    public void StartLoop()
    {
        StartSequence();
    }

    public void StopSequence()
    {
        if (_sequenceRoutine != null)
        {
            StopCoroutine(_sequenceRoutine);
            _sequenceRoutine = null;
        }

        StopAllGroupsImmediate();
    }

    /// <summary>Legacy alias for <see cref="StopSequence"/>.</summary>
    public void StopLoop()
    {
        StopSequence();
    }

    private IEnumerator PlaySequenceRoutine()
    {
        bool releaseGroupAAtPhaseStart = true;

        while (true)
        {
            yield return RunGroupSwimShrinkAndReleaseNext(
                groupA,
                groupAActiveDurationSeconds,
                groupAShrinkDurationSeconds,
                groupB,
                groupBReleaseBeforeAShrinkSeconds,
                releaseGroupAtPhaseStart: releaseGroupAAtPhaseStart
            );

            ShutdownGroupA();

            bool shrinkGroupB = shrinkGroupBAtEndOfCycle;
            yield return RunGroupSwimShrinkAndReleaseNext(
                groupB,
                groupBActiveDurationSeconds,
                groupBShrinkDurationSeconds,
                groupA,
                groupAReleaseBeforeBShrinkSeconds,
                releaseGroupAtPhaseStart: false,
                shrinkAtEnd: shrinkGroupB,
                releaseNextGroup: loopForever
            );

            if (!shrinkGroupB)
            {
                GroupBReadyForViewerEncounter?.Invoke(groupB);
            }

            onSequenceComplete?.Invoke();
            SequenceCycleCompleted?.Invoke();

            if (!loopForever)
            {
                break;
            }

            releaseGroupAAtPhaseStart = false;
        }

        _sequenceRoutine = null;
    }

    private IEnumerator RunGroupSwimShrinkAndReleaseNext(
        List<VertexPathSwarmFollower> group,
        float activeDurationSeconds,
        float shrinkDurationSeconds,
        List<VertexPathSwarmFollower> nextGroup,
        float releaseBeforeShrinkSeconds,
        bool releaseGroupAtPhaseStart,
        bool shrinkAtEnd = true,
        bool releaseNextGroup = true)
    {
        if (releaseGroupAtPhaseStart)
        {
            RequestGroupStart(group);
        }

        if (!HasAnySwarm(group))
        {
            yield break;
        }

        yield return WaitForGroupRunning(group);

        float groupStartTime = GetLatestSwarmStartTime(group);
        if (groupStartTime < 0f)
        {
            yield break;
        }

        float shrinkStartTime = groupStartTime + Mathf.Max(0f, activeDurationSeconds);
        float releaseNextTime = shrinkStartTime - Mathf.Max(0f, releaseBeforeShrinkSeconds);

        if (releaseNextGroup && HasAnySwarm(nextGroup))
        {
            yield return WaitUntilTime(releaseNextTime);
            RequestGroupStart(nextGroup);
        }

        yield return WaitUntilTime(shrinkStartTime);
        if (shrinkAtEnd)
        {
            yield return ShrinkGroupOnly(group, shrinkDurationSeconds);
        }
    }

    private static IEnumerator WaitUntilTime(float targetTime)
    {
        while (Time.time < targetTime)
        {
            yield return null;
        }
    }

    private IEnumerator ShrinkGroupOnly(List<VertexPathSwarmFollower> group, float shrinkDurationSeconds)
    {
        int pendingShrinks = 0;

        for (int i = 0; i < group.Count; i++)
        {
            VertexPathSwarmFollower swarm = group[i];
            if (swarm == null || !swarm.IsSwarmRunning)
            {
                continue;
            }

            pendingShrinks++;
            StartCoroutine(ShrinkSwarmAndSignal(swarm, shrinkDurationSeconds, () => pendingShrinks--));
        }

        while (pendingShrinks > 0)
        {
            yield return null;
        }
    }

    private static bool HasAnySwarm(List<VertexPathSwarmFollower> group)
    {
        for (int i = 0; i < group.Count; i++)
        {
            if (group[i] != null)
            {
                return true;
            }
        }

        return false;
    }

    private static void RequestGroupStart(List<VertexPathSwarmFollower> group)
    {
        for (int i = 0; i < group.Count; i++)
        {
            VertexPathSwarmFollower swarm = group[i];
            if (swarm != null)
            {
                swarm.RequestStart();
            }
        }
    }

    private static IEnumerator WaitForGroupRunning(List<VertexPathSwarmFollower> group)
    {
        while (true)
        {
            bool hasSwarm = false;
            bool allRunning = true;

            for (int i = 0; i < group.Count; i++)
            {
                VertexPathSwarmFollower swarm = group[i];
                if (swarm == null)
                {
                    continue;
                }

                hasSwarm = true;

                if (!swarm.IsSwarmRunning)
                {
                    allRunning = false;
                    break;
                }
            }

            if (!hasSwarm || allRunning)
            {
                yield break;
            }

            yield return null;
        }
    }

    private static float GetLatestSwarmStartTime(List<VertexPathSwarmFollower> group)
    {
        float latestStartTime = -1f;

        for (int i = 0; i < group.Count; i++)
        {
            VertexPathSwarmFollower swarm = group[i];
            if (swarm == null || !swarm.HasSwarmStarted)
            {
                continue;
            }

            latestStartTime = Mathf.Max(latestStartTime, swarm.SwarmStartedTime);
        }

        return latestStartTime;
    }

    private IEnumerator ShrinkSwarmAndSignal(
        VertexPathSwarmFollower swarm,
        float duration,
        System.Action onComplete)
    {
        yield return swarm.ShrinkAndStopSwarm(duration);
        onComplete?.Invoke();
    }

    private static void PrepareGroupForSequence(List<VertexPathSwarmFollower> group)
    {
        for (int i = 0; i < group.Count; i++)
        {
            VertexPathSwarmFollower swarm = group[i];
            if (swarm != null)
            {
                swarm.StopSwarmImmediate();
            }
        }
    }

    private void StopAllGroupsImmediate()
    {
        ShutdownGroupA();
        PrepareGroupForSequence(groupB);
    }

    private void ShutdownGroupA()
    {
        for (int i = 0; i < groupA.Count; i++)
        {
            VertexPathSwarmFollower swarm = groupA[i];
            if (swarm == null)
            {
                continue;
            }

            PlantFishReleaseController plantRelease = swarm.GetComponent<PlantFishReleaseController>();
            if (plantRelease != null)
            {
                plantRelease.CancelReleaseAndShutdown();
            }
            else
            {
                swarm.ShutdownAndHideAllFollowers();
            }
        }
    }
}
