using System.Collections;
using UnityEngine;

/// <summary>
/// Rigid underwater sway: rotates the plant around a base pivot so the bottom stays fixed
/// and motion increases toward the top. Starts after the grow Animator clip finishes.
/// </summary>
[DefaultExecutionOrder(200)]
[DisallowMultipleComponent]
public class PlantUnderwaterSway : MonoBehaviour
{
    [System.Serializable]
    public struct SwayDefaults
    {
        [Tooltip("Blend from grow pose and ramp sway amplitude over this duration.")]
        public float poseHandoffSeconds;
        public float primarySwayAngleDegrees;
        [Range(0f, 1f)]
        public float secondarySwayAngleFactor;
        public float swaySpeed;
        public float secondarySwaySpeedFactor;
        [Tooltip("Wait until the grow clip finishes before swaying.")]
        public bool waitForGrowthComplete;
        public int animatorLayer;
        public bool disableAnimatorAfterGrowth;

        public static SwayDefaults CreateBuiltIn()
        {
            return new SwayDefaults
            {
                poseHandoffSeconds = 1.5f,
                primarySwayAngleDegrees = 3.5f,
                secondarySwayAngleFactor = 0.4f,
                swaySpeed = 0.6f,
                secondarySwaySpeedFactor = 1.17f,
                waitForGrowthComplete = true,
                animatorLayer = 0,
                disableAnimatorAfterGrowth = true,
            };
        }
    }

    [Header("Timing")]
    [Tooltip("Wait until the default-layer grow clip has finished before swaying.")]
    [SerializeField] private bool waitForGrowthComplete = true;
    [SerializeField] private int animatorLayer;

    [Header("Pivot")]
    [Tooltip("World pivot for rotation (bottom of plant). Leave empty to use renderer bounds min Y.")]
    [SerializeField] private Transform basePivot;
    [Tooltip("Local-space offset added to auto-detected base (e.g. nudge pivot into the ground).")]
    [SerializeField] private Vector3 basePivotOffset;

    [Header("Sway")]
    [SerializeField] private bool swayEnabled = true;
    [Tooltip("Primary sway axis in world space (normalized).")]
    [SerializeField] private Vector3 primarySwayAxis = Vector3.right;
    [Tooltip("Secondary sway axis (orthogonal wobble).")]
    [SerializeField] private Vector3 secondarySwayAxis = Vector3.forward;
    [SerializeField] private float primarySwayAngleDegrees = 5f;
    [Range(0f, 1f)]
    [SerializeField] private float secondarySwayAngleFactor = 0.55f;
    [SerializeField] private float swaySpeed = 0.85f;
    [SerializeField] private float secondarySwaySpeedFactor = 1.17f;
    [Tooltip("Leave at 0 for a random phase per plant.")]
    [Range(0f, 6.283185f)]
    [SerializeField] private float phaseOffset;

    [Tooltip("Disable Animator after growth so procedural sway is not overwritten.")]
    [SerializeField] private bool disableAnimatorAfterGrowth = true;
    [Tooltip("Blend from final grow geometry pose into sway rest pose, and ramp sway amplitude over this duration.")]
    [SerializeField] private float poseHandoffSeconds = 1.5f;

    private Animator _animator;
    private float _runtimePhase;
    private float _swayStartTime;
    private Transform _swayTransform;
    private Vector3 _pivotWorld;
    private Vector3 _handoffFromPosition;
    private Quaternion _handoffFromRotation;
    private Vector3 _restPosition;
    private Quaternion _restRotation;
    private bool _swayActive;
    private bool _pivotCached;
    private Coroutine _waitRoutine;

    private void OnValidate()
    {
        animatorLayer = Mathf.Max(0, animatorLayer);
        primarySwayAngleDegrees = Mathf.Max(0f, primarySwayAngleDegrees);
        swaySpeed = Mathf.Max(0f, swaySpeed);
        secondarySwaySpeedFactor = Mathf.Max(0f, secondarySwaySpeedFactor);
        secondarySwayAngleFactor = Mathf.Clamp01(secondarySwayAngleFactor);
        poseHandoffSeconds = Mathf.Max(0f, poseHandoffSeconds);

        if (primarySwayAxis.sqrMagnitude > 0.0001f)
        {
            primarySwayAxis = primarySwayAxis.normalized;
        }
        else
        {
            primarySwayAxis = Vector3.right;
        }

        if (secondarySwayAxis.sqrMagnitude > 0.0001f)
        {
            secondarySwayAxis = secondarySwayAxis.normalized;
        }
        else
        {
            secondarySwayAxis = Vector3.forward;
        }
    }

    private void OnEnable()
    {
        BeginAfterGrowth();
    }

    private void OnDisable()
    {
        StopWaiting();
        _swayActive = false;
    }

    public void ApplyDefaults(SwayDefaults defaults)
    {
        poseHandoffSeconds = Mathf.Max(0f, defaults.poseHandoffSeconds);
        primarySwayAngleDegrees = Mathf.Max(0f, defaults.primarySwayAngleDegrees);
        secondarySwayAngleFactor = Mathf.Clamp01(defaults.secondarySwayAngleFactor);
        swaySpeed = Mathf.Max(0f, defaults.swaySpeed);
        secondarySwaySpeedFactor = Mathf.Max(0f, defaults.secondarySwaySpeedFactor);
        waitForGrowthComplete = defaults.waitForGrowthComplete;
        animatorLayer = Mathf.Max(0, defaults.animatorLayer);
        disableAnimatorAfterGrowth = defaults.disableAnimatorAfterGrowth;
    }

    public static PlantUnderwaterSway EnsureConfigured(GameObject plant, SwayDefaults defaults, bool enabled)
    {
        if (!enabled || plant == null)
        {
            return null;
        }

        PlantUnderwaterSway sway = plant.GetComponent<PlantUnderwaterSway>();
        if (sway == null)
        {
            sway = plant.AddComponent<PlantUnderwaterSway>();
        }

        sway.ApplyDefaults(defaults);
        if (plant.activeInHierarchy)
        {
            sway.BeginAfterGrowth();
        }

        return sway;
    }

    /// <summary>Call when the plant is activated (e.g. from RandomOneAtATimeActivator).</summary>
    public void BeginAfterGrowth()
    {
        if (!swayEnabled)
        {
            return;
        }

        StopWaiting();
        CacheReferences();
        _swayActive = false;
        _pivotCached = false;

        if (waitForGrowthComplete && _animator != null)
        {
            _waitRoutine = StartCoroutine(WaitForGrowthThenEnableSway());
            return;
        }

        _waitRoutine = StartCoroutine(EnableSwayRoutineAndClear());
    }

    private IEnumerator EnableSwayRoutineAndClear()
    {
        yield return EnableSwayRoutine();
        _waitRoutine = null;
    }

    private void StopWaiting()
    {
        if (_waitRoutine == null)
        {
            return;
        }

        StopCoroutine(_waitRoutine);
        _waitRoutine = null;
    }

    private void CacheReferences()
    {
        _animator = GetComponentInChildren<Animator>(true);
        _swayTransform = _animator != null ? _animator.transform : transform;
    }

    private IEnumerator WaitForGrowthThenEnableSway()
    {
        yield return null;

        while (_animator != null && _animator.isActiveAndEnabled)
        {
            if (IsGrowthComplete(_animator, animatorLayer))
            {
                break;
            }

            yield return null;
        }

        yield return EnableSwayRoutine();
        _waitRoutine = null;
    }

    private static bool IsGrowthComplete(Animator animator, int layer)
    {
        if (animator == null || !animator.isActiveAndEnabled)
        {
            return true;
        }

        AnimatorStateInfo state = animator.GetCurrentAnimatorStateInfo(layer);
        if (state.loop)
        {
            return false;
        }

        if (state.normalizedTime >= 1f)
        {
            return true;
        }

        float clipLength = state.length;
        if (clipLength <= 0f)
        {
            AnimatorClipInfo[] clips = animator.GetCurrentAnimatorClipInfo(layer);
            if (clips.Length > 0 && clips[0].clip != null)
            {
                clipLength = clips[0].clip.length;
            }
        }

        return clipLength > 0f && state.normalizedTime * clipLength >= clipLength - 0.02f;
    }

    private IEnumerator EnableSwayRoutine()
    {
        if (!swayEnabled || _swayTransform == null)
        {
            yield break;
        }

        _runtimePhase = phaseOffset > 0f ? phaseOffset : UnityEngine.Random.Range(0f, Mathf.PI * 2f);

        _pivotCached = false;
        CachePivot();
        _handoffFromPosition = _swayTransform.position;
        _handoffFromRotation = _swayTransform.rotation;

        if (disableAnimatorAfterGrowth && _animator != null)
        {
            _animator.enabled = false;
            yield return null;
        }

        _restPosition = _swayTransform.position;
        _restRotation = _swayTransform.rotation;
        _swayStartTime = Time.time;
        _swayActive = true;
    }

    private void CachePivot()
    {
        if (basePivot != null)
        {
            _pivotWorld = basePivot.position + basePivotOffset;
            _pivotCached = true;
            return;
        }

        Renderer[] renderers = GetComponentsInChildren<Renderer>(true);
        if (renderers.Length == 0)
        {
            _pivotWorld = _swayTransform.position + basePivotOffset;
            _pivotCached = true;
            return;
        }

        Bounds bounds = renderers[0].bounds;
        for (int i = 1; i < renderers.Length; i++)
        {
            if (renderers[i] != null && renderers[i].enabled)
            {
                bounds.Encapsulate(renderers[i].bounds);
            }
        }

        _pivotWorld = new Vector3(bounds.center.x, bounds.min.y, bounds.center.z) + basePivotOffset;
        _pivotCached = true;
    }

    private void LateUpdate()
    {
        if (!_swayActive || _swayTransform == null)
        {
            return;
        }

        float elapsed = Time.time - _swayStartTime;
        float handoffT = poseHandoffSeconds <= 0f
            ? 1f
            : Mathf.Clamp01(elapsed / poseHandoffSeconds);
        handoffT = handoffT * handoffT * (3f - 2f * handoffT);

        Vector3 basePosition = Vector3.Lerp(_handoffFromPosition, _restPosition, handoffT);
        Quaternion baseRotation = Quaternion.Slerp(_handoffFromRotation, _restRotation, handoffT);

        float primaryAngle = Mathf.Sin(elapsed * swaySpeed + _runtimePhase)
            * primarySwayAngleDegrees
            * handoffT;
        float secondaryAngle = Mathf.Sin(
            elapsed * swaySpeed * secondarySwaySpeedFactor + _runtimePhase * 1.37f
        ) * (primarySwayAngleDegrees * secondarySwayAngleFactor * handoffT);

        _swayTransform.SetPositionAndRotation(basePosition, baseRotation);
        _swayTransform.RotateAround(_pivotWorld, primarySwayAxis, primaryAngle);
        _swayTransform.RotateAround(_pivotWorld, secondarySwayAxis, secondaryAngle);
    }
}
