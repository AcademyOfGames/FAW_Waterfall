using System.Collections;
using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// Plant-parented fish (target swarm Followers only): hidden until release time, then staggered homing to the spline.
/// Does not affect prefab-spawned fish on other swarms.
/// </summary>
[DefaultExecutionOrder(-50)]
public class PlantFishReleaseController : MonoBehaviour
{
    [Header("References")]
    [SerializeField] private VertexPathSwarmFollower targetSwarm;

    [Header("Release Timing")]
    [Tooltip("Seconds after Play before plant fish are turned on and begin homing to the path.")]
    [SerializeField] private float releaseAfterPlaySeconds = 10f;
    [Tooltip("When on, plant fish stay hidden until each fish is about to take off.")]
    [SerializeField] private bool hideUntilRelease = true;
    [Tooltip("Seconds each fish is shown on the plant immediately before unparenting and ascending.")]
    [SerializeField] private float visibleLeadSeconds = 0.001f;

    [Header("Stagger")]
    [Tooltip("Max seconds between first and last fish starting homing.")]
    [SerializeField] private float staggerSpreadSeconds = 10f;
    [Tooltip("Upper cap per-index delay (auto lowered when many fish).")]
    [SerializeField] private float maxStaggerPerFishSeconds = 0.35f;

    [Header("Plant Fish Scale")]
    [Tooltip("Scale on the plant before release (1 = authored mesh scale on the kelp/flower).")]
    [SerializeField] private float plantFishScaleFactor = 1f;
    [Tooltip("Grow from plant scale to convoy scale while ascending (0 = plant, 1 = full convoy size).")]
    [SerializeField] private bool growScaleDuringAscent = true;
    [Tooltip("Prefer vertical rise for scale growth; if ascent is flat, uses distance to the spline slot.")]
    [SerializeField] private bool useHeightForAscendScale = true;

    [Header("Homing")]
    [Tooltip("Unparent while flying to the spline so plant hierarchy scale does not hide fish.")]
    [SerializeField] private bool unparentDuringHoming = true;
    [SerializeField] private Transform homingParent;
    [Tooltip("World-units per second while ascending to the spline (constant speed per fish).")]
    [SerializeField] private float homingSpeed = 2.5f;
    [Tooltip("Max seconds to wait for an inactive plant parent before starting homing anyway.")]
    [SerializeField] private float maxWaitForPlantActiveSeconds = 45f;

    [Header("Join Convoy")]
    [Tooltip("Each fish must reach the spline within this many seconds after it begins homing.")]
    [SerializeField] private float joinConvoyDurationSeconds = 8f;
    [SerializeField] private float joinDistance = 0.75f;
    [SerializeField] private float homingTurnSpeed = 6f;

    private readonly List<int> _releaseIndices = new List<int>();
    private bool _releaseStarted;
    private bool _shutdown;
    private Coroutine _waitRoutine;
    private Coroutine _releaseAllRoutine;

    public bool IsShutdown => _shutdown;

    private void OnValidate()
    {
        releaseAfterPlaySeconds = Mathf.Max(0f, releaseAfterPlaySeconds);
        visibleLeadSeconds = Mathf.Max(0f, visibleLeadSeconds);
        staggerSpreadSeconds = Mathf.Max(0f, staggerSpreadSeconds);
        maxStaggerPerFishSeconds = Mathf.Max(0f, maxStaggerPerFishSeconds);
        joinConvoyDurationSeconds = Mathf.Max(0.1f, joinConvoyDurationSeconds);
        joinDistance = Mathf.Max(0.01f, joinDistance);
        homingTurnSpeed = Mathf.Max(0f, homingTurnSpeed);
        homingSpeed = Mathf.Max(0.1f, homingSpeed);
        maxWaitForPlantActiveSeconds = Mathf.Max(0f, maxWaitForPlantActiveSeconds);
        plantFishScaleFactor = Mathf.Max(0.01f, plantFishScaleFactor);
    }

    private void Awake()
    {
        if (targetSwarm == null)
        {
            targetSwarm = GetComponent<VertexPathSwarmFollower>();
        }

        if (targetSwarm != null && hideUntilRelease && HasAssignedFollowers())
        {
            targetSwarm.PreparePlantFishRelease(plantFishScaleFactor, hideFollowers: true);
        }
    }

    private void OnEnable()
    {
        ScheduleRelease();
    }

    private void OnDisable()
    {
        if (_waitRoutine != null)
        {
            StopCoroutine(_waitRoutine);
            _waitRoutine = null;
        }

        if (_releaseAllRoutine != null)
        {
            StopCoroutine(_releaseAllRoutine);
            _releaseAllRoutine = null;
        }
    }

    /// <summary>
    /// Stops all release coroutines and hides plant fish. Called when group A shrink completes.
    /// </summary>
    public void CancelReleaseAndShutdown()
    {
        _shutdown = true;
        _releaseStarted = true;

        if (_waitRoutine != null)
        {
            StopCoroutine(_waitRoutine);
            _waitRoutine = null;
        }

        if (_releaseAllRoutine != null)
        {
            StopCoroutine(_releaseAllRoutine);
            _releaseAllRoutine = null;
        }

        StopAllCoroutines();
        _releaseAllRoutine = null;
        _waitRoutine = null;

        if (targetSwarm != null)
        {
            targetSwarm.ShutdownAndHideAllFollowers();
        }
    }

    private bool CanReleaseFish()
    {
        return !_shutdown && targetSwarm != null && targetSwarm.IsSwarmRunning;
    }

    private void ScheduleRelease()
    {
        if (_waitRoutine != null)
        {
            StopCoroutine(_waitRoutine);
        }

        _waitRoutine = StartCoroutine(WaitAndReleaseRoutine());
    }

    private IEnumerator WaitAndReleaseRoutine()
    {
        if (releaseAfterPlaySeconds > 0f)
        {
            yield return new WaitForSeconds(releaseAfterPlaySeconds);
        }

        _waitRoutine = null;

        if (_shutdown)
        {
            yield break;
        }

        BeginRelease();
    }

    /// <summary>Starts homing from plants immediately (e.g. from UI or animation event).</summary>
    public void BeginRelease()
    {
        if (_shutdown || _releaseStarted || targetSwarm == null)
        {
            return;
        }

        _releaseStarted = true;

        // Follower list is often wired on prefab instances after Awake — register again here.
        targetSwarm.PreparePlantFishRelease(plantFishScaleFactor, hideUntilRelease);

        BuildReleaseIndexList();
        targetSwarm.BeginSwarmForPlantRelease();
        _releaseAllRoutine = StartCoroutine(ReleaseAllFishRoutine());
    }

    private void BuildReleaseIndexList()
    {
        _releaseIndices.Clear();
        int count = targetSwarm.FollowerCount;
        for (int i = 0; i < count; i++)
        {
            if (targetSwarm.Followers[i] != null)
            {
                _releaseIndices.Add(i);
            }
        }
    }

    private bool HasAssignedFollowers()
    {
        if (targetSwarm == null)
        {
            return false;
        }

        for (int i = 0; i < targetSwarm.FollowerCount; i++)
        {
            if (targetSwarm.Followers[i] != null)
            {
                return true;
            }
        }

        return false;
    }

    private IEnumerator ReleaseAllFishRoutine()
    {
        if (_releaseIndices.Count == 0)
        {
            Debug.LogWarning(
                "[PlantFishReleaseController] No followers assigned on target swarm — assign plant fish only (not spawned fish).",
                this);
            _releaseAllRoutine = null;
            yield break;
        }

        float staggerStep = GetStaggerStep(_releaseIndices.Count);
        int running = 0;

        for (int i = 0; i < _releaseIndices.Count; i++)
        {
            if (_shutdown || !CanReleaseFish())
            {
                break;
            }

            int followerIndex = _releaseIndices[i];
            float startDelay = i * staggerStep;
            running++;
            StartCoroutine(ReleaseFishRoutine(followerIndex, startDelay, () => running--));
        }

        while (running > 0)
        {
            if (_shutdown)
            {
                yield break;
            }

            yield return null;
        }

        _releaseAllRoutine = null;
    }

    private float GetStaggerStep(int fishCount)
    {
        if (fishCount <= 1)
        {
            return 0f;
        }

        return Mathf.Min(maxStaggerPerFishSeconds, staggerSpreadSeconds / (fishCount - 1));
    }

    private static bool IsHostingPlantActive(Transform follower)
    {
        Transform current = follower != null ? follower.parent : null;
        while (current != null)
        {
            if (!current.gameObject.activeSelf)
            {
                return false;
            }

            current = current.parent;
        }

        return follower != null && follower.parent != null;
    }

    private IEnumerator WaitUntilHostingPlantActive(Transform follower)
    {
        if (follower == null)
        {
            yield break;
        }

        float elapsed = 0f;
        while (!IsHostingPlantActive(follower) && elapsed < maxWaitForPlantActiveSeconds)
        {
            elapsed += Time.deltaTime;
            yield return null;
        }
    }

    private IEnumerator ReleaseFishRoutine(int followerIndex, float startDelay, System.Action onComplete)
    {
        if (startDelay > 0f)
        {
            yield return new WaitForSeconds(startDelay);
        }

        if (!CanReleaseFish() || followerIndex < 0 || followerIndex >= targetSwarm.FollowerCount)
        {
            onComplete?.Invoke();
            yield break;
        }

        Transform follower = targetSwarm.Followers[followerIndex];
        if (follower == null)
        {
            onComplete?.Invoke();
            yield break;
        }

        yield return WaitUntilHostingPlantActive(follower);

        follower = targetSwarm.Followers[followerIndex];
        if (follower == null)
        {
            onComplete?.Invoke();
            yield break;
        }

        Vector3 homingStartPosition = follower.position;
        Quaternion homingStartRotation = follower.rotation;

        if (hideUntilRelease)
        {
            follower.gameObject.SetActive(true);
            targetSwarm.ApplyPlantRestScale(followerIndex, plantFishScaleFactor);

            if (visibleLeadSeconds > 0f)
            {
                yield return new WaitForSeconds(visibleLeadSeconds);
            }
            else
            {
                yield return null;
            }
        }
        else
        {
            follower.gameObject.SetActive(true);
            targetSwarm.ApplyPlantRestScale(followerIndex, plantFishScaleFactor);
        }

        if (unparentDuringHoming)
        {
            Transform parent = homingParent != null ? homingParent : targetSwarm.transform;
            follower.SetParent(parent, true);
            follower.SetPositionAndRotation(homingStartPosition, homingStartRotation);
        }

        float homingStartDistance = -1f;
        float joinDeadline = Time.time + joinConvoyDurationSeconds;
        int failedPoseFrames = 0;

        while (true)
        {
            if (!CanReleaseFish())
            {
                if (follower != null)
                {
                    follower.gameObject.SetActive(false);
                }

                onComplete?.Invoke();
                yield break;
            }

            if (!targetSwarm.TryGetJoinWorldPose(followerIndex, out Vector3 joinPosition, out Quaternion joinRotation))
            {
                failedPoseFrames++;
                if (failedPoseFrames > 120)
                {
                    targetSwarm.JoinFollowerOnPath(followerIndex);
                    onComplete?.Invoke();
                    yield break;
                }

                yield return null;
                continue;
            }

            failedPoseFrames = 0;

            if (homingStartDistance < 0f)
            {
                homingStartDistance = Mathf.Max(0.01f, Vector3.Distance(homingStartPosition, joinPosition));
            }

            float distance = Vector3.Distance(follower.position, joinPosition);
            bool withinDistance = distance <= joinDistance;
            bool pastDeadline = Time.time >= joinDeadline;

            if (growScaleDuringAscent)
            {
                float ascend01 = ComputeAscendProgress(
                    follower.position,
                    homingStartPosition,
                    joinPosition,
                    distance,
                    homingStartDistance
                );
                targetSwarm.ApplyAscendScale(followerIndex, plantFishScaleFactor, ascend01);
            }

            if (withinDistance || pastDeadline)
            {
                targetSwarm.JoinFollowerOnPath(followerIndex);
                onComplete?.Invoke();
                yield break;
            }

            float step = Mathf.Min(distance, homingSpeed * Time.deltaTime);
            Vector3 direction = (joinPosition - follower.position).normalized;
            follower.position += direction * step;

            if (direction.sqrMagnitude > 0.0001f)
            {
                Quaternion faceMovement = Quaternion.LookRotation(direction, Vector3.up);
                follower.rotation = Quaternion.Slerp(
                    follower.rotation,
                    faceMovement,
                    homingTurnSpeed * Time.deltaTime
                );
            }
            else
            {
                follower.rotation = Quaternion.Slerp(
                    follower.rotation,
                    joinRotation,
                    homingTurnSpeed * Time.deltaTime
                );
            }

            yield return null;
        }
    }

    private float ComputeAscendProgress(
        Vector3 currentPosition,
        Vector3 startPosition,
        Vector3 joinPosition,
        float distanceToJoin,
        float startDistanceToJoin)
    {
        float distanceProgress = 1f - Mathf.Clamp01(distanceToJoin / startDistanceToJoin);

        if (!useHeightForAscendScale)
        {
            return distanceProgress;
        }

        float heightDelta = joinPosition.y - startPosition.y;
        if (Mathf.Abs(heightDelta) > 0.01f)
        {
            float heightProgress = Mathf.Clamp01((currentPosition.y - startPosition.y) / heightDelta);
            return Mathf.Max(heightProgress, distanceProgress);
        }

        return distanceProgress;
    }
}
