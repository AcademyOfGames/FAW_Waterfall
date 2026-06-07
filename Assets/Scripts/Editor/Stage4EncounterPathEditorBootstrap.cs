#if UNITY_EDITOR
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

/// <summary>
/// Ensures Stage 4 encounter spline has baked points for Edit-mode visibility.
/// Only touches ViewerEncounterPath — never Stage 2/3 splines.
/// </summary>
[InitializeOnLoad]
internal static class Stage4EncounterPathEditorBootstrap
{
    private const string FishPathsPrefabPath = "Assets/_artAssets/Alina/Prefabs/FishPaths.prefab";
    private const string PrefabBakeSessionKey = "Stage4EncounterPathEditorBootstrap.PrefabBaked";

    static Stage4EncounterPathEditorBootstrap()
    {
        EditorApplication.delayCall += OnEditorReady;
        PrefabStage.prefabStageOpened += OnPrefabStageOpened;
        Selection.selectionChanged += OnSelectionChanged;
    }

    private static void OnEditorReady()
    {
        EditorApplication.delayCall -= OnEditorReady;
        if (EditorApplication.isPlayingOrWillChangePlaymode)
        {
            return;
        }

        EnsurePrefabHasStage4Points();
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

    private static void OnSelectionChanged()
    {
        if (EditorApplication.isPlayingOrWillChangePlaymode)
        {
            return;
        }

        GameObject selected = Selection.activeGameObject;
        if (selected == null)
        {
            return;
        }

        ViewerSpiralSplineRig rig = selected.GetComponent<ViewerSpiralSplineRig>()
            ?? selected.GetComponentInParent<ViewerSpiralSplineRig>();
        if (rig != null)
        {
            EnsureRigVisibleInEditMode(rig);
        }
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

    internal static void EnsureRigVisibleInEditMode(ViewerSpiralSplineRig rig)
    {
        if (rig == null || EditorApplication.isPlayingOrWillChangePlaymode)
        {
            return;
        }

        if (!rig.UseManualSplinePoints || rig.HasManualSplinePoints())
        {
            return;
        }

        Transform fishPathsRoot = FindFishPathsRoot(rig.transform);
        Transform viewer = Stage4EncounterPathBaker.ResolveViewerTransform();
        Vector3 pathStart = Stage4EncounterPathBaker.ResolvePathStartWorld(
            fishPathsRoot != null ? fishPathsRoot : rig.transform.root,
            viewer);

        ViewerSpiralSplineRigEditor.PreviewPath(rig, viewer, pathStart, lockManual: true, registerUndo: false);
        EditorUtility.SetDirty(rig);
        if (rig.Spline != null)
        {
            EditorUtility.SetDirty(rig.Spline);
        }
    }

    private static void EnsurePrefabHasStage4Points()
    {
        if (SessionState.GetBool(PrefabBakeSessionKey, false))
        {
            return;
        }

        GameObject prefabRoot = AssetDatabase.LoadAssetAtPath<GameObject>(FishPathsPrefabPath);
        if (prefabRoot == null)
        {
            return;
        }

        ViewerSpiralSplineRig rig = prefabRoot.GetComponentInChildren<ViewerSpiralSplineRig>(true);
        if (rig == null || (rig.UseManualSplinePoints && rig.HasManualSplinePoints()))
        {
            SessionState.SetBool(PrefabBakeSessionKey, true);
            return;
        }

        SessionState.SetBool(PrefabBakeSessionKey, true);
        Stage4EncounterPathBaker.BakeFishPathsEncounterPath();
    }

    private static Transform FindFishPathsRoot(Transform start)
    {
        Transform current = start;
        while (current != null)
        {
            if (current.name == "FishPaths")
            {
                return current;
            }

            current = current.parent;
        }

        return null;
    }
}
#endif
