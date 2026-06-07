using System.Collections.Generic;
using BezierSolution;
using UnityEngine;

/// <summary>
/// Editor-placed junction(s) between Stage 3 source paths and the Stage 4 encounter spline.
/// Draws bridge gizmo lines in the Scene view.
/// </summary>
public class Stage4SplineConnection : MonoBehaviour
{
    [Header("Stage 4 (encounter path)")]
    [Tooltip("Stage 4 / ViewerEncounterPath spline. When empty, resolved from Stage 4 rig at runtime.")]
    [SerializeField] private BezierSpline stage4Spline;
    [Tooltip("Normalized T on Stage 4 where the bridge arrives. Default 0 = path start (top of spiral).")]
    [Range(0f, 1f)]
    [SerializeField] private float stage4ConnectionNormalizedT;
    [Tooltip("When on, bridge target is the highest world-Y point on Stage 4 (overrides Connection T).")]
    [SerializeField] private bool useTopOfStage4AsBridgeTarget = true;

    [Header("Sources (Group B paths)")]
    [Tooltip("Per-source junction + optional bridge. When empty, legacy primary fields + Group B list are used.")]
    [SerializeField] private List<Stage4SourceConnection> sourceConnections = new List<Stage4SourceConnection>();

    [Header("Primary source (legacy)")]
    [SerializeField] private VertexPathSwarmFollower stage3Swarm;
    [SerializeField] private BezierSpline stage3Spline;
    [Range(0.05f, 1f)]
    [SerializeField] private float stage3ConnectionNormalizedT = 0.88f;

    [Header("Runtime bridge build")]
    [SerializeField] private int bridgePointCount = 4;

    public float Stage3ConnectionNormalizedT => stage3ConnectionNormalizedT;
    public float Stage4ConnectionNormalizedT => stage4ConnectionNormalizedT;
    public int BridgePointCount => bridgePointCount;

    public float ResolveStage4ConnectionNormalizedT()
    {
        BezierSpline stage4 = stage4Spline;
        if (useTopOfStage4AsBridgeTarget && stage4 != null)
        {
            return FindHighestNormalizedT(stage4);
        }

        return stage4ConnectionNormalizedT;
    }

    public static float FindHighestNormalizedT(BezierSpline spline, int samples = 64)
    {
        if (spline == null || samples < 2)
        {
            return 0f;
        }

        float bestT = 0f;
        float bestY = float.MinValue;
        for (int i = 0; i <= samples; i++)
        {
            float t = i / (float)samples;
            float y = spline.GetPoint(t).y;
            if (y > bestY)
            {
                bestY = y;
                bestT = t;
            }
        }

        return bestT;
    }

    public BezierSpline ResolveStage4Spline(ViewerSpiralSplineRig rig, VertexPathSwarmFollower convoySwarm)
    {
        if (stage4Spline != null)
        {
            return stage4Spline;
        }

        if (convoySwarm != null && convoySwarm.Spline != null)
        {
            return convoySwarm.Spline;
        }

        return rig != null ? rig.Spline : null;
    }

    public void ConfigureFromEncounter(
        IReadOnlyList<VertexPathSwarmFollower> groupB,
        ViewerSpiralSplineRig rig,
        VertexPathSwarmFollower convoySwarm)
    {
        if (stage4Spline == null)
        {
            stage4Spline = ResolveStage4Spline(rig, convoySwarm);
        }

        if (sourceConnections == null)
        {
            sourceConnections = new List<Stage4SourceConnection>();
        }

        if (sourceConnections.Count == 0)
        {
            EnsureLegacyPrimarySource(groupB);
        }

        SyncSourceConnectionsFromGroupB(groupB);
    }

    public int GetSourceCount(IReadOnlyList<VertexPathSwarmFollower> groupB)
    {
        if (sourceConnections != null && sourceConnections.Count > 0)
        {
            return sourceConnections.Count;
        }

        return groupB != null ? groupB.Count : 0;
    }

    public Stage4SourceConnection ResolveSourceConnection(IReadOnlyList<VertexPathSwarmFollower> groupB, int sourceIndex)
    {
        if (sourceConnections != null && sourceIndex >= 0 && sourceIndex < sourceConnections.Count)
        {
            Stage4SourceConnection configured = sourceConnections[sourceIndex];
            if (configured != null)
            {
                FillMissingSourceFields(configured, groupB, sourceIndex);
                return configured;
            }
        }

        var fallback = new Stage4SourceConnection
        {
            stage3ConnectionNormalizedT = stage3ConnectionNormalizedT,
            appendBehindConvoy = sourceIndex > 0
        };

        FillMissingSourceFields(fallback, groupB, sourceIndex);
        return fallback;
    }

    public VertexPathSwarmFollower ResolveSourceSwarm(
        IReadOnlyList<VertexPathSwarmFollower> groupB,
        int sourceIndex)
    {
        Stage4SourceConnection connection = ResolveSourceConnection(groupB, sourceIndex);
        if (connection.stage3Swarm != null)
        {
            return connection.stage3Swarm;
        }

        if (groupB != null && sourceIndex >= 0 && sourceIndex < groupB.Count)
        {
            return groupB[sourceIndex];
        }

        return null;
    }

    public BezierSpline ResolveBridgeSpline(
        Transform bridgeRoot,
        IReadOnlyList<VertexPathSwarmFollower> groupB,
        int sourceIndex,
        BezierSpline stage4,
        float stage4JoinT,
        List<BezierSpline> runtimeBridges)
    {
        Stage4SourceConnection connection = ResolveSourceConnection(groupB, sourceIndex);
        if (connection.bridgeSpline != null)
        {
            return connection.bridgeSpline;
        }

        BezierSpline stage3 = ResolveSourceStage3Spline(connection, groupB, sourceIndex);
        if (stage3 == null || stage4 == null || bridgeRoot == null)
        {
            return null;
        }

        VertexPathSwarmFollower swarm = ResolveSourceSwarm(groupB, sourceIndex);
        string bridgeName = swarm != null ? $"Stage4_Bridge_{swarm.name}" : $"Stage4_Bridge_{sourceIndex}";

        BezierSpline runtimeBridge = Stage4CombinedPathBuilder.BuildBridgeOnly(
            bridgeRoot,
            stage3,
            connection.stage3ConnectionNormalizedT,
            stage4,
            stage4JoinT,
            bridgePointCount,
            bridgeName);

        if (runtimeBridge != null && runtimeBridges != null)
        {
            runtimeBridges.Add(runtimeBridge);
        }

        return runtimeBridge;
    }

    private void EnsureLegacyPrimarySource(IReadOnlyList<VertexPathSwarmFollower> groupB)
    {
        var primary = new Stage4SourceConnection
        {
            stage3Swarm = stage3Swarm,
            stage3Spline = stage3Spline,
            stage3ConnectionNormalizedT = stage3ConnectionNormalizedT,
            appendBehindConvoy = false
        };

        FillMissingSourceFields(primary, groupB, 0);
        sourceConnections.Add(primary);

        if (groupB == null)
        {
            return;
        }

        for (int i = 1; i < groupB.Count; i++)
        {
            if (groupB[i] == null)
            {
                continue;
            }

            sourceConnections.Add(new Stage4SourceConnection
            {
                stage3Swarm = groupB[i],
                stage3ConnectionNormalizedT = stage3ConnectionNormalizedT,
                appendBehindConvoy = true
            });
        }
    }

    private void SyncSourceConnectionsFromGroupB(IReadOnlyList<VertexPathSwarmFollower> groupB)
    {
        if (groupB == null)
        {
            return;
        }

        for (int i = 0; i < sourceConnections.Count; i++)
        {
            FillMissingSourceFields(sourceConnections[i], groupB, i);
        }

        for (int i = sourceConnections.Count; i < groupB.Count; i++)
        {
            if (groupB[i] == null)
            {
                continue;
            }

            sourceConnections.Add(new Stage4SourceConnection
            {
                stage3Swarm = groupB[i],
                stage3ConnectionNormalizedT = stage3ConnectionNormalizedT,
                appendBehindConvoy = i > 0
            });
        }
    }

    private static void FillMissingSourceFields(
        Stage4SourceConnection connection,
        IReadOnlyList<VertexPathSwarmFollower> groupB,
        int sourceIndex)
    {
        if (connection == null)
        {
            return;
        }

        if (connection.stage3Swarm == null
            && groupB != null
            && sourceIndex >= 0
            && sourceIndex < groupB.Count)
        {
            connection.stage3Swarm = groupB[sourceIndex];
        }

        if (connection.stage3Spline == null && connection.stage3Swarm != null)
        {
            connection.stage3Spline = connection.stage3Swarm.Spline;
        }

        if (sourceIndex > 0)
        {
            connection.appendBehindConvoy = true;
        }
    }

    private static BezierSpline ResolveSourceStage3Spline(
        Stage4SourceConnection connection,
        IReadOnlyList<VertexPathSwarmFollower> groupB,
        int sourceIndex)
    {
        if (connection == null)
        {
            return null;
        }

        if (connection.stage3Spline != null)
        {
            return connection.stage3Spline;
        }

        if (connection.stage3Swarm != null && connection.stage3Swarm.Spline != null)
        {
            return connection.stage3Swarm.Spline;
        }

        if (groupB != null && sourceIndex >= 0 && sourceIndex < groupB.Count && groupB[sourceIndex] != null)
        {
            return groupB[sourceIndex].Spline;
        }

        return null;
    }

    private void OnValidate()
    {
        stage3ConnectionNormalizedT = Mathf.Clamp(stage3ConnectionNormalizedT, 0.05f, 1f);
        stage4ConnectionNormalizedT = Mathf.Clamp01(stage4ConnectionNormalizedT);
        bridgePointCount = Mathf.Max(2, bridgePointCount);

        if (sourceConnections == null)
        {
            sourceConnections = new List<Stage4SourceConnection>();
        }

        for (int i = 0; i < sourceConnections.Count; i++)
        {
            if (sourceConnections[i] == null)
            {
                continue;
            }

            sourceConnections[i].stage3ConnectionNormalizedT = Mathf.Clamp(
                sourceConnections[i].stage3ConnectionNormalizedT,
                0.05f,
                1f);
        }
    }

#if UNITY_EDITOR
    private void OnDrawGizmosSelected()
    {
        BezierSpline stage4 = stage4Spline;
        if (stage4 == null)
        {
            return;
        }

        float stage4JoinT = ResolveStage4ConnectionNormalizedT();
        Vector3 stage4Point = stage4.GetPoint(stage4JoinT);

        if (sourceConnections != null && sourceConnections.Count > 0)
        {
            for (int i = 0; i < sourceConnections.Count; i++)
            {
                Stage4SourceConnection connection = sourceConnections[i];
                if (connection == null)
                {
                    continue;
                }

                BezierSpline stage3 = connection.stage3Spline;
                if (stage3 == null && connection.stage3Swarm != null)
                {
                    stage3 = connection.stage3Swarm.Spline;
                }

                if (stage3 == null)
                {
                    continue;
                }

                DrawSourceBridgeGizmo(stage3, connection.stage3ConnectionNormalizedT, stage4Point);
            }

            return;
        }

        BezierSpline legacyStage3 = stage3Spline;
        if (legacyStage3 == null && stage3Swarm != null)
        {
            legacyStage3 = stage3Swarm.Spline;
        }

        if (legacyStage3 != null)
        {
            DrawSourceBridgeGizmo(legacyStage3, stage3ConnectionNormalizedT, stage4Point);
        }
    }

    private static void DrawSourceBridgeGizmo(BezierSpline stage3, float stage3JoinT, Vector3 stage4Point)
    {
        Vector3 from = stage3.GetPoint(stage3JoinT);
        Gizmos.color = new Color(1f, 0.25f, 0.15f, 0.95f);
        Gizmos.DrawLine(from, stage4Point);
        Gizmos.DrawWireSphere(from, 0.35f);
        Gizmos.DrawWireSphere(stage4Point, 0.35f);
    }
#endif
}
