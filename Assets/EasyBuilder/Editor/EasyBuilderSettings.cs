using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEngine;
using UnityEngine.Serialization;

namespace EasyBuilder
{
    /// <summary>
    /// The Easy Builder window's selections. Saved per user in UserSettings/, which Unity's default
    /// .gitignore excludes, so teammates don't overwrite each other's selection.
    /// </summary>
    [FilePath("UserSettings/EasyBuilderSettings.asset", FilePathAttribute.Location.ProjectFolder)]
    internal sealed class EasyBuilderSettings : ScriptableSingleton<EasyBuilderSettings>
    {
        public const string ProfilesProperty = nameof(profiles);
        public const string KeystoreProperty = nameof(keystore);
        public const string ResolveDependenciesProperty = nameof(forceResolveAndroidDependencies);
        public const string OutputFolderProperty = nameof(lastOutputFolder);
        public const string OpenOutputFolderProperty = nameof(openOutputFolderWhenDone);
        public const string VersionProfileProperty = nameof(versionProfile);
        public const string ChangelogHistoryFileProperty = nameof(changelogHistoryFile);
        public const string ChangelogDetailProperty = nameof(changelogDetail);
        public const string ChangelogAITimeoutProperty = nameof(changelogAITimeout);

        [SerializeField] private List<BaseBuildProfileSO> profiles = new();
        [SerializeField] private List<BaseBuildProfileSO> disabledProfiles = new();
        [SerializeField] private KeystoreProfileSO keystore;
        [SerializeField] private bool forceResolveAndroidDependencies = true;
        [SerializeField] private string lastOutputFolder;
        [SerializeField] private bool openOutputFolderWhenDone;
        [SerializeField] private VersionProfileSO versionProfile;
        [SerializeField] private string changelogHistoryFile;
        [SerializeField] private ChangelogDetail changelogDetail = ChangelogDetail.Normal;
        [SerializeField] private string changelogAI;
        [SerializeField] private int changelogAITimeout = ChangelogGenerator.DefaultTimeoutSeconds;
        // Up to v2.1 the notes were kept here; they move into the first version profile created.
        [SerializeField, FormerlySerializedAs("changelog")] private string legacyChangelog;
        [SerializeField] private bool legacySelectionImported;

        public KeystoreProfileSO Keystore => keystore;

        /// <summary>The profile list as shown in the window, including empty slots and switched-off profiles.</summary>
        public List<BaseBuildProfileSO> ProfileList => profiles;

        public bool ContainsProfile(BaseBuildProfileSO profile) => profile != null && profiles.Contains(profile);

        /// <summary>Adds a profile to the window's list (filling an empty slot if there is one). Undoable.</summary>
        public void AddProfile(BaseBuildProfileSO profile)
        {
            if (profile == null || profiles.Contains(profile))
                return;

            Undo.RecordObject(this, "Add Easy Builder Profile");
            var emptySlot = profiles.IndexOf(null);

            if (emptySlot >= 0)
                profiles[emptySlot] = profile;
            else
                profiles.Add(profile);

            SaveSettings();
        }

        public void RemoveProfile(BaseBuildProfileSO profile)
        {
            Undo.RecordObject(this, "Remove Easy Builder Profile");
            profiles.RemoveAll(entry => entry == profile);
            SaveSettings();
        }

        /// <summary>Selects the keystore profile the window signs Android builds with. Undoable.</summary>
        public void SetKeystore(KeystoreProfileSO value)
        {
            Undo.RecordObject(this, "Set Easy Builder Keystore");
            keystore = value;
            SaveSettings();
        }

        public bool IsProfileEnabled(BaseBuildProfileSO profile) => profile != null && !disabledProfiles.Contains(profile);

        public void SetProfileEnabled(BaseBuildProfileSO profile, bool enabled)
        {
            if (profile == null)
                return;

            disabledProfiles.RemoveAll(disabled => disabled == null || disabled == profile);

            if (!enabled)
                disabledProfiles.Add(profile);
        }

        public string LastOutputFolder
        {
            get => lastOutputFolder;
            set => lastOutputFolder = value;
        }

        public bool OpenOutputFolderWhenDone => openOutputFolderWhenDone;

        /// <summary>The id of the AI picked for generating changelogs; see <see cref="ChangelogGenerator.All"/>.</summary>
        public string ChangelogAI
        {
            get => changelogAI;
            set
            {
                changelogAI = value;
                SaveSettings();
            }
        }

        public int ChangelogAITimeout => changelogAITimeout;

        public ChangelogDetail ChangelogDetail => changelogDetail;

        /// <summary>The version that is built: its version, build number and changelog notes.</summary>
        public VersionProfileSO VersionProfile => versionProfile;

        /// <summary>Selects the version to build. Undoable.</summary>
        public void SetVersionProfile(VersionProfileSO value)
        {
            Undo.RecordObject(this, "Set Easy Builder Version");
            versionProfile = value;
            SaveSettings();
        }

        /// <summary>Notes written in the window before v2.2, which had no version profiles. Read once, then cleared.</summary>
        public string TakeLegacyChangelog()
        {
            var notes = legacyChangelog;

            if (!string.IsNullOrEmpty(notes))
            {
                legacyChangelog = null;
                SaveSettings();
            }

            return notes;
        }

        public void SaveSettings() => Save(true);

        public BuildRunner.Request CreateRequest() => new()
        {
            Profiles = profiles.Where(IsProfileEnabled).ToList(),
            Keystore = keystore,
            ForceResolveAndroidDependencies = forceResolveAndroidDependencies,
            Version = versionProfile,
            Changelog = versionProfile != null ? versionProfile.Notes : null,
            ChangelogHistoryFile = changelogHistoryFile,
            ChangelogDetail = changelogDetail,
        };

        // Up to v1.2 the selection was kept in Assets/Plugins/buildProfile.txt.
        private static string LegacySelectionFile => Path.Combine(Application.dataPath, "Plugins", "buildProfile.txt");

        /// <summary>Imports the selection saved by v1.x, once.</summary>
        public void ImportLegacySelection()
        {
            if (legacySelectionImported || !File.Exists(LegacySelectionFile))
                return;

            const string keystorePrefix = "keystore:";

            foreach (var rawLine in File.ReadAllLines(LegacySelectionFile))
            {
                var line = rawLine.Trim();

                if (line.StartsWith(keystorePrefix))
                {
                    if (keystore == null)
                        keystore = AssetDatabase.LoadAssetAtPath<KeystoreProfileSO>(line.Substring(keystorePrefix.Length));
                }
                else if (line.Length > 0)
                {
                    var profile = AssetDatabase.LoadAssetAtPath<BaseBuildProfileSO>(line);

                    if (profile != null && !profiles.Contains(profile))
                        profiles.Add(profile);
                }
            }

            legacySelectionImported = true;
            SaveSettings();

            Debug.Log("[EasyBuilder] Imported your profile selection from Assets/Plugins/buildProfile.txt. " +
                      "Easy Builder now keeps it in UserSettings/EasyBuilderSettings.asset, so you can delete the old file.");
        }
    }
}
