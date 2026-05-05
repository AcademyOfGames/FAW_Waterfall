using System.Collections.Generic;
using System.Text;
using TMPro;
using UnityEngine;

public class RuntimeLogConsole : MonoBehaviour
{
    [Header("UI References")]
    [SerializeField] private TextMeshProUGUI logOutput;
    [SerializeField] private GameObject panelToToggle;

    [Header("Settings")]
    [SerializeField] private int maxLogs = 40;

    private readonly List<string> _logLines = new List<string>();
    private readonly StringBuilder _builder = new StringBuilder();

    private void OnEnable()
    {
        Application.logMessageReceived += HandleLogMessage;
    }

    private void OnDisable()
    {
        Application.logMessageReceived -= HandleLogMessage;
    }

    private void HandleLogMessage(string condition, string stackTrace, LogType type)
    {
        if (logOutput == null)
        {
            return;
        }

        string formatted = $"[{type}] {condition}";
        _logLines.Add(formatted);

        int safeMax = Mathf.Max(1, maxLogs);
        while (_logLines.Count > safeMax)
        {
            _logLines.RemoveAt(0);
        }

        _builder.Clear();
        for (int i = 0; i < _logLines.Count; i++)
        {
            _builder.AppendLine(_logLines[i]);
        }

        logOutput.text = _builder.ToString();
    }

    public void TogglePanel()
    {
        if (panelToToggle == null)
        {
            return;
        }

        panelToToggle.SetActive(!panelToToggle.activeSelf);
    }

    public void ClearLogs()
    {
        _logLines.Clear();
        if (logOutput != null)
        {
            logOutput.text = string.Empty;
        }
    }
}
