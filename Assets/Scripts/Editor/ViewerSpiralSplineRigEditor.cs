using UnityEditor;
using UnityEngine;

[CustomEditor(typeof(ViewerSpiralSplineRig))]
public class ViewerSpiralSplineRigEditor : Editor
{
    public override void OnInspectorGUI()
    {
        DrawDefaultInspector();

        var rig = (ViewerSpiralSplineRig)target;
        Stage4EncounterPathEditorBootstrap.EnsureRigVisibleInEditMode(rig);
        EditorGUILayout.Space();

        EditorGUILayout.BeginHorizontal();
        if (GUILayout.Button("Preview Procedural Path"))
        {
            PreviewPath(rig, ResolveViewerTransform(rig), ResolvePathStartWorld(rig), lockManual: false, registerUndo: true);
        }

        if (GUILayout.Button("Preview And Lock Manual"))
        {
            PreviewPath(rig, ResolveViewerTransform(rig), ResolvePathStartWorld(rig), lockManual: true, registerUndo: true);
        }
        EditorGUILayout.EndHorizontal();

        EditorGUILayout.Space();
        if (GUILayout.Button("Apply Convoy Defaults To Follower"))
        {
            Undo.RecordObject(rig.ConvoySwarm, "Apply Convoy Defaults To Follower");
            rig.SyncConvoyDefaultsToFollower();
            EditorUtility.SetDirty(rig.ConvoySwarm);
        }

        EditorGUILayout.HelpBox(
            "Stage 4 convoy tuning: edit Vertex Path Swarm Follower directly (path speed, spacing, scale, rotation). " +
            "Convoy Defaults on this rig are optional presets — enable Apply Convoy Defaults To Follower on the rig, " +
            "or click the button above, to copy them over.",
            MessageType.Info);

        if (rig.UseManualSplinePoints && rig.HasManualSplinePoints())
        {
            EditorGUILayout.HelpBox(
                "Manual mode ON: Stage 4 uses these spline points in Edit mode and Play mode. Drag Bezier points in the Scene view.",
                MessageType.Info);
        }
        else if (rig.HasManualSplinePoints())
        {
            EditorGUILayout.HelpBox(
                "Spline points exist but Use Manual Spline Points is off. Play mode will still keep saved points. Enable manual to prevent accidental procedural rebuilds from the Preview button.",
                MessageType.Warning);
        }
        else
        {
            EditorGUILayout.HelpBox(
                "No spline points yet. Click Preview And Lock Manual to generate a path you can edit. " +
                "With manual mode on and no points, Play mode keeps the path empty until you bake.",
                MessageType.None);
        }
    }

    internal static void PreviewPath(
        ViewerSpiralSplineRig rig,
        Transform viewer,
        Vector3 pathStartWorld,
        bool lockManual,
        bool registerUndo)
    {
        if (registerUndo)
        {
            Undo.RegisterFullObjectHierarchyUndo(rig.gameObject, lockManual ? "Preview And Lock Encounter Path" : "Preview Encounter Path");
        }

        rig.PreviewProceduralPath(viewer, pathStartWorld);

        if (lockManual)
        {
            rig.SetUseManualSplinePoints(true);
        }

        EditorUtility.SetDirty(rig);
        if (rig.Spline != null)
        {
            EditorUtility.SetDirty(rig.Spline);
        }

        SceneView.RepaintAll();
    }

    private static Transform ResolveViewerTransform(ViewerSpiralSplineRig rig)
    {
        return Stage4EncounterPathBaker.ResolveViewerTransform();
    }

    private static Vector3 ResolvePathStartWorld(ViewerSpiralSplineRig rig)
    {
        return Stage4EncounterPathBaker.ResolvePathStartWorld(rig, ResolveViewerTransform(rig));
    }
}
