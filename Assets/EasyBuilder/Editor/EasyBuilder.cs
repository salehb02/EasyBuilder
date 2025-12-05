using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEditor;
#if ADDRESSABLE_ACTIVE
using UnityEditor.AddressableAssets.Build;
using UnityEditor.AddressableAssets.Settings;
#endif
using UnityEditor.Build.Reporting;
using UnityEditor.UIElements;
using UnityEngine;
using UnityEngine.UIElements;
using Debug = UnityEngine.Debug;

public class EasyBuilder : EditorWindow
{
    private const string VERSION = "1.2";

    private List<BaseBuildProfileSO> _currentProfiles = new();
    private List<ObjectField> _profilesArray = new();

    private string _profileSaveFolderPath = Path.Combine(Application.dataPath, "Plugins");
    private string _profileSaveFilePath = Path.Combine(Application.dataPath, "Plugins", "buildProfile.txt");

    // Keystore
    private KeystoreProfileSO _keystoreProfile;

    public int callbackOrder => 10;

    [MenuItem("Tools/Easy Builder")]
    public static void OpenBuilder()
    {
        var window = GetWindow<EasyBuilder>();
        window.titleContent = new UnityEngine.GUIContent($"Easy Builder (v{VERSION})");
    }

    public void CreateGUI()
    {
        var root = rootVisualElement;
        root.style.paddingLeft = 5;
        root.style.paddingRight = 5;

        var label = new Label("This is <b>'EASY BUILDER'</b> MF!");
        label.enableRichText = true;
        label.style.unityTextAlign = TextAnchor.MiddleCenter;
        label.style.marginBottom = 20;
        label.style.marginTop = 20;
        label.style.fontSize = 25;

        root.Add(label);

        var version = new TextField("Version");
        version.value = Application.version;
        version.RegisterValueChangedCallback(value =>
        {
            PlayerSettings.bundleVersion = value.newValue;
        });
        root.Add(version);

        var buildNum = new IntegerField("Build Number");
        buildNum.value = PlayerSettings.Android.bundleVersionCode;
        buildNum.RegisterValueChangedCallback(value =>
        {
            PlayerSettings.Android.bundleVersionCode = value.newValue;
        });
        root.Add(buildNum);

        var profilesFoldout = new Foldout();
        profilesFoldout.text = "Profiles";
        root.Add(profilesFoldout);

        // Create a container to hold the ObjectFields for the array
        var objectArrayContainer = new VisualElement();
        objectArrayContainer.style.flexDirection = FlexDirection.Column;
        profilesFoldout.Add(objectArrayContainer);

        _currentProfiles = GetCurrentBuildProfile();
        foreach (var profile in _currentProfiles)
            AddObjectField(objectArrayContainer, profile);

        var addButton = new Button(() => AddObjectField(objectArrayContainer));
        addButton.text = "Add Profile";
        profilesFoldout.Add(addButton);

        var removeBtn = new Button(() => RemoveObjectField(objectArrayContainer));
        removeBtn.text = "Remove Profile";
        profilesFoldout.Add(removeBtn);

        var keyaliasBox = new Foldout();
        keyaliasBox.text = "Keystore";

        var newObjectField = new ObjectField("Profile");
        newObjectField.objectType = typeof(KeystoreProfileSO);
        _keystoreProfile = GetCurrentKeystoreProfile();
        newObjectField.value = _keystoreProfile;
        newObjectField.RegisterValueChangedCallback(evt =>
        {
            _keystoreProfile = evt.newValue as KeystoreProfileSO;
            SaveCurrentProfile();
        });

        keyaliasBox.Add(newObjectField);
        root.Add(keyaliasBox);

        var buildBtn = new Button(Build);
        buildBtn.text = "Build!";
        buildBtn.style.fontSize = 18;
        buildBtn.style.unityFontStyleAndWeight = FontStyle.Bold;
        buildBtn.style.marginTop = 10;
        buildBtn.style.marginBottom = 10;
        buildBtn.style.paddingBottom = 5;
        buildBtn.style.paddingTop = 5;
        root.Add(buildBtn);
    }

    private void AddObjectField(VisualElement parentContainer, BaseBuildProfileSO profile = null)
    {
        if (_profilesArray.Count != 0 && _profilesArray.Last().value == null)
            return;

        var newObjectField = new ObjectField(profile != null ? profile.name : "Empty");
        newObjectField.objectType = typeof(BaseBuildProfileSO);
        newObjectField.value = profile;
        newObjectField.RegisterValueChangedCallback(evt =>
        {
            var index = parentContainer.IndexOf(newObjectField);

            if (index >= 0 && index < _currentProfiles.Count)
            {
                _currentProfiles[index] = evt.newValue as BaseBuildProfileSO;
            }
            else if (index == _currentProfiles.Count)
            {
                _currentProfiles.Add(evt.newValue as BaseBuildProfileSO);
            }

            var targetProfile = _currentProfiles[index];

            newObjectField.label = targetProfile != null ? targetProfile.name : "Empty";
            SaveCurrentProfile();
        });

        _profilesArray.Add(newObjectField);
        parentContainer.Add(newObjectField);
    }

    private void RemoveObjectField(VisualElement parent)
    {
        if (_profilesArray.Count == 0)
            return;

        var lastElement = _profilesArray.Last();

        if (lastElement.value != null)
            _currentProfiles.Remove((BaseBuildProfileSO)lastElement.value);

        _profilesArray.RemoveAt(_profilesArray.Count - 1);
        parent.Remove(lastElement);

        SaveCurrentProfile();
    }

    private List<BaseBuildProfileSO> GetCurrentBuildProfile()
    {
        if (!File.Exists(_profileSaveFilePath))
            return new List<BaseBuildProfileSO>();

        var finalList = new List<BaseBuildProfileSO>();
        var lines = File.ReadAllLines(_profileSaveFilePath);

        foreach (var line in lines)
        {
            if (!line.StartsWith("keystore:"))
                finalList.Add(AssetDatabase.LoadAssetAtPath<BaseBuildProfileSO>(line));
        }

        return finalList;
    }

    private KeystoreProfileSO GetCurrentKeystoreProfile()
    {
        if (!File.Exists(_profileSaveFilePath))
            return null;

        var lines = File.ReadAllLines(_profileSaveFilePath);

        var targetLine = lines.FirstOrDefault(x => x.StartsWith("keystore:"));

        if (targetLine != null)
        {
            var assetPath = targetLine.Split(':')[1];

            if (!string.IsNullOrEmpty(assetPath))
                return AssetDatabase.LoadAssetAtPath<KeystoreProfileSO>(assetPath);
        }

        return null;
    }

    private void SaveCurrentProfile()
    {
        if (_currentProfiles == null)
            return;

        if (!Directory.Exists(_profileSaveFolderPath))
            Directory.CreateDirectory(_profileSaveFolderPath);

        if (!File.Exists(_profileSaveFilePath))
        {
            using (var sw = new StreamWriter(_profileSaveFilePath, true))
            {
                sw.WriteLine("This is a new text file!");
            }
        }

        var allPaths = new List<string>();

        foreach (var item in _currentProfiles)
            if (item != null)
                allPaths.Add(AssetDatabase.GetAssetPath(item));

        if (_keystoreProfile != null)
            allPaths.Add("keystore:" + AssetDatabase.GetAssetPath(_keystoreProfile));

        File.WriteAllLines(_profileSaveFilePath, allPaths);
        AssetDatabase.Refresh();
    }

    private static bool BuildAddressables()
    {
#if ADDRESSABLE_ACTIVE
        AddressableAssetSettings.BuildPlayerContent(out AddressablesPlayerBuildResult result);
        var success = string.IsNullOrEmpty(result.Error);

        if (!success)
            Debug.LogError("Addressables build error encountered: " + result.Error);

        return success;
#else
        return true;
#endif
    }

    public void Build()
    {
        if (EditorApplication.isCompiling)
        {
            Debug.LogWarning("Editor is compiling! Wait...");
            return;
        }

        if (_keystoreProfile != null)
        {
            _keystoreProfile.Apply();
        }
        else
        {
            KeystoreProfileSO.ClearKeystore();
        }

        if (_currentProfiles == null || _currentProfiles.Count == 0)
        {
            Debug.LogWarning("Profile is not selected!");
            return;
        }

        var baseBuildPath = EditorUtility.OpenFolderPanel("Choose build path", "", "");

        if (string.IsNullOrEmpty(baseBuildPath))
        {
            Debug.Log("Path did not selected! Build canceled...");
            return;
        }

        foreach (var profile in _currentProfiles)
        {
            profile.ApplyProfile();
            ForceResolveAutomated();

            var contentBuildSucceeded = BuildAddressables();

            if (contentBuildSucceeded)
            {
                var options = new BuildPlayerOptions();
                options.target = EditorUserBuildSettings.activeBuildTarget;
                options.scenes = GetBuildScenes();

                var developmentBuild = EditorUserBuildSettings.development;
                if (developmentBuild)
                    options.options |= BuildOptions.Development;
                if (EditorUserBuildSettings.allowDebugging && developmentBuild)
                    options.options |= BuildOptions.AllowDebugging;
                if (EditorUserBuildSettings.symlinkSources)
                    options.options |= BuildOptions.SymlinkSources;
                if (EditorUserBuildSettings.connectProfiler && (developmentBuild || options.target == BuildTarget.WSAPlayer))
                    options.options |= BuildOptions.ConnectWithProfiler;
                if (EditorUserBuildSettings.buildWithDeepProfilingSupport && developmentBuild)
                    options.options |= BuildOptions.EnableDeepProfilingSupport;
                if (EditorUserBuildSettings.buildScriptsOnly)
                    options.options |= BuildOptions.BuildScriptsOnly;

                var productName = PlayerSettings.productName;
                var packageName = PlayerSettings.applicationIdentifier;
                var version = PlayerSettings.bundleVersion;
                var buildNum = PlayerSettings.Android.bundleVersionCode;
                var ext = EditorUserBuildSettings.buildAppBundle ? "aab" : "apk";

                var finalName = $"{packageName}_v{version}_{buildNum}_{profile.name}";

                if (!PlayerSettings.Android.buildApkPerCpuArchitecture)
                    finalName += $".{ext}";

                var path = Path.Join(baseBuildPath, finalName);

                if (PlayerSettings.Android.buildApkPerCpuArchitecture)
                    if (!Directory.Exists(path))
                        Directory.CreateDirectory(path);

                options.locationPathName = path;

                var report = BuildPipeline.BuildPlayer(options);
                var summary = report.summary;

                profile.RevertProfile();

                if (summary.result == BuildResult.Succeeded)
                {
                    Debug.Log("Build succeeded!");
                }

                if (summary.result == BuildResult.Failed)
                {
                    Debug.LogError($"Build failed!");
                    return;
                }
            }
        }
    }

    private static string[] GetBuildScenes()
    {
        return EditorBuildSettings.scenes.Where(scene => scene.enabled).Select(scene => scene.path).ToArray();
    }

    public static void ForceResolveAutomated()
    {
#if UNITY_ANDROID
#if GOOGLE_PLAY_ACTIVE
        GooglePlayServices.PlayServicesResolver.MenuForceResolve();
        UnityEngine.Debug.Log("EDM4U: Android Force Resolve triggered successfully.");
#endif
#else
        UnityEngine.Debug.LogWarning("EDM4U: Not an Android build target. Skipping Force Resolve.");
#endif
    }
}