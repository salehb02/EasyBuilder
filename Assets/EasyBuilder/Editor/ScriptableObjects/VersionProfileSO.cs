using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using UnityEditor;
using UnityEngine;

namespace EasyBuilder
{
    /// <summary>
    /// One release: its version, build number and changelog notes. Keep one asset per version, so the notes of every
    /// release stay in the project. The selected one is written to Player Settings and built.
    /// </summary>
    [CreateAssetMenu(menuName = "Easy Builder/New Version Profile")]
    public class VersionProfileSO : ScriptableObject
    {
        public const string VersionProperty = nameof(version);
        public const string BuildNumberProperty = nameof(buildNumber);
        public const string NotesProperty = nameof(notes);
        public const string CommitsFromProperty = nameof(commitsFrom);
        public const string CommitsToProperty = nameof(commitsTo);

        /// <summary>Where new version assets go when there are none yet. Never inside the package, which can be read-only.</summary>
        public const string DefaultFolder = "Assets/Versions";

        [Tooltip("The version players see, e.g. 1.2.0 (PlayerSettings.bundleVersion).")]
        [SerializeField] private string version = "1.0.0";

        [Tooltip("Android version code and iOS build number. Stores need it to go up with every upload.")]
        [SerializeField] private int buildNumber = 1;

        [Tooltip("What changed in this version, one change per line, e.g. \"feat: New shop\" or \"fix(ui): Button overlap\".")]
        [SerializeField, TextArea(4, 20)] private string notes;

        [Tooltip("First commit, tag or branch to generate the notes from (included). Empty: the commits after the newest tag.")]
        [SerializeField] private string commitsFrom;

        [Tooltip("Last commit, tag or branch to generate the notes from (included). Empty: HEAD.")]
        [SerializeField] private string commitsTo;

        public string Version => version;
        public int BuildNumber => buildNumber;
        public string Notes => notes;
        public string CommitsFrom => commitsFrom;
        public string CommitsTo => commitsTo;

        /// <summary>"1.2.0 (build 46)".</summary>
        public string DisplayName => $"{(string.IsNullOrWhiteSpace(version) ? "(no version)" : version)} (build {buildNumber})";

        public void Validate(List<string> errors)
        {
            if (string.IsNullOrWhiteSpace(version))
                errors.Add("The version is empty.");

            if (buildNumber < 1 || buildNumber > BuildRunner.MaxAndroidVersionCode)
                errors.Add($"The build number must be between 1 and {BuildRunner.MaxAndroidVersionCode}.");
        }

        /// <summary>True when Player Settings already hold this version and build number.</summary>
        public bool IsApplied =>
            PlayerSettings.bundleVersion == version.Trim() &&
            PlayerSettings.Android.bundleVersionCode == buildNumber &&
            PlayerSettings.iOS.buildNumber == buildNumber.ToString();

        /// <summary>Writes the version and build number to Player Settings, for both Android and iOS.</summary>
        public void Apply()
        {
            if (IsApplied)
                return; // Don't touch ProjectSettings.asset for nothing.

            PlayerSettings.bundleVersion = version.Trim();
            PlayerSettings.Android.bundleVersionCode = Mathf.Clamp(buildNumber, 1, BuildRunner.MaxAndroidVersionCode);
            PlayerSettings.iOS.buildNumber = buildNumber.ToString();
        }

        public enum Bump
        {
            Patch,
            Minor,
            Major,
        }

        /// <summary>"1.2.3" bumped: Patch 1.2.4, Minor 1.3.0, Major 2.0.0. Text after the numbers (e.g. "-beta") is dropped.</summary>
        public static string BumpVersion(string current, Bump bump)
        {
            var match = Regex.Match(current?.Trim() ?? string.Empty, @"^\d+(\.\d+)*");
            var parts = match.Success ? match.Value.Split('.').Select(part => int.TryParse(part, out var number) ? number : 0).ToList() : new List<int> { 0 };

            while (parts.Count < 3)
                parts.Add(0);

            var index = bump switch
            {
                Bump.Major => 0,
                Bump.Minor => 1,
                _ => 2,
            };

            parts[index]++;

            for (var i = index + 1; i < parts.Count; i++)
                parts[i] = 0;

            return string.Join(".", parts);
        }

        /// <summary>Every version asset in the project, newest version first.</summary>
        public static List<VersionProfileSO> FindAll() =>
            AssetDatabase.FindAssets("t:" + nameof(VersionProfileSO))
                .Select(guid => AssetDatabase.LoadAssetAtPath<VersionProfileSO>(AssetDatabase.GUIDToAssetPath(guid)))
                .Where(profile => profile != null)
                .OrderByDescending(profile => ParseVersion(profile.version))
                .ThenByDescending(profile => profile.buildNumber)
                .ToList();

        private static System.Version ParseVersion(string text)
        {
            var match = Regex.Match(text?.Trim() ?? string.Empty, @"^\d+(\.\d+){0,3}");
            var value = !match.Success ? "0.0" : match.Value.Contains(".") ? match.Value : match.Value + ".0";
            return System.Version.TryParse(value, out var parsed) ? parsed : new System.Version(0, 0);
        }

        /// <summary>
        /// Creates the asset for the next version: <paramref name="previous"/>'s version bumped (or Player Settings' when
        /// there is no previous one), a build number above every known one, and empty notes. It is saved next to the
        /// other versions as "Version &lt;version&gt;.asset".
        /// </summary>
        public static VersionProfileSO CreateNext(VersionProfileSO previous, Bump? bump, string notes = null)
        {
            var all = FindAll();
            var baseVersion = previous != null ? previous.version : PlayerSettings.bundleVersion;
            var highestBuild = all.Select(profile => profile.buildNumber).DefaultIfEmpty(0).Max();

            var profile = CreateInstance<VersionProfileSO>();
            profile.version = bump.HasValue ? BumpVersion(baseVersion, bump.Value) : (string.IsNullOrWhiteSpace(baseVersion) ? "1.0.0" : baseVersion.Trim());
            profile.notes = notes ?? string.Empty;

            // Stores reject a build number that isn't higher than the last upload.
            profile.buildNumber = bump.HasValue || previous != null
                ? Mathf.Max(highestBuild, PlayerSettings.Android.bundleVersionCode) + 1
                : Mathf.Max(1, PlayerSettings.Android.bundleVersionCode);
            profile.buildNumber = Mathf.Min(profile.buildNumber, BuildRunner.MaxAndroidVersionCode);

            var folder = previous != null ? System.IO.Path.GetDirectoryName(AssetDatabase.GetAssetPath(previous))?.Replace('\\', '/')
                : all.Count > 0 ? System.IO.Path.GetDirectoryName(AssetDatabase.GetAssetPath(all[0]))?.Replace('\\', '/')
                : null;

            // A version from a read-only package folder can't have siblings there.
            if (string.IsNullOrEmpty(folder) || !folder.StartsWith("Assets", StringComparison.Ordinal))
                folder = DefaultFolder;

            if (!AssetDatabase.IsValidFolder(folder))
            {
                var parent = System.IO.Path.GetDirectoryName(folder)?.Replace('\\', '/');
                AssetDatabase.CreateFolder(parent, System.IO.Path.GetFileName(folder));
            }

            var path = AssetDatabase.GenerateUniqueAssetPath($"{folder}/Version {BuildRunner.SanitizeFileName(profile.version)}.asset");
            AssetDatabase.CreateAsset(profile, path);
            AssetDatabase.SaveAssets();
            return profile;
        }
    }
}
