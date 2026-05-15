using TMPro;
using UnityEngine;

/// <summary>
/// Simple status + message lines (TextMeshPro UGUI). Assign in inspector or use <see cref="Initialize"/> from runtime builder.
/// </summary>
public class GeofenceHudView : MonoBehaviour
{
    [SerializeField] private TextMeshProUGUI statusLine;
    [SerializeField] private TextMeshProUGUI messageLine;
    [Tooltip("In-range loading panel; Geofence Experience Coordinator may show/hide this same object while downloading.")]
    [SerializeField] private GameObject loadingWidgetRoot;
    [Tooltip("Log when HUD text is set — use with Android logcat / Xcode device console.")]
    [SerializeField] private bool debugLogHud = true;
    [Tooltip("One-line diagnostics: confirms distance/name were applied to TMP (mobile logcat).")]
    [SerializeField] private bool debugLogStatusFillCheck = true;

    public GameObject LoadingWidgetRoot => loadingWidgetRoot;

    public void Initialize(TextMeshProUGUI status, TextMeshProUGUI message, GameObject loadingRoot)
    {
        statusLine = status;
        messageLine = message;
        loadingWidgetRoot = loadingRoot;
        ConfigureTmpForHud(statusLine);
        ConfigureTmpForHud(messageLine);
    }

    private static void ConfigureTmpForHud(TextMeshProUGUI tmp)
    {
        if (tmp == null)
            return;
        tmp.richText = true;
    }

    /// <summary>
    /// Nearest-experience copy: first line distance, second line place name (TMP rich text: blue + bold).
    /// </summary>
    public static string FormatNearestExperienceStatus(double distanceKm, string experienceName, Color nameHighlight)
    {
        var safeName = string.IsNullOrEmpty(experienceName)
            ? "?"
            : experienceName.Replace("<", string.Empty).Replace(">", string.Empty);
        var hex = "#" + ColorUtility.ToHtmlStringRGBA(nameHighlight);
        return
            $"You're {distanceKm:F2} km from the nearest experience:\n" +
            $"<size=115%><color={hex}><b>{safeName}</b></color></size>";
    }

    /// <summary>
    /// Sets the top status line with styled nearest-place text and optional fill diagnostics for device logs.
    /// </summary>
    public void SetNearestExperienceStatus(double distanceKm, string experienceName, Color nameHighlight)
    {
        if (statusLine == null)
        {
            if (debugLogHud)
                Debug.LogWarning("[Geofence HUD] SetNearestExperienceStatus: statusLine is null — assign TMP or use runtime builder.");
            return;
        }

        ConfigureTmpForHud(statusLine);
        var formatted = FormatNearestExperienceStatus(distanceKm, experienceName, nameHighlight);
        statusLine.text = formatted;

        if (debugLogStatusFillCheck)
        {
            var canvas = statusLine.GetComponentInParent<Canvas>();
            Debug.Log(
                "[Geofence HUD fill-check] " +
                $"distKm={distanceKm:F5} name='{experienceName}' nameLen={experienceName?.Length ?? 0} " +
                $"assignedTmpLen={formatted.Length} richTextOn={statusLine.richText} " +
                $"tmpActive={statusLine.gameObject.activeSelf} tmpActiveInHierarchy={statusLine.gameObject.activeInHierarchy} " +
                $"canvas={(canvas != null ? canvas.name : "null")} canvasEnabled={(canvas != null && canvas.enabled)} " +
                $"sortOrder={(canvas != null ? canvas.sortingOrder.ToString() : "-")}");
        }

        if (debugLogHud)
            Debug.Log("[Geofence HUD] SetNearestExperienceStatus (plain preview): " +
                      $"{distanceKm:F2} km → {experienceName}");
    }

    public void SetStatus(string text)
    {
        if (statusLine == null)
        {
            if (debugLogHud)
                Debug.LogWarning("[Geofence HUD] SetStatus: statusLine is null — assign TMP or use runtime builder.");
            return;
        }

        ConfigureTmpForHud(statusLine);
        var value = text ?? string.Empty;
        statusLine.text = value;
        if (debugLogHud)
            Debug.Log("[Geofence HUD] SetStatus: " + value);
    }

    public void SetUserMessage(string userFacing, string debugDetail)
    {
        if (!string.IsNullOrEmpty(debugDetail))
            Debug.Log("[Geofence] " + debugDetail);

        if (messageLine == null)
        {
            if (debugLogHud)
                Debug.LogWarning("[Geofence HUD] SetUserMessage: messageLine is null — assign TMP or use runtime builder.");
            return;
        }

        ConfigureTmpForHud(messageLine);
        var value = userFacing ?? string.Empty;
        messageLine.text = value;
        if (debugLogHud)
            Debug.Log("[Geofence HUD] SetUserMessage: " + value);
    }
}
