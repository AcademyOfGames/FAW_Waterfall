using TMPro;
using UnityEngine;
using UnityEngine.UI;

/// <summary>
/// On-screen guide for tap-to-place: a faded reference photo with a prompt underneath, shown
/// until the user taps. Built at runtime by <see cref="ARPlaneContentAnchor"/>, no scene setup needed.
/// </summary>
public class AlignTapOverlay : MonoBehaviour
{
    private ARPlaneContentAnchor _anchor;
    private GameObject _imageRoot;
    private TextMeshProUGUI _label;
    private string _alignPrompt;
    private string _floorPrompt;

    public static AlignTapOverlay Create(ARPlaneContentAnchor anchor, Texture2D guide, float opacity,
        float widthFraction, string alignPrompt, string floorPrompt)
    {
        var canvasGo = new GameObject("AlignTapOverlay", typeof(RectTransform));
        var canvas = canvasGo.AddComponent<Canvas>();
        canvas.renderMode = RenderMode.ScreenSpaceOverlay;
        canvas.sortingOrder = -10;   // behind the app's own buttons and HUD
        var scaler = canvasGo.AddComponent<CanvasScaler>();
        scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
        scaler.referenceResolution = new Vector2(1080f, 1920f);
        scaler.matchWidthOrHeight = 0f;   // scale with width, so the layout holds on any phone
        // No GraphicRaycaster: the overlay must never swallow the tap that places the content.

        var overlay = canvasGo.AddComponent<AlignTapOverlay>();
        overlay._anchor = anchor;
        overlay._alignPrompt = alignPrompt;
        overlay._floorPrompt = floorPrompt;

        float width = 1080f * Mathf.Clamp01(widthFraction);
        float height = width;
        if (guide != null)
            height = width * guide.height / Mathf.Max(guide.width, 1);

        // Faded reference photo, centred a little above the middle to leave room for the prompt.
        var imageGo = new GameObject("Guide", typeof(RectTransform));
        imageGo.transform.SetParent(canvasGo.transform, false);
        var imageRt = (RectTransform)imageGo.transform;
        imageRt.anchorMin = imageRt.anchorMax = imageRt.pivot = new Vector2(0.5f, 0.5f);
        imageRt.sizeDelta = new Vector2(width, height);
        imageRt.anchoredPosition = new Vector2(0f, 80f);
        var raw = imageGo.AddComponent<RawImage>();
        raw.texture = guide;
        raw.color = new Color(1f, 1f, 1f, Mathf.Clamp01(opacity));
        raw.raycastTarget = false;
        imageGo.SetActive(guide != null);
        overlay._imageRoot = imageGo;

        // Prompt directly below the photo.
        var labelGo = new GameObject("Prompt", typeof(RectTransform));
        labelGo.transform.SetParent(canvasGo.transform, false);
        var labelRt = (RectTransform)labelGo.transform;
        labelRt.anchorMin = labelRt.anchorMax = new Vector2(0.5f, 0.5f);
        labelRt.pivot = new Vector2(0.5f, 1f);
        labelRt.sizeDelta = new Vector2(1000f, 160f);
        labelRt.anchoredPosition = new Vector2(0f, 80f - height * 0.5f - 30f);
        var label = labelGo.AddComponent<TextMeshProUGUI>();
        label.text = alignPrompt;
        label.fontSize = 64f;
        label.alignment = TextAlignmentOptions.Top;
        label.color = Color.white;
        label.raycastTarget = false;
        label.outlineWidth = 0.2f;          // stays readable over a bright camera feed
        label.outlineColor = new Color32(0, 0, 0, 200);
        overlay._label = label;

        return overlay;
    }

    private void Update()
    {
        if (_anchor == null)
        {
            Destroy(gameObject);
            return;
        }

        if (_anchor.WaitingForTap)
            return;

        // Tapped: the photo has done its job. Keep a prompt only while the floor is still missing.
        if (_imageRoot.activeSelf)
            _imageRoot.SetActive(false);

        if (_anchor.WaitingForFloor && !string.IsNullOrEmpty(_floorPrompt))
        {
            _label.text = _floorPrompt;
            return;
        }

        Destroy(gameObject);
    }
}
