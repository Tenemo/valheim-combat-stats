using System;
using System.Collections.Generic;
using System.Globalization;
using CombatStats.Model;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace CombatStats.Ui
{
    /// <summary>
    /// The settings that get changed often, in game: the two windows, what they show and where,
    /// and their appearance. Keys and colours stay
    /// in <c>tenemo.CombatStats.cfg</c>, where they are changed once and forgotten.
    ///
    /// Every control updates its config entry and the private display-settings file.
    /// </summary>
    internal sealed class SettingsPanel : MonoBehaviour
    {
        private const float Padding = 16f;

        private const float RowHeight = 24f;

        private const float ColumnWidth = 320f;

        /// <summary>Wide enough for the longest value, "middle right".</summary>
        private const float ValueWidth = 110f;

        private readonly List<Control> _rows = new List<Control>();

        private RectTransform _panel;

        private TMP_Text _hint;

        private bool _failed;

        private bool _open;

        /// <summary>The running panel, once it has been opened for the first time.</summary>
        public static SettingsPanel Instance { get; private set; }

        /// <summary>True while the panel has the screen.</summary>
        public static bool IsOpen => Instance != null && Instance._open;

        /// <summary>Opens the panel, or closes it when it is already up.</summary>
        public void Toggle()
        {
            if (_open)
            {
                Close();
                return;
            }

            if (_panel == null)
            {
                _panel = UiStyle.Build("CombatStats Settings", ref _failed, Build);
                if (_panel == null)
                {
                    return;
                }
            }

            _open = true;
            _panel.gameObject.SetActive(true);
            RefreshValues();
        }

        /// <summary>Closes the panel.</summary>
        public void Close()
        {
            _open = false;
            if (_panel != null)
            {
                _panel.gameObject.SetActive(false);
            }
        }

        private void Awake()
        {
            Instance = this;
        }

        private void Update()
        {
            if (_open && _panel == null)
            {
                // The canvas went with the world.
                Close();
            }
        }

        private void OnDestroy()
        {
            if (Instance == this)
            {
                Instance = null;
            }
        }

        private void RefreshValues()
        {
            foreach (Control row in _rows)
            {
                row.Refresh();
            }

            ModConfig settings = Plugin.Settings;
            _hint.text = settings.CompactKey.Value + "  Meter visibility    " + settings.DetailKey.Value + "  Fight details";
        }

        private void Build(RectTransform panel)
        {
            _panel = panel;
            _rows.Clear();

            UiStyle.Panel(panel, new Vector2((2f * ColumnWidth) + 48f + (2f * Padding), 470f), 110, "Combat stats settings");
            UiStyle.CornerButton(panel, "Close", Padding, 70f, Close);

            ModConfig settings = Plugin.Settings;

            float left = Padding;
            float right = Padding + ColumnWidth + 48f;
            float y = 46f;

            y = Heading("Compact meter", left, y);
            y = Row(left, y, "When to show", () => Word(settings.CompactMode.Value), step =>
                settings.CompactMode.Value = (MeterMode)Wrap((int)settings.CompactMode.Value + step, 3));
            y = Row(left, y, "Rows", () => settings.CompactRows.Value.ToString(CultureInfo.InvariantCulture), step =>
                settings.CompactRows.Value += step);
            y = Row(left, y, "Position", () => Word(settings.CompactAnchor.Value), step =>
                settings.CompactAnchor.Value = (ScreenAnchor)Wrap((int)settings.CompactAnchor.Value + step, 6));
            // Insets move the meter in from its corner, towards the middle of the screen.
            y = Row(left, y, "Horizontal inset", () => settings.CompactOffset.Value.x.ToString("0", CultureInfo.InvariantCulture), step =>
                settings.CompactOffset.Value = new Vector2(Mathf.Max(0f, settings.CompactOffset.Value.x + (step * 10f)), settings.CompactOffset.Value.y));
            y = Row(left, y, "Vertical inset", () => settings.CompactOffset.Value.y.ToString("0", CultureInfo.InvariantCulture), step =>
                settings.CompactOffset.Value = new Vector2(settings.CompactOffset.Value.x, Mathf.Max(0f, settings.CompactOffset.Value.y + (step * 10f))));
            y = Row(left, y, "Width", () => settings.CompactWidth.Value.ToString("0", CultureInfo.InvariantCulture), step =>
                settings.CompactWidth.Value += step * 20f);
            y = Row(left, y, "Scale", () => settings.CompactScale.Value.ToString("0.00", CultureInfo.InvariantCulture), step =>
                settings.CompactScale.Value += step * 0.05f);
            y = Row(left, y, "Background opacity", () => settings.CompactOpacity.Value.ToString("0.00", CultureInfo.InvariantCulture), step =>
                settings.CompactOpacity.Value += step * 0.05f);
            y = Toggle(left, y, "Caption line", () => settings.CompactCaption.Value, value => settings.CompactCaption.Value = value);
            y = Toggle(left, y, "Percentages", () => settings.CompactShare.Value, value => settings.CompactShare.Value = value);
            Toggle(left, y, "Bars", () => settings.CompactBars.Value, value => settings.CompactBars.Value = value);

            y = 46f;
            y = Heading("Detail window", right, y);
            y = Row(right, y, "Width", () => settings.DetailSize.Value.x.ToString("0", CultureInfo.InvariantCulture), step =>
                settings.DetailSize.Value = new Vector2(Mathf.Clamp(settings.DetailSize.Value.x + (step * 20f), 600f, 1400f), settings.DetailSize.Value.y));
            y = Row(right, y, "Height", () => settings.DetailSize.Value.y.ToString("0", CultureInfo.InvariantCulture), step =>
                settings.DetailSize.Value = new Vector2(settings.DetailSize.Value.x, Mathf.Clamp(settings.DetailSize.Value.y + (step * 20f), 280f, 1000f)));

            y += 16f;
            TMP_Text rules = UiStyle.Text("RecordingRules", panel, UiStyle.SmallSize, TextAlignmentOptions.TopLeft, UiStyle.Dim);
            UiStyle.PlaceTopLeft(rules.rectTransform, right, y, ColumnWidth, 44f);
            rules.text = "Pets and summons count for their owner.\nDPS uses the whole fight.";

            _hint = UiStyle.Text("Hint", panel, UiStyle.CaptionSize, TextAlignmentOptions.MidlineLeft, UiStyle.Dim);
            UiStyle.PlaceTopLeft(_hint.rectTransform, Padding, panel.sizeDelta.y - 30f, panel.sizeDelta.x - (2f * Padding), 18f);

            panel.gameObject.SetActive(false);
            Plugin.Debug("Settings panel built");
        }

        private float Heading(string text, float x, float y)
        {
            TMP_Text label = UiStyle.Text("Heading", _panel, UiStyle.SmallSize, TextAlignmentOptions.MidlineLeft, UiStyle.Mine);
            UiStyle.PlaceTopLeft(label.rectTransform, x, y, ColumnWidth, 20f);
            label.text = text;

            UiStyle.PlaceTopLeft(UiStyle.Line("HeadingLine", _panel).rectTransform, x, y + 19f, ColumnWidth, 1f);
            return y + 26f;
        }

        private float Toggle(float x, float y, string label, Func<bool> read, Action<bool> write)
        {
            return Row(x, y, label, () => read() ? "on" : "off", step =>
            {
                if (step != 0)
                {
                    write(!read());
                }
            });
        }

        private float Row(float x, float y, string label, Func<string> read, Action<int> step)
        {
            var go = new GameObject("Setting", typeof(RectTransform));
            var root = (RectTransform)go.transform;
            root.SetParent(_panel, false);
            UiStyle.PlaceTopLeft(root, x, y, ColumnWidth, RowHeight);

            const float arrow = 22f;
            TMP_Text caption = UiStyle.Text("Label", root, UiStyle.BodySize, TextAlignmentOptions.MidlineLeft, UiStyle.FontColor);
            caption.rectTransform.offsetMax = new Vector2(-(ValueWidth + (2f * arrow) + 8f), 0f);
            caption.text = label;

            TMP_Text value = UiStyle.Text("Value", root, UiStyle.BodySize, TextAlignmentOptions.Center, UiStyle.Dim);
            value.rectTransform.anchorMin = new Vector2(1f, 0f);
            value.rectTransform.anchorMax = new Vector2(1f, 1f);
            value.rectTransform.offsetMin = new Vector2(-(ValueWidth + arrow + 2f), 0f);
            value.rectTransform.offsetMax = new Vector2(-(arrow + 2f), 0f);

            var row = new Control(value, read);

            Button less = UiStyle.TextButton("Less", root, "<", UiStyle.RowSize, UiStyle.Dim, () =>
            {
                step(-1);
                RefreshValues();
            });
            Place(less, -(ValueWidth + (2f * arrow) + 4f), -(ValueWidth + arrow + 4f));

            Button more = UiStyle.TextButton("More", root, ">", UiStyle.RowSize, UiStyle.Dim, () =>
            {
                step(1);
                RefreshValues();
            });
            Place(more, -arrow, 0f);

            row.Refresh();
            _rows.Add(row);
            return y + RowHeight;
        }

        private static void Place(Button button, float from, float to)
        {
            var rect = (RectTransform)button.transform;
            rect.anchorMin = new Vector2(1f, 0f);
            rect.anchorMax = new Vector2(1f, 1f);
            rect.pivot = new Vector2(0.5f, 0.5f);
            rect.offsetMin = new Vector2(from, 2f);
            rect.offsetMax = new Vector2(to, -2f);
        }

        private static int Wrap(int value, int count)
        {
            return ((value % count) + count) % count;
        }

        private static string Word(MeterMode mode)
        {
            switch (mode)
            {
                case MeterMode.Always: return "always";
                case MeterMode.Off: return "off";
                default: return "latest fight";
            }
        }

        private static string Word(ScreenAnchor anchor)
        {
            switch (anchor)
            {
                case ScreenAnchor.TopRight: return "top right";
                case ScreenAnchor.MiddleLeft: return "middle left";
                case ScreenAnchor.MiddleRight: return "middle right";
                case ScreenAnchor.BottomLeft: return "bottom left";
                case ScreenAnchor.BottomRight: return "bottom right";
                default: return "top left";
            }
        }

        /// <summary>One setting on screen, and how to read it back out of the config.</summary>
        private sealed class Control
        {
            private readonly TMP_Text _value;

            private readonly Func<string> _read;

            public Control(TMP_Text value, Func<string> read)
            {
                _value = value;
                _read = read;
            }

            public void Refresh()
            {
                _value.text = _read();
            }
        }
    }
}
