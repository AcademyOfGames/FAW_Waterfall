using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Events;

/// <summary>
/// Sequences two VPSF groups: A starts, swims, shrinks, then B starts, swims, shrinks.
/// By default this repeats forever (B -> A -> B -> ...).
/// Start timing (delay, stagger) lives on each VertexPathSwarmFollower.
/// </summary>
public class SplineFishGroupOrchestrator : MonoBehaviour
{
    [Header("Groups")]
    [SerializeField] private List<VertexPathSwarmFollower> groupA = new List<VertexPathSwarmFollower>();
    [SerializeField] private List<VertexPathSwarmFollower> groupB = new List<VertexPathSwarmFollower>();

    [Header("Group A Timing")]
    [Tooltip("How long group A swims after its last swarm has started, before shrinking.")]
    [SerializeField] private float groupAActiveDurationSeconds = 6f;
    [Tooltip("How long group A takes to shrink away before group B is released.")]
    [SerializeField] private float groupAShrinkDurationSeconds = 1f;

    [Header("Group B Timing")]
    [Tooltip("How long group B swims after its last swarm has started, before shrinking.")]
    [SerializeField] private float groupBActiveDurationSeconds = 6f;
    [Tooltip("How long group B takes to shrink away at the end of the sequence.")]
    [SerializeField] private float groupBShrinkDurationSeconds = 1f;

    [Header("Startup")]
    [Tooltip("When on, begins the A->B sequence on Play. Leave off when RandomOneAtATimeActivator (or another trigger) calls StartSequence().")]
    [SerializeField] private bool startOnPlay;

    [Header("Looping")]
    [Tooltip("When on, after group B shrinks the sequence returns to group A and repeats forever.")]
    [SerializeField] private bool loopForever = true;

    [Header("Events")]
    [Tooltip("Invoked after each full A->B cycle (or once at completion if looping is off).")]
    [SerializeField] private UnityEvent onSequenceComplete;

    private Coroutine _sequenceRoutine;

    public bool IsSequenceRunning => _sequenceRoutine != null;

    private void OnValidate()
    {
        groupAActiveDurationSeconds = Mathf.Max(0f, groupAActiveDurationSeconds);
        groupAShrinkDurationSeconds = Mathf.Max(0.01f, groupAShrinkDurationSeconds);
        groupBActiveDurationSeconds = Mathf.Max(0f, groupBActiveDurationSeconds);
        groupBShrinkDurationSeconds = Mathf.Max(0.01f, groupBShrinkDurationSeconds);
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

        _sequenceRoutine = StartCoroutine(PlaySequenceRoutine());
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
        while (true)
        {
            yield return RunGroupPhase(groupA, groupAActiveDurationSeconds, groupAShrinkDurationSeconds, releaseGroup: true);
            yield return RunGroupPhase(groupB, groupBActiveDurationSeconds, groupBShrinkDurationSeconds, releaseGroup: true);

            onSequenceComplete?.Invoke();

            if (!loopForever)
            {
                break;
            }
        }

        _sequenceRoutine = null;
    }

    private IEnumerator RunGroupPhase(
        List<VertexPathSwarmFollower> group,
        float activeDurationSeconds,
        float shrinkDurationSeconds,
        bool releaseGroup)
    {
        if (releaseGroup)
        {
            RequestGroupStart(group);
        }

        if (!HasAnySwarm(group))
        {
            yield break;
        }

        yield return WaitForGroupRunning(group);

        if (activeDurationSeconds > 0f)
        {
            yield return WaitForActiveDuration(group, activeDurationSeconds);
        }

        yield return ShrinkGroup(group, shrinkDurationSeconds);
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

    private static IEnumerator WaitForActiveDuration(
        List<VertexPathSwarmFollower> group,
        float activeDurationSeconds)
    {
        float groupStartTime = GetLatestSwarmStartTime(group);
        if (groupStartTime < 0f)
        {
            yield break;
        }

        float endTime = groupStartTime + activeDurationSeconds;
        while (Time.time < endTime)
        {
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

    private IEnumerator ShrinkGroup(List<VertexPathSwarmFollower> group, float shrinkDurationSeconds)
    {
        int pending = 0;

        for (int i = 0; i < group.Count; i++)
        {
            VertexPathSwarmFollower swarm = group[i];
            if (swarm == null || !swarm.IsSwarmRunning)
            {
                continue;
            }

            pending++;
            StartCoroutine(ShrinkSwarmAndSignal(swarm, shrinkDurationSeconds, () => pending--));
        }

        while (pending > 0)
        {
            yield return null;
        }
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
        PrepareGroupForSequence(groupA);
        PrepareGroupForSequence(groupB);
    }
}
