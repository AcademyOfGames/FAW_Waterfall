using UnityEngine;

/// <summary>
/// Opens a URL in the device default browser via <see cref="Application.OpenURL"/>.
/// Wire <see cref="Open"/> to a UI Button, or call from code.
/// </summary>
public class OpenWebUrl : MonoBehaviour
{
    [SerializeField] private string url = "https://www.futurearts.co/";

    public void Open()
    {
        if (string.IsNullOrWhiteSpace(url))
        {
            Debug.LogWarning("[OpenWebUrl] URL is empty.");
            return;
        }

        Application.OpenURL(url);
    }

    /// <summary>For UnityEvents that pass a string parameter (optional alternate URL).</summary>
    public void Open(string explicitUrl)
    {
        if (!string.IsNullOrWhiteSpace(explicitUrl))
            Application.OpenURL(explicitUrl);
        else
            Open();
    }
}
