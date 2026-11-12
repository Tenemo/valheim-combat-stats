using System;
using CombatStats.Model;
using CombatStats.Stats;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace CombatStats.Ui
{
    /// <summary>
    /// One combatant's line: the name on the left, the numbers on the right and, under them, a
    /// bar as long as their share of the leader's damage, split into the kinds it is made of.
    ///
    /// The objects are built once and reused; a refresh only writes text and resizes rectangles.
    /// </summary>
    internal sealed class MeterRow
    {
        public const float LineHeight = 17f;

        public const float BarHeight = 6f;

        /// <summary>The compact row's numbers: total, rate and share, right-aligned.</summary>
        public const float CompactValueWidth = 185f;

        private const float BarGap = 2f;

        private const float RowGap = 4f;

        private readonly TMP_Text _name;

        private readonly TMP_Text _value;

        private readonly TMP_Text _share;

        private readonly TMP_Text _rate;

        private readonly RectTransform _bar;

        private readonly Image[] _segments = new Image[DamageKinds.Count];

        private readonly bool _detailed;

        private MeterRow(RectTransform root, bool detailed, TMP_Text name, TMP_Text value, TMP_Text share, TMP_Text rate, RectTransform bar)
        {
            Root = root;
            _detailed = detailed;
            _name = name;
            _value = value;
            _share = share;
            _rate = rate;
            _bar = bar;
        }

        /// <summary>The rectangle the row lives in; the owner places it.</summary>
        public RectTransform Root { get; }

        /// <summary>The combatant currently shown, so a click knows whose row it hit.</summary>
        public long Id { get; private set; }

        /// <summary>Height of one row, bar included.</summary>
        public static float Height(bool bars)
        {
            return bars ? LineHeight + BarGap + BarHeight + RowGap : LineHeight + RowGap;
        }

        /// <summary>
        /// Builds a row. A detailed row carries total, rate and share in their own columns;
        /// a compact one puts them beside the player name.
        /// </summary>
        public static MeterRow Create(RectTransform parent, bool detailed, Action<long> onClick)
        {
            var go = new GameObject("Row", typeof(RectTransform));
            var root = (RectTransform)go.transform;
            root.SetParent(parent, false);
            root.anchorMin = new Vector2(0f, 1f);
            root.anchorMax = new Vector2(1f, 1f);
            root.pivot = new Vector2(0f, 1f);

            MeterRow row = null;
            if (onClick != null)
            {
                UiStyle.Clickable(go, () => onClick(row.Id));
            }

            row = Build(root, detailed);
            return row;
        }

        /// <summary>Fills the row in and sizes its bar. <paramref name="width"/> is the row's own width.</summary>
        public void Apply(CombatantRow row, float leader, float width, bool bars, bool showShare, bool local, string prefix)
        {
            Id = row.Id;

            string name = string.IsNullOrEmpty(row.Name) ? "..." : row.Name;
            if (Combatant.IsUnownedPet(row.Id) && Localization.instance != null) name = MeterFormat.Plain(Localization.instance.Localize(name));
            _name.text = prefix + name;
            _name.color = local ? UiStyle.Mine : UiStyle.FontColor;

            string total = MeterFormat.Number(row.Total);
            if (_detailed)
            {
                _value.text = total;
                _share.text = MeterFormat.Percent(row.Share);
                _rate.text = MeterFormat.Number(row.Dps);
            }
            else
            {
                _value.text = showShare
                    ? total + "  " + MeterFormat.Number(row.Dps) + "/s  <color=#" + ColorUtility.ToHtmlStringRGB(UiStyle.Dim) + ">" + MeterFormat.Percent(row.Share) + "</color>"
                    : total + "  " + MeterFormat.Number(row.Dps) + "/s";
            }

            _bar.gameObject.SetActive(bars);
            if (bars)
            {
                Layout(row, leader, width);
            }

            Root.sizeDelta = new Vector2(0f, Height(bars));
        }

        /// <summary>Hides the row without destroying it.</summary>
        public void SetActive(bool active)
        {
            if (Root.gameObject.activeSelf != active)
            {
                Root.gameObject.SetActive(active);
            }
        }

        /// <summary>Puts the row <paramref name="y"/> pixels below the top of its parent.</summary>
        public void Place(float y)
        {
            Root.anchoredPosition = new Vector2(0f, -y);
        }

        private static MeterRow Build(RectTransform root, bool detailed)
        {
            TMP_Text name = UiStyle.Text("Name", root, UiStyle.RowSize, TextAlignmentOptions.MidlineLeft, UiStyle.FontColor);
            name.richText = false;
            name.rectTransform.anchorMin = new Vector2(0f, 1f);
            name.rectTransform.anchorMax = new Vector2(1f, 1f);
            name.rectTransform.pivot = new Vector2(0.5f, 1f);
            name.rectTransform.offsetMin = new Vector2(0f, -LineHeight);
            name.rectTransform.offsetMax = new Vector2(detailed ? -UiStyle.TotalFrom : -CompactValueWidth, 0f);

            TMP_Text value = UiStyle.Text("Value", root, UiStyle.RowSize, TextAlignmentOptions.MidlineRight, UiStyle.FontColor);
            TMP_Text share = null;
            TMP_Text rate = null;

            if (detailed)
            {
                UiStyle.PinRight(value.rectTransform, UiStyle.TotalFrom, UiStyle.TotalTo, 0f, LineHeight);

                share = UiStyle.Text("Share", root, UiStyle.SmallSize, TextAlignmentOptions.MidlineRight, UiStyle.Dim);
                UiStyle.PinRight(share.rectTransform, UiStyle.ShareFrom, 0f, 0f, LineHeight);

                rate = UiStyle.Text("Rate", root, UiStyle.SmallSize, TextAlignmentOptions.MidlineRight, UiStyle.Dim);
                UiStyle.PinRight(rate.rectTransform, UiStyle.RateFrom, UiStyle.RateTo, 0f, LineHeight);
            }
            else
            {
                UiStyle.PinRight(value.rectTransform, CompactValueWidth, 0f, 0f, LineHeight);
            }

            var barGo = new GameObject("Bar", typeof(RectTransform));
            var bar = (RectTransform)barGo.transform;
            bar.SetParent(root, false);
            bar.anchorMin = new Vector2(0f, 1f);
            bar.anchorMax = new Vector2(1f, 1f);
            bar.pivot = new Vector2(0f, 1f);
            bar.anchoredPosition = new Vector2(0f, -(LineHeight + BarGap));
            bar.sizeDelta = new Vector2(0f, BarHeight);

            Image back = UiStyle.Box("Back", bar, UiStyle.BarTrack);
            back.sprite = UiStyle.Flat();
            UiStyle.Stretch(back.rectTransform, 0f, 0f, 0f, 0f);

            var built = new MeterRow(root, detailed, name, value, share, rate, bar);
            for (int kind = 0; kind < DamageKinds.Count; kind++)
            {
                Image segment = UiStyle.Box("Kind" + kind, bar, Color.white);
                segment.sprite = UiStyle.Flat();
                RectTransform rect = segment.rectTransform;
                rect.anchorMin = new Vector2(0f, 0f);
                rect.anchorMax = new Vector2(0f, 1f);
                rect.pivot = new Vector2(0f, 0.5f);
                rect.offsetMin = new Vector2(rect.offsetMin.x, 0f);
                rect.offsetMax = new Vector2(rect.offsetMax.x, 0f);
                segment.gameObject.SetActive(false);
                built._segments[kind] = segment;
            }

            return built;
        }

        private void Layout(CombatantRow row, float leader, float width)
        {
            float full = leader > 0f ? Mathf.Clamp01(row.Total / leader) * width : 0f;
            Color[] colors = UiStyle.Colors;
            float offset = 0f;

            for (int kind = 0; kind < DamageKinds.Count; kind++)
            {
                Image segment = _segments[kind];
                float amount = row.ByKind[kind];
                if (amount <= 0f || row.Total <= 0f || full <= 0f)
                {
                    if (segment.gameObject.activeSelf)
                    {
                        segment.gameObject.SetActive(false);
                    }

                    continue;
                }

                float part = full * (amount / row.Total);
                RectTransform rect = segment.rectTransform;
                rect.anchoredPosition = new Vector2(offset, 0f);
                rect.sizeDelta = new Vector2(part, 0f);
                segment.color = colors[kind];
                if (!segment.gameObject.activeSelf)
                {
                    segment.gameObject.SetActive(true);
                }

                offset += part;
            }
        }
    }
}
