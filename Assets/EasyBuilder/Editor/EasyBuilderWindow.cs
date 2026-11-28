using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEditor.Build;
using UnityEditor.Build.Reporting;
using UnityEditor.SceneManagement;
using UnityEditor.UIElements;
using UnityEngine;
using UnityEngine.UIElements;
using static EasyBuilder.EditorUI;

namespace EasyBuilder
{
    public class EasyBuilderWindow : EditorWindow
    {
        private const string Version = "2.1";
        private const int MaxDialogLength = 1500;
        private const long ValidationIntervalMs = 2000;

        private SerializedObject _settings;

        private Label _targetLabel;
        private VisualElement _versionPopupHolder;
        private PopupField<VersionProfileSO> _versionPopup;
        private string _versionKeys;
        private Button _selectVersionButton;
        private HelpBox _noVersionInfo;
        private VersionProfileFields _versionFields;
        private VisualElement _versionSettings;
        private ChangelogAIField _changelogAIField;
        private CollapsibleSection _signingSection;
        private CollapsibleSection _outputSection;
        private Label _noKeystoreInfo;
        private Label _keystoreSummary;
        private Button _editKeystoreButton;
        private KeystorePasswordFields _passwords;
        private VisualElement _resolveDependenciesField;
        private ProfileList _profileList;
        private StatusPanel _statusPanel;
        private Button _buildButton;
        private Button _openOutputButton;
        private bool _buildQueued;

        [MenuItem("Tools/DevDude/Easy Builder")]
        public static void OpenBuilder()
        {
            var window = GetWindow<EasyBuilderWindow>();
            window.titleContent = new GUIContent($"Easy Builder (v{Version})");
            window.minSize = new Vector2(380, 420);
        }

        /// <summary>Opens the window, or focuses it if it's already open.</summary>
        internal static void Open() => OpenBuilder();

        // Version and build number can be changed in Player Settings while the window is open.
        private void OnFocus() => RefreshFromProject();

        private void OnEnable() => Undo.undoRedoPerformed += OnUndoRedo;

        private void OnDisable()
        {
            Undo.undoRedoPerformed -= OnUndoRedo;
        }

        private void OnUndoRedo()
        {
            _settings?.Update();
            EasyBuilderSettings.instance.SaveSettings();
            RefreshFromProject();
        }

        internal static void RefreshOpenWindows()
        {
            foreach (var window in Resources.FindObjectsOfTypeAll<EasyBuilderWindow>())
                window.RefreshFromProject();
        }

        public void CreateGUI()
        {
            titleContent = new GUIContent($"Easy Builder (v{Version})");
            minSize = new Vector2(380, 420);

            var settings = EasyBuilderSettings.instance;
            settings.ImportLegacySelection();

            // ScriptableSingleton instances are created read-only; this window edits it through bindings.
            settings.hideFlags &= ~HideFlags.NotEditable;
            _settings = new SerializedObject(settings);

            var root = rootVisualElement;
            root.style.flexDirection = FlexDirection.Column;

            // The view data key keeps the scroll position across recompiles.
            var content = new ScrollView(ScrollViewMode.Vertical) { viewDataKey = "EasyBuilderWindowScroll" };
            content.style.flexGrow = 1;
            content.style.flexShrink = 1;
            content.contentContainer.style.paddingLeft = 6;
            content.contentContainer.style.paddingRight = 6;
            content.contentContainer.style.paddingBottom = 6;
            root.Add(content);

            content.Add(CreateHeader());
            content.Add(CreateVersionSection());
            content.Add(CreateProfilesSection());
            content.Add(CreateSigningSection());
            content.Add(CreateOutputSection());

            root.Add(CreateFooter());

            // Bound part by part: the version fields are bound to the selected version asset instead.
            _versionSettings.Bind(_settings);
            _signingSection.Bind(_settings);
            _outputSection.Bind(_settings);
            root.TrackSerializedObjectValue(_settings, _ =>
            {
                EasyBuilderSettings.instance.SaveSettings();
                RefreshFromProject();
            });

            // Catch changes the window isn't told about, such as a keystore file appearing or scenes being enabled.
            root.schedule.Execute(() =>
            {
                _profileList.Refresh();
                RefreshValidation();
            }).Every(ValidationIntervalMs);

            RefreshFromProject();
        }

        private VisualElement CreateHeader()
        {
            var header = new VisualElement();
            header.style.flexDirection = FlexDirection.Row;
            header.style.alignItems = Align.Center;
            header.style.marginTop = 6;
            header.style.paddingLeft = 3;

            _targetLabel = new Label();
            _targetLabel.style.flexGrow = 1;
            _targetLabel.style.flexShrink = 1;
            _targetLabel.style.fontSize = 13;
            _targetLabel.style.whiteSpace = WhiteSpace.Normal;
            header.Add(_targetLabel);

            header.Add(new Button(BuildPlayerWindow.ShowBuildPlayerWindow)
            {
                text = "Switch Platform…",
                tooltip = "Open the Build Settings window to change the active platform.",
            });

            return header;
        }

        private VisualElement CreateVersionSection()
        {
            var section = CreateSection("Version", "The release to build: one asset per version, with its build number and notes.");

            // Pick a version, or make the next one.
            var pickRow = new VisualElement();
            pickRow.style.flexDirection = FlexDirection.Row;
            _versionPopupHolder = new VisualElement();
            _versionPopupHolder.style.flexGrow = 1;
            _versionPopupHolder.style.flexShrink = 1;
            pickRow.Add(_versionPopupHolder);

            _selectVersionButton = new Button(() =>
            {
                var profile = EasyBuilderSettings.instance.VersionProfile;

                if (profile == null)
                    return;

                Selection.activeObject = profile;
                EditorGUIUtility.PingObject(profile);
            })
            {
                text = "Select",
                tooltip = "Show the version asset in the Project window.",
            };
            pickRow.Add(_selectVersionButton);

            Button newVersionButton = null;
            newVersionButton = new Button(() => ShowNewVersionMenu(newVersionButton))
            {
                text = "New Version ▾",
                tooltip = "Create the next version: patch, minor or major, with the next build number and empty notes.",
            };
            newVersionButton.style.unityFontStyleAndWeight = FontStyle.Bold;
            pickRow.Add(newVersionButton);
            section.Add(pickRow);

            _noVersionInfo = new HelpBox(string.Empty, HelpBoxMessageType.Info);
            section.Add(_noVersionInfo);

            // The selected version's fields, edited in place.
            _versionFields = new VersionProfileFields();
            _versionFields.style.marginTop = 4;
            _versionFields.Changed += () =>
            {
                RefreshVersion();
                RefreshValidation();
            };
            section.Add(_versionFields);

            // Window settings, the same for every version. Bound to the window's settings, not the version.
            _versionSettings = new VisualElement();

            var options = new Foldout { text = "Options", value = false, viewDataKey = "EasyBuilderChangelogOptions" };
            _versionSettings.Add(options);

            _changelogAIField = new ChangelogAIField();
            _changelogAIField.Changed += () => _versionFields.RefreshAI();
            options.Add(_changelogAIField);

            options.Add(new PropertyField(_settings.FindProperty(EasyBuilderSettings.ChangelogDetailProperty), "Detail Level")
            {
                tooltip = "Which changes the notes list, both when generating and when writing the changelog file. " +
                          "Brief: features, fixes and untagged notes. Normal: also performance and other changes, with scopes. " +
                          "Detailed: everything, including docs, maintenance and indented detail lines. Breaking changes are always listed.",
            });

            var timeoutField = new IntegerField("AI Timeout (s)")
            {
                isDelayed = true,
                bindingPath = EasyBuilderSettings.ChangelogAITimeoutProperty,
                tooltip = $"Stop waiting for the AI after this many seconds ({ChangelogGenerator.MinTimeoutSeconds} to {ChangelogGenerator.MaxTimeoutSeconds}).",
            };
            timeoutField.RegisterValueChangedCallback(evt =>
            {
                var clamped = Mathf.Clamp(evt.newValue, ChangelogGenerator.MinTimeoutSeconds, ChangelogGenerator.MaxTimeoutSeconds);

                if (clamped != evt.newValue)
                    SetSetting(EasyBuilderSettings.ChangelogAITimeoutProperty, property => property.intValue = clamped);
            });
            options.Add(timeoutField);

            var historyField = new TextField("History File")
            {
                isDelayed = true,
                bindingPath = EasyBuilderSettings.ChangelogHistoryFileProperty,
                tooltip = "Optional, e.g. CHANGELOG.md (relative to the project folder). When every profile builds, " +
                          "this version's notes are added at the top. Rebuilding the same version and build number replaces its entry.",
            };
            historyField.RegisterValueChangedCallback(_ => RefreshValidation());
            options.Add(WithSideButtons(historyField, ("Browse…", "Choose or create the changelog history file.", BrowseChangelogHistoryFile)));

            section.Add(_versionSettings);
            return section;
        }

        // Shows the version list and the selected version's fields, and keeps Player Settings in step with it.
        private void RefreshVersion()
        {
            var settings = EasyBuilderSettings.instance;
            var all = VersionProfileSO.FindAll();
            var selected = settings.VersionProfile;

            // Nothing selected (a new machine: the selection is per user) or the asset was deleted: take the newest.
            if (selected == null && all.Count > 0)
            {
                selected = all[0];
                settings.SetVersionProfile(selected);
                _settings.Update();
            }

            // Rebuild the popup only when the list or a name changed, so an open popup isn't closed every time.
            var key = string.Join("|", all.Select(profile => profile.GetInstanceID() + ":" + profile.DisplayName));

            if (_versionPopup == null || key != _versionKeys)
            {
                _versionKeys = key;
                _versionPopupHolder.Clear();
                _versionPopup = null;

                if (all.Count > 0)
                {
                    _versionPopup = new PopupField<VersionProfileSO>("Release", all, all.Contains(selected) ? selected : all[0],
                        profile => profile.DisplayName, profile => profile.DisplayName)
                    {
                        tooltip = "The version to build. Newest first.",
                    };
                    _versionPopup.RegisterValueChangedCallback(evt =>
                    {
                        EasyBuilderSettings.instance.SetVersionProfile(evt.newValue);
                        _settings.Update();
                        RefreshVersion();
                        RefreshValidation();
                    });
                    _versionPopupHolder.Add(_versionPopup);
                }
            }
            else if (selected != null && all.Contains(selected))
            {
                _versionPopup.SetValueWithoutNotify(selected);
            }

            SetVisible(_versionPopupHolder, all.Count > 0);
            SetVisible(_selectVersionButton, selected != null);
            SetVisible(_noVersionInfo, selected == null);
            SetVisible(_versionFields, selected != null);
            _noVersionInfo.text = "No versions yet. Click New Version to start from Player Settings " +
                                  $"({PlayerSettings.bundleVersion}, build {PlayerSettings.Android.bundleVersionCode}).";

            _versionFields.SetProfile(selected);
            _changelogAIField.Refresh();
            _versionFields.RefreshAI();

            // The selected version is the source of truth; Apply does nothing when they already match.
            if (selected != null)
                selected.Apply();
        }

        private void ShowNewVersionMenu(Button button)
        {
            var current = EasyBuilderSettings.instance.VersionProfile;
            var baseVersion = current != null ? current.Version : PlayerSettings.bundleVersion;
            var menu = new GenericMenu();

            if (current == null)
            {
                menu.AddItem(new GUIContent($"From Player Settings  ({PlayerSettings.bundleVersion}, build {PlayerSettings.Android.bundleVersionCode})"),
                    false, () => CreateVersion(null));
                menu.AddSeparator(string.Empty);
            }

            foreach (var bump in new[] { VersionProfileSO.Bump.Patch, VersionProfileSO.Bump.Minor, VersionProfileSO.Bump.Major })
                menu.AddItem(new GUIContent($"{bump}  →  {VersionProfileSO.BumpVersion(baseVersion, bump)}"), false, () => CreateVersion(bump));

            menu.DropDown(button.worldBound);
        }

        private void CreateVersion(VersionProfileSO.Bump? bump)
        {
            var settings = EasyBuilderSettings.instance;

            // Notes written in the window before version profiles existed go into the first one.
            var notes = VersionProfileSO.FindAll().Count == 0 ? settings.TakeLegacyChangelog() : null;

            var profile = VersionProfileSO.CreateNext(settings.VersionProfile, bump, notes);
            settings.SetVersionProfile(profile);
            _settings.Update();

            EditorGUIUtility.PingObject(profile);
            Debug.Log($"[EasyBuilder] Created version {profile.DisplayName} at {AssetDatabase.GetAssetPath(profile)}.");
            RefreshFromProject();
        }

        private VisualElement CreateProfilesSection()
        {
            var section = CreateSection("Profiles", "Built top to bottom. Untick to skip one. Drag profile assets here to add them.");

            _profileList = new ProfileList();
            _profileList.Changed += () =>
            {
                _settings.Update();
                RefreshValidation();
            };
            section.Add(_profileList);

            Button addButton = null;
            addButton = new Button(() => ShowAddProfileMenu(addButton))
            {
                text = "Add Profile ▾",
                tooltip = "Add a profile asset from your project, or create a new one.",
            };
            var buttons = CreateButtonRow();
            buttons.Add(addButton);
            section.Add(buttons);

            return section;
        }

        // Every profile asset not in the list yet, then New Profile…: no empty slot to fill in afterwards.
        private void ShowAddProfileMenu(Button button)
        {
            var settings = EasyBuilderSettings.instance;
            var available = AssetDatabase.FindAssets("t:" + nameof(BaseBuildProfileSO))
                .Select(guid => AssetDatabase.LoadAssetAtPath<BaseBuildProfileSO>(AssetDatabase.GUIDToAssetPath(guid)))
                .Where(profile => profile != null && !settings.ContainsProfile(profile))
                .OrderBy(profile => profile.name)
                .ToList();

            var menu = new GenericMenu();

            foreach (var profile in available)
            {
                menu.AddItem(new GUIContent(profile.name.Replace('/', '∕')), false, () =>
                {
                    settings.AddProfile(profile);
                    _settings.Update();
                    _profileList.Refresh();
                    RefreshValidation();
                });
            }

            if (available.Count == 0)
                menu.AddDisabledItem(new GUIContent("Every profile asset is already in the list"));

            menu.AddSeparator(string.Empty);
            menu.AddItem(new GUIContent("New Profile…"), false,
                () => CreateAssetInto<BaseBuildProfileSO>("New Build Profile", EasyBuilderSettings.ProfilesProperty, addToList: true));
            menu.DropDown(button.worldBound);
        }

        private VisualElement CreateSigningSection()
        {
            _signingSection = new CollapsibleSection("Signing (Android)", "EasyBuilder.Window.SigningOpen");
            var body = _signingSection.Content;

            var keystoreRow = new VisualElement();
            keystoreRow.style.flexDirection = FlexDirection.Row;
            var keystoreField = new PropertyField(_settings.FindProperty(EasyBuilderSettings.KeystoreProperty), "Keystore Profile");
            keystoreField.style.flexGrow = 1;
            keystoreField.style.flexShrink = 1;
            keystoreRow.Add(keystoreField);

            _editKeystoreButton = new Button(() =>
            {
                var keystore = EasyBuilderSettings.instance.Keystore;

                if (keystore == null)
                    return;

                Selection.activeObject = keystore;
                EditorGUIUtility.PingObject(keystore);
            })
            {
                text = "Edit",
                tooltip = "Select the keystore profile to edit its file and alias in the Inspector.",
            };
            keystoreRow.Add(_editKeystoreButton);
            keystoreRow.Add(new Button(() =>
                CreateAssetInto<KeystoreProfileSO>("New Keystore Profile", EasyBuilderSettings.KeystoreProperty, addToList: false))
            {
                text = "New…",
                tooltip = "Create a keystore profile asset and select it.",
            });
            body.Add(keystoreRow);

            _noKeystoreInfo = CreateHint("None selected: the keystore set in Player Settings is used as it is.");
            body.Add(_noKeystoreInfo);

            _keystoreSummary = CreateHint(string.Empty);
            body.Add(_keystoreSummary);

            _passwords = new KeystorePasswordFields();
            _passwords.Changed += RefreshValidation;
            body.Add(_passwords);

            return _signingSection;
        }

        private VisualElement CreateOutputSection()
        {
            _outputSection = new CollapsibleSection("Output", "EasyBuilder.Window.OutputOpen");
            var body = _outputSection.Content;

            var outputField = new TextField("Output Folder")
            {
                isDelayed = true,
                bindingPath = EasyBuilderSettings.OutputFolderProperty,
                tooltip = "Where builds are written. Leave empty to be asked when you build.",
            };
            outputField.textEdition.placeholder = "Ask when building";
            outputField.textEdition.hidePlaceholderOnFocus = true;
            outputField.RegisterValueChangedCallback(_ => RefreshValidation());

            var row = WithSideButtons(outputField, ("Browse…", "Choose the folder builds are written to.", BrowseOutputFolder));
            _openOutputButton = new Button(() =>
            {
                var folder = EasyBuilderSettings.instance.LastOutputFolder;

                if (!string.IsNullOrEmpty(folder) && Directory.Exists(folder))
                    EditorUtility.RevealInFinder(folder);
            })
            {
                text = "Open",
            };
            row.Add(_openOutputButton);
            body.Add(row);

            body.Add(new PropertyField(_settings.FindProperty(EasyBuilderSettings.OpenOutputFolderProperty), "Open Folder When Done")
            {
                tooltip = "Show the build folder in the file browser when the batch finishes and at least one build succeeded.",
            });

            _resolveDependenciesField = new PropertyField(_settings.FindProperty(EasyBuilderSettings.ResolveDependenciesProperty),
                "Resolve Android Dependencies")
            {
                tooltip = "Force-resolve Android dependencies with the External Dependency Manager (EDM4U) before each Android build.",
            };
            body.Add(_resolveDependenciesField);

            return _outputSection;
        }

        private void BrowseChangelogHistoryFile()
        {
            var current = EasyBuilderSettings.instance.CreateRequest().ChangelogHistoryFile;
            var folder = string.IsNullOrWhiteSpace(current) ? Changelog.ProjectFolder : Path.GetDirectoryName(Changelog.ResolvePath(current));
            var path = EditorUtility.SaveFilePanel("Choose changelog history file", folder, "CHANGELOG", "md");

            if (!string.IsNullOrEmpty(path))
                SetSetting(EasyBuilderSettings.ChangelogHistoryFileProperty, property => property.stringValue = Changelog.ToProjectRelative(path));
        }

        private VisualElement CreateFooter()
        {
            var footer = new VisualElement();
            footer.style.paddingLeft = 6;
            footer.style.paddingRight = 6;
            footer.style.paddingTop = 6;
            footer.style.paddingBottom = 6;
            footer.style.borderTopWidth = 1;
            footer.style.borderTopColor = BorderColor;
            footer.style.flexShrink = 0;

            _statusPanel = new StatusPanel("Ready to build", "to fix before building");
            _statusPanel.style.marginBottom = 6;
            footer.Add(_statusPanel);

            _buildButton = new Button(QueueBuild);
            _buildButton.style.fontSize = 15;
            _buildButton.style.unityFontStyleAndWeight = FontStyle.Bold;
            _buildButton.style.height = 32;
            footer.Add(_buildButton);

            return footer;
        }

        private void RefreshFromProject()
        {
            if (_versionFields == null)
                return; // The GUI hasn't been built yet.

            // The selection may have been changed from a profile or keystore inspector.
            _settings.Update();

            var target = BuildRunner.ActiveTarget;
            var supported = BuildRunner.IsSupported(target);
            var keystore = EasyBuilderSettings.instance.Keystore;

            _targetLabel.text = supported ? $"Building for <b>{target}</b>" : $"<b>{target}</b> isn't supported. Switch to Android or iOS.";
            _targetLabel.style.color = supported ? new StyleColor(StyleKeyword.Null) : new StyleColor(WarningColor);

            SetVisible(_signingSection, target == BuildTarget.Android);
            SetVisible(_noKeystoreInfo, keystore == null);
            SetVisible(_keystoreSummary, keystore != null);
            _editKeystoreButton.SetEnabled(keystore != null);
            SetVisible(_resolveDependenciesField, AndroidDependencyResolver.IsAvailable);

            _passwords.SetKeystore(keystore);
            _profileList.Refresh();
            RefreshVersion();
            RefreshValidation();
        }

        // Shows what would stop a build right now, and only enables the button when nothing would.
        private void RefreshValidation()
        {
            if (_buildButton == null)
                return;

            var settings = EasyBuilderSettings.instance;
            var keystore = settings.Keystore;

            // The keystore asset may have been edited in the Inspector.
            var keystoreProblems = new List<string>();

            if (keystore != null)
            {
                var path = string.IsNullOrEmpty(keystore.Path) ? "no file set" : keystore.Path;
                var alias = string.IsNullOrEmpty(keystore.AliasName) ? "no alias set" : keystore.AliasName;
                _keystoreSummary.text = $"File: {path}  ·  Alias: {alias}";
                keystore.Validate(keystoreProblems);
            }

            // What a collapsed section is set to, and whether it needs a look.
            _signingSection.Summary = keystore == null ? "Player Settings keystore"
                : keystoreProblems.Count > 0 ? $"⚠ {keystore.name}: {keystoreProblems[0]}"
                : $"{keystore.name} ({keystore.AliasName})";
            _outputSection.Summary = string.IsNullOrEmpty(settings.LastOutputFolder) ? "Ask when building" : settings.LastOutputFolder;

            var request = settings.CreateRequest();
            request.OutputFolder = settings.LastOutputFolder;

            var errors = new List<string>();
            ValidateRequest(request, errors, checkOutputFolder: !string.IsNullOrEmpty(request.OutputFolder));

            _statusPanel.SetProblems(errors);

            var outputExists = !string.IsNullOrEmpty(request.OutputFolder) && Directory.Exists(request.OutputFolder);
            _openOutputButton.SetEnabled(outputExists);
            _openOutputButton.tooltip = outputExists ? "Show the folder in the file browser."
                : string.IsNullOrEmpty(request.OutputFolder) ? "No output folder set yet."
                : "The folder doesn't exist yet. The first build creates it.";

            // Says exactly what a click builds: "Build v1.2.0 (46) · 2 Profiles · Android".
            var count = request.Profiles.Count;
            var version = request.Version != null ? $"v{request.Version.Version} ({request.Version.BuildNumber}) · " : string.Empty;
            _buildButton.text = count == 0
                ? "Build"
                : $"Build {version}{count} Profile{(count == 1 ? "" : "s")} · {BuildRunner.ActiveTarget}";
            _buildButton.SetEnabled(errors.Count == 0 && !_buildQueued);
            _buildButton.tooltip = errors.Count == 0
                ? "Build every ticked profile, in order. Project settings are restored afterwards."
                : "Fix the problems listed above first.";
        }

        private void BrowseOutputFolder()
        {
            var current = EasyBuilderSettings.instance.LastOutputFolder;
            var folder = EditorUtility.OpenFolderPanel("Choose build folder", current ?? string.Empty, string.Empty);

            if (!string.IsNullOrEmpty(folder))
                SetSetting(EasyBuilderSettings.OutputFolderProperty, property => property.stringValue = folder);
        }

        private void CreateAssetInto<T>(string defaultName, string propertyName, bool addToList) where T : ScriptableObject
        {
            // Suggest the folder the existing profiles or keystores live in.
            var settings = EasyBuilderSettings.instance;
            var existing = addToList
                ? settings.ProfileList.FirstOrDefault(profile => profile != null) as Object
                : settings.Keystore;
            var existingPath = existing != null ? AssetDatabase.GetAssetPath(existing) : null;
            var folder = string.IsNullOrEmpty(existingPath) ? "Assets" : Path.GetDirectoryName(existingPath)?.Replace('\\', '/');

            var path = EditorUtility.SaveFilePanelInProject($"Create {defaultName}", defaultName, "asset",
                "Choose where to save the new asset.", folder);

            if (string.IsNullOrEmpty(path))
                return;

            var asset = CreateInstance<T>();
            AssetDatabase.CreateAsset(asset, path);
            AssetDatabase.SaveAssets();

            SetSetting(propertyName, property =>
            {
                if (!addToList)
                {
                    property.objectReferenceValue = asset;
                    return;
                }

                // Reuse an empty slot at the end of the list if there is one.
                var count = property.arraySize;

                if (count == 0 || property.GetArrayElementAtIndex(count - 1).objectReferenceValue != null)
                    property.arraySize = ++count;

                property.GetArrayElementAtIndex(count - 1).objectReferenceValue = asset;
            });

            Selection.activeObject = asset;
            EditorGUIUtility.PingObject(asset);
        }

        private void SetSetting(string propertyName, System.Action<SerializedProperty> change)
        {
            _settings.Update();
            change(_settings.FindProperty(propertyName));
            _settings.ApplyModifiedProperties();
        }

        // Start the build after the current UI event, outside the button's click handler.
        private void QueueBuild()
        {
            if (_buildQueued)
                return;

            _buildQueued = true;
            _buildButton.SetEnabled(false);
            EditorApplication.delayCall += () =>
            {
                try
                {
                    BuildFromWindow();
                }
                finally
                {
                    _buildQueued = false;
                    RefreshFromProject();
                }
            };
        }

        private void BuildFromWindow()
        {
            var settings = EasyBuilderSettings.instance;
            var request = settings.CreateRequest();
            var errors = new List<string>();

            if (!ValidateRequest(request, errors, checkOutputFolder: false))
            {
                ShowErrors(errors);
                return;
            }

            // Builds read scenes from disk, so unsaved changes would be left out.
            if (!EditorSceneManager.SaveCurrentModifiedScenesIfUserWantsTo())
            {
                Debug.Log("[EasyBuilder] Build canceled.");
                return;
            }

            var outputFolder = settings.LastOutputFolder;

            if (string.IsNullOrEmpty(outputFolder))
            {
                outputFolder = EditorUtility.OpenFolderPanel("Choose build folder", string.Empty, string.Empty);

                if (string.IsNullOrEmpty(outputFolder))
                {
                    Debug.Log("[EasyBuilder] No build folder chosen. Build canceled.");
                    return;
                }

                SetSetting(EasyBuilderSettings.OutputFolderProperty, property => property.stringValue = outputFolder);
            }

            request.OutputFolder = outputFolder;

            if (!ValidateRequest(request, errors, checkOutputFolder: true))
            {
                ShowErrors(errors);
                return;
            }

            var results = BuildRunner.Run(request);
            ShowSummary(results, request.Profiles.Count, outputFolder, request.WrittenChangelogPath, settings.OpenOutputFolderWhenDone);
        }

        // The window always builds a version profile; the command line can also use Player Settings as they are.
        private static bool ValidateRequest(BuildRunner.Request request, List<string> errors, bool checkOutputFolder)
        {
            if (request.Version == null)
                errors.Add("No version selected. Click New Version to create one.");

            return BuildRunner.Validate(request, errors, checkOutputFolder);
        }

        private static void ShowErrors(List<string> errors)
        {
            var message = "Fix these before building:\n\n• " + string.Join("\n• ", errors);
            Debug.LogError("[EasyBuilder] " + message);
            EditorUtility.DisplayDialog("Easy Builder: can't build yet", Truncate(message), "OK");
        }

        private static void ShowSummary(List<BuildRunner.ProfileResult> results, int profileCount, string outputFolder,
            string changelogPath, bool openFolder)
        {
            var message = BuildRunner.Summarize(results, profileCount, outputFolder, changelogPath);
            var allSucceeded = results.Count == profileCount && results.All(result => result.Result == BuildResult.Succeeded);
            var title = allSucceeded ? "Easy Builder: done" : "Easy Builder: build failed";
            var lastBuild = results.LastOrDefault(result => result.Result == BuildResult.Succeeded)?.OutputPath;

            // Nothing was built, so there is nothing new in the folder to show.
            if (openFolder && lastBuild != null)
            {
                RevealBuild(lastBuild, outputFolder);
                EditorUtility.DisplayDialog(title, Truncate(message), "Close");
                return;
            }

            if (EditorUtility.DisplayDialog(title, Truncate(message), "Show in Folder", "Close"))
                RevealBuild(lastBuild, outputFolder);
        }

        // Revealing the last build selects it in the file browser; fall back to the folder itself.
        private static void RevealBuild(string lastBuild, string outputFolder) =>
            EditorUtility.RevealInFinder(string.IsNullOrEmpty(lastBuild) ? outputFolder : lastBuild);

        private static string Truncate(string text) =>
            text.Length <= MaxDialogLength ? text : text.Substring(0, MaxDialogLength) + "…\n\n(See the Console for the full message.)";
    }

    internal sealed class EasyBuilderBuildTargetListener : IActiveBuildTargetChanged
    {
        public int callbackOrder => 0;

        public void OnActiveBuildTargetChanged(BuildTarget previousTarget, BuildTarget newTarget) =>
            EasyBuilderWindow.RefreshOpenWindows();
    }
}
