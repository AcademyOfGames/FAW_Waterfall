using UnityEngine;

/// <summary>
/// Finds every SkinnedMeshRenderer under this object (all nesting levels) and drives blend shapes.
/// Default: cycles through blend shape indices from first to last over <see cref="cycleDurationSeconds"/>, then loops.
/// </summary>
public class BlendShapeChildCycle : MonoBehaviour
{
    public enum CycleMode
    {
        /// <summary>One blend shape at a time, full weight; advances index from start to end of each mesh.</summary>
        SequentialIndices,
        /// <summary>Every blend shape on each mesh animates weight 0 → 100 together.</summary>
        AllWeightsTogether
    }

    [Header("Timing")]
    [Tooltip("Seconds for one full cycle (first index → last index, or 0 → 100 weight depending on mode).")]
    [Min(0.01f)]
    public float cycleDurationSeconds = 5f;

    [Header("Behavior")]
    public CycleMode mode = CycleMode.SequentialIndices;

    private SkinnedMeshRenderer[] skinnedMeshes;

    private void Awake()
    {
        skinnedMeshes = GetComponentsInChildren<SkinnedMeshRenderer>(true);
    }

    private void Update()
    {
        if (skinnedMeshes == null || skinnedMeshes.Length == 0)
            return;

        float duration = Mathf.Max(0.01f, cycleDurationSeconds);
        float phase = Mathf.Repeat(Time.time, duration) / duration;

        foreach (SkinnedMeshRenderer smr in skinnedMeshes)
        {
            if (smr == null)
                continue;

            Mesh mesh = smr.sharedMesh;
            if (mesh == null)
                continue;

            int blendCount = mesh.blendShapeCount;
            if (blendCount == 0)
                continue;

            if (mode == CycleMode.SequentialIndices)
            {
                int active = Mathf.Clamp(Mathf.FloorToInt(phase * blendCount), 0, blendCount - 1);
                for (int i = 0; i < blendCount; i++)
                    smr.SetBlendShapeWeight(i, i == active ? 100f : 0f);
            }
            else
            {
                float w = phase * 100f;
                for (int i = 0; i < blendCount; i++)
                    smr.SetBlendShapeWeight(i, w);
            }
        }
    }
}
