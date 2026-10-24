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
    [Tooltip("Volume the water audio settles at while the water is OUT (animation reversed), so the bird calls carry.")]
    [Range(0f, 1f)] [SerializeField] private float outVolume = 0.15f;
    [Tooltip("Seconds to lerp between Max Volume and Out Volume when the tide turns.")]
    [SerializeField] private float tideFadeSeconds = 10f;

    [Header("Wave Scroll")]
    [Tooltip("Drive the water material's scroll direction with the tide: negative while the water comes in, " +
             "positive (the material's starting value) while it goes out.")]
    [SerializeField] private bool reverseScrollWhenRewinding = true;
    [Tooltip("Seconds to lerp the scroll from the incoming direction back to the outgoing one when the tide turns.")]
    [SerializeField] private float scrollFlipSeconds = 5f;
    [Tooltip("Shader reference name of the scroll clock the script feeds every frame. The water shader's normal-map " +
             "drift reads this instead of the global time, so the direction can change without the texture jumping.")]
    [SerializeField] private string scrollTimeProperty = "_ScrollTime";

    private Coroutine fadeRoutine;
    private bool reversing;
    private readonly System.Collections.Generic.List<Material> scrollMaterials = new System.Collections.Generic.List<Material>();
    private float scrollTime;          // the clock handed to the shader
    private float scrollDirection = 1f; // +1 scene start / tide out, -1 tide in
    private Coroutine scrollRoutine;

    /// <summary>True while the animation is playing backwards (tide going out).</summary>
    public bool IsReversing => reversing;

    /// <summary>
    /// True once the current playback has reached its end: the last frame when playing forward,
    /// the first frame when playing in reverse. False before Play()/PlayReverse() has been called.
    /// </summary>
    public bool IsFinished
    {
        get
        {
            if (!triggered || animator == null || !animator.isActiveAndEnabled) return false;
            var info = animator.GetCurrentAnimatorStateInfo(layer);
            if (reversing)
                return HasDirectionParameter(animator) ? info.normalizedTime <= 0.001f : reverseFinished;
            return info.normalizedTime >= 1f;
        }
    }

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

        // Every renderer in the prefab whose material has the scroll clock (the water plane and the
        // sand puddles share the shader). Materials are instanced so the assets are never modified.
        if (!string.IsNullOrEmpty(scrollTimeProperty))
        {
            Transform root = transform.parent != null ? transform.parent : transform;
            foreach (var r in root.GetComponentsInChildren<Renderer>(true))
            {
                if (r.sharedMaterial != null && r.sharedMaterial.HasProperty(scrollTimeProperty))
                    scrollMaterials.Add(r.material);
            }
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
            linked.speed = Mathf.Abs(alsoPlaySpeeds[i]);
            SetDirection(linked, 1f);
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

        reversing = false;
        reverseFinished = false;
        if (reverseRoutine != null) { StopCoroutine(reverseRoutine); reverseRoutine = null; }
        if (animator != null)
        {
            animator.speed = 1f;
            SetDirection(animator, 1f);
            animator.Play(stateName, layer, 0f);
        }
        PlayLinked();
        ApplyScrollDirection();
        StartAudioFade();
    }

    /// <summary>
    /// Plays the animation backwards from its last frame (tide going out), reverses the linked
    /// animations and the wave scroll, and lerps the audio down to Out Volume.
    /// </summary>
    public void PlayReverse()
    {
        triggered = true;
        if (!gameObject.activeSelf)
        {
            gameObject.SetActive(true);
        }

        reversing = true;
        reverseFinished = false;
        if (reverseRoutine != null) { StopCoroutine(reverseRoutine); reverseRoutine = null; }
        Debug.Log($"PlayAnimationOnTap: PlayReverse on '{name}' (state '{stateName}')", this);

        // Preferred path: the controller's "Direction" float drives the state's speed multiplier,
        // which Mecanim honours when negative. Falls back to scrubbing for controllers without it.
        bool scrubNeeded = false;
        if (animator != null)
        {
            if (HasDirectionParameter(animator))
            {
                animator.speed = 1f;
                SetDirection(animator, -1f);
                animator.Play(stateName, layer, 1f);
            }
            else scrubNeeded = true;
        }
        if (alsoPlay != null)
        {
            for (int i = 0; i < alsoPlay.Length; i++)
            {
                var linked = alsoPlay[i];
                if (linked == null || !linked.isActiveAndEnabled) continue;
                if (HasDirectionParameter(linked))
                {
                    string requested = alsoPlayStates != null && i < alsoPlayStates.Length ? alsoPlayStates[i] : null;
                    linked.speed = alsoPlaySpeeds != null && i < alsoPlaySpeeds.Length ? Mathf.Abs(alsoPlaySpeeds[i]) : 1f;
                    SetDirection(linked, -1f);
                    linked.Play(ResolveLinkedState(linked, requested), 0, 1f);
                }
                else scrubNeeded = true;
            }
        }
        if (scrubNeeded)
        {
            Debug.LogWarning("PlayAnimationOnTap: a controller has no 'Direction' parameter; using frame-by-frame scrub for it.", this);
            reverseRoutine = StartCoroutine(ReverseScrub());
        }

        ApplyScrollDirection();
        FadeAudioTo(outVolume, tideFadeSeconds);
    }

    private static readonly int DirectionHash = Animator.StringToHash("Direction");

    private static bool HasDirectionParameter(Animator anim)
    {
        if (anim == null || anim.runtimeAnimatorController == null) return false;
        foreach (var prm in anim.parameters)
            if (prm.nameHash == DirectionHash && prm.type == AnimatorControllerParameterType.Float) return true;
        return false;
    }

    private static void SetDirection(Animator anim, float direction)
    {
        if (HasDirectionParameter(anim)) anim.SetFloat(DirectionHash, direction);
    }

    private Coroutine reverseRoutine;
    private bool reverseFinished;

    /// <summary>
    /// Mecanim ignores a negative Animator.speed, so reverse playback is done by hand: every
    /// animator is frozen (speed 0) and re-posed each frame at a normalized time that counts
    /// down from 1 to 0 over the clip's own length. Each linked clip rewinds at its own pace.
    /// </summary>
    private IEnumerator ReverseScrub()
    {
        var targets = new System.Collections.Generic.List<(Animator anim, int hash, int lay, float length, float t)>();

        if (animator != null && !HasDirectionParameter(animator))
        {
            animator.speed = 0f;
            animator.Play(stateName, layer, 1f);
            targets.Add((animator, Animator.StringToHash(stateName), layer, 0f, 1f));
        }
        if (alsoPlay != null)
        {
            for (int i = 0; i < alsoPlay.Length; i++)
            {
                var linked = alsoPlay[i];
                if (linked == null || !linked.isActiveAndEnabled || HasDirectionParameter(linked)) continue;
                string requested = alsoPlayStates != null && i < alsoPlayStates.Length ? alsoPlayStates[i] : null;
                int hash = ResolveLinkedState(linked, requested);
                linked.speed = 0f;
                linked.Play(hash, 0, 1f);
                targets.Add((linked, hash, 0, 0f, 1f));
            }
        }

        // Let the Play() calls take effect so the state lengths can be read.
        yield return null;
        for (int i = 0; i < targets.Count; i++)
        {
            var tg = targets[i];
            float len = tg.anim.GetCurrentAnimatorStateInfo(tg.lay).length;
            targets[i] = (tg.anim, tg.hash, tg.lay, Mathf.Max(len, 0.01f), 1f);
        }

        bool anyRunning = true;
        while (anyRunning)
        {
            anyRunning = false;
            for (int i = 0; i < targets.Count; i++)
            {
                var tg = targets[i];
                if (tg.t <= 0f || tg.anim == null) continue;
                tg.t = Mathf.Max(0f, tg.t - Time.deltaTime / tg.length);
                if (tg.anim.isActiveAndEnabled)
                    tg.anim.Play(tg.hash, tg.lay, tg.t);
                targets[i] = tg;
                if (tg.t > 0f) anyRunning = true;
            }
            yield return null;
        }

        reverseFinished = true;
        reverseRoutine = null;
    }

    /// <summary>
    /// Water coming in: the scroll clock runs backwards (direction -1) from the moment the animation
    /// starts. Water going out: the direction lerps back to +1 over Scroll Flip Seconds. Because the
    /// shader reads this clock rather than the global time, changing direction never makes the
    /// texture jump; it just slows, stops, and drifts the other way.
    /// </summary>
    private void ApplyScrollDirection()
    {
        if (scrollMaterials.Count == 0 || !reverseScrollWhenRewinding) return;
        if (scrollRoutine != null) StopCoroutine(scrollRoutine);

        if (!reversing)
        {
            scrollDirection = -1f;
            return;
        }
        scrollRoutine = StartCoroutine(LerpScrollDirection(1f, Mathf.Max(scrollFlipSeconds, 0f)));
    }

    private IEnumerator LerpScrollDirection(float target, float duration)
    {
        float from = scrollDirection;
        for (float t = 0f; t < duration; t += Time.deltaTime)
        {
            scrollDirection = Mathf.Lerp(from, target, t / duration);
            yield return null;
        }
        scrollDirection = target;
        scrollRoutine = null;
    }

    private void LateUpdate()
    {
        if (scrollMaterials.Count == 0) return;
        scrollTime += Time.deltaTime * scrollDirection;
        for (int i = 0; i < scrollMaterials.Count; i++)
            if (scrollMaterials[i] != null) scrollMaterials[i].SetFloat(scrollTimeProperty, scrollTime);
    }

    /// <summary>Lerps the water audio to a target volume over the given seconds.</summary>
    public void FadeAudioTo(float target, float seconds)
    {
        if (audioSource == null) return;
        if (fadeRoutine != null) StopCoroutine(fadeRoutine);
        fadeRoutine = StartCoroutine(FadeTo(Mathf.Clamp01(target), Mathf.Max(seconds, 0f)));
    }

    private IEnumerator FadeTo(float target, float duration)
    {
        if (!audioSource.isPlaying) audioSource.Play();
        float from = audioSource.volume;
        for (float t = 0f; t < duration; t += Time.deltaTime)
        {
            audioSource.volume = Mathf.Lerp(from, target, t / duration);
            yield return null;
        }
        audioSource.volume = target;
        fadeRoutine = null;
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
        // First time: from silence after the optional delay. Later cycles: from wherever the
        // tide-out fade left the volume, so the water swells back in rather than cutting out.
        float from = audioSource.isPlaying ? audioSource.volume : 0f;
        audioSource.volume = from;
        if (!audioSource.isPlaying && audioDelaySeconds > 0f)
            yield return new WaitForSeconds(audioDelaySeconds);

        if (!audioSource.isPlaying) audioSource.Play();

        float duration = Mathf.Max(audioSource.volume > 0f ? tideFadeSeconds : fadeInSeconds, 0f);
        for (float t = 0f; t < duration; t += Time.deltaTime)
        {
            audioSource.volume = Mathf.Lerp(from, maxVolume, t / duration);
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
