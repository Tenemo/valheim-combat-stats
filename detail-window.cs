using System;
using System.Collections.Generic;
using System.Linq;
using CombatStats.Collect;
using CombatStats.Model;
using CombatStats.Stats;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace CombatStats.Ui
{
    /// <summary>
    /// The window with the whole picture: fight selection, shared totals and player rows that
    /// open into damage types, targets, skills and pets.
    /// </summary>
    internal sealed class DetailWindow : MonoBehaviour
    {
        private const float RefreshInterval = 0.25f;

        private const float Padding = 14f;

        private const float ScrollStep = 48f;

        /// <summary>Kept clear around the window when the screen is smaller than its set size.</summary>
        private const float ScreenMargin = 20f;

        private static readonly Vector2 MinimumSize = new Vector2(600f, 280f);

        private static readonly FightView[] Views = { FightView.Current, FightView.Last, FightView.Overall, FightView.History };

        /// <summary>What an opened player row lists.</summary>
        private enum Breakdown { Types, Targets, Skills, Pets }

        private readonly List<MeterRow> _rows = new List<MeterRow>();

        private readonly List<BreakdownLine> _lines = new List<BreakdownLine>();

        private readonly List<Button> _viewTabs = new List<Button>();

        private readonly List<Button> _channelTabs = new List<Button>();
        private readonly List<GameObject> _columnHeaders = new List<GameObject>();

        private readonly HashSet<long> _expanded = new HashSet<long>();

        private RectTransform _panel;

        private RectTransform _content;

        private RectTransform _viewport;

        private TMP_Text _summary;

        private TMP_Text _empty;

        private TMP_Text _rateHeading;

        private Button _breakdownButton;

        private FightView _view;

        private int _historyIndex;

        private Breakdown _breakdown;

        private CombatChannel _channel = CombatChannel.DamageDealt;

        private float _nextRefresh;

        private float _scrolled;

        private int _scrollFrame = -1;

        private bool _failed;

        private bool _open;

        private bool _seededExpanded;

        /// <summary>True while the window has the screen, which is what stops the player moving.</summary>
        public static bool IsOpen => Instance != null && Instance._open;

        /// <summary>The running window, once the plugin has loaded.</summary>
        public static DetailWindow Instance { get; private set; }

        /// <summary>
        /// The mouse wheel, handed over by the patch that takes it from the game. Several game
        /// systems read the wheel in one frame; only the first reading scrolls.
        /// </summary>
        public static void FeedScrollWheel(float amount)
        {
            DetailWindow window = Instance;
            if (!IsOpen || window._panel == null || window._scrollFrame == Time.frameCount)
            {
                return;
            }

            window._scrollFrame = Time.frameCount;
            window._scrolled = Mathf.Max(0f, window._scrolled - (Mathf.Sign(amount) * ScrollStep));
            window.Layout();
        }

        /// <summary>Opens the window, or closes it when it is already up.</summary>
        public void Toggle()
        {
            if (_open)
            {
                Close();
                return;
            }

            if (_panel == null)
            {
                _panel = UiStyle.Build("CombatStats Detail", ref _failed, Build);
                if (_panel == null)
                {
                    return;
                }
            }

            _open = true;
            _scrolled = 0f;
            _panel.gameObject.SetActive(true);
            Refresh();
        }

        /// <summary>Closes the window and gives the player back their hands.</summary>
        public void Close()
        {
            _open = false;
            SettingsPanel.Instance?.Close();
            if (_panel != null)
            {
                _panel.gameObject.SetActive(false);
            }
        }

        private void Awake()
        {
            Instance = this;
        }

        private void OnDestroy()
        {
            if (Instance == this)
            {
                Instance = null;
            }
        }

        private void Update()
        {
            if (Plugin.Settings == null)
            {
                return;
            }

            if (Shortcut.IsPressed(Plugin.Settings.DetailKey.Value))
            {
                Toggle();
            }

            if (_open && _panel == null)
            {
                // The canvas went with the world; the window is not open any more, whatever the
                // flags say, and leaving them set would keep the player from moving.
                Close();
            }

            if (_open && Time.time >= _nextRefresh)
            {
                _nextRefresh = Time.time + RefreshInterval;
                Refresh();
            }
        }

        private void Refresh()
        {
            // The set size, but never larger than the screen.
            Vector2 wanted = Plugin.Settings.DetailSize.Value;
            Vector2 room = ((RectTransform)_panel.parent).rect.size - (2f * ScreenMargin * Vector2.one);
            var size = new Vector2(Mathf.Min(Mathf.Max(MinimumSize.x, wanted.x), room.x), Mathf.Min(Mathf.Max(MinimumSize.y, wanted.y), room.y));
            if (_panel.sizeDelta != size)
            {
                _panel.sizeDelta = size;
            }

            for (int index = 0; index < _viewTabs.Count; index++)
                UiStyle.MarkSelected(_viewTabs[index], Views[index] == _view);
            for (int index = 0; index < _channelTabs.Count; index++)
                UiStyle.MarkSelected(_channelTabs[index], CombatChannels.All[index] == _channel);
            _rateHeading.text = _channel == CombatChannel.Healing ? "HPS" : _channel == CombatChannel.DamageTaken ? "Taken/s" : "DPS";
            // Healing has no target, skill or pet: it is only ever the healed player's own.
            _breakdownButton.gameObject.SetActive(_channel != CombatChannel.Healing);
            UiStyle.LabelOf(_breakdownButton).text = Label(_breakdown, _channel);
            Layout();
        }

        /// <summary>Fills the rows in and places everything the scroll area holds.</summary>
        private void Layout()
        {
            long local = DamageCollector.LocalId();
            EncounterSnapshot snapshot = Plugin.History.Snapshot(_view, _channel, local, _historyIndex);
            int saved = Plugin.History.HistoryCount;
            string position = _view == FightView.History && saved > 0
                ? "Fight " + (Math.Min(_historyIndex, saved - 1) + 1) + " of " + saved + " | "
                : "";
            _summary.richText = false;
            _summary.text = snapshot.HasEncounter ? position + MeterFormat.Caption(snapshot, MeterFormat.Plain(Localization.instance.Localize(snapshot.Name))) : "";
            if (snapshot.Rows.Count > 0) _summary.text += "\nTotal " + MeterFormat.Number(snapshot.Total) + " | " +
                (_channel == CombatChannel.Healing ? "HPS received " : _channel == CombatChannel.DamageTaken ? "Taken/s " : "DPS ") + MeterFormat.Number(snapshot.Dps);
            foreach (var header in _columnHeaders) header.SetActive(snapshot.Rows.Count > 0);
            _breakdownButton.gameObject.SetActive(snapshot.Rows.Count > 0 && _channel != CombatChannel.Healing);
            _empty.text = MeterFormat.EmptyText(snapshot, _channel);

            float width = _viewport.rect.width;
            float leader = snapshot.Rows.Count > 0 ? snapshot.Rows[0].Total : 0f;
            Breakdown breakdown = _channel == CombatChannel.Healing ? Breakdown.Types : _breakdown;

            if (!_seededExpanded && local != 0L)
            {
                // Once, so the player's own row starts open and can then be closed for good.
                _seededExpanded = true;
                _expanded.Add(local);
            }

            int rowIndex = 0;
            int lineIndex = 0;
            float y = 0f;

            foreach (CombatantRow row in snapshot.Rows)
            {
                MeterRow meter = RowAt(rowIndex++);
                bool open = _expanded.Contains(row.Id);
                meter.SetActive(true);
                meter.Apply(row, leader, width, bars: true, showShare: false, local: row.Id == local, prefix: open ? "- " : "+ ");
                meter.Place(y);
                y += MeterRow.Height(bars: true);

                if (!open)
                {
                    continue;
                }

                BreakdownLine counts = LineAt(lineIndex++);
                counts.SetActive(true);
                counts.ApplySummary(_channel == CombatChannel.Healing
                    ? "Heals " + row.Hits + " | Largest " + MeterFormat.Number(row.Max) + " | Avg/heal " + MeterFormat.Number(row.Average)
                    : "Hits " + row.Hits + (row.Ticks > 0 ? " | Ticks " + row.Ticks : "") + " | Largest " + MeterFormat.Number(row.Max) +
                      (row.Ticks > 0 ? " | Avg/hit or tick " : " | Avg/hit ") + MeterFormat.Number(row.Average) +
                      (row.Overkill > 0f ? " | Overkill " + MeterFormat.Number(row.Overkill) : ""));
                counts.Place(y);
                y += BreakdownLine.Height;
                if (_channel == CombatChannel.Healing) continue;
                if (breakdown == Breakdown.Types)
                {
                    for (int kind = 0; kind < DamageKinds.Count; kind++)
                    {
                        float amount = row.ByKind[kind];
                        if (amount <= 0f) continue;
                        BreakdownLine line = LineAt(lineIndex++);
                        line.SetActive(true);
                        line.Apply((DamageKind)kind, amount, row.Total > 0f ? amount / row.Total : 0f, _channel == CombatChannel.Healing);
                        line.Place(y);
                        y += BreakdownLine.Height;
                    }
                }
                else
                {
                    var values = breakdown == Breakdown.Targets ? row.Targets : breakdown == Breakdown.Skills ? row.Skills : row.Pets;
                    foreach (var value in values.Where(x => x.Value > 0f).OrderByDescending(x => x.Value))
                    {
                        BreakdownLine line = LineAt(lineIndex++);
                        line.SetActive(true);
                        line.ApplyText(breakdown == Breakdown.Skills ? MeterFormat.Skill(value.Key) : value.Key, MeterFormat.Number(value.Value),
                            row.Total > 0f ? MeterFormat.Percent(value.Value / row.Total) : "");
                        line.Place(y);
                        y += BreakdownLine.Height;
                    }
                }

                y += 4f;
            }

            for (int index = rowIndex; index < _rows.Count; index++)
            {
                _rows[index].SetActive(false);
            }

            for (int index = lineIndex; index < _lines.Count; index++)
            {
                _lines[index].SetActive(false);
            }

            _empty.gameObject.SetActive(rowIndex == 0);

            float height = Mathf.Max(y, _viewport.rect.height);
            _content.sizeDelta = new Vector2(0f, height);
            _scrolled = Mathf.Clamp(_scrolled, 0f, Mathf.Max(0f, height - _viewport.rect.height));
            _content.anchoredPosition = new Vector2(0f, _scrolled);
        }

        private void OnRowClicked(long id)
        {
            if (!_expanded.Remove(id))
            {
                _expanded.Add(id);
            }

            Layout();
        }

        private MeterRow RowAt(int index)
        {
            while (_rows.Count <= index)
            {
                _rows.Add(MeterRow.Create(_content, detailed: true, OnRowClicked));
            }

            return _rows[index];
        }

        private BreakdownLine LineAt(int index)
        {
            while (_lines.Count <= index)
            {
                _lines.Add(BreakdownLine.Create(_content));
            }

            return _lines[index];
        }

        private void Build(RectTransform panel)
        {
            _panel = panel;
            _rows.Clear();
            _lines.Clear();
            _viewTabs.Clear();
            _channelTabs.Clear();
            _columnHeaders.Clear();

            UiStyle.Panel(panel, Plugin.Settings.DetailSize.Value, 100, "Combat stats");
            UiStyle.CornerButton(panel, "Close", Padding, 70f, Close);
            UiStyle.CornerButton(panel, "Settings", Padding + 74f, 84f, OpenSettings);

            BuildViewTabs();
            BuildChannelTabs();

            _summary = UiStyle.Text("Summary", panel, UiStyle.SmallSize, TextAlignmentOptions.MidlineLeft, UiStyle.Dim);
            UiStyle.PlaceTopStretch(_summary.rectTransform, Padding, Padding, 100f, 36f);

            TMP_Text header = UiStyle.Text("Header", panel, UiStyle.CaptionSize, TextAlignmentOptions.MidlineLeft, UiStyle.Dim);
            UiStyle.PlaceTopStretch(header.rectTransform, Padding, Padding, 148f, 14f);
            header.text = "Player";
            _columnHeaders.Add(header.gameObject);

            // Pinned at the same distances from the right edge as the columns of a row, so the
            // heads stay over their numbers whatever the window is resized to.
            Column("Total", UiStyle.TotalFrom, UiStyle.TotalTo);
            _rateHeading = Column("DPS", UiStyle.RateFrom, UiStyle.RateTo);
            Column("Share", UiStyle.ShareFrom, 0f);

            var separator = UiStyle.Line("Separator", panel);
            UiStyle.PlaceTopStretch(separator.rectTransform, Padding, Padding, 164f, 1f);
            _columnHeaders.Add(separator.gameObject);

            BuildScroll();

            _empty = UiStyle.Text("Empty", _viewport, UiStyle.BodySize, TextAlignmentOptions.Center, UiStyle.Dim);
            _empty.text = "No fights recorded for this view.";
            _empty.gameObject.SetActive(false);

            panel.gameObject.SetActive(false);
            Plugin.Debug($"Detail window built ({panel.sizeDelta.x}x{panel.sizeDelta.y})");
        }

        private void BuildScroll()
        {
            var viewportGo = new GameObject("Viewport", typeof(RectTransform), typeof(RectMask2D), typeof(Image));
            _viewport = (RectTransform)viewportGo.transform;
            _viewport.SetParent(_panel, false);
            UiStyle.Stretch(_viewport, Padding, Padding, 172f, Padding);

            Image viewportImage = viewportGo.GetComponent<Image>();
            viewportImage.color = new Color(0f, 0f, 0f, 0f);
            viewportImage.raycastTarget = true;

            var contentGo = new GameObject("Content", typeof(RectTransform));
            _content = (RectTransform)contentGo.transform;
            _content.SetParent(_viewport, false);
            _content.anchorMin = new Vector2(0f, 1f);
            _content.anchorMax = new Vector2(1f, 1f);
            _content.pivot = new Vector2(0f, 1f);
            _content.anchoredPosition = Vector2.zero;
            _content.sizeDelta = new Vector2(0f, 0f);
        }

        /// <summary>One head of the column table, over the numbers it names.</summary>
        private TMP_Text Column(string text, float from, float to)
        {
            TMP_Text label = UiStyle.Text("Column" + text, _panel, UiStyle.CaptionSize, TextAlignmentOptions.MidlineRight, UiStyle.Dim);
            label.text = text;
            _columnHeaders.Add(label.gameObject);
            UiStyle.PinRight(label.rectTransform, from + Padding, to + Padding, 148f, 14f);
            return label;
        }

        private void BuildViewTabs()
        {
            float x = Padding;
            foreach (FightView view in Views)
            {
                FightView captured = view;
                Button tab = UiStyle.TextButton("View" + view, _panel, view.ToString(), UiStyle.BodySize, UiStyle.Dim, () =>
                { _view = captured; _scrolled = 0f; Refresh(); });
                UiStyle.PlaceTopLeft((RectTransform)tab.transform, x, 42f, 72f, 22f);
                x += 74f; _viewTabs.Add(tab);
            }
            Button earlier = UiStyle.TextButton("Earlier", _panel, "Earlier", UiStyle.SmallSize, UiStyle.Dim, () =>
            { _view = FightView.History; _historyIndex = Math.Min(Plugin.History.HistoryCount - 1, _historyIndex + 1); _scrolled = 0f; Refresh(); });
            UiStyle.PlaceTopLeft((RectTransform)earlier.transform, x + 8f, 42f, 65f, 22f);
            Button later = UiStyle.TextButton("Later", _panel, "Later", UiStyle.SmallSize, UiStyle.Dim, () =>
            { _view = FightView.History; _historyIndex = Math.Max(0, _historyIndex - 1); _scrolled = 0f; Refresh(); });
            UiStyle.PlaceTopLeft((RectTransform)later.transform, x + 76f, 42f, 65f, 22f);
            Button reset = UiStyle.TextButton("Reset", _panel, "Reset", UiStyle.SmallSize, UiStyle.Dim, () =>
            {
                if (!Plugin.History.Reset() && MessageHud.instance)
                    MessageHud.instance.ShowMessage(MessageHud.MessageType.TopLeft, "Finish the fight before resetting history.");
                _historyIndex = 0; Refresh();
            });
            UiStyle.PlaceTopLeft((RectTransform)reset.transform, x + 150f, 42f, 65f, 22f);
        }

        private void BuildChannelTabs()
        {
            float x = Padding;
            foreach (CombatChannel channel in CombatChannels.All)
            {
                CombatChannel captured = channel;
                Button tab = UiStyle.TextButton("Channel" + channel, _panel, Label(channel), UiStyle.SmallSize, UiStyle.Dim, () =>
                {
                    _channel = captured;
                    _scrolled = 0f;
                    Refresh();
                });

                UiStyle.PlaceTopLeft((RectTransform)tab.transform, x, 70f, 130f, 22f);
                x += 132f;
                _channelTabs.Add(tab);
            }
            _breakdownButton = UiStyle.TextButton("Breakdown", _panel, "Types", UiStyle.SmallSize, UiStyle.Dim, () =>
            { _breakdown = (Breakdown)(((int)_breakdown + 1) % 4); _scrolled = 0f; Refresh(); });
            UiStyle.PlaceTopLeft((RectTransform)_breakdownButton.transform, x + 10f, 70f, 100f, 22f);
        }

        private void OpenSettings()
        {
            SettingsPanel panel = SettingsPanel.Instance ?? gameObject.AddComponent<SettingsPanel>();
            panel.Toggle();
        }

        private static string Label(CombatChannel channel) => channel == CombatChannel.DamageTaken ? "Damage taken" :
            channel == CombatChannel.Healing ? "Healing received" : "Damage done";

        private static string Label(Breakdown breakdown, CombatChannel channel)
        {
            switch (breakdown)
            {
                case Breakdown.Targets: return channel == CombatChannel.DamageTaken ? "Sources" : "Targets";
                case Breakdown.Skills: return "Skills";
                case Breakdown.Pets: return "Pets";
                default: return "Types";
            }
        }

        /// <summary>One line under an opened row: a damage kind, target, skill or pet, or the row's counts.</summary>
        private sealed class BreakdownLine
        {
            public const float Height = 16f;

            private readonly RectTransform _root;

            private readonly TMP_Text _name;

            private readonly TMP_Text _amount;

            private readonly TMP_Text _share;

            private BreakdownLine(RectTransform root, TMP_Text name, TMP_Text amount, TMP_Text share)
            {
                _root = root;
                _name = name;
                _amount = amount;
                _share = share;
            }

            public static BreakdownLine Create(RectTransform parent)
            {
                var go = new GameObject("Breakdown", typeof(RectTransform));
                var root = (RectTransform)go.transform;
                root.SetParent(parent, false);
                root.anchorMin = new Vector2(0f, 1f);
                root.anchorMax = new Vector2(1f, 1f);
                root.pivot = new Vector2(0f, 1f);
                root.sizeDelta = new Vector2(0f, Height);

                TMP_Text name = UiStyle.Text("Name", root, UiStyle.SmallSize, TextAlignmentOptions.MidlineLeft, UiStyle.Dim);
                name.richText = false;
                name.rectTransform.offsetMin = new Vector2(22f, 0f);

                TMP_Text amount = UiStyle.Text("Amount", root, UiStyle.SmallSize, TextAlignmentOptions.MidlineRight, UiStyle.Dim);
                UiStyle.PinRight(amount.rectTransform, UiStyle.TotalFrom, UiStyle.TotalTo, 0f, Height);

                TMP_Text share = UiStyle.Text("Share", root, UiStyle.SmallSize, TextAlignmentOptions.MidlineRight, UiStyle.Dim);
                UiStyle.PinRight(share.rectTransform, UiStyle.ShareFrom, 0f, 0f, Height);

                return new BreakdownLine(root, name, amount, share);
            }

            /// <summary>A row's counts, across the whole line.</summary>
            public void ApplySummary(string text)
            {
                SetName(text, UiStyle.Dim, 0f);
                _amount.text = "";
                _share.text = "";
            }

            public void ApplyText(string name, string amount, string share)
            {
                SetName(MeterFormat.Plain(Localization.instance.Localize(name)), UiStyle.Dim, UiStyle.TotalFrom);
                _amount.text = amount;
                _share.text = share;
            }

            public void Apply(DamageKind kind, float amount, float share, bool healing)
            {
                // Healing is recorded in the type-less slot, which the game calls damage.
                SetName(healing ? "Healing" : KindName(kind), healing ? UiStyle.Dim : UiStyle.Colors[(int)kind], UiStyle.TotalFrom);
                _amount.text = MeterFormat.Number(amount);
                _share.text = MeterFormat.Percent(share);
            }

            public void SetActive(bool active)
            {
                if (_root.gameObject.activeSelf != active)
                {
                    _root.gameObject.SetActive(active);
                }
            }

            public void Place(float y)
            {
                _root.anchoredPosition = new Vector2(0f, -y);
            }

            private void SetName(string text, Color color, float right)
            {
                _name.text = text;
                _name.color = color;
                _name.rectTransform.offsetMax = new Vector2(-right, 0f);
            }

            /// <summary>The game's own word for the kind, or the English one when it has none.</summary>
            private static string KindName(DamageKind kind)
            {
                string key = DamageKinds.LocalizationKey(kind);
                Localization localization = Localization.instance;
                if (localization == null)
                {
                    return DamageKinds.EnglishName(kind);
                }

                string localized = localization.Localize(key);
                return string.IsNullOrEmpty(localized) || localized.StartsWith("[", StringComparison.Ordinal) || localized.Contains("$")
                    ? DamageKinds.EnglishName(kind)
                    : localized;
            }
        }
    }
}
