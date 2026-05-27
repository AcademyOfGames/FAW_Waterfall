using System.Collections;
using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// Activates all targets in random order within a fixed duration.
/// One target is activated immediately on start.
/// </summary>
public class RandomOneAtATimeActivator : MonoBehaviour
{
    [SerializeField] private List<GameObject> targets = new List<GameObject>();
    [SerializeField] private float totalDurationSeconds = 15f;
    [SerializeField] private float minSwitchIntervalSeconds = 0.25f;
    [SerializeField] private float maxSwitchIntervalSeconds = 1.5f;
    public VertexPathSwarmFollower vertexPathSwarmFollower;
    [SerializeField] private float swarmStartDelaySeconds = 3f;
    [SerializeField] private float followerActivateStaggerSeconds = 3f;

    private Coroutine _activationRoutine;

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

        yield return StartSwarmAfterAllTargetsActivated();
    }

    private IEnumerator StartSwarmAfterAllTargetsActivated()
    {
        yield return new WaitForSeconds(Mathf.Max(0f, swarmStartDelaySeconds));

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
        if (pendingTargets.Count == 0) return;

        int nextIndex = Random.Range(0, pendingTargets.Count);
        GameObject nextTarget = pendingTargets[nextIndex];
        pendingTargets.RemoveAt(nextIndex);
        nextTarget.SetActive(true);
    }
}
