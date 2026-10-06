using Unity.XR.CoreUtils;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.InputSystem;
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

    [Header("Tap To Place")]
    [Tooltip("Off: content is placed automatically as soon as a floor is found. On: nothing is placed until the user " +
             "taps the screen. The content then appears on the floor at its authored offset from the camera, using " +
             "where the phone was and which way it was facing at the moment of the tap. If no floor has been found " +
             "yet, it keeps looking and places as soon as one is.")]
    [SerializeField] private bool placeOnTap = false;
    [Tooltip("Ignore taps that land on UI (buttons etc.).")]
    [SerializeField] private bool ignoreTapsOnUI = true;

    [Header("Tap To Place Guide")]
    [Tooltip("Reference photo shown faded on screen until the user taps, so they can line the view up with it. " +
             "Left empty, the texture named below is loaded from a Resources folder.")]
    [SerializeField] private Texture2D alignGuideImage;
    [Tooltip("Resources name used when no image is assigned above.")]
    [SerializeField] private string alignGuideResourceName = "KingStreetStationAlignGuide";
    [Tooltip("How see-through the reference photo is. 0 = invisible, 1 = solid.")]
    [Range(0f, 1f)] [SerializeField] private float alignGuideOpacity = 0.5f;
    [Tooltip("Width of the photo as a fraction of the screen width.")]
    [Range(0.1f, 1f)] [SerializeField] private float alignGuideWidth = 0.85f;
    [SerializeField] private string alignPrompt = "Align and tap to place";
    [Tooltip("Shown after the tap while the floor has not been found yet. Leave empty for no message.")]
    [SerializeField] private string findFloorPrompt = "Point at the ground";

    /// <summary>Raised once, when the content is first placed.</summary>
    public event System.Action Placed;

    /// <summary>True once the content has been placed on the floor (by tap or automatically).</summary>
    public bool IsPlaced => _anchored;

    /// <summary>True while tap-to-place is on and the user has not tapped yet.</summary>
    public bool WaitingForTap => placeOnTap && !_tapped && !_anchored;

    /// <summary>True after the tap while the floor still has not been found, so nothing is placed yet.</summary>
    public bool WaitingForFloor => placeOnTap && _tapped && !_anchored;

    private bool _tapped;
    private Vector3 _tapCameraPosition;
    private Quaternion _tapHeading = Quaternion.identity;

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

    private void Start()
    {
        if (!placeOnTap)
            return;

        Texture2D guide = alignGuideImage;
        if (guide == null && !string.IsNullOrEmpty(alignGuideResourceName))
            guide = Resources.Load<Texture2D>(alignGuideResourceName);
        AlignTapOverlay.Create(this, guide, alignGuideOpacity, alignGuideWidth, alignPrompt, findFloorPrompt);
    }

    private void Update()
    {
        if (planeManager == null)
            return;

        if (!_anchored)
        {
            if (placeOnTap)
            {
                if (!_tapped)
                {
                    if (!WasTappedThisFrame())
                        return;

                    // Freeze where the phone was and which way it faced at the tap: that is the
                    // alignment the user chose, even if the floor only shows up a moment later.
                    _tapped = true;
                    CaptureCameraPose(out _tapCameraPosition, out _tapHeading);
                }

                // Never place without a real floor: keep looking every frame until one is found.
                if (TrySelectBestFloorPlane(planeManager.trackables, out ARPlane tapPlane))
                    AnchorContentToPlane(tapPlane);
                return;
            }

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

        // Tap-to-place always uses the camera-relative layout; that is the whole point of aligning first.
        Pose anchorPose = placeRelativeToCamera || placeOnTap
            ? GetCameraRelativePose(GetPlaneCenterWorld(plane).y)
            : new Pose(GetPlaneCenterWorld(plane), plane.transform.rotation);

        FinishPlacement(CreateAnchor(plane, anchorPose), GetPlaneCenterWorld(plane).y);
    }

    private void FinishPlacement(ARAnchor anchor, float floorY)
    {
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
        _currentFloorY = floorY;
        _nextRefineTime = Time.time + refineCooldownSeconds;
        _anchored = true;
        Placed?.Invoke();
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
    private Pose GetCameraRelativePose(float floorY)
    {
        Vector3 camPosition;
        Quaternion heading;
        if (_tapped)
        {
            camPosition = _tapCameraPosition;
            heading = _tapHeading;
        }
        else
        {
            CaptureCameraPose(out camPosition, out heading);
        }

        Vector3 flatOffset = new Vector3(_authoredLocalPosition.x, 0f, _authoredLocalPosition.z);
        Vector3 position = new Vector3(camPosition.x, floorY, camPosition.z) + heading * flatOffset;
        return new Pose(position, heading * _authoredLocalRotation);
    }

    /// <summary>Camera position plus its heading flattened onto the ground (tilting the phone must not tilt the world).</summary>
    private void CaptureCameraPose(out Vector3 position, out Quaternion heading)
    {
        Camera cam = Camera.main;
        var origin = GetComponent<XROrigin>();
        if (origin != null && origin.Camera != null)
            cam = origin.Camera;

        Transform t = cam != null ? cam.transform : _originTransform;
        position = t.position;

        Vector3 forward = t.forward;
        forward.y = 0f;
        if (forward.sqrMagnitude < 0.0001f)
        {
            // Pointing straight up or down: the top of the phone still gives a usable heading.
            forward = t.forward.y > 0f ? -t.up : t.up;
            forward.y = 0f;
        }
        heading = forward.sqrMagnitude < 0.0001f ? Quaternion.identity : Quaternion.LookRotation(forward.normalized, Vector3.up);
    }

    private bool WasTappedThisFrame()
    {
        int pointerId = -1;
        bool tapped = false;

        var touchscreen = Touchscreen.current;
        if (touchscreen != null && touchscreen.primaryTouch.press.wasPressedThisFrame)
        {
            pointerId = touchscreen.primaryTouch.touchId.ReadValue();
            tapped = true;
        }
        else
        {
            var mouse = Mouse.current;
            if (mouse != null && mouse.leftButton.wasPressedThisFrame)
                tapped = true;
        }

        if (!tapped)
            return false;

        if (ignoreTapsOnUI && EventSystem.current != null && EventSystem.current.IsPointerOverGameObject(pointerId))
            return false;

        return true;
    }

    private static Vector3 GetPlaneCenterWorld(ARPlane plane)
    {
        Vector2 centerInPlaneSpace = plane.center;
        return plane.transform.TransformPoint(new Vector3(centerInPlaneSpace.x, 0f, centerInPlaneSpace.y));
    }
}
