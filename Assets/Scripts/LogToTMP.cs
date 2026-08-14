using System.Collections.Generic;
using UnityEngine;
using TMPro;

public class LogToTMP : MonoBehaviour
{
    [SerializeField] private TextMeshProUGUI logText;
    [SerializeField] private int maxLines = 50;

    private readonly Queue<string> logQueue = new Queue<string>();

    void OnEnable()
    {
        Application.logMessageReceived += HandleLog;
    }

    void OnDisable()
    {
        Application.logMessageReceived -= HandleLog;
    }

    private void HandleLog(string condition, string stackTrace, LogType type)
    {
        // logText is unassigned in some scenes (e.g. devScene) — bail out silently. Do NOT log
        // here (Debug.LogWarning/Error would re-enter this same handler via
        // Application.logMessageReceived and recurse).
        if (logText == null)
            return;

        string formattedLog = $"[{type}] {condition}";

        logQueue.Enqueue(formattedLog);

        if (logQueue.Count > maxLines)
        {
            logQueue.Dequeue();
        }

        logText.text = string.Join("\n", logQueue);
    }
}



