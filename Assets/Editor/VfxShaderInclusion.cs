using UnityEditor;
using UnityEngine;
using RhythmDartMR.VFX;

/// <summary>
/// The VFX materials are created at runtime with Shader.Find(), which only works in a player build
/// (e.g. the Quest APK) if the shader is included. This adds the URP Particles/Unlit shader to
/// Project Settings > Graphics > Always Included Shaders once, automatically.
/// </summary>
[InitializeOnLoad]
public static class VfxShaderInclusion
{
    static VfxShaderInclusion()
    {
        EditorApplication.delayCall += EnsureVfxShaderIncluded;
    }

    [MenuItem("Window/Meta/Rhythm Dart MR - Include VFX Shaders")]
    public static void EnsureVfxShaderIncluded()
    {
        var shader = Shader.Find(VfxResources.ParticleShaderName);
        if (shader == null) return;

        var assets = AssetDatabase.LoadAllAssetsAtPath("ProjectSettings/GraphicsSettings.asset");
        if (assets == null || assets.Length == 0) return;

        var so = new SerializedObject(assets[0]);
        var list = so.FindProperty("m_AlwaysIncludedShaders");
        if (list == null) return;

        for (int i = 0; i < list.arraySize; i++)
        {
            if (list.GetArrayElementAtIndex(i).objectReferenceValue == shader) return;
        }

        list.InsertArrayElementAtIndex(list.arraySize);
        list.GetArrayElementAtIndex(list.arraySize - 1).objectReferenceValue = shader;
        so.ApplyModifiedProperties();
        AssetDatabase.SaveAssets();
        Debug.Log($"[RhythmDartMR] Added '{VfxResources.ParticleShaderName}' to Always Included Shaders for runtime VFX.");
    }
}
