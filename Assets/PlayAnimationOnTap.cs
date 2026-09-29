using System.Collections;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.InputSystem;

/// <summary>
/// Plays an Animator state whenever the user taps (touch) or clicks (mouse) the screen.
/// </summary>
public class PlayAnimationOnTap : MonoBehaviour
{
    [SerializeField] private Animator animator;
    [SerializeField] private string stateName = "waterIn";
    [SerializeField] private int layer = 0;
    [Tooltip("Restart the animation from the beginning on every tap.")]
    [SerializeField] private bool restartOnTap = true;
    [Tooltip("Ignore taps/clicks that land on UI elements.")]
    [SerializeField] private bool ignoreUI = true;
    [Tooltip("Play the animation as soon as the scene starts.")]
    [SerializeField] private bool playOnStart = true;
    [Tooltip("Also replay the animation when the screen is tapped.")]
    [SerializeField] private bool playOnTap = false;

    [Header("Audio")]
    [Tooltip("AudioSource to fade in when the animation plays. Found on this object if left empty.")]
    [SerializeField] private AudioSource audioSource;
    [Tooltip("Volume the audio fades up to when the animation starts.")]
    [Range(0f, 1f)] [SerializeField] private float maxVolume = 1f;
    [Tooltip("Seconds to fade from silent to Max Volume once the animation starts.")]
    [SerializeField] private float fadeInSeconds = 2f;
    [Tooltip("Seconds to wait after the animation starts before the fade begins.")]
    [SerializeField] private float audioDelaySeconds = 0f;

    private Coroutine fadeRoutine;

    private void Awake()
    {
        if (animator == null)
        {
            animator = GetComponent<Animator>();
        }
        if (audioSource == null)
        {
            audioSource = GetComponent<AudioSource>();
        }
        if (audioSource != null)
        {
            audioSource.playOnAwake = false;
            audioSource.volume = 0f;
        }
    }

    private void Start()
    {
        if (playOnStart && animator != null)
        {
            Play();
        }
    }

    /// <summary>Plays the animation from the beginning. Call this from other scripts to trigger it.</summary>
    public void Play()
    {
        if (animator != null)
        {
            animator.Play(stateName, layer, 0f);
        }
        StartAudioFade();
    }

    /// <summary>Fades the audio from silent up to Max Volume, starting it if it isn't already playing.</summary>
    public void StartAudioFade()
    {
        if (audioSource == null) return;
        if (fadeRoutine != null) StopCoroutine(fadeRoutine);
        fadeRoutine = StartCoroutine(FadeIn());
    }

    private IEnumerator FadeIn()
    {
        audioSource.volume = 0f;
        if (audioDelaySeconds > 0f)
            yield return new WaitForSeconds(audioDelaySeconds);

        if (!audioSource.isPlaying) audioSource.Play();

        float duration = Mathf.Max(fadeInSeconds, 0f);
        for (float t = 0f; t < duration; t += Time.deltaTime)
        {
            audioSource.volume = Mathf.Lerp(0f, maxVolume, t / duration);
            yield return null;
        }
        audioSource.volume = maxVolume;
        fadeRoutine = null;
    }

    /// <summary>Stops this from playing itself at scene start, so another script can trigger it instead.</summary>
    public void SuppressPlayOnStart()
    {
        playOnStart = false;
    }

    private void Update()
    {
        if (!playOnTap || animator == null || !WasTappedThisFrame(out int pointerId))
        {
            return;
        }

        if (ignoreUI && EventSystem.current != null && EventSystem.current.IsPointerOverGameObject(pointerId))
        {
            return;
        }

        if (restartOnTap)
        {
            animator.Play(stateName, layer, 0f);
        }
        else
        {
            animator.Play(stateName, layer);
        }
        StartAudioFade();
    }

    private static bool WasTappedThisFrame(out int pointerId)
    {
        pointerId = -1;

        var touchscreen = Touchscreen.current;
        if (touchscreen != null && touchscreen.primaryTouch.press.wasPressedThisFrame)
        {
            pointerId = touchscreen.primaryTouch.touchId.ReadValue();
            return true;
        }

        var mouse = Mouse.current;
        if (mouse != null && mouse.leftButton.wasPressedThisFrame)
        {
            return true;
        }

        return false;
    }
}
