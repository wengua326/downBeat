using UnityEditor;
using UnityEngine;

/// <summary>
/// Switches the Editor (and therefore Play mode in the Meta XR Simulator) from the heavy "PC" quality
/// preset (HDR, soft shadows, extra shadow lights) to the lighter "Mobile" preset, which is also what
/// the Quest build uses. Runs once per machine automatically; can be re-run from the menu.
/// To undo: Edit > Project Settings > Quality and click "PC".
/// </summary>
[InitializeOnLoad]
public static class RhythmDartPerformanceSetup
{
    private const string DoneKey = "RhythmDartMR_PerformanceSetup_v1";
    private const string TargetQualityName = "Mobile";

    static RhythmDartPerformanceSetup()
    {
        if (EditorPrefs.GetBool(DoneKey, false)) return;
        EditorApplication.delayCall += () =>
        {
            if (EditorApplication.isPlayingOrWillChangePlaymode) return;
            ApplyPerformanceSettings();
            EditorPrefs.SetBool(DoneKey, true);
        };
    }

    [MenuItem("Window/Meta/Rhythm Dart MR - Use Performance Settings")]
    public static void ApplyPerformanceSettings()
    {
        string[] names = QualitySettings.names;
        for (int i = 0; i < names.Length; i++)
        {
            if (names[i] != TargetQualityName) continue;
            if (QualitySettings.GetQualityLevel() != i)
            {
                QualitySettings.SetQualityLevel(i, true);
                AssetDatabase.SaveAssets();
                Debug.Log($"[RhythmDartMR] Editor quality set to '{TargetQualityName}' for smoother simulator performance.");
            }
            return;
        }
        Debug.LogWarning($"[RhythmDartMR] Quality level '{TargetQualityName}' not found; leaving quality settings unchanged.");
    }
}
