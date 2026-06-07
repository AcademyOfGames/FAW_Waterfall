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
        public float FixedScaleMultiplier;
        public Vector3 AxisStretch = Vector3.one;
        public Vector3 BaseLocalScale;
        public bool HasBaseScale;
        public Quaternion PrefabRotationOffset = Quaternion.identity;
        public bool HasPrefabRotation;
        public Material AssignedMaterial;
        public bool HasAssignedMaterial;
        public float EndShrinkStartTime = -1f;
        public float EndShrinkFromMultiplier = 1f;
        public float EncounterScaleStartTime = -1f;
        public float EncounterBaseScaleMultiplier = 1f;
        public bool EncounterFinished;
    }

    private const string Flora1MaterialPath = "Assets/alinaFlora1.mat";
    private const string Flora2MaterialPath = "Assets/alinaFlora2.mat";

    private const int PathSampleAccuracy = 12;
    private const float EncounterPathEndThreshold = 0.999f;
    private const float EncounterEndPopDuration = 0.25f;

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
    [Tooltip("Per-fish scale multiplier is chosen once at random between these values (stable on the path).")]
    [SerializeField] private float minFishScale = 3f;
    [Tooltip("Upper bound for the per-fish random scale multiplier.")]
    [SerializeField] private float maxFishScale = 5f;

    [Header("Materials")]
    [Tooltip("When on, each follower gets alinaFlora1 or alinaFlora2 at random once (spawned and plant fish).")]
    [SerializeField] private bool randomizeFollowerMaterials = true;
    [SerializeField] private Material flora1Material;
    [SerializeField] private Material flora2Material;

    [Header("Orientation")]
    [Tooltip("Which local axis points out of the fish nose in the source mesh/prefab. Default +X is an estimate for this fish setup.")]
    [SerializeField] private ModelForwardAxis modelForwardAxis = ModelForwardAxis.PositiveX;
    [Tooltip("When on, applies the follower prefab root Transform local rotation (edit rotation on fishPrefab in Prefab mode).")]
    [SerializeField] private bool applyFollowerPrefabRotation = true;
    [Tooltip("Extra Euler rotation after aligning to the path tangent and model-axis correction.")]
    [SerializeField] private Vector3 rotationOffsetEuler = Vector3.zero;

    [Header("Spawning")]
    [Tooltip("When off, only pre-assigned followers (e.g. plant fish) are used — no prefab spawn.")]
    [SerializeField] private bool allowPrefabSpawning = true;
    [Tooltip("Random stretch along the fish body axis (model forward) for spawned prefab fish only.")]
    [SerializeField] private float spawnedBodyStretchMin = 0.85f;
    [SerializeField] private float spawnedBodyStretchMax = 1.35f;
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
    private readonly HashSet<int> _pendingHomingIndices = new HashSet<int>();
    private bool _plantReleasePrepared;
    private bool _externallyControlled;
    private bool _encounterScaleAlongPath;
    private bool _encounterPreserveFishScale;
    private float _encounterMidScaleFactor = 0.5f;
    private float _encounterEndScaleFactor;
    private float _encounterScaleDurationSeconds;
    private float _encounterPathStartNormalizedT;
    private float _encounterLeadNormalizedT = -1f;
    private float _encounterConvoyFrontT;
    private bool _encounterConvoyFrontActive;
    private bool _isEncounterConvoy;
    private bool _isBridgeTransit;
    private int _encounterFollowersAddedCount;
    private readonly List<BridgeArrival> _bridgeArrivals = new List<BridgeArrival>();
    private bool _encounterHomingActive;
    private Coroutine _encounterHomingRoutine;
    private float _encounterHomingSpeed = 10f;
    private float _encounterJoinDistance = 0.75f;
    private float _encounterHomingStaggerSeconds = 0.06f;
    private float _encounterJoinTimeoutSeconds = 15f;
    private float _encounterHomingTurnSpeed = 8f;
    private bool _tailDissolveActive;
    private float _tailDissolvePopDuration = 0.25f;
    private float _tailDissolveInterval = 0.04f;
    private float _tailDissolveNextFishTime;
    private int _tailDissolveCurrentIndex = -1;

    public IReadOnlyList<Transform> Followers => followers;
    public BezierSpline Spline => spline;
    public bool IsExternallyControlled => _externallyControlled;
    public bool HasPendingPlantFish => _plantReleasePrepared && _pendingHomingIndices.Count > 0;
    public int FollowerCount => followers.Count;
    public bool IsSwarmRunning => _swarmRunning;
    public bool IsFollowerOnPath(int followerIndex) => IsValidFollowerIndex(followerIndex) && !_pendingHomingIndices.Contains(followerIndex);
    public bool IsAwaitingStart => _delayedStartRoutine != null;
    public bool HasSwarmStarted => _swarmStartedTime >= 0f;
    public float SwarmStartedTime => _swarmStartedTime;
    public bool IsEncounterConvoy => _isEncounterConvoy;
    public bool IsBridgeTransit => _isBridgeTransit;
    public float FishSpacing => fishSpacing;
    public float PathSpeed => pathSpeed;
    public float HeadGap => headGap;

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
        spawnedBodyStretchMin = Mathf.Max(0.01f, spawnedBodyStretchMin);
        spawnedBodyStretchMax = Mathf.Max(spawnedBodyStretchMin, spawnedBodyStretchMax);
        pathSpeed = Mathf.Max(0f, pathSpeed);
        maxFishCount = Mathf.Max(1, maxFishCount);
        spawnMinInterval = Mathf.Max(0f, spawnMinInterval);
        spawnMaxInterval = Mathf.Max(spawnMinInterval, spawnMaxInterval);
        activateDelaySeconds = Mathf.Max(0f, activateDelaySeconds);
        activateStaggerSeconds = Mathf.Max(0f, activateStaggerSeconds);
#if UNITY_EDITOR
        TryAssignDefaultFloraMaterialsInEditor();
#endif
    }

    private void Awake()
    {
        _movingForward = true;
    }

    private void Start()
    {
        if (_isEncounterConvoy)
        {
            return;
        }

        AssignTrailSlots();
        ScheduleNextSpawn();

        if (_pendingHomingIndices.Count > 0 || HasPlantFishReleaseController())
        {
            return;
        }

        if (activateOnPlay)
        {
            ScheduleAutoStart();
        }
        else
        {
            DeactivateAllFollowers();
        }
    }

    /// <summary>
    /// Registers plant-parented followers for homing. When hideFollowers is true they stay disabled until release.
    /// </summary>
    public void PreparePlantFishRelease(float plantScaleFactor = 1f, bool hideFollowers = false)
    {
        CancelDelayedStart();
        _plantReleasePrepared = true;
        _pendingHomingIndices.Clear();
        AssignTrailSlots();

        int preparedCount = 0;
        for (int i = 0; i < followers.Count; i++)
        {
            Transform follower = followers[i];
            if (follower == null)
            {
                continue;
            }

            follower.gameObject.SetActive(!hideFollowers);
            EnsureStateForIndex(i);
            CaptureFollowerBaseScale(follower, _states[i], forceRecapture: true);
            CaptureFollowerPrefabRotation(follower, _states[i], forceRecapture: true);
            ApplyRandomFollowerMaterial(follower, _states[i], forceRecapture: true);
            ApplyPlantRestScale(follower, _states[i], plantScaleFactor);
            _pendingHomingIndices.Add(i);
            preparedCount++;
        }

        if (preparedCount == 0)
        {
            Debug.LogWarning(
                "[VertexPathSwarmFollower] PreparePlantFishRelease: Followers list is empty or all null — assign plant fish transforms.",
                this);
        }
    }

    public void SetPendingPlantFollowersActive(bool active)
    {
        foreach (int index in _pendingHomingIndices)
        {
            if (!IsValidFollowerIndex(index))
            {
                continue;
            }

            Transform follower = followers[index];
            if (follower != null)
            {
                follower.gameObject.SetActive(active);
            }
        }
    }

    public void ApplyFollowerVisibleScale(int followerIndex)
    {
        if (!IsValidFollowerIndex(followerIndex) || _states[followerIndex] == null)
        {
            return;
        }

        ApplyFollowerVisibleScale(followers[followerIndex], _states[followerIndex]);
    }

    public void ApplyPlantRestScale(int followerIndex, float plantScaleFactor)
    {
        if (!IsValidFollowerIndex(followerIndex) || _states[followerIndex] == null)
        {
            return;
        }

        ApplyPlantRestScale(followers[followerIndex], _states[followerIndex], plantScaleFactor);
    }

    public void ApplyAscendScale(int followerIndex, float plantScaleFactor, float ascend01)
    {
        if (!IsValidFollowerIndex(followerIndex) || _states[followerIndex] == null)
        {
            return;
        }

        Transform follower = followers[followerIndex];
        FollowerState state = _states[followerIndex];
        if (follower == null || state == null)
        {
            return;
        }

        Vector3 plantScale = GetPlantRestScaleVector(state, plantScaleFactor);
        Vector3 convoyScale = GetConvoyScaleVector(state);
        float t = Mathf.SmoothStep(0f, 1f, Mathf.Clamp01(ascend01));
        follower.localScale = Vector3.Lerp(plantScale, convoyScale, t);
    }

    /// <summary>
    /// Starts path movement without activating hidden plant fish (they join via JoinFollowerOnPath).
    /// </summary>
    public void BeginSwarmForPlantRelease()
    {
        CancelDelayedStart();

        if (_activateFollowersRoutine != null)
        {
            StopCoroutine(_activateFollowersRoutine);
            _activateFollowersRoutine = null;
        }

        _externalScaleMultiplier = 1f;
        _swarmRunning = true;
        _swarmFollowingActive = true;
        _swarmStartedTime = Time.time;
        ScheduleNextSpawn();
        onSwarmStarted?.Invoke();
        SwarmStarted?.Invoke();
    }

    /// <summary>
    /// Starts encounter convoy motion without re-running AssignTrailSlots (Stage 4 handoff).
    /// </summary>
    public void BeginEncounterSwarmImmediate()
    {
        CancelDelayedStart();

        if (_activateFollowersRoutine != null)
        {
            StopCoroutine(_activateFollowersRoutine);
            _activateFollowersRoutine = null;
        }

        _externalScaleMultiplier = 1f;
        _swarmRunning = true;
        _swarmFollowingActive = true;
        _swarmStartedTime = Time.time;
        onSwarmStarted?.Invoke();
        SwarmStarted?.Invoke();
    }

    /// <summary>
    /// Stops the swarm and fully hides every follower (no shadows / renderers).
    /// </summary>
    public void ShutdownAndHideAllFollowers()
    {
        StopSwarmImmediate();

        for (int i = 0; i < followers.Count; i++)
        {
            HardHideFollower(followers[i]);
        }
    }

    public bool TryGetFollowerRevealScale(int followerIndex, out Vector3 scale)
    {
        scale = Vector3.one;
        if (!IsValidFollowerIndex(followerIndex) || _states[followerIndex] == null)
        {
            return false;
        }

        FollowerState state = _states[followerIndex];
        if (!state.HasBaseScale)
        {
            return false;
        }

        scale = GetConvoyScaleVector(state);
        return true;
    }

    public bool TryGetJoinWorldPose(int followerIndex, out Vector3 position, out Quaternion rotation)
    {
        position = Vector3.zero;
        rotation = Quaternion.identity;

        if (!IsValidFollowerIndex(followerIndex) || _states[followerIndex] == null || spline == null)
        {
            return false;
        }

        FollowerState state = _states[followerIndex];
        if (!SampleFishFrame(state, out Vector3 slotCenter, out Vector3 tangent, out Vector3 right, out Vector3 up))
        {
            return false;
        }

        position = ApplyTubeOffset(state, slotCenter, right, up);
        rotation = GetFollowerRotation(tangent, up, state);
        return true;
    }

    public void JoinFollowerOnPath(int followerIndex)
    {
        if (!IsValidFollowerIndex(followerIndex) || !_pendingHomingIndices.Contains(followerIndex))
        {
            return;
        }

        _pendingHomingIndices.Remove(followerIndex);
        Transform follower = followers[followerIndex];
        if (follower == null)
        {
            return;
        }

        follower.gameObject.SetActive(true);
        follower.SetParent(transform, true);
        ApplyEncounterConvoyTrailFromFront();
        SnapFollowerToSlot(followerIndex);
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
        if (HasPendingPlantFish)
        {
            return;
        }

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

    /// <summary>Stops spline following/spawning but leaves active fish visible for external motion (Stage 4).</summary>
    public void BeginExternalControl()
    {
        CancelDelayedStart();

        if (_activateFollowersRoutine != null)
        {
            StopCoroutine(_activateFollowersRoutine);
            _activateFollowersRoutine = null;
        }

        _swarmFollowingActive = false;
        _externallyControlled = true;
    }

    public void CollectActiveFollowers(List<Transform> results)
    {
        if (results == null)
        {
            return;
        }

        for (int i = 0; i < followers.Count; i++)
        {
            Transform follower = followers[i];
            if (follower == null || !follower.gameObject.activeSelf || _pendingHomingIndices.Contains(i))
            {
                continue;
            }

            if (i < _states.Count && _states[i] != null && _states[i].EncounterFinished)
            {
                continue;
            }

            results.Add(follower);
        }
    }

    /// <summary>Active followers sorted by convoy slot (lead first) for Stage 4 handoff.</summary>
    public void CollectActiveFollowersOrdered(List<Transform> results)
    {
        if (results == null)
        {
            return;
        }

        EnsureBuffers();

        var entries = new List<(int slot, int index, Transform follower)>();
        for (int i = 0; i < followers.Count; i++)
        {
            Transform follower = followers[i];
            if (follower == null || !follower.gameObject.activeSelf || _pendingHomingIndices.Contains(i))
            {
                continue;
            }

            int slot = i < _states.Count && _states[i] != null ? _states[i].SlotIndex : i;
            entries.Add((slot, i, follower));
        }

        entries.Sort((a, b) =>
        {
            int slotCompare = a.slot.CompareTo(b.slot);
            return slotCompare != 0 ? slotCompare : a.index.CompareTo(b.index);
        });

        for (int i = 0; i < entries.Count; i++)
        {
            results.Add(entries[i].follower);
        }
    }

    /// <summary>Lead convoy fish still on this swarm (slot 0 first).</summary>
    public bool TryCollectLeadActiveFollower(HashSet<Transform> exclude, out Transform lead)
    {
        lead = null;
        var ordered = new List<Transform>();
        CollectActiveFollowersOrdered(ordered);
        for (int i = 0; i < ordered.Count; i++)
        {
            Transform follower = ordered[i];
            if (follower == null || (exclude != null && exclude.Contains(follower)))
            {
                continue;
            }

            lead = follower;
            return true;
        }

        return false;
    }

    /// <summary>Visual/state data preserved when a fish moves from Stage 3 onto Stage 4.</summary>
    public struct FollowerTransferSnapshot
    {
        public float TubeAngle;
        public float TubeRadius;
        public float WobblePhase;
        public float WobbleSpeed;
        public float FixedScaleMultiplier;
        public Vector3 AxisStretch;
        public Vector3 BaseLocalScale;
        public bool HasBaseScale;
        public Quaternion PrefabRotationOffset;
        public bool HasPrefabRotation;
        public Material AssignedMaterial;
        public bool HasAssignedMaterial;
    }

    public struct Stage4HandoffEntry
    {
        public Transform Follower;
        public FollowerTransferSnapshot Snapshot;
        public float SourceNormalizedT;
        public int SlotIndex;
        public float CombinedNormalizedT;
    }

    public struct BridgeArrival
    {
        public Transform Follower;
        public FollowerTransferSnapshot Snapshot;
    }

    public bool TryGetFollowerWorldPose(Transform follower, out Vector3 position, out Quaternion rotation)
    {
        position = Vector3.zero;
        rotation = Quaternion.identity;
        if (follower == null)
        {
            return false;
        }

        for (int i = 0; i < followers.Count; i++)
        {
            if (followers[i] != follower || i >= _states.Count || _states[i] == null)
            {
                continue;
            }

            if (!SampleFishFrame(_states[i], out Vector3 slotCenter, out Vector3 tangent, out Vector3 right, out Vector3 up))
            {
                return false;
            }

            position = ApplyTubeOffset(_states[i], slotCenter, right, up);
            rotation = GetFollowerRotation(tangent, up, _states[i]);
            return true;
        }

        return false;
    }

    public bool TryGetFollowerPathProgress(Transform follower, out float normalizedT)
    {
        normalizedT = 0f;
        if (follower == null)
        {
            return false;
        }

        for (int i = 0; i < followers.Count; i++)
        {
            if (followers[i] != follower || i >= _states.Count || _states[i] == null)
            {
                continue;
            }

            normalizedT = _states[i].NormalizedT;
            return true;
        }

        return false;
    }

    /// <summary>Copies path speed and spacing so a peeled convoy keeps the same train rhythm.</summary>
    public void SyncConvoyMotionFrom(VertexPathSwarmFollower source)
    {
        if (source == null)
        {
            return;
        }

        pathSpeed = Mathf.Max(0.1f, source.PathSpeed);
        fishSpacing = Mathf.Max(0.05f, source.FishSpacing);
        headGap = Mathf.Max(0f, source.HeadGap);
    }

    /// <summary>Removes a follower and returns preserved state for Stage 4 handoff.</summary>
    public bool TryExtractFollower(Transform follower, out FollowerTransferSnapshot snapshot)
    {
        snapshot = default;
        if (follower == null)
        {
            return false;
        }

        for (int i = 0; i < followers.Count; i++)
        {
            if (followers[i] != follower)
            {
                continue;
            }

            FollowerState state = i < _states.Count ? _states[i] : null;
            if (state == null)
            {
                return false;
            }

            snapshot = CreateTransferSnapshot(state);
            followers.RemoveAt(i);
            if (i < _states.Count)
            {
                _states.RemoveAt(i);
            }

            RebuildPendingHomingIndices();
            return true;
        }

        return false;
    }

    private static FollowerTransferSnapshot CreateTransferSnapshot(FollowerState state)
    {
        return new FollowerTransferSnapshot
        {
            TubeAngle = state.TubeAngle,
            TubeRadius = state.TubeRadius,
            WobblePhase = state.WobblePhase,
            WobbleSpeed = state.WobbleSpeed,
            FixedScaleMultiplier = state.FixedScaleMultiplier,
            AxisStretch = state.AxisStretch,
            BaseLocalScale = state.BaseLocalScale,
            HasBaseScale = state.HasBaseScale,
            PrefabRotationOffset = state.PrefabRotationOffset,
            HasPrefabRotation = state.HasPrefabRotation,
            AssignedMaterial = state.AssignedMaterial,
            HasAssignedMaterial = state.HasAssignedMaterial
        };
    }

    private static void ApplyTransferSnapshot(FollowerState state, FollowerTransferSnapshot snapshot)
    {
        state.TubeAngle = snapshot.TubeAngle;
        state.TubeRadius = snapshot.TubeRadius;
        state.WobblePhase = snapshot.WobblePhase;
        state.WobbleSpeed = snapshot.WobbleSpeed;
        state.FixedScaleMultiplier = snapshot.FixedScaleMultiplier;
        state.AxisStretch = snapshot.AxisStretch;
        state.BaseLocalScale = snapshot.BaseLocalScale;
        state.HasBaseScale = snapshot.HasBaseScale;
        state.PrefabRotationOffset = snapshot.PrefabRotationOffset;
        state.HasPrefabRotation = snapshot.HasPrefabRotation;
        state.AssignedMaterial = snapshot.AssignedMaterial;
        state.HasAssignedMaterial = snapshot.HasAssignedMaterial;
    }

    /// <summary>
    /// Prepares Stage 4 convoy: normal per-fish conveyor motion + disappear at path end (no peel/homing/front-T).
    /// </summary>
    public void BeginStage4Convoy(
        BezierSpline stage4Spline,
        float pathMidScaleFactor = 0.5f,
        float pathEndScaleFactor = 0f,
        float encounterPathStartNormalizedT = 0f,
        bool preserveFishScale = false,
        float encounterScaleDurationSeconds = -1f)
    {
        CancelDelayedStart();
        StopEncounterHoming();

        if (_activateFollowersRoutine != null)
        {
            StopCoroutine(_activateFollowersRoutine);
            _activateFollowersRoutine = null;
        }

        _swarmRunning = false;
        _swarmFollowingActive = false;
        _externallyControlled = false;
        _plantReleasePrepared = false;
        _pendingHomingIndices.Clear();
        _externalScaleMultiplier = 1f;
        _encounterPreserveFishScale = preserveFishScale;
        _encounterScaleAlongPath = !preserveFishScale && encounterScaleDurationSeconds <= 0f;
        _encounterMidScaleFactor = Mathf.Clamp(pathMidScaleFactor, 0.01f, 1f);
        _encounterEndScaleFactor = Mathf.Max(0f, pathEndScaleFactor);
        _encounterScaleDurationSeconds = preserveFishScale
            ? 0f
            : Mathf.Max(0f, encounterScaleDurationSeconds);
        _encounterPathStartNormalizedT = Mathf.Clamp01(encounterPathStartNormalizedT);
        _encounterLeadNormalizedT = -1f;
        _encounterConvoyFrontActive = false;
        _isEncounterConvoy = true;
        _isBridgeTransit = false;
        _encounterFollowersAddedCount = 0;
        _movingForward = true;

        if (stage4Spline != null)
        {
            spline = stage4Spline;
        }

        travelMode = TravelMode.Once;
        allowPrefabSpawning = false;
        activateOnPlay = false;

        followers.Clear();
        _states.Clear();
        _nextSlotIndex = 0;
    }

    /// <summary>Conveyor on a bridge spline only — fish dequeue when they reach the end.</summary>
    public void BeginBridgeTransit(BezierSpline bridgeSpline, float speed, float spacing)
    {
        CancelDelayedStart();
        StopEncounterHoming();

        if (_activateFollowersRoutine != null)
        {
            StopCoroutine(_activateFollowersRoutine);
            _activateFollowersRoutine = null;
        }

        _isBridgeTransit = true;
        _isEncounterConvoy = false;
        _encounterConvoyFrontActive = false;
        _swarmRunning = true;
        _swarmFollowingActive = true;
        _swarmStartedTime = Time.time;
        _externallyControlled = false;
        _plantReleasePrepared = false;
        _pendingHomingIndices.Clear();
        _bridgeArrivals.Clear();
        _movingForward = true;
        travelMode = TravelMode.Once;
        allowPrefabSpawning = false;
        activateOnPlay = false;

        if (bridgeSpline != null)
        {
            spline = bridgeSpline;
        }

        pathSpeed = Mathf.Max(0.1f, speed);
        fishSpacing = Mathf.Max(0.05f, spacing);
        followers.Clear();
        _states.Clear();
        _nextSlotIndex = 0;
    }

    public bool HasBridgeFollowersInTransit()
    {
        return _isBridgeTransit && followers.Count > 0;
    }

    public bool HasPendingBridgeArrivals => _isBridgeTransit && _bridgeArrivals.Count > 0;

    public bool TryDequeueBridgeArrival(out BridgeArrival arrival)
    {
        arrival = default;
        if (_bridgeArrivals.Count == 0)
        {
            return false;
        }

        arrival = _bridgeArrivals[0];
        _bridgeArrivals.RemoveAt(0);
        return arrival.Follower != null;
    }

    public bool TryAddBridgeFollower(Transform follower, FollowerTransferSnapshot snapshot)
    {
        if (!_isBridgeTransit || follower == null || spline == null)
        {
            return false;
        }

        follower.SetParent(transform, true);
        EnsureFollowerVisible(follower);

        followers.Add(follower);

        FollowerState state = CreateEncounterFollowerState(_nextSlotIndex++);
        ApplyTransferSnapshot(state, snapshot);
        state.NormalizedT = ResolveBridgeEntryNormalizedT();
        _states.Add(state);
        CaptureFollowerBaseScale(follower, state, forceRecapture: false);
        CaptureFollowerPrefabRotation(follower, state, forceRecapture: false);

        if (!SampleFishFrame(state, out Vector3 slotCenter, out Vector3 tangent, out Vector3 right, out Vector3 up))
        {
            RemoveBridgeFollowerAt(followers.Count - 1);
            return false;
        }

        follower.position = ApplyTubeOffset(state, slotCenter, right, up);
        follower.rotation = GetFollowerRotation(tangent, up, state);
        ApplyFishScale(follower, state);
        return true;
    }

    private void RemoveBridgeFollowerAt(int removeIndex)
    {
        if (removeIndex < 0 || removeIndex >= followers.Count)
        {
            return;
        }

        followers.RemoveAt(removeIndex);
        if (removeIndex < _states.Count)
        {
            _states.RemoveAt(removeIndex);
        }
    }

    /// <summary>Moves every fish still on the bridge into the arrival queue (encounter shutdown).</summary>
    public void ForceFlushAllBridgeFollowersToArrivals()
    {
        if (!_isBridgeTransit)
        {
            return;
        }

        for (int i = followers.Count - 1; i >= 0; i--)
        {
            Transform follower = followers[i];
            FollowerState state = i < _states.Count ? _states[i] : null;
            if (follower == null || state == null)
            {
                RemoveBridgeFollowerAt(i);
                continue;
            }

            _bridgeArrivals.Add(new BridgeArrival
            {
                Follower = follower,
                Snapshot = CreateTransferSnapshot(state)
            });
            RemoveBridgeFollowerAt(i);
        }
    }

    private float ResolveBridgeEntryNormalizedT()
    {
        if (spline == null || followers.Count <= 1)
        {
            return 0f;
        }

        float minT = float.MaxValue;
        for (int i = 0; i < followers.Count - 1; i++)
        {
            if (i >= _states.Count || _states[i] == null)
            {
                continue;
            }

            minT = Mathf.Min(minT, _states[i].NormalizedT);
        }

        if (minT >= float.MaxValue)
        {
            return 0f;
        }

        float trailT = minT;
        float delta = _movingForward ? -fishSpacing : fishSpacing;
        spline.MoveAlongSpline(ref trailT, delta, PathSampleAccuracy);
        return Mathf.Clamp01(trailT);
    }

    private void ProcessBridgeTransitArrivals()
    {
        for (int i = followers.Count - 1; i >= 0; i--)
        {
            Transform follower = followers[i];
            FollowerState state = i < _states.Count ? _states[i] : null;
            if (!IsFollowerActive(follower) || state == null)
            {
                continue;
            }

            if (state.NormalizedT < EncounterPathEndThreshold)
            {
                continue;
            }

            _bridgeArrivals.Add(new BridgeArrival
            {
                Follower = follower,
                Snapshot = CreateTransferSnapshot(state)
            });

            followers.RemoveAt(i);
            if (i < _states.Count)
            {
                _states.RemoveAt(i);
            }
        }
    }

    public void StopBridgeTransit()
    {
        _isBridgeTransit = false;
        _swarmRunning = false;
        _swarmFollowingActive = false;
    }

    public void ConfigureEncounterHoming(
        float homingSpeed,
        float joinDistance,
        float joinTimeoutSeconds,
        float homingTurnSpeed)
    {
        _encounterHomingSpeed = Mathf.Max(0.1f, homingSpeed);
        _encounterJoinDistance = Mathf.Max(0.01f, joinDistance);
        _encounterJoinTimeoutSeconds = Mathf.Max(0.5f, joinTimeoutSeconds);
        _encounterHomingTurnSpeed = Mathf.Max(0f, homingTurnSpeed);
    }

    /// <summary>Homes a fish onto the real Stage 4 spline (junction or convoy tail).</summary>
    public void AcceptStage4FollowerWithHoming(
        Transform follower,
        FollowerTransferSnapshot snapshot,
        bool appendBehindConvoy)
    {
        if (!_isEncounterConvoy || follower == null || spline == null)
        {
            return;
        }

        follower.SetParent(transform, true);
        EnsureFollowerVisible(follower);

        int index = followers.Count;
        followers.Add(follower);

        FollowerState state = CreateEncounterFollowerState(_nextSlotIndex++);
        ApplyTransferSnapshot(state, snapshot);
        state.NormalizedT = ResolveStage4JoinNormalizedT(_encounterPathStartNormalizedT, appendBehindConvoy);
        _states.Add(state);
        CaptureFollowerBaseScale(follower, state, forceRecapture: false);
        CaptureFollowerPrefabRotation(follower, state, forceRecapture: false);
        _encounterFollowersAddedCount++;
        BeginEncounterScaleForFish(state);

        _encounterHomingActive = true;
        _pendingHomingIndices.Add(index);

        if (!_swarmRunning)
        {
            BeginEncounterSwarmImmediate();
        }

        StartCoroutine(HomingEncounterFishRoutine(index));
    }

    /// <summary>Snaps a fish onto Stage 4 conveyor slots (used after bridge exit at the junction).</summary>
    public void AcceptStage4FollowerDirectJoin(
        Transform follower,
        FollowerTransferSnapshot snapshot,
        bool appendBehindConvoy)
    {
        if (!_isEncounterConvoy || follower == null || spline == null)
        {
            return;
        }

        follower.SetParent(transform, true);
        EnsureFollowerVisible(follower);

        int index = followers.Count;
        followers.Add(follower);

        FollowerState state = CreateEncounterFollowerState(_nextSlotIndex++);
        ApplyTransferSnapshot(state, snapshot);
        state.NormalizedT = ResolveStage4JoinNormalizedT(_encounterPathStartNormalizedT, appendBehindConvoy);
        _states.Add(state);
        CaptureFollowerBaseScale(follower, state, forceRecapture: false);
        CaptureFollowerPrefabRotation(follower, state, forceRecapture: false);
        _encounterFollowersAddedCount++;
        BeginEncounterScaleForFish(state);

        if (!_swarmRunning)
        {
            BeginEncounterSwarmImmediate();
        }

        SnapFollowerToSlot(index);
        ApplyFishScale(follower, state);
    }

    private float ResolveStage4JoinNormalizedT(float junctionNormalizedT, bool appendBehindConvoy)
    {
        float junctionT = Mathf.Clamp01(junctionNormalizedT);
        if (!appendBehindConvoy || _encounterFollowersAddedCount <= 0)
        {
            return junctionT;
        }

        float rearmostT = GetRearmostEncounterNormalizedT(includePendingHoming: true);
        if (rearmostT < 0f)
        {
            return junctionT;
        }

        float trailT = rearmostT;
        float delta = _movingForward ? -fishSpacing : fishSpacing;
        spline.MoveAlongSpline(ref trailT, delta, PathSampleAccuracy);
        return ClampEncounterJoinNormalizedT(trailT);
    }

    private float ClampEncounterJoinNormalizedT(float normalizedT)
    {
        float minT = _isEncounterConvoy ? _encounterPathStartNormalizedT : 0f;
        return Mathf.Clamp(Mathf.Clamp01(normalizedT), minT, 1f);
    }

    /// <summary>Copies active followers in convoy order for a one-shot Stage 3 → combined path handoff.</summary>
    public void GatherStage4HandoffEntries(List<Stage4HandoffEntry> results)
    {
        if (results == null)
        {
            return;
        }

        results.Clear();
        var ordered = new List<Transform>();
        CollectActiveFollowersOrdered(ordered);

        for (int i = 0; i < ordered.Count; i++)
        {
            Transform follower = ordered[i];
            if (follower == null)
            {
                continue;
            }

            for (int index = 0; index < followers.Count; index++)
            {
                if (followers[index] != follower || index >= _states.Count || _states[index] == null)
                {
                    continue;
                }

                FollowerState state = _states[index];
                results.Add(new Stage4HandoffEntry
                {
                    Follower = follower,
                    Snapshot = CreateTransferSnapshot(state),
                    SourceNormalizedT = state.NormalizedT,
                    SlotIndex = state.SlotIndex,
                    CombinedNormalizedT = state.NormalizedT
                });
                break;
            }
        }
    }

    public void ClearAllFollowersKeepTransforms()
    {
        followers.Clear();
        _states.Clear();
        _nextSlotIndex = 0;
        _pendingHomingIndices.Clear();
    }

    /// <summary>
    /// All fish on the combined path using each fish's mapped T — no SnapFollowerToSlot teleport.
    /// </summary>
    public void BeginCombinedStage4ConvoyPreservePositions(
        BezierSpline combinedSpline,
        IReadOnlyList<Stage4HandoffEntry> orderedFish,
        float pathMidScaleFactor = 0.5f,
        float pathEndScaleFactor = 0f,
        float encounterPathStartNormalizedT = 0f,
        bool preserveFishScale = true)
    {
        BeginStage4Convoy(
            combinedSpline,
            pathMidScaleFactor,
            pathEndScaleFactor,
            encounterPathStartNormalizedT,
            preserveFishScale);

        if (orderedFish == null || orderedFish.Count == 0)
        {
            return;
        }

        for (int i = 0; i < orderedFish.Count; i++)
        {
            Stage4HandoffEntry entry = orderedFish[i];
            Transform follower = entry.Follower;
            if (follower == null)
            {
                continue;
            }

            EnsureFollowerVisible(follower);
            follower.SetParent(transform, true);

            followers.Add(follower);

            FollowerState state = CreateEncounterFollowerState(_nextSlotIndex++);
            ApplyTransferSnapshot(state, snapshot: entry.Snapshot);
            state.NormalizedT = Mathf.Clamp01(entry.CombinedNormalizedT);
            _states.Add(state);
            CaptureFollowerBaseScale(follower, state, forceRecapture: false);
            CaptureFollowerPrefabRotation(follower, state, forceRecapture: false);
            _encounterFollowersAddedCount++;
            ApplyFishScale(follower, state);
        }

        BeginEncounterSwarmImmediate();
    }

    /// <summary>
    /// All fish at once on the combined Stage 3→bridge→Stage 4 spline (normal conveyor + end disappear).
    /// </summary>
    public void BeginCombinedStage4Convoy(
        BezierSpline combinedSpline,
        IReadOnlyList<Stage4HandoffEntry> orderedFish,
        float pathMidScaleFactor = 0.5f,
        float pathEndScaleFactor = 0f)
    {
        BeginStage4Convoy(combinedSpline, pathMidScaleFactor, pathEndScaleFactor);

        if (orderedFish == null || orderedFish.Count == 0)
        {
            return;
        }

        for (int i = 0; i < orderedFish.Count; i++)
        {
            Stage4HandoffEntry entry = orderedFish[i];
            Transform follower = entry.Follower;
            if (follower == null)
            {
                continue;
            }

            follower.SetParent(transform, true);
            follower.gameObject.SetActive(true);

            int index = followers.Count;
            followers.Add(follower);

            FollowerState state = CreateFollowerState(_nextSlotIndex++);
            ApplyTransferSnapshot(state, entry.Snapshot);
            state.NormalizedT = Mathf.Clamp01(entry.CombinedNormalizedT);
            _states.Add(state);
            CaptureFollowerBaseScale(follower, state, forceRecapture: false);
            CaptureFollowerPrefabRotation(follower, state, forceRecapture: false);
            _encounterFollowersAddedCount++;
        }

        BeginEncounterSwarmImmediate();

        for (int i = 0; i < followers.Count; i++)
        {
            SnapFollowerToSlot(i);
            ApplyFishScale(followers[i], _states[i]);
        }
    }

    public static void AssignCombinedTrailPositions(
        BezierSpline combinedSpline,
        List<Stage4HandoffEntry> orderedFish,
        float stage3JoinNormalizedT,
        float combinedStage3SectionEndT,
        float spacing,
        bool movingForward)
    {
        if (combinedSpline == null || orderedFish == null || orderedFish.Count == 0)
        {
            return;
        }

        const int accuracy = 12;

        Stage4HandoffEntry lead = orderedFish[0];
        float leadCombinedT = Stage4CombinedPathBuilder.MapStage3NormalizedTToCombined(
            lead.SourceNormalizedT,
            stage3JoinNormalizedT,
            combinedStage3SectionEndT);

        lead.CombinedNormalizedT = leadCombinedT;

        for (int i = 1; i < orderedFish.Count; i++)
        {
            float trailT = orderedFish[i - 1].CombinedNormalizedT;
            float delta = movingForward ? -spacing : spacing;
            combinedSpline.MoveAlongSpline(ref trailT, delta, accuracy);
            Stage4HandoffEntry entry = orderedFish[i];
            entry.CombinedNormalizedT = Mathf.Clamp01(trailT);
            orderedFish[i] = entry;
        }
    }

    /// <summary>World pose for a slot on this convoy (used while blending onto Stage 4).</summary>
    public bool TryGetSlotJoinPose(int slotIndex, out Vector3 position, out Quaternion rotation)
    {
        position = Vector3.zero;
        rotation = Quaternion.identity;

        if (spline == null || slotIndex < 0)
        {
            return false;
        }

        var tempState = new FollowerState
        {
            SlotIndex = slotIndex,
            NormalizedT = ComputeNormalizedTForNewSlot(slotIndex),
            TubeAngle = 0f,
            TubeRadius = 0f
        };

        if (!SampleFishFrame(tempState, out Vector3 slotCenter, out Vector3 tangent, out Vector3 right, out Vector3 up))
        {
            return false;
        }

        position = slotCenter;
        rotation = GetFollowerRotation(tangent, up, tempState);
        return true;
    }

    /// <summary>
    /// Transfers one fish onto Stage 4 at the junction, keeping world pose (no snap teleport).
    /// </summary>
    public void AcceptStage4FollowerPreservePose(
        Transform follower,
        FollowerTransferSnapshot snapshot,
        float junctionNormalizedT)
    {
        if (!_isEncounterConvoy || follower == null || spline == null)
        {
            return;
        }

        follower.SetParent(transform, true);
        follower.gameObject.SetActive(true);

        followers.Add(follower);

        FollowerState state = CreateEncounterFollowerState(_nextSlotIndex++);
        ApplyTransferSnapshot(state, snapshot);

        float junctionT = Mathf.Clamp01(junctionNormalizedT);
        float entryT = ResolveStage4EntryNormalizedT(junctionT);
        state.NormalizedT = entryT;

        _states.Add(state);
        CaptureFollowerBaseScale(follower, state, forceRecapture: false);
        CaptureFollowerPrefabRotation(follower, state, forceRecapture: false);
        _encounterFollowersAddedCount++;
        BeginEncounterScaleForFish(state);

        if (!_swarmRunning)
        {
            BeginEncounterSwarmImmediate();
        }

        ApplyFishScale(follower, state);
    }

    private void BeginEncounterScaleForFish(FollowerState state)
    {
        if (state == null || _encounterPreserveFishScale || _encounterScaleDurationSeconds <= 0f)
        {
            return;
        }

        state.EncounterBaseScaleMultiplier = GetFishScaleMultiplier(state);
        state.EncounterScaleStartTime = Time.time;
    }

    /// <summary>Lowest active T on the encounter convoy (rearmost when moving forward).</summary>
    public float GetRearmostEncounterNormalizedT(bool includePendingHoming = false)
    {
        float minT = float.MaxValue;
        bool found = false;

        for (int i = 0; i < followers.Count; i++)
        {
            if (!includePendingHoming && _pendingHomingIndices.Contains(i))
            {
                continue;
            }

            Transform follower = followers[i];
            FollowerState candidate = i < _states.Count ? _states[i] : null;
            if (!IsFollowerActive(follower) || candidate == null || candidate.EncounterFinished)
            {
                continue;
            }

            found = true;
            minT = Mathf.Min(minT, candidate.NormalizedT);
        }

        return found ? minT : -1f;
    }

    private float ResolveStage4EntryNormalizedT(float junctionNormalizedT)
    {
        float junctionT = Mathf.Clamp01(junctionNormalizedT);
        if (spline == null || _encounterFollowersAddedCount <= 0)
        {
            return junctionT;
        }

        float rearmostT = GetRearmostEncounterNormalizedT();
        if (rearmostT < 0f)
        {
            return junctionT;
        }

        float trailT = rearmostT;
        float delta = _movingForward ? -fishSpacing : fishSpacing;
        spline.MoveAlongSpline(ref trailT, delta, PathSampleAccuracy);
        return ClampEncounterJoinNormalizedT(trailT);
    }

    /// <summary>Adds a fish that finished blending onto Stage 4 and starts convoy motion on first fish.</summary>
    public void AcceptStage4Follower(Transform follower, FollowerTransferSnapshot snapshot)
    {
        if (!_isEncounterConvoy || follower == null || spline == null)
        {
            return;
        }

        follower.SetParent(transform, true);
        follower.gameObject.SetActive(true);

        int index = followers.Count;
        followers.Add(follower);

        FollowerState state = CreateFollowerState(_nextSlotIndex++);
        ApplyTransferSnapshot(state, snapshot);
        _states.Add(state);
        CaptureFollowerBaseScale(follower, state, forceRecapture: false);
        CaptureFollowerPrefabRotation(follower, state, forceRecapture: false);
        _encounterFollowersAddedCount++;
        BeginEncounterScaleForFish(state);

        if (!_swarmRunning)
        {
            BeginEncounterSwarmImmediate();
        }

        SnapFollowerToSlot(index);
        ApplyFishScale(follower, state);
    }

    public bool HasActiveFollowers()
    {
        var active = new List<Transform>();
        CollectActiveFollowers(active);
        return active.Count > 0;
    }

    /// <summary>
    /// Shrinks and hides rearmost convoy fish one-by-one (Group B tail during Stage 4 wind-down).
    /// </summary>
    public void BeginTailDissolve(float popDurationSeconds, float intervalBetweenFishSeconds)
    {
        _tailDissolveActive = true;
        _tailDissolvePopDuration = Mathf.Max(0.05f, popDurationSeconds);
        _tailDissolveInterval = Mathf.Max(0f, intervalBetweenFishSeconds);
        _tailDissolveNextFishTime = Time.time;
        _tailDissolveCurrentIndex = -1;
    }

    public void StopTailDissolve()
    {
        _tailDissolveActive = false;
        _tailDissolveCurrentIndex = -1;
    }

    public bool IsTailDissolveActive => _tailDissolveActive;

    /// <summary>
    /// Removes only the given followers from this swarm so the rest keep following the path (Stage 4 peel-off).
    /// </summary>
    public void ReleaseFollowersForEncounter(IReadOnlyCollection<Transform> toRelease)
    {
        if (toRelease == null || toRelease.Count == 0)
        {
            return;
        }

        var releaseSet = toRelease as HashSet<Transform> ?? new HashSet<Transform>(toRelease);

        for (int i = followers.Count - 1; i >= 0; i--)
        {
            Transform follower = followers[i];
            if (follower == null || !releaseSet.Contains(follower))
            {
                continue;
            }

            followers.RemoveAt(i);
            if (i < _states.Count)
            {
                _states.RemoveAt(i);
            }
        }

        RebuildPendingHomingIndices();
    }

    private void RebuildPendingHomingIndices()
    {
        if (_pendingHomingIndices.Count == 0)
        {
            return;
        }

        var rebuilt = new HashSet<int>();
        foreach (int oldIndex in _pendingHomingIndices)
        {
            if (oldIndex >= 0 && oldIndex < followers.Count)
            {
                rebuilt.Add(oldIndex);
            }
        }

        _pendingHomingIndices.Clear();
        foreach (int index in rebuilt)
        {
            _pendingHomingIndices.Add(index);
        }
    }

    /// <summary>Links the follower to the encounter spline without overwriting inspector tuning.</summary>
    public void WireEncounterConvoy(BezierSpline encounterSpline)
    {
        if (encounterSpline != null)
        {
            spline = encounterSpline;
        }

        travelMode = TravelMode.Once;
        allowPrefabSpawning = false;
        activateOnPlay = false;

        if (Application.isPlaying)
        {
            _isEncounterConvoy = true;
        }
    }

    /// <summary>Copies numeric convoy settings from ViewerSpiralSplineRig Convoy Defaults.</summary>
    public void ApplyEncounterConvoyDefaults(
        float encounterPathSpeed,
        float encounterHeadGap,
        float encounterFishSpacing,
        float encounterTubeRadius)
    {
        pathSpeed = Mathf.Max(0.1f, encounterPathSpeed);
        headGap = Mathf.Max(0f, encounterHeadGap);
        fishSpacing = Mathf.Max(0.05f, encounterFishSpacing);
        tubeRadius = Mathf.Max(0f, encounterTubeRadius);
    }

    public void ConfigureForEncounterConvoy(
        BezierSpline encounterSpline,
        float encounterPathSpeed,
        float encounterHeadGap,
        float encounterFishSpacing,
        float encounterTubeRadius)
    {
        WireEncounterConvoy(encounterSpline);
        ApplyEncounterConvoyDefaults(
            encounterPathSpeed,
            encounterHeadGap,
            encounterFishSpacing,
            encounterTubeRadius);

        if (Application.isPlaying)
        {
            _isEncounterConvoy = true;
        }
    }

    public void BeginEncounterConvoy(
        BezierSpline encounterSpline,
        float pathMidScaleFactor = 0.5f,
        float pathEndScaleFactor = 0f,
        float pathSpeedOverride = -1f,
        float homingSpeed = 10f,
        float joinDistance = 0.75f,
        float joinTimeoutSeconds = 15f,
        float homingTurnSpeed = 8f)
    {
        CancelDelayedStart();
        StopEncounterHoming();

        if (_activateFollowersRoutine != null)
        {
            StopCoroutine(_activateFollowersRoutine);
            _activateFollowersRoutine = null;
        }

        _swarmRunning = false;
        _swarmFollowingActive = false;
        _externallyControlled = false;
        _plantReleasePrepared = false;
        _pendingHomingIndices.Clear();
        _externalScaleMultiplier = 1f;
        _encounterScaleAlongPath = true;
        _encounterMidScaleFactor = Mathf.Clamp(pathMidScaleFactor, 0.01f, 1f);
        _encounterEndScaleFactor = Mathf.Max(0f, pathEndScaleFactor);
        _encounterLeadNormalizedT = -1f;
        _encounterConvoyFrontT = startNormalizedT;
        _encounterConvoyFrontActive = true;
        _isEncounterConvoy = true;
        _encounterFollowersAddedCount = 0;
        _encounterHomingSpeed = Mathf.Max(0.1f, homingSpeed);
        _encounterJoinDistance = Mathf.Max(0.01f, joinDistance);
        _encounterJoinTimeoutSeconds = Mathf.Max(0.5f, joinTimeoutSeconds);
        _encounterHomingTurnSpeed = Mathf.Max(0f, homingTurnSpeed);

        WireEncounterConvoy(encounterSpline);
        _isEncounterConvoy = true;
        _movingForward = true;

        if (pathSpeedOverride > 0f)
        {
            pathSpeed = pathSpeedOverride;
        }

        followers.Clear();
        _states.Clear();
        _nextSlotIndex = 0;

        _encounterHomingActive = true;
        BeginEncounterSwarmImmediate();
    }

    /// <summary>Adds one fish to a running Stage 4 encounter convoy and homes it onto the path.</summary>
    public void AddEncounterFollower(Transform follower)
    {
        if (!_isEncounterConvoy || follower == null || spline == null)
        {
            return;
        }

        int index = followers.Count;
        followers.Add(follower);
        FollowerState state = CreateEncounterFollowerState(_nextSlotIndex++);
        _states.Add(state);
        CaptureFollowerBaseScale(follower, state, forceRecapture: true);
        CaptureFollowerPrefabRotation(follower, state, forceRecapture: true);
        ActivateEncounterFollowerAt(index);
        _pendingHomingIndices.Add(index);
        _encounterFollowersAddedCount++;
        BeginEncounterScaleForFish(state);
        ApplyEncounterConvoyTrailFromFront();
        StartCoroutine(HomingEncounterFishRoutine(index));
    }

    /// <summary>Ideal delay between peeling fish so join rate matches convoy spacing at pathSpeed.</summary>
    public float GetEncounterHandoffPeelIntervalSeconds()
    {
        return pathSpeed > 0f ? fishSpacing / pathSpeed : 0.19f;
    }

    /// <summary>Max fish that fit on the spline at once without stacking at t=0.</summary>
    public int GetEncounterMaxTrailFishCount()
    {
        if (spline == null || fishSpacing <= 0f)
        {
            return maxFishCount;
        }

        float splineLength = spline.GetLengthApproximately(0f, 1f, PathSampleAccuracy * 4);
        float usableLength = Mathf.Max(0f, splineLength - headGap);
        return Mathf.Max(1, 1 + Mathf.FloorToInt(usableLength / fishSpacing));
    }

    public bool CanAcceptAnotherEncounterFollower()
    {
        if (!_isEncounterConvoy || !_encounterConvoyFrontActive)
        {
            return true;
        }

        return CountEncounterTrailFish() < GetEncounterMaxTrailFishCount();
    }

    public bool IsEncounterConvoyComplete()
    {
        if (!_isEncounterConvoy || !_swarmRunning)
        {
            return !_swarmRunning && _encounterFollowersAddedCount <= 0 && followers.Count == 0;
        }

        if (_encounterFollowersAddedCount <= 0)
        {
            return followers.Count == 0;
        }

        if (_pendingHomingIndices.Count > 0)
        {
            return false;
        }

        for (int i = 0; i < followers.Count; i++)
        {
            Transform follower = followers[i];
            FollowerState state = i < _states.Count ? _states[i] : null;
            if (follower == null || state == null)
            {
                continue;
            }

            if (!state.EncounterFinished && follower.gameObject.activeSelf)
            {
                return false;
            }
        }

        return true;
    }

    public void ImportEncounterFollowers(
        IReadOnlyList<Transform> importedFish,
        BezierSpline encounterSpline,
        float encounterEndScaleFactor,
        float pathSpeedOverride = -1f,
        float leadNormalizedT = -1f,
        float homingSpeed = 10f,
        float joinDistance = 0.75f,
        float homingStaggerSeconds = 0.06f,
        float joinTimeoutSeconds = 15f,
        float homingTurnSpeed = 8f)
    {
        _encounterHomingStaggerSeconds = Mathf.Max(0f, homingStaggerSeconds);
        BeginEncounterConvoy(
            encounterSpline,
            pathMidScaleFactor: 0.5f,
            pathEndScaleFactor: encounterEndScaleFactor,
            pathSpeedOverride,
            homingSpeed,
            joinDistance,
            joinTimeoutSeconds,
            homingTurnSpeed);

        if (leadNormalizedT >= 0f)
        {
            _encounterLeadNormalizedT = Mathf.Clamp01(leadNormalizedT);
        }

        for (int i = 0; i < importedFish.Count; i++)
        {
            Transform follower = importedFish[i];
            if (follower == null)
            {
                continue;
            }

            AddEncounterFollower(follower);
            if (_encounterHomingStaggerSeconds > 0f && i < importedFish.Count - 1)
            {
                // Batch import preserves stagger via delayed adds in a coroutine on caller side.
            }
        }

        _encounterLeadNormalizedT = -1f;
    }

    private void ActivateEncounterFollowerAt(int index)
    {
        if (index < 0 || index >= followers.Count)
        {
            return;
        }

        Transform follower = followers[index];
        if (follower == null)
        {
            return;
        }

        follower.gameObject.SetActive(true);
        Renderer[] renderers = follower.GetComponentsInChildren<Renderer>(true);
        for (int r = 0; r < renderers.Length; r++)
        {
            Renderer renderer = renderers[r];
            if (renderer == null)
            {
                continue;
            }

            renderer.enabled = true;
        }
    }

    public float GetLeadNormalizedT()
    {
        if (_isEncounterConvoy && _encounterConvoyFrontActive)
        {
            return _encounterConvoyFrontT;
        }

        FollowerState lead = GetLeadState();
        return lead == null ? 0f : lead.NormalizedT;
    }

    public bool IsConvoyPastThreshold(float threshold)
    {
        return GetLeadNormalizedT() >= Mathf.Clamp01(threshold);
    }

    public bool AreAllFollowersAtPathEnd(float threshold = EncounterPathEndThreshold)
    {
        if (_isEncounterConvoy)
        {
            return IsEncounterConvoyComplete();
        }

        if (followers.Count == 0)
        {
            return false;
        }

        bool anyActive = false;
        for (int i = 0; i < followers.Count; i++)
        {
            Transform follower = followers[i];
            FollowerState state = i < _states.Count ? _states[i] : null;
            if (!IsFollowerActive(follower) || state == null)
            {
                continue;
            }

            if (_pendingHomingIndices.Contains(i))
            {
                return false;
            }

            anyActive = true;
            if (state.NormalizedT < threshold)
            {
                return false;
            }
        }

        return anyActive;
    }

    private static bool HasReachedPathEnd(FollowerState state)
    {
        return state != null && state.NormalizedT >= EncounterPathEndThreshold;
    }

    private void Update()
    {
        if (spline == null)
        {
            return;
        }

        EnsureBuffers();

        if (_externallyControlled)
        {
            return;
        }

        if (_swarmRunning)
        {
            AdvanceAllFish(Time.deltaTime);
        }

        if (_swarmRunning && _swarmFollowingActive && allowPrefabSpawning)
        {
            TrySpawnFollower();
        }

        for (int i = 0; i < followers.Count; i++)
        {
            Transform follower = followers[i];
            FollowerState state = _states[i];
            if (!IsFollowerActive(follower) || state == null || _pendingHomingIndices.Contains(i))
            {
                continue;
            }

            if (!_isEncounterConvoy && state.EncounterFinished)
            {
                continue;
            }

            if (!SampleFishFrame(state, out Vector3 slotCenter, out Vector3 tangent, out Vector3 right, out Vector3 up))
            {
                continue;
            }

            follower.position = ApplyTubeOffset(state, slotCenter, right, up);
            follower.rotation = GetFollowerRotation(tangent, up, state);
            ApplyFishScale(follower, state);
        }

        if (_isEncounterConvoy && _swarmRunning)
        {
            ProcessEncounterEndDisappear();
        }

        if (_isBridgeTransit && _swarmRunning)
        {
            ProcessBridgeTransitArrivals();
        }

        if (_tailDissolveActive && _swarmRunning && !_isEncounterConvoy)
        {
            ProcessTailDissolve();
        }
    }

    private void ProcessTailDissolve()
    {
        if (_tailDissolveCurrentIndex >= 0)
        {
            UpdateTailDissolveCurrent();
            return;
        }

        if (Time.time < _tailDissolveNextFishTime)
        {
            return;
        }

        if (!TryGetRearmostActiveFollowerIndex(out int index, out FollowerState state))
        {
            return;
        }

        Transform follower = followers[index];
        CaptureFollowerBaseScale(follower, state, forceRecapture: false);
        state.EndShrinkStartTime = Time.time;
        state.EndShrinkFromMultiplier = GetFishScaleMultiplier(state);
        _tailDissolveCurrentIndex = index;
    }

    private void UpdateTailDissolveCurrent()
    {
        if (_tailDissolveCurrentIndex < 0 || !IsValidFollowerIndex(_tailDissolveCurrentIndex))
        {
            _tailDissolveCurrentIndex = -1;
            return;
        }

        Transform follower = followers[_tailDissolveCurrentIndex];
        FollowerState state = _states[_tailDissolveCurrentIndex];
        if (follower == null || state == null || state.EncounterFinished)
        {
            _tailDissolveCurrentIndex = -1;
            _tailDissolveNextFishTime = Time.time + _tailDissolveInterval;
            return;
        }

        ApplyFishScale(follower, state);

        if (GetTailDissolveScaleMultiplier(state) <= 0.05f)
        {
            HardHideFollower(follower);
            int removeIndex = _tailDissolveCurrentIndex;
            followers.RemoveAt(removeIndex);
            if (removeIndex < _states.Count)
            {
                _states.RemoveAt(removeIndex);
            }

            RebuildPendingHomingIndices();
            _tailDissolveCurrentIndex = -1;
            _tailDissolveNextFishTime = Time.time + _tailDissolveInterval;
        }
    }

    private bool TryGetRearmostActiveFollowerIndex(out int index, out FollowerState state)
    {
        index = -1;
        state = null;
        int bestSlot = int.MinValue;
        int activeCount = 0;

        EnsureBuffers();

        for (int i = 0; i < followers.Count; i++)
        {
            Transform follower = followers[i];
            FollowerState candidate = i < _states.Count ? _states[i] : null;
            if (!IsFollowerActive(follower) || candidate == null || candidate.EncounterFinished
                || _pendingHomingIndices.Contains(i))
            {
                continue;
            }

            activeCount++;
            if (candidate.SlotIndex >= bestSlot)
            {
                bestSlot = candidate.SlotIndex;
                index = i;
                state = candidate;
            }
        }

        if (activeCount <= 1)
        {
            index = -1;
            state = null;
            return false;
        }

        return index >= 0;
    }

    private float GetTailDissolveScaleMultiplier(FollowerState state)
    {
        if (state == null || state.EndShrinkStartTime < 0f)
        {
            return 1f;
        }

        float shrinkT = Mathf.Clamp01((Time.time - state.EndShrinkStartTime) / _tailDissolvePopDuration);
        return Mathf.Lerp(state.EndShrinkFromMultiplier, 0f, Mathf.SmoothStep(0f, 1f, shrinkT));
    }

    private void ProcessEncounterEndDisappear()
    {
        for (int i = followers.Count - 1; i >= 0; i--)
        {
            if (_pendingHomingIndices.Contains(i))
            {
                continue;
            }

            Transform follower = followers[i];
            FollowerState state = i < _states.Count ? _states[i] : null;
            if (!IsFollowerActive(follower) || state == null || state.EncounterFinished)
            {
                continue;
            }

            if (!HasReachedPathEnd(state))
            {
                continue;
            }

            if (_encounterPreserveFishScale)
            {
                HardHideFollower(follower);
                state.EncounterFinished = true;
                RemoveEncounterFollowerAt(i);
                continue;
            }

            if (state.EndShrinkStartTime < 0f)
            {
                state.EndShrinkStartTime = Time.time;
                state.EndShrinkFromMultiplier = GetEncounterUniformScaleMultiplier(state);
            }

            ApplyFishScale(follower, state);

            if (GetEffectiveScaleMultiplier(state) <= 0.05f)
            {
                HardHideFollower(follower);
                state.EncounterFinished = true;
                RemoveEncounterFollowerAt(i);
            }
        }
    }

    private void RemoveEncounterFollowerAt(int removeIndex)
    {
        if (removeIndex < 0 || removeIndex >= followers.Count)
        {
            return;
        }

        followers.RemoveAt(removeIndex);
        if (removeIndex < _states.Count)
        {
            _states.RemoveAt(removeIndex);
        }

        if (_pendingHomingIndices.Count == 0)
        {
            return;
        }

        var adjusted = new HashSet<int>();
        foreach (int index in _pendingHomingIndices)
        {
            if (index == removeIndex)
            {
                continue;
            }

            adjusted.Add(index > removeIndex ? index - 1 : index);
        }

        _pendingHomingIndices.Clear();
        foreach (int index in adjusted)
        {
            _pendingHomingIndices.Add(index);
        }

        if (_isEncounterConvoy && _encounterConvoyFrontActive)
        {
            ApplyEncounterConvoyTrailFromFront();
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
        if (HasPendingPlantFish)
        {
            return;
        }

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
        StopEncounterHoming();

        if (_activateFollowersRoutine != null)
        {
            StopCoroutine(_activateFollowersRoutine);
            _activateFollowersRoutine = null;
        }

        _swarmRunning = false;
        _swarmFollowingActive = false;
        _externallyControlled = false;
        _externalScaleMultiplier = 1f;
        _encounterScaleAlongPath = false;
        _encounterMidScaleFactor = 0.5f;
        _encounterEndScaleFactor = 1f;
        _encounterScaleDurationSeconds = 0f;
        _encounterLeadNormalizedT = -1f;
        _encounterConvoyFrontActive = false;
        _isEncounterConvoy = false;
        _isBridgeTransit = false;
        _encounterFollowersAddedCount = 0;
        _bridgeArrivals.Clear();
        _swarmStartedTime = -1f;
        _plantReleasePrepared = false;
        _pendingHomingIndices.Clear();
        StopTailDissolve();
        DeactivateAllFollowers();
    }

    /// <summary>Stops motion and clears lists without hiding follower renderers (Stage 3 after handoff).</summary>
    public void StopSwarmMotionOnly()
    {
        CancelDelayedStart();
        StopEncounterHoming();

        if (_activateFollowersRoutine != null)
        {
            StopCoroutine(_activateFollowersRoutine);
            _activateFollowersRoutine = null;
        }

        _swarmRunning = false;
        _swarmFollowingActive = false;
        _externallyControlled = false;
        _swarmStartedTime = -1f;
        _plantReleasePrepared = false;
        _pendingHomingIndices.Clear();
        StopTailDissolve();
    }

    private static void EnsureFollowerVisible(Transform follower)
    {
        if (follower == null)
        {
            return;
        }

        follower.gameObject.SetActive(true);

        Renderer[] renderers = follower.GetComponentsInChildren<Renderer>(true);
        for (int i = 0; i < renderers.Length; i++)
        {
            Renderer renderer = renderers[i];
            if (renderer == null)
            {
                continue;
            }

            renderer.enabled = true;
        }
    }

    private void StopEncounterHoming()
    {
        _encounterHomingActive = false;
        if (_encounterHomingRoutine != null)
        {
            StopCoroutine(_encounterHomingRoutine);
            _encounterHomingRoutine = null;
        }

        ForceJoinAllPendingEncounterFollowers();
    }

    private void ForceJoinPendingEncounterFollower(int followerIndex)
    {
        if (IsValidFollowerIndex(followerIndex) && _pendingHomingIndices.Contains(followerIndex))
        {
            JoinFollowerOnPath(followerIndex);
        }
    }

    public void ForceJoinAllPendingEncounterFollowers()
    {
        if (_pendingHomingIndices.Count == 0)
        {
            return;
        }

        var pending = new List<int>(_pendingHomingIndices);
        for (int i = 0; i < pending.Count; i++)
        {
            ForceJoinPendingEncounterFollower(pending[i]);
        }
    }

    private IEnumerator EncounterHomingRoutine()
    {
        for (int i = 0; i < followers.Count; i++)
        {
            if (!_encounterHomingActive)
            {
                yield break;
            }

            int followerIndex = i;
            StartCoroutine(HomingEncounterFishRoutine(followerIndex));

            if (_encounterHomingStaggerSeconds > 0f && i < followers.Count - 1)
            {
                yield return new WaitForSeconds(_encounterHomingStaggerSeconds);
            }
        }

        _encounterHomingRoutine = null;
    }

    private IEnumerator HomingEncounterFishRoutine(int followerIndex)
    {
        if (!IsValidFollowerIndex(followerIndex) || !_pendingHomingIndices.Contains(followerIndex))
        {
            yield break;
        }

        Transform follower = followers[followerIndex];
        if (follower == null)
        {
            yield break;
        }

        float joinDeadline = Time.time + _encounterJoinTimeoutSeconds;
        int failedPoseFrames = 0;

        while (_encounterHomingActive && _swarmRunning && _pendingHomingIndices.Contains(followerIndex))
        {
            if (follower == null)
            {
                yield break;
            }

            if (!TryGetJoinWorldPose(followerIndex, out Vector3 joinPosition, out Quaternion joinRotation))
            {
                failedPoseFrames++;
                if (failedPoseFrames > 120)
                {
                    JoinFollowerOnPath(followerIndex);
                    yield break;
                }

                yield return null;
                continue;
            }

            failedPoseFrames = 0;

            float distance = Vector3.Distance(follower.position, joinPosition);
            if (distance <= _encounterJoinDistance || Time.time >= joinDeadline)
            {
                JoinFollowerOnPath(followerIndex);
                yield break;
            }

            float step = Mathf.Min(distance, _encounterHomingSpeed * Time.deltaTime);
            Vector3 direction = (joinPosition - follower.position).normalized;
            follower.position += direction * step;

            if (direction.sqrMagnitude > 0.0001f)
            {
                Quaternion faceMovement = Quaternion.LookRotation(direction, Vector3.up);
                follower.rotation = Quaternion.Slerp(
                    follower.rotation,
                    faceMovement,
                    _encounterHomingTurnSpeed * Time.deltaTime
                );
            }

            ApplyFishScale(follower, _states[followerIndex]);
            yield return null;
        }

        ForceJoinPendingEncounterFollower(followerIndex);
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
            HardHideFollower(followers[i]);
        }
    }

    private static void HardHideFollower(Transform follower)
    {
        if (follower == null)
        {
            return;
        }

        Renderer[] renderers = follower.GetComponentsInChildren<Renderer>(true);
        for (int i = 0; i < renderers.Length; i++)
        {
            Renderer renderer = renderers[i];
            if (renderer == null)
            {
                continue;
            }

            renderer.enabled = false;
            renderer.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
        }

        follower.gameObject.SetActive(false);
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
            if (follower == null || _pendingHomingIndices.Contains(i))
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

        if (_isEncounterConvoy && _encounterConvoyFrontActive)
        {
            spline.MoveAlongSpline(ref _encounterConvoyFrontT, delta, PathSampleAccuracy);
            WrapEncounterFrontT();
            EnforceEncounterMinimumFrontT();
            ApplyEncounterConvoyTrailFromFront();
            PostProcessConvoyMovement();
            return;
        }

        for (int i = 0; i < followers.Count; i++)
        {
            if (_states[i] == null || _pendingHomingIndices.Contains(i))
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
        if (travelMode == TravelMode.Once)
        {
            state.NormalizedT = Mathf.Clamp01(state.NormalizedT);
            return;
        }

        if (travelMode == TravelMode.Loop)
        {
            state.NormalizedT = Mathf.Repeat(state.NormalizedT, 1f);
        }
    }

    private void WrapEncounterFrontT()
    {
        if (_isEncounterConvoy && travelMode == TravelMode.Once)
        {
            _encounterConvoyFrontT = Mathf.Max(_encounterConvoyFrontT, startNormalizedT);
            return;
        }

        if (travelMode == TravelMode.Once)
        {
            _encounterConvoyFrontT = Mathf.Clamp01(_encounterConvoyFrontT);
            return;
        }

        if (travelMode == TravelMode.Loop)
        {
            _encounterConvoyFrontT = Mathf.Repeat(_encounterConvoyFrontT, 1f);
        }
    }

    private void EnforceEncounterMinimumFrontT()
    {
        int trailCount = CountEncounterTrailFish();
        if (trailCount <= 0)
        {
            return;
        }

        float minFront = GetMinimumEncounterFrontT(trailCount);
        if (_encounterConvoyFrontT < minFront)
        {
            _encounterConvoyFrontT = minFront;
        }
    }

    private int CountEncounterTrailFish()
    {
        int joinedCount = 0;
        for (int i = 0; i < followers.Count; i++)
        {
            if (_states[i] == null || _pendingHomingIndices.Contains(i) || _states[i].EncounterFinished)
            {
                continue;
            }

            if (IsFollowerActive(followers[i]))
            {
                joinedCount++;
            }
        }

        return joinedCount + _pendingHomingIndices.Count;
    }

    private float GetMinimumEncounterFrontT(int trailFishCount)
    {
        if (trailFishCount <= 0)
        {
            return startNormalizedT;
        }

        float maxArcBehind = headGap + (trailFishCount - 1) * fishSpacing;
        return GetNormalizedTAtArcFromStart(maxArcBehind);
    }

    private float GetNormalizedTAtArcFromStart(float arcDistance)
    {
        if (spline == null || arcDistance <= 0f)
        {
            return startNormalizedT;
        }

        float normalizedT = startNormalizedT;
        float arcDelta = _movingForward ? arcDistance : -arcDistance;
        spline.MoveAlongSpline(ref normalizedT, arcDelta, PathSampleAccuracy);
        return normalizedT;
    }

    private void ApplyEncounterConvoyTrailFromFront()
    {
        if (spline == null || !_isEncounterConvoy || !_encounterConvoyFrontActive)
        {
            return;
        }

        EnforceEncounterMinimumFrontT();
        var joinedIndices = new List<int>();
        var pendingIndices = new List<int>(_pendingHomingIndices);

        for (int i = 0; i < followers.Count; i++)
        {
            if (_states[i] == null || _pendingHomingIndices.Contains(i) || _states[i].EncounterFinished)
            {
                continue;
            }

            Transform follower = followers[i];
            if (!IsFollowerActive(follower))
            {
                continue;
            }

            joinedIndices.Add(i);
        }

        joinedIndices.Sort((a, b) => _states[a].SlotIndex.CompareTo(_states[b].SlotIndex));
        pendingIndices.Sort((a, b) =>
        {
            int slotA = IsValidFollowerIndex(a) && _states[a] != null ? _states[a].SlotIndex : a;
            int slotB = IsValidFollowerIndex(b) && _states[b] != null ? _states[b].SlotIndex : b;
            return slotA.CompareTo(slotB);
        });

        int joinedCount = joinedIndices.Count;
        for (int rank = 0; rank < joinedCount; rank++)
        {
            float arcBehindFront = headGap + rank * fishSpacing;
            _states[joinedIndices[rank]].NormalizedT = GetEncounterTrailNormalizedT(arcBehindFront);
        }

        for (int pendingRank = 0; pendingRank < pendingIndices.Count; pendingRank++)
        {
            int followerIndex = pendingIndices[pendingRank];
            if (!IsValidFollowerIndex(followerIndex) || _states[followerIndex] == null)
            {
                continue;
            }

            float arcBehindFront = headGap + (joinedCount + pendingRank) * fishSpacing;
            _states[followerIndex].NormalizedT = GetEncounterTrailNormalizedT(arcBehindFront);
        }
    }

    private float GetEncounterTrailNormalizedT(float arcDistanceBehindFront)
    {
        float normalizedT = _encounterConvoyFrontT;
        float arcDelta = _movingForward ? -arcDistanceBehindFront : arcDistanceBehindFront;
        spline.MoveAlongSpline(ref normalizedT, arcDelta, PathSampleAccuracy);
        return ClampEncounterFishNormalizedT(normalizedT);
    }

    private float ClampEncounterFishNormalizedT(float normalizedT)
    {
        if (travelMode == TravelMode.Once)
        {
            return Mathf.Clamp01(normalizedT);
        }

        if (travelMode == TravelMode.Loop)
        {
            return Mathf.Repeat(normalizedT, 1f);
        }

        return normalizedT;
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
            if (!IsFollowerActive(followers[i]) || _states[i] == null || _pendingHomingIndices.Contains(i))
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

            FollowerState state = CreateFollowerState(_nextSlotIndex);
            _states.Add(state);
            CaptureFollowerPrefabRotation(followers[i], state, forceRecapture: true);
            ApplyRandomFollowerMaterial(followers[i], state, forceRecapture: true);
            _nextSlotIndex++;
        }
    }

    private void AssignEncounterTrailSlots()
    {
        _states.Clear();
        _nextSlotIndex = 0;
        _movingForward = true;

        for (int i = 0; i < followers.Count; i++)
        {
            Transform follower = followers[i];
            if (follower == null)
            {
                _states.Add(null);
                continue;
            }

            FollowerState state = CreateEncounterFollowerState(_nextSlotIndex);
            _states.Add(state);
            CaptureFollowerBaseScale(follower, state, forceRecapture: true);
            CaptureFollowerPrefabRotation(follower, state, forceRecapture: true);
            _nextSlotIndex++;
        }
    }

    private FollowerState CreateEncounterFollowerState(int slotIndex)
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
            FixedScaleMultiplier = 1f,
            AxisStretch = Vector3.one
        };
    }

    private FollowerState CreateFollowerState(int slotIndex, bool applySpawnBodyStretch = false)
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
            FixedScaleMultiplier = Random.Range(minFishScale, maxFishScale),
            AxisStretch = applySpawnBodyStretch ? CreateSpawnedBodyAxisStretch() : Vector3.one
        };
    }

    private Vector3 CreateSpawnedBodyAxisStretch()
    {
        Vector3 stretch = Vector3.one;
        float stretchValue = Random.Range(spawnedBodyStretchMin, spawnedBodyStretchMax);

        switch (modelForwardAxis)
        {
            case ModelForwardAxis.PositiveX:
                stretch.x = stretchValue;
                break;
            case ModelForwardAxis.NegativeX:
                stretch.x = stretchValue;
                break;
            case ModelForwardAxis.PositiveY:
                stretch.y = stretchValue;
                break;
            case ModelForwardAxis.NegativeY:
                stretch.y = stretchValue;
                break;
            case ModelForwardAxis.PositiveZ:
                stretch.z = stretchValue;
                break;
            case ModelForwardAxis.NegativeZ:
                stretch.z = stretchValue;
                break;
        }

        return stretch;
    }

    private float ComputeNormalizedTForNewSlot(int slotIndex)
    {
        if (slotIndex <= 0)
        {
            if (_encounterLeadNormalizedT >= 0f)
            {
                return _encounterLeadNormalizedT;
            }

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

        FollowerState spawnState = CreateFollowerState(_nextSlotIndex, applySpawnBodyStretch: true);
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
        CaptureFollowerPrefabRotation(instance.transform, spawnState, forceRecapture: true);
        ApplyRandomFollowerMaterial(instance.transform, spawnState, forceRecapture: true);
        ApplyFishScale(instance.transform, spawnState);
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
        follower.rotation = GetFollowerRotation(tangent, up, state);
        ApplyFishScale(follower, state);
    }

    private Quaternion GetFollowerRotation(Vector3 tangent, Vector3 pathUp, FollowerState state)
    {
        Quaternion modelAxisCorrection = Quaternion.FromToRotation(
            GetModelForwardAxisVector(modelForwardAxis),
            Vector3.forward
        );
        Quaternion extraOffset = Quaternion.Euler(rotationOffsetEuler);
        Quaternion prefabOffset = GetPrefabRotationOffset(state);

        if (tangent.sqrMagnitude <= 0.0001f)
        {
            return modelAxisCorrection * extraOffset * prefabOffset;
        }

        Vector3 upDirection = pathUp.sqrMagnitude > 0.0001f ? pathUp : Vector3.up;
        return Quaternion.LookRotation(tangent, upDirection) * modelAxisCorrection * extraOffset * prefabOffset;
    }

    private Quaternion GetPrefabRotationOffset(FollowerState state)
    {
        if (!applyFollowerPrefabRotation || state == null || !state.HasPrefabRotation)
        {
            return Quaternion.identity;
        }

        return state.PrefabRotationOffset;
    }

    private void ApplyRandomFollowerMaterial(Transform follower, FollowerState state, bool forceRecapture)
    {
        if (!randomizeFollowerMaterials || state == null)
        {
            return;
        }

        if (state.HasAssignedMaterial && !forceRecapture)
        {
            ApplyStoredFollowerMaterial(follower, state);
            return;
        }

        Material chosen = PickRandomFloraMaterial();
        if (chosen == null)
        {
            return;
        }

        state.AssignedMaterial = chosen;
        state.HasAssignedMaterial = true;
        ApplyStoredFollowerMaterial(follower, state);
    }

    private void ApplyStoredFollowerMaterial(Transform follower, FollowerState state)
    {
        if (follower == null || state == null || !state.HasAssignedMaterial || state.AssignedMaterial == null)
        {
            return;
        }

        MeshRenderer renderer = follower.GetComponent<MeshRenderer>();
        if (renderer == null)
        {
            renderer = follower.GetComponentInChildren<MeshRenderer>(true);
        }

        if (renderer != null)
        {
            renderer.sharedMaterial = state.AssignedMaterial;
        }
    }

    private Material PickRandomFloraMaterial()
    {
        bool hasFlora1 = flora1Material != null;
        bool hasFlora2 = flora2Material != null;

        if (hasFlora1 && hasFlora2)
        {
            return Random.value < 0.5f ? flora1Material : flora2Material;
        }

        if (hasFlora1)
        {
            return flora1Material;
        }

        if (hasFlora2)
        {
            return flora2Material;
        }

        return null;
    }

    private void CaptureFollowerPrefabRotation(Transform follower, FollowerState state, bool forceRecapture)
    {
        if (!applyFollowerPrefabRotation || state == null)
        {
            return;
        }

        if (state.HasPrefabRotation && !forceRecapture)
        {
            return;
        }

        if (followerPrefab != null)
        {
            state.PrefabRotationOffset = followerPrefab.transform.localRotation;
        }
        else if (follower != null)
        {
            state.PrefabRotationOffset = follower.localRotation;
        }
        else
        {
            state.PrefabRotationOffset = Quaternion.identity;
        }

        state.HasPrefabRotation = true;
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

    private bool HasPlantFishReleaseController()
    {
        return TryGetComponent<PlantFishReleaseController>(out PlantFishReleaseController controller)
            && controller != null
            && controller.isActiveAndEnabled;
    }

    private static void CaptureFollowerBaseScale(Transform follower, FollowerState state, bool forceRecapture)
    {
        if (follower == null || state == null)
        {
            return;
        }

        if (state.HasBaseScale && !forceRecapture)
        {
            return;
        }

        Vector3 scale = follower.localScale;
        if (scale.sqrMagnitude < 0.0001f)
        {
            scale = Vector3.one;
        }

        state.BaseLocalScale = scale;
        state.HasBaseScale = true;
    }

    private void ApplyFollowerVisibleScale(Transform follower, FollowerState state)
    {
        if (follower == null || state == null)
        {
            return;
        }

        CaptureFollowerBaseScale(follower, state, forceRecapture: false);
        follower.localScale = GetConvoyScaleVector(state);
    }

    private void ApplyPlantRestScale(Transform follower, FollowerState state, float plantScaleFactor)
    {
        if (follower == null || state == null)
        {
            return;
        }

        CaptureFollowerBaseScale(follower, state, forceRecapture: false);
        follower.localScale = GetPlantRestScaleVector(state, plantScaleFactor);
    }

    private Vector3 GetPlantRestScaleVector(FollowerState state, float plantScaleFactor)
    {
        return ComposeFollowerScale(state, Mathf.Max(0.01f, plantScaleFactor));
    }

    private Vector3 GetConvoyScaleVector(FollowerState state)
    {
        return ComposeFollowerScale(state, GetEffectiveScaleMultiplier(state));
    }

    private float GetEncounterPathScaleMultiplier(FollowerState state)
    {
        if (state == null)
        {
            return 1f;
        }

        float start = _encounterPathStartNormalizedT;
        float endRange = 1f - start;
        if (endRange <= 0.0001f)
        {
            return 1f;
        }

        float progress = Mathf.Clamp01((state.NormalizedT - start) / endRange);
        return Mathf.Lerp(
            1f,
            _encounterMidScaleFactor,
            Mathf.SmoothStep(0f, 1f, progress));
    }

    private float GetEffectiveScaleMultiplier(FollowerState state)
    {
        if (state != null && state.EndShrinkStartTime >= 0f && _tailDissolveActive && !_isEncounterConvoy)
        {
            if (state.EncounterFinished)
            {
                return 0f;
            }

            return GetTailDissolveScaleMultiplier(state);
        }

        if (_isEncounterConvoy && state != null)
        {
            if (state.EncounterFinished)
            {
                return _encounterEndScaleFactor;
            }

            if (state.EndShrinkStartTime >= 0f)
            {
                float shrinkT = Mathf.Clamp01((Time.time - state.EndShrinkStartTime) / EncounterEndPopDuration);
                return Mathf.Lerp(
                    state.EndShrinkFromMultiplier,
                    _encounterEndScaleFactor,
                    Mathf.SmoothStep(0f, 1f, shrinkT));
            }

            if (_encounterPreserveFishScale)
            {
                return GetFishScaleMultiplier(state);
            }

            return GetEncounterUniformScaleMultiplier(state);
        }

        return GetFishScaleMultiplier(state);
    }

    private float GetEncounterUniformScaleMultiplier(FollowerState state)
    {
        if (state == null)
        {
            return 1f;
        }

        float baseScale = state.EncounterBaseScaleMultiplier > 0f
            ? state.EncounterBaseScaleMultiplier
            : GetFishScaleMultiplier(state);

        if (_encounterScaleDurationSeconds > 0f && state.EncounterScaleStartTime >= 0f)
        {
            float elapsed = Time.time - state.EncounterScaleStartTime;
            float progress = Mathf.Clamp01(elapsed / _encounterScaleDurationSeconds);
            float scaleFactor = Mathf.Lerp(
                1f,
                _encounterMidScaleFactor,
                Mathf.SmoothStep(0f, 1f, progress));
            return baseScale * scaleFactor;
        }

        return baseScale * GetEncounterPathScaleMultiplier(state);
    }

    private Vector3 ComposeFollowerScale(FollowerState state, float uniformMultiplier)
    {
        float uniform = uniformMultiplier * _externalScaleMultiplier;
        Vector3 scaled = Vector3.Scale(state.BaseLocalScale, state.AxisStretch);
        return new Vector3(
            scaled.x * uniform,
            scaled.y * uniform,
            scaled.z * uniform
        );
    }

    private void InitializeFollowerScale(Transform follower, FollowerState state)
    {
        CaptureFollowerBaseScale(follower, state, forceRecapture: false);
    }

    private float GetFishScaleMultiplier(FollowerState state)
    {
        if (state == null)
        {
            return minFishScale;
        }

        if (state.FixedScaleMultiplier <= 0f)
        {
            state.FixedScaleMultiplier = Random.Range(minFishScale, maxFishScale);
        }

        return state.FixedScaleMultiplier;
    }

    private void ApplyFishScale(Transform follower, FollowerState state)
    {
        if (follower == null || state == null)
        {
            return;
        }

        InitializeFollowerScale(follower, state);
        follower.localScale = GetConvoyScaleVector(state);
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
            int index = _states.Count;
            FollowerState state = CreateFollowerState(_nextSlotIndex);
            _states.Add(state);
            CaptureFollowerPrefabRotation(followers[index], state, forceRecapture: true);
            ApplyRandomFollowerMaterial(followers[index], state, forceRecapture: true);
            _nextSlotIndex++;
        }

        if (_states.Count > followers.Count)
        {
            _states.RemoveRange(followers.Count, _states.Count - followers.Count);
        }
    }

    private void EnsureStateForIndex(int index)
    {
        EnsureBuffers();
        if (_states[index] != null)
        {
            return;
        }

        FollowerState state = CreateFollowerState(_nextSlotIndex);
        _states[index] = state;
        CaptureFollowerPrefabRotation(followers[index], state, forceRecapture: true);
        ApplyRandomFollowerMaterial(followers[index], state, forceRecapture: true);
        _nextSlotIndex++;
    }

    private bool IsValidFollowerIndex(int index)
    {
        return index >= 0 && index < followers.Count;
    }

#if UNITY_EDITOR
    private void TryAssignDefaultFloraMaterialsInEditor()
    {
        if (flora1Material == null)
        {
            flora1Material = UnityEditor.AssetDatabase.LoadAssetAtPath<Material>(Flora1MaterialPath);
        }

        if (flora2Material == null)
        {
            flora2Material = UnityEditor.AssetDatabase.LoadAssetAtPath<Material>(Flora2MaterialPath);
        }
    }
#endif
}
