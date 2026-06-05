#if UNITY_IOS
using System.IO;
using UnityEditor;
using UnityEditor.Callbacks;
using UnityEditor.iOS.Xcode;

/// <summary>
/// Writes export-compliance and related keys onto the exported iOS Info.plist on every build,
/// so they survive Unity re-exporting the Xcode project into /Builds/.
/// </summary>
public static class iOSBuildPostProcessor
{
    [PostProcessBuild(100)]
    public static void OnPostProcessBuild(BuildTarget target, string pathToBuiltProject)
    {
        if (target != BuildTarget.iOS)
            return;

        var plistPath = Path.Combine(pathToBuiltProject, "Info.plist");
        if (!File.Exists(plistPath))
            return;

        var plist = new PlistDocument();
        plist.ReadFromFile(plistPath);

        // FutureArtsWay uses only standard HTTPS/TLS, which is exempt from US export regulations.
        // Declaring this stops App Store Connect re-prompting the encryption questionnaire on every upload.
        plist.root.SetBoolean("ITSAppUsesNonExemptEncryption", false);

        plist.WriteToFile(plistPath);
    }
}
#endif
