using System;
using System.IO;
using Google.XR.ARCoreExtensions.Internal;
using UnityEditor;
using UnityEditor.Build;
using UnityEditor.Build.Reporting;
using UnityEngine;
#if UNITY_IOS
using UnityEditor.iOS.Xcode;
#endif

/// <summary>
/// Hooks for Unity Build Automation (Cloud Build).
///
/// In the Build Automation target's Advanced Settings, set
/// "Pre-Export Method Name" to: CloudBuildHooks.PreExport
///
/// Optional environment variables (Build Automation target > Environment Variables):
///   ARCORE_IOS_API_KEY        Google Cloud API key for ARCore Geospatial on iOS, so it
///                             does not have to be committed to the repo.
///   IOS_BUILD_NUMBER_OFFSET   Added to the Build Automation build number, so the
///                             CFBundleVersion is higher than builds already uploaded
///                             to App Store Connect.
/// </summary>
public class CloudBuildHooks : IPostprocessBuildWithReport
{
    public int callbackOrder => 100;

#if UNITY_CLOUD_BUILD
    public static void PreExport(UnityEngine.CloudBuild.BuildManifestObject manifest)
    {
        string cloudBuildNumber = manifest.GetValue<string>("buildNumber");
        ApplyBuildNumber(cloudBuildNumber);
        ApplyArCoreIosApiKey();
    }
#endif

    static void ApplyBuildNumber(string cloudBuildNumber)
    {
        if (!int.TryParse(cloudBuildNumber, out int buildNumber))
        {
            Debug.LogWarning($"[CloudBuildHooks] Could not parse build number '{cloudBuildNumber}', leaving it unchanged.");
            return;
        }

        if (int.TryParse(Environment.GetEnvironmentVariable("IOS_BUILD_NUMBER_OFFSET"), out int offset))
            buildNumber += offset;

        PlayerSettings.iOS.buildNumber = buildNumber.ToString();
        Debug.Log($"[CloudBuildHooks] iOS build number set to {buildNumber} (version {PlayerSettings.bundleVersion}).");
    }

    static void ApplyArCoreIosApiKey()
    {
        string apiKey = Environment.GetEnvironmentVariable("ARCORE_IOS_API_KEY");
        if (string.IsNullOrEmpty(apiKey))
            return;

        ARCoreExtensionsProjectSettings settings = ARCoreExtensionsProjectSettings.Instance;
        settings.IsIOSSupportEnabled = true;
        settings.IOSAuthenticationStrategySetting = IOSAuthenticationStrategy.ApiKey;
        settings.IOSCloudServicesApiKey = apiKey;
        Debug.Log("[CloudBuildHooks] ARCore iOS API key applied from environment.");
    }

    public void OnPostprocessBuild(BuildReport report)
    {
#if UNITY_IOS
        if (report.summary.platform != BuildTarget.iOS)
            return;

        string plistPath = Path.Combine(report.summary.outputPath, "Info.plist");
        PlistDocument plist = new PlistDocument();
        plist.ReadFromFile(plistPath);

        // App only uses standard HTTPS, which is exempt. Skips the export compliance
        // prompt on every TestFlight upload.
        plist.root.SetBoolean("ITSAppUsesNonExemptEncryption", false);

        plist.WriteToFile(plistPath);
#endif
    }
}
