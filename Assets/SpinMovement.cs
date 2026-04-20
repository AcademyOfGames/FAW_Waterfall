using UnityEngine;

public class SpinMovement : MonoBehaviour
{
    [Header("Targeting")]
    public Transform centerPoint;
    public GameObject[] orbiters;

    [Header("Random Orbit Axis Range")]
    [Tooltip("Each orbiter gets a random axis using these X/Y/Z ranges.")]
    public Vector2 axisXRange = new Vector2(-1f, 1f);
    public Vector2 axisYRange = new Vector2(-1f, 1f);
    public Vector2 axisZRange = new Vector2(-1f, 1f);

    [Header("Orbit Settings (Real-time)")]
    [Range(0f, 50f)] public float minRadius = 5f;
    [Range(0f, 50f)] public float maxRadius = 15f;
    [Space]
    [Range(0f, 200f)] public float minSpeed = 20f;
    [Range(0f, 200f)] public float maxSpeed = 60f;

    [Header("Local Spin Settings")]
    [Range(0f, 400f)] public float minLocalSpinSpeed = 30f;
    [Range(0f, 400f)] public float maxLocalSpinSpeed = 120f;

    // Unique data per object
    private float[] radiusLerps;
    private float[] speedLerps;
    private Vector3[] orbitAxes;
    private Vector3[] localSpinAxes;
    private float[] localSpinSpeeds;

    void Start()
    {
        if (centerPoint == null || orbiters == null || orbiters.Length == 0) return;

        int count = orbiters.Length;
        radiusLerps = new float[count];
        speedLerps = new float[count];
        orbitAxes = new Vector3[count];
        localSpinAxes = new Vector3[count];
        localSpinSpeeds = new float[count];

        for (int i = 0; i < count; i++)
        {
            if (orbiters[i] == null) continue;

            // Randomize where they sit within your min/max ranges
            radiusLerps[i] = Random.value;
            speedLerps[i] = Random.value;
            orbitAxes[i] = GenerateRandomAxis();
            localSpinAxes[i] = Random.onUnitSphere;
            localSpinSpeeds[i] = Random.Range(minLocalSpinSpeed, maxLocalSpinSpeed);

            // Spread them out randomly around the circle at the start
            float startRadius = Mathf.Lerp(minRadius, maxRadius, radiusLerps[i]);
            Vector3 randomDir = GetDirectionOnOrbitPlane(orbitAxes[i]);
            orbiters[i].transform.position = centerPoint.position + (randomDir * startRadius);
        }
    }

    void Update()
    {
        if (centerPoint == null || orbiters == null) return;

        for (int i = 0; i < orbiters.Length; i++)
        {
            if (orbiters[i] == null) continue;

            // 1. Determine current speed and radius from Inspector sliders
            float currentSpeed = Mathf.Lerp(minSpeed, maxSpeed, speedLerps[i]);
            float currentRadius = Mathf.Lerp(minRadius, maxRadius, radiusLerps[i]);

            // 2. Adjust distance from center (allows real-time radius resizing)
            Vector3 offset = orbiters[i].transform.position - centerPoint.position;
            Vector3 direction = offset.normalized;
            orbiters[i].transform.position = centerPoint.position + (direction * currentRadius);

            // 3. Perform orbit rotation around this orbiter's randomized axis
            orbiters[i].transform.RotateAround(centerPoint.position, orbitAxes[i], currentSpeed * Time.deltaTime);

            // 4. Spin the object in local space while it orbits
            orbiters[i].transform.Rotate(localSpinAxes[i], localSpinSpeeds[i] * Time.deltaTime, Space.Self);
        }
    }

    private Vector3 GenerateRandomAxis()
    {
        Vector3 axis = new Vector3(
            Random.Range(axisXRange.x, axisXRange.y),
            Random.Range(axisYRange.x, axisYRange.y),
            Random.Range(axisZRange.x, axisZRange.y)
        );

        if (axis.sqrMagnitude < 0.0001f)
        {
            return Vector3.up;
        }

        return axis.normalized;
    }

    private Vector3 GetDirectionOnOrbitPlane(Vector3 axis)
    {
        Vector3 projected = Vector3.ProjectOnPlane(Random.onUnitSphere, axis);
        if (projected.sqrMagnitude >= 0.0001f)
        {
            return projected.normalized;
        }

        Vector3 fallback = Vector3.Cross(axis, Vector3.right);
        if (fallback.sqrMagnitude < 0.0001f)
        {
            fallback = Vector3.Cross(axis, Vector3.forward);
        }

        return fallback.normalized;
    }
}