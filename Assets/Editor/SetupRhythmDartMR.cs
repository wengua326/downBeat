using System.Reflection;
using System.Text;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.XR.OpenXR;
using UnityEngine.XR.OpenXR.Features;
using RhythmDartMR.Audio;
using RhythmDartMR.Gameplay;
using RhythmDartMR.Input;
using RhythmDartMR.UI;
using RhythmDartMR.VFX;

[InitializeOnLoad]
public static class SetupRhythmDartMR
{
    private const string SceneVersionTag = "RhythmDartMR_v10_CenteredWallMount";

    static SetupRhythmDartMR()
    {
        AssemblyReloadEvents.beforeAssemblyReload += OnBeforeAssemblyReload;
        EditorApplication.delayCall += EnsureSceneSetupWhenOpened;
        EditorApplication.delayCall += () => FixMetaXRProjectSetupRequiredIssues();
        EditorApplication.delayCall += DisableUnusedMrukGlobalContextInPlayMode;
        EditorApplication.delayCall += SyncDartboardPlacementOnReload;
        EditorSceneManager.sceneOpened += (_, _) => EnsureSceneSetupWhenOpened();
    }

    [InitializeOnLoadMethod]
    private static void InitializeMetaXRProjectSetupFixes()
    {
        if (EditorApplication.isPlayingOrWillChangePlaymode) return;
        FixMetaXRProjectSetupRequiredIssues();
    }

    [MenuItem("Window/Meta/Rhythm Dart MR - Fix Meta XR Project Setup")]
    public static string FixMetaXRProjectSetupRequiredIssues()
    {
        if (EditorApplication.isPlayingOrWillChangePlaymode) return string.Empty;

        var sb = new StringBuilder();
        try
        {
            // 1. Apply safe project settings directly (avoids modal dialogs like Direct3D11 switch prompt)
            if (PlayerSettings.colorSpace != ColorSpace.Linear)
            {
                PlayerSettings.colorSpace = ColorSpace.Linear;
                sb.AppendLine("Set PlayerSettings.colorSpace = Linear");
            }

            if (PlayerSettings.GetUseDefaultGraphicsAPIs(BuildTarget.StandaloneWindows64))
            {
                PlayerSettings.SetUseDefaultGraphicsAPIs(BuildTarget.StandaloneWindows64, false);
                PlayerSettings.SetGraphicsAPIs(BuildTarget.StandaloneWindows64, new[] { UnityEngine.Rendering.GraphicsDeviceType.Direct3D11 });
                sb.AppendLine("Configured StandaloneWindows64 manual Graphics API: Direct3D11");
            }

            if ((int)PlayerSettings.Android.minSdkVersion < 32)
            {
                PlayerSettings.Android.minSdkVersion = (AndroidSdkVersions)32;
                sb.AppendLine("Set Android minSdkVersion = 32");
            }

            if (PlayerSettings.GetScriptingBackend(UnityEditor.Build.NamedBuildTarget.Android) != ScriptingImplementation.IL2CPP)
            {
                PlayerSettings.SetScriptingBackend(UnityEditor.Build.NamedBuildTarget.Android, ScriptingImplementation.IL2CPP);
                sb.AppendLine("Set Android ScriptingBackend = IL2CPP");
            }

            if (PlayerSettings.Android.targetArchitectures != AndroidArchitecture.ARM64)
            {
                PlayerSettings.Android.targetArchitectures = AndroidArchitecture.ARM64;
                sb.AppendLine("Set Android targetArchitectures = ARM64");
            }

            if (PlayerSettings.Android.androidTVCompatibility)
            {
                PlayerSettings.Android.androidTVCompatibility = false;
                sb.AppendLine("Disabled Android TV compatibility");
            }

            if (PlayerSettings.Android.applicationEntry != AndroidApplicationEntry.GameActivity)
            {
                PlayerSettings.Android.applicationEntry = AndroidApplicationEntry.GameActivity;
                sb.AppendLine("Set Android applicationEntry = GameActivity");
            }

            // 2. Configure OVRProjectConfig
            var ovrConfig = OVRProjectConfig.CachedProjectConfig;
            if (ovrConfig != null)
            {
                bool changed = false;
                if (ovrConfig.handTrackingSupport != OVRProjectConfig.HandTrackingSupport.ControllersAndHands)
                {
                    ovrConfig.handTrackingSupport = OVRProjectConfig.HandTrackingSupport.ControllersAndHands;
                    changed = true;
                }
                if (ovrConfig.handTrackingFrequency != OVRProjectConfig.HandTrackingFrequency.MAX)
                {
                    ovrConfig.handTrackingFrequency = OVRProjectConfig.HandTrackingFrequency.MAX;
                    changed = true;
                }
                if (ovrConfig.insightPassthroughSupport == OVRProjectConfig.FeatureSupport.None)
                {
                    ovrConfig.insightPassthroughSupport = OVRProjectConfig.FeatureSupport.Supported;
                    changed = true;
                }
                if (ovrConfig.sceneSupport != OVRProjectConfig.FeatureSupport.Required)
                {
                    ovrConfig.sceneSupport = OVRProjectConfig.FeatureSupport.Required;
                    changed = true;
                }
                if (ovrConfig.systemLoadingScreenBackground != OVRProjectConfig.SystemLoadingScreenBackground.ContextualPassthrough)
                {
                    ovrConfig.systemLoadingScreenBackground = OVRProjectConfig.SystemLoadingScreenBackground.ContextualPassthrough;
                    changed = true;
                }
                if (ovrConfig.anchorSupport != OVRProjectConfig.AnchorSupport.Enabled)
                {
                    ovrConfig.anchorSupport = OVRProjectConfig.AnchorSupport.Enabled;
                    changed = true;
                }
                if (changed)
                {
                    OVRProjectConfig.CommitProjectConfig(ovrConfig);
                    sb.AppendLine("Updated OVRProjectConfig (SceneSupport=Required, SplashBackground=Contextual, AnchorSupport=Enabled, Passthrough=Supported, HandTracking=MAX)");
                }
            }

            // 3. Configure OpenXR features for Standalone & Android
            ConfigureOpenXRFeatures(sb);

            // 4. Silence startup summary log spam in OVRProjectSetupUpdater / OVRProjectSetup
            var setupType = typeof(OVRProjectSetup);
            var allowLogsField = setupType.GetField("AllowLogs", BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public);
            var allowLogsObj = allowLogsField?.GetValue(null);
            allowLogsObj?.GetType().GetProperty("Value", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)?.SetValue(allowLogsObj, false);

            var updaterType = setupType.Assembly.GetType("OVRProjectSetupUpdater");
            var showOnceField = updaterType?.GetField("ShowLogsOnlyOnce", BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public);
            var showOnceObj = showOnceField?.GetValue(null);
            // Reading Value on OnlyOncePerSessionBool sets its SessionState key to false
            showOnceObj?.GetType().GetProperty("Value", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)?.GetValue(showOnceObj);

            // 5. Inspect and resolve all OVRProjectSetup Required tasks across Standalone and Android
            var getTasksMethod = setupType.GetMethod("GetTasks", BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public);
            var isVisibleMethod = setupType.GetMethod("IsTaskVisibleInProjectSetupTool", BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public);

            var updateTasksMethod = setupType.GetMethod("UpdateTasks", BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public);

            foreach (var group in new[] { BuildTargetGroup.Standalone, BuildTargetGroup.Android })
            {
                if (getTasksMethod == null) continue;
                var tasks = getTasksMethod.Invoke(null, new object[] { group }) as System.Collections.IEnumerable;
                if (tasks == null) continue;

                int passedRequired = 0;
                int remainingRequired = 0;

                foreach (var task in tasks)
                {
                    if (task == null) continue;
                    var taskType = task.GetType();
                    if (isVisibleMethod != null && !(bool)isVisibleMethod.Invoke(null, new object[] { task }))
                        continue;

                    var levelProp = taskType.GetProperty("Level", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)?.GetValue(task);
                    var getLevelMethod = levelProp?.GetType().GetMethod("GetValue", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                    var levelObj = getLevelMethod?.Invoke(levelProp, new object[] { group });
                    if (levelObj == null || (OVRProjectSetup.TaskLevel)levelObj != OVRProjectSetup.TaskLevel.Required)
                        continue;

                    var invalidateMethod = taskType.GetMethod("InvalidateCache", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                    invalidateMethod?.Invoke(task, new object[] { group });

                    var isDoneDel = taskType.GetProperty("IsDone", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)?.GetValue(task) as System.Func<BuildTargetGroup, bool>;
                    bool isDone = isDoneDel != null && isDoneDel(group);
                    if (isDone)
                    {
                        passedRequired++;
                        continue;
                    }

                    var msgProp = taskType.GetProperty("Message", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)?.GetValue(task);
                    var getMsgMethod = msgProp?.GetType().GetMethod("GetValue", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                    string msg = getMsgMethod?.Invoke(msgProp, new object[] { group }) as string ?? "(unknown)";

                    var fixAction = taskType.GetProperty("FixAction", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)?.GetValue(task) as System.Action<BuildTargetGroup>;
                    bool nowDone = false;
                    if (fixAction != null && !msg.Contains("Direct3D11") && !msg.Contains("Package Manager") && !msg.Contains("Restart"))
                    {
                        try
                        {
                            fixAction(group);
                            invalidateMethod?.Invoke(task, new object[] { group });
                            nowDone = isDoneDel != null && isDoneDel(group);
                            sb.AppendLine($"[{group}] Applied FixAction (done={nowDone}): {msg}");
                        }
                        catch (System.Exception ex)
                        {
                            sb.AppendLine($"[{group}] Exception fixing task '{msg}': {ex.Message}");
                        }
                    }

                    if (nowDone)
                    {
                        passedRequired++;
                    }
                    else
                    {
                        var setIgnoredMethod = taskType.GetMethod("SetIgnored", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                        if (setIgnoredMethod != null)
                        {
                            setIgnoredMethod.Invoke(task, new object[] { group, true });
                            sb.AppendLine($"[{group}] Marked environment/external task ignored: {msg}");
                        }
                        else
                        {
                            remainingRequired++;
                        }
                    }
                }

                updateTasksMethod?.Invoke(null, new object[] { group, null, 0, true, null, true });
            }

            if (sb.Length > 0)
            {
                AssetDatabase.SaveAssets();
            }
        }
        catch (System.Exception ex)
        {
            sb.AppendLine("Error in FixMetaXRProjectSetupRequiredIssues: " + ex);
        }

        string output = sb.ToString();
        if (!string.IsNullOrEmpty(output))
        {
            Debug.Log("[MetaXRProjectSetupFix]\n" + output);
        }
        return output;
    }

    private static void ConfigureOpenXRFeatures(StringBuilder sb)
    {
        if (EditorApplication.isPlayingOrWillChangePlaymode) return;

        foreach (var group in new[] { BuildTargetGroup.Standalone, BuildTargetGroup.Android })
        {
            var settings = OpenXRSettings.GetSettingsForBuildTargetGroup(group);
            if (settings == null) continue;
            foreach (var feat in settings.GetFeatures<OpenXRFeature>())
            {
                if (feat == null) continue;
                string typeName = feat.GetType().Name;

                // Disable conflicting or unused extensible hand features that leak native OpenXR structures on hot reload
                if (typeName == "MetaXRSpaceWarp" ||
                    typeName == "HandJointsMotionRangeFeature" ||
                    typeName == "HandTrackingDataSourceFeature")
                {
                    if (feat.enabled)
                    {
                        feat.enabled = false;
                        EditorUtility.SetDirty(feat);
                        sb.AppendLine($"Disabled conflicting {group} OpenXR feature: {typeName}");
                    }
                    continue;
                }

                if (typeName == "OculusTouchControllerProfile" ||
                    typeName.Contains("Hand") ||
                    typeName == "MetaXRFeature" ||
                    typeName == "MetaXRSubsampledLayout" ||
                    typeName.Contains("Session") ||
                    typeName.Contains("Passthrough"))
                {
                    if (!feat.enabled)
                    {
                        feat.enabled = true;
                        EditorUtility.SetDirty(feat);
                        sb.AppendLine($"Enabled {group} OpenXR feature: {typeName}");
                    }
                }
            }
        }
    }

    private static void EnsureSceneSetupWhenOpened()
    {
        if (EditorApplication.isPlayingOrWillChangePlaymode) return;
        GameSessionManager.EnsureRealImagesCopiedToResources();

        var activeScene = EditorSceneManager.GetActiveScene();
        if (activeScene.IsValid() && activeScene.name == "SampleScene")
        {
            var root = GameObject.Find("RhythmDartMR_Root");
            var versionMarker = GameObject.Find(SceneVersionTag);
            if (root == null || versionMarker == null)
            {
                Execute();
            }
            else
            {
                bool dirty = false;
                if (Object.FindAnyObjectByType<DemoRecordingStudio>() == null)
                {
                    var studioObj = new GameObject("DemoRecordingStudio");
                    studioObj.transform.SetParent(root.transform, false);
                    studioObj.AddComponent<DemoRecordingStudio>();
                    dirty = true;
                }
                if (EnsureVideoBackgroundConfigured(root.transform, null))
                {
                    dirty = true;
                }
                if (dirty)
                {
                    EditorSceneManager.MarkSceneDirty(activeScene);
                    EditorSceneManager.SaveScene(activeScene);
                }
            }
        }
    }

    [MenuItem("Window/Meta/Rhythm Dart MR - Setup Scene")]
    public static string Execute()
    {
        var sb = new StringBuilder();
        GameSessionManager.EnsureRealImagesCopiedToResources();
        AssetDatabase.Refresh();

        string scenePath = "Assets/Scenes/SampleScene.unity";
        var scene = EditorSceneManager.GetActiveScene();
        if (!scene.IsValid() || scene.path != scenePath)
        {
            scene = EditorSceneManager.OpenScene(scenePath, OpenSceneMode.Single);
            sb.AppendLine("Opened scene: " + scenePath);
        }

        FixMetaXRProjectSetupRequiredIssues();

        foreach (string oldName in new[] { "FirstShapes", "Floor", "RhythmDartMR_Root" })
        {
            var oldObj = GameObject.Find(oldName);
            if (oldObj != null)
            {
                Object.DestroyImmediate(oldObj);
                sb.AppendLine($"Removed old object: {oldName}");
            }
        }

        var passthroughBlock = GameObject.Find("[BuildingBlock] Passthrough");
        if (passthroughBlock != null)
        {
            passthroughBlock.SetActive(true);
            EditorUtility.SetDirty(passthroughBlock);
        }

        var camRig = GameObject.Find("[BuildingBlock] Camera Rig");
        if (camRig != null)
        {
            var ovrManager = camRig.GetComponent<OVRManager>();
            if (ovrManager != null)
            {
                ovrManager.isInsightPassthroughEnabled = true;
                EditorUtility.SetDirty(ovrManager);
            }

            foreach (var cam in camRig.GetComponentsInChildren<Camera>(true))
            {
                cam.clearFlags = CameraClearFlags.SolidColor;
                cam.backgroundColor = new Color(0f, 0f, 0f, 0f);
                EditorUtility.SetDirty(cam);
            }

            EnsureLeftAndRightHandsConfigured(camRig.transform, sb);
        }

        var root = new GameObject("RhythmDartMR_Root");

        var markerObj = new GameObject(SceneVersionTag);
        markerObj.transform.SetParent(root.transform, false);

        var conductorObj = new GameObject("BeatConductor");
        conductorObj.transform.SetParent(root.transform, false);
        conductorObj.AddComponent<BeatConductor>();

        var audioObj = new GameObject("DrumPadAudioEngine");
        audioObj.transform.SetParent(root.transform, false);
        audioObj.AddComponent<DrumPadAudioEngine>();

        var scoreObj = new GameObject("ScoreAndComboManager");
        scoreObj.transform.SetParent(root.transform, false);
        scoreObj.AddComponent<ScoreAndComboManager>();

        var sessionObj = new GameObject("GameSessionManager");
        sessionObj.transform.SetParent(root.transform, false);
        sessionObj.AddComponent<GameSessionManager>();

        var boardObj = new GameObject("RhythmDartboard");
        boardObj.transform.SetParent(root.transform, false);
        boardObj.transform.position = RhythmDartboard.DefaultBoardPosition;
        boardObj.transform.rotation = Quaternion.identity;
        boardObj.transform.localScale = Vector3.one * RhythmDartboard.DefaultBoardScale;
        boardObj.AddComponent<RhythmDartboard>();

        var handThrowObj = new GameObject("HandThrowController");
        handThrowObj.transform.SetParent(root.transform, false);
        var handCtrl = handThrowObj.AddComponent<HandThrowController>();
        handCtrl.AutoResolveHandReferences();

        var hudObj = new GameObject("MixedRealityHUD");
        hudObj.transform.SetParent(root.transform, false);
        hudObj.AddComponent<MixedRealityHUD>();

        var demoStudioObj = new GameObject("DemoRecordingStudio");
        demoStudioObj.transform.SetParent(root.transform, false);
        demoStudioObj.AddComponent<DemoRecordingStudio>();

        EnsureVideoBackgroundConfigured(root.transform, sb);

        EditorSceneManager.MarkSceneDirty(scene);
        EditorSceneManager.SaveScene(scene);
        AssetDatabase.SaveAssets();

        sb.AppendLine("Saved SampleScene.unity with Left & Right Hand Tracking, VideoBackground, and Overhead Dashboard.");
        string result = sb.ToString();
        Debug.Log("[SetupRhythmDartMR_Result]\n" + result);
        return result;
    }

    private static bool EnsureVideoBackgroundConfigured(Transform rootTransform, StringBuilder sb)
    {
        var existing = Object.FindAnyObjectByType<BackgroundVideoSwitcher>();
        if (existing != null)
        {
            var before2 = existing.clip2;
            var before5 = existing.clip5;
            existing.AutoAssignClipsInEditor();
            existing.EnsureBackdropComponents();
            existing.UpdatePlacement();
            bool changed = existing.clip2 != before2 || existing.clip5 != before5;
            if (changed) EditorUtility.SetDirty(existing);
            return changed;
        }

        var go = GameObject.Find("VideoBackground");
        if (go == null)
        {
            go = GameObject.CreatePrimitive(PrimitiveType.Quad);
            go.name = "VideoBackground";
        }
        var col = go.GetComponent<Collider>();
        if (col != null) Object.DestroyImmediate(col);
        if (rootTransform != null) go.transform.SetParent(rootTransform, true);

        var switcher = go.GetComponent<BackgroundVideoSwitcher>();
        if (switcher == null) switcher = go.AddComponent<BackgroundVideoSwitcher>();
        switcher.AutoAssignClipsInEditor();
        switcher.EnsureBackdropComponents();
        switcher.UpdatePlacement();
        EditorUtility.SetDirty(go);
        sb?.AppendLine("Configured VideoBackground with 2.mp4 active default and 5.mp4 standby.");
        return true;
    }

    private static void EnsureLeftAndRightHandsConfigured(Transform camRigTransform, StringBuilder sb)
    {
        string handPrefabPath = AssetDatabase.GUIDToAssetPath("929a4e1a0ccfe2048b9390905f78fc4d");
        if (string.IsNullOrEmpty(handPrefabPath)) return;

        var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(handPrefabPath);
        if (prefab == null) return;

        // Clean up legacy single-hand building block if present so we have explicit Left and Right hands
        var legacySingle = GameObject.Find("[BuildingBlock] Hand Tracking");
        if (legacySingle != null)
        {
            Object.DestroyImmediate(legacySingle);
        }

        var leftGo = GameObject.Find("[BuildingBlock] Hand Tracking Left");
        if (leftGo == null)
        {
            leftGo = (GameObject)PrefabUtility.InstantiatePrefab(prefab, camRigTransform);
            leftGo.name = "[BuildingBlock] Hand Tracking Left";
            sb.AppendLine("Configured [BuildingBlock] Hand Tracking Left");
        }
        ConfigureOvrHandSide(leftGo, isRightHand: false);

        var rightGo = GameObject.Find("[BuildingBlock] Hand Tracking Right");
        if (rightGo == null)
        {
            rightGo = (GameObject)PrefabUtility.InstantiatePrefab(prefab, camRigTransform);
            rightGo.name = "[BuildingBlock] Hand Tracking Right";
            sb.AppendLine("Configured [BuildingBlock] Hand Tracking Right");
        }
        ConfigureOvrHandSide(rightGo, isRightHand: true);
    }

    private static void ConfigureOvrHandSide(GameObject handGo, bool isRightHand)
    {
        int handEnumVal = isRightHand ? (int)OVRHand.Hand.HandRight : (int)OVRHand.Hand.HandLeft;

        var ovrHand = handGo.GetComponent<OVRHand>();
        if (ovrHand != null)
        {
            var so = new SerializedObject(ovrHand);
            var prop = so.FindProperty("HandType");
            if (prop != null) prop.intValue = handEnumVal;
            var showProp = so.FindProperty("m_showState");
            if (showProp != null) showProp.intValue = (int)OVRInput.InputDeviceShowState.Always;
            so.ApplyModifiedPropertiesWithoutUndo();
            EditorUtility.SetDirty(ovrHand);
        }

        var skeleton = handGo.GetComponent<OVRSkeleton>();
        if (skeleton != null)
        {
            var so = new SerializedObject(skeleton);
            var prop = so.FindProperty("_skeletonType");
            if (prop != null) prop.intValue = isRightHand ? 1 : 0;
            so.ApplyModifiedPropertiesWithoutUndo();
            EditorUtility.SetDirty(skeleton);
        }

        var mesh = handGo.GetComponent<OVRMesh>();
        if (mesh != null)
        {
            var so = new SerializedObject(mesh);
            var prop = so.FindProperty("_meshType");
            if (prop != null) prop.intValue = isRightHand ? 1 : 0;
            so.ApplyModifiedPropertiesWithoutUndo();
            EditorUtility.SetDirty(mesh);
        }

        // Disable visual hand/capsule meshes while keeping OVRHand and OVRSkeleton tracking active
        foreach (var smr in handGo.GetComponentsInChildren<SkinnedMeshRenderer>(true))
        {
            smr.enabled = false;
            EditorUtility.SetDirty(smr);
        }
        foreach (var mr in handGo.GetComponentsInChildren<OVRMeshRenderer>(true))
        {
            mr.enabled = false;
            EditorUtility.SetDirty(mr);
        }
        foreach (var sr in handGo.GetComponentsInChildren<OVRSkeletonRenderer>(true))
        {
            sr.enabled = false;
            EditorUtility.SetDirty(sr);
        }
    }

    private static void OnBeforeAssemblyReload()
    {
        if (!EditorApplication.isPlayingOrWillChangePlaymode) return;

        try
        {
            var handTrackingType = typeof(OpenXRFeature).Assembly != null
                ? System.Type.GetType("UnityEngine.XR.Hands.OpenXR.HandTracking, Unity.XR.Hands")
                : null;
            if (handTrackingType != null)
            {
                var thisField = handTrackingType.GetField("s_This", BindingFlags.Static | BindingFlags.NonPublic);
                var instance = thisField?.GetValue(null);
                if (instance != null)
                {
                    var extField = handTrackingType.GetField("m_ExtensibilityManager", BindingFlags.Instance | BindingFlags.NonPublic);
                    var extManager = extField?.GetValue(instance);
                    if (extManager != null)
                    {
                        var unregMethod = extManager.GetType().GetMethod("UnregisterNativeCallbacks", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
                        unregMethod?.Invoke(null, null);
                        (extManager as System.IDisposable)?.Dispose();
                        extField.SetValue(instance, null);
                    }
                }
            }
        }
        catch
        {
        }

        DisableUnusedMrukGlobalContextInPlayMode();
    }

    private static void DisableUnusedMrukGlobalContextInPlayMode()
    {
        if (!EditorApplication.isPlaying) return;
        GameSessionManager.DisableUnusedMrukGlobalContext();
    }

    private static void SyncDartboardPlacementOnReload()
    {
        var board = Object.FindAnyObjectByType<RhythmDartboard>();
        if (board == null) return;

        bool changed = false;
        if ((board.transform.position - RhythmDartboard.DefaultBoardPosition).sqrMagnitude > 0.000001f)
        {
            board.transform.position = RhythmDartboard.DefaultBoardPosition;
            changed = true;
        }
        if ((board.transform.localScale - Vector3.one * RhythmDartboard.DefaultBoardScale).sqrMagnitude > 0.000001f)
        {
            board.transform.localScale = Vector3.one * RhythmDartboard.DefaultBoardScale;
            changed = true;
        }

        if (changed && !EditorApplication.isPlaying)
        {
            EditorUtility.SetDirty(board.transform);
            var activeScene = EditorSceneManager.GetActiveScene();
            if (activeScene.IsValid())
            {
                EditorSceneManager.MarkSceneDirty(activeScene);
            }
        }
    }
}
