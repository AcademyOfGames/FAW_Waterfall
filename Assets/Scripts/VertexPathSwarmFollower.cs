using System.Collections;
using System.Collections.Generic;
using BezierSolution;
using UnityEngine;
using UnityEngine.Events;

/// <summary>
/// Fish ride a bezier spline as a conveyor belt: each fish owns a spline parameter
/// and advances by the same arc-length every frame.
/// </summary>
public class VertexPathSwarmFollower : MonoBehaviour
{
    private enum ModelForwardAxis
    {
        PositiveX,
        NegativeX,
        PositiveY,
        NegativeY,
        PositiveZ,
        NegativeZ
    }

    private class FollowerState
    {
        public int SlotIndex;
        public float NormalizedT;
        public float TubeAngle;
        public float TubeRadius;
        public float WobblePhase;
        public float WobbleSpeed;
        public float ScalePhase;
        public Vector3 BaseLocalScale;
        public bool HasBaseScale;
    }

    private const int PathSampleAccuracy = 12;

    [SerializeField] private List<Transform> followers = new List<Transform>();
    [SerializeField] private BezierSpline spline;

    [Header("Path")]
    [SerializeField] private TravelMode travelMode = TravelMode.Loop;
    [SerializeField] private float pathSpeed = 12f;
    [Range(0f, 1f)]
    [SerializeField] private float startNormalizedT;

    [Header("Tube Trail")]
    [Tooltip("Arc-length gap between the convoy front and the first fish.")]
    [SerializeField] private float headGap = 0.4f;
    [Tooltip("Fixed arc-length spacing assigned when each fish is created.")]
    [SerializeField] private float fishSpacing = 1f;
    [Tooltip("Maximum radial offset from the path centerline.")]
    [SerializeField] private float tubeRadius = 2f;
    [Range(0f, 1f)]
    [SerializeField] private float tubeRadiusVariation = 0.75f;
    [SerializeField] private float tubeWobbleAmplitude = 0.06f;
    [SerializeField] private float tubeWobbleSpeed = 1.4f;

    [Header("Fish Scale")]
    [Tooltip("Minimum uniform scale multiplier applied to each fish.")]
    [SerializeField] private float minFishScale = 3f;
    [Tooltip("Maximum uniform scale multiplier applied to each fish.")]
    [SerializeField] private float maxFishScale = 5f;
    [Tooltip("Seconds for one full pulse cycle (min → max → min).")]
    [SerializeField] private float fishScaleCycleSeconds = 2f;

    [Header("Orientation")]
    [Tooltip("Which local axis points out of the fish nose in the source mesh/prefab. Default +X is an estimate for this fish setup.")]
    [SerializeField] private ModelForwardAxis modelForwardAxis = ModelForwardAxis.PositiveX;
    [Tooltip("Extra Euler rotation after aligning to the path tangent and model-axis correction.")]
    [SerializeField] private Vector3 rotationOffsetEuler = Vector3.zero;

    [Header("Spawning")]
    [SerializeField] private GameObject followerPrefab;
    [SerializeField] private int maxFishCount = 400;
    [SerializeField] private float spawnMinInterval = 0.001f;
    [SerializeField] private float spawnMaxInterval = 0.001f;
    [SerializeField] private Transform spawnParent;

    [Header("Activation")]
    [Tooltip("When on, this swarm starts itself on Play using the delay and stagger below. When off, call RequestStart() or use SplineFishGroupOrchestrator to release group B.")]
    [SerializeField] private bool activateOnPlay = true;
    [Tooltip("Seconds to wait before the swarm starts (after Play or after RequestStart()).")]
    [SerializeField] private float activateDelaySeconds;
    [Tooltip("Random spread for turning on pre-assigned followers after the swarm starts. Does not affect spawned fish.")]
    [SerializeField] private float activateStaggerSeconds;
    [Tooltip("Invoked when movement begins (after any activate delay).")]
    [SerializeField] private UnityEvent onSwarmStarted;

    private readonly List<FollowerState> _states = new List<FollowerState>();

    private bool _movingForward = true;
    private bool _swarmRunning;
    private float _externalScaleMultiplier = 1f;
    private float _nextSpawnTime;
    private Coroutine _activateFollowersRoutine;
    private Coroutine _delayedStartRoutine;
    private bool _swarmFollowingActive;
    private int _nextSlotIndex;
    private float _swarmStartedTime = -1f;

    public bool IsSwarmRunning => _swarmRunning;
    public bool IsAwaitingStart => _delayedStartRoutine != null;
    public bool HasSwarmStarted => _swarmStartedTime >= 0f;
    public float SwarmStartedTime => _swarmStartedTime;

    public event System.Action SwarmStarted;

    private void OnValidate()
    {
        headGap = Mathf.Max(0f, headGap);
        fishSpacing = Mathf.Max(0.05f, fishSpacing);
        tubeRadius = Mathf.Max(0f, tubeRadius);
        tubeWobbleAmplitude = Mathf.Max(0f, tubeWobbleAmplitude);
        tubeWobbleSpeed = Mathf.Max(0f, tubeWobbleSpeed);
        minFishScale = Mathf.Max(0.01f, minFishScale);
        maxFishScale = Mathf.Max(minFishScale, maxFishScale);
        fishScaleCycleSeconds = Mathf.Max(0.01f, fishScaleCycleSeconds);
        pathSpeed = Mathf.Max(0f, pathSpeed);
        maxFishCount = Mathf.Max(1, maxFishCount);
        spawnMinInterval = Mathf.Max(0f, spawnMinInterval);
        spawnMaxInterval = Mathf.Max(spawnMinInterval, spawnMaxInterval);
        activateDelaySeconds = Mathf.Max(0f, activateDelaySeconds);
        activateStaggerSeconds = Mathf.Max(0f, activateStaggerSeconds);
    }

    private void Awake()
    {
        _movingForward = true;
    }

    private void Start()
    {
        AssignTrailSlots();
        ScheduleNextSpawn();

        if (activateOnPlay)
        {
            ScheduleAutoStart();
        }
        else
        {
            DeactivateAllFollowers();
        }
    }

    private void ScheduleAutoStart()
    {
        CancelDelayedStart();

        if (activateDelaySeconds > 0f)
        {
            DeactivateAllFollowers();
            _delayedStartRoutine = StartCoroutine(DelayedBeginSwarmRoutine());
            return;
        }

        BeginSwarmInternal(activateStaggerSeconds);
    }

    /// <summary>
    /// Starts this swarm using this component's delay and stagger settings.
    /// Used by SplineFishGroupOrchestrator for group B and for manual triggers.
    /// </summary>
    public void RequestStart()
    {
        ScheduleAutoStart();
    }

    private IEnumerator DelayedBeginSwarmRoutine()
    {
        yield return new WaitForSeconds(activateDelaySeconds);
        _delayedStartRoutine = null;
        BeginSwarmInternal(activateStaggerSeconds);
    }

    private void CancelDelayedStart()
    {
        if (_delayedStartRoutine == null)
        {
            return;
        }

        StopCoroutine(_delayedStartRoutine);
        _delayedStartRoutine = null;
    }

    private void Update()
    {
        if (spline == null)
        {
            return;
        }

        EnsureBuffers();

        if (_swarmRunning)
        {
            AdvanceAllFish(Time.deltaTime);
        }

        if (_swarmRunning && _swarmFollowingActive)
        {
            TrySpawnFollower();
        }

        for (int i = 0; i < followers.Count; i++)
        {
            Transform follower = followers[i];
            FollowerState state = _states[i];
            if (!IsFollowerActive(follower) || state == null)
            {
                continue;
            }

            if (!SampleFishFrame(state, out Vector3 slotCenter, out Vector3 tangent, out Vector3 right, out Vector3 up))
            {
                continue;
            }

            follower.position = ApplyTubeOffset(state, slotCenter, right, up);
            follower.rotation = GetFollowerRotation(tangent, up);
            ApplyFishScale(follower, state);
        }
    }

    public void ActivateFollowersAndStartSwarm()
    {
        RequestStart();
    }

    public void ActivateFollowersAndStartSwarm(float activateStaggerSeconds)
    {
        CancelDelayedStart();
        BeginSwarmInternal(activateStaggerSeconds);
    }

    public void BeginSwarm()
    {
        BeginSwarmInternal(activateStaggerSeconds);
    }

    public void BeginSwarm(float activateStaggerSeconds)
    {
        CancelDelayedStart();
        BeginSwarmInternal(activateStaggerSeconds);
    }

    private void BeginSwarmInternal(float staggerSeconds)
    {
        CancelDelayedStart();

        if (_activateFollowersRoutine != null)
        {
            StopCoroutine(_activateFollowersRoutine);
        }

        _externalScaleMultiplier = 1f;
        _swarmRunning = true;
        _swarmStartedTime = Time.time;
        onSwarmStarted?.Invoke();
        SwarmStarted?.Invoke();
        _activateFollowersRoutine = StartCoroutine(
            ActivateFollowersStaggeredRoutine(staggerSeconds)
        );
    }

    public void StopSwarmImmediate()
    {
        CancelDelayedStart();

        if (_activateFollowersRoutine != null)
        {
            StopCoroutine(_activateFollowersRoutine);
            _activateFollowersRoutine = null;
        }

        _swarmRunning = false;
        _swarmFollowingActive = false;
        _externalScaleMultiplier = 1f;
        _swarmStartedTime = -1f;
        DeactivateAllFollowers();
    }

    public IEnumerator ShrinkAndStopSwarm(float duration)
    {
        duration = Mathf.Max(0.01f, duration);
        float elapsed = 0f;

        while (elapsed < duration)
        {
            elapsed += Time.deltaTime;
            _externalScaleMultiplier = Mathf.Lerp(1f, 0f, elapsed / duration);
            yield return null;
        }

        _externalScaleMultiplier = 0f;
        StopSwarmImmediate();
    }

    private void DeactivateAllFollowers()
    {
        for (int i = 0; i < followers.Count; i++)
        {
            Transform follower = followers[i];
            if (follower != null)
            {
                follower.gameObject.SetActive(false);
            }
        }
    }

    private IEnumerator ActivateFollowersStaggeredRoutine(float activateStaggerSeconds)
    {
        _swarmFollowingActive = true;
        AssignTrailSlots();
        ScheduleNextSpawn();

        var activationSchedule = new List<(int index, Transform follower, float activateTime)>();
        for (int i = 0; i < followers.Count; i++)
        {
            Transform follower = followers[i];
            if (follower == null)
            {
                continue;
            }

            float activateTime = activateStaggerSeconds <= 0f
                ? 0f
                : Random.Range(0f, activateStaggerSeconds);
            activationSchedule.Add((i, follower, activateTime));
        }

        activationSchedule.Sort((a, b) => a.activateTime.CompareTo(b.activateTime));

        float startTime = Time.time;
        for (int i = 0; i < activationSchedule.Count; i++)
        {
            (int index, Transform follower, float activateTime) = activationSchedule[i];
            float wait = activateTime - (Time.time - startTime);
            if (wait > 0f)
            {
                yield return new WaitForSeconds(wait);
            }

            follower.gameObject.SetActive(true);
            SnapFollowerToSlot(index);
        }

        _activateFollowersRoutine = null;
    }

    private void AdvanceAllFish(float deltaTime)
    {
        if (pathSpeed <= 0f)
        {
            return;
        }

        float delta = (_movingForward ? pathSpeed : -pathSpeed) * deltaTime;

        for (int i = 0; i < followers.Count; i++)
        {
            if (_states[i] == null)
            {
                continue;
            }

            FollowerState state = _states[i];
            spline.MoveAlongSpline(ref state.NormalizedT, delta, PathSampleAccuracy);
            WrapFishNormalizedT(state);
        }

        PostProcessConvoyMovement();
    }

    private void WrapFishNormalizedT(FollowerState state)
    {
        if (travelMode == TravelMode.Loop || travelMode == TravelMode.Once)
        {
            state.NormalizedT = Mathf.Repeat(state.NormalizedT, 1f);
        }
    }

    private void PostProcessConvoyMovement()
    {
        if (travelMode != TravelMode.PingPong)
        {
            return;
        }

        FollowerState lead = GetLeadState();
        if (lead == null)
        {
            return;
        }

        if (_movingForward)
        {
            if (lead.NormalizedT < 1f)
            {
                return;
            }

            lead.NormalizedT = 2f - lead.NormalizedT;
            _movingForward = false;
            return;
        }

        if (lead.NormalizedT > 0f)
        {
            return;
        }

        lead.NormalizedT = -lead.NormalizedT;
        _movingForward = true;
    }

    private FollowerState GetLeadState()
    {
        FollowerState lead = null;
        int bestSlot = int.MaxValue;

        for (int i = 0; i < followers.Count; i++)
        {
            if (!IsFollowerActive(followers[i]) || _states[i] == null)
            {
                continue;
            }

            if (_states[i].SlotIndex < bestSlot)
            {
                bestSlot = _states[i].SlotIndex;
                lead = _states[i];
            }
        }

        return lead;
    }

    private void AssignTrailSlots()
    {
        _states.Clear();
        _nextSlotIndex = 0;
        _movingForward = true;

        for (int i = 0; i < followers.Count; i++)
        {
            if (followers[i] == null)
            {
                _states.Add(null);
                continue;
            }

            _states.Add(CreateFollowerState(_nextSlotIndex));
            _nextSlotIndex++;
        }
    }

    private FollowerState CreateFollowerState(int slotIndex)
    {
        float radiusScale = tubeRadiusVariation <= 0f
            ? 0f
            : Random.Range(1f - tubeRadiusVariation, 1f);

        return new FollowerState
        {
            SlotIndex = slotIndex,
            NormalizedT = ComputeNormalizedTForNewSlot(slotIndex),
            TubeAngle = Random.Range(0f, Mathf.PI * 2f),
            TubeRadius = tubeRadius * radiusScale,
            WobblePhase = Random.Range(0f, Mathf.PI * 2f),
            WobbleSpeed = tubeWobbleSpeed * Random.Range(0.85f, 1.15f),
            ScalePhase = Random.Range(0f, fishScaleCycleSeconds)
        };
    }

    private float ComputeNormalizedTForNewSlot(int slotIndex)
    {
        if (slotIndex <= 0)
        {
            return OffsetNormalizedTFromFront(headGap);
        }

        FollowerState previous = FindStateBySlot(slotIndex - 1);
        if (previous != null)
        {
            float normalizedT = previous.NormalizedT;
            float delta = _movingForward ? -fishSpacing : fishSpacing;
            spline.MoveAlongSpline(ref normalizedT, delta, PathSampleAccuracy);
            return normalizedT;
        }

        return OffsetNormalizedTFromFront(headGap + slotIndex * fishSpacing);
    }

    private FollowerState FindStateBySlot(int slotIndex)
    {
        for (int i = 0; i < _states.Count; i++)
        {
            if (_states[i] != null && _states[i].SlotIndex == slotIndex)
            {
                return _states[i];
            }
        }

        return null;
    }

    private float OffsetNormalizedTFromFront(float arcDistanceBehindFront)
    {
        float normalizedT = startNormalizedT;
        float delta = _movingForward ? -arcDistanceBehindFront : arcDistanceBehindFront;
        spline.MoveAlongSpline(ref normalizedT, delta, PathSampleAccuracy);
        return normalizedT;
    }

    private void TrySpawnFollower()
    {
        if (!_swarmFollowingActive || followerPrefab == null || Time.time < _nextSpawnTime)
        {
            return;
        }

        ScheduleNextSpawn();

        if (_nextSlotIndex >= maxFishCount)
        {
            return;
        }

        FollowerState spawnState = CreateFollowerState(_nextSlotIndex);
        _nextSlotIndex++;

        Vector3 spawnPosition = spline.GetPoint(spawnState.NormalizedT);
        if (SampleFishFrame(spawnState, out Vector3 slotCenter, out _, out Vector3 right, out Vector3 up))
        {
            spawnPosition = ApplyTubeOffset(spawnState, slotCenter, right, up);
        }

        Transform parent = spawnParent != null ? spawnParent : transform;
        GameObject instance = Instantiate(followerPrefab, spawnPosition, Quaternion.identity, parent);
        instance.SetActive(true);

        followers.Add(instance.transform);
        _states.Add(spawnState);
        InitializeFollowerScale(instance.transform, spawnState);
    }

    private void SnapFollowerToSlot(int index)
    {
        if (index < 0 || index >= followers.Count || index >= _states.Count)
        {
            return;
        }

        Transform follower = followers[index];
        FollowerState state = _states[index];
        if (!IsFollowerActive(follower) || state == null)
        {
            return;
        }

        if (!SampleFishFrame(state, out Vector3 slotCenter, out Vector3 tangent, out Vector3 right, out Vector3 up))
        {
            return;
        }

        follower.position = ApplyTubeOffset(state, slotCenter, right, up);
        follower.rotation = GetFollowerRotation(tangent, up);
        ApplyFishScale(follower, state);
    }

    private Quaternion GetFollowerRotation(Vector3 tangent, Vector3 pathUp)
    {
        Quaternion modelAxisCorrection = Quaternion.FromToRotation(
            GetModelForwardAxisVector(modelForwardAxis),
            Vector3.forward
        );
        Quaternion extraOffset = Quaternion.Euler(rotationOffsetEuler);

        if (tangent.sqrMagnitude <= 0.0001f)
        {
            return modelAxisCorrection * extraOffset;
        }

        Vector3 upDirection = pathUp.sqrMagnitude > 0.0001f ? pathUp : Vector3.up;
        return Quaternion.LookRotation(tangent, upDirection) * modelAxisCorrection * extraOffset;
    }

    private static Vector3 GetModelForwardAxisVector(ModelForwardAxis axis)
    {
        switch (axis)
        {
            case ModelForwardAxis.PositiveX:
                return Vector3.right;
            case ModelForwardAxis.NegativeX:
                return Vector3.left;
            case ModelForwardAxis.PositiveY:
                return Vector3.up;
            case ModelForwardAxis.NegativeY:
                return Vector3.down;
            case ModelForwardAxis.PositiveZ:
                return Vector3.forward;
            case ModelForwardAxis.NegativeZ:
                return Vector3.back;
            default:
                return Vector3.forward;
        }
    }

    private void InitializeFollowerScale(Transform follower, FollowerState state)
    {
        if (follower == null || state == null || state.HasBaseScale)
        {
            return;
        }

        state.BaseLocalScale = follower.localScale;
        state.HasBaseScale = true;
    }

    private void ApplyFishScale(Transform follower, FollowerState state)
    {
        if (follower == null || state == null)
        {
            return;
        }

        InitializeFollowerScale(follower, state);

        if (fishScaleCycleSeconds <= 0f || Mathf.Approximately(minFishScale, maxFishScale))
        {
            follower.localScale = state.BaseLocalScale * minFishScale * _externalScaleMultiplier;
            return;
        }

        float phaseTime = Time.time + state.ScalePhase;
        float t = (Mathf.Sin(phaseTime / fishScaleCycleSeconds * (Mathf.PI * 2f)) + 1f) * 0.5f;
        float scaleMultiplier = Mathf.Lerp(minFishScale, maxFishScale, t);
        follower.localScale = state.BaseLocalScale * scaleMultiplier * _externalScaleMultiplier;
    }

    private Vector3 ApplyTubeOffset(
        FollowerState state,
        Vector3 center,
        Vector3 right,
        Vector3 up)
    {
        float wobble = tubeWobbleAmplitude > 0f
            ? Mathf.Sin(Time.time * state.WobbleSpeed + state.WobblePhase) * tubeWobbleAmplitude
            : 0f;
        float radius = state.TubeRadius + wobble;

        return center
            + right * (Mathf.Cos(state.TubeAngle) * radius)
            + up * (Mathf.Sin(state.TubeAngle) * radius);
    }

    private bool SampleFishFrame(
        FollowerState state,
        out Vector3 position,
        out Vector3 tangent,
        out Vector3 right,
        out Vector3 up)
    {
        position = Vector3.zero;
        tangent = Vector3.forward;
        right = Vector3.right;
        up = Vector3.up;

        if (spline == null)
        {
            return false;
        }

        float normalizedT = state.NormalizedT;
        position = spline.GetPoint(normalizedT);

        tangent = spline.GetTangent(normalizedT);
        if (tangent.sqrMagnitude > 0.0001f)
        {
            tangent.Normalize();
            if (!_movingForward)
            {
                tangent = -tangent;
            }
        }
        else
        {
            tangent = Vector3.forward;
        }

        Vector3 normal = spline.GetNormal(normalizedT);
        if (normal.sqrMagnitude < 0.0001f)
        {
            normal = Vector3.up;
        }
        else
        {
            normal.Normalize();
        }

        right = Vector3.Cross(normal, tangent);
        if (right.sqrMagnitude < 0.0001f)
        {
            right = Vector3.Cross(Vector3.up, tangent);
        }

        right.Normalize();
        up = Vector3.Cross(tangent, right).normalized;
        return true;
    }

    private void ScheduleNextSpawn()
    {
        _nextSpawnTime = Time.time + Random.Range(spawnMinInterval, spawnMaxInterval);
    }

    private static bool IsFollowerActive(Transform follower)
    {
        return follower != null && follower.gameObject.activeSelf;
    }

    private void EnsureBuffers()
    {
        while (_states.Count < followers.Count)
        {
            _states.Add(CreateFollowerState(_nextSlotIndex));
            _nextSlotIndex++;
        }

        if (_states.Count > followers.Count)
        {
            _states.RemoveRange(followers.Count, _states.Count - followers.Count);
        }
    }
}
