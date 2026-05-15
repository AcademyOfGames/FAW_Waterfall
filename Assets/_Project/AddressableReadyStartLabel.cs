using TMPro;
using UnityEngine;
using UnityEngine.UI;

/// <summary>
/// Keeps <see cref="LoadingSequenceAnimator"/> running on the label until
/// <see cref="AddressableLoadingManager.IsReadyForLabel"/> is true for <see cref="AddressableLoadingManager.ActiveLabel"/>,
/// then disables the sequence and sets the label to START (optional <see cref="Button"/> becomes interactable).
/// </summary>
[DisallowMultipleComponent]
public class AddressableReadyStartLabel : MonoBehaviour
{
    [SerializeField] private AddressableLoadingManager addressables;
    [SerializeField] private LoadingSequenceAnimator loadingSequence;
    [Tooltip("If null, uses LoadingSequenceAnimator's loading text field.")]
    [SerializeField] private TextMeshProUGUI label;
    [SerializeField] private string readyText = "START";
    [Tooltip("If set, interactable only while ActiveLabel dependencies are ready.")]
    [SerializeField] private Button optionalButton;

    private bool _showingReady;

    private void Awake()
    {
        if (addressables == null)
            addressables = GetComponent<AddressableLoadingManager>();
        if (addressables == null)
            addressables = GetComponentInParent<AddressableLoadingManager>();

        if (loadingSequence != null && label == null)
            label = loadingSequence.LoadingText;

        if (optionalButton != null)
            optionalButton.interactable = false;
    }

    private void LateUpdate()
    {
        if (addressables == null)
            return;

        var active = addressables.ActiveLabel;
        var ready = !string.IsNullOrEmpty(active) && addressables.IsReadyForLabel(active);

        if (ready == _showingReady)
            return;

        _showingReady = ready;
        ApplyVisualState(ready);
    }

    private void ApplyVisualState(bool ready)
    {
        if (loadingSequence != null)
            loadingSequence.enabled = !ready;

        var tmp = label != null ? label : loadingSequence != null ? loadingSequence.LoadingText : null;
        if (tmp != null && ready)
            tmp.text = readyText;

        if (optionalButton != null)
            optionalButton.interactable = ready;
    }
}
