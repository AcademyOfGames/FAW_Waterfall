#if UNITY_EDITOR
using System;
using System.Reflection;
using UnityEditor;
using UnityEngine;

/// <summary>
/// Hides AudioSource (and related) 3D icons in the Scene view so grow sounds don't clutter preview.
/// Uses reflection because AnnotationUtility is internal to UnityEditor.
/// </summary>
[InitializeOnLoad]
public static class HideSceneAudioIcons
{
    private static readonly string[] HiddenScriptClasses =
    {
        "AudioSource",
        "AudioClip",
        "AudioListener"
    };

    static HideSceneAudioIcons()
    {
        EditorApplication.delayCall += Apply;
    }

    private static void Apply()
    {
        Type utilityType = typeof(Editor).Assembly.GetType("UnityEditor.AnnotationUtility");
        if (utilityType == null)
        {
            return;
        }

        MethodInfo getAnnotations = utilityType.GetMethod(
            "GetAnnotations",
            BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
        MethodInfo setIconEnabled = utilityType.GetMethod(
            "SetIconEnabled",
            BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
        MethodInfo setGizmoEnabled = FindSetGizmoEnabledMethod(utilityType);

        if (getAnnotations == null)
        {
            return;
        }

        Array annotations = getAnnotations.Invoke(null, null) as Array;
        if (annotations == null)
        {
            return;
        }

        Type annotationType = typeof(Editor).Assembly.GetType("UnityEditor.Annotation");
        const BindingFlags fieldFlags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
        FieldInfo classIdField = annotationType?.GetField("classID", fieldFlags);
        FieldInfo scriptClassField = annotationType?.GetField("scriptClass", fieldFlags);

        bool changed = false;

        if (classIdField != null && scriptClassField != null)
        {
            changed |= HideFromAnnotations(
                annotations,
                classIdField,
                scriptClassField,
                setIconEnabled,
                setGizmoEnabled);
        }

        changed |= HideByTypeName(setIconEnabled, setGizmoEnabled, typeof(AudioSource));
        changed |= HideByTypeName(setIconEnabled, setGizmoEnabled, typeof(AudioListener));

        if (changed)
        {
            SceneView.RepaintAll();
        }
    }

    private static bool HideFromAnnotations(
        Array annotations,
        FieldInfo classIdField,
        FieldInfo scriptClassField,
        MethodInfo setIconEnabled,
        MethodInfo setGizmoEnabled)
    {
        bool changed = false;
        for (int i = 0; i < annotations.Length; i++)
        {
            object annotation = annotations.GetValue(i);
            if (annotation == null)
            {
                continue;
            }

            string scriptClass = scriptClassField.GetValue(annotation) as string;
            if (!ShouldHide(scriptClass))
            {
                continue;
            }

            int classId = (int)classIdField.GetValue(annotation);

            if (setIconEnabled != null)
            {
                setIconEnabled.Invoke(null, new object[] { classId, scriptClass, 0 });
                changed = true;
            }

            if (setGizmoEnabled != null)
            {
                InvokeSetGizmoEnabled(setGizmoEnabled, classId, scriptClass);
                changed = true;
            }
        }

        return changed;
    }

    private static bool HideByTypeName(MethodInfo setIconEnabled, MethodInfo setGizmoEnabled, Type componentType)
    {
        const int monoBehaviourClassId = 114;
        string scriptClass = componentType.Name;
        bool changed = false;

        if (setIconEnabled != null)
        {
            setIconEnabled.Invoke(null, new object[] { monoBehaviourClassId, scriptClass, 0 });
            changed = true;
        }

        if (setGizmoEnabled != null)
        {
            InvokeSetGizmoEnabled(setGizmoEnabled, monoBehaviourClassId, scriptClass);
            changed = true;
        }

        return changed;
    }

    private static MethodInfo FindSetGizmoEnabledMethod(Type utilityType)
    {
        MethodInfo[] methods = utilityType.GetMethods(BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
        for (int i = 0; i < methods.Length; i++)
        {
            if (methods[i].Name == "SetGizmoEnabled")
            {
                return methods[i];
            }
        }

        return null;
    }

    private static void InvokeSetGizmoEnabled(MethodInfo setGizmoEnabled, int classId, string scriptClass)
    {
        ParameterInfo[] parameters = setGizmoEnabled.GetParameters();
        if (parameters.Length == 4)
        {
            setGizmoEnabled.Invoke(null, new object[] { classId, scriptClass, 0, false });
            return;
        }

        if (parameters.Length == 3)
        {
            object third = parameters[2].ParameterType == typeof(bool) ? (object)false : (object)0;
            setGizmoEnabled.Invoke(null, new object[] { classId, scriptClass, third });
        }
    }

    private static bool ShouldHide(string scriptClass)
    {
        if (string.IsNullOrEmpty(scriptClass))
        {
            return false;
        }

        for (int i = 0; i < HiddenScriptClasses.Length; i++)
        {
            if (scriptClass == HiddenScriptClasses[i])
            {
                return true;
            }
        }

        return false;
    }
}
#endif
