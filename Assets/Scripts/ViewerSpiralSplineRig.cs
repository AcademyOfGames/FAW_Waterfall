using BezierSolution;
using UnityEngine;

/// <summary>
/// Builds a world-space encounter path: long descent from Stage 3 toward the viewer,
/// then a horizontal spiral around the camera. Supports hand-placed spline points in the editor.
/// </summary>
[ExecuteAlways]
public class ViewerSpiralSplineRig : MonoBehaviour
{
    [SerializeField] private BezierSpline spline;
    [SerializeField] private VertexPathSwarmFollower convoySwarm;

    [Header("Spline Source")]
    [Tooltip("When on and the spline already has 2+ points, runtime keeps your hand-placed path. Off = full procedural descent + spiral (default).")]
    [SerializeField] private bool useManualSplinePoints;

    [Header("Path Layout")]
    [Tooltip("Fraction of the path used for the descending approach before the camera spiral.")]
    [Range(0.2f, 0.85f)]
    [SerializeField] private float descentSectionFraction = 0.55f;
    [SerializeField] private int pathPointCount = 28;

    [Header("Descent")]
    [SerializeField] private float preSpiralHeightMeters = 6f;
    [SerializeField] private float preSpiralForwardMeters = 4f;
    [Tooltip("Lateral S-curve amplitude during the descent.")]
    [SerializeField] private float descentSwayMeters = 5f;
    [SerializeField] private float descentSwayWaves = 1.25f;

    [Header("Camera Spiral")]
    [SerializeField] private float spiralTurns = 2.5f;
    [SerializeField] private float spiralStartRadiusMeters = 7f;
    [SerializeField] private float spiralEndRadiusMeters = 1.4f;
    [SerializeField] private float spiralHeightOffsetMeters = 0.35f;
    [SerializeField] private float spiralVerticalDropMeters = 1.2f;

    [Header("Convoy Defaults")]
    [Tooltip("When on, Convoy Defaults below are pushed into Vertex Path Swarm Follower on preview/prepare. When off, edit the follower directly.")]
    [SerializeField] private bool applyConvoyDefaultsToFollower;
    [SerializeField] private float convoyPathSpeed = 8f;
    [SerializeField] private float convoyHeadGap = 0.75f;
    [SerializeField] private float convoyFishSpacing = 1.5f;
    [SerializeField] private float convoyTubeRadius = 0.5f;

    public BezierSpline Spline => spline;
    public VertexPathSwarmFollower ConvoySwarm => convoySwarm;
    public bool UseManualSplinePoints => useManualSplinePoints;

    public void SetUseManualSplinePoints(bool value)
    {
        useManualSplinePoints = value;
    }

    private void OnValidate()
    {
        descentSectionFraction = Mathf.Clamp(descentSectionFraction, 0.2f, 0.85f);
        pathPointCount = Mathf.Clamp(pathPointCount, 8, 64);
        preSpiralHeightMeters = Mathf.Max(0f, preSpiralHeightMeters);
        preSpiralForwardMeters = Mathf.Max(0f, preSpiralForwardMeters);
        descentSwayMeters = Mathf.Max(0f, descentSwayMeters);
        descentSwayWaves = Mathf.Max(0.1f, descentSwayWaves);
        spiralTurns = Mathf.Max(0.5f, spiralTurns);
        spiralStartRadiusMeters = Mathf.Max(0.5f, spiralStartRadiusMeters);
        spiralEndRadiusMeters = Mathf.Max(0.1f, spiralEndRadiusMeters);
        convoyPathSpeed = Mathf.Max(0.1f, convoyPathSpeed);
        convoyHeadGap = Mathf.Max(0f, convoyHeadGap);
        convoyFishSpacing = Mathf.Max(0.05f, convoyFishSpacing);
        convoyTubeRadius = Mathf.Max(0f, convoyTubeRadius);

#if UNITY_EDITOR
        if (!Application.isPlaying && applyConvoyDefaultsToFollower)
        {
            SyncConvoyDefaultsToFollower();
        }
#endif
    }

    private void OnEnable()
    {
        EnsureComponents();
        EnableSplineGizmos();

#if UNITY_EDITOR
        if (!Application.isPlaying)
        {
            if (HasManualSplinePoints())
            {
                spline.Refresh();
            }
        }
#endif
    }

    /// <summary>Prepares the encounter path at runtime (saved points or procedural fallback).</summary>
    public void PrepareEncounterPath(Vector3 pathStartWorld, Transform viewer)
    {
        EnsureComponents();
        EnableSplineGizmos();

        if (HasManualSplinePoints())
        {
            spline.Refresh();
            ConfigureConvoySwarm();
            return;
        }

        if (useManualSplinePoints)
        {
            Debug.LogWarning(
                "[ViewerSpiralSplineRig] Manual spline mode is on but no points exist. " +
                "Use Preview And Lock Manual in the inspector.",
                this);
            return;
        }

        BuildProceduralEncounterPath(pathStartWorld, viewer);
    }

    /// <summary>Legacy entry point used by older callers.</summary>
    public void BuildWorldEncounterPath(Vector3 pathStartWorld, Transform viewer)
    {
        PrepareEncounterPath(pathStartWorld, viewer);
    }

    /// <summary>Generates a procedural preview path (editor only — does not run at runtime activation).</summary>
    public void PreviewProceduralPath(Transform viewer, Vector3 pathStartWorld)
    {
#if UNITY_EDITOR
        EnsureComponents();
        BuildProceduralEncounterPath(pathStartWorld, viewer);
        EnableSplineGizmos();
#else
        Debug.LogWarning("[ViewerSpiralSplineRig] PreviewProceduralPath is editor-only.", this);
#endif
    }

    public bool HasManualSplinePoints()
    {
        return spline != null && spline.Count >= 2;
    }

    public float GetNearestNormalizedT(Vector3 worldPosition)
    {
        if (spline == null)
        {
            return 0f;
        }

        spline.FindNearestPointTo(worldPosition, out float normalizedT);
        return Mathf.Clamp01(normalizedT);
    }

    public Vector3 GetNearestWorldPosition(Vector3 worldPosition)
    {
        if (spline == null)
        {
            return worldPosition;
        }

        return spline.FindNearestPointTo(worldPosition);
    }

    private void BuildProceduralEncounterPath(Vector3 pathStartWorld, Transform viewer)
    {
        EnsureComponents();
        ClearSplinePoints();
        spline.autoConstructMode = SplineAutoConstructMode.Smooth1;

        transform.SetPositionAndRotation(Vector3.zero, Quaternion.identity);

        int pointCount = Mathf.Max(2, pathPointCount);
        for (int i = 0; i < pointCount; i++)
        {
            float t = pointCount <= 1 ? 0f : i / (float)(pointCount - 1);
            BezierPoint point = spline.InsertNewPointAt(i);
            point.position = ComputeWorldPathPoint(t, pathStartWorld, viewer);
        }

        spline.Refresh();
        ConfigureConvoySwarm();
    }

    private void EnsureComponents()
    {
        if (spline == null)
        {
            spline = GetComponent<BezierSpline>();
        }

        if (spline == null)
        {
            spline = gameObject.AddComponent<BezierSpline>();
        }

        if (convoySwarm == null)
        {
            convoySwarm = GetComponent<VertexPathSwarmFollower>();
        }

        if (convoySwarm == null)
        {
            convoySwarm = gameObject.AddComponent<VertexPathSwarmFollower>();
        }
    }

    private void EnableSplineGizmos()
    {
#if UNITY_EDITOR
        if (Application.isPlaying || spline == null)
        {
            return;
        }

        spline.drawGizmos = true;
#endif
    }

    private void ClearSplinePoints()
    {
        BezierPoint[] existingPoints = GetComponentsInChildren<BezierPoint>(true);
        for (int i = 0; i < existingPoints.Length; i++)
        {
            if (existingPoints[i] == null)
            {
                continue;
            }

#if UNITY_EDITOR
            if (!Application.isPlaying)
            {
                DestroyImmediate(existingPoints[i].gameObject);
                continue;
            }
#endif
            Destroy(existingPoints[i].gameObject);
        }

        if (spline != null)
        {
            spline.Refresh();
        }
    }

    private Vector3 ComputeWorldPathPoint(float t, Vector3 pathStartWorld, Transform viewer)
    {
        Vector3 cameraPosition = viewer != null ? viewer.position : pathStartWorld;
        Vector3 cameraForward = viewer != null ? viewer.forward : Vector3.forward;
        Vector3 cameraRight = viewer != null ? viewer.right : Vector3.right;
        cameraForward.y = 0f;
        if (cameraForward.sqrMagnitude < 0.0001f)
        {
            cameraForward = Vector3.forward;
        }
        else
        {
            cameraForward.Normalize();
        }

        if (cameraRight.sqrMagnitude < 0.0001f)
        {
            cameraRight = Vector3.right;
        }
        else
        {
            cameraRight.Normalize();
        }

        if (t <= descentSectionFraction)
        {
            float descentT = descentSectionFraction <= 0f ? 0f : t / descentSectionFraction;
            float eased = Mathf.SmoothStep(0f, 1f, descentT);
            Vector3 preSpiral = cameraPosition
                + Vector3.up * preSpiralHeightMeters
                + cameraForward * preSpiralForwardMeters;
            Vector3 position = Vector3.Lerp(pathStartWorld, preSpiral, eased);

            Vector3 descentDirection = preSpiral - pathStartWorld;
            descentDirection.y = 0f;
            Vector3 lateral = Vector3.Cross(Vector3.up, descentDirection.sqrMagnitude > 0.0001f
                ? descentDirection.normalized
                : cameraRight);
            if (lateral.sqrMagnitude < 0.0001f)
            {
                lateral = cameraRight;
            }

            float sway = Mathf.Sin(eased * Mathf.PI * descentSwayWaves) * descentSwayMeters;
            position += lateral.normalized * sway;
            return position;
        }

        float spiralT = (t - descentSectionFraction) / Mathf.Max(0.0001f, 1f - descentSectionFraction);
        float spiralEased = Mathf.SmoothStep(0f, 1f, spiralT);
        float angle = spiralEased * spiralTurns * Mathf.PI * 2f;
        float radius = Mathf.Lerp(spiralStartRadiusMeters, spiralEndRadiusMeters, spiralEased);
        Vector3 spiralCenter = cameraPosition + Vector3.up * spiralHeightOffsetMeters;

        return spiralCenter
            + cameraRight * (Mathf.Cos(angle) * radius)
            + cameraForward * (Mathf.Sin(angle) * radius)
            - Vector3.up * (spiralVerticalDropMeters * spiralEased);
    }

    public void SyncConvoyDefaultsToFollower()
    {
        EnsureComponents();
        if (convoySwarm == null || spline == null)
        {
            return;
        }

        convoySwarm.WireEncounterConvoy(spline);
        convoySwarm.ApplyEncounterConvoyDefaults(
            convoyPathSpeed,
            convoyHeadGap,
            convoyFishSpacing,
            convoyTubeRadius);
    }

    private void ConfigureConvoySwarm()
    {
        if (convoySwarm == null || spline == null)
        {
            return;
        }

        convoySwarm.WireEncounterConvoy(spline);

        if (applyConvoyDefaultsToFollower)
        {
            convoySwarm.ApplyEncounterConvoyDefaults(
                convoyPathSpeed,
                convoyHeadGap,
                convoyFishSpacing,
                convoyTubeRadius);
        }
    }
}
