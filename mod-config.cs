using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using BepInEx;
using BepInEx.Configuration;
using CombatStats.Model;
using UnityEngine;

namespace CombatStats
{
    internal sealed class ModConfig
    {
        public readonly ConfigEntry<KeyboardShortcut> CompactKey, DetailKey;
        public readonly ConfigEntry<MeterMode> CompactMode;
        public readonly ConfigEntry<int> CompactRows;
        public readonly ConfigEntry<ScreenAnchor> CompactAnchor;
        public readonly ConfigEntry<Vector2> CompactOffset, DetailSize;
        public readonly ConfigEntry<float> CompactWidth, CompactScale, CompactOpacity;
        public readonly ConfigEntry<bool> CompactCaption, CompactShare, CompactBars, IsDebug;
        public readonly ConfigEntry<string> Colors;
        private readonly ConfigFile config;
        private readonly SortedDictionary<string, SortedDictionary<string, string>> personal = new SortedDictionary<string, SortedDictionary<string, string>>();
        private readonly string path;
        private bool writable = true;

        public ModConfig(ConfigFile config)
        {
            this.config = config;
            config.SaveOnConfigSet = false;
            CompactKey = config.Bind("Compact", "CompactKey", new KeyboardShortcut(KeyCode.F10), "Cycle compact meter visibility.");
            DetailKey = config.Bind("Detail", "DetailKey", new KeyboardShortcut(KeyCode.F11), "Open fight details.");
            CompactMode = config.Bind("Compact", "CompactMode", MeterMode.Off, "Auto shows the running or latest fight; Always also shows an empty meter; Off hides it.");
            CompactRows = config.Bind("Compact", "CompactRows", 5, new ConfigDescription("Visible rows, including your own.", new AcceptableValueRange<int>(1, 12)));
            CompactAnchor = config.Bind("Compact", "CompactAnchor", ScreenAnchor.TopRight, "Screen corner or edge.");
            CompactOffset = config.Bind("Compact", "CompactOffset", new Vector2(28f, 164f), "Distance in pixels from the chosen corner towards the middle of the screen; middle edges centre the meter vertically.");
            // Below about 260 the numbers leave no room for names.
            CompactWidth = config.Bind("Compact", "CompactWidth", 360f, new ConfigDescription("Width in pixels.", new AcceptableValueRange<float>(260f, 700f)));
            CompactScale = config.Bind("Compact", "CompactScale", 1f, new ConfigDescription("", new AcceptableValueRange<float>(0.75f, 2f)));
            CompactOpacity = config.Bind("Compact", "CompactOpacity", 0.9f, new ConfigDescription("", new AcceptableValueRange<float>(0.5f, 1f)));
            CompactCaption = config.Bind("Compact", "CompactCaption", true, "Show the fight's name, duration and result.");
            CompactShare = config.Bind("Compact", "CompactShare", true, "Show contribution percentages.");
            CompactBars = config.Bind("Compact", "CompactBars", true, "Show damage-type bars.");
            DetailSize = config.Bind("Detail", "DetailSize", new Vector2(700f, 480f), "Width and height in pixels.");
            Colors = config.Bind("Colours", "Colors", "", "Damage-type overrides, such as Fire=#ff7733.");
            IsDebug = config.Bind("General", "IsDebug", false, "Enable diagnostic logging.");
            path = Path.Combine(Paths.BepInExRootPath, "combat-stats-state", "settings.dat");
            LoadPersonal();
            config.SettingChanged += (_, change) =>
            {
                Remember(change.ChangedSetting.Definition, change.ChangedSetting.GetSerializedValue());
                Save();
            };
        }

        private void LoadPersonal()
        {
            string text = Storage.Read(path, out string problem);
            if (problem != null)
            {
                // Saving now would replace every remembered choice with this session's changes.
                writable = false;
                Plugin.Log.LogError(problem + ". Display changes are not saved this session.");
            }
            if (text == null) return;
            string section = null;
            bool hidden = false;
            foreach (string line in text.Split('\n'))
            {
                string trimmed = line.Trim();
                if (trimmed.Length == 0 || trimmed.StartsWith("#")) continue;
                if (trimmed.StartsWith("[") && trimmed.EndsWith("]")) { section = trimmed.Substring(1, trimmed.Length - 2); continue; }
                string[] parts = trimmed.Split(new[] { '=' }, 2);
                if (section == null || parts.Length != 2) continue;
                var definition = new ConfigDefinition(section, parts[0].Trim());
                if (section == "General" && definition.Key == "Enabled" && bool.TryParse(parts[1].Trim(), out bool enabled)) hidden = !enabled;
                // Retired time-window/collection settings are intentionally not migrated.
                if (!config.ContainsKey(definition)) continue;
                config[definition].SetSerializedValue(parts[1].Trim());
                Remember(definition, config[definition].GetSerializedValue());
            }
            if (hidden)
            {
                CompactMode.Value = MeterMode.Off;
                Remember(CompactMode.Definition, CompactMode.GetSerializedValue());
            }
            Vector2 offset = CompactOffset.Value;
            if (offset.x < 0f || offset.y < 0f)
            {
                // Before 1.0.2 the offset was signed: negative moved left and down. A placement
                // that kept the meter on screen had the same distances from its corner.
                CompactOffset.Value = new Vector2(Mathf.Abs(offset.x), Mathf.Abs(offset.y));
                Remember(CompactOffset.Definition, CompactOffset.GetSerializedValue());
            }
        }

        private void Remember(ConfigDefinition definition, string value)
        {
            if (!personal.TryGetValue(definition.Section, out var section))
                personal[definition.Section] = section = new SortedDictionary<string, string>();
            section[definition.Key] = value;
        }

        private void Save()
        {
            if (!writable) return;
            var text = new StringBuilder("## Personal combat meter display settings. Gale does not replace this file.\n");
            foreach (var section in personal)
            {
                text.Append("\n[").Append(section.Key).Append("]\n");
                foreach (var entry in section.Value) text.Append(entry.Key).Append(" = ").Append(entry.Value).Append('\n');
            }
            string problem = Storage.Write(path, text.ToString());
            if (problem != null) Plugin.Log.LogWarning(problem);
        }

        public Color[] ParseColors()
        {
            var result = new Color[DamageKinds.Count];
            for (int kind = 0; kind < result.Length; kind++) ColorUtility.TryParseHtmlString(DamageKinds.ColorHex((DamageKind)kind), out result[kind]);
            foreach (string item in Colors.Value.Split(','))
            {
                string[] pair = item.Split('=');
                if (pair.Length == 2 && Enum.TryParse(pair[0].Trim(), true, out DamageKind kind) &&
                    (int)kind >= 0 && (int)kind < result.Length && ColorUtility.TryParseHtmlString(pair[1].Trim(), out Color color))
                    result[(int)kind] = color;
            }
            return result;
        }
    }
}
