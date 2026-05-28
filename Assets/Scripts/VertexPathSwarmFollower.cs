using System.Collections;
using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// Swarm followers with boids, per-fish speed variation, occasional straying,
/// and a few camera-curious fish that watch or flee from the main camera.
/// </summary>
public class VertexPathSwarmFollower : MonoBehaviour
{
    private enum FollowerMode
    {
        Following,
        Straying,
        SeekingCamera,
        WatchingCamera,
        EscapingCamera
    }

    private class FollowerState
    {
        public float MaxSpeed;
        public FollowerMode Mode;
        public float ModeEndTime;
        public float NextBehaviorRollTime;
        public Vector3 StrayOffset;
        public bool IsCameraCurious;
        public float SwayPhase;
        public float FormationBehind;
        public float FormationLateral;
        public float FormationVertical;
    }

    [SerializeField] private List<Transform> followers = new List<Transform>();
    [SerializeField] private Transform target;
    [SerializeField] private float followSmoothTime = 0.35f;
    [SerializeField] private float followSpeed = 20f;
    [SerializeField] private float turnSpeed = 6f;

    [Header("Speed Variation")]
    [SerializeField] private float minSpeedMultiplier = 0.65f;
    [SerializeField] private float maxSpeedMultiplier = 1.35f;

    [Header("Stray")]
    [SerializeField] private float strayOffsetDistance = 4f;
    [SerializeField] private float strayMinDuration = 2f;
    [SerializeField] private float strayMaxDuration = 6f;
    [SerializeField] private float strayChance = 0.35f;
    [SerializeField] private float behaviorRollMinInterval = 4f;
    [SerializeField] private float behaviorRollMaxInterval = 12f;

    [Header("Camera Curious")]
    [SerializeField] private int minCameraCuriousCount = 2;
    [SerializeField] private int maxCameraCuriousCount = 5;
    [SerializeField] private float cameraSeekChance = 0.4f;
    [SerializeField] private float cameraWatchDistance = 2.5f;
    [SerializeField] private float cameraArriveDistance = 0.6f;
    [SerializeField] private float cameraWatchMinDuration = 3f;
    [SerializeField] private float cameraWatchMaxDuration = 8f;
    [SerializeField] private float cameraSeekMaxDuration = 15f;
    [SerializeField] private float cameraTooCloseDistance = 1.2f;
    [SerializeField] private float cameraSafeDistance = 2.5f;
    [SerializeField] private float escapeSpeed = 35f;
    [SerializeField] private float escapeFleeDistance = 4f;
    [SerializeField] private float cameraLookTurnSpeed = 8f;

    [Header("Sway")]
    [SerializeField] private float swayAmplitude = 0.15f;
    [SerializeField] private float swaySpeed = 1.2f;

    [Header("Spawning")]
    [SerializeField] private GameObject followerPrefab;
    [SerializeField] private int maxFishCount = 40;
    [SerializeField] private float spawnDistance = 2f;
    [SerializeField] private float spawnMinInterval = 8f;
    [SerializeField] private float spawnMaxInterval = 20f;
    [SerializeField] private Transform spawnParent;

    [Header("Formation (Gaussian Trail)")]
    [Tooltip("Peak distance behind the target where most fish aim to swim.")]
    [SerializeField] private float formationMeanBehind = 3.5f;
    [SerializeField] private float formationStdBehind = 2f;
    [SerializeField] private float formationStdLateral = 1.4f;
    [SerializeField] private float formationStdVertical = 0.35f;
    [SerializeField] private float formationMinBehind = 0.75f;
    [SerializeField] private float formationMaxBehind = 14f;
    [Range(0f, 1f)]
    [SerializeField] private float formationCohesionScale = 0.35f;

    [Header("Boids")]
    [Tooltip("Neighbors within this distance contribute separation, alignment, and cohesion.")]
    [SerializeField] private float neighborRadius = 3f;
    [SerializeField] private float separationWeight = 1.5f;
    [SerializeField] private float alignmentWeight = 0.8f;
    [SerializeField] private float cohesionWeight = 0.5f;
    [Tooltip("Caps how far boid steering can offset the follow target.")]
    [SerializeField] private float maxBoidOffset = 2f;

    private readonly List<Vector3> _velocities = new List<Vector3>();
    private readonly List<Vector3> _desiredPositions = new List<Vector3>();
    private readonly List<FollowerState> _states = new List<FollowerState>();

    private Transform _cameraTransform;
    private float _neighborRadiusSq;
    private float _nextSpawnTime;
    private Vector3 _targetPreviousPosition;
    private Vector3 _targetMoveDirection = Vector3.forward;
    private Coroutine _activateFollowersRoutine;
    private bool _swarmFollowingActive;

    private void OnValidate()
    {
        followSmoothTime = Mathf.Max(0.01f, followSmoothTime);
        followSpeed = Mathf.Max(0.01f, followSpeed);
        turnSpeed = Mathf.Max(0f, turnSpeed);
        minSpeedMultiplier = Mathf.Max(0.1f, minSpeedMultiplier);
        maxSpeedMultiplier = Mathf.Max(minSpeedMultiplier, maxSpeedMultiplier);
        neighborRadius = Mathf.Max(0.01f, neighborRadius);
        separationWeight = Mathf.Max(0f, separationWeight);
        alignmentWeight = Mathf.Max(0f, alignmentWeight);
        cohesionWeight = Mathf.Max(0f, cohesionWeight);
        maxBoidOffset = Mathf.Max(0f, maxBoidOffset);
        strayOffsetDistance = Mathf.Max(0.1f, strayOffsetDistance);
        strayMinDuration = Mathf.Max(0.1f, strayMinDuration);
        strayMaxDuration = Mathf.Max(strayMinDuration, strayMaxDuration);
        behaviorRollMinInterval = Mathf.Max(0.1f, behaviorRollMinInterval);
        behaviorRollMaxInterval = Mathf.Max(behaviorRollMinInterval, behaviorRollMaxInterval);
        minCameraCuriousCount = Mathf.Max(0, minCameraCuriousCount);
        maxCameraCuriousCount = Mathf.Max(minCameraCuriousCount, maxCameraCuriousCount);
        cameraWatchDistance = Mathf.Max(0.1f, cameraWatchDistance);
        cameraArriveDistance = Mathf.Max(0.1f, cameraArriveDistance);
        cameraWatchMinDuration = Mathf.Max(0.1f, cameraWatchMinDuration);
        cameraWatchMaxDuration = Mathf.Max(cameraWatchMinDuration, cameraWatchMaxDuration);
        cameraTooCloseDistance = Mathf.Max(0.1f, cameraTooCloseDistance);
        cameraSafeDistance = Mathf.Max(cameraTooCloseDistance, cameraSafeDistance);
        escapeSpeed = Mathf.Max(followSpeed, escapeSpeed);
        escapeFleeDistance = Mathf.Max(0.1f, escapeFleeDistance);
        cameraLookTurnSpeed = Mathf.Max(0f, cameraLookTurnSpeed);
        swayAmplitude = Mathf.Max(0f, swayAmplitude);
        swaySpeed = Mathf.Max(0f, swaySpeed);
        maxFishCount = Mathf.Max(1, maxFishCount);
        spawnDistance = Mathf.Max(0.1f, spawnDistance);
        spawnMinInterval = Mathf.Max(0.1f, spawnMinInterval);
        spawnMaxInterval = Mathf.Max(spawnMinInterval, spawnMaxInterval);
        formationMeanBehind = Mathf.Max(0f, formationMeanBehind);
        formationStdBehind = Mathf.Max(0.01f, formationStdBehind);
        formationStdLateral = Mathf.Max(0.01f, formationStdLateral);
        formationStdVertical = Mathf.Max(0.01f, formationStdVertical);
        formationMinBehind = Mathf.Max(0f, formationMinBehind);
        formationMaxBehind = Mathf.Max(formationMinBehind, formationMaxBehind);
        _neighborRadiusSq = neighborRadius * neighborRadius;
    }

    private void Awake()
    {
        _neighborRadiusSq = neighborRadius * neighborRadius;
        CacheCamera();
    }

    private void Start()
    {
        ResetVelocities();
        InitializeFollowerStates();
        ScheduleNextSpawn();
        if (target != null)
        {
            _targetPreviousPosition = target.position;
            _targetMoveDirection = target.forward.sqrMagnitude > 0.0001f ? target.forward : Vector3.forward;
        }
    }

    private void Update()
    {
        if (target == null)
        {
            return;
        }

        CacheCamera();
        EnsureBuffers();
        if (_swarmFollowingActive)
        {
            TrySpawnFollower();
        }

        UpdateTargetMotion();

        Vector3 targetPosition = target.position;
        for (int i = 0; i < followers.Count; i++)
        {
            Transform follower = followers[i];
            if (!IsFollowerActive(follower))
            {
                continue;
            }

            UpdateBehaviorMode(i);
            _desiredPositions[i] = ComputeDesiredPosition(i, targetPosition);
        }

        for (int i = 0; i < followers.Count; i++)
        {
            Transform follower = followers[i];
            FollowerState state = _states[i];
            if (!IsFollowerActive(follower) || state == null)
            {
                continue;
            }

            Vector3 oldPosition = follower.position;
            Vector3 velocity = _velocities[i];
            float maxSpeed = GetMaxSpeed(state);
            follower.position = Vector3.SmoothDamp(
                oldPosition,
                _desiredPositions[i],
                ref velocity,
                followSmoothTime,
                maxSpeed
            );
            _velocities[i] = velocity;

            ApplyRotation(follower, state, oldPosition);
        }
    }

    private void InitializeFollowerStates()
    {
        _states.Clear();
        HashSet<int> cameraCuriousIndices = PickCameraCuriousIndices();

        for (int i = 0; i < followers.Count; i++)
        {
            _states.Add(CreateFollowerState(cameraCuriousIndices.Contains(i)));
        }
    }

    private FollowerState CreateFollowerState(bool isCameraCurious)
    {
        float speedMultiplier = Random.Range(minSpeedMultiplier, maxSpeedMultiplier);
        return new FollowerState
        {
            MaxSpeed = followSpeed * speedMultiplier,
            Mode = FollowerMode.Following,
            ModeEndTime = 0f,
            NextBehaviorRollTime = Time.time + Random.Range(behaviorRollMinInterval, behaviorRollMaxInterval),
            StrayOffset = Vector3.zero,
            IsCameraCurious = isCameraCurious,
            SwayPhase = Random.Range(0f, Mathf.PI * 2f),
            FormationBehind = SampleFormationBehind(),
            FormationLateral = SampleGaussian(0f, formationStdLateral),
            FormationVertical = SampleGaussian(0f, formationStdVertical)
        };
    }

    private float SampleFormationBehind()
    {
        float behind = SampleGaussian(formationMeanBehind, formationStdBehind);
        return Mathf.Clamp(behind, formationMinBehind, formationMaxBehind);
    }

    private static float SampleGaussian(float mean, float standardDeviation)
    {
        float u1 = Mathf.Max(1e-6f, Random.value);
        float u2 = Random.value;
        float standardNormal = Mathf.Sqrt(-2f * Mathf.Log(u1)) * Mathf.Cos(2f * Mathf.PI * u2);
        return mean + standardDeviation * standardNormal;
    }

    private void UpdateTargetMotion()
    {
        Vector3 delta = target.position - _targetPreviousPosition;
        if (delta.sqrMagnitude > 0.0001f)
        {
            _targetMoveDirection = delta.normalized;
        }
        else if (target.forward.sqrMagnitude > 0.0001f)
        {
            _targetMoveDirection = target.forward;
        }

        _targetPreviousPosition = target.position;
    }

    private Vector3 GetFormationOffset(FollowerState state)
    {
        Vector3 behind = -_targetMoveDirection;
        behind.y = 0f;
        if (behind.sqrMagnitude < 0.0001f)
        {
            behind = -target.forward;
            behind.y = 0f;
        }

        behind.Normalize();
        Vector3 right = Vector3.Cross(Vector3.up, behind);
        if (right.sqrMagnitude < 0.0001f)
        {
            right = Vector3.right;
        }

        right.Normalize();
        Vector3 up = Vector3.up;

        return (behind * state.FormationBehind)
            + (right * state.FormationLateral)
            + (up * state.FormationVertical);
    }

    private void ScheduleNextSpawn()
    {
        _nextSpawnTime = Time.time + Random.Range(spawnMinInterval, spawnMaxInterval);
    }

    private static bool IsFollowerActive(Transform follower)
    {
        return follower != null && follower.gameObject.activeSelf;
    }

    private void TrySpawnFollower()
    {
        if (!_swarmFollowingActive || followerPrefab == null || Time.time < _nextSpawnTime)
        {
            return;
        }

        ScheduleNextSpawn();

        if (GetValidFollowerCount() >= maxFishCount)
        {
            return;
        }

        Vector3 offset = Random.onUnitSphere;
        offset.y *= 0.4f;
        if (offset.sqrMagnitude < 0.0001f)
        {
            offset = Vector3.right;
        }

        offset = offset.normalized * spawnDistance;
        Vector3 spawnPosition = target.position + offset;
        Transform parent = spawnParent != null ? spawnParent : transform;
        GameObject instance = Instantiate(followerPrefab, spawnPosition, Quaternion.identity, parent);
        instance.SetActive(true);

        followers.Add(instance.transform);
        _velocities.Add(Vector3.zero);
        _desiredPositions.Add(spawnPosition);
        _states.Add(CreateFollowerState(isCameraCurious: false));
    }

    private int GetValidFollowerCount()
    {
        int count = 0;
        for (int i = 0; i < followers.Count; i++)
        {
            if (followers[i] != null)
            {
                count++;
            }
        }

        return count;
    }

    private HashSet<int> PickCameraCuriousIndices()
    {
        var chosen = new HashSet<int>();
        if (followers.Count == 0)
        {
            return chosen;
        }

        int desiredCount = Mathf.Clamp(
            Random.Range(minCameraCuriousCount, maxCameraCuriousCount + 1),
            0,
            followers.Count
        );

        var candidates = new List<int>();
        for (int i = 0; i < followers.Count; i++)
        {
            if (followers[i] != null)
            {
                candidates.Add(i);
            }
        }

        while (chosen.Count < desiredCount && candidates.Count > 0)
        {
            int pick = Random.Range(0, candidates.Count);
            chosen.Add(candidates[pick]);
            candidates.RemoveAt(pick);
        }

        return chosen;
    }

    private void UpdateBehaviorMode(int index)
    {
        FollowerState state = _states[index];
        Transform follower = followers[index];
        if (state == null || follower == null)
        {
            return;
        }

        float cameraDistance = GetCameraDistance(follower.position);

        if (_cameraTransform != null && cameraDistance < cameraTooCloseDistance)
        {
            EnterEscapeMode(state);
            return;
        }

        switch (state.Mode)
        {
            case FollowerMode.Following:
                if (Time.time >= state.NextBehaviorRollTime)
                {
                    TryRollNewBehavior(state);
                }
                break;

            case FollowerMode.Straying:
                if (Time.time >= state.ModeEndTime)
                {
                    SetFollowing(state);
                }
                break;

            case FollowerMode.SeekingCamera:
                if (_cameraTransform == null)
                {
                    SetFollowing(state);
                    break;
                }

                if (HasReachedCameraWatchPoint(follower.position))
                {
                    EnterWatchCameraMode(state);
                }
                else if (Time.time >= state.ModeEndTime)
                {
                    SetFollowing(state);
                }
                break;

            case FollowerMode.WatchingCamera:
                if (_cameraTransform == null)
                {
                    SetFollowing(state);
                    break;
                }

                if (Time.time >= state.ModeEndTime)
                {
                    SetFollowing(state);
                }
                break;

            case FollowerMode.EscapingCamera:
                if (_cameraTransform == null || cameraDistance >= cameraSafeDistance)
                {
                    SetFollowing(state);
                }
                break;
        }
    }

    private void TryRollNewBehavior(FollowerState state)
    {
        state.NextBehaviorRollTime = Time.time + Random.Range(behaviorRollMinInterval, behaviorRollMaxInterval);

        if (state.IsCameraCurious && _cameraTransform != null && Random.value < cameraSeekChance)
        {
            EnterSeekCameraMode(state);
            return;
        }

        if (Random.value < strayChance)
        {
            EnterStrayMode(state);
        }
    }

    private void EnterStrayMode(FollowerState state)
    {
        Vector3 randomOffset = Random.onUnitSphere;
        randomOffset.y *= 0.35f;
        if (randomOffset.sqrMagnitude < 0.0001f)
        {
            randomOffset = Vector3.right;
        }

        state.StrayOffset = randomOffset.normalized * strayOffsetDistance;
        state.Mode = FollowerMode.Straying;
        state.ModeEndTime = Time.time + Random.Range(strayMinDuration, strayMaxDuration);
    }

    private void EnterSeekCameraMode(FollowerState state)
    {
        state.Mode = FollowerMode.SeekingCamera;
        state.ModeEndTime = Time.time + cameraSeekMaxDuration;
    }

    private void EnterWatchCameraMode(FollowerState state)
    {
        state.Mode = FollowerMode.WatchingCamera;
        state.ModeEndTime = Time.time + Random.Range(cameraWatchMinDuration, cameraWatchMaxDuration);
    }

    private void EnterEscapeMode(FollowerState state)
    {
        state.Mode = FollowerMode.EscapingCamera;
        state.ModeEndTime = Time.time + 5f;
    }

    private void SetFollowing(FollowerState state)
    {
        state.Mode = FollowerMode.Following;
        state.ModeEndTime = 0f;
        state.StrayOffset = Vector3.zero;
    }

    private Vector3 ComputeDesiredPosition(int index, Vector3 targetPosition)
    {
        Transform follower = followers[index];
        FollowerState state = _states[index];
        if (follower == null || state == null)
        {
            return targetPosition;
        }

        Vector3 goal = targetPosition + GetFormationOffset(state);
        bool applyBoids = true;
        bool useFormationCohesionScale = true;

        switch (state.Mode)
        {
            case FollowerMode.Straying:
                goal = targetPosition + state.StrayOffset;
                useFormationCohesionScale = false;
                break;

            case FollowerMode.SeekingCamera:
                goal = GetCameraWatchPosition(follower.position);
                applyBoids = false;
                useFormationCohesionScale = false;
                break;

            case FollowerMode.WatchingCamera:
                goal = GetCameraMaintainPosition(follower.position);
                applyBoids = false;
                useFormationCohesionScale = false;
                break;

            case FollowerMode.EscapingCamera:
                goal = follower.position + GetAwayFromCameraDirection(follower.position) * escapeFleeDistance;
                applyBoids = false;
                useFormationCohesionScale = false;
                break;
        }

        if (applyBoids)
        {
            goal += ComputeBoidOffset(index, follower, useFormationCohesionScale);
        }

        return ApplySway(state, goal);
    }

    private Vector3 ApplySway(FollowerState state, Vector3 position)
    {
        if (swayAmplitude <= 0f || swaySpeed <= 0f)
        {
            return position;
        }

        position.y += Mathf.Sin(Time.time * swaySpeed + state.SwayPhase) * swayAmplitude;
        return position;
    }

    private Vector3 ComputeBoidOffset(int index, Transform follower, bool scaleCohesionForFormation)
    {
        Vector3 position = follower.position;
        Vector3 separation = Vector3.zero;
        Vector3 alignmentSum = Vector3.zero;
        Vector3 cohesionSum = Vector3.zero;
        int separationCount = 0;
        int alignmentCount = 0;
        int cohesionCount = 0;

        for (int j = 0; j < followers.Count; j++)
        {
            if (j == index)
            {
                continue;
            }

            Transform neighbor = followers[j];
            if (!IsFollowerActive(neighbor))
            {
                continue;
            }

            Vector3 toNeighbor = neighbor.position - position;
            float distSq = toNeighbor.sqrMagnitude;
            if (distSq > _neighborRadiusSq)
            {
                continue;
            }

            float dist = Mathf.Sqrt(distSq);

            if (dist > 0.0001f)
            {
                separation += -toNeighbor / dist;
                separationCount++;
            }

            alignmentSum += GetMovementDirection(j, neighbor);
            alignmentCount++;
            cohesionSum += neighbor.position;
            cohesionCount++;
        }

        Vector3 boidOffset = Vector3.zero;

        if (separationCount > 0 && separationWeight > 0f)
        {
            boidOffset += (separation / separationCount).normalized * separationWeight;
        }

        if (alignmentCount > 0 && alignmentWeight > 0f)
        {
            Vector3 averageDirection = (alignmentSum / alignmentCount).normalized;
            Vector3 selfDirection = GetMovementDirection(index, follower);
            if (averageDirection.sqrMagnitude > 0.0001f && selfDirection.sqrMagnitude > 0.0001f)
            {
                boidOffset += (averageDirection - selfDirection) * alignmentWeight;
            }
        }

        if (cohesionCount > 0 && cohesionWeight > 0f)
        {
            Vector3 center = cohesionSum / cohesionCount;
            Vector3 toCenter = center - position;
            if (toCenter.sqrMagnitude > 0.0001f)
            {
                float cohesionScale = scaleCohesionForFormation ? formationCohesionScale : 1f;
                boidOffset += toCenter.normalized * cohesionWeight * cohesionScale;
            }
        }

        if (maxBoidOffset > 0f && boidOffset.sqrMagnitude > maxBoidOffset * maxBoidOffset)
        {
            boidOffset = boidOffset.normalized * maxBoidOffset;
        }

        return boidOffset;
    }

    private Vector3 GetCameraWatchPosition(Vector3 followerPosition)
    {
        Vector3 fromCamera = followerPosition - _cameraTransform.position;
        fromCamera.y = 0f;
        if (fromCamera.sqrMagnitude < 0.01f)
        {
            fromCamera = -_cameraTransform.forward;
            fromCamera.y = 0f;
        }

        fromCamera.Normalize();
        Vector3 watchPoint = _cameraTransform.position + fromCamera * cameraWatchDistance;
        watchPoint.y = followerPosition.y;
        return watchPoint;
    }

    private Vector3 GetCameraMaintainPosition(Vector3 followerPosition)
    {
        Vector3 fromCamera = followerPosition - _cameraTransform.position;
        float distance = fromCamera.magnitude;
        if (distance < 0.01f)
        {
            fromCamera = -_cameraTransform.forward;
            distance = 1f;
        }

        Vector3 direction = fromCamera / distance;
        float idealDistance = cameraWatchDistance;
        if (distance < idealDistance * 0.92f || distance > idealDistance * 1.08f)
        {
            Vector3 adjusted = _cameraTransform.position + direction * idealDistance;
            adjusted.y = followerPosition.y;
            return adjusted;
        }

        return followerPosition;
    }

    private Vector3 GetAwayFromCameraDirection(Vector3 followerPosition)
    {
        Vector3 away = followerPosition - _cameraTransform.position;
        away.y = 0f;
        if (away.sqrMagnitude < 0.0001f)
        {
            away = _cameraTransform.forward;
            away.y = 0f;
        }

        return away.normalized;
    }

    private bool HasReachedCameraWatchPoint(Vector3 followerPosition)
    {
        Vector3 watchPoint = GetCameraWatchPosition(followerPosition);
        return Vector3.Distance(followerPosition, watchPoint) <= cameraArriveDistance;
    }

    private float GetCameraDistance(Vector3 followerPosition)
    {
        if (_cameraTransform == null)
        {
            return float.MaxValue;
        }

        return Vector3.Distance(followerPosition, _cameraTransform.position);
    }

    private float GetMaxSpeed(FollowerState state)
    {
        if (state.Mode == FollowerMode.EscapingCamera)
        {
            return escapeSpeed;
        }

        return state.MaxSpeed;
    }

    private void ApplyRotation(Transform follower, FollowerState state, Vector3 oldPosition)
    {
        if (state.Mode == FollowerMode.WatchingCamera && _cameraTransform != null && cameraLookTurnSpeed > 0f)
        {
            Vector3 toCamera = _cameraTransform.position - follower.position;
            toCamera.y = 0f;
            if (toCamera.sqrMagnitude > 0.0001f)
            {
                Quaternion lookRotation = Quaternion.LookRotation(toCamera.normalized, Vector3.up);
                follower.rotation = Quaternion.Slerp(
                    follower.rotation,
                    lookRotation,
                    Time.deltaTime * cameraLookTurnSpeed
                );
            }

            return;
        }

        Vector3 movement = follower.position - oldPosition;
        if (movement.sqrMagnitude > 0.00001f && turnSpeed > 0f)
        {
            Quaternion targetRotation = Quaternion.LookRotation(movement.normalized, Vector3.up);
            follower.rotation = Quaternion.Slerp(
                follower.rotation,
                targetRotation,
                Time.deltaTime * turnSpeed
            );
        }
    }

    private Vector3 GetMovementDirection(int index, Transform follower)
    {
        Vector3 velocity = _velocities[index];
        if (velocity.sqrMagnitude > 0.0001f)
        {
            return velocity.normalized;
        }

        return follower.forward;
    }

    private void CacheCamera()
    {
        if (_cameraTransform != null)
        {
            return;
        }

        Camera mainCamera = Camera.main;
        if (mainCamera != null)
        {
            _cameraTransform = mainCamera.transform;
        }
    }

    public void ActivateFollowersAndStartSwarm()
    {
        ActivateFollowersAndStartSwarm(3f);
    }

    public void ActivateFollowersAndStartSwarm(float activateStaggerSeconds)
    {
        if (_activateFollowersRoutine != null)
        {
            StopCoroutine(_activateFollowersRoutine);
        }

        _activateFollowersRoutine = StartCoroutine(
            ActivateFollowersStaggeredRoutine(activateStaggerSeconds)
        );
    }

    private IEnumerator ActivateFollowersStaggeredRoutine(float activateStaggerSeconds)
    {
        _swarmFollowingActive = true;
        ResetVelocities();
        InitializeFollowerStates();
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
            if (index >= 0 && index < _velocities.Count)
            {
                _velocities[index] = Vector3.zero;
            }
        }

        _activateFollowersRoutine = null;
    }

    private void EnsureBuffers()
    {
        while (_velocities.Count < followers.Count)
        {
            _velocities.Add(Vector3.zero);
        }

        if (_velocities.Count > followers.Count)
        {
            _velocities.RemoveRange(followers.Count, _velocities.Count - followers.Count);
        }

        while (_desiredPositions.Count < followers.Count)
        {
            _desiredPositions.Add(Vector3.zero);
        }

        if (_desiredPositions.Count > followers.Count)
        {
            _desiredPositions.RemoveRange(followers.Count, _desiredPositions.Count - followers.Count);
        }

        while (_states.Count < followers.Count)
        {
            _states.Add(CreateFollowerState(isCameraCurious: false));
        }

        if (_states.Count > followers.Count)
        {
            _states.RemoveRange(followers.Count, _states.Count - followers.Count);
        }
    }

    private void ResetVelocities()
    {
        EnsureBuffers();
        for (int i = 0; i < _velocities.Count; i++)
        {
            _velocities[i] = Vector3.zero;
        }
    }
}
