using System.Collections;
using UnityEngine;

// Spawns the heron at heronStart, flies it along a descending curve to heronEnd,
// lands it so heronFeet sits on the landing point, then plays idle followed by eat.
// Put this on the Heron root (the object with the Animator).
[RequireComponent(typeof(Animator))]
public class HeronFlight : MonoBehaviour
{
    [Header("Path")]
    [Tooltip("Spawn point. Falls back to a scene object named 'heronStart'.")]
    public Transform heronStart;
    [Tooltip("Landing point on the rocks. Falls back to a scene object named 'heronEnd'.")]
    public Transform heronEnd;
    [Tooltip("Object at the heron's feet. Falls back to a child named 'heronFeet'.")]
    public Transform heronFeet;

    [Tooltip("Optional hand-drawn path. When set, the heron follows this spline's shape; its ends are blended " +
             "onto heronStart and the feet landing spot. When empty, the curve settings below are used.")]
    public BezierSolution.BezierSpline flightPath;

    [Tooltip("How far along the start->end distance the first control point sits (keeps altitude early).")]
    [Range(0f, 1f)] public float departureStretch = 0.4f;
    [Tooltip("How far back from the end the second control point sits (shapes the final approach).")]
    [Range(0f, 1f)] public float approachStretch = 0.35f;
    [Tooltip("Height above heronEnd of the final approach control point. Higher = steeper final descent.")]
    public float approachHeight = 0.5f;
    [Tooltip("Sideways bend of the curve (metres). Positive bends right of the flight direction.")]
    public float sideCurve = 1.5f;

    [Header("Motion")]
    public float flySpeed = 3f;
    [Tooltip("0 = constant speed, 1 = strong slowdown into the landing.")]
    [Range(0f, 1f)] public float landingSlowdown = 0.6f;
    [Tooltip("Portion of the path (from the end) over which the body levels out to upright.")]
    [Range(0f, 1f)] public float levelOutPortion = 0.2f;
    public float turnSmoothing = 10f;
    public float startDelay = 0f;

    [Header("Landing")]
    [Tooltip("Optional: raycast down from heronEnd onto these layers to find the rock surface. Leave empty to land exactly on heronEnd.")]
    public LayerMask groundMask = 0;
    [Tooltip("Seconds before arrival to start blending into the landing animation.")]
    public float landingLeadTime = 0.4f;
    public bool playLandingAnimation = true;
    [Tooltip("Seconds to smoothly correct any foot offset after touching down.")]
    public float settleTime = 0.3f;

    [Header("On Landing")]
    [Tooltip("Water animation to start as the heron comes in to land. Found automatically if left empty.")]
    public PlayAnimationOnTap waterAnimation;
    [Tooltip("Seconds before touchdown to start the water animation. 0 = start exactly on landing.")]
    public float waterLeadTime = 3f;
    [Tooltip("Anything else to trigger on touchdown.")]
    public UnityEngine.Events.UnityEvent onLanded;

    [Header("Animation")]
    public string flyState = "fly";
    public string landingState = "landing";
    public string idleState = "idle";
    public string eatState = "eat";
    public float crossFade = 0.25f;
    public float idleDuration = 3f;
    [Tooltip("How many times to play the eat animation before flying away. 0 = never fly away.")]
    public int eatCycles = 2;

    [Header("Fly Away")]
    [Tooltip("Where the heron leaves to. Falls back to a scene object named 'HeronFlyAwayTarget'.")]
    public Transform heronFlyAwayTarget;
    public string takeoffState = "takeoff";
    [Tooltip("Seconds of idle after the last bite before taking off.")]
    public float flyAwayDelay = 1f;
    public float flyAwaySpeed = 4f;
    [Tooltip("Frame of the takeoff clip at which the flight path starts blending in. Before this the clip's own " +
             "baked root motion carries the heron.")]
    public int takeoffPathStartFrame = 22;
    [Tooltip("Seconds over which the path takes over from the clip's root motion once it starts.")]
    public float takeoffPathBlendSeconds = 1f;
    [Tooltip("Multiplier on the takeoff clip's root motion, in case the clip moves too little or too far for the scene.")]
    public float takeoffRootMotionScale = 1f;
    [Tooltip("How far it carries on straight ahead before curving toward the target (fraction of the distance).")]
    [Range(0f, 1f)] public float flyAwayForwardStretch = 0.45f;
    [Tooltip("Extra height gained over that first straight-ahead stretch.")]
    public float flyAwayClimb = 1.5f;
    [Tooltip("How directly it comes into the target at the end (fraction of the distance).")]
    [Range(0f, 1f)] public float flyAwayApproachStretch = 0.3f;
    [Tooltip("Switch the heron off once it reaches the target.")]
    public bool deactivateOnFlyAway = true;

    Animator animator;
    Vector3 localFeetOffset;   // feet position in heron local space, measured in the standing pose
    Vector3 p0, p1, p2, p3;    // bezier control points (root positions)
    Vector3 landPoint;
    Quaternion landRotation;
    Vector3 splineStartFix, splineEndFix;
    bool useGeneratedPath;     // set while flying away, so the arrival spline is ignored

    const int ArcSamples = 64;
    float[] arcTable;

    void Awake()
    {
        animator = GetComponent<Animator>();

        // The pack's demo script drives the animator from keyboard/mouse input; it would fight us.
        var demo = GetComponent<Heron>();
        if (demo != null) demo.enabled = false;

        if (heronStart == null) heronStart = FindTransform("heronStart");
        if (heronEnd == null) heronEnd = FindTransform("heronEnd");
        if (heronFeet == null) heronFeet = FindChild(transform, "heronFeet");
        if (heronFlyAwayTarget == null) heronFlyAwayTarget = FindTransform("HeronFlyAwayTarget");

        // Awake runs before every Start, so this stops the water playing itself at scene load.
        if (waterAnimation == null) waterAnimation = FindObjectOfType<PlayAnimationOnTap>(true);
        if (waterAnimation != null) waterAnimation.SuppressPlayOnStart();
    }

    IEnumerator Start()
    {
        if (heronStart == null || heronEnd == null)
        {
            Debug.LogError("HeronFlight: heronStart / heronEnd not assigned or found.", this);
            yield break;
        }

        // Measure where the feet are relative to the root while standing, so we can
        // land the root such that the feet hit the landing point.
        animator.Play(idleState, 0, 0f);
        animator.Update(0f);
        localFeetOffset = heronFeet != null ? transform.InverseTransformPoint(heronFeet.position) : Vector3.zero;

        BuildPath();

        transform.SetPositionAndRotation(p0, Quaternion.LookRotation(Tangent(0f)));

        if (startDelay > 0f)
        {
            SetVisible(false);
            yield return new WaitForSeconds(startDelay);
            SetVisible(true);
        }
        animator.Play(flyState, 0, 0f);

        yield return Fly();
        yield return Land();
        yield return IdleThenEat();

        if (eatCycles > 0 && heronFlyAwayTarget != null)
            yield return FlyAway();
        else if (eatCycles > 0)
            Debug.LogWarning("HeronFlight: heronFlyAwayTarget not assigned or found; staying on the rocks.", this);
    }

    // The controller uses root-motion clips. Handling OnAnimatorMove keeps Unity from applying
    // them, so this script alone decides where the heron goes. The one exception is takeoff,
    // whose movement is banked here and spent by FlyAway while it blends onto the path.
    bool bankTakeoffRootMotion;
    Vector3 bankedRootMotion;

    void OnAnimatorMove()
    {
        if (bankTakeoffRootMotion)
            bankedRootMotion += animator.deltaPosition * takeoffRootMotionScale;
    }

    // Turns the body toward the direction it actually moved this frame.
    void FaceMovement(Vector3 previous, Vector3 current)
    {
        Vector3 velocity = current - previous;
        if (velocity.sqrMagnitude < 1e-8f) return;
        Quaternion look = Quaternion.LookRotation(velocity.normalized, Vector3.up);
        transform.rotation = Quaternion.Slerp(transform.rotation, look, 1f - Mathf.Exp(-turnSmoothing * Time.deltaTime));
    }

    void BuildPath()
    {
        landPoint = heronEnd.position;
        if (groundMask.value != 0 &&
            Physics.Raycast(heronEnd.position + Vector3.up * 2f, Vector3.down, out var hit, 10f, groundMask, QueryTriggerInteraction.Ignore))
        {
            landPoint = hit.point;
        }

        if (UseSpline)
        {
            // Face along the spline's final direction, upright, when standing on the rocks.
            Vector3 endDir = flightPath.GetTangent(1f);
            endDir.y = 0f;
            if (endDir.sqrMagnitude < 0.0001f) endDir = heronEnd.forward;
            landRotation = Quaternion.LookRotation(endDir.normalized, Vector3.up);

            Vector3 splineLandRoot = landPoint - landRotation * Vector3.Scale(localFeetOffset, transform.lossyScale);
            splineStartFix = heronStart.position - flightPath.GetPoint(0f);
            splineEndFix = splineLandRoot - flightPath.GetPoint(1f);
            p0 = heronStart.position;
            p3 = splineLandRoot;
            BuildArcTable();
            return;
        }

        Vector3 start = heronStart.position;
        Vector3 flat = landPoint - start;
        flat.y = 0f;
        if (flat.sqrMagnitude < 0.0001f) flat = heronStart.forward;
        float dist = flat.magnitude;
        Vector3 dir = flat.normalized;
        Vector3 side = Vector3.Cross(Vector3.up, dir);

        // Face along the final approach, upright, when standing on the rocks.
        Vector3 approachDir = dir - side * (sideCurve / Mathf.Max(dist, 0.01f));
        approachDir.y = 0f;
        landRotation = Quaternion.LookRotation(approachDir.normalized, Vector3.up);

        Vector3 scaledFeet = Vector3.Scale(localFeetOffset, transform.lossyScale);
        Vector3 landRoot = landPoint - landRotation * scaledFeet;

        p0 = start;
        p3 = landRoot;
        p1 = p0 + dir * dist * departureStretch + side * sideCurve;
        p2 = p3 - (landRotation * Vector3.forward) * dist * approachStretch + Vector3.up * approachHeight;

        BuildArcTable();
    }

    // Arc-length table so speed is even along the curve.
    void BuildArcTable()
    {
        arcTable = new float[ArcSamples + 1];
        Vector3 prev = p0;
        for (int i = 1; i <= ArcSamples; i++)
        {
            Vector3 pt = Bezier((float)i / ArcSamples);
            arcTable[i] = arcTable[i - 1] + Vector3.Distance(prev, pt);
            prev = pt;
        }
    }

    IEnumerator Fly()
    {
        float length = arcTable[ArcSamples];
        float duration = Mathf.Max(length / Mathf.Max(flySpeed, 0.01f), 0.01f);
        bool landingStarted = false;
        bool waterStarted = false;

        for (float time = 0f; time < duration; time += Time.deltaTime)
        {
            if (!waterStarted && duration - time <= waterLeadTime)
            {
                StartWater();
                waterStarted = true;
            }

            float u = time / duration;
            float s = Mathf.Lerp(u, 1f - (1f - u) * (1f - u), landingSlowdown); // ease-out into landing
            float t = DistanceToT(s * length);

            transform.position = Bezier(t);

            // Z forward points along the flight direction, leveling out for touchdown.
            Quaternion look = Quaternion.LookRotation(Tangent(t), Vector3.up);
            float level = levelOutPortion > 0f ? Mathf.InverseLerp(1f - levelOutPortion, 1f, s) : 0f;
            Quaternion target = Quaternion.Slerp(look, landRotation, level);
            transform.rotation = Quaternion.Slerp(transform.rotation, target, 1f - Mathf.Exp(-turnSmoothing * Time.deltaTime));

            if (playLandingAnimation && !landingStarted && duration - time <= landingLeadTime)
            {
                animator.CrossFadeInFixedTime(landingState, crossFade);
                landingStarted = true;
            }
            yield return null;
        }

        transform.SetPositionAndRotation(p3, landRotation);
        if (playLandingAnimation && !landingStarted)
            animator.CrossFadeInFixedTime(landingState, crossFade);
    }

    bool waterPlaying;

    void StartWater()
    {
        if (waterPlaying || waterAnimation == null) return;
        waterPlaying = true;
        waterAnimation.Play();
    }

    IEnumerator Land()
    {
        if (playLandingAnimation)
        {
            // Wait for the landing clip to finish (the controller exits landing -> idle on its own).
            yield return null;
            while (animator.GetCurrentAnimatorStateInfo(0).IsName(landingState) || animator.IsInTransition(0))
                yield return null;
        }
        else
        {
            animator.CrossFadeInFixedTime(idleState, crossFade);
        }

        StartWater();   // no-op if the approach already started it
        onLanded.Invoke();

        // Nudge the root so heronFeet ends exactly on the landing point.
        if (heronFeet != null && settleTime > 0f)
        {
            for (float time = 0f; time < settleTime; time += Time.deltaTime)
            {
                yield return new WaitForEndOfFrame(); // feet reflect this frame's pose
                Vector3 error = landPoint - heronFeet.position;
                transform.position += error * Mathf.Clamp01(Time.deltaTime / Mathf.Max(settleTime - time, Time.deltaTime));
            }
        }
    }

    IEnumerator IdleThenEat()
    {
        if (!animator.GetCurrentAnimatorStateInfo(0).IsName(idleState))
            animator.CrossFadeInFixedTime(idleState, crossFade);

        for (int i = 0; i < Mathf.Max(eatCycles, 1); i++)
        {
            yield return new WaitForSeconds(idleDuration);
            animator.CrossFadeInFixedTime(eatState, crossFade);

            // eat -> idle happens automatically in the controller when the clip ends.
            yield return null;
            while (animator.GetCurrentAnimatorStateInfo(0).IsName(eatState) || animator.IsInTransition(0))
                yield return null;
        }
    }

    IEnumerator FlyAway()
    {
        yield return new WaitForSeconds(flyAwayDelay);

        // Phase 1: the takeoff clip's own root motion moves the heron until the blend point.
        animator.CrossFadeInFixedTime(takeoffState, crossFade);
        bankTakeoffRootMotion = true;
        bankedRootMotion = Vector3.zero;

        float blendTime = Mathf.Max(takeoffPathBlendSeconds, 0.05f);
        for (float elapsed = 0f; elapsed < 5f; elapsed += Time.deltaTime)
        {
            yield return null;
            Vector3 before = transform.position;
            transform.position += bankedRootMotion;
            bankedRootMotion = Vector3.zero;
            FaceMovement(before, transform.position);

            var state = animator.GetCurrentAnimatorStateInfo(0);
            if (state.IsName(takeoffState))
            {
                // Convert the frame number to clip time using the clip's own frame rate.
                var clips = animator.GetCurrentAnimatorClipInfo(0);
                float frameRate = clips.Length > 0 && clips[0].clip != null ? clips[0].clip.frameRate : 30f;
                float startNormalized = takeoffPathStartFrame / Mathf.Max(state.length * frameRate, 1f);
                if (state.normalizedTime >= startNormalized)
                    break;
            }
            else if (elapsed > crossFade && !animator.IsInTransition(0))
            {
                break;   // clip already finished (or never entered): go straight to the path
            }
        }

        // Phase 2: path from wherever the takeoff left the heron, carrying on straight ahead
        // first, then curving over to the target.
        Vector3 start = transform.position;
        Vector3 end = heronFlyAwayTarget.position;
        float dist = Vector3.Distance(start, end);
        Vector3 ahead = transform.forward;
        ahead.y = 0f;
        if (ahead.sqrMagnitude < 0.0001f) ahead = (end - start).normalized;
        ahead.Normalize();

        p0 = start;
        p3 = end;
        p1 = p0 + ahead * dist * flyAwayForwardStretch + Vector3.up * flyAwayClimb;
        p2 = p3 - (end - start).normalized * dist * flyAwayApproachStretch;
        useGeneratedPath = true;   // the flight path spline describes the arrival, not the exit
        BuildArcTable();

        // takeoff -> fly happens automatically in the controller when the clip ends, so the
        // hand-off is just checked along the way rather than waited for up front.
        bool takeoffDone = false;
        Vector3 rootMotionPos = start;   // where the clip alone would have put the heron

        float length = arcTable[ArcSamples];
        float duration = Mathf.Max(length / Mathf.Max(flyAwaySpeed, 0.01f), 0.01f);
        for (float time = 0f; time < duration; time += Time.deltaTime)
        {
            float t = DistanceToT(time / duration * length);   // even speed, no slowdown
            Vector3 pathPos = Bezier(t);

            // Over the rest of the takeoff clip, hand over from root motion to the path.
            float w = Mathf.SmoothStep(0f, 1f, time / blendTime);
            if (w < 1f)
            {
                rootMotionPos += bankedRootMotion;
                bankedRootMotion = Vector3.zero;
            }
            else if (bankTakeoffRootMotion)
            {
                bankTakeoffRootMotion = false;
                bankedRootMotion = Vector3.zero;
            }

            Vector3 before = transform.position;
            transform.position = Vector3.Lerp(rootMotionPos, pathPos, w);
            FaceMovement(before, transform.position);

            if (!takeoffDone && time > 0f &&
                !animator.GetCurrentAnimatorStateInfo(0).IsName(takeoffState) && !animator.IsInTransition(0))
            {
                takeoffDone = true;
                if (!animator.GetCurrentAnimatorStateInfo(0).IsName(flyState))
                    animator.CrossFadeInFixedTime(flyState, crossFade);
            }
            yield return null;
        }

        bankTakeoffRootMotion = false;
        transform.position = p3;
        if (deactivateOnFlyAway) gameObject.SetActive(false);
    }

    bool UseSpline => !useGeneratedPath && flightPath != null && flightPath.Count >= 2;

    Vector3 Bezier(float t)
    {
        if (UseSpline)
        {
            // Keep the drawn shape, but ease the ends onto the start point and landing spot.
            float blend = Mathf.SmoothStep(0f, 1f, t);
            return flightPath.GetPoint(t) + splineStartFix * (1f - blend) + splineEndFix * blend;
        }
        float u = 1f - t;
        return u * u * u * p0 + 3f * u * u * t * p1 + 3f * u * t * t * p2 + t * t * t * p3;
    }

    Vector3 Tangent(float t)
    {
        if (UseSpline)
        {
            const float h = 0.002f;
            Vector3 delta = Bezier(Mathf.Min(t + h, 1f)) - Bezier(Mathf.Max(t - h, 0f));
            return delta.sqrMagnitude > 1e-8f ? delta.normalized : transform.forward;
        }
        float u = 1f - t;
        Vector3 d = 3f * u * u * (p1 - p0) + 6f * u * t * (p2 - p1) + 3f * t * t * (p3 - p2);
        return d.sqrMagnitude > 1e-6f ? d.normalized : transform.forward;
    }

    float DistanceToT(float distance)
    {
        for (int i = 1; i <= ArcSamples; i++)
        {
            if (arcTable[i] >= distance)
            {
                float seg = arcTable[i] - arcTable[i - 1];
                float f = seg > 0f ? (distance - arcTable[i - 1]) / seg : 0f;
                return (i - 1 + f) / ArcSamples;
            }
        }
        return 1f;
    }

    void SetVisible(bool visible)
    {
        foreach (var r in GetComponentsInChildren<Renderer>()) r.enabled = visible;
        animator.enabled = visible;
    }

    // Case-insensitive, includes inactive objects (GameObject.Find does neither).
    static Transform FindTransform(string name)
    {
        var scene = UnityEngine.SceneManagement.SceneManager.GetActiveScene();
        foreach (var root in scene.GetRootGameObjects())
            foreach (var t in root.GetComponentsInChildren<Transform>(true))
                if (string.Equals(t.name, name, System.StringComparison.OrdinalIgnoreCase)) return t;
        return null;
    }

    static Transform FindChild(Transform root, string name)
    {
        foreach (var t in root.GetComponentsInChildren<Transform>(true))
            if (string.Equals(t.name, name, System.StringComparison.OrdinalIgnoreCase)) return t;
        return FindTransform(name);
    }

#if UNITY_EDITOR
    void OnDrawGizmosSelected()
    {
        if (!Application.isPlaying)
        {
            if (heronStart == null) heronStart = FindTransform("heronStart");
            if (heronEnd == null) heronEnd = FindTransform("heronEnd");
            if (heronStart == null || heronEnd == null) return;
            BuildPath();
        }
        Gizmos.color = Color.cyan;
        Vector3 prev = p0;
        for (int i = 1; i <= 40; i++)
        {
            Vector3 pt = Bezier(i / 40f);
            Gizmos.DrawLine(prev, pt);
            prev = pt;
        }
        Gizmos.color = Color.yellow;
        Gizmos.DrawWireSphere(landPoint, 0.05f);
    }
#endif
}
