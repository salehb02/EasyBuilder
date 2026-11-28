using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using UnityEditor;
using UnityEditor.UIElements;
using UnityEngine;
using UnityEngine.UIElements;
using static EasyBuilder.EditorUI;

namespace EasyBuilder
{
    /// <summary>
    /// The profile list in the Easy Builder window. Each profile is a card with an on/off switch, its
    /// asset, a summary of what it changes and its problems, plus buttons to reorder, edit and remove it.
    /// Profile assets can be dragged onto the list.
    /// </summary>
    internal sealed class ProfileList : VisualElement
    {
        private readonly VisualElement _rows;
        private readonly Label _emptyState;
        private string _shownState;

        /// <summary>Raised after the list is changed from this element.</summary>
        public event Action Changed;

        private static EasyBuilderSettings Settings => EasyBuilderSettings.instance;

        public bool HasEmptySlot => Settings.ProfileList.Contains(null);

        public ProfileList()
        {
            _rows = new VisualElement();
            Add(_rows);

            _emptyState = new Label("No profiles yet.\nClick Add Profile ▾, or drag profile assets from the Project window here.");
            _emptyState.style.whiteSpace = WhiteSpace.Normal;
            _emptyState.style.unityTextAlign = TextAnchor.MiddleCenter;
            _emptyState.style.opacity = 0.7f;
            _emptyState.style.paddingTop = _emptyState.style.paddingBottom = 12;
            Add(_emptyState);

            RegisterCallback<DragUpdatedEvent>(OnDragUpdated);
            RegisterCallback<DragPerformEvent>(OnDragPerform);
            RegisterCallback<DragLeaveEvent>(_ => SetDropHighlight(false));
            RegisterCallback<DragExitedEvent>(_ => SetDropHighlight(false));
        }

        /// <summary>
        /// Updates the cards if anything they show has changed. Cheap to call often: rebuilding only when
        /// needed keeps clicks from landing on cards that are being replaced.
        /// </summary>
        public void Refresh()
        {
            if (DescribeState() != _shownState)
                Rebuild();
        }

        public void AddEmptySlot()
        {
            if (!HasEmptySlot)
                Modify(() => Settings.ProfileList.Add(null));
        }

        private void Rebuild()
        {
            _shownState = DescribeState();
            _rows.Clear();

            var profiles = Settings.ProfileList;
            var target = BuildRunner.ActiveTarget;
            var duplicates = FindDuplicates(profiles);

            for (var i = 0; i < profiles.Count; i++)
                _rows.Add(CreateRow(i, profiles.Count, profiles[i], target, duplicates.Contains(profiles[i])));

            SetVisible(_emptyState, profiles.Count == 0);
        }

        // Everything a card displays, so Refresh can tell whether the cards are out of date.
        private static string DescribeState()
        {
            var profiles = Settings.ProfileList;
            var target = BuildRunner.ActiveTarget;
            var state = new StringBuilder().Append(target).Append(EditorGUIUtility.isProSkin);

            foreach (var profile in profiles)
            {
                state.Append('\n');

                if (profile == null)
                    continue;

                state.Append(AssetDatabase.GetAssetPath(profile)).Append('|')
                    .Append(profile.name).Append('|')
                    .Append(Settings.IsProfileEnabled(profile)).Append('|')
                    .Append(profile.Describe(target)).Append('|')
                    .Append(string.Join(";", BuildRunner.ValidateProfile(profile, target)));
            }

            return state.ToString();
        }

        private static HashSet<BaseBuildProfileSO> FindDuplicates(List<BaseBuildProfileSO> profiles) =>
            new(profiles
                .Where(profile => profile != null)
                .GroupBy(profile => profile)
                .Where(group => group.Count() > 1)
                .Select(group => group.Key));

        private VisualElement CreateRow(int index, int count, BaseBuildProfileSO profile, BuildTarget target, bool isDuplicate)
        {
            var enabled = profile != null && Settings.IsProfileEnabled(profile);

            var card = new VisualElement();
            StyleCard(card);

            // Top line: switch, number, asset, status and actions.
            var top = new VisualElement();
            top.style.flexDirection = FlexDirection.Row;
            top.style.alignItems = Align.Center;
            card.Add(top);

            var toggle = new Toggle
            {
                value = enabled,
                tooltip = profile == null
                    ? "Pick a profile asset first."
                    : "Build this profile. Untick to skip it without removing it from the list.",
            };
            toggle.style.marginRight = 2;
            toggle.SetEnabled(profile != null);
            toggle.RegisterValueChangedCallback(evt => Modify(() => Settings.SetProfileEnabled(profile, evt.newValue)));
            top.Add(toggle);

            var number = new Label($"{index + 1}.");
            number.style.minWidth = 20;
            number.style.unityFontStyleAndWeight = FontStyle.Bold;
            top.Add(number);

            var field = new ObjectField { objectType = typeof(BaseBuildProfileSO), allowSceneObjects = false, value = profile };
            field.style.flexGrow = 1;
            field.style.flexShrink = 1;
            field.style.minWidth = 60;
            field.RegisterValueChangedCallback(evt => Modify(() => Settings.ProfileList[index] = evt.newValue as BaseBuildProfileSO));
            top.Add(field);

            var problems = new List<string>();

            if (enabled)
            {
                if (isDuplicate)
                    problems.Add("This profile is in the list more than once, so its builds would overwrite each other.");

                problems.AddRange(BuildRunner.ValidateProfile(profile, target));
            }

            top.Add(CreateStatus(profile, enabled, problems));

            var up = CreateIconButton(null, "↑", "Move up", () => Modify(() => Swap(index, index - 1)));
            up.SetEnabled(index > 0);
            top.Add(up);

            var down = CreateIconButton(null, "↓", "Move down", () => Modify(() => Swap(index, index + 1)));
            down.SetEnabled(index < count - 1);
            top.Add(down);

            var edit = CreateIconButton("editicon.sml", "…", "Select the profile asset to edit it in the Inspector", () =>
            {
                Selection.activeObject = profile;
                EditorGUIUtility.PingObject(profile);
            });
            edit.SetEnabled(profile != null);
            top.Add(edit);

            top.Add(CreateIconButton("TreeEditor.Trash", "×", "Remove from the list. The asset itself is kept.",
                () => Modify(() => Settings.ProfileList.RemoveAt(index))));

            // Summary and problems.
            var summary = new Label(profile == null
                ? "Empty slot: pick a profile asset in the field above, or drag one here."
                : (enabled ? "" : "Skipped  ·  ") + profile.Describe(target));
            StyleDetail(summary);
            summary.style.opacity = 0.75f;
            card.Add(summary);

            foreach (var problem in problems)
            {
                var line = new Label("• " + problem);
                StyleDetail(line);
                line.style.color = ErrorColor;
                card.Add(line);
            }

            if (profile != null && !enabled)
                card.style.opacity = 0.6f;

            return card;
        }

        private static Label CreateStatus(BaseBuildProfileSO profile, bool enabled, List<string> problems)
        {
            var status = new Label();
            status.style.minWidth = 34;
            status.style.unityTextAlign = TextAnchor.MiddleCenter;
            status.style.unityFontStyleAndWeight = FontStyle.Bold;

            if (!enabled)
            {
                status.text = "–";
                status.tooltip = profile == null ? "No profile in this slot." : "Skipped: this profile won't be built.";
            }
            else if (problems.Count == 0)
            {
                status.text = "✓";
                status.tooltip = "Ready to build.";
                status.style.color = OkColor;
            }
            else
            {
                status.text = $"⚠ {problems.Count}";
                status.tooltip = string.Join("\n", problems);
                status.style.color = WarningColor;
            }

            return status;
        }

        private static void StyleCard(VisualElement card)
        {
            card.style.marginBottom = 4;
            card.style.paddingTop = card.style.paddingBottom = 3;
            card.style.paddingLeft = card.style.paddingRight = 4;
            SetBorder(card, 1, BorderColor, 3);
            card.style.backgroundColor = EditorGUIUtility.isProSkin ? new Color(0, 0, 0, 0.12f) : new Color(1, 1, 1, 0.4f);
        }

        private static void StyleDetail(Label label)
        {
            label.style.whiteSpace = WhiteSpace.Normal;
            label.style.fontSize = 10;
            label.style.paddingLeft = 40;
            label.style.marginTop = 1;
        }

        private static void Swap(int a, int b)
        {
            var profiles = Settings.ProfileList;
            (profiles[a], profiles[b]) = (profiles[b], profiles[a]);
        }

        private void Modify(Action change)
        {
            Undo.RecordObject(Settings, "Edit Easy Builder Profiles");
            change();
            Settings.SaveSettings();

            // Rebuild after the current event; the element that raised it is about to be replaced.
            schedule.Execute(() =>
            {
                Rebuild();
                Changed?.Invoke();
            });
        }

        private static BaseBuildProfileSO[] DraggedProfiles() =>
            DragAndDrop.objectReferences.OfType<BaseBuildProfileSO>().ToArray();

        private void OnDragUpdated(DragUpdatedEvent evt)
        {
            var dragging = DraggedProfiles().Length > 0;
            DragAndDrop.visualMode = dragging ? DragAndDropVisualMode.Link : DragAndDropVisualMode.Rejected;
            SetDropHighlight(dragging);
        }

        private void OnDragPerform(DragPerformEvent evt)
        {
            var dropped = DraggedProfiles();
            SetDropHighlight(false);

            if (dropped.Length == 0)
                return;

            DragAndDrop.AcceptDrag();
            Modify(() =>
            {
                var profiles = Settings.ProfileList;

                foreach (var profile in dropped.Where(profile => !profiles.Contains(profile)))
                {
                    var emptySlot = profiles.IndexOf(null);

                    if (emptySlot >= 0)
                        profiles[emptySlot] = profile;
                    else
                        profiles.Add(profile);
                }
            });
        }

        private void SetDropHighlight(bool on) =>
            SetBorder(this, on ? 2 : 0, on ? new Color(0.3f, 0.6f, 1f, 0.9f) : Color.clear, 4);
    }
}
