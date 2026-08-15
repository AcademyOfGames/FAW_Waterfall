using System;
using UnityEngine;

/// <summary>
/// RETIRED — inert stub.
///
/// iOS screen recording no longer goes through ReplayKit / SmileSoftRecorderPro.framework. Both
/// platforms now record Unity's own frames via <see cref="InAppScreenRecorder"/> (see
/// InAppFrameEncoder.mm / InAppFrameEncoder.java). The native ReplayKit framework has been removed
/// from the project, so this adapter's [DllImport("__Internal")] bindings to its RK_* symbols were
/// deleted — keeping them would leave IL2CPP with undefined symbols at link time.
///
/// The type is kept only so the (also-retired) <see cref="SmileSoftScreenRecordController"/> still
/// compiles; nothing routes to it at runtime. Safe to delete along with SmileSoftScreenRecordController
/// and EasyScreenRecordInitializer once the "Screen Recorder" prefab is cleaned up in the Editor.
/// </summary>
public static class ReplayKitBridge
{
    private const string RetiredMsg =
        "[ReplayKitBridge] Retired — iOS recording now uses InAppScreenRecorder (Unity-frame capture). No-op.";

    public static void StartRecording(int audioMode, bool saveToPhotos, bool saveToDocuments)
    {
        Debug.LogWarning(RetiredMsg);
    }

    public static void StopRecording(Action<string> onRecordingSaved)
    {
        Debug.LogWarning(RetiredMsg);
        onRecordingSaved?.Invoke(string.Empty);
    }

    internal static void NotifyRecordingSaved(string path) { }

    public static bool IsRecordingAvailable() => false;

    public static bool IsRecording() => false;

    public static void PlayVideo(string path) { }

    public static void ShareVideo(string path) { }

    public static void SetFreeVersionMaxRecordingSeconds(int seconds) { }
}
