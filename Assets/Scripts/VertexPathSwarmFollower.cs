using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// Moves a list of objects around a looping path defined by mesh vertices.
/// Followers are offset and softly damped to create a fish-like swarm feel.
/// </summary>
public class VertexPathSwarmFollower : MonoBehaviour
{
    [Header("Path")]
    [SerializeField] private MeshFilter pathMeshFilter;
    [SerializeField] private bool useMeshWorldTransform = true;

    [Header("Followers")]
    [SerializeField] private List<Transform> followers = new List<Transform>();

    [Header("Loop Timing")]
    [Tooltip("How long it takes for one full loop around the path.")]
    [SerializeField] private float loopDurationSeconds = 30f;
    [Tooltip("Distance offset between neighbors as a fraction of the full loop.")]
    [Range(0f, 1f)]
    [SerializeField] private float followerSpacingNormalized = 0.06f;

    [Header("Swarm Motion")]
    [Tooltip("How quickly each fish catches up to its target.")]
    [SerializeField] private float followSmoothTime = 0.35f;
    [Tooltip("Max movement speed while smoothing toward target.")]
    [SerializeField] private float maxFollowSpeed = 20f;
    [Tooltip("Side-to-side random offset from path center.")]
    [SerializeField] private float lateralJitter = 0.8f;
    [Tooltip("Up/down random offset from path center.")]
    [SerializeField] private float verticalJitter = 0.5f;
    [Tooltip("Oscillation speed for fish-like wobble.")]
    [SerializeField] private float wobbleSpeed = 1.1f;
    [SerializeField] private bool orientToMovement = true;
    [SerializeField] private float turnSpeed = 6f;
    [Tooltip("Minimum world-space distance to keep between followers.")]
    [SerializeField] private float minimumFollowerDistance = 0.75f;
    [Header("Directional Vertex Following")]
    [Tooltip("How close a fish must get to a target vertex before selecting a new forward one.")]
    [SerializeField] private float vertexReachedDistance = 1.5f;
    [Tooltip("How many vertices ahead to scan when selecting the closest forward target.")]
    [SerializeField] private int forwardSearchVertices = 12;
    [Header("Debug")]
    [SerializeField] private bool enableDebugLogs = true;
    [SerializeField] private float debugLogIntervalSeconds = 1f;
    [SerializeField] private bool visualizeTargetWithRedCube = true;
    [SerializeField] private float debugCubeSize = 0.75f;
    [Header("Start Control")]
    [SerializeField] private bool startSwarmOnAwake = true;

    private readonly List<Vector3> _pathPoints = new List<Vector3>();
    private readonly List<float> _segmentStartDistances = new List<float>();
    private readonly List<FollowerState> _states = new List<FollowerState>();
    private float _pathLength;
    private float _startTime;
    private bool _isSwarmActive;
    private float _nextDebugLogTime;
    private Transform _debugTargetCube;

    private class FollowerState
    {
        public Transform Transform;
        public Vector3 Velocity;
        public float Phase;
        public Vector2 JitterSeed;
        public int CurrentTargetIndex;
    }

    private void Awake()
    {
        RebuildPathCache();
        InitializeFollowerStates();
        _startTime = Time.time;
        _isSwarmActive = startSwarmOnAwake;
        _nextDebugLogTime = 0f;
        LogDebug(
            "Awake: active=" + _isSwarmActive +
            ", loopDuration=" + loopDurationSeconds.ToString("F2") +
            ", pathPoints=" + _pathPoints.Count +
            ", pathLength=" + _pathLength.ToString("F3") +
            ", followers=" + followers.Count +
            ", states=" + _states.Count
        );
    }

    private void OnValidate()
    {
        loopDurationSeconds = Mathf.Max(0.01f, loopDurationSeconds);
        followSmoothTime = Mathf.Max(0.01f, followSmoothTime);
        maxFollowSpeed = Mathf.Max(0.01f, maxFollowSpeed);
        wobbleSpeed = Mathf.Max(0f, wobbleSpeed);
        followerSpacingNormalized = Mathf.Clamp01(followerSpacingNormalized);
        minimumFollowerDistance = Mathf.Max(0f, minimumFollowerDistance);
        debugLogIntervalSeconds = Mathf.Max(0.1f, debugLogIntervalSeconds);
        vertexReachedDistance = Mathf.Max(0.05f, vertexReachedDistance);
        forwardSearchVertices = Mathf.Max(1, forwardSearchVertices);
        debugCubeSize = Mathf.Max(0.05f, debugCubeSize);
    }

    private void Update()
    {
        if (!_isSwarmActive)
        {
            LogDebug("Update skip: swarm inactive (Start Swarm On Awake is off unless activated by script/call).");
            return;
        }

        if (_pathPoints.Count < 2)
        {
            LogDebug("Update skip: path has fewer than 2 points.");
            return;
        }

        if (followers.Count == 0)
        {
            LogDebug("Update skip: no followers assigned.");
            return;
        }

        if (_pathLength <= 0.001f)
        {
            LogDebug("Update skip: path length near zero.");
            return;
        }

        if (_states.Count != followers.Count)
        {
            InitializeFollowerStates();
        }

        float elapsed = Time.time - _startTime;
        Vector3 markerPosition = Vector3.zero;
        bool hasMarkerPosition = false;

        for (int i = 0; i < _states.Count; i++)
        {
            FollowerState state = _states[i];
            if (state.Transform == null)
            {
                continue;
            }

            if (state.CurrentTargetIndex < 0 || state.CurrentTargetIndex >= _pathPoints.Count)
            {
                state.CurrentTargetIndex = FindClosestVertexIndex(state.Transform.position);
            }

            int targetIndex = state.CurrentTargetIndex;
            Vector3 targetVertex = _pathPoints[targetIndex];
            int nextIndex = (targetIndex + 1) % _pathPoints.Count;
            Vector3 tangent = (_pathPoints[nextIndex] - targetVertex).normalized;

            float distanceToTarget = Vector3.Distance(state.Transform.position, targetVertex);
            if (distanceToTarget <= vertexReachedDistance)
            {
                state.CurrentTargetIndex = FindClosestForwardVertexIndex(
                    state.Transform.position,
                    targetIndex,
                    forwardSearchVertices
                );
                targetIndex = state.CurrentTargetIndex;
                targetVertex = _pathPoints[targetIndex];
                nextIndex = (targetIndex + 1) % _pathPoints.Count;
                tangent = (_pathPoints[nextIndex] - targetVertex).normalized;
            }

            if (i < 2)
            {
                LogDebug(
                    "Follower[" + i + "] targetIndex=" + targetIndex +
                    ", distanceToTarget=" + distanceToTarget.ToString("F2") +
                    ", targetVertex=" + targetVertex.ToString("F2")
                );
            }
            Vector3 up = Vector3.up;
            if (tangent.sqrMagnitude > 0.0001f)
            {
                Vector3 right = Vector3.Cross(up, tangent.normalized);
                if (right.sqrMagnitude > 0.0001f)
                {
                    right.Normalize();
                    up = Vector3.Cross(tangent.normalized, right).normalized;
                }
            }

            float wobble = elapsed * wobbleSpeed + state.Phase;
            float side = Mathf.Sin(wobble + state.JitterSeed.x) * lateralJitter;
            float height = Mathf.Cos(wobble * 0.85f + state.JitterSeed.y) * verticalJitter;
            Vector3 offset = (Vector3.Cross(Vector3.up, tangent.normalized).normalized * side) + (up * height);
            if (!float.IsFinite(offset.x) || !float.IsFinite(offset.y) || !float.IsFinite(offset.z))
            {
                offset = new Vector3(side, height, 0f);
            }

            Vector3 targetPosition = targetVertex + offset;
            Vector3 oldPosition = state.Transform.position;
            state.Transform.position = Vector3.SmoothDamp(
                oldPosition,
                targetPosition,
                ref state.Velocity,
                followSmoothTime,
                maxFollowSpeed
            );

            if (orientToMovement)
            {
                Vector3 movement = state.Transform.position - oldPosition;
                if (movement.sqrMagnitude > 0.00001f)
                {
                    Quaternion targetRotation = Quaternion.LookRotation(movement.normalized, Vector3.up);
                    state.Transform.rotation = Quaternion.Slerp(
                        state.Transform.rotation,
                        targetRotation,
                        Time.deltaTime * turnSpeed
                    );
                }
            }

            if (!hasMarkerPosition)
            {
                markerPosition = targetPosition;
                hasMarkerPosition = true;
            }
        }

        if (minimumFollowerDistance > 0f)
        {
            EnforceMinimumDistance();
        }

        UpdateDebugTargetCube(hasMarkerPosition, markerPosition);
    }

    public void ActivateFollowersAndStartSwarm()
    {
        for (int i = 0; i < followers.Count; i++)
        {
            if (followers[i] != null)
            {
                followers[i].gameObject.SetActive(true);
            }
        }

        InitializeFollowerStates();
        _startTime = Time.time;
        _isSwarmActive = true;
        LogDebug("Swarm activated via ActivateFollowersAndStartSwarm().");
    }

    [ContextMenu("Rebuild Path Cache")]
    private void RebuildPathCache()
    {
        _pathPoints.Clear();
        _segmentStartDistances.Clear();
        _pathLength = 0f;

        if (pathMeshFilter == null || pathMeshFilter.sharedMesh == null)
        {
            LogDebug("RebuildPathCache failed: path mesh filter or mesh is null.");
            return;
        }

        Vector3[] vertices = pathMeshFilter.sharedMesh.vertices;
        if (vertices == null || vertices.Length < 2)
        {
            LogDebug("RebuildPathCache failed: mesh has fewer than 2 vertices.");
            return;
        }

        Transform meshTransform = pathMeshFilter.transform;
        for (int i = 0; i < vertices.Length; i++)
        {
            Vector3 point = useMeshWorldTransform
                ? meshTransform.TransformPoint(vertices[i])
                : vertices[i];
            _pathPoints.Add(point);
        }

        for (int i = 0; i < _pathPoints.Count; i++)
        {
            int next = (i + 1) % _pathPoints.Count;
            _segmentStartDistances.Add(_pathLength);
            _pathLength += Vector3.Distance(_pathPoints[i], _pathPoints[next]);
        }

        LogDebug(
            "RebuildPathCache success: vertices=" + _pathPoints.Count +
            ", totalLength=" + _pathLength.ToString("F3")
        );
    }

    private void InitializeFollowerStates()
    {
        _states.Clear();
        int validCount = 0;
        for (int i = 0; i < followers.Count; i++)
        {
            if (followers[i] == null)
            {
                continue;
            }

            int initialTargetIndex = _pathPoints.Count > 0
                ? (i * Mathf.Max(1, _pathPoints.Count) / Mathf.Max(1, followers.Count)) % _pathPoints.Count
                : 0;

            _states.Add(new FollowerState
            {
                Transform = followers[i],
                Velocity = Vector3.zero,
                Phase = Random.Range(0f, Mathf.PI * 2f),
                JitterSeed = new Vector2(
                    Random.Range(0f, 1000f),
                    Random.Range(0f, 1000f)
                ),
                CurrentTargetIndex = initialTargetIndex
            });
            validCount++;
        }

        LogDebug("InitializeFollowerStates: valid followers=" + validCount);
    }

    private int GetSegmentIndex(float distanceAlongPath)
    {
        for (int i = _segmentStartDistances.Count - 1; i >= 0; i--)
        {
            if (distanceAlongPath >= _segmentStartDistances[i])
            {
                return i;
            }
        }

        return 0;
    }

    private int FindClosestVertexIndex(Vector3 position)
    {
        if (_pathPoints.Count == 0)
        {
            return 0;
        }

        int bestIndex = 0;
        float bestDistanceSq = float.MaxValue;
        for (int i = 0; i < _pathPoints.Count; i++)
        {
            float distanceSq = (_pathPoints[i] - position).sqrMagnitude;
            if (distanceSq < bestDistanceSq)
            {
                bestDistanceSq = distanceSq;
                bestIndex = i;
            }
        }

        return bestIndex;
    }

    private int FindClosestForwardVertexIndex(Vector3 position, int currentIndex, int searchAheadCount)
    {
        int count = _pathPoints.Count;
        if (count == 0)
        {
            return 0;
        }

        int nextIndex = (currentIndex + 1) % count;
        Vector3 forward = (_pathPoints[nextIndex] - _pathPoints[currentIndex]).normalized;
        if (forward.sqrMagnitude <= 0.000001f)
        {
            return nextIndex;
        }

        int clampedSearch = Mathf.Clamp(searchAheadCount, 1, count - 1);
        int bestIndex = nextIndex;
        float bestDistanceSq = float.MaxValue;

        for (int step = 1; step <= clampedSearch; step++)
        {
            int candidateIndex = (currentIndex + step) % count;
            Vector3 toCandidate = _pathPoints[candidateIndex] - position;
            if (Vector3.Dot(forward, toCandidate) <= 0f)
            {
                continue;
            }

            float distanceSq = toCandidate.sqrMagnitude;
            if (distanceSq < bestDistanceSq)
            {
                bestDistanceSq = distanceSq;
                bestIndex = candidateIndex;
            }
        }

        return bestIndex;
    }

    private void UpdateDebugTargetCube(bool hasPosition, Vector3 position)
    {
        if (!visualizeTargetWithRedCube || !hasPosition)
        {
            if (_debugTargetCube != null)
            {
                _debugTargetCube.gameObject.SetActive(false);
            }
            return;
        }

        if (_debugTargetCube == null)
        {
            GameObject cube = GameObject.CreatePrimitive(PrimitiveType.Cube);
            cube.name = "SwarmTargetDebugCube";
            cube.transform.localScale = Vector3.one * debugCubeSize;
            Renderer renderer = cube.GetComponent<Renderer>();
            if (renderer != null)
            {
                renderer.material.color = Color.red;
            }
            Collider cubeCollider = cube.GetComponent<Collider>();
            if (cubeCollider != null)
            {
                Destroy(cubeCollider);
            }
            _debugTargetCube = cube.transform;
        }

        _debugTargetCube.gameObject.SetActive(true);
        _debugTargetCube.position = position;
        _debugTargetCube.localScale = Vector3.one * debugCubeSize;
    }

    private void EnforceMinimumDistance()
    {
        float minDistanceSq = minimumFollowerDistance * minimumFollowerDistance;

        for (int i = 0; i < _states.Count; i++)
        {
            Transform a = _states[i].Transform;
            if (a == null)
            {
                continue;
            }

            for (int j = i + 1; j < _states.Count; j++)
            {
                Transform b = _states[j].Transform;
                if (b == null)
                {
                    continue;
                }

                Vector3 delta = b.position - a.position;
                float distSq = delta.sqrMagnitude;
                if (distSq >= minDistanceSq)
                {
                    continue;
                }

                float distance = Mathf.Sqrt(Mathf.Max(distSq, 0.000001f));
                Vector3 direction = distSq > 0.000001f ? delta / distance : Random.onUnitSphere;
                float push = (minimumFollowerDistance - distance) * 0.5f;

                a.position -= direction * push;
                b.position += direction * push;
            }
        }
    }

    private void LogDebug(string message)
    {
        if (!enableDebugLogs)
        {
            return;
        }

        if (Time.time < _nextDebugLogTime)
        {
            return;
        }

        _nextDebugLogTime = Time.time + debugLogIntervalSeconds;
        Debug.Log("[VertexPathSwarmFollower] " + message, this);
    }
}
