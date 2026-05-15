using TMPro;
using UnityEngine;
using UnityEngine.UI;

/// <summary>
/// Builds a minimal overlay canvas for geofence status/messages when nothing is wired in the scene.
/// </summary>
public static class GeofenceRuntimeUiBuilder
{
    public static GeofenceHudView Build(Transform parent)
    {
        var canvasGo = new GameObject("GeofenceHudCanvas");
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
        Debug.Log("[Geofence] Runtime HUD canvas built under parent '" + parent.name + "' (TMP status/message + loading widget).");
        return hud;
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
