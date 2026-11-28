using System;
using System.Collections.Generic;
using UnityEditor;
using UnityEngine;
using UnityEngine.UIElements;

namespace EasyBuilder
{
    /// <summary>Shared look for the Easy Builder window and inspectors.</summary>
    internal static class EditorUI
    {
        // Darker variants on the light skin so the text stays readable.
        public static Color OkColor => EditorGUIUtility.isProSkin ? new Color(0.35f, 0.75f, 0.4f) : new Color(0.1f, 0.45f, 0.15f);
        public static Color WarningColor => EditorGUIUtility.isProSkin ? new Color(0.95f, 0.65f, 0.2f) : new Color(0.6f, 0.33f, 0f);
        public static Color ErrorColor => EditorGUIUtility.isProSkin ? new Color(0.95f, 0.45f, 0.4f) : new Color(0.7f, 0.12f, 0.08f);

        public static Color BorderColor => EditorGUIUtility.isProSkin ? new Color(0, 0, 0, 0.45f) : new Color(0, 0, 0, 0.2f);

        /// <summary>A built-in editor icon for the current skin, or null if this editor version doesn't have it.</summary>
        public static Texture2D FindIcon(string name)
        {
            var icon = EditorGUIUtility.isProSkin ? EditorGUIUtility.FindTexture("d_" + name) : null;
            return icon != null ? icon : EditorGUIUtility.FindTexture(name);
        }

        /// <summary>
        /// A small square button showing a built-in icon, or <paramref name="fallbackText"/> when the icon
        /// isn't available, so it never renders as a missing glyph.
        /// </summary>
        public static Button CreateIconButton(string iconName, string fallbackText, string tooltip, Action onClick)
        {
            var button = new Button(onClick) { tooltip = tooltip };
            button.style.width = 22;
            button.style.height = 20;
            button.style.paddingLeft = button.style.paddingRight = 0;
            button.style.marginLeft = 1;
            button.style.marginRight = 0;
            button.style.alignItems = Align.Center;
            button.style.justifyContent = Justify.Center;

            var icon = iconName != null ? FindIcon(iconName) : null;

            if (icon != null)
            {
                var image = new Image { image = icon, scaleMode = ScaleMode.ScaleToFit, pickingMode = PickingMode.Ignore };
                image.style.width = image.style.height = 14;
                button.Add(image);
            }
            else
            {
                button.text = fallbackText;
            }

            return button;
        }

        /// <summary>True while the element, or a part of it such as a text field's input, has keyboard focus.</summary>
        public static bool HasFocus(VisualElement element) =>
            element.focusController?.focusedElement is VisualElement focused && (focused == element || element.Contains(focused));

        /// <summary>A bordered box with a bold title and an optional one-line explanation.</summary>
        public static VisualElement CreateSection(string title, string description = null)
        {
            var section = new VisualElement();
            section.style.marginTop = 8;
            section.style.paddingTop = section.style.paddingBottom = 6;
            section.style.paddingLeft = section.style.paddingRight = 6;
            SetBorder(section, 1, BorderColor, 4);
            section.style.backgroundColor = EditorGUIUtility.isProSkin ? new Color(1, 1, 1, 0.03f) : new Color(1, 1, 1, 0.35f);

            var header = new Label(title);
            header.style.unityFontStyleAndWeight = FontStyle.Bold;
            header.style.fontSize = 13;
            header.style.marginBottom = string.IsNullOrEmpty(description) ? 4 : 0;
            header.style.paddingLeft = 3;
            section.Add(header);

            if (!string.IsNullOrEmpty(description))
            {
                var hint = CreateHint(description);
                hint.style.marginBottom = 4;
                section.Add(hint);
            }

            return section;
        }

        /// <summary>Small, dimmed, wrapping text for explanations under a field.</summary>
        public static Label CreateHint(string text)
        {
            var hint = new Label(text);
            hint.style.whiteSpace = WhiteSpace.Normal;
            hint.style.fontSize = 10;
            hint.style.opacity = 0.7f;
            hint.style.paddingLeft = 3;
            hint.style.marginBottom = 2;
            return hint;
        }

        public static VisualElement CreateButtonRow(params (string text, string tooltip, Action onClick)[] buttons)
        {
            var row = new VisualElement();
            row.style.flexDirection = FlexDirection.Row;
            row.style.justifyContent = Justify.FlexEnd;
            row.style.flexWrap = Wrap.Wrap;
            row.style.marginTop = 4;

            foreach (var (text, tooltip, onClick) in buttons)
                row.Add(new Button(onClick) { text = text, tooltip = tooltip });

            return row;
        }

        public static VisualElement WithSideButtons(VisualElement field, params (string text, string tooltip, Action onClick)[] buttons)
        {
            var row = new VisualElement();
            row.style.flexDirection = FlexDirection.Row;
            field.style.flexGrow = 1;
            field.style.flexShrink = 1;
            row.Add(field);

            foreach (var (text, tooltip, onClick) in buttons)
                row.Add(new Button(onClick) { text = text, tooltip = tooltip });

            return row;
        }

        public static void SetVisible(VisualElement element, bool visible) =>
            element.style.display = visible ? DisplayStyle.Flex : DisplayStyle.None;

        public static void SetBorder(VisualElement element, float width, Color color, float radius)
        {
            element.style.borderTopWidth = element.style.borderBottomWidth = width;
            element.style.borderLeftWidth = element.style.borderRightWidth = width;
            element.style.borderTopColor = element.style.borderBottomColor = color;
            element.style.borderLeftColor = element.style.borderRightColor = color;
            element.style.borderTopLeftRadius = element.style.borderTopRightRadius = radius;
            element.style.borderBottomLeftRadius = element.style.borderBottomRightRadius = radius;
        }
    }

    /// <summary>
    /// A section like <see cref="EditorUI.CreateSection"/> whose title collapses it. While collapsed, a one-line summary
    /// sits next to the title, so set-once sections stay out of the way but still say what they're set to. Whether it's
    /// open is remembered per machine.
    /// </summary>
    internal sealed class CollapsibleSection : VisualElement
    {
        private readonly string _prefsKey;
        private readonly Label _arrow;
        private readonly Label _summary;

        /// <summary>Where the section's fields go.</summary>
        public VisualElement Content { get; }

        public bool IsOpen { get; private set; }

        public CollapsibleSection(string title, string prefsKey, bool openByDefault = true)
        {
            _prefsKey = prefsKey;

            style.marginTop = 8;
            style.paddingTop = style.paddingBottom = 6;
            style.paddingLeft = style.paddingRight = 6;
            EditorUI.SetBorder(this, 1, EditorUI.BorderColor, 4);
            style.backgroundColor = EditorGUIUtility.isProSkin ? new Color(1, 1, 1, 0.03f) : new Color(1, 1, 1, 0.35f);

            var header = new VisualElement { tooltip = "Click to show or hide." };
            header.style.flexDirection = FlexDirection.Row;
            header.style.alignItems = Align.Center;
            header.style.paddingLeft = 3;
            header.RegisterCallback<ClickEvent>(_ => SetOpen(!IsOpen));
            Add(header);

            _arrow = new Label { pickingMode = PickingMode.Ignore };
            _arrow.style.width = 12;
            _arrow.style.fontSize = 10;
            _arrow.style.opacity = 0.7f;
            header.Add(_arrow);

            var titleLabel = new Label(title) { pickingMode = PickingMode.Ignore };
            titleLabel.style.unityFontStyleAndWeight = FontStyle.Bold;
            titleLabel.style.fontSize = 13;
            titleLabel.style.flexShrink = 0;
            header.Add(titleLabel);

            _summary = new Label { pickingMode = PickingMode.Ignore };
            _summary.style.flexGrow = 1;
            _summary.style.flexShrink = 1;
            _summary.style.marginLeft = 8;
            _summary.style.opacity = 0.7f;
            _summary.style.unityTextAlign = TextAnchor.MiddleRight;
            _summary.style.whiteSpace = WhiteSpace.NoWrap;
            _summary.style.overflow = Overflow.Hidden;
            _summary.style.textOverflow = TextOverflow.Ellipsis;
            header.Add(_summary);

            Content = new VisualElement();
            Content.style.marginTop = 4;
            Add(Content);

            SetOpen(EditorPrefs.GetBool(_prefsKey, openByDefault), remember: false);
        }

        /// <summary>Shown next to the title while the section is collapsed, e.g. the folder builds go to.</summary>
        public string Summary
        {
            get => _summary.text;
            set
            {
                _summary.text = value;
                _summary.tooltip = value;
            }
        }

        public void SetOpen(bool open) => SetOpen(open, remember: true);

        private void SetOpen(bool open, bool remember)
        {
            IsOpen = open;
            _arrow.text = open ? "▼" : "▶";
            EditorUI.SetVisible(Content, open);
            EditorUI.SetVisible(_summary, !open);

            if (remember)
                EditorPrefs.SetBool(_prefsKey, open);
        }
    }

    /// <summary>
    /// A green "ready" or orange "N problems" panel. Long problem lists scroll inside the panel, so it never
    /// pushes the rest of the UI around.
    /// </summary>
    internal sealed class StatusPanel : VisualElement
    {
        private readonly Label _title;
        private readonly ScrollView _list;
        private readonly string _readyText;
        private readonly string _problemsText;
        private string _shown;

        /// <param name="readyText">Shown when there are no problems.</param>
        /// <param name="problemsText">Shown after the problem count, e.g. "to fix before building".</param>
        public StatusPanel(string readyText, string problemsText, float maxListHeight = 90)
        {
            _readyText = readyText;
            _problemsText = problemsText;

            style.paddingTop = style.paddingBottom = 4;
            style.paddingLeft = style.paddingRight = 6;
            style.borderLeftWidth = 3;
            style.borderTopRightRadius = style.borderBottomRightRadius = 3;

            _title = new Label();
            _title.style.unityFontStyleAndWeight = FontStyle.Bold;
            _title.style.whiteSpace = WhiteSpace.Normal;
            Add(_title);

            _list = new ScrollView(ScrollViewMode.Vertical);
            _list.style.maxHeight = maxListHeight;
            _list.style.marginTop = 2;
            Add(_list);
        }

        public void SetProblems(IReadOnlyList<string> problems)
        {
            // Only rebuild when something changed, so the list keeps its scroll position.
            var key = string.Join("\n", problems);

            if (key == _shown)
                return;

            _shown = key;
            _list.Clear();

            var color = problems.Count == 0 ? EditorUI.OkColor : EditorUI.WarningColor;
            style.borderLeftColor = color;
            style.backgroundColor = new Color(color.r, color.g, color.b, 0.12f);
            _title.style.color = color;
            _title.text = problems.Count == 0
                ? "✓ " + _readyText
                : $"⚠ {problems.Count} problem{(problems.Count == 1 ? "" : "s")} {_problemsText}";

            foreach (var problem in problems)
            {
                var line = new Label("• " + problem);
                line.style.whiteSpace = WhiteSpace.Normal;
                line.style.fontSize = 11;
                line.style.marginTop = 1;
                _list.Add(line);
            }

            EditorUI.SetVisible(_list, problems.Count > 0);
        }
    }
}
