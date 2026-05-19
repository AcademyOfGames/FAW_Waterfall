using TMPro;
using UnityEngine;

/// <summary>
/// Simple status + message lines (TextMeshPro UGUI). Assign in inspector or use <see cref="Initialize"/> from runtime builder.
/// </summary>
public class GeofenceHudView : MonoBehaviour
{
    /// <summary>At or inside this distance (km), status shows arrival copy instead of distance.</summary>
    public const double ArrivalDistanceKm = 0.08;

    [SerializeField] private TextMeshProUGUI statusLine;
    [SerializeField] private TextMeshProUGUI messageLine;
    [Tooltip("In-range loading panel; Geofence Experience Coordinator may show/hide this same object while downloading.")]
    [SerializeField] private GameObject loadingWidgetRoot;
    [Tooltip("Verbose TMP logs (normally off; use GeofenceExperienceCoordinator \"FAW\" logs on device).")]
    [SerializeField] private bool debugLogHud = false;

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
        if (distanceKm <= ArrivalDistanceKm)
        {
            return
                "You've arrived!\n" +
                $"<size=115%><color={hex}><b>{safeName}</b></color></size>";
        }

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

        if (debugLogHud)
            Debug.Log($"[FAW] HUD status distKm={distanceKm:F3} name='{experienceName}'");
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
        if (debugLogHud && !string.IsNullOrEmpty(debugDetail))
            Debug.Log("[FAW] HUD msg-detail: " + debugDetail);

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
