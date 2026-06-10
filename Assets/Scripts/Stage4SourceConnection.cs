using System;
using BezierSolution;
using UnityEngine;

/// <summary>
/// Junction + optional bridge spline for one Group B source path onto Stage 4.
/// </summary>
[Serializable]
public class Stage4SourceConnection
{
    [Tooltip("Group B swarm on this Stage 3 path. When empty, matched from orchestrator Group B by index.")]
    public VertexPathSwarmFollower stage3Swarm;

    [Tooltip("Optional override. When empty, uses the swarm's spline.")]
    public BezierSpline stage3Spline;

    [Tooltip("Optional editor-placed bridge Bezier. When empty, a runtime bridge is built from junctions.")]
    public BezierSpline bridgeSpline;

    [Tooltip("Normalized T on this Stage 3 spline where fish peel onto the bridge.")]
    [Range(0.05f, 1f)]
    public float stage3ConnectionNormalizedT = 0.88f;

    [Tooltip("When on, fish home onto Stage 4 behind the existing convoy tail (second path).")]
    public bool appendBehindConvoy;
}
