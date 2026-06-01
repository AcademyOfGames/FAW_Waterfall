using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Serialization;

/// <summary>
/// Activates all targets in random order within a fixed duration.
/// One target is activated immediately on start.
/// After all targets are active and their animations are near completion,
/// releases fish swarms through an orchestrator or a single follower component.
/// </summary>
public class RandomOneAtATimeActivator : MonoBehaviour
{
    [SerializeField] private List<GameObject> targets = new List<GameObject>();
    [SerializeField] private float totalDurationSeconds = 15f;
    [SerializeField] private float minSwitchIntervalSeconds = 0.25f;
    [SerializeField] private float maxSwitchIntervalSeconds = 1.5f;

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

    private void OnValidate()
    {
        totalDurationSeconds = Mathf.Max(0f, totalDurationSeconds);
        minSwitchIntervalSeconds = Mathf.Max(0f, minSwitchIntervalSeconds);
        maxSwitchIntervalSeconds = Mathf.Max(minSwitchIntervalSeconds, maxSwitchIntervalSeconds);
        followerActivateStaggerSeconds = Mathf.Max(0f, followerActivateStaggerSeconds);
        extraFishStartDelaySeconds = Mathf.Max(0f, extraFishStartDelaySeconds);
        animationLeadTimeSeconds = Mathf.Max(0f, animationLeadTimeSeconds);
        animatorLayer = Mathf.Max(0, animatorLayer);
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
        List<GameObject> validTargets = GetValidTargets();
        if (validTargets.Count > 0)
        {
            List<GameObject> pendingTargets = new List<GameObject>(validTargets);
            ActivateRandomPending(pendingTargets);

            float elapsed = 0f;
            float minInterval = Mathf.Max(0f, minSwitchIntervalSeconds);
            float maxInterval = Mathf.Max(minInterval, maxSwitchIntervalSeconds);

            while (pendingTargets.Count > 0 && elapsed < totalDurationSeconds)
            {
                float remainingTime = totalDurationSeconds - elapsed;
                int activationsLeftAfterThisWait = pendingTargets.Count - 1;
                float minWaitNeededNow = Mathf.Max(0f, remainingTime - (activationsLeftAfterThisWait * maxInterval));
                float maxWaitAllowedNow = Mathf.Max(0f, remainingTime - (activationsLeftAfterThisWait * minInterval));

                float waitLowerBound = Mathf.Min(maxWaitAllowedNow, Mathf.Max(minInterval, minWaitNeededNow));
                float waitUpperBound = Mathf.Min(maxInterval, maxWaitAllowedNow);
                if (waitUpperBound < waitLowerBound)
                {
                    waitLowerBound = waitUpperBound;
                }

                float waitTime = waitUpperBound > waitLowerBound
                    ? Random.Range(waitLowerBound, waitUpperBound)
                    : waitLowerBound;

                yield return new WaitForSeconds(waitTime);
                elapsed += waitTime;

                if (elapsed <= totalDurationSeconds && pendingTargets.Count > 0)
                {
                    ActivateRandomPending(pendingTargets);
                }
            }

            while (pendingTargets.Count > 0)
            {
                ActivateRandomPending(pendingTargets);
                yield return null;
            }
        }

        yield return WaitUntilAnimationsNearFinish(validTargets);
        yield return StartFishAfterTargetsActivated();
        _activationRoutine = null;
    }

    private IEnumerator WaitUntilAnimationsNearFinish(List<GameObject> validTargets)
    {
        if (validTargets == null || validTargets.Count == 0 || animationLeadTimeSeconds <= 0f)
        {
            yield break;
        }

        yield return null;

        while (true)
        {
            float maxRemainingSeconds = 0f;
            bool hasAnimator = false;

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

                hasAnimator = true;
                maxRemainingSeconds = Mathf.Max(
                    maxRemainingSeconds,
                    GetAnimationRemainingSeconds(animator, animatorLayer)
                );
            }

            if (!hasAnimator || maxRemainingSeconds <= animationLeadTimeSeconds)
            {
                yield break;
            }

            yield return null;
        }
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
        if (extraFishStartDelaySeconds > 0f)
        {
            yield return new WaitForSeconds(extraFishStartDelaySeconds);
        }

        if (fishOrchestrator != null)
        {
            fishOrchestrator.StartSequence();
            yield break;
        }

        if (vertexPathSwarmFollower != null)
        {
            vertexPathSwarmFollower.ActivateFollowersAndStartSwarm(
                Mathf.Max(0f, followerActivateStaggerSeconds)
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

    private void ActivateRandomPending(List<GameObject> pendingTargets)
    {
        if (pendingTargets.Count == 0)
        {
            return;
        }

        int nextIndex = Random.Range(0, pendingTargets.Count);
        GameObject nextTarget = pendingTargets[nextIndex];
        pendingTargets.RemoveAt(nextIndex);
        nextTarget.SetActive(true);
    }
}
