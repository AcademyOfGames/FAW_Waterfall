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
    [Tooltip("When the water animation starts, relative to the heron landing. Positive = seconds BEFORE touchdown " +
             "(during the approach). 0 = when the landing clip finishes. Negative = seconds AFTER the landing clip finishes.")]
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
    [Tooltip("Log the idle countdown (time left before the next eat) every frame while the heron is idling on the rocks.")]
    public bool logIdleCountdown = true;

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
    [Tooltip("Steepest nose-up angle (degrees) the heron may pitch to while the takeoff clip is driving it. Stops it " +
             "pointing straight up on the hop, since the clip's root motion is mostly vertical. Relaxes to unlimited " +
             "over the path blend.")]
    [Range(0f, 90f)] public float takeoffMaxPitch = 20f;
    [Tooltip("Multiplier on the takeoff clip's root motion, in case the clip moves too little or too far for the scene.")]
    public float takeoffRootMotionScale = 1f;
    [Tooltip("How far it carries on straight ahead before curving toward the target (fraction of the distance).")]
    [Range(0f, 1f)] public float flyAwayForwardStretch = 0.45f;
    [Tooltip("Extra height gained over that first straight-ahead stretch.")]
    public float flyAwayClimb = 1.5f;
    [Tooltip("How directly it comes into the target at the end (fraction of the distance).")]
    [Range(0f, 1f)] public float flyAwayApproachStretch = 0.3f;
    [Tooltip("Sideways bend in the first half of the exit, in metres. Positive = right of the line to the target, negative = left.")]
    public float flyAwaySideCurve = 0f;
    [Tooltip("Sideways bend in the second half of the exit, in metres. Same sign as above; use the opposite sign for an S-curve.")]
    public float flyAwaySideCurveEnd = 0f;
    [Tooltip("Switch the heron off once it reaches the target.")]
    public bool deactivateOnFlyAway = true;

    Animator animator;
    Vector3 localFeetOffset;   // feet position in heron local space, measured in the standing pose
    Vector3 p0, p1, p2, p3;    // bezier control points (root positions)
    Vector3 landPoint;
    Quaternion landRotation;
    Vector3 splineStartFix, splineEndFix;

    // The path is authored in world space at build time, but the whole prefab can move afterwards
    // (AR floor refinement, anchor drift). Everything cached above is therefore re-expressed each
    // frame relative to the heron's parent, so the heron keeps landing on the rock wherever it goes.
    Transform frame;
    Matrix4x4 frameInverseAtBuild = Matrix4x4.identity;
    Quaternion frameRotationAtBuild = Quaternion.identity;

    void SnapshotFrame()
    {
        frame = transform.parent;
        if (frame == null)
        {
            frameInverseAtBuild = Matrix4x4.identity;
            frameRotationAtBuild = Quaternion.identity;
            return;
        }
        frameInverseAtBuild = frame.worldToLocalMatrix;
        frameRotationAtBuild = frame.rotation;
    }

    /// <summary>A world point captured at build time, moved to where the prefab is now.</summary>
    Vector3 Now(Vector3 worldAtBuild) =>
        frame == null ? worldAtBuild : frame.localToWorldMatrix.MultiplyPoint3x4(frameInverseAtBuild.MultiplyPoint3x4(worldAtBuild));

    Vector3 NowDir(Vector3 worldDirAtBuild) =>
        frame == null ? worldDirAtBuild : frame.localToWorldMatrix.MultiplyVector(frameInverseAtBuild.MultiplyVector(worldDirAtBuild));

    Quaternion NowRot(Quaternion worldRotAtBuild) =>
        frame == null ? worldRotAtBuild : frame.rotation * Quaternion.Inverse(frameRotationAtBuild) * worldRotAtBuild;
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

        transform.SetPositionAndRotation(Now(p0), Quaternion.LookRotation(Tangent(0f)));

        if (startDelay > 0f)
        {
            SetVisible(false);
            yield return new WaitForSeconds(startDelay);
            SetVisible(true);
        }
        animator.Play(flyState, 0, 0f);

        yield return Fly();
        Phase("Fly finished, starting Land");
        yield return Land();
        Phase("Land finished, starting IdleThenEat");
        yield return IdleThenEat();
        Phase($"IdleThenEat finished (eatCycles={eatCycles}, flyAwayTarget={(heronFlyAwayTarget != null ? heronFlyAwayTarget.name : "null")})");

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

    void Phase(string message)
    {
        if (logIdleCountdown) Debug.Log($"HeronFlight [{Time.time:F2}s] {message}", this);
    }

    // >= 0 only while IdleThenEat is counting down to the next eat.
    float idleTimeLeft = -1f;
    int idleCycle;

    void Update()
    {
        if (!logIdleCountdown || idleTimeLeft < 0f || animator == null) return;
        var state = animator.GetCurrentAnimatorStateInfo(0);
        string stateName = state.IsName(idleState) ? idleState
            : state.IsName(landingState) ? landingState
            : state.IsName(eatState) ? eatState : "other";
        Debug.Log($"HeronFlight idle {idleCycle}/{Mathf.Max(eatCycles, 1)}: {idleTimeLeft:F2}s left before eat " +
                  $"(animator state={stateName}, inTransition={animator.IsInTransition(0)})", this);
    }

    void OnAnimatorMove()
    {
        if (bankTakeoffRootMotion)
            bankedRootMotion += animator.deltaPosition * takeoffRootMotionScale;
    }

    // Turns the body toward the direction it actually moved this frame.
    void FaceMovement(Vector3 previous, Vector3 current, float maxPitchDegrees = 90f)
    {
        Vector3 velocity = current - previous;
        if (velocity.sqrMagnitude < 1e-8f) return;
        Vector3 dir = velocity.normalized;

        if (maxPitchDegrees < 90f)
        {
            // Clamp how far above/below horizontal the facing may tilt. If the movement is nearly
            // vertical there's no useful heading in it, so keep the current one.
            Vector3 flat = new Vector3(dir.x, 0f, dir.z);
            if (flat.sqrMagnitude < 1e-6f)
            {
                flat = transform.forward;
                flat.y = 0f;
                if (flat.sqrMagnitude < 1e-6f) return;
            }
            flat.Normalize();
            float pitch = Mathf.Clamp(Mathf.Asin(Mathf.Clamp(dir.y, -1f, 1f)) * Mathf.Rad2Deg, -maxPitchDegrees, maxPitchDegrees);
            dir = Quaternion.AngleAxis(-pitch, Vector3.Cross(Vector3.up, flat)) * flat;
        }

        Quaternion look = Quaternion.LookRotation(dir, Vector3.up);
        transform.rotation = Quaternion.Slerp(transform.rotation, look, 1f - Mathf.Exp(-turnSmoothing * Time.deltaTime));
    }

    void BuildPath()
    {
        SnapshotFrame();
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
            Quaternion target = Quaternion.Slerp(look, NowRot(landRotation), level);
            transform.rotation = Quaternion.Slerp(transform.rotation, target, 1f - Mathf.Exp(-turnSmoothing * Time.deltaTime));

            if (playLandingAnimation && !landingStarted && duration - time <= landingLeadTime)
            {
                animator.CrossFadeInFixedTime(landingState, crossFade);
                landingStarted = true;
            }
            yield return null;
        }

        transform.SetPositionAndRotation(Now(p3), NowRot(landRotation));
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
            float nextLog = 0f;
            while (animator.GetCurrentAnimatorStateInfo(0).IsName(landingState) || animator.IsInTransition(0))
            {
                if (logIdleCountdown && Time.time >= nextLog)
                {
                    nextLog = Time.time + 0.5f;
                    var st = animator.GetCurrentAnimatorStateInfo(0);
                    Phase($"Land: waiting for '{landingState}' clip to finish (isLanding={st.IsName(landingState)} " +
                          $"normalizedTime={st.normalizedTime:F2} inTransition={animator.IsInTransition(0)} " +
                          $"next={(animator.IsInTransition(0) ? animator.GetNextAnimatorStateInfo(0).shortNameHash : 0)})");
                }
                yield return null;
            }
            Phase("Land: landing clip finished");
        }
        else
        {
            animator.CrossFadeInFixedTime(idleState, crossFade);
        }

        if (waterLeadTime < 0f)
        {
            Phase($"Land: waiting {-waterLeadTime:F2}s (negative waterLeadTime) before water");
            yield return new WaitForSeconds(-waterLeadTime);
        }
        StartWater();   // no-op if the approach already started it
        onLanded.Invoke();
        Phase($"Land: water started, onLanded invoked, settling for {settleTime:F2}s (heronFeet={(heronFeet != null ? heronFeet.name : "null")})");

        // Nudge the root so heronFeet ends exactly on the landing point. The correction runs in
        // LateUpdate (after the animator has posed the feet). WaitForEndOfFrame is deliberately
        // avoided: it never fires while the Game view isn't rendering, which hung the sequence.
        if (heronFeet != null && settleTime > 0f)
        {
            settleRemaining = settleTime;
            while (settleRemaining > 0f)
                yield return null;
        }
        Phase("Land: settle finished");
    }

    float settleRemaining;

    void LateUpdate()
    {
        if (settleRemaining <= 0f || heronFeet == null) return;
        Vector3 error = Now(landPoint) - heronFeet.position;
        transform.position += error * Mathf.Clamp01(Time.deltaTime / Mathf.Max(settleRemaining, Time.deltaTime));
        settleRemaining -= Time.deltaTime;
    }

    IEnumerator IdleThenEat()
    {
        if (!animator.GetCurrentAnimatorStateInfo(0).IsName(idleState))
            animator.CrossFadeInFixedTime(idleState, crossFade);

        for (int i = 0; i < Mathf.Max(eatCycles, 1); i++)
        {
            idleCycle = i + 1;
            idleTimeLeft = idleDuration;
            while (idleTimeLeft > 0f)
            {
                yield return null;
                idleTimeLeft -= Time.deltaTime;
            }
            idleTimeLeft = -1f;
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
            FaceMovement(before, transform.position, takeoffMaxPitch);

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

        Vector3 toTarget = (end - start).normalized;
        Vector3 exitSide = ExitSide(start, end);
        SnapshotFrame();
        p0 = start;
        p3 = end;
        p1 = p0 + ahead * dist * flyAwayForwardStretch + Vector3.up * flyAwayClimb + exitSide * flyAwaySideCurve;
        p2 = p3 - toTarget * dist * flyAwayApproachStretch + exitSide * flyAwaySideCurveEnd;
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
            FaceMovement(before, transform.position, Mathf.Lerp(takeoffMaxPitch, 90f, w));

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
        transform.position = Now(p3);
        if (deactivateOnFlyAway) gameObject.SetActive(false);
    }

    bool UseSpline => !useGeneratedPath && flightPath != null && flightPath.Count >= 2;

    /// <summary>Horizontal "right" of the straight line from the exit start to the target.</summary>
    static Vector3 ExitSide(Vector3 start, Vector3 end)
    {
        Vector3 flat = end - start;
        flat.y = 0f;
        if (flat.sqrMagnitude < 0.0001f) return Vector3.right;
        return Vector3.Cross(Vector3.up, flat.normalized);
    }

    Vector3 Bezier(float t)
    {
        if (UseSpline)
        {
            // Keep the drawn shape, but ease the ends onto the start point and landing spot.
            float blend = Mathf.SmoothStep(0f, 1f, t);
            // The spline lives inside the prefab, so GetPoint is already current; only the fix-ups need moving.
            return flightPath.GetPoint(t) + NowDir(splineStartFix) * (1f - blend) + NowDir(splineEndFix) * blend;
        }
        float u = 1f - t;
        return Now(u * u * u * p0 + 3f * u * u * t * p1 + 3f * u * t * t * p2 + t * t * t * p3);
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
        Vector3 d = NowDir(3f * u * u * (p1 - p0) + 6f * u * t * (p2 - p1) + 3f * t * t * (p3 - p2));
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
        Vector3 prev = Now(p0);
        for (int i = 1; i <= 40; i++)
        {
            Vector3 pt = Bezier(i / 40f);
            Gizmos.DrawLine(prev, pt);
            prev = pt;
        }
        Gizmos.color = Color.yellow;
        Gizmos.DrawWireSphere(landPoint, 0.05f);

        // Exit curve preview. At runtime the exit starts wherever the takeoff clip's root motion
        // leaves the heron, so this is drawn from the landing spot facing the landing direction:
        // same shape, just anchored a little earlier than the real thing.
        if (Application.isPlaying) return;   // in play mode the cyan curve above becomes the exit path once FlyAway runs
        if (heronFlyAwayTarget == null) heronFlyAwayTarget = FindTransform("HeronFlyAwayTarget");
        if (heronFlyAwayTarget == null) return;

        Vector3 exitStart = p3;
        Vector3 exitEnd = heronFlyAwayTarget.position;
        float exitDist = Vector3.Distance(exitStart, exitEnd);
        Vector3 ahead = landRotation * Vector3.forward;
        ahead.y = 0f;
        if (ahead.sqrMagnitude < 0.0001f) ahead = (exitEnd - exitStart).normalized;
        ahead.Normalize();
        Vector3 exitSide = ExitSide(exitStart, exitEnd);
        Vector3 e0 = exitStart;
        Vector3 e3 = exitEnd;
        Vector3 e1 = e0 + ahead * exitDist * flyAwayForwardStretch + Vector3.up * flyAwayClimb + exitSide * flyAwaySideCurve;
        Vector3 e2 = e3 - (exitEnd - exitStart).normalized * exitDist * flyAwayApproachStretch + exitSide * flyAwaySideCurveEnd;

        Gizmos.color = new Color(1f, 0.5f, 0f);   // orange
        prev = e0;
        for (int i = 1; i <= 40; i++)
        {
            float t = i / 40f, u = 1f - t;
            Vector3 pt = u * u * u * e0 + 3f * u * u * t * e1 + 3f * u * t * t * e2 + t * t * t * e3;
            Gizmos.DrawLine(prev, pt);
            prev = pt;
        }
        Gizmos.DrawWireSphere(exitEnd, 0.05f);
    }
#endif
}
