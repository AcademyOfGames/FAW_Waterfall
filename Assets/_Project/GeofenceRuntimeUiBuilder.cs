using TMPro;
using UnityEngine;
using UnityEngine.UI;

/// <summary>
/// Builds a minimal overlay canvas for geofence status/messages when nothing is wired in the scene.
/// </summary>
public static class GeofenceRuntimeUiBuilder
{
    public const string RuntimeHudCanvasObjectName = "GeofenceHudCanvas";

    public static GeofenceHudView Build(Transform parent, GeofenceExperienceCoordinator coordinator = null,
        bool addForceGeofenceToggles = false)
    {
        var canvasGo = new GameObject(RuntimeHudCanvasObjectName);
        canvasGo.transform.SetParent(parent, false);
        var canvas = canvasGo.AddComponent<Canvas>();
        canvas.renderMode = RenderMode.ScreenSpaceOverlay;
        canvas.sortingOrder = 5000;
        canvasGo.AddComponent<CanvasScaler>().uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
        canvasGo.AddComponent<GraphicRaycaster>();

        var rt = canvasGo.GetComponent<RectTransform>();
        rt.anchorMin = Vector2.zero;
        rt.anchorMax = Vector2.one;
        rt.offsetMin = Vector2.zero;
        rt.offsetMax = Vector2.zero;

        var fontAsset = TMP_Settings.defaultFontAsset;

        var status = new GameObject("StatusLine", typeof(RectTransform));
        status.transform.SetParent(canvasGo.transform, false);
        var statusRect = status.GetComponent<RectTransform>();
        statusRect.anchorMin = new Vector2(0.5f, 1f);
        statusRect.anchorMax = new Vector2(0.5f, 1f);
        statusRect.pivot = new Vector2(0.5f, 1f);
        statusRect.anchoredPosition = new Vector2(0, -36);
        statusRect.sizeDelta = new Vector2(720, 200);
        var statusTmp = status.AddComponent<TextMeshProUGUI>();
        if (fontAsset != null)
            statusTmp.font = fontAsset;
        statusTmp.fontSize = 24f;
        statusTmp.alignment = TextAlignmentOptions.Top;
        statusTmp.color = Color.white;
        statusTmp.enableWordWrapping = true;
        statusTmp.overflowMode = TextOverflowModes.Overflow;
        statusTmp.richText = true;
        statusTmp.raycastTarget = false;

        var message = new GameObject("MessageLine", typeof(RectTransform));
        message.transform.SetParent(canvasGo.transform, false);
        var messageRect = message.GetComponent<RectTransform>();
        messageRect.anchorMin = new Vector2(0.5f, 0f);
        messageRect.anchorMax = new Vector2(0.5f, 0f);
        messageRect.pivot = new Vector2(0.5f, 0f);
        messageRect.anchoredPosition = new Vector2(0, 88);
        messageRect.sizeDelta = new Vector2(800, 120);
        var messageTmp = message.AddComponent<TextMeshProUGUI>();
        if (fontAsset != null)
            messageTmp.font = fontAsset;
        messageTmp.fontSize = 18f;
        messageTmp.alignment = TextAlignmentOptions.Bottom;
        messageTmp.color = Color.white;
        messageTmp.enableWordWrapping = true;
        messageTmp.overflowMode = TextOverflowModes.Overflow;
        messageTmp.richText = true;
        messageTmp.raycastTarget = false;

        var loading = new GameObject("LoadingWidget");
        loading.transform.SetParent(canvasGo.transform, false);
        var loadingRt = loading.AddComponent<RectTransform>();
        loadingRt.anchorMin = new Vector2(0.5f, 0.5f);
        loadingRt.anchorMax = new Vector2(0.5f, 0.5f);
        loadingRt.sizeDelta = new Vector2(280, 80);
        loadingRt.anchoredPosition = Vector2.zero;
        var loadingBg = loading.AddComponent<Image>();
        loadingBg.color = new Color(0, 0, 0, 0.65f);
        var loadingText = CreateTmpText(loading.transform, "Text", fontAsset, 20f, TextAlignmentOptions.Center,
            new Vector2(0.5f, 0.5f), new Vector2(0.5f, 0.5f), Vector2.zero, new Vector2(260, 70));
        loadingText.text = "Loading experience…";
        loading.SetActive(false);

        var hud = canvasGo.AddComponent<GeofenceHudView>();
        hud.Initialize(statusTmp, messageTmp, loading);

        if (addForceGeofenceToggles && coordinator != null)
            BuildForceGeofenceToggles(canvasGo.transform, coordinator);

        Debug.Log("[FAW] Geofence: runtime HUD canvas built (TMP + loading widget).");
        return hud;
    }

    private static void BuildForceGeofenceToggles(Transform parent, GeofenceExperienceCoordinator coordinator)
    {
        const float rowHeight = 40f;
        const float bottomPad = 24f;
        const float leftPad = 24f;
        var rowIndex = 0;

        foreach (var def in ExperienceGeofenceDefinition.All)
        {
            var label = ForceGeofenceToggleLabel(def);
            var y = bottomPad + rowIndex * rowHeight;
            BuildForceGeofenceToggleRow(parent, coordinator, def.SceneName, label, leftPad, y);
            rowIndex++;
        }
    }

    private static string ForceGeofenceToggleLabel(ExperienceGeofenceDefinition def)
    {
        if (string.Equals(def.SceneName, "SampleScene", System.StringComparison.Ordinal))
            return "Simulate at Sample Scene (Divine)";
        return $"Simulate at {def.ExperienceName}";
    }

    private static void BuildForceGeofenceToggleRow(Transform parent, GeofenceExperienceCoordinator coordinator,
        string sceneName, string labelText, float left, float bottom)
    {
        var row = new GameObject($"ForceGeofence_{sceneName}", typeof(RectTransform));
        row.transform.SetParent(parent, false);
        var rowRt = row.GetComponent<RectTransform>();
        rowRt.anchorMin = new Vector2(0f, 0f);
        rowRt.anchorMax = new Vector2(0f, 0f);
        rowRt.pivot = new Vector2(0f, 0f);
        rowRt.anchoredPosition = new Vector2(left, bottom);
        rowRt.sizeDelta = new Vector2(460f, 36f);

        var toggleGo = new GameObject("Toggle", typeof(RectTransform));
        toggleGo.transform.SetParent(row.transform, false);
        var toggleRt = toggleGo.GetComponent<RectTransform>();
        toggleRt.anchorMin = new Vector2(0f, 0.5f);
        toggleRt.anchorMax = new Vector2(0f, 0.5f);
        toggleRt.pivot = new Vector2(0f, 0.5f);
        toggleRt.anchoredPosition = Vector2.zero;
        toggleRt.sizeDelta = new Vector2(32f, 32f);

        var bg = toggleGo.AddComponent<Image>();
        bg.color = new Color(0.15f, 0.15f, 0.15f, 0.85f);

        var check = new GameObject("Checkmark", typeof(RectTransform));
        check.transform.SetParent(toggleGo.transform, false);
        var checkRt = check.GetComponent<RectTransform>();
        checkRt.anchorMin = Vector2.zero;
        checkRt.anchorMax = Vector2.one;
        checkRt.offsetMin = new Vector2(6f, 6f);
        checkRt.offsetMax = new Vector2(-6f, -6f);
        var checkImg = check.AddComponent<Image>();
        checkImg.color = new Color32(0x2E, 0x8B, 0xFF, 0xFF);

        var toggle = toggleGo.AddComponent<Toggle>();
        toggle.targetGraphic = bg;
        toggle.graphic = checkImg;
        toggle.isOn = coordinator.GetForceGeofence(sceneName);
        var capturedScene = sceneName;
        toggle.onValueChanged.AddListener(v => coordinator.SetForceGeofence(capturedScene, v));

        var fontAsset = TMP_Settings.defaultFontAsset;
        var label = CreateTmpText(row.transform, "Label", fontAsset, 17f, TextAlignmentOptions.MidlineLeft,
            new Vector2(0f, 0.5f), new Vector2(1f, 0.5f), new Vector2(44f, 0f), new Vector2(-8f, 32f));
        label.text = labelText;
        label.raycastTarget = false;
    }

    private static TextMeshProUGUI CreateTmpText(Transform parent, string name, TMP_FontAsset fontAsset, float fontSize,
        TextAlignmentOptions alignment, Vector2 anchorMin, Vector2 anchorMax, Vector2 anchoredPosition,
        Vector2 sizeDelta)
    {
        var go = new GameObject(name);
        go.transform.SetParent(parent, false);
        var rect = go.AddComponent<RectTransform>();
        rect.anchorMin = anchorMin;
        rect.anchorMax = anchorMax;
        rect.pivot = new Vector2(0.5f, 0.5f);
        rect.anchoredPosition = anchoredPosition;
        rect.sizeDelta = sizeDelta;
        var tmp = go.AddComponent<TextMeshProUGUI>();
        if (fontAsset != null)
            tmp.font = fontAsset;
        tmp.fontSize = fontSize;
        tmp.alignment = alignment;
        tmp.color = Color.white;
        tmp.enableWordWrapping = true;
        tmp.overflowMode = TextOverflowModes.Truncate;
        tmp.richText = true;
        tmp.raycastTarget = false;
        return tmp;
    }
}
