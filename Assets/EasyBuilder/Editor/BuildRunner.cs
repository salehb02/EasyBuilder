using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using UnityEditor;
using UnityEditor.Build;
using UnityEditor.Build.Reporting;
using UnityEngine;
using Debug = UnityEngine.Debug;
#if EASYBUILDER_ADDRESSABLES
using UnityEditor.AddressableAssets;
using UnityEditor.AddressableAssets.Build;
using UnityEditor.AddressableAssets.Settings;
#endif

namespace EasyBuilder
{
    /// <summary>
    /// Builds one player per profile. Used by the Easy Builder window, and by CI through
    /// <see cref="BuildFromCommandLine"/>.
    /// </summary>
    public static class BuildRunner
    {
        /// <summary>The highest version code Google Play accepts.</summary>
        public const int MaxAndroidVersionCode = 2100000000;

        private static readonly BuildTarget[] SupportedTargets = { BuildTarget.Android, BuildTarget.iOS };

        public sealed class Request
        {
            public IReadOnlyList<BaseBuildProfileSO> Profiles = Array.Empty<BaseBuildProfileSO>();

            /// <summary>Android only. When null, the keystore in Player Settings is used unchanged.</summary>
            public KeystoreProfileSO Keystore;

            public string OutputFolder;
            public bool ForceResolveAndroidDependencies = true;

            /// <summary>
            /// The version to build. Its version and build number are written to Player Settings before building and
            /// stay there afterwards. When null, Player Settings are used as they are.
            /// </summary>
            public VersionProfileSO Version;

            /// <summary>
            /// What changed in this version, one change per line, optionally as <c>type(scope): text</c>. When set, the
            /// notes are grouped by type (features, fixes, …) into Changelog_v&lt;version&gt;_&lt;build&gt;.md in the output folder.
            /// </summary>
            public string Changelog;

            /// <summary>
            /// Optional running changelog (e.g. CHANGELOG.md), relative to the project folder or absolute. When set
            /// and every profile built, an entry for this version is added at the top.
            /// </summary>
            public string ChangelogHistoryFile;

            /// <summary>Which kinds of changes are written out. Breaking changes always are.</summary>
            public ChangelogDetail ChangelogDetail = ChangelogDetail.Normal;

            /// <summary>Set by <see cref="Run"/>: the release-notes file it wrote, or null.</summary>
            public string WrittenChangelogPath { get; internal set; }
        }

        public sealed class ProfileResult
        {
            public BaseBuildProfileSO Profile;
            public string ProfileName;
            public BuildResult Result = BuildResult.Failed;
            public string OutputPath;
            public TimeSpan Duration;
            public ulong TotalSize;
            public string Error;
        }

        public static BuildTarget ActiveTarget => EditorUserBuildSettings.activeBuildTarget;

        public static NamedBuildTarget ActiveNamedTarget =>
            NamedBuildTarget.FromBuildTargetGroup(BuildPipeline.GetBuildTargetGroup(ActiveTarget));

        public static bool IsSupported(BuildTarget target) => Array.IndexOf(SupportedTargets, target) >= 0;

        public static bool IsModuleInstalled(BuildTarget target) =>
            BuildPipeline.IsBuildTargetSupported(BuildPipeline.GetBuildTargetGroup(target), target);

        public static string GetBuildNumber(BuildTarget target) => target switch
        {
            BuildTarget.Android => PlayerSettings.Android.bundleVersionCode.ToString(),
            BuildTarget.iOS => PlayerSettings.iOS.buildNumber,
            _ => string.Empty,
        };

        public static string[] GetBuildSettingsScenes() =>
            EditorBuildSettings.scenes.Where(scene => scene.enabled).Select(scene => scene.path).ToArray();

        /// <summary>
        /// Checks everything that can be checked before starting, so a batch doesn't fail halfway through.
        /// </summary>
        public static bool Validate(Request request, List<string> errors, bool checkOutputFolder = true)
        {
            var target = ActiveTarget;

            if (EditorApplication.isCompiling || EditorApplication.isUpdating)
                errors.Add("The editor is compiling or importing. Wait for it to finish.");

            if (EditorApplication.isPlayingOrWillChangePlaymode)
                errors.Add("Exit Play mode before building.");

            if (!IsSupported(target))
                errors.Add($"The active build target is {target}. Easy Builder builds Android and iOS; switch the platform in Build Settings.");
            else if (!IsModuleInstalled(target))
                errors.Add($"{target} Build Support is not installed. Add it through Unity Hub.");

            if (request.Version != null)
            {
                var versionErrors = new List<string>();
                request.Version.Validate(versionErrors);
                errors.AddRange(versionErrors.Select(error => $"Version '{request.Version.name}': {error}"));
            }
            else
            {
                if (string.IsNullOrWhiteSpace(PlayerSettings.bundleVersion))
                    errors.Add("Version is empty.");

                if (target == BuildTarget.Android && PlayerSettings.Android.bundleVersionCode < 1)
                    errors.Add("Build number must be 1 or higher.");

                if (target == BuildTarget.iOS && string.IsNullOrWhiteSpace(PlayerSettings.iOS.buildNumber))
                    errors.Add("Build number is empty.");
            }

            var profiles = request.Profiles?.Where(profile => profile != null).ToList() ?? new List<BaseBuildProfileSO>();

            if (profiles.Count == 0)
                errors.Add("No profiles to build. Add a profile, or switch on one that is turned off.");

            foreach (var duplicate in profiles.GroupBy(profile => profile).Where(group => group.Count() > 1))
                errors.Add($"'{duplicate.Key.name}' is in the profile list more than once, so its builds would overwrite each other.");

            foreach (var profile in profiles.Distinct())
                errors.AddRange(ValidateProfile(profile, target).Select(error => $"{profile.name}: {error}"));

            if (target == BuildTarget.Android)
            {
                if (request.Keystore != null)
                {
                    var keystoreErrors = new List<string>();
                    request.Keystore.Validate(keystoreErrors);
                    errors.AddRange(keystoreErrors.Select(error => $"Keystore '{request.Keystore.name}': {error}"));
                }
                else if (PlayerSettings.Android.useCustomKeystore &&
                         (string.IsNullOrEmpty(PlayerSettings.Android.keystorePass) || string.IsNullOrEmpty(PlayerSettings.Android.keyaliasPass)))
                {
                    errors.Add("Player Settings uses a custom keystore, but its passwords haven't been entered in this editor session. " +
                               "Enter them in Player Settings > Publishing Settings, or select a keystore profile.");
                }
            }

            EasyBuilder.Changelog.Validate(request.ChangelogHistoryFile, errors);

            if (checkOutputFolder)
            {
                if (string.IsNullOrWhiteSpace(request.OutputFolder))
                    errors.Add("No output folder was given.");
                else if (IsInsideAssetsFolder(request.OutputFolder))
                    errors.Add("The output folder is inside Assets/, so Unity would import the build output. Pick a folder outside Assets.");
            }

            return errors.Count == 0;
        }

        /// <summary>The problems with one profile for the given target.</summary>
        public static List<string> ValidateProfile(BaseBuildProfileSO profile, BuildTarget target)
        {
            var errors = new List<string>();
            profile.Validate(target, errors);

            if ((profile.GetSceneOverride() ?? GetBuildSettingsScenes()).Length == 0)
                errors.Add("There are no scenes to build. Enable scenes in Build Settings or fill in the profile's scene list.");

            return errors;
        }

        /// <summary>
        /// Builds every profile in order and stops at the first one that fails or is canceled.
        /// Project settings are restored afterwards. Call <see cref="Validate"/> first.
        /// </summary>
        public static List<ProfileResult> Run(Request request)
        {
            var target = ActiveTarget;
            var profiles = request.Profiles.Where(profile => profile != null).ToList();
            var results = new List<ProfileResult>();

            Directory.CreateDirectory(request.OutputFolder);

            // Before the snapshot, so the version stays in Player Settings when the snapshot is restored.
            request.Version?.Apply();

            var batchSnapshot = BuildSettingsSnapshot.Capture(ActiveNamedTarget);
            var holder = BatchAssetHolder.Create(profiles.Cast<UnityEngine.Object>().Append(request.Keystore));

#if EASYBUILDER_ADDRESSABLES
            var addressables = AddressableAssetSettingsDefaultObject.Settings;
            var playerBuildOption = addressables != null ? addressables.BuildAddressablesWithPlayerBuild : default;

            // Content is built explicitly for each profile below; don't let BuildPlayer build it a second time.
            if (addressables != null)
                addressables.BuildAddressablesWithPlayerBuild = AddressableAssetSettings.PlayerBuildOption.DoNotBuildWithPlayer;
#endif

            try
            {
                if (target == BuildTarget.Android && request.Keystore != null)
                    request.Keystore.Apply();

                foreach (var profile in profiles)
                {
                    var result = BuildProfile(profile, target, request);
                    results.Add(result);

                    if (result.Result != BuildResult.Succeeded)
                        break;
                }
            }
            finally
            {
#if EASYBUILDER_ADDRESSABLES
                if (addressables != null)
                {
                    addressables.BuildAddressablesWithPlayerBuild = playerBuildOption;
                    EditorUtility.SetDirty(addressables);
                }
#endif
                batchSnapshot.Restore();
                AssetDatabase.SaveAssets();
                UnityEngine.Object.DestroyImmediate(holder);
            }

            var allSucceeded = results.Count == profiles.Count && results.All(result => result.Result == BuildResult.Succeeded);

            WriteChangelog(request, target, results, allSucceeded);

            var summary = Summarize(results, profiles.Count, request.OutputFolder, request.WrittenChangelogPath);

            if (allSucceeded)
                Debug.Log(summary);
            else
                Debug.LogError(summary);

            return results;
        }

        // Runs after the batch snapshot is restored, so version, build number and product name are the project's.
        private static void WriteChangelog(Request request, BuildTarget target, List<ProfileResult> results, bool allSucceeded)
        {
            request.WrittenChangelogPath = null;
            var notes = EasyBuilder.Changelog.Normalize(request.Changelog);

            if (notes.Length == 0 || !results.Any(result => result.Result == BuildResult.Succeeded))
                return;

            var formatted = EasyBuilder.Changelog.Format(notes, request.ChangelogDetail);

            if (formatted.Length == 0)
            {
                Debug.LogWarning($"[EasyBuilder] No changelog was written: at the {request.ChangelogDetail} detail level, none of the notes are listed.");
                return;
            }

            try
            {
                request.WrittenChangelogPath = EasyBuilder.Changelog.WriteReleaseNotes(request.OutputFolder, formatted, target);
                Debug.Log($"[EasyBuilder] Wrote the changelog to {request.WrittenChangelogPath}");
            }
            catch (Exception e)
            {
                Debug.LogError($"[EasyBuilder] Couldn't write the changelog file: {e.Message}");
            }

            if (string.IsNullOrWhiteSpace(request.ChangelogHistoryFile))
                return;

            if (!allSucceeded)
            {
                Debug.LogWarning("[EasyBuilder] Not every profile built, so the changelog history file was left unchanged.");
                return;
            }

            try
            {
                var historyPath = EasyBuilder.Changelog.UpdateHistory(request.ChangelogHistoryFile, notes, request.ChangelogDetail, target);
                Debug.Log($"[EasyBuilder] Added this version to the changelog history in {historyPath}");
            }
            catch (Exception e)
            {
                Debug.LogError($"[EasyBuilder] Couldn't update the changelog history file: {e.Message}");
            }
        }

        private static ProfileResult BuildProfile(BaseBuildProfileSO profile, BuildTarget target, Request request)
        {
            var result = new ProfileResult { Profile = profile, ProfileName = profile.name };
            var stopwatch = Stopwatch.StartNew();

            try
            {
                profile.ApplyProfile();

                if (target == BuildTarget.Android && request.ForceResolveAndroidDependencies &&
                    !AndroidDependencyResolver.ForceResolve(out var resolveError))
                {
                    result.Error = resolveError;
                    return result;
                }

                if (!BuildAddressables(out var addressablesError))
                {
                    result.Error = addressablesError;
                    return result;
                }

                var options = CreateBuildOptions(target, profile, request.OutputFolder);
                profile.ConfigureBuildOptions(ref options);
                result.OutputPath = options.locationPathName;

                Debug.Log($"[EasyBuilder] Building '{profile.name}' to {options.locationPathName}");

                var report = BuildPipeline.BuildPlayer(options);
                result.Result = report.summary.result;

                // summary.totalSize is the size of the build's content before packaging, not of the APK, AAB or
                // project that was written, so measure the output on disk instead.
                result.TotalSize = GetSizeOnDisk(result.OutputPath) ?? report.summary.totalSize;

                if (result.Result == BuildResult.Cancelled)
                    result.Error = "Canceled.";
                else if (result.Result != BuildResult.Succeeded)
                    result.Error = GetFirstErrors(report);
            }
            catch (Exception e)
            {
                result.Result = BuildResult.Failed;
                result.Error = e.Message;
                Debug.LogException(e);
            }
            finally
            {
                try
                {
                    profile.RevertProfile();
                }
                catch (Exception e)
                {
                    Debug.LogException(e);
                }

                result.Duration = stopwatch.Elapsed;
            }

            return result;
        }

        private static BuildPlayerOptions CreateBuildOptions(BuildTarget target, BaseBuildProfileSO profile, string outputFolder)
        {
            var options = new BuildPlayerOptions
            {
                target = target,
                targetGroup = BuildPipeline.GetBuildTargetGroup(target),
                scenes = GetBuildSettingsScenes(),
                locationPathName = GetOutputPath(target, profile, outputFolder),
            };

            // Mirror the options of the Build Settings window.
            var development = EditorUserBuildSettings.development;

            if (development)
                options.options |= BuildOptions.Development;
            if (development && EditorUserBuildSettings.allowDebugging)
                options.options |= BuildOptions.AllowDebugging;
            if (EditorUserBuildSettings.symlinkSources)
                options.options |= BuildOptions.SymlinkSources;
            if (development && EditorUserBuildSettings.connectProfiler)
                options.options |= BuildOptions.ConnectWithProfiler;
            if (development && EditorUserBuildSettings.buildWithDeepProfilingSupport)
                options.options |= BuildOptions.EnableDeepProfilingSupport;
            if (EditorUserBuildSettings.buildScriptsOnly)
                options.options |= BuildOptions.BuildScriptsOnly;

            return options;
        }

        private static string GetOutputPath(BuildTarget target, BaseBuildProfileSO profile, string outputFolder)
        {
            var namedTarget = NamedBuildTarget.FromBuildTargetGroup(BuildPipeline.GetBuildTargetGroup(target));
            var fileName = SanitizeFileName(
                $"{PlayerSettings.GetApplicationIdentifier(namedTarget)}_v{PlayerSettings.bundleVersion}_{GetBuildNumber(target)}_{profile.name}");
            var path = Path.Combine(outputFolder, fileName);

            if (target != BuildTarget.Android)
                return path; // iOS writes an Xcode project folder.

            if (EditorUserBuildSettings.exportAsGoogleAndroidProject)
                return path; // A Gradle project folder.

            if (PlayerSettings.Android.buildApkPerCpuArchitecture && !EditorUserBuildSettings.buildAppBundle)
            {
                // One APK per CPU architecture, written into this folder.
                Directory.CreateDirectory(path);
                return path;
            }

            return path + (EditorUserBuildSettings.buildAppBundle ? ".aab" : ".apk");
        }

        private static bool BuildAddressables(out string error)
        {
            error = null;

#if EASYBUILDER_ADDRESSABLES
            if (AddressableAssetSettingsDefaultObject.Settings == null)
                return true; // The package is installed but Addressables aren't set up in this project.

            Debug.Log("[EasyBuilder] Building Addressables content...");
            AddressableAssetSettings.BuildPlayerContent(out AddressablesPlayerBuildResult result);

            if (!string.IsNullOrEmpty(result.Error))
            {
                error = "Addressables content build failed: " + result.Error;
                return false;
            }
#endif

            return true;
        }

        public static void IncrementBuildNumber(BuildTarget target)
        {
            switch (target)
            {
                case BuildTarget.Android:
                    if (PlayerSettings.Android.bundleVersionCode >= MaxAndroidVersionCode)
                    {
                        Debug.LogWarning("[EasyBuilder] The build number is already at Google Play's maximum; not incrementing it.");
                        return;
                    }

                    PlayerSettings.Android.bundleVersionCode++;
                    break;

                case BuildTarget.iOS:
                    if (!int.TryParse(PlayerSettings.iOS.buildNumber, out var buildNumber))
                    {
                        Debug.LogWarning($"[EasyBuilder] Can't increment the iOS build number '{PlayerSettings.iOS.buildNumber}' because it isn't a whole number.");
                        return;
                    }

                    PlayerSettings.iOS.buildNumber = (buildNumber + 1).ToString();
                    break;

                default:
                    return;
            }

            AssetDatabase.SaveAssets();
            Debug.Log($"[EasyBuilder] Build number is now {GetBuildNumber(target)}.");
        }

        public static string Summarize(IReadOnlyList<ProfileResult> results, int profileCount, string outputFolder,
            string changelogPath = null)
        {
            var succeeded = results.Count(result => result.Result == BuildResult.Succeeded);
            var text = new StringBuilder();
            text.AppendLine($"Easy Builder: {succeeded} of {profileCount} profile build(s) succeeded. Output folder: {outputFolder}");

            foreach (var result in results)
            {
                var name = result.ProfileName;

                if (result.Result == BuildResult.Succeeded)
                    text.AppendLine($"  ✓ {name} ({result.Duration:hh\\:mm\\:ss}, {EditorUtility.FormatBytes((long)result.TotalSize)}): {result.OutputPath}");
                else
                    text.AppendLine($"  ✗ {name}: {result.Result}{(string.IsNullOrEmpty(result.Error) ? "" : ". " + result.Error)}");
            }

            var skipped = profileCount - results.Count;

            if (skipped > 0)
                text.AppendLine($"  {skipped} remaining profile(s) were not built.");

            if (!string.IsNullOrEmpty(changelogPath))
                text.AppendLine($"Changelog: {changelogPath}");

            return text.ToString();
        }

        /// <summary>
        /// Entry point for CI, for example:
        /// <code>
        /// Unity -batchmode -quit -projectPath . -buildTarget Android
        ///       -executeMethod EasyBuilder.BuildRunner.BuildFromCommandLine
        ///       -ebProfiles "Assets/Profiles/Store.asset;Assets/Profiles/Demo.asset" -ebOutput Builds
        /// </code>
        /// Arguments:
        /// <list type="bullet">
        /// <item><c>-ebOutput &lt;folder&gt;</c> (required) where builds are written.</item>
        /// <item><c>-ebProfiles "&lt;a&gt;;&lt;b&gt;"</c> profile asset paths or GUIDs. Defaults to the window's selection.</item>
        /// <item><c>-ebKeystore &lt;asset path or GUID&gt;</c> keystore profile. Defaults to the window's selection.</item>
        /// <item><c>-ebVersionProfile &lt;asset path or GUID&gt;</c> the version to build: its version, build number and
        /// changelog notes. Defaults to the window's selection.</item>
        /// <item><c>-ebVersion &lt;version&gt;</c> and <c>-ebBuildNumber &lt;number&gt;</c> set them before building, without a
        /// version profile.</item>
        /// <item><c>-ebSkipDependencyResolve</c> skips the EDM4U Android dependency resolve.</item>
        /// <item><c>-ebChangelog "&lt;text&gt;"</c> or <c>-ebChangelogFile &lt;path&gt;</c> the changes in this version,
        /// written to a changelog file next to the builds. Defaults to the version profile's notes.</item>
        /// <item><c>-ebChangelogHistory &lt;path&gt;</c> a running changelog (e.g. CHANGELOG.md) to add this version to.
        /// Defaults to the window's setting.</item>
        /// <item><c>-ebChangelogDetail Brief|Normal|Detailed</c> which changes are written. Defaults to the window's setting.</item>
        /// </list>
        /// Keystore passwords are read from the environment variables named in the keystore profile.
        /// Exits with code 0 when every profile built and 1 otherwise.
        /// </summary>
        public static void BuildFromCommandLine()
        {
            var success = false;

            try
            {
                success = BuildFromArguments(Environment.GetCommandLineArgs());
            }
            catch (Exception e)
            {
                Debug.LogException(e);
            }

            if (Application.isBatchMode)
                EditorApplication.Exit(success ? 0 : 1);
        }

        private static bool BuildFromArguments(string[] args)
        {
            var errors = new List<string>();
            var saved = EasyBuilderSettings.instance.CreateRequest();

            var request = new Request
            {
                Profiles = saved.Profiles,
                Keystore = saved.Keystore,
                OutputFolder = GetArgument(args, "-ebOutput"),
                ForceResolveAndroidDependencies = !args.Contains("-ebSkipDependencyResolve"),
                Version = saved.Version,
                Changelog = saved.Changelog,
                ChangelogHistoryFile = saved.ChangelogHistoryFile,
                ChangelogDetail = saved.ChangelogDetail,
            };

            var versionProfileArgument = GetArgument(args, "-ebVersionProfile");

            if (versionProfileArgument != null)
            {
                request.Version = LoadAsset<VersionProfileSO>(versionProfileArgument);
                request.Changelog = request.Version != null ? request.Version.Notes : null;

                if (request.Version == null)
                    errors.Add($"Version profile not found: {versionProfileArgument}");
            }

            var changelogText = GetArgument(args, "-ebChangelog");
            var changelogFile = GetArgument(args, "-ebChangelogFile");

            if (changelogFile != null)
            {
                var changelogPath = EasyBuilder.Changelog.ResolvePath(changelogFile);

                if (File.Exists(changelogPath))
                    request.Changelog = File.ReadAllText(changelogPath);
                else
                    errors.Add($"Changelog file not found: {changelogFile}");
            }
            else if (changelogText != null)
            {
                // A command line can't easily hold line breaks, so "\n" in the text starts a new line.
                request.Changelog = changelogText.Replace("\\n", "\n");
            }

            var historyArgument = GetArgument(args, "-ebChangelogHistory");

            if (historyArgument != null)
                request.ChangelogHistoryFile = historyArgument;

            var detailArgument = GetArgument(args, "-ebChangelogDetail");

            if (detailArgument != null)
            {
                if (EasyBuilder.Changelog.TryParseDetail(detailArgument, out var detail))
                    request.ChangelogDetail = detail;
                else
                    errors.Add($"-ebChangelogDetail must be Brief, Normal or Detailed, got '{detailArgument}'.");
            }

            var profileArgument = GetArgument(args, "-ebProfiles");

            if (profileArgument != null)
            {
                var profiles = new List<BaseBuildProfileSO>();

                foreach (var entry in profileArgument.Split(new[] { ';' }, StringSplitOptions.RemoveEmptyEntries))
                {
                    var profile = LoadAsset<BaseBuildProfileSO>(entry.Trim());

                    if (profile != null)
                        profiles.Add(profile);
                    else
                        errors.Add($"Build profile not found: {entry}");
                }

                request.Profiles = profiles;
            }

            var keystoreArgument = GetArgument(args, "-ebKeystore");

            if (keystoreArgument != null)
            {
                request.Keystore = LoadAsset<KeystoreProfileSO>(keystoreArgument);

                if (request.Keystore == null)
                    errors.Add($"Keystore profile not found: {keystoreArgument}");
            }

            if (!string.IsNullOrEmpty(request.OutputFolder))
                request.OutputFolder = Path.GetFullPath(request.OutputFolder);

            var version = GetArgument(args, "-ebVersion");
            var buildNumber = GetArgument(args, "-ebBuildNumber");

            // A version profile would overwrite them when the build starts.
            if ((version != null || buildNumber != null) && request.Version != null)
            {
                if (versionProfileArgument != null)
                    errors.Add("Pass either -ebVersionProfile or -ebVersion/-ebBuildNumber, not both.");
                else
                    request.Version = null; // Only the window's selection; the arguments win.
            }

            if (version != null)
                PlayerSettings.bundleVersion = version;

            if (buildNumber != null)
            {
                if (ActiveTarget == BuildTarget.iOS)
                    PlayerSettings.iOS.buildNumber = buildNumber;
                else if (int.TryParse(buildNumber, out var versionCode))
                    PlayerSettings.Android.bundleVersionCode = versionCode;
                else
                    errors.Add($"-ebBuildNumber must be a whole number for Android, got '{buildNumber}'.");
            }

            // Validate also fails when argument errors were already collected, so all problems are reported together.
            if (!Validate(request, errors))
            {
                foreach (var error in errors)
                    Debug.LogError("[EasyBuilder] " + error);

                return false;
            }

            var results = Run(request);
            return results.Count == request.Profiles.Count && results.All(result => result.Result == BuildResult.Succeeded);
        }

        private static string GetArgument(string[] args, string name)
        {
            var index = Array.IndexOf(args, name);
            return index >= 0 && index + 1 < args.Length ? args[index + 1] : null;
        }

        private static T LoadAsset<T>(string pathOrGuid) where T : UnityEngine.Object
        {
            var path = AssetDatabase.GUIDToAssetPath(pathOrGuid);

            if (string.IsNullOrEmpty(path))
                path = pathOrGuid;

            return AssetDatabase.LoadAssetAtPath<T>(path);
        }

        private static string GetFirstErrors(BuildReport report)
        {
            const int maxLength = 300;

            var messages = report.steps
                .SelectMany(step => step.messages)
                .Where(message => message.type == LogType.Error || message.type == LogType.Exception)
                .Select(message => message.content?.Trim().Split('\n')[0].Trim())
                .Where(content => !string.IsNullOrEmpty(content))
                .Select(content => content.Length > maxLength ? content.Substring(0, maxLength) + "…" : content)
                .Distinct()
                .Take(3)
                .ToList();

            return messages.Count > 0 ? string.Join(" | ", messages) : "See the Console for details.";
        }

        private static bool IsInsideAssetsFolder(string folder)
        {
            var full = Path.GetFullPath(folder).Replace('\\', '/').TrimEnd('/');
            var assets = Path.GetFullPath(Application.dataPath).Replace('\\', '/').TrimEnd('/');
            return full.Equals(assets, StringComparison.OrdinalIgnoreCase) ||
                   full.StartsWith(assets + "/", StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>The size of a build file, or of every file in a build folder; null if it doesn't exist.</summary>
        private static ulong? GetSizeOnDisk(string path)
        {
            if (string.IsNullOrEmpty(path))
                return null;

            try
            {
                if (File.Exists(path))
                    return (ulong)new FileInfo(path).Length;

                if (Directory.Exists(path))
                    return (ulong)new DirectoryInfo(path).EnumerateFiles("*", SearchOption.AllDirectories).Sum(file => file.Length);
            }
            catch (Exception e) when (e is IOException || e is UnauthorizedAccessException)
            {
                Debug.LogWarning($"[EasyBuilder] Couldn't measure the size of {path}: {e.Message}");
            }

            return null;
        }

        internal static string SanitizeFileName(string name)
        {
            foreach (var invalid in Path.GetInvalidFileNameChars())
                name = name.Replace(invalid, '_');

            return name;
        }
    }
}
