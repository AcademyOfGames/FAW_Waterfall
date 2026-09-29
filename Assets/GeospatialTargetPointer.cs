using System;
using System.Collections.Generic;
using Google.XR.ARCoreExtensions.GeospatialCreator;
using UnityEngine;

/// <summary>
/// Always-on-top screen indicator that points at every Geospatial Creator anchor in the scene.
///
/// The point of this is to answer "I don't see anything" without guesswork: if the content
/// exists at all, the pointer says which way to turn, how far up or down to look, and how far
/// away it is. If the pointer is steady and the content still cannot be seen once you are
/// aimed at it, the problem is rendering, not placement — and vice versa.
///
/// IMGUI on purpose, like the debug overlay: no Canvas, no font asset, no scene wiring.
/// </summary>
public class GeospatialTargetPointer : MonoBehaviour
{
    [Tooltip("Draw the pointer.")]
    [SerializeField]
    private bool showPointer = true;

    [Tooltip("Multiplier on all pointer text and icon sizes.")]
    [SerializeField]
    private float uiScale = 2f;

    [Tooltip("Distance in pixels the edge arrows are inset from the screen border.")]
    [SerializeField]
    private float edgeMargin = 90f;

    private readonly List<Target> _targets = new List<Target>();

    private Camera _camera;
    private Texture2D _arrowTexture;
    private Texture2D _pixel;
    private ARGeospatialCreatorAnchor[] _anchors = Array.Empty<ARGeospatialCreatorAnchor>();
    private float _nextScanTime;

    private GUIStyle _labelStyle;
    private GUIStyle _shadowStyle;

    /// <summary>Whether the pointer is drawn. Settable at runtime.</summary>
    public bool ShowPointer
    {
        get => showPointer;
        set => showPointer = value;
    }

    /// <summary>
    /// One anchor reduced to what the pointer needs to draw it. Rebuilt every frame because
    /// both the camera and the resolved anchor move.
    /// </summary>
    private struct Target
    {
        public string Name;
        public Vector3 World;
        public float Distance;
        public float HorizontalDistance;
        public float VerticalOffset;

        // Signed yaw from where the camera is looking to the target: negative is left.
        public float RelativeYaw;

        // Angle above (positive) or below the camera's horizon.
        public float Elevation;

        public bool Behind;
        public bool OnScreen;
        public Vector2 ScreenPoint;
        public bool HasVisibleRenderer;
    }

    private void Awake()
    {
        _camera = Camera.main != null ? Camera.main : FindAnyObjectByType<Camera>();
    }

    private void Update()
    {
        if (_camera == null || !_camera.isActiveAndEnabled)
        {
            _camera = Camera.main != null ? Camera.main : FindAnyObjectByType<Camera>();
        }

        if (Time.realtimeSinceStartup >= _nextScanTime)
        {
            _nextScanTime = Time.realtimeSinceStartup + 2f;
            _anchors = FindObjectsByType<ARGeospatialCreatorAnchor>(
                FindObjectsInactive.Include, FindObjectsSortMode.None);
        }

        RebuildTargets();
    }

    private void RebuildTargets()
    {
        _targets.Clear();
        if (_camera == null)
        {
            return;
        }

        Transform cameraTransform = _camera.transform;
        Vector3 eye = cameraTransform.position;

        for (int i = 0; i < _anchors.Length; i++)
        {
            ARGeospatialCreatorAnchor anchor = _anchors[i];
            if (anchor == null)
            {
                continue;
            }

            Vector3 world = ContentCentre(anchor, out bool hasVisibleRenderer);
            if (float.IsNaN(world.x) || float.IsInfinity(world.x))
            {
                continue;
            }

            Vector3 delta = world - eye;
            Vector3 flat = new Vector3(delta.x, 0f, delta.z);

            Target target = new Target
            {
                Name = anchor.name,
                World = world,
                Distance = delta.magnitude,
                HorizontalDistance = flat.magnitude,
                VerticalOffset = delta.y,
                HasVisibleRenderer = hasVisibleRenderer,
            };

            // Yaw is measured in the horizontal plane so that tilting the phone up and down
            // does not swing the "turn left / turn right" reading around.
            Vector3 flatForward = new Vector3(
                cameraTransform.forward.x, 0f, cameraTransform.forward.z);
            if (flatForward.sqrMagnitude < 1e-6f)
            {
                // Phone pointed straight up or down: fall back to which way the top of the
                // screen faces, which is the direction the user is effectively "facing".
                flatForward = new Vector3(cameraTransform.up.x, 0f, cameraTransform.up.z);
            }

            target.RelativeYaw = Vector3.SignedAngle(
                flatForward.normalized,
                flat.sqrMagnitude < 1e-6f ? flatForward.normalized : flat.normalized,
                Vector3.up);
            target.Elevation = target.Distance < 1e-4f
                ? 0f
                : Mathf.Asin(Mathf.Clamp(delta.y / target.Distance, -1f, 1f)) * Mathf.Rad2Deg;

            Vector3 screen = _camera.WorldToScreenPoint(world);
            target.Behind = screen.z <= 0f;
            if (target.Behind)
            {
                // Behind the camera the projection folds back on itself, so mirror it to keep
                // the arrow pointing the way you actually have to turn.
                screen.x = Screen.width - screen.x;
                screen.y = Screen.height - screen.y;
            }

            // GUI space has its origin at the top-left; screen space at the bottom-left.
            target.ScreenPoint = new Vector2(screen.x, Screen.height - screen.y);
            target.OnScreen = !target.Behind &&
                              screen.x >= 0f && screen.x <= Screen.width &&
                              screen.y >= 0f && screen.y <= Screen.height;

            _targets.Add(target);
        }
    }

    /// <summary>
    /// The point worth aiming at: the centre of the anchor's enabled renderers if it has any,
    /// otherwise the anchor itself. Using the renderer bounds means the arrow points at the
    /// content the user is looking for rather than at a pivot that may sit far away from it.
    /// </summary>
    private static Vector3 ContentCentre(
        ARGeospatialCreatorAnchor anchor, out bool hasVisibleRenderer)
    {
        hasVisibleRenderer = false;
        Renderer[] renderers = anchor.GetComponentsInChildren<Renderer>(true);
        bool any = false;
        Bounds bounds = new Bounds();

        for (int i = 0; i < renderers.Length; i++)
        {
            Renderer renderer = renderers[i];
            if (!renderer.enabled || !renderer.gameObject.activeInHierarchy)
            {
                continue;
            }

            if (renderer.isVisible)
            {
                hasVisibleRenderer = true;
            }

            if (any)
            {
                bounds.Encapsulate(renderer.bounds);
            }
            else
            {
                bounds = renderer.bounds;
                any = true;
            }
        }

        return any ? bounds.center : anchor.transform.position;
    }

    private void EnsureResources()
    {
        if (_arrowTexture == null)
        {
            _arrowTexture = BuildArrowTexture(64);
        }

        if (_pixel == null)
        {
            _pixel = new Texture2D(1, 1, TextureFormat.RGBA32, false);
            _pixel.SetPixel(0, 0, Color.white);
            _pixel.Apply();
        }

        int fontSize = Mathf.RoundToInt(14f * uiScale * Mathf.Max(1f, Screen.width / 1080f));

        if (_labelStyle == null || _labelStyle.fontSize != fontSize)
        {
            _labelStyle = new GUIStyle(GUI.skin.label)
            {
                fontSize = fontSize,
                alignment = TextAnchor.MiddleCenter,
                fontStyle = FontStyle.Bold,
                wordWrap = false,
            };
            _labelStyle.normal.textColor = new Color(1f, 0.95f, 0.35f);

            _shadowStyle = new GUIStyle(_labelStyle);
            _shadowStyle.normal.textColor = new Color(0f, 0f, 0f, 0.85f);
        }
    }

    /// <summary>A solid triangle pointing up, generated so no art asset is needed.</summary>
    private static Texture2D BuildArrowTexture(int size)
    {
        Texture2D texture = new Texture2D(size, size, TextureFormat.RGBA32, false)
        {
            filterMode = FilterMode.Bilinear,
            wrapMode = TextureWrapMode.Clamp,
        };

        Color32[] pixels = new Color32[size * size];
        for (int y = 0; y < size; y++)
        {
            // Row 0 is the bottom of the texture; the apex is at the top.
            float t = y / (float)(size - 1);
            float halfWidth = (1f - t) * 0.5f * size;
            float centre = size * 0.5f;

            for (int x = 0; x < size; x++)
            {
                float distance = Mathf.Abs(x + 0.5f - centre);
                // Feather the edge by one pixel so the rotated arrow does not look ragged.
                float alpha = Mathf.Clamp01(halfWidth - distance);
                bool border = alpha > 0f && (halfWidth - distance) < 3f;
                byte a = (byte)(alpha * 255f);
                pixels[(y * size) + x] = border
                    ? new Color32(20, 20, 20, a)
                    : new Color32(255, 235, 60, a);
            }
        }

        texture.SetPixels32(pixels);
        texture.Apply();
        return texture;
    }

    private void OnGUI()
    {
        if (!showPointer || _targets.Count == 0)
        {
            return;
        }

        EnsureResources();

        // Negative depth puts this in front of the debug overlay's IMGUI, so the pointer is
        // never hidden behind the panel it is meant to complement.
        int previousDepth = GUI.depth;
        GUI.depth = -100;

        float arrowSize = 46f * uiScale * Mathf.Max(1f, Screen.width / 1080f);
        Vector2 centre = new Vector2(Screen.width * 0.5f, Screen.height * 0.5f);
        float halfWidth = (Screen.width * 0.5f) - edgeMargin - (arrowSize * 0.5f);
        float halfHeight = (Screen.height * 0.5f) - edgeMargin - (arrowSize * 0.5f);

        for (int i = 0; i < _targets.Count; i++)
        {
            Target target = _targets[i];
            string distanceText = target.Distance >= 1000f
                ? $"{target.Distance / 1000f:F2} km"
                : $"{target.Distance:F0} m";

            if (target.OnScreen)
            {
                DrawOnScreenReticle(target, distanceText, arrowSize);
                continue;
            }

            Vector2 direction = target.ScreenPoint - centre;
            if (direction.sqrMagnitude < 1e-4f)
            {
                direction = new Vector2(0f, -1f);
            }

            // Push the arrow out to whichever screen edge the direction hits first.
            float scaleX = halfWidth / Mathf.Max(Mathf.Abs(direction.x), 1e-4f);
            float scaleY = halfHeight / Mathf.Max(Mathf.Abs(direction.y), 1e-4f);
            Vector2 position = centre + (direction * Mathf.Min(scaleX, scaleY));

            // The texture points up (0,-1 in GUI space); rotate it onto the direction.
            float angle = Mathf.Atan2(direction.x, -direction.y) * Mathf.Rad2Deg;

            Matrix4x4 previousMatrix = GUI.matrix;
            GUIUtility.RotateAroundPivot(angle, position);
            GUI.DrawTexture(
                new Rect(position.x - (arrowSize * 0.5f), position.y - (arrowSize * 0.5f),
                    arrowSize, arrowSize),
                _arrowTexture);
            GUI.matrix = previousMatrix;

            string turn = target.RelativeYaw >= 0f
                ? $"turn right {Mathf.Abs(target.RelativeYaw):F0}°"
                : $"turn left {Mathf.Abs(target.RelativeYaw):F0}°";
            string look = target.Elevation >= 0f
                ? $"look up {target.Elevation:F0}°"
                : $"look down {Mathf.Abs(target.Elevation):F0}°";

            // Keep the label inside the screen: nudge it back toward the centre.
            Vector2 labelPosition = position - (direction.normalized * arrowSize * 0.85f);
            DrawLabel(
                labelPosition,
                $"{target.Name}\n{distanceText}  {turn}  {look}\n" +
                $"{target.HorizontalDistance:F0} m out, {target.VerticalOffset:+0;-0} m up",
                arrowSize * 8f);
        }

        GUI.depth = previousDepth;
    }

    private void DrawOnScreenReticle(Target target, string distanceText, float arrowSize)
    {
        float boxSize = Mathf.Clamp(arrowSize * 1.6f, 40f, Screen.height * 0.5f);
        Rect box = new Rect(
            target.ScreenPoint.x - (boxSize * 0.5f),
            target.ScreenPoint.y - (boxSize * 0.5f),
            boxSize, boxSize);

        // Green once something under the anchor is actually being drawn, amber when the
        // anchor is aimed at but nothing rendered last frame — that difference is the whole
        // "is it placement or is it rendering?" question.
        Color color = target.HasVisibleRenderer
            ? new Color(0.35f, 1f, 0.45f)
            : new Color(1f, 0.75f, 0.2f);

        DrawRectOutline(box, 3f, color);

        string state = target.HasVisibleRenderer ? "RENDERING" : "nothing drawn";
        DrawLabel(
            new Vector2(target.ScreenPoint.x, box.yMax + (boxSize * 0.28f)),
            $"{target.Name}\n{distanceText}  •  {state}\n" +
            $"{target.VerticalOffset:+0;-0} m up  •  elev {target.Elevation:F0}°",
            boxSize * 6f);
    }

    private void DrawRectOutline(Rect rect, float thickness, Color color)
    {
        Color previousColor = GUI.color;
        GUI.color = color;
        GUI.DrawTexture(new Rect(rect.x, rect.y, rect.width, thickness), _pixel);
        GUI.DrawTexture(new Rect(rect.x, rect.yMax - thickness, rect.width, thickness), _pixel);
        GUI.DrawTexture(new Rect(rect.x, rect.y, thickness, rect.height), _pixel);
        GUI.DrawTexture(new Rect(rect.xMax - thickness, rect.y, thickness, rect.height), _pixel);
        GUI.color = previousColor;
    }

    /// <summary>
    /// Centred text with a drop shadow, so it stays readable over an arbitrary camera feed.
    /// </summary>
    private void DrawLabel(Vector2 centre, string text, float width)
    {
        Vector2 size = _labelStyle.CalcSize(new GUIContent(text));
        float height = Mathf.Max(size.y, _labelStyle.lineHeight * 2f);
        Rect rect = new Rect(centre.x - (width * 0.5f), centre.y - (height * 0.5f), width, height);

        rect.x = Mathf.Clamp(rect.x, -width * 0.5f, Screen.width - (width * 0.5f));
        rect.y = Mathf.Clamp(rect.y, 0f, Screen.height - height);

        Rect shadow = rect;
        shadow.x += 2f;
        shadow.y += 2f;
        GUI.Label(shadow, text, _shadowStyle);
        GUI.Label(rect, text, _labelStyle);
    }
}
