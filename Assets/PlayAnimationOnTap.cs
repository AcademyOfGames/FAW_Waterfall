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

    [Tooltip("Switch this GameObject off when the scene starts and switch it back on the moment the animation is " +
             "triggered by another script (e.g. the heron landing). Leave the object ACTIVE in the scene; this script " +
             "hides it. While hidden, Play On Start and Play On Tap can't fire, so something must call Play().")]
    [SerializeField] private bool startDisabled = true;

    private bool triggered;

    [Header("Linked Animations")]
    [Tooltip("Other Animators to start at the same moment as this one (e.g. waterlinesSandWater). Each is held on the " +
             "first frame of its default state until then.")]
    [SerializeField] private Animator[] alsoPlay;
    [Tooltip("State to play on each linked Animator, matched by position in the list above. Leave an entry empty (or " +
             "the list short) to auto-pick: the state named after one of the controller's clips, else the default state.")]
    [SerializeField] private string[] alsoPlayStates;

    private float[] alsoPlaySpeeds;

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

        // Freeze the linked animators on their first frame until the water starts.
        alsoPlaySpeeds = new float[alsoPlay != null ? alsoPlay.Length : 0];
        for (int i = 0; i < alsoPlaySpeeds.Length; i++)
        {
            if (alsoPlay[i] == null) continue;
            alsoPlaySpeeds[i] = Mathf.Approximately(alsoPlay[i].speed, 0f) ? 1f : alsoPlay[i].speed;
            alsoPlay[i].speed = 0f;
        }

        // Hide until triggered. Skipped when Awake is running because Play() just activated us.
        if (startDisabled && !triggered)
        {
            gameObject.SetActive(false);
        }
    }

    private void PlayLinked()
    {
        if (alsoPlay == null || alsoPlaySpeeds == null) return;
        for (int i = 0; i < alsoPlay.Length && i < alsoPlaySpeeds.Length; i++)
        {
            var linked = alsoPlay[i];
            if (linked == null || !linked.isActiveAndEnabled) continue;
            linked.speed = alsoPlaySpeeds[i];
            string requested = alsoPlayStates != null && i < alsoPlayStates.Length ? alsoPlayStates[i] : null;
            linked.Play(ResolveLinkedState(linked, requested), 0, 0f);
        }
    }

    /// <summary>
    /// Picks the state to start on a linked Animator: the requested name if it exists, otherwise a state
    /// named after one of the controller's clips (so it still works when that state isn't the default),
    /// otherwise whatever state the Animator is currently sitting in.
    /// </summary>
    private int ResolveLinkedState(Animator linked, string requested)
    {
        if (!string.IsNullOrEmpty(requested))
        {
            int hash = Animator.StringToHash(requested);
            if (linked.HasState(0, hash)) return hash;
            Debug.LogWarning($"PlayAnimationOnTap: '{linked.name}' has no state named '{requested}'; auto-picking instead.", this);
        }

        var controller = linked.runtimeAnimatorController;
        if (controller != null)
        {
            foreach (var clip in controller.animationClips)
            {
                if (clip == null) continue;
                int hash = Animator.StringToHash(clip.name);
                if (linked.HasState(0, hash)) return hash;
            }
        }

        return linked.GetCurrentAnimatorStateInfo(0).fullPathHash;
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
        // Enable first: the Animator and the audio fade coroutine both need an active object.
        triggered = true;
        if (!gameObject.activeSelf)
        {
            gameObject.SetActive(true);
        }

        if (animator != null)
        {
            animator.Play(stateName, layer, 0f);
        }
        PlayLinked();
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
        PlayLinked();
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
