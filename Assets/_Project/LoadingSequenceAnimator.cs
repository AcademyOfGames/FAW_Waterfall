using TMPro;
using UnityEngine;
using UnityEngine.Serialization;

/// <summary>
/// Cycles loading icons on then off in order, and TMP text through Loading. / .. / ...
/// Icon and text speeds are independent; both update live from the Inspector in Play Mode.
/// </summary>
public class LoadingSequenceAnimator : MonoBehaviour
{
    [SerializeField] private GameObject[] loadingIcons = new GameObject[4];
    [SerializeField] private TextMeshProUGUI loadingText;

    public TextMeshProUGUI LoadingText => loadingText;

    [Tooltip("Icon sequence steps per second (higher = faster).")]
    [Min(0.01f)]
    [FormerlySerializedAs("stepSpeed")]
    [SerializeField] private float iconStepSpeed = 4f;

    [Tooltip("Text dot cycle steps per second (higher = faster).")]
    [Min(0.01f)]
    [SerializeField] private float textStepSpeed = 2f;

    private const int IconPhaseCount = 8;
    private static readonly string[] LoadingDotSuffixes = { ".", "..", "..." };
    private const int TextPhaseCount = 3;

    private float _iconAccumulator;
    private float _textAccumulator;
    private int _iconStepIndex;
    private int _textStepIndex;

    private void OnEnable()
    {
        _iconAccumulator = 0f;
        _textAccumulator = 0f;
        _iconStepIndex = 0;
        _textStepIndex = 0;
        ApplyIconVisualState(_iconStepIndex);
        ApplyTextVisualState(_textStepIndex);
    }

    private void Update()
    {
        float iconInterval = 1f / Mathf.Max(iconStepSpeed, 0.01f);
        float textInterval = 1f / Mathf.Max(textStepSpeed, 0.01f);

        _iconAccumulator += Time.deltaTime;
        while (_iconAccumulator >= iconInterval)
        {
            _iconAccumulator -= iconInterval;
            _iconStepIndex = (_iconStepIndex + 1) % IconPhaseCount;
            ApplyIconVisualState(_iconStepIndex);
        }

        _textAccumulator += Time.deltaTime;
        while (_textAccumulator >= textInterval)
        {
            _textAccumulator -= textInterval;
            _textStepIndex = (_textStepIndex + 1) % TextPhaseCount;
            ApplyTextVisualState(_textStepIndex);
        }
    }

    /// <summary>
    /// step 0-3: cumulative on for icons 0..3. step 4-7: cumulative off from 0..3.
    /// </summary>
    private void ApplyIconVisualState(int step)
    {
        if (loadingIcons == null)
            return;

        for (int i = 0; i < loadingIcons.Length; i++)
        {
            if (loadingIcons[i] == null)
                continue;

            bool on;
            if (step < 4)
                on = i <= step;
            else
                on = i > step - 4;

            loadingIcons[i].SetActive(on);
        }
    }

    private void ApplyTextVisualState(int dotIndex)
    {
        if (loadingText == null)
            return;

        loadingText.text = "Loading" + LoadingDotSuffixes[dotIndex];
    }
}
