using System.Collections.Generic;
using CombatStats.Collect;
using CombatStats.Model;
using CombatStats.Stats;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace CombatStats.Ui
{
    /// <summary>
    /// The small window that shows the fight as it happens.
    ///
    /// It is a HUD element rather than a panel: a dark backing that thins out towards the bottom,
    /// a hairline above it and text in the game's outlined font, so it reads on snow and stays
    /// quiet in a dark forest. It appears with the first fight and keeps the latest result.
    /// </summary>
    internal sealed class CompactMeter : MonoBehaviour
    {
        private const float RefreshInterval = 0.25f;

        private const float FadeSeconds = 0.4f;

        private const float PadLeft = 11f;

        private const float PadTop = 7f;

        private const float PadBottom = 9f;

        private const float CaptionHeight = 14f;

        private readonly List<MeterRow> _rows = new List<MeterRow>();

        private RectTransform _root;

        private RectTransform _list;

        private CanvasGroup _group;

        private Image _background;

        private TMP_Text _caption;

        private float _nextRefresh;

        private float _alpha;

        private bool _failed;

        private void Update()
        {
            if (Plugin.Settings == null)
            {
                return;
            }

            if (Shortcut.IsPressed(Plugin.Settings.CompactKey.Value))
            {
                Cycle();
            }

            bool wanted = Wanted();
            if (wanted && Time.time >= _nextRefresh)
            {
                _nextRefresh = Time.time + RefreshInterval;
                Refresh();
            }

            Fade(wanted);
        }

        /// <summary>Shown from the first fight, always shown, off - and back round again.</summary>
        private void Cycle()
        {
            ConfigEntryCycle();
            MeterMode mode = Plugin.Settings.CompactMode.Value;
            string message = mode == MeterMode.Auto
                ? "Combat meter: shows the latest fight"
                : mode == MeterMode.Always ? "Combat meter: always shown" : "Combat meter: off";

            MessageHud hud = MessageHud.instance;
            if (hud != null)
            {
                hud.ShowMessage(MessageHud.MessageType.TopLeft, message);
            }

            Plugin.Debug(message);
        }

        private void ConfigEntryCycle()
        {
            switch (Plugin.Settings.CompactMode.Value)
            {
                case MeterMode.Auto:
                    Plugin.Settings.CompactMode.Value = MeterMode.Always;
                    break;
                case MeterMode.Always:
                    Plugin.Settings.CompactMode.Value = MeterMode.Off;
                    break;
                default:
                    Plugin.Settings.CompactMode.Value = MeterMode.Auto;
                    break;
            }
        }

        private bool Wanted()
        {
            if (Plugin.Settings.CompactMode.Value == MeterMode.Off)
            {
                return false;
            }

            if (Player.m_localPlayer == null || global::Hud.IsUserHidden() || Menu.IsVisible() || Minimap.IsOpen())
            {
                return false;
            }

            if (Plugin.Settings.CompactMode.Value == MeterMode.Always)
            {
                return true;
            }

            // Auto: the running fight, or else the latest saved one.
            return Plugin.History.Current(DamageCollector.LocalId()) != null;
        }

        private void Fade(bool wanted)
        {
            float target = wanted ? 1f : 0f;
            if (Mathf.Approximately(_alpha, target))
            {
                if (_group != null && _group.gameObject.activeSelf != (_alpha > 0.01f))
                {
                    _group.gameObject.SetActive(_alpha > 0.01f);
                }

                return;
            }

            _alpha = Mathf.MoveTowards(_alpha, target, Time.unscaledDeltaTime / FadeSeconds);

            if (_group == null)
            {
                return;
            }

            _group.alpha = _alpha;
            bool active = _alpha > 0.01f;
            if (_group.gameObject.activeSelf != active)
            {
                _group.gameObject.SetActive(active);
            }
        }

        private void Refresh()
        {
            if (_root == null)
            {
                _root = UiStyle.Build("CombatStats Compact", ref _failed, Build);
                if (_root == null)
                {
                    return;
                }
            }

            ModConfig settings = Plugin.Settings;
            float width = settings.CompactWidth.Value;
            bool bars = settings.CompactBars.Value;
            float rowWidth = width - (2f * PadLeft);

            long local = DamageCollector.LocalId();
            EncounterSnapshot snapshot = Plugin.History.Snapshot(FightView.Current, CombatChannel.DamageDealt, local);
            List<CombatantRow> shown = snapshot.Visible(settings.CompactRows.Value, local);
            float leader = shown.Count > 0 ? shown[0].Total : 0f;

            float y = 0f;
            bool caption = settings.CompactCaption.Value || shown.Count == 0;
            _caption.gameObject.SetActive(caption);
            if (caption)
            {
                _caption.text = shown.Count == 0 ? MeterFormat.EmptyText(snapshot, CombatChannel.DamageDealt) :
                    MeterFormat.Caption(snapshot, MeterFormat.Plain(Localization.instance.Localize(snapshot.Name)));
                UiStyle.PlaceTopStretch(_caption.rectTransform, 0f, 0f, 0f, CaptionHeight);
                y += CaptionHeight;
            }

            for (int index = 0; index < shown.Count; index++)
            {
                MeterRow row = RowAt(index);
                row.SetActive(true);
                row.Apply(shown[index], leader, rowWidth, bars, settings.CompactShare.Value, shown[index].Id == local, string.Empty);
                row.Place(y);
                y += MeterRow.Height(bars);
            }

            for (int index = shown.Count; index < _rows.Count; index++)
            {
                _rows[index].SetActive(false);
            }

            _list.sizeDelta = new Vector2(_list.sizeDelta.x, y);
            _root.sizeDelta = new Vector2(width, PadTop + y + PadBottom);
            _root.localScale = Vector3.one * settings.CompactScale.Value;
            UiStyle.Anchor(_root, settings.CompactAnchor.Value, settings.CompactOffset.Value);

            Color backing = UiStyle.MeterBackground;
            backing.a = settings.CompactOpacity.Value;
            _background.color = backing;
        }

        private MeterRow RowAt(int index)
        {
            while (_rows.Count <= index)
            {
                _rows.Add(MeterRow.Create(_list, detailed: false, onClick: null));
            }

            return _rows[index];
        }

        private void Build(RectTransform root)
        {
            _rows.Clear();
            root.sizeDelta = new Vector2(Plugin.Settings.CompactWidth.Value, 80f);

            _group = root.gameObject.AddComponent<CanvasGroup>();
            _group.alpha = 0f;
            _group.interactable = false;
            _group.blocksRaycasts = false;
            root.gameObject.SetActive(false);

            _background = UiStyle.Box("Background", root, UiStyle.MeterBackground);
            _background.sprite = UiStyle.Fade();
            UiStyle.Stretch(_background.rectTransform, 0f, 0f, 0f, 0f);

            Image hairline = UiStyle.Box("Hairline", root, UiStyle.Hairline);
            hairline.sprite = UiStyle.Flat();
            hairline.rectTransform.anchorMin = new Vector2(0f, 1f);
            hairline.rectTransform.anchorMax = new Vector2(1f, 1f);
            hairline.rectTransform.pivot = new Vector2(0.5f, 1f);
            hairline.rectTransform.sizeDelta = new Vector2(0f, 1f);
            hairline.rectTransform.anchoredPosition = Vector2.zero;

            var listGo = new GameObject("List", typeof(RectTransform));
            _list = (RectTransform)listGo.transform;
            _list.SetParent(root, false);
            UiStyle.PlaceTopStretch(_list, PadLeft, PadLeft, PadTop, 40f);

            _caption = UiStyle.Text("Caption", _list, UiStyle.CaptionSize, TextAlignmentOptions.MidlineLeft, UiStyle.Dim);
            _caption.richText = false;

            Plugin.Debug("Compact meter built");
        }
    }
}
