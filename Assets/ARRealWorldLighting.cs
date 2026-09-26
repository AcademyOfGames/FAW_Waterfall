using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.XR.ARFoundation;

/// <summary>
/// Drives a directional light and the scene ambient from ARCore/ARKit light estimation,
/// so virtual objects are lit by the real environment. Reflections come from
/// AREnvironmentProbeManager; this handles direct light and ambient.
/// Put this on the scene's Directional Light.
/// </summary>
[RequireComponent(typeof(Light))]
public class ARRealWorldLighting : MonoBehaviour
{
    [Tooltip("Camera manager that reports light estimation. Found automatically if left empty.")]
    public ARCameraManager cameraManager;
    [Tooltip("Rotate the light to the estimated main light direction.")]
    public bool applyDirection = true;
    [Tooltip("Apply the estimated colour and brightness.")]
    public bool applyColorAndIntensity = true;
    [Tooltip("Apply estimated ambient light (spherical harmonics) to the scene.")]
    public bool applyAmbient = true;
    [Tooltip("How quickly the light follows the estimate. Lower is smoother.")]
    public float smoothing = 4f;

    Light directionalLight;
    Quaternion targetRotation;
    Color targetColor;
    float targetIntensity;
    bool hasDirection, hasColor;

    void Awake()
    {
        directionalLight = GetComponent<Light>();
        targetRotation = transform.rotation;
        targetColor = directionalLight.color;
        targetIntensity = directionalLight.intensity;

        if (cameraManager == null)
            cameraManager = FindObjectOfType<ARCameraManager>();
    }

    void OnEnable()
    {
        if (cameraManager != null) cameraManager.frameReceived += OnFrameReceived;
    }

    void OnDisable()
    {
        if (cameraManager != null) cameraManager.frameReceived -= OnFrameReceived;
    }

    void OnFrameReceived(ARCameraFrameEventArgs args)
    {
        var light = args.lightEstimation;

        if (applyDirection && light.mainLightDirection.HasValue)
        {
            targetRotation = Quaternion.LookRotation(light.mainLightDirection.Value);
            hasDirection = true;
        }

        if (applyColorAndIntensity)
        {
            if (light.mainLightColor.HasValue)
            {
                targetColor = light.mainLightColor.Value;
                hasColor = true;
            }
            else if (light.colorCorrection.HasValue)
            {
                targetColor = light.colorCorrection.Value;
                hasColor = true;
            }

            // ARCore reports lumens; ARKit reports brightness 0..1.
            if (light.mainLightIntensityLumens.HasValue)
                targetIntensity = light.averageMainLightBrightness ?? (light.mainLightIntensityLumens.Value / 10000f);
            else if (light.averageBrightness.HasValue)
                targetIntensity = light.averageBrightness.Value;
        }

        if (applyAmbient && light.ambientSphericalHarmonics.HasValue)
        {
            RenderSettings.ambientMode = AmbientMode.Skybox;
            RenderSettings.ambientProbe = light.ambientSphericalHarmonics.Value;
        }
    }

    void Update()
    {
        float k = 1f - Mathf.Exp(-Mathf.Max(smoothing, 0.01f) * Time.deltaTime);

        if (hasDirection)
            transform.rotation = Quaternion.Slerp(transform.rotation, targetRotation, k);

        if (hasColor)
            directionalLight.color = Color.Lerp(directionalLight.color, targetColor, k);

        directionalLight.intensity = Mathf.Lerp(directionalLight.intensity, targetIntensity, k);
    }
}
