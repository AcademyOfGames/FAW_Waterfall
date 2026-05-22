using UnityEngine;

/// <summary>Rotates this object around a configurable axis at a set speed (degrees per second).</summary>
public class ConstantRotation : MonoBehaviour
{
    [Tooltip("Rotation axis in local space (e.g. 0,1,0 for spin around local Y).")]
    [SerializeField] private Vector3 axis = Vector3.up;

    [Tooltip("Degrees per second.")]
    [SerializeField] private float speed = 45f;

    [Tooltip("When enabled, axis is interpreted in world space instead of local.")]
    [SerializeField] private bool useWorldSpace;

    void Update()
    {
        if (axis.sqrMagnitude < 0.0001f)
            return;

        var space = useWorldSpace ? Space.World : Space.Self;
        transform.Rotate(axis.normalized, speed * Time.deltaTime, space);
    }
}
