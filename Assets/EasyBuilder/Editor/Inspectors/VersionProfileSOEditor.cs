using System.Collections.Generic;
using UnityEditor;
using UnityEditor.UIElements;
using UnityEngine.UIElements;
using static EasyBuilder.EditorUI;

namespace EasyBuilder
{
    [CustomEditor(typeof(VersionProfileSO))]
    [CanEditMultipleObjects]
    internal class VersionProfileSOEditor : Editor
    {
        private VersionProfileSO Profile => (VersionProfileSO)target;

        private StatusPanel _status;
        private Button _useButton;

        public override VisualElement CreateInspectorGUI()
        {
            if (targets.Length > 1)
            {
                var section = CreateSection($"{targets.Length} Version Profiles");
                section.Add(new HelpBox("Select a single version profile to edit it.", HelpBoxMessageType.Info));
                return section;
            }

            var root = new VisualElement();

            var overview = CreateSection("Version Profile", "One release: its version, build number and changelog notes.");
            _status = new StatusPanel("Ready to build", "to fix before building");
            overview.Add(_status);

            _useButton = new Button(() =>
            {
                EasyBuilderSettings.instance.SetVersionProfile(Profile);
                EasyBuilderWindow.RefreshOpenWindows();
                Refresh();
            });
            var buttons = CreateButtonRow(("Open Easy Builder", "Open the Easy Builder window.", EasyBuilderWindow.Open));
            buttons.Insert(0, _useButton);
            overview.Add(buttons);
            root.Add(overview);

            var fieldsSection = CreateSection("Release");
            var fields = new VersionProfileFields();
            fields.SetProfile(Profile);
            fields.Changed += () =>
            {
                Refresh();
                EasyBuilderWindow.RefreshOpenWindows();
            };
            fieldsSection.Add(fields);
            root.Add(fieldsSection);

            // The AI tools can be installed while the Inspector is open.
            root.RegisterCallback<FocusInEvent>(_ => fields.RefreshAI());

            Refresh();
            return root;
        }

        private void Refresh()
        {
            if (_status == null || target == null)
                return;

            var problems = new List<string>();
            Profile.Validate(problems);
            _status.SetProblems(problems);

            var inUse = EasyBuilderSettings.instance.VersionProfile == Profile;
            _useButton.text = inUse ? "✓ Used by Easy Builder" : "Use in Easy Builder";
            _useButton.tooltip = inUse ? "Easy Builder builds this version." : "Make Easy Builder build this version.";
            _useButton.SetEnabled(!inUse);
        }
    }
}
