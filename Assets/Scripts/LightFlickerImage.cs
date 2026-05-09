using System.Collections;
using UnityEngine;
using UnityEngine.UI;

/// <summary>
/// On start, flickers a UI Image alpha like a light strike — strong and hectic at first,
/// then softer — over a configurable duration (~3s default).
/// </summary>
public class LightFlickerImage : MonoBehaviour
{
    [SerializeField] private Image targetImage;
    [SerializeField] private float durationSeconds = 3f;

    private Color _baseColor;
    private Coroutine _flicker;

    private void Awake()
    {
        if (targetImage == null)
        {
            targetImage = GetComponent<Image>();
        }

        if (targetImage != null)
        {
            _baseColor = targetImage.color;
        }
    }

    private void Start()
    {
        if (targetImage == null)
        {
            return;
        }

        if (_flicker != null)
        {
            StopCoroutine(_flicker);
        }

        _flicker = StartCoroutine(FlickerRoutine());
    }

    private IEnumerator FlickerRoutine()
    {
        float elapsed = 0f;
        Image img = targetImage;

        while (elapsed < durationSeconds)
        {
            float t = elapsed / durationSeconds;
            // Overall brightness falls — quicker at the end feels like the flash dying away.
            float envelope = 1f - (t * t);

            // Oscillation speeds drop over time: frantic → gentle.
            float freqA = Mathf.Lerp(32f, 7f, t);
            float freqB = Mathf.Lerp(21f, 5f, t);
            float wave =
                0.5f * (Mathf.Sin(elapsed * freqA) * 0.5f + 0.5f) +
                0.5f * (Mathf.Sin(elapsed * freqB + 1.7f) * 0.5f + 0.5f);

            // Random crackle — mostly early.
            float chaos = (1f - t) * (1f - t);
            float crackle = Random.value < chaos * 0.45f ? Random.Range(0.55f, 1f) : Random.Range(0f, 0.35f);

            float blend = Mathf.Clamp01(Mathf.Lerp(wave, Mathf.Max(wave, crackle), chaos * 0.75f));
            float alpha = envelope * Mathf.Lerp(0.08f, 1f, blend);

            Color c = _baseColor;
            c.a = Mathf.Clamp01(alpha);
            img.color = c;

            elapsed += Time.deltaTime;
            yield return null;
        }

        Color endColor = _baseColor;
        endColor.a = 0f;
        img.color = endColor;
        _flicker = null;
    }

    private void OnDisable()
    {
        if (_flicker != null)
        {
            StopCoroutine(_flicker);
            _flicker = null;
        }
    }
}
