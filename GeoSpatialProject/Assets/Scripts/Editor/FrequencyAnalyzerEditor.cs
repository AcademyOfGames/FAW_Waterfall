using UnityEditor;
using UnityEngine;

[CustomEditor(typeof(FrequencyAnalyzer))]
public class FrequencyAnalyzerEditor : Editor
{
    private static readonly string[] BandLabels = { "Sub", "Bass", "LoMid", "Mid", "HiMid", "Hi" };

    private void OnEnable()
    {
        EditorApplication.update += OnEditorUpdate;
    }

    private void OnDisable()
    {
        EditorApplication.update -= OnEditorUpdate;
    }

    private void OnEditorUpdate()
    {
        if (!Application.isPlaying || target == null)
            return;
        Repaint();
    }

    public override void OnInspectorGUI()
    {
        DrawDefaultInspector();

        var analyzer = (FrequencyAnalyzer)target;
        EditorGUILayout.Space(6f);
        EditorGUILayout.LabelField("Band levels (Play mode)", EditorStyles.boldLabel);

        float[] levels =
        {
            analyzer.SubBass,
            analyzer.Bass,
            analyzer.LowMid,
            analyzer.Mid,
            analyzer.HighMid,
            analyzer.High
        };

        const float graphHeight = 96f;
        const float pad = 2f;
        Rect r = GUILayoutUtility.GetRect(1f, graphHeight, GUILayout.ExpandWidth(true));
        r = EditorGUI.IndentedRect(r);

        EditorGUI.DrawRect(r, new Color(0.12f, 0.12f, 0.12f, 1f));

        float innerW = r.width - pad * 2f;
        float innerH = r.height - pad * 2f - 14f;
        float barW = (innerW - (BandLabels.Length - 1) * 2f) / BandLabels.Length;

        for (int i = 0; i < BandLabels.Length; i++)
        {
            float x = r.x + pad + i * (barW + 2f);
            float h = Mathf.Clamp01(levels[i]) * innerH;
            float y = r.y + pad + innerH - h;

            var baseCol = Color.HSVToRGB(0.08f + i * 0.11f, 0.55f, 0.95f);
            var dim = new Color(baseCol.r * 0.25f, baseCol.g * 0.25f, baseCol.b * 0.25f, 1f);
            EditorGUI.DrawRect(new Rect(x, r.y + pad, barW, innerH), dim);
            if (h > 0.5f)
                EditorGUI.DrawRect(new Rect(x, y, barW, h), baseCol);

            var lr = new Rect(x, r.yMax - 13f, barW, 12f);
            GUI.Label(lr, BandLabels[i], EditorStyles.miniLabel);
        }

        if (!Application.isPlaying)
            EditorGUILayout.HelpBox("Enter Play mode with audio playing to see live levels.", MessageType.Info);
    }
}
