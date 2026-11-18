using UnityEngine;
using UnityEngine.Rendering.Universal;

namespace RhythmDartMR.VFX
{
    /// <summary>
    /// Runtime performance defaults applied automatically when the scene loads (no setup needed).
    /// The neon art style is entirely self-lit, so real-time shadows, HDR and post-processing
    /// cost GPU time without changing how the game looks.
    /// </summary>
    public static class PerformanceTuner
    {
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        private static void Apply()
        {
            int lights = 0, cams = 0;

            foreach (var light in Object.FindObjectsByType<Light>())
            {
                if (light.shadows != LightShadows.None)
                {
                    light.shadows = LightShadows.None;
                    lights++;
                }
            }

            foreach (var cam in Object.FindObjectsByType<Camera>())
            {
                cam.allowHDR = false;
                var data = cam.GetComponent<UniversalAdditionalCameraData>();
                if (data != null)
                {
                    data.renderShadows = false;
                    data.renderPostProcessing = false;
                }
                cams++;
            }

            Debug.Log($"[RhythmDartMR] Performance: disabled shadows on {lights} light(s), HDR/post-processing off on {cams} camera(s). Quality level: {QualitySettings.names[QualitySettings.GetQualityLevel()]}");
        }
    }
}
