using System;
using System.Linq;
using System.Threading;
using UnityEditor;
using UnityEditor.UIElements;
using UnityEngine;
using UnityEngine.UIElements;
using static EasyBuilder.EditorUI;

namespace EasyBuilder
{
    /// <summary>
    /// Edits one <see cref="VersionProfileSO"/>: version, build number, and the changelog notes with AI generation from
    /// a commit range. Used by the Easy Builder window and by the version asset's Inspector.
    /// </summary>
    internal sealed class VersionProfileFields : VisualElement
    {
        private const string GenerateText = "Generate";

        private readonly Button _generateButton;
        private readonly HelpBox _cantGenerateInfo;

        private VersionProfileSO _profile;
        private SerializedObject _serialized;
        private VisualElement _tracker;
        private CancellationTokenSource _cancellation;

        /// <summary>Raised when the profile's values change, from here, the Inspector or Undo.</summary>
        public event Action Changed;

        public VersionProfileSO Profile => _profile;

        public bool IsGenerating => _cancellation != null;

        public VersionProfileFields()
        {
            Add(new TextField("Version") { isDelayed = true, bindingPath = VersionProfileSO.VersionProperty, tooltip = "e.g. 1.2.0 (PlayerSettings.bundleVersion)." });

            var buildNumberField = new IntegerField("Build Number")
            {
                isDelayed = true,
                bindingPath = VersionProfileSO.BuildNumberProperty,
                tooltip = $"Android version code and iOS build number, 1 to {BuildRunner.MaxAndroidVersionCode}. Raise it for every store upload.",
            };
            buildNumberField.RegisterValueChangedCallback(evt =>
            {
                var clamped = Mathf.Clamp(evt.newValue, 1, BuildRunner.MaxAndroidVersionCode);

                if (clamped != evt.newValue)
                    Set(VersionProfileSO.BuildNumberProperty, property => property.intValue = clamped);
            });
            Add(WithSideButtons(buildNumberField, ("+1", "Increment the build number.", () =>
                Set(VersionProfileSO.BuildNumberProperty, property =>
                    property.intValue = Mathf.Min(property.intValue + 1, BuildRunner.MaxAndroidVersionCode)))));

            // Changelog: the commit range with Generate (the AI is picked under the window's Options), then the notes.
            _generateButton = new Button(OnGenerateClicked) { text = GenerateText };
            _generateButton.style.unityFontStyleAndWeight = FontStyle.Bold;
            _generateButton.style.minWidth = 90;
            _generateButton.style.marginLeft = 4;

            var commitRow = CreateCommitRangeRow();
            commitRow.style.marginTop = 6;
            commitRow.Add(_generateButton);
            Add(commitRow);

            _cantGenerateInfo = new HelpBox(string.Empty, HelpBoxMessageType.Info);
            Add(_cantGenerateInfo);

            var notesField = new TextField { multiline = true, isDelayed = true, bindingPath = VersionProfileSO.NotesProperty };
            notesField.textEdition.placeholder = "feat: New shop\nfix(ui): Button overlap\n\nOne change per line, or Generate them.";
            notesField.textEdition.hidePlaceholderOnFocus = true;
            notesField.style.minHeight = 80;
            notesField.style.marginTop = 4;
            notesField.style.whiteSpace = WhiteSpace.Normal;
            Add(notesField);

            RefreshAI();

            // Closing the window or a script reload stops a running AI tool instead of leaving it behind.
            RegisterCallback<DetachFromPanelEvent>(_ => _cancellation?.Cancel());
        }

        /// <summary>Shows and edits <paramref name="profile"/>; null leaves the fields empty and disabled.</summary>
        public void SetProfile(VersionProfileSO profile)
        {
            if (profile == _profile && (profile == null || _serialized?.targetObject != null))
                return;

            _profile = profile;
            _tracker?.RemoveFromHierarchy();
            _tracker = null;
            this.Unbind();
            _serialized = profile != null ? new SerializedObject(profile) : null;
            SetEnabled(profile != null);

            if (_serialized == null)
                return;

            this.Bind(_serialized);

            // Its own element, so switching profiles drops the old tracking with it.
            _tracker = new VisualElement();
            _tracker.TrackSerializedObjectValue(_serialized, _ => Changed?.Invoke());
            Add(_tracker);
        }

        private void Set(string propertyName, Action<SerializedProperty> change)
        {
            if (_serialized == null || _serialized.targetObject == null)
                return;

            _serialized.Update();
            change(_serialized.FindProperty(propertyName));
            _serialized.ApplyModifiedProperties();
        }

        // "Commits  [after last tag][▾] → [HEAD][▾]": both ends included; empty ends show what they default to.
        private VisualElement CreateCommitRangeRow()
        {
            var row = new VisualElement();
            row.style.flexDirection = FlexDirection.Row;
            row.style.alignItems = Align.Center;

            var label = new Label("Commits")
            {
                tooltip = "The commits to generate the notes from, both ends included. Type a commit hash, tag or branch, or pick one with ▾.",
            };
            label.AddToClassList(BaseField<string>.labelUssClassName);
            label.style.marginLeft = 3; // A field's own margin, so it lines up with the labels above and below.
            row.Add(label);

            row.Add(CreateRevisionField(VersionProfileSO.CommitsFromProperty, "after last tag",
                "First commit, tag or branch (included). Empty: the commits after the newest tag."));

            var arrow = new Label("→");
            arrow.style.marginLeft = arrow.style.marginRight = 3;
            row.Add(arrow);

            row.Add(CreateRevisionField(VersionProfileSO.CommitsToProperty, "HEAD",
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
                var menu = new GenericMenu();

                void AddItem(string text, string value) =>
                    menu.AddItem(new GUIContent(text.Replace('/', '∕')), field.value == value, // '/' would make a submenu.
                        () => Set(propertyName, property => property.stringValue = value));

                AddItem($"Default ({placeholder})", string.Empty);

                // Say why there's nothing to pick, instead of a menu that looks empty.
                if (ChangelogGenerator.GetGitProblem() is { } gitProblem)
                {
                    menu.AddSeparator(string.Empty);
                    menu.AddDisabledItem(new GUIContent(gitProblem.Replace('/', '∕')));
                    menu.DropDown(pick.worldBound);
                    return;
                }

                var (tags, commits) = ChangelogGenerator.GetRecentRevisions(15);

                if (tags.Count > 0)
                {
                    menu.AddSeparator(string.Empty);
                    menu.AddDisabledItem(new GUIContent("Tags"));
                    tags.ForEach(tag => AddItem(tag, tag));
                }

                if (commits.Count > 0)
                {
                    menu.AddSeparator(string.Empty);
                    menu.AddDisabledItem(new GUIContent("Recent commits"));

                    foreach (var (hash, subject) in commits)
                        AddItem($"{hash}  {(subject.Length > 50 ? subject.Substring(0, 50) + "…" : subject)}", hash);
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

        /// <summary>
        /// Enables Generate when the AI picked under Options is installed. AI tools can be installed or removed while
        /// the editor is open, so the window calls this whenever it gets focus.
        /// </summary>
        public void RefreshAI()
        {
            var selected = ChangelogGenerator.Resolve(EasyBuilderSettings.instance.ChangelogAI);
            var isInstalled = selected.IsAvailable();
            var gitProblem = ChangelogGenerator.GetGitProblem();

            // Disabled buttons don't always show their tooltip, so the reasons are also written out here.
            var reasons = new System.Collections.Generic.List<string>();

            if (gitProblem != null)
                reasons.Add($"Can't generate: {gitProblem}");

            if (!isInstalled)
                reasons.Add($"{selected.DisplayName} isn't installed. {selected.InstallHint} Or pick another AI under Options in the Easy Builder window.");

            SetVisible(_cantGenerateInfo, reasons.Count > 0);
            _cantGenerateInfo.text = string.Join("\n", reasons);

            // While generating, the button is the Cancel button.
            if (IsGenerating)
                return;

            _generateButton.SetEnabled(isInstalled && gitProblem == null);
            _generateButton.tooltip = gitProblem != null ? $"Can't generate: {gitProblem}"
                : !isInstalled ? $"{selected.DisplayName} isn't installed. Pick another AI under Options, or install it."
                : $"Write the notes with {selected.DisplayName} from the commits in the range (by default, since the last tag), " +
                  "at the detail level set under Options.";
        }

        private void OnGenerateClicked()
        {
            if (_cancellation != null)
                _cancellation.Cancel();
            else
                Generate();
        }

        private async void Generate()
        {
            var profile = _profile;

            if (profile == null)
                return;

            var settings = EasyBuilderSettings.instance;
            var ai = ChangelogGenerator.Resolve(settings.ChangelogAI);

            // It may have been uninstalled since the list was last checked.
            if (!ai.IsAvailable())
            {
                RefreshAI();
                return;
            }

            var append = false;

            if (!string.IsNullOrWhiteSpace(profile.Notes))
            {
                var choice = EditorUtility.DisplayDialogComplex("Generate Changelog",
                    $"{profile.DisplayName} already has notes. Replace them, or add the generated notes below them?", "Replace", "Cancel", "Add Below");

                if (choice == 1)
                    return;

                append = choice == 2;
            }

            _cancellation = new CancellationTokenSource();
            _generateButton.text = "Cancel";
            _generateButton.tooltip = $"Stop waiting for {ai.DisplayName}.";

            // "Cancel (12s)", so it's clear the AI is still working.
            var started = DateTime.UtcNow;
            var ticker = _generateButton.schedule.Execute(() =>
                _generateButton.text = $"Cancel ({(int)(DateTime.UtcNow - started).TotalSeconds}s)").Every(1000);

            Debug.Log($"[EasyBuilder] Generating the changelog for {profile.DisplayName} with {ai.DisplayName}...");

            try
            {
                var generated = await ChangelogGenerator.GenerateAsync(ai, settings.ChangelogDetail, settings.ChangelogAITimeout,
                    profile.CommitsFrom, profile.CommitsTo, _cancellation.Token);

                // The asset may have been deleted while the AI was answering.
                if (profile == null)
                    return;

                // Written to the profile it was generated for, even if another one is shown now. Undoable.
                var serialized = new SerializedObject(profile);
                var notes = serialized.FindProperty(VersionProfileSO.NotesProperty);
                notes.stringValue = append ? Changelog.Normalize(notes.stringValue) + "\n" + generated : generated;
                serialized.ApplyModifiedProperties();
                AssetDatabase.SaveAssetIfDirty(profile);

                Debug.Log($"[EasyBuilder] {ai.DisplayName} wrote the notes for {profile.DisplayName}. Review them before building.");
            }
            catch (OperationCanceledException)
            {
                Debug.Log("[EasyBuilder] Changelog generation canceled.");
            }
            catch (Exception e)
            {
                Debug.LogException(e);
                var message = e.Message.Length > 1500 ? e.Message.Substring(0, 1500) + "…" : e.Message;
                EditorUtility.DisplayDialog("Generate Changelog", $"{ai.DisplayName} couldn't generate the changelog:\n\n{message}", "OK");
            }
            finally
            {
                ticker.Pause();
                _cancellation.Dispose();
                _cancellation = null;
                _generateButton.text = GenerateText;
                RefreshAI();
            }
        }
    }
}
