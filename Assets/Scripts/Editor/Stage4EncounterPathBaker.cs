using BezierSolution;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

/// <summary>
/// Bakes procedural Stage 4 preview points into the FishPaths prefab for WYSIWYG editing.
/// </summary>
public static class Stage4EncounterPathBaker
{
    private const string FishPathsPrefabPath = "Assets/_artAssets/Alina/Prefabs/FishPaths.prefab";
    private const string SampleScenePath = "Assets/Scenes/SampleScene.unity";

    [MenuItem("Tools/Alina/Bake Stage 4 Encounter Path (FishPaths Prefab)")]
    public static void BakeFishPathsEncounterPath()
    {
        BakeFishPathsEncounterPathInternal();
    }

    /// <summary>Batch-mode entry point: Unity.exe -executeMethod Stage4EncounterPathBaker.BakeFromCommandLine</summary>
    public static void BakeFromCommandLine()
    {
        BakeFishPathsEncounterPathInternal();
        EditorApplication.Exit(0);
    }

    private static void BakeFishPathsEncounterPathInternal()
    {
        GameObject prefabRoot = AssetDatabase.LoadAssetAtPath<GameObject>(FishPathsPrefabPath);
        if (prefabRoot == null)
        {
            Debug.LogError("[Stage4EncounterPathBaker] FishPaths prefab not found at " + FishPathsPrefabPath);
            return;
        }

        Scene setupScene = default;
        bool openedSetupScene = false;
        if (!Application.isPlaying && Stage4EncounterPathBaker.ResolveViewerTransform() == null)
        {
            setupScene = EditorSceneManager.OpenScene(SampleScenePath, OpenSceneMode.Additive);
            openedSetupScene = true;
        }

        GameObject instance = PrefabUtility.InstantiatePrefab(prefabRoot) as GameObject;
        if (instance == null)
        {
            Debug.LogError("[Stage4EncounterPathBaker] Failed to instantiate FishPaths prefab.");
            return;
        }

        try
        {
            ViewerSpiralSplineRig rig = instance.GetComponentInChildren<ViewerSpiralSplineRig>(true);
            if (rig == null)
            {
                Debug.LogError("[Stage4EncounterPathBaker] ViewerSpiralSplineRig not found under FishPaths.");
                return;
            }

            Transform viewer = ResolveViewerTransform();
            Vector3 pathStart = ResolvePathStartWorld(instance.transform, viewer);
            ViewerSpiralSplineRigEditor.PreviewPath(rig, viewer, pathStart, lockManual: true, registerUndo: false);

            PrefabUtility.SaveAsPrefabAsset(instance, FishPathsPrefabPath);
            AssetDatabase.SaveAssets();
            Debug.Log(
                $"[Stage4EncounterPathBaker] Baked {rig.Spline?.Count ?? 0} manual Stage 4 encounter points into FishPaths.prefab. " +
                $"Viewer={(viewer != null ? viewer.name : "none")}, pathStart={pathStart}",
                rig);
        }
        finally
        {
            Object.DestroyImmediate(instance);
            if (openedSetupScene && setupScene.IsValid())
            {
                EditorSceneManager.CloseScene(setupScene, true);
            }
        }
    }

    internal static Transform ResolveViewerTransform()
    {
        if (Camera.main != null)
        {
            return Camera.main.transform;
        }

        Camera sceneCamera = SceneView.lastActiveSceneView != null
            ? SceneView.lastActiveSceneView.camera
            : null;
        if (sceneCamera != null)
        {
            return sceneCamera.transform;
        }

        Camera[] cameras = Object.FindObjectsOfType<Camera>();
        for (int i = 0; i < cameras.Length; i++)
        {
            if (cameras[i] != null && cameras[i].CompareTag("MainCamera"))
            {
                return cameras[i].transform;
            }
        }

        return cameras.Length > 0 ? cameras[0].transform : null;
    }

    internal static Vector3 ResolvePathStartWorld(Transform fishPathsRoot, Transform viewer)
    {
        BezierSpline stageThreeSpline = FindStageThreeSpline(fishPathsRoot);
        if (stageThreeSpline != null && stageThreeSpline.Count >= 1)
        {
            BezierPoint lastPoint = stageThreeSpline[stageThreeSpline.Count - 1];
            if (lastPoint != null)
            {
                return lastPoint.position;
            }
        }

        if (viewer != null)
        {
            return viewer.position + viewer.forward * 12f;
        }

        return fishPathsRoot != null ? fishPathsRoot.position + Vector3.forward * 12f : Vector3.forward * 12f;
    }

    internal static Vector3 ResolvePathStartWorld(ViewerSpiralSplineRig rig, Transform viewer)
    {
        Transform fishPathsRoot = rig != null ? FindFishPathsRoot(rig.transform) : null;
        return ResolvePathStartWorld(fishPathsRoot != null ? fishPathsRoot : rig.transform.root, viewer);
    }

    internal static Transform FindFishPathsRoot(Transform start)
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

    private static BezierSpline FindStageThreeSpline(Transform fishPathsRoot)
    {
        if (fishPathsRoot == null)
        {
            return null;
        }

        Transform stageThree = fishPathsRoot.Find("Stage3");
        if (stageThree == null)
        {
            return null;
        }

        return stageThree.GetComponentInChildren<BezierSpline>(true);
    }
}
