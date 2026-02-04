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
        string formattedLog = $"[{type}] {condition}";

        logQueue.Enqueue(formattedLog);

        if (logQueue.Count > maxLines)
        {
            logQueue.Dequeue();
        }

        logText.text = string.Join("\n", logQueue);
    }
}
