#if UNITY_EDITOR
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

/// <summary>
/// Keeps saved Stage 4 encounter splines visible in Edit mode without regenerating them.
/// Procedural bake is opt-in via Tools/Alina/Bake Stage 4 Encounter Path.
/// Only touches ViewerEncounterPath — never Stage 2/3 splines.
/// </summary>
[InitializeOnLoad]
internal static class Stage4EncounterPathEditorBootstrap
{
    static Stage4EncounterPathEditorBootstrap()
    {
        EditorApplication.delayCall += OnEditorReady;
        PrefabStage.prefabStageOpened += OnPrefabStageOpened;
    }

    private static void OnEditorReady()
    {
        EditorApplication.delayCall -= OnEditorReady;
        if (EditorApplication.isPlayingOrWillChangePlaymode)
        {
            return;
        }

        EnsureLoadedStage4RigsVisible();
    }

    private static void OnPrefabStageOpened(PrefabStage stage)
    {
        if (stage == null || EditorApplication.isPlayingOrWillChangePlaymode)
        {
            return;
        }

        EditorApplication.delayCall += EnsureLoadedStage4RigsVisible;
    }

    internal static void EnsureLoadedStage4RigsVisible()
    {
        ViewerSpiralSplineRig[] rigs = Object.FindObjectsOfType<ViewerSpiralSplineRig>(true);
        for (int i = 0; i < rigs.Length; i++)
        {
            EnsureRigVisibleInEditMode(rigs[i]);
        }

        SceneView.RepaintAll();
    }

    /// <summary>
    /// Refreshes gizmo display for saved spline points. Never rebuilds procedural geometry.
    /// </summary>
    internal static void EnsureRigVisibleInEditMode(ViewerSpiralSplineRig rig)
    {
        if (rig == null || EditorApplication.isPlayingOrWillChangePlaymode)
        {
            return;
        }

        if (!rig.HasManualSplinePoints() || rig.Spline == null)
        {
            return;
        }

        rig.Spline.drawGizmos = true;
        rig.Spline.Refresh();
    }
}
#endif
