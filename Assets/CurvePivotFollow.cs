using UnityEngine;

/// <summary>
/// Keeps a pivot child aligned with a target by moving/rotating this transform (the curve root).
/// The pivot's local position/rotation are never modified.
/// </summary>
public class CurvePivotFollow : MonoBehaviour
{
    [Header("References")]
    [Tooltip("Target to follow (Object A).")]
    public Transform target;

    [Tooltip("Pivot on the curve hierarchy (Object C). Must be a child of this transform.")]
    public Transform pivot;

    [Tooltip("Object the curve should point toward (Object D).")]
    public Transform pointAtTarget;

    [Header("Pointing")]
    [Tooltip("Local euler offset applied after aiming +Z at Object D. " +
             "X/Y tilt which local direction points at D (e.g. Y=90 if the tip faces local +X). " +
             "Z rolls around the aim axis.")]
    public Vector3 pointDirectionOffset;

    void LateUpdate()
    {
        if (target == null || pivot == null)
            return;

        AlignPivotToTarget();
        PointAtTarget();
    }

    void AlignPivotToTarget()
    {
        transform.position += target.position - pivot.position;
    }

    void PointAtTarget()
    {
        if (pointAtTarget == null)
            return;

        Vector3 pivotWorld = pivot.position;
        Vector3 toTarget = pointAtTarget.position - pivotWorld;
        if (toTarget.sqrMagnitude < 0.0001f)
            return;

        Vector3 desiredDirection = toTarget.normalized;
        Vector3 referenceUp = GetReferenceUp(desiredDirection);
        Quaternion desiredRotation = Quaternion.LookRotation(desiredDirection, referenceUp)
            * Quaternion.Euler(pointDirectionOffset);

        SetWorldRotationAroundPivot(desiredRotation, pivotWorld);
    }

    Vector3 GetReferenceUp(Vector3 desiredDirection)
    {
        Vector3 up = Vector3.up;
        if (Mathf.Abs(Vector3.Dot(desiredDirection, up)) > 0.99f)
            up = Vector3.forward;

        return up;
    }

    void SetWorldRotationAroundPivot(Quaternion desiredRotation, Vector3 pivotWorld)
    {
        transform.rotation = desiredRotation;
        transform.position = pivotWorld - transform.rotation * pivot.localPosition;
    }

    void OnValidate()
    {
        if (pivot != null && pivot != transform && !pivot.IsChildOf(transform))
            Debug.LogWarning($"{nameof(CurvePivotFollow)} on '{name}': pivot should be a child of this transform.", this);
    }
}
