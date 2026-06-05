using System.Collections.Generic;
using System.IO;
using UnityEditor;
using UnityEngine;

/// <summary>
/// Converts materials under a selected hierarchy root (e.g. GameObject (1)) to alinaFish,
/// preserving existing texture assignments.
/// </summary>
public static class ConvertToAlinaFishShader
{
    const string AlinaFishShaderName = "Shader Graphs/alinaFish";
    const string SubstanceRoot = "Assets/_artAssets/Alina/SubstanceTextures";

    static readonly string[] ColorProperties =
    {
        "_ColorMap", "_BaseMap", "_MainTex", "baseColorTexture"
    };

    static readonly string[] NormalProperties =
    {
        "_Normal", "_BumpMap", "normalTexture"
    };

    static readonly string[] SpecProperties =
    {
        "_Spec", "_MetallicGlossMap", "_SpecGlossMap", "metallicRoughnessTexture"
    };

    [MenuItem("Tools/Alina/Convert GameObject (1) Materials To alinaFish")]
    static void ConvertGameObject1Materials()
    {
        var root = FindGameObject1Root();
        if (root == null)
        {
            Debug.LogError(
                "Could not find 'GameObject (1)'. Select it in the hierarchy, or open a scene/prefab that contains it.");
            return;
        }

        ConvertMaterials(CollectMaterialsUnderRoot(root));
    }

    [MenuItem("Tools/Alina/Convert Selected Root Materials To alinaFish")]
    static void ConvertSelectedRoot()
    {
        var root = Selection.activeGameObject;
        if (root == null)
        {
            Debug.LogError("Select GameObject (1) (or any parent) in the hierarchy, then run this menu item.");
            return;
        }

        ConvertMaterials(CollectMaterialsUnderRoot(root));
    }

    [MenuItem("Tools/Alina/Fix Selected Root alinaFish Texture Mappings")]
    static void FixSelectedRootTextureMappings()
    {
        var root = Selection.activeGameObject ?? FindGameObject1Root();
        if (root == null)
        {
            Debug.LogError("Select GameObject (1) in the hierarchy, then run this menu item.");
            return;
        }

        var shader = Shader.Find(AlinaFishShaderName);
        if (shader == null)
        {
            Debug.LogError($"Shader not found: {AlinaFishShaderName}");
            return;
        }

        var fixedCount = 0;
        foreach (var mat in CollectMaterialsUnderRoot(root))
        {
            if (mat.shader != shader)
                continue;

            Undo.RecordObject(mat, "Fix alinaFish texture mappings");
            if (ApplyAlinaFishShader(mat, shader, forceRemap: true))
            {
                EditorUtility.SetDirty(mat);
                fixedCount++;
            }
        }

        AssetDatabase.SaveAssets();
        Debug.Log($"Re-mapped textures on {fixedCount} material(s) under '{root.name}'.");
    }

    static GameObject FindGameObject1Root()
    {
        if (Selection.activeGameObject != null && Selection.activeGameObject.name == "GameObject (1)")
            return Selection.activeGameObject;

        var all = Object.FindObjectsByType<Transform>(FindObjectsInactive.Include, FindObjectsSortMode.None);
        foreach (var t in all)
        {
            if (t.name == "GameObject (1)")
                return t.gameObject;
        }

        return null;
    }

    static HashSet<Material> CollectMaterialsUnderRoot(GameObject root)
    {
        var materials = new HashSet<Material>();
        var renderers = root.GetComponentsInChildren<Renderer>(true);

        foreach (var renderer in renderers)
        {
            foreach (var mat in renderer.sharedMaterials)
            {
                if (mat == null)
                    continue;

                materials.Add(mat);

                var assetPath = AssetDatabase.GetAssetPath(mat);
                if (string.IsNullOrEmpty(assetPath))
                    continue;

                foreach (var asset in AssetDatabase.LoadAllAssetsAtPath(assetPath))
                {
                    if (asset is Material embedded && embedded != mat)
                        materials.Add(embedded);
                }
            }
        }

        return materials;
    }

    static void ConvertMaterials(IEnumerable<Material> materials)
    {
        var shader = Shader.Find(AlinaFishShaderName);
        if (shader == null)
        {
            Debug.LogError($"Shader not found: {AlinaFishShaderName}");
            return;
        }

        var converted = 0;
        var remapped = 0;

        foreach (var mat in materials)
        {
            var assetPath = AssetDatabase.GetAssetPath(mat);
            var isSubAsset = !string.IsNullOrEmpty(assetPath) &&
                             AssetDatabase.IsSubAsset(mat) &&
                             !AssetDatabase.IsMainAsset(mat);

            if (mat.shader == shader)
            {
                Undo.RecordObject(mat, "Fix alinaFish texture mappings");
                if (ApplyAlinaFishShader(mat, shader, forceRemap: true))
                {
                    EditorUtility.SetDirty(mat);
                    remapped++;
                }
                continue;
            }

            Undo.RecordObject(mat, "Convert to alinaFish");
            if (ApplyAlinaFishShader(mat, shader, forceRemap: false))
            {
                EditorUtility.SetDirty(mat);
                converted++;
            }

            if (isSubAsset)
                Debug.Log($"Converted embedded material '{mat.name}' from '{assetPath}'.", mat);
        }

        AssetDatabase.SaveAssets();
        Debug.Log($"alinaFish conversion done. Converted: {converted}, re-mapped: {remapped}.");
    }

    static bool ApplyAlinaFishShader(Material mat, Shader shader, bool forceRemap)
    {
        var color = GetTexture(mat, ColorProperties);
        var normal = GetTexture(mat, NormalProperties);
        var spec = GetTexture(mat, SpecProperties);

        if (color == null && normal == null && spec == null)
            TryLoadSubstanceTextures(mat.name, ref color, ref normal, ref spec);

        var alreadyCorrectShader = mat.shader == shader;
        var hasMappedColor = mat.HasProperty("_ColorMap") && mat.GetTexture("_ColorMap") != null;

        if (alreadyCorrectShader && hasMappedColor && !forceRemap)
            return false;

        if (!alreadyCorrectShader)
            mat.shader = shader;

        if (color != null)
            mat.SetTexture("_ColorMap", color);
        if (normal != null)
            mat.SetTexture("_Normal", normal);
        if (spec != null)
            mat.SetTexture("_Spec", spec);

        return color != null || normal != null || spec != null || !alreadyCorrectShader;
    }

    static Texture GetTexture(Material mat, params string[] propertyNames)
    {
        foreach (var name in propertyNames)
        {
            if (!mat.HasProperty(name))
                continue;
            var tex = mat.GetTexture(name);
            if (tex != null)
                return tex;
        }

        return null;
    }

    static bool TryLoadSubstanceTextures(string materialName, ref Texture color, ref Texture normal, ref Texture spec)
    {
        if (!Directory.Exists(SubstanceRoot))
            return false;

        var prefix = ResolveSubstancePrefix(materialName);
        if (string.IsNullOrEmpty(prefix))
            return false;

        color ??= FindSubstanceTexture(prefix, "AlbedoTransparency");
        normal ??= FindSubstanceTexture(prefix, "Normal");
        spec ??= FindSubstanceTexture(prefix, "MetallicSmoothness")
                 ?? FindSubstanceTexture(prefix, "SpecularSmoothness");

        return color != null || normal != null || spec != null;
    }

    static string ResolveSubstancePrefix(string materialName)
    {
        switch (materialName)
        {
            case "Kelp_1_BAKED": return "Kelp_1_BAKED.001";
            case "Kelp_2_BAKED.001": return "ALLMODELSTOSUBSTANCE_Kelp_2_BAKED.001";
            case "Kelp_3_BAKED": return "Kelp_3_BAKED.001";
            case "Kelp_4_BAKED": return "Kelp_4_BAKED.001";
            case "Kelp_5_BAKED": return "Kelp_5_BAKED.001";
            case "Kelp_6_BAKED": return "Kelp_6_BAKED.001";
            case "Fern_1_BAKED": return "Fern_1_BAKED.001";
            case "Fern_2_BAKED": return "Fern_2_BAKED.001";
            case "Flower_3_1_BAKED": return "Flower_3_1_BAKED.001";
            case "Flower_3_2_BAKED": return "Flower_3_2_BAKED.001";
            default:
                if (materialName.EndsWith("_BAKED") || materialName.Contains("_BAKED."))
                    return materialName.EndsWith(".001") ? materialName : materialName + ".001";
                return null;
        }
    }

    static Texture2D FindSubstanceTexture(string prefix, string suffix)
    {
        var filter = $"{prefix}_{suffix}";
        var guids = AssetDatabase.FindAssets($"{filter} t:Texture2D", new[] { SubstanceRoot });
        foreach (var guid in guids)
        {
            var path = AssetDatabase.GUIDToAssetPath(guid);
            if (Path.GetFileNameWithoutExtension(path).Contains(filter))
                return AssetDatabase.LoadAssetAtPath<Texture2D>(path);
        }

        return null;
    }
}
