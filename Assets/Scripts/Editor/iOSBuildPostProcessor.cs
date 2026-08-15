#if UNITY_IOS
using System.IO;
using UnityEditor;
using UnityEditor.Callbacks;
using UnityEditor.iOS.Xcode;

/// <summary>
/// Post-build steps for the exported iOS Xcode project, applied on every build so they survive
/// Unity re-exporting into /Builds/:
///  - Links the system frameworks the in-app video encoder needs (InAppFrameEncoder.mm uses
///    AVFoundation/CoreMedia/CoreVideo/VideoToolbox to write the mp4, and Photos to save it).
///  - Adds the Photos "add" usage description required to save the recording to the camera roll.
///  - Declares export-compliance so App Store Connect stops re-prompting the encryption
///    questionnaire on every upload.
/// (The microphone/camera/location usage strings come from ProjectSettings and are not repeated here.)
/// </summary>
public static class iOSBuildPostProcessor
{
    [PostProcessBuild(100)]
    public static void OnPostProcessBuild(BuildTarget target, string pathToBuiltProject)
    {
        if (target != BuildTarget.iOS)
            return;

        AddEncoderFrameworks(pathToBuiltProject);
        SetPlistKeys(pathToBuiltProject);
    }

    // The native video encoder (Assets/Plugins/iOS/InAppFrameEncoder.mm) links against these; Unity
    // does not auto-add system frameworks for a plugin's #imports, so we add them here.
    private static void AddEncoderFrameworks(string buildPath)
    {
        string pbxPath = PBXProject.GetPBXProjectPath(buildPath);
        var project = new PBXProject();
        project.ReadFromFile(pbxPath);

        // Native plug-in code lives in the UnityFramework target (Unity 2019.3+).
        string frameworkTarget = project.GetUnityFrameworkTargetGuid();

        foreach (var fw in new[]
        {
            "AVFoundation.framework",
            "CoreMedia.framework",
            "CoreVideo.framework",
            "VideoToolbox.framework",
            "Photos.framework",
        })
        {
            // AddFrameworkToProject is idempotent, so this is safe even if another post-processor
            // already added one of these.
            project.AddFrameworkToProject(frameworkTarget, fw, false);
        }

        project.WriteToFile(pbxPath);
    }

    private static void SetPlistKeys(string buildPath)
    {
        string plistPath = Path.Combine(buildPath, "Info.plist");
        if (!File.Exists(plistPath))
            return;

        var plist = new PlistDocument();
        plist.ReadFromFile(plistPath);
        var root = plist.root;

        // Required to save a recording into the Photos camera roll (PHPhotoLibrary add-only).
        if (!root.values.ContainsKey("NSPhotoLibraryAddUsageDescription"))
            root.SetString("NSPhotoLibraryAddUsageDescription",
                "FutureArtsWay saves your screen recordings to your photo library.");

        // FutureArtsWay uses only standard HTTPS/TLS, which is exempt from US export regulations.
        root.SetBoolean("ITSAppUsesNonExemptEncryption", false);

        plist.WriteToFile(plistPath);
    }
}
#endif
