using UnityEngine;
using UnityEngine.XR.ARFoundation;
using UnityEngine.XR.ARSubsystems;

/// <summary>
/// Creates an AR anchor on the best detected horizontal plane and parents content at its center.
/// Prefers the lowest sufficiently large plane to avoid locking onto tables or counters.
/// </summary>
[DisallowMultipleComponent]
public class ARPlaneContentAnchor : MonoBehaviour
{
    [Header("AR")]
    [SerializeField] private ARPlaneManager planeManager;
    [SerializeField] private ARAnchorManager anchorManager;

    [Header("Content")]
    [SerializeField] private Transform contentRoot;
    [SerializeField] private bool hideContentUntilAnchored = true;

    [Header("Plane Selection")]
    [SerializeField] private float minimumPlaneArea = 0.25f;

    private bool _anchored;
    private bool _contentWasActive;

    private void Awake()
    {
        if (planeManager == null)
            planeManager = GetComponent<ARPlaneManager>();

        if (anchorManager == null)
            anchorManager = GetComponent<ARAnchorManager>();

        if (contentRoot == null)
        {
            var contentObject = GameObject.Find("AlinaPrefabParent");
            if (contentObject != null)
                contentRoot = contentObject.transform;
        }

        if (hideContentUntilAnchored && contentRoot != null)
        {
            _contentWasActive = contentRoot.gameObject.activeSelf;
            contentRoot.gameObject.SetActive(false);
        }
    }

    private void Update()
    {
        if (_anchored || planeManager == null)
            return;

        if (TrySelectBestFloorPlane(planeManager.trackables, out ARPlane plane))
            AnchorContentToPlane(plane);
    }

    private bool TrySelectBestFloorPlane(TrackableCollection<ARPlane> planes, out ARPlane bestPlane)
    {
        bestPlane = null;
        float bestCenterY = float.MaxValue;
        float bestArea = 0f;

        foreach (ARPlane plane in planes)
        {
            if (plane == null || plane.alignment != PlaneAlignment.HorizontalUp)
                continue;

            float area = plane.size.x * plane.size.y;
            if (area < minimumPlaneArea)
                continue;

            float centerY = GetPlaneCenterWorld(plane).y;
            if (bestPlane == null
                || centerY < bestCenterY - 0.05f
                || (Mathf.Abs(centerY - bestCenterY) <= 0.05f && area > bestArea))
            {
                bestPlane = plane;
                bestCenterY = centerY;
                bestArea = area;
            }
        }

        return bestPlane != null;
    }

    private void AnchorContentToPlane(ARPlane plane)
    {
        if (contentRoot == null || anchorManager == null)
            return;

        Pose anchorPose = new Pose(GetPlaneCenterWorld(plane), plane.transform.rotation);
        ARAnchor anchor = anchorManager.AttachAnchor(plane, anchorPose);
        if (anchor == null)
            anchor = anchorManager.AddAnchor(anchorPose);

        if (anchor == null)
            return;

        contentRoot.SetParent(anchor.transform, worldPositionStays: false);
        contentRoot.localPosition = Vector3.zero;
        contentRoot.localRotation = Quaternion.identity;

        if (hideContentUntilAnchored)
            contentRoot.gameObject.SetActive(_contentWasActive);
        else if (!contentRoot.gameObject.activeSelf)
            contentRoot.gameObject.SetActive(true);

        _anchored = true;
    }

    private static Vector3 GetPlaneCenterWorld(ARPlane plane)
    {
        Vector2 centerInPlaneSpace = plane.center;
        return plane.transform.TransformPoint(new Vector3(centerInPlaneSpace.x, 0f, centerInPlaneSpace.y));
    }
}
