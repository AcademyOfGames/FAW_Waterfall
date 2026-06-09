using System.Collections;
using System.Collections.Generic;
using BezierSolution;
using UnityEngine;

/// <summary>
/// Stage 4: fish peel from Stage 3 onto a bridge spline, swim to Stage 4, then home onto the
/// real ViewerEncounterPath. Second Group B paths append behind the convoy tail.
/// </summary>
public class ViewerFishEncounterController : MonoBehaviour
{
    [Header("References")]
    [SerializeField] private SplineFishGroupOrchestrator fishOrchestrator;
    [Tooltip("Leave empty to use Camera.main at runtime.")]
    [SerializeField] private Transform viewerTransform;
    [SerializeField] private ExperienceRiverAmbience riverAmbience;
    [Tooltip("Defines Stage 3 / Stage 4 junctions and optional bridge splines.")]
    [SerializeField] private Stage4SplineConnection splineConnection;
    [Tooltip("Optional pre-wired rig with the Stage 4 spline.")]
    [SerializeField] private ViewerSpiralSplineRig spiralRig;
    [Tooltip("Optional override. When empty, uses the rig's convoy swarm.")]
    [SerializeField] private VertexPathSwarmFollower viewerConvoySwarm;

    [Header("Collection")]
    [Tooltip("Max fish on Stage 4. 0 = all active Group B fish.")]
    [SerializeField] private int maxEncounterFish;

    [Header("Convoy")]
    [SerializeField] private float maxConvoyDurationSeconds = 180f;
    [Tooltip("Extra seconds after Group B is ready to wait for late spawns before handoff.")]
    [SerializeField] private float postSpawnSettleSeconds = 0.5f;
    [Tooltip("Max seconds to wait for each fish to reach the Stage 3 junction before stopping peel.")]
    [SerializeField] private float maxWaitForJunctionSeconds = 120f;
    [Tooltip("Seconds to smoothly shrink each fish from its handoff size to half size on Stage 4.")]
    [SerializeField] private float encounterScaleDurationSeconds = 12f;
    [Tooltip("Target scale factor relative to each fish's size at handoff (0.5 = half size).")]
    [Range(0.01f, 1f)]
    [SerializeField] private float encounterTargetScaleFactor = 0.5f;
    [Tooltip("Spread Stage 4 disappearances over this many meters before the path end (rearmost fish first).")]
    [SerializeField] private float encounterEndDissolveSpreadMeters = 6f;
    [Tooltip("Seconds for each fish to shrink out once its dissolve point is reached.")]
    [SerializeField] private float encounterEndPopDurationSeconds = 0.75f;
    [Tooltip("Meters per second along the Stage 4 spiral and bridge transit (independent of Stage 3 speed).")]
    [SerializeField] private float stage4PathSpeed = 4f;

    [Header("Stage 4 join homing")]
    [SerializeField] private float stage4HomingSpeed = 5f;
    [SerializeField] private float stage4JoinDistance = 0.75f;
    [SerializeField] private float stage4JoinTimeoutSeconds = 15f;
    [SerializeField] private float stage4HomingTurnSpeed = 8f;

    private Coroutine _encounterRoutine;
    private bool _encounterStarted;
    private Transform _bridgeRoot;
    private readonly List<VertexPathSwarmFollower> _bridgeSwarms = new List<VertexPathSwarmFollower>();
    private readonly List<BezierSpline> _runtimeBridges = new List<BezierSpline>();
    private readonly List<bool> _sourcePeelStopped = new List<bool>();

    private void Awake()
    {
        EnsureFishOrchestrator();
        EnsureSplineConnection();
    }

    private void OnValidate()
    {
        maxEncounterFish = Mathf.Max(0, maxEncounterFish);
        maxConvoyDurationSeconds = Mathf.Max(1f, maxConvoyDurationSeconds);
        postSpawnSettleSeconds = Mathf.Max(0f, postSpawnSettleSeconds);
        maxWaitForJunctionSeconds = Mathf.Max(1f, maxWaitForJunctionSeconds);
        encounterScaleDurationSeconds = Mathf.Max(0.01f, encounterScaleDurationSeconds);
        encounterTargetScaleFactor = Mathf.Clamp(encounterTargetScaleFactor, 0.01f, 1f);
        encounterEndDissolveSpreadMeters = Mathf.Max(0f, encounterEndDissolveSpreadMeters);
        encounterEndPopDurationSeconds = Mathf.Max(0.05f, encounterEndPopDurationSeconds);
        stage4PathSpeed = Mathf.Max(0.1f, stage4PathSpeed);
        stage4HomingSpeed = Mathf.Max(0.1f, stage4HomingSpeed);
        stage4JoinDistance = Mathf.Max(0.01f, stage4JoinDistance);
        stage4JoinTimeoutSeconds = Mathf.Max(0.5f, stage4JoinTimeoutSeconds);
        stage4HomingTurnSpeed = Mathf.Max(0f, stage4HomingTurnSpeed);
    }

    private void OnEnable()
    {
        EnsureFishOrchestrator();

        if (fishOrchestrator != null)
        {
            fishOrchestrator.GroupBReadyForViewerEncounter += OnGroupBReadyForViewerEncounter;
        }
    }

    private void OnDisable()
    {
        if (fishOrchestrator != null)
        {
            fishOrchestrator.GroupBReadyForViewerEncounter -= OnGroupBReadyForViewerEncounter;
        }

        if (_encounterRoutine != null)
        {
            StopCoroutine(_encounterRoutine);
            _encounterRoutine = null;
        }

        int unused = 0;
        VertexPathSwarmFollower convoy = viewerConvoySwarm != null
            ? viewerConvoySwarm
            : GetComponentInChildren<ViewerSpiralSplineRig>(true)?.ConvoySwarm;
        CleanupBridgeInfrastructure(convoy);
    }

    private void OnGroupBReadyForViewerEncounter(IReadOnlyList<VertexPathSwarmFollower> groupB)
    {
        if (_encounterStarted)
        {
            return;
        }

        _encounterStarted = true;
        _encounterRoutine = StartCoroutine(RunEncounterRoutine(groupB));
    }

    private IEnumerator RunEncounterRoutine(IReadOnlyList<VertexPathSwarmFollower> groupB)
    {
        EnsureViewer();
        EnsureSplineConnection();

        if (splineConnection == null)
        {
            Debug.LogError(
                "[ViewerFishEncounterController] Stage4SplineConnection missing — Stage 4 handoff aborted.",
                this);
            yield break;
        }

        ViewerSpiralSplineRig activeRig = ResolveStage4Rig();
        VertexPathSwarmFollower stage4Convoy = ResolveStage4Convoy(activeRig);
        splineConnection.ConfigureFromEncounter(groupB, activeRig, stage4Convoy);

        if (postSpawnSettleSeconds > 0f)
        {
            yield return new WaitForSeconds(postSpawnSettleSeconds);
        }

        BezierSpline stage4Spline = splineConnection.ResolveStage4Spline(activeRig, stage4Convoy);
        if (stage4Spline == null || stage4Convoy == null)
        {
            Debug.LogWarning("[ViewerFishEncounterController] Missing Stage 4 spline or convoy swarm.", this);
            yield break;
        }

        float stage4JunctionT = splineConnection.ResolveStage4ConnectionNormalizedT();
        stage4Convoy.BeginStage4Convoy(
            stage4Spline,
            pathMidScaleFactor: encounterTargetScaleFactor,
            pathEndScaleFactor: 0f,
            encounterPathStartNormalizedT: stage4JunctionT,
            preserveFishScale: false,
            encounterScaleDurationSeconds: encounterScaleDurationSeconds,
            encounterEndDissolveSpreadMeters: encounterEndDissolveSpreadMeters,
            encounterEndPopDurationSeconds: encounterEndPopDurationSeconds);

        ConfigureStage4Homing(stage4Convoy);
        stage4Convoy.SetPathSpeed(stage4PathSpeed);

        if (!TrySetupBridgeSwarms(groupB, stage4Spline, stage4JunctionT))
        {
            Debug.LogWarning("[ViewerFishEncounterController] Failed to create bridge transit swarms.", this);
            yield break;
        }

        int totalTransferred = 0;
        float encounterElapsed = 0f;
        bool motionSynced = false;

        while (encounterElapsed < maxConvoyDurationSeconds)
        {
            ProcessBridgeArrivals(ref totalTransferred, stage4Convoy);

            bool anySourceStillPeeling = false;
            for (int sourceIndex = 0; sourceIndex < _bridgeSwarms.Count; sourceIndex++)
            {
                if (_sourcePeelStopped[sourceIndex])
                {
                    continue;
                }

                VertexPathSwarmFollower sourceSwarm = splineConnection.ResolveSourceSwarm(groupB, sourceIndex);
                if (sourceSwarm == null || !sourceSwarm.HasActiveFollowers())
                {
                    _sourcePeelStopped[sourceIndex] = true;
                    sourceSwarm?.StopSwarmMotionOnly();
                    continue;
                }

                anySourceStillPeeling = true;

                if (!motionSynced)
                {
                    stage4Convoy.SyncConvoySpacingFrom(sourceSwarm);
                    stage4Convoy.SetPathSpeed(stage4PathSpeed);
                    motionSynced = true;
                }

                if (maxEncounterFish > 0 && totalTransferred >= maxEncounterFish)
                {
                    continue;
                }

                Stage4SourceConnection connection = splineConnection.ResolveSourceConnection(groupB, sourceIndex);
                float stage3JunctionT = connection.stage3ConnectionNormalizedT;

                if (!sourceSwarm.IsConvoyPastThreshold(stage3JunctionT))
                {
                    continue;
                }

                if (!sourceSwarm.TryCollectLeadActiveFollower(null, out Transform leadFish))
                {
                    _sourcePeelStopped[sourceIndex] = true;
                    sourceSwarm.StopSwarmMotionOnly();
                    continue;
                }

                if (!sourceSwarm.TryExtractFollower(
                        leadFish,
                        out VertexPathSwarmFollower.FollowerTransferSnapshot snapshot))
                {
                    continue;
                }

                VertexPathSwarmFollower bridgeSwarm = _bridgeSwarms[sourceIndex];
                if (!bridgeSwarm.TryAddBridgeFollower(leadFish, snapshot))
                {
                    Debug.LogWarning(
                        $"[ViewerFishEncounterController] Bridge rejected fish from {sourceSwarm.name}.",
                        this);
                }
            }

            if (!anySourceStillPeeling
                && !HasBridgeActivity()
                && totalTransferred > 0
                && stage4Convoy.IsEncounterConvoyComplete())
            {
                break;
            }

            encounterElapsed += Time.deltaTime;
            yield return null;
        }

        for (int i = 0; i < _bridgeSwarms.Count; i++)
        {
            VertexPathSwarmFollower sourceSwarm = splineConnection.ResolveSourceSwarm(groupB, i);
            sourceSwarm?.StopSwarmMotionOnly();
        }

        ProcessBridgeArrivals(ref totalTransferred, stage4Convoy);

        if (totalTransferred == 0)
        {
            Debug.LogWarning("[ViewerFishEncounterController] No fish transferred to Stage 4.", this);
        }

        while (encounterElapsed < maxConvoyDurationSeconds)
        {
            ProcessBridgeArrivals(ref totalTransferred, stage4Convoy);
            encounterElapsed += Time.deltaTime;

            if (stage4Convoy.IsEncounterConvoyComplete() && !HasBridgeActivity())
            {
                break;
            }

            yield return null;
        }

        ProcessBridgeArrivals(ref totalTransferred, stage4Convoy);
        yield return FinishEncounterRoutine(stage4Convoy);
        _encounterRoutine = null;
    }

    private bool TrySetupBridgeSwarms(
        IReadOnlyList<VertexPathSwarmFollower> groupB,
        BezierSpline stage4Spline,
        float stage4JunctionT)
    {
        CleanupBridgeInfrastructure();

        int sourceCount = splineConnection.GetSourceCount(groupB);
        if (sourceCount <= 0)
        {
            return false;
        }

        var bridgeRootGo = new GameObject("Stage4_Bridges");
        _bridgeRoot = bridgeRootGo.transform;
        _bridgeRoot.SetParent(transform, false);

        for (int sourceIndex = 0; sourceIndex < sourceCount; sourceIndex++)
        {
            VertexPathSwarmFollower sourceSwarm = splineConnection.ResolveSourceSwarm(groupB, sourceIndex);
            if (sourceSwarm == null)
            {
                _bridgeSwarms.Add(null);
                _sourcePeelStopped.Add(true);
                continue;
            }

            BezierSpline bridgeSpline = splineConnection.ResolveBridgeSpline(
                _bridgeRoot,
                groupB,
                sourceIndex,
                stage4Spline,
                stage4JunctionT,
                _runtimeBridges);

            if (bridgeSpline == null)
            {
                Debug.LogWarning(
                    $"[ViewerFishEncounterController] Missing bridge for source {sourceSwarm.name}.",
                    this);
                _bridgeSwarms.Add(null);
                _sourcePeelStopped.Add(true);
                continue;
            }

            string bridgeObjectName = $"BridgeTransit_{sourceSwarm.name}";
            var bridgeGo = new GameObject(bridgeObjectName);
            bridgeGo.transform.SetParent(_bridgeRoot, false);
            bridgeGo.SetActive(false);

            VertexPathSwarmFollower bridgeSwarm = bridgeGo.AddComponent<VertexPathSwarmFollower>();
            bridgeSwarm.BeginBridgeTransit(
                bridgeSpline,
                stage4PathSpeed,
                sourceSwarm.FishSpacing);

            bridgeGo.SetActive(true);
            _bridgeSwarms.Add(bridgeSwarm);
            _sourcePeelStopped.Add(false);
        }

        return _bridgeSwarms.Count > 0;
    }

    private void ProcessBridgeArrivals(ref int totalTransferred, VertexPathSwarmFollower stage4Convoy)
    {
        for (int i = 0; i < _bridgeSwarms.Count; i++)
        {
            VertexPathSwarmFollower bridgeSwarm = _bridgeSwarms[i];
            if (bridgeSwarm == null)
            {
                continue;
            }

            while (bridgeSwarm.TryDequeueBridgeArrival(out VertexPathSwarmFollower.BridgeArrival arrival))
            {
                if (maxEncounterFish > 0 && totalTransferred >= maxEncounterFish)
                {
                    break;
                }

                bool appendBehindConvoy = totalTransferred > 0;
                stage4Convoy.AcceptStage4FollowerDirectJoin(
                    arrival.Follower,
                    arrival.Snapshot,
                    appendBehindConvoy);
                totalTransferred++;
            }
        }
    }

    private bool HasBridgeActivity()
    {
        for (int i = 0; i < _bridgeSwarms.Count; i++)
        {
            VertexPathSwarmFollower bridgeSwarm = _bridgeSwarms[i];
            if (bridgeSwarm == null)
            {
                continue;
            }

            if (bridgeSwarm.HasBridgeFollowersInTransit() || bridgeSwarm.HasPendingBridgeArrivals)
            {
                return true;
            }
        }

        return false;
    }

    private void ConfigureStage4Homing(VertexPathSwarmFollower stage4Convoy)
    {
        if (stage4Convoy == null)
        {
            return;
        }

        stage4Convoy.ConfigureEncounterHoming(
            stage4HomingSpeed,
            stage4JoinDistance,
            stage4JoinTimeoutSeconds,
            stage4HomingTurnSpeed);
    }

    private void CleanupBridgeInfrastructure(VertexPathSwarmFollower stage4Convoy = null)
    {
        if (stage4Convoy != null)
        {
            for (int i = 0; i < _bridgeSwarms.Count; i++)
            {
                VertexPathSwarmFollower bridgeSwarm = _bridgeSwarms[i];
                bridgeSwarm?.ForceFlushAllBridgeFollowersToArrivals();
            }

            int drainCount = 0;
            ProcessBridgeArrivals(ref drainCount, stage4Convoy);
            stage4Convoy.ForceJoinAllPendingEncounterFollowers();
        }

        for (int i = 0; i < _bridgeSwarms.Count; i++)
        {
            if (_bridgeSwarms[i] != null)
            {
                _bridgeSwarms[i].StopBridgeTransit();
            }
        }

        _bridgeSwarms.Clear();
        _sourcePeelStopped.Clear();

        if (_bridgeRoot != null)
        {
            Destroy(_bridgeRoot.gameObject);
            _bridgeRoot = null;
        }

        for (int i = 0; i < _runtimeBridges.Count; i++)
        {
            if (_runtimeBridges[i] != null)
            {
                Destroy(_runtimeBridges[i].gameObject);
            }
        }

        _runtimeBridges.Clear();
    }

    private ViewerSpiralSplineRig ResolveStage4Rig()
    {
        if (spiralRig != null)
        {
            return spiralRig;
        }

        spiralRig = GetComponentInChildren<ViewerSpiralSplineRig>(true);
        if (spiralRig != null)
        {
            return spiralRig;
        }

        Transform searchRoot = transform.parent != null ? transform.parent : transform;
        spiralRig = searchRoot.GetComponentInChildren<ViewerSpiralSplineRig>(true);
        return spiralRig;
    }

    private VertexPathSwarmFollower ResolveStage4Convoy(ViewerSpiralSplineRig activeRig)
    {
        if (viewerConvoySwarm != null)
        {
            return viewerConvoySwarm;
        }

        return activeRig != null ? activeRig.ConvoySwarm : null;
    }

    private IEnumerator FinishEncounterRoutine(VertexPathSwarmFollower convoy)
    {
        CleanupBridgeInfrastructure(convoy);
        convoy?.StopSwarmImmediate();
        riverAmbience?.RequestFadeOut();
        yield break;
    }

    private void EnsureFishOrchestrator()
    {
        if (fishOrchestrator != null)
        {
            return;
        }

        fishOrchestrator = GetComponent<SplineFishGroupOrchestrator>();
    }

    private void EnsureSplineConnection()
    {
        if (splineConnection == null)
        {
            splineConnection = GetComponent<Stage4SplineConnection>();
        }

        if (splineConnection == null)
        {
            splineConnection = gameObject.AddComponent<Stage4SplineConnection>();
        }
    }

    private void EnsureViewer()
    {
        if (viewerTransform != null)
        {
            return;
        }

        Camera mainCamera = Camera.main;
        if (mainCamera != null)
        {
            viewerTransform = mainCamera.transform;
        }
    }
}
