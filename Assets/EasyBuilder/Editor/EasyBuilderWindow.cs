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
        private TextField _versionField;
        private IntegerField _androidBuildNumberField;
        private TextField _iosBuildNumberField;
        private VisualElement _signingSection;
        private HelpBox _noKeystoreInfo;
        private Label _keystoreSummary;
        private VisualElement _keystoreButtons;
        private KeystorePasswordFields _passwords;
        private VisualElement _resolveDependenciesField;
        private ProfileList _profileList;
        private StatusPanel _statusPanel;
        private Button _buildButton;
        private Button _addSlotButton;
        private Button _openOutputButton;
        private Button _generateChangelogButton;
        private VisualElement _changelogAIFieldHolder;
        private PopupField<ChangelogAI> _changelogAIField;
        private string _changelogAIIds;
        private HelpBox _changelogAIMissing;
        private System.Threading.CancellationTokenSource _generateCancellation;
        private bool _buildQueued;

        private const string GenerateChangelogText = "Generate";

        [MenuItem("Tools/Easy Builder")]
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

            // Closing the window or a script reload stops a running AI tool instead of leaving it behind.
            _generateCancellation?.Cancel();
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
            content.Add(_signingSection = CreateSigningSection());
            content.Add(CreateChangelogSection());
            content.Add(CreateOutputSection());

            root.Add(CreateFooter());

            root.Bind(_settings);
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
            header.style.marginTop = 10;
            header.style.marginBottom = 4;

            var title = new Label("EASY BUILDER");
            title.style.unityTextAlign = TextAnchor.MiddleCenter;
            title.style.unityFontStyleAndWeight = FontStyle.Bold;
            title.style.fontSize = 22;
            header.Add(title);

            var targetRow = new VisualElement();
            targetRow.style.flexDirection = FlexDirection.Row;
            targetRow.style.justifyContent = Justify.Center;
            targetRow.style.alignItems = Align.Center;
            targetRow.style.marginTop = 4;

            _targetLabel = new Label();
            _targetLabel.style.marginRight = 6;
            targetRow.Add(_targetLabel);

            var switchPlatformButton = new Button(BuildPlayerWindow.ShowBuildPlayerWindow)
            {
                text = "Switch Platform…",
                tooltip = "Open the Build Settings window to change the active platform.",
            };
            targetRow.Add(switchPlatformButton);
            header.Add(targetRow);

            return header;
        }

        private VisualElement CreateVersionSection()
        {
            var section = CreateSection("Version", "Applies to every profile. Changes are written to Player Settings.");

            _versionField = new TextField("Version") { isDelayed = true, tooltip = "PlayerSettings.bundleVersion" };
            _versionField.RegisterValueChangedCallback(evt =>
            {
                PlayerSettings.bundleVersion = evt.newValue.Trim();
                RefreshValidation();
            });
            section.Add(_versionField);

            _androidBuildNumberField = new IntegerField("Build Number")
            {
                isDelayed = true,
                tooltip = $"Android version code, 1 to {BuildRunner.MaxAndroidVersionCode}.",
            };
            _androidBuildNumberField.RegisterValueChangedCallback(evt =>
            {
                var buildNumber = Mathf.Clamp(evt.newValue, 1, BuildRunner.MaxAndroidVersionCode);

                if (buildNumber != evt.newValue)
                    _androidBuildNumberField.SetValueWithoutNotify(buildNumber);

                PlayerSettings.Android.bundleVersionCode = buildNumber;
                RefreshValidation();
            });
            section.Add(WithSideButtons(_androidBuildNumberField, ("+1", "Increment the build number.", () => BumpBuildNumber())));

            _iosBuildNumberField = new TextField("Build Number") { isDelayed = true, tooltip = "iOS build number (CFBundleVersion)." };
            _iosBuildNumberField.RegisterValueChangedCallback(evt =>
            {
                PlayerSettings.iOS.buildNumber = evt.newValue.Trim();
                RefreshValidation();
            });
            section.Add(WithSideButtons(_iosBuildNumberField, ("+1", "Increment the build number.", () => BumpBuildNumber())));

            section.Add(CreateButtonRow(("Player Settings…", "Open Project Settings > Player.",
                () => SettingsService.OpenProjectSettings("Project/Player"))));

            return section;
        }

        private VisualElement CreateProfilesSection()
        {
            var section = CreateSection("Profiles",
                "Built in order, top to bottom. Untick a profile to skip it. Drag profile assets here to add them.");

            _profileList = new ProfileList();
            _profileList.Changed += () =>
            {
                _settings.Update();
                RefreshValidation();
            };
            section.Add(_profileList);

            var buttons = CreateButtonRow(
                ("New Profile…", "Create a profile asset and add it to the list.",
                    () => CreateAssetInto<BaseBuildProfileSO>("New Build Profile", EasyBuilderSettings.ProfilesProperty, addToList: true)));
            _addSlotButton = new Button(() => _profileList.AddEmptySlot()) { text = "Add Slot" };
            buttons.Insert(0, _addSlotButton);
            section.Add(buttons);

            return section;
        }

        private VisualElement CreateSigningSection()
        {
            var section = CreateSection("Signing (Android)", null);

            section.Add(new PropertyField(_settings.FindProperty(EasyBuilderSettings.KeystoreProperty), "Keystore Profile"));

            _noKeystoreInfo = new HelpBox("No keystore profile selected: the keystore set in Player Settings is used unchanged.",
                HelpBoxMessageType.Info);
            section.Add(_noKeystoreInfo);

            _keystoreSummary = new Label();
            _keystoreSummary.style.whiteSpace = WhiteSpace.Normal;
            _keystoreSummary.style.paddingLeft = 3;
            _keystoreSummary.style.marginTop = 2;
            _keystoreSummary.style.marginBottom = 4;
            _keystoreSummary.style.fontSize = 10;
            section.Add(_keystoreSummary);

            _passwords = new KeystorePasswordFields();
            _passwords.Changed += RefreshValidation;
            section.Add(_passwords);

            _keystoreButtons = CreateButtonRow(("Edit Keystore Profile", "Select the keystore profile to edit its path and alias.", () =>
            {
                var keystore = EasyBuilderSettings.instance.Keystore;

                if (keystore == null)
                    return;

                Selection.activeObject = keystore;
                EditorGUIUtility.PingObject(keystore);
            }
            ));
            section.Add(_keystoreButtons);

            section.Add(CreateButtonRow(("New Keystore Profile…", "Create a keystore profile asset and select it.",
                () => CreateAssetInto<KeystoreProfileSO>("New Keystore Profile", EasyBuilderSettings.KeystoreProperty, addToList: false))));

            _resolveDependenciesField = new PropertyField(_settings.FindProperty(EasyBuilderSettings.ResolveDependenciesProperty),
                "Force-Resolve Dependencies (EDM4U)")
            {
                tooltip = "Run the External Dependency Manager's Android resolver before each build.",
            };
            section.Add(_resolveDependenciesField);

            return section;
        }

        private VisualElement CreateOutputSection()
        {
            var section = CreateSection("Output", "Leave empty to be asked for a folder when you build.");

            var outputField = new TextField("Output Folder") { isDelayed = true, bindingPath = EasyBuilderSettings.OutputFolderProperty };
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
            section.Add(row);

            section.Add(new PropertyField(_settings.FindProperty(EasyBuilderSettings.OpenOutputFolderProperty), "Open Folder When Done")
            {
                tooltip = "Show the build folder in the file browser when the batch finishes and at least one build succeeded.",
            });

            return section;
        }

        private VisualElement CreateChangelogSection()
        {
            var section = CreateSection("Changelog",
                "Written next to the builds as Changelog_v<version>_<build>.md. Leave the notes empty to skip.");

            // 1. Generate: the AI (see RefreshChangelogAI) and the button, then the commit range.
            var aiRow = new VisualElement();
            aiRow.style.flexDirection = FlexDirection.Row;
            _changelogAIFieldHolder = new VisualElement();
            _changelogAIFieldHolder.style.flexGrow = 1;
            _changelogAIFieldHolder.style.flexShrink = 1;
            aiRow.Add(_changelogAIFieldHolder);

            _generateChangelogButton = new Button(OnGenerateChangelogClicked) { text = GenerateChangelogText };
            _generateChangelogButton.style.unityFontStyleAndWeight = FontStyle.Bold;
            _generateChangelogButton.style.minWidth = 90;
            aiRow.Add(_generateChangelogButton);
            section.Add(aiRow);

            _changelogAIMissing = new HelpBox(string.Empty, HelpBoxMessageType.Info);
            section.Add(_changelogAIMissing);

            section.Add(CreateCommitRangeRow());

            // 2. The notes, which the AI fills in and you can edit.
            var notesField = new TextField { multiline = true, isDelayed = true, bindingPath = EasyBuilderSettings.ChangelogProperty };
            notesField.textEdition.placeholder = "feat: New shop\nfix(ui): Button overlap\n\nOne change per line, or Generate them.";
            notesField.textEdition.hidePlaceholderOnFocus = true;
            notesField.style.minHeight = 80;
            notesField.style.marginTop = 4;
            notesField.style.whiteSpace = WhiteSpace.Normal;
            section.Add(notesField);

            section.Add(new PropertyField(_settings.FindProperty(EasyBuilderSettings.ChangelogDetailProperty), "Detail Level")
            {
                tooltip = "Brief: features, fixes and untagged notes. Normal: also performance and other changes, with scopes. " +
                          "Detailed: everything, including docs, maintenance and indented detail lines. Breaking changes are always listed.",
            });

            // 3. Set-once settings, out of the way.
            var options = new Foldout { text = "Options", value = false, viewDataKey = "EasyBuilderChangelogOptions" };
            section.Add(options);

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

            return section;
        }

        // "Commits  [last tag][▾] → [HEAD][▾]": both ends included; empty ends show what they default to.
        private VisualElement CreateCommitRangeRow()
        {
            var row = new VisualElement();
            row.style.flexDirection = FlexDirection.Row;
            row.style.alignItems = Align.Center;

            var label = new Label("Commits")
            {
                tooltip = "The commits to generate from, both ends included. Type a commit hash, tag or branch, or pick one with ▾.",
            };
            label.AddToClassList(BaseField<string>.labelUssClassName);
            label.style.marginLeft = 3; // A field's own margin, so it lines up with the labels above and below.
            row.Add(label);

            row.Add(CreateRevisionField(EasyBuilderSettings.ChangelogFromProperty, "after last tag",
                "First commit, tag or branch (included). Empty: the commits after the newest tag."));

            var arrow = new Label("→");
            arrow.style.marginLeft = arrow.style.marginRight = 3;
            row.Add(arrow);

            row.Add(CreateRevisionField(EasyBuilderSettings.ChangelogToProperty, "HEAD",
                "Last commit, tag or branch (included). Empty: the current commit (HEAD)."));

            return row;
        }

        // A commit, tag or branch, typed in or picked from the recent tags and commits in one flat menu.
        private VisualElement CreateRevisionField(string propertyName, string placeholder, string tooltip)
        {
            var field = new TextField { isDelayed = true, bindingPath = propertyName, tooltip = tooltip };
            field.textEdition.placeholder = placeholder;
            field.textEdition.hidePlaceholderOnFocus = true;
            field.style.flexGrow = 1;
            field.style.flexShrink = 1;
            field.style.minWidth = 60;
            field.style.marginRight = 0;

            Button pick = null;
            pick = CreateIconButton("icon dropdown", "▾", "Pick a recent tag or commit.", () =>
            {
                var (tags, commits) = ChangelogGenerator.GetRecentRevisions(15);
                var menu = new GenericMenu();

                void Add(string text, string value) =>
                    menu.AddItem(new GUIContent(text.Replace('/', '∕')), field.value == value, // '/' would make a submenu.
                        () => SetSetting(propertyName, property => property.stringValue = value));

                Add($"Default ({placeholder})", string.Empty);

                if (tags.Count > 0)
                {
                    menu.AddSeparator(string.Empty);
                    menu.AddDisabledItem(new GUIContent("Tags"));
                    tags.ForEach(tag => Add(tag, tag));
                }

                if (commits.Count > 0)
                {
                    menu.AddSeparator(string.Empty);
                    menu.AddDisabledItem(new GUIContent("Recent commits"));

                    foreach (var (hash, subject) in commits)
                        Add($"{hash}  {(subject.Length > 50 ? subject.Substring(0, 50) + "…" : subject)}", hash);
                }

                menu.DropDown(pick.worldBound);
            });

            var group = new VisualElement();
            group.style.flexDirection = FlexDirection.Row;
            group.style.flexGrow = 1;
            group.style.flexShrink = 1;
            group.style.flexBasis = 0;
            group.Add(field);
            group.Add(pick);
            return group;
        }

        // AI tools can be installed or removed while the editor is open, so this runs whenever the window gets focus.
        private void RefreshChangelogAI()
        {
            var installed = ChangelogGenerator.All.Where(ai => ai.IsAvailable()).Select(ai => ai.Id).ToList();
            var installedKey = string.Join(",", installed);
            var selected = ChangelogGenerator.Resolve(EasyBuilderSettings.instance.ChangelogAI);

            // Rebuild the popup only when what's installed changed (the labels show it), so an open popup isn't
            // closed every time.
            if (_changelogAIField == null || installedKey != _changelogAIIds)
            {
                _changelogAIIds = installedKey;
                _changelogAIFieldHolder.Clear();

                string Label(ChangelogAI ai) => installed.Contains(ai.Id) ? ai.DisplayName : $"{ai.DisplayName} (not installed)";

                _changelogAIField = new PopupField<ChangelogAI>("AI", ChangelogGenerator.All.ToList(), selected, Label, Label)
                {
                    tooltip = "The AI that writes the notes. Command-line tools use your own login, so sign in to them once in a terminal.",
                };
                _changelogAIField.RegisterValueChangedCallback(evt =>
                {
                    EasyBuilderSettings.instance.ChangelogAI = evt.newValue?.Id;
                    RefreshChangelogAI();
                });
                _changelogAIField.SetEnabled(_generateCancellation == null);
                _changelogAIFieldHolder.Add(_changelogAIField);
            }
            else
            {
                _changelogAIField.SetValueWithoutNotify(selected);
            }

            var isInstalled = installed.Contains(selected.Id);
            SetVisible(_changelogAIMissing, !isInstalled);
            _changelogAIMissing.text = $"{selected.DisplayName} isn't installed. {selected.InstallHint}";

            // While generating, the button is the Cancel button.
            if (_generateCancellation != null)
                return;

            _generateChangelogButton.SetEnabled(isInstalled);
            _generateChangelogButton.tooltip = isInstalled
                ? $"Write the notes with {selected.DisplayName} from the git commits in the From–To range (by default, since the last tag), at the selected detail level."
                : $"{selected.DisplayName} isn't installed. Pick another AI, or install it.";
        }

        private void OnGenerateChangelogClicked()
        {
            if (_generateCancellation != null)
                _generateCancellation.Cancel();
            else
                GenerateChangelog();
        }

        private async void GenerateChangelog()
        {
            var settings = EasyBuilderSettings.instance;
            var current = settings.CreateRequest();
            var ai = ChangelogGenerator.Resolve(settings.ChangelogAI);

            // It may have been uninstalled since the window last checked.
            if (!ai.IsAvailable())
            {
                RefreshChangelogAI();
                return;
            }

            var append = false;

            if (!string.IsNullOrWhiteSpace(current.Changelog))
            {
                var choice = EditorUtility.DisplayDialogComplex("Auto Generate Changelog",
                    "The changelog already has notes. Replace them, or add the generated notes below them?", "Replace", "Cancel", "Add Below");

                if (choice == 1)
                    return;

                append = choice == 2;
            }

            _generateCancellation = new System.Threading.CancellationTokenSource();
            _generateChangelogButton.text = "Cancel";
            _generateChangelogButton.tooltip = $"Stop waiting for {ai.DisplayName}.";

            // "Cancel (12s)", so it's clear the AI is still working.
            var started = System.DateTime.UtcNow;
            var ticker = _generateChangelogButton.schedule.Execute(() =>
                _generateChangelogButton.text = $"Cancel ({(int)(System.DateTime.UtcNow - started).TotalSeconds}s)").Every(1000);
            _changelogAIField?.SetEnabled(false);
            Debug.Log($"[EasyBuilder] Generating the changelog with {ai.DisplayName}...");

            try
            {
                var generated = await ChangelogGenerator.GenerateAsync(ai, current.ChangelogDetail, settings.ChangelogAITimeout,
                    settings.ChangelogFrom, settings.ChangelogTo, _generateCancellation.Token);

                // The window may have been closed while the AI was answering.
                if (_settings == null || _settings.targetObject == null)
                    return;

                var notes = append ? Changelog.Normalize(current.Changelog) + "\n" + generated : generated;
                SetSetting(EasyBuilderSettings.ChangelogProperty, property => property.stringValue = notes);
                Debug.Log($"[EasyBuilder] {ai.DisplayName} wrote the changelog notes. Review them before building.");
            }
            catch (System.OperationCanceledException)
            {
                Debug.Log("[EasyBuilder] Changelog generation canceled.");
            }
            catch (System.Exception e)
            {
                Debug.LogException(e);
                EditorUtility.DisplayDialog("Auto Generate Changelog",
                    $"{ai.DisplayName} couldn't generate the changelog:\n\n" + Truncate(e.Message), "OK");
            }
            finally
            {
                ticker.Pause();
                _generateCancellation.Dispose();
                _generateCancellation = null;

                if (_generateChangelogButton != null)
                {
                    _generateChangelogButton.text = GenerateChangelogText;
                    _changelogAIField?.SetEnabled(true);
                    RefreshChangelogAI();
                }
            }
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
            if (_versionField == null)
                return; // The GUI hasn't been built yet.

            // The selection may have been changed from a profile or keystore inspector.
            _settings.Update();

            var target = BuildRunner.ActiveTarget;
            var supported = BuildRunner.IsSupported(target);
            var keystore = EasyBuilderSettings.instance.Keystore;

            _targetLabel.text = supported ? $"Building for <b>{target}</b>" : $"<b>{target}</b> isn't supported. Switch to Android or iOS.";

            _versionField.SetValueWithoutNotify(PlayerSettings.bundleVersion);
            _androidBuildNumberField.SetValueWithoutNotify(PlayerSettings.Android.bundleVersionCode);
            _iosBuildNumberField.SetValueWithoutNotify(PlayerSettings.iOS.buildNumber);
            SetVisible(_androidBuildNumberField.parent, target != BuildTarget.iOS);
            SetVisible(_iosBuildNumberField.parent, target == BuildTarget.iOS);

            SetVisible(_signingSection, target == BuildTarget.Android);
            SetVisible(_noKeystoreInfo, keystore == null);
            SetVisible(_keystoreSummary, keystore != null);
            SetVisible(_keystoreButtons, keystore != null);
            SetVisible(_resolveDependenciesField, AndroidDependencyResolver.IsAvailable);

            _passwords.SetKeystore(keystore);
            _profileList.Refresh();
            RefreshChangelogAI();
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
            if (keystore != null)
            {
                var path = string.IsNullOrEmpty(keystore.Path) ? "no file set" : keystore.Path;
                var alias = string.IsNullOrEmpty(keystore.AliasName) ? "no alias set" : keystore.AliasName;
                _keystoreSummary.text = $"File: {path}  ·  Alias: {alias}";
            }

            var request = settings.CreateRequest();
            request.OutputFolder = settings.LastOutputFolder;

            var errors = new List<string>();
            BuildRunner.Validate(request, errors, checkOutputFolder: !string.IsNullOrEmpty(request.OutputFolder));

            _statusPanel.SetProblems(errors);

            var hasEmptySlot = _profileList.HasEmptySlot;
            _addSlotButton.SetEnabled(!hasEmptySlot);
            _addSlotButton.tooltip = hasEmptySlot
                ? "There's already an empty slot. Pick a profile in it first."
                : "Add an empty slot to pick an existing profile asset.";

            var outputExists = !string.IsNullOrEmpty(request.OutputFolder) && Directory.Exists(request.OutputFolder);
            _openOutputButton.SetEnabled(outputExists);
            _openOutputButton.tooltip = outputExists ? "Show the folder in the file browser."
                : string.IsNullOrEmpty(request.OutputFolder) ? "No output folder set yet."
                : "The folder doesn't exist yet. The first build creates it.";

            var count = request.Profiles.Count;
            _buildButton.text = count == 0
                ? "Build"
                : $"Build {count} Profile{(count == 1 ? "" : "s")} for {BuildRunner.ActiveTarget}";
            _buildButton.SetEnabled(errors.Count == 0 && !_buildQueued);
            _buildButton.tooltip = errors.Count == 0
                ? "Build every ticked profile, in order. Project settings are restored afterwards."
                : "Fix the problems listed above first.";
        }

        private void BumpBuildNumber()
        {
            BuildRunner.IncrementBuildNumber(BuildRunner.ActiveTarget);
            RefreshFromProject();
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

            if (!BuildRunner.Validate(request, errors, checkOutputFolder: false))
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

            if (!BuildRunner.Validate(request, errors))
            {
                ShowErrors(errors);
                return;
            }

            var results = BuildRunner.Run(request);
            ShowSummary(results, request.Profiles.Count, outputFolder, request.WrittenChangelogPath, settings.OpenOutputFolderWhenDone);
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
