using System.Collections.Generic;
using BezierSolution;
using UnityEngine;

/// <summary>
/// Builds a runtime Bezier spline: Stage 3 [0..join] → invisible bridge → Stage 4 [join..1].
/// </summary>
public static class Stage4CombinedPathBuilder
{
    private const int SplineMoveAccuracy = 12;

    public struct BuildResult
    {
        public BezierSpline Spline;
        public float Stage3SectionEndNormalizedT;
        public float CombinedStage4SectionStartNormalizedT;
        public float Stage3SourceJoinT;
        public float Stage4SourceJoinT;
    }

    public static BuildResult Build(
        Transform parent,
        BezierSpline stage3,
        float stage3JoinNormalizedT,
        BezierSpline stage4,
        float stage4JoinNormalizedT,
        int bridgePointCount = 2,
        int stage3SampleCount = 8,
        int stage4SampleCount = 10)
    {
        var result = new BuildResult
        {
            Spline = null,
            Stage3SectionEndNormalizedT = 0f,
            CombinedStage4SectionStartNormalizedT = 1f,
            Stage3SourceJoinT = Mathf.Clamp01(stage3JoinNormalizedT),
            Stage4SourceJoinT = Mathf.Clamp01(stage4JoinNormalizedT)
        };

        if (stage3 == null || stage4 == null || parent == null)
        {
            return result;
        }

        stage3SampleCount = Mathf.Max(2, stage3SampleCount);
        stage4SampleCount = Mathf.Max(2, stage4SampleCount);
        bridgePointCount = Mathf.Max(1, bridgePointCount);

        var worldPoints = new List<Vector3>();

        for (int i = 0; i <= stage3SampleCount; i++)
        {
            float sourceT = result.Stage3SourceJoinT * (i / (float)stage3SampleCount);
            worldPoints.Add(stage3.GetPoint(sourceT));
        }

        int stage3EndPointIndex = worldPoints.Count - 1;

        Vector3 bridgeStart = stage3.GetPoint(result.Stage3SourceJoinT);
        Vector3 bridgeEnd = stage4.GetPoint(result.Stage4SourceJoinT);
        Vector3 tangentStart = stage3.GetTangent(result.Stage3SourceJoinT);
        Vector3 tangentEnd = stage4.GetTangent(result.Stage4SourceJoinT);

        if (tangentStart.sqrMagnitude < 0.0001f)
        {
            tangentStart = bridgeEnd - bridgeStart;
        }

        if (tangentEnd.sqrMagnitude < 0.0001f)
        {
            tangentEnd = bridgeEnd - bridgeStart;
        }

        tangentStart.Normalize();
        tangentEnd.Normalize();

        float handleLength = Vector3.Distance(bridgeStart, bridgeEnd) * 0.35f;

        for (int b = 1; b <= bridgePointCount; b++)
        {
            float u = b / (float)(bridgePointCount + 1);
            worldPoints.Add(CubicHermite(
                bridgeStart,
                tangentStart * handleLength,
                bridgeEnd,
                tangentEnd * handleLength,
                u));
        }

        for (int i = 1; i <= stage4SampleCount; i++)
        {
            float sourceT = Mathf.Lerp(result.Stage4SourceJoinT, 1f, i / (float)stage4SampleCount);
            worldPoints.Add(stage4.GetPoint(sourceT));
        }

        if (worldPoints.Count < 2)
        {
            return result;
        }

        var pathObject = new GameObject("Stage4_CombinedEncounterPath");
        pathObject.transform.SetParent(parent, worldPositionStays: false);

        BezierSpline combined = pathObject.AddComponent<BezierSpline>();
        combined.Initialize(worldPoints.Count);

        for (int i = 0; i < worldPoints.Count; i++)
        {
            combined[i].position = worldPoints[i];
        }

        combined.autoConstructMode = SplineAutoConstructMode.Smooth1;
        combined.drawGizmos = false;
        combined.Refresh();

        result.Spline = combined;
        result.Stage3SectionEndNormalizedT = EstimateNormalizedTForPointIndex(combined, stage3EndPointIndex);
        int stage4StartPointIndex = stage3EndPointIndex + bridgePointCount + 1;
        result.CombinedStage4SectionStartNormalizedT =
            EstimateNormalizedTForPointIndex(combined, stage4StartPointIndex);
        return result;
    }

    /// <summary>
    /// Builds a short bridge-only Bezier from Stage 3 junction to Stage 4 junction (no path duplication).
    /// </summary>
    public static BezierSpline BuildBridgeOnly(
        Transform parent,
        BezierSpline stage3,
        float stage3JoinNormalizedT,
        BezierSpline stage4,
        float stage4JoinNormalizedT,
        int bridgePointCount = 4,
        string objectName = "Stage4_BridgePath")
    {
        if (stage3 == null || stage4 == null || parent == null)
        {
            return null;
        }

        bridgePointCount = Mathf.Max(2, bridgePointCount);
        float stage3JoinT = Mathf.Clamp01(stage3JoinNormalizedT);
        float stage4JoinT = Mathf.Clamp01(stage4JoinNormalizedT);

        Vector3 bridgeStart = stage3.GetPoint(stage3JoinT);
        Vector3 bridgeEnd = stage4.GetPoint(stage4JoinT);
        Vector3 tangentStart = stage3.GetTangent(stage3JoinT);
        Vector3 tangentEnd = stage4.GetTangent(stage4JoinT);

        if (tangentStart.sqrMagnitude < 0.0001f)
        {
            tangentStart = bridgeEnd - bridgeStart;
        }

        if (tangentEnd.sqrMagnitude < 0.0001f)
        {
            tangentEnd = bridgeEnd - bridgeStart;
        }

        tangentStart.Normalize();
        tangentEnd.Normalize();

        float handleLength = Vector3.Distance(bridgeStart, bridgeEnd) * 0.35f;
        var worldPoints = new List<Vector3> { bridgeStart };

        for (int b = 1; b <= bridgePointCount; b++)
        {
            float u = b / (float)(bridgePointCount + 1);
            worldPoints.Add(CubicHermite(
                bridgeStart,
                tangentStart * handleLength,
                bridgeEnd,
                tangentEnd * handleLength,
                u));
        }

        worldPoints.Add(bridgeEnd);

        var pathObject = new GameObject(objectName);
        pathObject.transform.SetParent(parent, worldPositionStays: false);

        BezierSpline bridge = pathObject.AddComponent<BezierSpline>();
        bridge.Initialize(worldPoints.Count);

        for (int i = 0; i < worldPoints.Count; i++)
        {
            bridge[i].position = worldPoints[i];
        }

        bridge.autoConstructMode = SplineAutoConstructMode.Smooth1;
        bridge.drawGizmos = false;
        bridge.Refresh();
        return bridge;
    }

    private static Vector3 CubicHermite(
        Vector3 p0,
        Vector3 m0,
        Vector3 p1,
        Vector3 m1,
        float t)
    {
        float t2 = t * t;
        float t3 = t2 * t;
        return (2f * t3 - 3f * t2 + 1f) * p0
            + (t3 - 2f * t2 + t) * m0
            + (-2f * t3 + 3f * t2) * p1
            + (t3 - t2) * m1;
    }

    private static float EstimateNormalizedTForPointIndex(BezierSpline spline, int pointIndex)
    {
        if (spline == null || spline.Count < 2)
        {
            return 0f;
        }

        return Mathf.Clamp01(pointIndex / (float)(spline.Count - 1));
    }

    /// <summary>
    /// Maps Stage 3 loop T to combined path T. Fish before the junction spread along the Stage 3
    /// section; fish at/past the junction trail backward from the bridge (no single-point clump).
    /// </summary>
    public static float MapStage3NormalizedTToCombined(
        float stage3NormalizedT,
        float stage3JoinNormalizedT,
        float combinedStage3SectionEndT)
    {
        float join = Mathf.Clamp(stage3JoinNormalizedT, 0.0001f, 1f);
        float sectionEnd = Mathf.Clamp01(combinedStage3SectionEndT);
        float t = Mathf.Repeat(stage3NormalizedT, 1f);

        if (t < join - 0.0005f)
        {
            return (t / join) * sectionEnd;
        }

        float tailSpan = 1f - join;
        if (tailSpan <= 0.0001f)
        {
            return sectionEnd;
        }

        const float pastJunctionTrailFraction = 0.2f;
        float pastJunction = Mathf.Clamp01((t - join) / tailSpan);
        return sectionEnd - pastJunction * sectionEnd * pastJunctionTrailFraction;
    }

    /// <summary>
    /// Maps every fish from its own Stage 3 T; past-junction fish are spaced along the combined path.
    /// </summary>
    public static void MapEachFishToCombinedPath(
        BezierSpline combinedSpline,
        List<VertexPathSwarmFollower.Stage4HandoffEntry> orderedFish,
        float stage3JoinNormalizedT,
        float combinedStage3SectionEndT,
        float fishSpacing)
    {
        if (orderedFish == null || orderedFish.Count == 0)
        {
            return;
        }

        float join = Mathf.Clamp(stage3JoinNormalizedT, 0.0001f, 1f);
        float sectionEnd = Mathf.Clamp01(combinedStage3SectionEndT);
        var pastJunctionIndices = new List<int>();

        for (int i = 0; i < orderedFish.Count; i++)
        {
            VertexPathSwarmFollower.Stage4HandoffEntry entry = orderedFish[i];
            float sourceT = Mathf.Repeat(entry.SourceNormalizedT, 1f);

            if (sourceT < join - 0.0005f)
            {
                entry.CombinedNormalizedT = (sourceT / join) * sectionEnd;
                orderedFish[i] = entry;
            }
            else
            {
                pastJunctionIndices.Add(i);
            }
        }

        if (pastJunctionIndices.Count == 0)
        {
            return;
        }

        pastJunctionIndices.Sort((a, b) =>
        {
            float ta = Mathf.Repeat(orderedFish[a].SourceNormalizedT, 1f);
            float tb = Mathf.Repeat(orderedFish[b].SourceNormalizedT, 1f);
            return ta.CompareTo(tb);
        });

        float trailT = sectionEnd;
        for (int i = 0; i < pastJunctionIndices.Count; i++)
        {
            int index = pastJunctionIndices[i];
            VertexPathSwarmFollower.Stage4HandoffEntry entry = orderedFish[index];
            entry.CombinedNormalizedT = Mathf.Clamp01(trailT);
            orderedFish[index] = entry;

            if (i >= pastJunctionIndices.Count - 1 || combinedSpline == null || fishSpacing <= 0f)
            {
                continue;
            }

            float nextTrailT = trailT;
            combinedSpline.MoveAlongSpline(ref nextTrailT, -fishSpacing, SplineMoveAccuracy);
            trailT = Mathf.Max(0f, nextTrailT);
        }
    }
}
