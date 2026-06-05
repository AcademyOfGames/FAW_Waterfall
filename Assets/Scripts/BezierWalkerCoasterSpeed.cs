using System.Collections;
using System.Collections.Generic;
using BezierSolution;
using UnityEngine;

/// <summary>
/// Drives a BezierWalkerWithSpeed with rollercoaster-like motion: speed builds on
/// downhill sections, bleeds off on climbs, and briefly pauses at local peaks.
/// </summary>
[DefaultExecutionOrder(-100)]
[RequireComponent(typeof(BezierWalkerWithSpeed))]
public class BezierWalkerCoasterSpeed : MonoBehaviour
{
    [SerializeField] private BezierWalkerWithSpeed walker;

    [Header("Speed")]
    [Tooltip("Target cruise speed on flat sections.")]
    [SerializeField] private float baseSpeed = 5f;
    [SerializeField] private float minSpeed = 0.25f;
    [SerializeField] private float maxSpeed = 30f;

    [Header("Gravity Feel")]
    [Tooltip("How quickly downhill slopes add speed and uphill slopes remove it.")]
    [SerializeField] private float gravityInfluence = 18f;
    [Tooltip("Extra multiplier when the path drops.")]
    [SerializeField] private float downhillBoost = 1.6f;
    [Tooltip("Extra multiplier when the path rises.")]
    [SerializeField] private float uphillDrag = 1.2f;
    [Tooltip("How quickly speed eases back toward baseSpeed on nearly flat track.")]
    [SerializeField] private float flatRecovery = 1.5f;

    [Header("Crest Pause")]
    [Tooltip("Seconds to hold still when passing a local peak.")]
    [SerializeField] private float crestHoldSeconds = 1f;
    [Tooltip("Path slope must be flatter than this to count as a crest.")]
    [SerializeField] private float crestSlopeThreshold = 0.1f;
    [Tooltip("Normalized-T distance used to detect a local height maximum.")]
    [SerializeField] private float crestSampleDelta = 0.006f;
    [Tooltip("Minimum normalized-T travel before the same crest can trigger again.")]
    [SerializeField] private float crestRetriggerDistance = 0.04f;

    [Header("Particles")]
    [Tooltip("Child particle systems to stop emitting after the delay. Auto-filled from children when empty.")]
    [SerializeField] private ParticleSystem[] childParticles;
    [Tooltip("Seconds before child particles stop spawning new particles. Existing particles finish naturally.")]
    [SerializeField] private float stopParticleEmissionAfterSeconds = 35f;

    private float _currentSpeed;
    private float _holdTimer;
    private float _lastCrestT = -1f;

    private void Reset()
    {
        walker = GetComponent<BezierWalkerWithSpeed>();
    }

    private void Awake()
    {
        if (!walker)
        {
            walker = GetComponent<BezierWalkerWithSpeed>();
        }

        _currentSpeed = walker != null ? Mathf.Max(walker.speed, baseSpeed) : baseSpeed;
        CacheChildParticles();
    }

    private void Start()
    {
        if (stopParticleEmissionAfterSeconds > 0f && childParticles != null && childParticles.Length > 0)
        {
            StartCoroutine(StopChildParticleEmissionAfterDelay());
        }
    }

    private void OnValidate()
    {
        baseSpeed = Mathf.Max(0f, baseSpeed);
        minSpeed = Mathf.Max(0f, minSpeed);
        maxSpeed = Mathf.Max(minSpeed, maxSpeed);
        gravityInfluence = Mathf.Max(0f, gravityInfluence);
        downhillBoost = Mathf.Max(0f, downhillBoost);
        uphillDrag = Mathf.Max(0f, uphillDrag);
        flatRecovery = Mathf.Max(0f, flatRecovery);
        crestHoldSeconds = Mathf.Max(0f, crestHoldSeconds);
        crestSlopeThreshold = Mathf.Clamp01(crestSlopeThreshold);
        crestSampleDelta = Mathf.Max(0.0001f, crestSampleDelta);
        crestRetriggerDistance = Mathf.Max(0.001f, crestRetriggerDistance);
        stopParticleEmissionAfterSeconds = Mathf.Max(0f, stopParticleEmissionAfterSeconds);
    }

    private void CacheChildParticles()
    {
        if (childParticles != null && childParticles.Length > 0)
        {
            return;
        }

        ParticleSystem[] allParticles = GetComponentsInChildren<ParticleSystem>(true);
        var childOnly = new List<ParticleSystem>(allParticles.Length);
        for (int i = 0; i < allParticles.Length; i++)
        {
            ParticleSystem ps = allParticles[i];
            if (ps != null && ps.transform != transform)
            {
                childOnly.Add(ps);
            }
        }

        childParticles = childOnly.ToArray();
    }

    private IEnumerator StopChildParticleEmissionAfterDelay()
    {
        yield return new WaitForSeconds(stopParticleEmissionAfterSeconds);

        for (int i = 0; i < childParticles.Length; i++)
        {
            ParticleSystem ps = childParticles[i];
            if (ps == null)
            {
                continue;
            }

            ps.Stop(false, ParticleSystemStopBehavior.StopEmitting);
        }
    }

    private void Update()
    {
        if (!walker || !walker.Spline)
        {
            return;
        }

        if (_holdTimer > 0f)
        {
            _holdTimer -= Time.deltaTime;
            walker.speed = 0f;
            return;
        }

        float normalizedT = walker.NormalizedT;
        BezierSpline spline = walker.Spline;

        Vector3 tangent = spline.GetTangent(normalizedT);
        if (!walker.MovingForward)
        {
            tangent = -tangent;
        }

        if (tangent.sqrMagnitude < 0.0001f)
        {
            return;
        }

        tangent.Normalize();
        float slope = Vector3.Dot(tangent, Vector3.up);

        if (TryBeginCrestHold(normalizedT, slope, spline))
        {
            walker.speed = 0f;
            return;
        }

        ApplyGravitySpeed(slope);
        walker.speed = _currentSpeed;
    }

    private bool TryBeginCrestHold(float normalizedT, float slope, BezierSpline spline)
    {
        if (crestHoldSeconds <= 0f)
        {
            return false;
        }

        if (_lastCrestT >= 0f && Mathf.Abs(normalizedT - _lastCrestT) < crestRetriggerDistance)
        {
            return false;
        }

        if (Mathf.Abs(slope) > crestSlopeThreshold)
        {
            return false;
        }

        float tBefore = Mathf.Clamp01(normalizedT - crestSampleDelta);
        float tAfter = Mathf.Clamp01(normalizedT + crestSampleDelta);
        float heightBefore = spline.GetPoint(tBefore).y;
        float heightHere = spline.GetPoint(normalizedT).y;
        float heightAfter = spline.GetPoint(tAfter).y;

        bool isLocalPeak = heightHere >= heightBefore && heightHere >= heightAfter;
        if (!isLocalPeak)
        {
            return false;
        }

        _holdTimer = crestHoldSeconds;
        _lastCrestT = normalizedT;
        return true;
    }

    private void ApplyGravitySpeed(float slope)
    {
        float influence = -slope * gravityInfluence * Time.deltaTime;
        if (slope < 0f)
        {
            influence *= downhillBoost;
        }
        else if (slope > 0f)
        {
            influence *= uphillDrag;
        }

        _currentSpeed += influence;
        _currentSpeed = Mathf.Clamp(_currentSpeed, minSpeed, maxSpeed);

        if (Mathf.Abs(slope) < crestSlopeThreshold)
        {
            _currentSpeed = Mathf.MoveTowards(_currentSpeed, baseSpeed, flatRecovery * Time.deltaTime);
        }
    }
}
