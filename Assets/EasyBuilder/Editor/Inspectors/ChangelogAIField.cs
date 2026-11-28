using System;
using System.Linq;
using UnityEngine.UIElements;
using UnityEditor.UIElements;

namespace EasyBuilder
{
    /// <summary>Picks the AI that writes changelog notes. Lists every supported AI and marks the ones that aren't installed.</summary>
    internal sealed class ChangelogAIField : VisualElement
    {
        private PopupField<ChangelogAI> _popup;
        private string _installedAIs;

        /// <summary>Raised when another AI is picked.</summary>
        public event Action Changed;

        public ChangelogAIField() => Refresh();

        /// <summary>AI tools can be installed or removed while the editor is open, so call this when the window gets focus.</summary>
        public void Refresh()
        {
            var installed = ChangelogGenerator.All.Where(ai => ai.IsAvailable()).Select(ai => ai.Id).ToList();
            var installedKey = string.Join(",", installed);
            var selected = ChangelogGenerator.Resolve(EasyBuilderSettings.instance.ChangelogAI);

            // Rebuild only when what's installed changed (the labels show it), so an open popup isn't closed every time.
            if (_popup != null && installedKey == _installedAIs)
            {
                _popup.SetValueWithoutNotify(selected);
                return;
            }

            _installedAIs = installedKey;
            Clear();

            string Label(ChangelogAI ai) => installed.Contains(ai.Id) ? ai.DisplayName : $"{ai.DisplayName} (not installed)";

            _popup = new PopupField<ChangelogAI>("AI", ChangelogGenerator.All.ToList(), selected, Label, Label)
            {
                tooltip = "The AI that writes the notes when you click Generate. Command-line tools use your own login, so sign in to them once in a terminal.",
            };
            _popup.RegisterValueChangedCallback(evt =>
            {
                EasyBuilderSettings.instance.ChangelogAI = evt.newValue?.Id;
                Changed?.Invoke();
            });
            Add(_popup);
        }
    }
}
