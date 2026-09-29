using Unity.XR.CoreUtils;
using UnityEngine;
using UnityEngine.XR.ARFoundation;
using UnityEngine.XR.ARSubsystems;

/// <summary>
/// Creates an AR anchor on the best detected horizontal plane and parents content at its center.
/// Prefers the lowest sufficiently large plane to avoid locking onto tables or counters.
/// Places early on a small plane, then drops the content onto a better (lower) floor a limited
/// number of times as detection improves, so it shows up fast without jittering afterwards.
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
    [Tooltip("Smallest plane (m²) the content will be placed on. Small = appears sooner, at the cost of the first guess being rougher.")]
    [SerializeField] private float minimumPlaneArea = 0.05f;

    [Header("Refinement")]
    [Tooltip("After the first placement, how many more times the height may be corrected onto a better floor plane. 0 = never move again.")]
    [SerializeField] private int maxRefinements = 2;
    [Tooltip("A new plane only counts as better if it is at least this much lower (metres) than the current floor.")]
    [SerializeField] private float refineHeightTolerance = 0.04f;
    [Tooltip("Minimum seconds between refinements, so successive corrections do not read as jitter.")]
    [SerializeField] private float refineCooldownSeconds = 1.5f;

    [Header("Placement")]
    [Tooltip("Off: content sits at the centre of the detected plane. On: content keeps the offset and heading it has " +
             "from the XR Origin in the editor, rebuilt around where the phone actually is when the floor is found, " +
             "so it appears in front of the user the way it was laid out in the scene.")]
    [SerializeField] private bool placeRelativeToCamera = false;

    private bool _anchored;
    private bool _contentWasActive;
    private Transform _originTransform;
    private Vector3 _authoredLocalPosition;
    private Quaternion _authoredLocalRotation = Quaternion.identity;

    private ARAnchor _currentAnchor;
    private float _currentFloorY;
    private int _refinementsDone;
    private float _nextRefineTime;

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

        // Capture the authored layout before anything moves: where the content sits relative to
        // the rig in the editor is the offset we rebuild around the real camera later.
        var origin = GetComponent<XROrigin>();
        _originTransform = origin != null ? origin.transform : transform;
        if (contentRoot != null)
        {
            _authoredLocalPosition = _originTransform.InverseTransformPoint(contentRoot.position);
            _authoredLocalRotation = Quaternion.Inverse(_originTransform.rotation) * contentRoot.rotation;
        }

        if (hideContentUntilAnchored && contentRoot != null)
        {
            _contentWasActive = contentRoot.gameObject.activeSelf;
            contentRoot.gameObject.SetActive(false);
        }
    }

    private void Update()
    {
        if (planeManager == null)
            return;

        if (!_anchored)
        {
            if (TrySelectBestFloorPlane(planeManager.trackables, out ARPlane plane))
                AnchorContentToPlane(plane);
            return;
        }

        // Already placed: only move again for a clearly lower floor, a limited number of times,
        // and never in quick succession.
        if (_refinementsDone >= maxRefinements || Time.time < _nextRefineTime)
            return;

        if (TrySelectBestFloorPlane(planeManager.trackables, out ARPlane better)
            && GetPlaneCenterWorld(better).y < _currentFloorY - refineHeightTolerance)
        {
            RefineHeight(better);
        }
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

        Pose anchorPose = placeRelativeToCamera
            ? GetCameraRelativePose(plane)
            : new Pose(GetPlaneCenterWorld(plane), plane.transform.rotation);

        ARAnchor anchor = CreateAnchor(plane, anchorPose);
        if (anchor == null)
            return;

        contentRoot.SetParent(anchor.transform, worldPositionStays: false);
        contentRoot.localPosition = Vector3.zero;
        contentRoot.localRotation = Quaternion.identity;

        if (hideContentUntilAnchored)
            contentRoot.gameObject.SetActive(_contentWasActive);
        else if (!contentRoot.gameObject.activeSelf)
            contentRoot.gameObject.SetActive(true);

        _currentAnchor = anchor;
        _currentFloorY = GetPlaneCenterWorld(plane).y;
        _nextRefineTime = Time.time + refineCooldownSeconds;
        _anchored = true;
    }

    /// <summary>
    /// Drops the content straight down onto a better floor. Only the height changes: the
    /// horizontal position and heading the user has already seen stay where they are.
    /// </summary>
    private void RefineHeight(ARPlane plane)
    {
        float floorY = GetPlaneCenterWorld(plane).y;
        Vector3 position = contentRoot.position;
        position.y = floorY;
        Pose anchorPose = new Pose(position, contentRoot.rotation);

        ARAnchor anchor = CreateAnchor(plane, anchorPose);
        if (anchor == null)
            return;

        contentRoot.SetParent(anchor.transform, worldPositionStays: false);
        contentRoot.localPosition = Vector3.zero;
        contentRoot.localRotation = Quaternion.identity;

        if (_currentAnchor != null)
            Destroy(_currentAnchor.gameObject);

        _currentAnchor = anchor;
        _currentFloorY = floorY;
        _refinementsDone++;
        _nextRefineTime = Time.time + refineCooldownSeconds;
    }

    private ARAnchor CreateAnchor(ARPlane plane, Pose pose)
    {
        ARAnchor anchor = plane != null ? anchorManager.AttachAnchor(plane, pose) : null;
        if (anchor != null)
            return anchor;

        // Fallback when the plane refuses the attachment: a free-standing anchor at the pose.
        // (ARAnchorManager.AddAnchor is obsolete; adding the component is the supported way.)
        var go = new GameObject("ContentAnchor");
        go.transform.SetPositionAndRotation(pose.position, pose.rotation);
        return go.AddComponent<ARAnchor>();
    }

    /// <summary>
    /// The authored offset from the rig, re-based onto the phone's current ground position and
    /// compass-free heading, with height taken from the detected floor.
    /// </summary>
    private Pose GetCameraRelativePose(ARPlane plane)
    {
        Camera cam = Camera.main;
        var origin = GetComponent<XROrigin>();
        if (origin != null && origin.Camera != null)
            cam = origin.Camera;

        float floorY = GetPlaneCenterWorld(plane).y;
        if (cam == null)
            return new Pose(GetPlaneCenterWorld(plane), plane.transform.rotation);

        // Heading only: tilting the phone must not tilt the world.
        Vector3 forward = cam.transform.forward;
        forward.y = 0f;
        if (forward.sqrMagnitude < 0.0001f)
        {
            forward = cam.transform.up;
            forward.y = 0f;
        }
        Quaternion heading = Quaternion.LookRotation(forward.normalized, Vector3.up);

        Vector3 flatOffset = new Vector3(_authoredLocalPosition.x, 0f, _authoredLocalPosition.z);
        Vector3 position = new Vector3(cam.transform.position.x, floorY, cam.transform.position.z) + heading * flatOffset;
        return new Pose(position, heading * _authoredLocalRotation);
    }

    private static Vector3 GetPlaneCenterWorld(ARPlane plane)
    {
        Vector2 centerInPlaneSpace = plane.center;
        return plane.transform.TransformPoint(new Vector3(centerInPlaneSpace.x, 0f, centerInPlaneSpace.y));
    }
}
