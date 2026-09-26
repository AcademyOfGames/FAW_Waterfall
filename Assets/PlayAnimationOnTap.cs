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

    private void Awake()
    {
        if (animator == null)
        {
            animator = GetComponent<Animator>();
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
