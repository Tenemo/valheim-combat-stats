using System;
using CombatStats.Model;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace CombatStats.Ui
{
    /// <summary>
    /// The look the windows borrow from the game, and the small pieces they are built from.
    ///
    /// Nothing here ships a font or a sprite: the font, its material and its colour come from the
    /// inventory, so the meter reads as part of the game rather than as a panel pasted over it.
    /// </summary>
    internal static class UiStyle
    {
        /// <summary>Window titles.</summary>
        public const float TitleSize = 20f;

        /// <summary>Player rows: names and totals.</summary>
        public const float RowSize = 15f;

        /// <summary>Buttons, setting names and values.</summary>
        public const float BodySize = 14f;

        /// <summary>Secondary text: tabs, summaries, rates, shares and breakdown lines.</summary>
        public const float SmallSize = 13f;

        /// <summary>The smallest text on screen: captions, column heads and hints.</summary>
        public const float CaptionSize = 12f;

        /// <summary>
        /// The detail table's number columns, as distances from a row's right edge. Column heads,
        /// player rows and breakdown lines share them, so numbers stay under their heads at any width.
        /// </summary>
        public const float TotalFrom = 250f, TotalTo = 152f, RateFrom = 145f, RateTo = 75f, ShareFrom = 68f;

        /// <summary>The colour of the dim, secondary text: captions, percentages, column heads.</summary>
        public static readonly Color Dim = new Color(0.659f, 0.569f, 0.416f, 1f);

        /// <summary>The player's own row and the selected tab, a shade brighter than everything else.</summary>
        public static readonly Color Mine = new Color(0.93f, 0.85f, 0.65f, 1f);

        /// <summary>The hairline above a HUD element.</summary>
        public static readonly Color Hairline = new Color(0.71f, 0.59f, 0.39f, 0.35f);

        /// <summary>The compact meter's backing; its opacity is a setting.</summary>
        public static readonly Color MeterBackground = new Color(0.03f, 0.028f, 0.024f, 1f);

        /// <summary>The empty part of a damage bar.</summary>
        public static readonly Color BarTrack = new Color(0f, 0f, 0f, 0.45f);

        /// <summary>Separators and the lines under headings.</summary>
        public static readonly Color Rule = new Color(0.45f, 0.38f, 0.25f, 0.5f);

        /// <summary>A window's background when the inventory's own cannot be read.</summary>
        public static readonly Color PanelFallback = new Color(0.08f, 0.07f, 0.06f, 0.94f);

        /// <summary>The wash behind anything clickable under the pointer, and while it is pressed.</summary>
        public static readonly Color Hover = new Color(1f, 1f, 1f, 0.12f), Pressed = new Color(1f, 1f, 1f, 0.2f);

        private static readonly Color Clear = new Color(1f, 1f, 1f, 0f);

        private static Color[] _colors = new Color[DamageKinds.Count];

        private static string _colorSource = "^@";

        private static Sprite _fade;

        private static Sprite _flat;

        /// <summary>The inventory's font, once it has been read.</summary>
        public static TMP_FontAsset Font { get; private set; }

        /// <summary>Its material, which is what gives the text its outline.</summary>
        public static Material FontMaterial { get; private set; }

        /// <summary>Its colour.</summary>
        public static Color FontColor { get; private set; } = new Color(0.85f, 0.79f, 0.65f, 1f);

        /// <summary>The colour of each damage kind, re-parsed whenever the setting changes.</summary>
        public static Color[] Colors
        {
            get
            {
                string source = Plugin.Settings != null ? Plugin.Settings.Colors.Value ?? string.Empty : string.Empty;
                if (source != _colorSource)
                {
                    _colorSource = source;
                    _colors = Plugin.Settings != null ? Plugin.Settings.ParseColors() : _colors;
                }

                return _colors;
            }
        }

        /// <summary>Reads the font and colour from the inventory. False while the game UI is not up.</summary>
        public static bool ReadStyle()
        {
            InventoryGui gui = InventoryGui.instance;
            TMP_Text sample = gui != null && gui.m_takeAllButton != null
                ? gui.m_takeAllButton.GetComponentInChildren<TMP_Text>(true)
                : null;

            if (sample == null)
            {
                return false;
            }

            Font = sample.font;
            FontMaterial = sample.fontSharedMaterial;
            FontColor = sample.color;
            return true;
        }

        /// <summary>The canvas the game's own HUD lives on.</summary>
        public static Transform FindCanvas()
        {
            global::Hud hud = global::Hud.instance;
            if (hud != null)
            {
                Canvas canvas = hud.GetComponentInParent<Canvas>();
                if (canvas != null)
                {
                    return canvas.transform;
                }
            }

            InventoryGui gui = InventoryGui.instance;
            if (gui != null)
            {
                Canvas canvas = gui.GetComponentInParent<Canvas>();
                if (canvas != null)
                {
                    return canvas.transform;
                }
            }

            return null;
        }

        /// <summary>
        /// Builds a window under the game's HUD canvas the first time it is needed. Returns null
        /// while the game UI is not up, and for good after a failed build, which is logged and
        /// leaves nothing half built behind.
        /// </summary>
        public static RectTransform Build(string name, ref bool failed, Action<RectTransform> build)
        {
            if (failed)
            {
                return null;
            }

            Transform canvas = FindCanvas();
            if (canvas == null || !ReadStyle())
            {
                return null;
            }

            var root = (RectTransform)new GameObject(name, typeof(RectTransform)).transform;
            root.SetParent(canvas, false);
            try
            {
                build(root);
                return root;
            }
            catch (Exception exception)
            {
                failed = true;
                UnityEngine.Object.Destroy(root.gameObject);
                Plugin.Log.LogError($"Could not build {name}; it stays hidden until restart: {exception}");
                return null;
            }
        }

        /// <summary>
        /// Makes a built root a centred window over the HUD, with its own sorting, the inventory's
        /// background and a title.
        /// </summary>
        public static void Panel(RectTransform root, Vector2 size, int sortingOrder, string title)
        {
            Canvas own = root.gameObject.AddComponent<Canvas>();
            own.overrideSorting = true;
            own.sortingOrder = sortingOrder;
            root.gameObject.AddComponent<GraphicRaycaster>();
            StylePanel(root.gameObject.AddComponent<Image>());

            root.anchorMin = new Vector2(0.5f, 0.5f);
            root.anchorMax = new Vector2(0.5f, 0.5f);
            root.pivot = new Vector2(0.5f, 0.5f);
            root.anchoredPosition = Vector2.zero;
            root.sizeDelta = size;

            TMP_Text heading = Text("Title", root, TitleSize, TextAlignmentOptions.MidlineLeft, FontColor);
            PlaceTopLeft(heading.rectTransform, 16f, 10f, 400f, 26f);
            heading.text = title;
        }

        /// <summary>A text button in a window's top-right corner, <paramref name="right"/> pixels from the edge.</summary>
        public static Button CornerButton(RectTransform panel, string label, float right, float width, Action onClick)
        {
            Button button = TextButton(label, panel, label, BodySize, Dim, onClick);
            var rect = (RectTransform)button.transform;
            rect.anchorMin = new Vector2(1f, 1f);
            rect.anchorMax = new Vector2(1f, 1f);
            rect.pivot = new Vector2(1f, 1f);
            rect.anchoredPosition = new Vector2(-right, -12f);
            rect.sizeDelta = new Vector2(width, 22f);
            return button;
        }

        /// <summary>A label in the game's font, stretched over its parent unless it is placed later.</summary>
        public static TMP_Text Text(string name, RectTransform parent, float fontSize, TextAlignmentOptions alignment, Color color)
        {
            var go = new GameObject(name, typeof(RectTransform));
            var rect = (RectTransform)go.transform;
            rect.SetParent(parent, false);

            var text = go.AddComponent<TextMeshProUGUI>();
            if (Font != null)
            {
                text.font = Font;
                if (FontMaterial != null)
                {
                    text.fontSharedMaterial = FontMaterial;
                }
            }

            text.fontSize = fontSize;
            text.alignment = alignment;
            text.textWrappingMode = TextWrappingModes.NoWrap;
            text.overflowMode = TextOverflowModes.Ellipsis;
            text.raycastTarget = false;
            text.richText = true;
            text.color = color;

            rect.anchorMin = new Vector2(0f, 0f);
            rect.anchorMax = new Vector2(1f, 1f);
            rect.offsetMin = Vector2.zero;
            rect.offsetMax = Vector2.zero;
            return text;
        }

        /// <summary>A plain coloured rectangle: a bar, a segment, a hairline.</summary>
        public static Image Box(string name, RectTransform parent, Color color)
        {
            var go = new GameObject(name, typeof(RectTransform));
            var rect = (RectTransform)go.transform;
            rect.SetParent(parent, false);

            var image = go.AddComponent<Image>();
            image.color = color;
            image.raycastTarget = false;
            return image;
        }

        /// <summary>A one-pixel line in the rule colour.</summary>
        public static Image Line(string name, RectTransform parent)
        {
            Image line = Box(name, parent, Rule);
            line.sprite = Flat();
            return line;
        }

        /// <summary>
        /// Makes an object clickable with no frame of its own: a wash shows only under the pointer.
        /// </summary>
        public static Button Clickable(GameObject go, Action onClick)
        {
            // Configured while inactive, so the button's first tint is already the clear one.
            bool active = go.activeSelf;
            go.SetActive(false);
            var background = go.AddComponent<Image>();
            // The button's tint multiplies this colour, so it is opaque and the tint decides what shows.
            background.color = Color.white;
            background.raycastTarget = true;

            var button = go.AddComponent<Button>();
            ColorBlock colors = button.colors;
            colors.normalColor = Clear;
            colors.highlightedColor = Hover;
            colors.pressedColor = Pressed;
            colors.selectedColor = Clear;
            colors.disabledColor = Clear;
            colors.fadeDuration = 0.05f;
            button.colors = colors;
            button.targetGraphic = background;
            button.onClick.AddListener(() => onClick());
            go.SetActive(active);
            return button;
        }

        /// <summary>
        /// A button whose label is the button, the same shape the game uses for its list headers.
        /// </summary>
        public static Button TextButton(string name, RectTransform parent, string label, float fontSize, Color color, Action onClick)
        {
            var go = new GameObject(name, typeof(RectTransform));
            var rect = (RectTransform)go.transform;
            rect.SetParent(parent, false);
            Button button = Clickable(go, onClick);

            TMP_Text text = Text("Label", rect, fontSize, TextAlignmentOptions.Center, color);
            text.text = label;
            text.rectTransform.offsetMin = new Vector2(4f, 0f);
            text.rectTransform.offsetMax = new Vector2(-4f, 0f);

            return button;
        }

        /// <summary>The label of a button built by <see cref="TextButton"/>.</summary>
        public static TMP_Text LabelOf(Button button)
        {
            return button.GetComponentInChildren<TMP_Text>(true);
        }

        /// <summary>Shows whether a tab is the chosen one: a bright label over an underline.</summary>
        public static void MarkSelected(Button tab, bool selected)
        {
            LabelOf(tab).color = selected ? Mine : Dim;
            Transform mark = tab.transform.Find("Selected");
            if (mark == null)
            {
                Image line = Box("Selected", (RectTransform)tab.transform, Mine);
                line.sprite = Flat();
                RectTransform rect = line.rectTransform;
                rect.anchorMin = new Vector2(0f, 0f);
                rect.anchorMax = new Vector2(1f, 0f);
                rect.pivot = new Vector2(0.5f, 0f);
                rect.offsetMin = new Vector2(6f, 0f);
                rect.offsetMax = new Vector2(-6f, 2f);
                mark = rect;
            }

            if (mark.gameObject.activeSelf != selected)
            {
                mark.gameObject.SetActive(selected);
            }
        }

        /// <summary>
        /// The backing of the compact window: solid at the top, a little thinner towards the
        /// bottom, which is how the game's own HUD panels sit on the world.
        /// </summary>
        public static Sprite Fade()
        {
            if (_fade != null)
            {
                return _fade;
            }

            var texture = new Texture2D(1, 64, TextureFormat.ARGB32, mipChain: false)
            {
                wrapMode = TextureWrapMode.Clamp,
                filterMode = FilterMode.Bilinear,
            };

            for (int y = 0; y < 64; y++)
            {
                // Row 0 is the bottom of the texture, so the thinner end is at the bottom.
                float alpha = Mathf.Lerp(0.78f, 1f, y / 63f);
                texture.SetPixel(0, y, new Color(1f, 1f, 1f, alpha));
            }

            texture.Apply();
            _fade = Sprite.Create(texture, new Rect(0f, 0f, 1f, 64f), new Vector2(0.5f, 0.5f), 100f, 0u, SpriteMeshType.FullRect);
            return _fade;
        }

        /// <summary>A one-pixel white sprite, for bars that should not inherit a panel's frame.</summary>
        public static Sprite Flat()
        {
            if (_flat != null)
            {
                return _flat;
            }

            var texture = new Texture2D(1, 1, TextureFormat.ARGB32, mipChain: false)
            {
                wrapMode = TextureWrapMode.Clamp,
                filterMode = FilterMode.Point,
            };

            texture.SetPixel(0, 0, Color.white);
            texture.Apply();
            _flat = Sprite.Create(texture, new Rect(0f, 0f, 1f, 1f), new Vector2(0.5f, 0.5f), 100f, 0u, SpriteMeshType.FullRect);
            return _flat;
        }

        /// <summary>Gives a window the same background the inventory panel has.</summary>
        public static void StylePanel(Image image)
        {
            InventoryGui gui = InventoryGui.instance;
            Image source = gui != null && gui.m_player != null ? gui.m_player.GetComponent<Image>() : null;
            if (source != null && source.sprite != null)
            {
                image.sprite = source.sprite;
                image.type = source.type;
                image.color = source.color;
                return;
            }

            image.color = PanelFallback;
        }

        /// <summary>Pins a rect to the top-left of its parent, measured in pixels from that corner.</summary>
        public static void PlaceTopLeft(RectTransform rect, float x, float y, float width, float height)
        {
            rect.anchorMin = new Vector2(0f, 1f);
            rect.anchorMax = new Vector2(0f, 1f);
            rect.pivot = new Vector2(0f, 1f);
            rect.anchoredPosition = new Vector2(x, -y);
            rect.sizeDelta = new Vector2(width, height);
        }

        /// <summary>
        /// Pins a rect between two distances from its parent's right edge, <paramref name="top"/>
        /// pixels below the parent's top, so it keeps its place when the parent is resized.
        /// </summary>
        public static void PinRight(RectTransform rect, float from, float to, float top, float height)
        {
            rect.anchorMin = new Vector2(1f, 1f);
            rect.anchorMax = new Vector2(1f, 1f);
            rect.pivot = new Vector2(1f, 1f);
            rect.offsetMin = new Vector2(-from, -(top + height));
            rect.offsetMax = new Vector2(-to, -top);
        }

        /// <summary>
        /// Pins a rect to the top of its parent and lets it follow the parent's width, which is
        /// what keeps a window's contents in place when the window is resized.
        /// </summary>
        public static void PlaceTopStretch(RectTransform rect, float left, float right, float top, float height)
        {
            rect.anchorMin = new Vector2(0f, 1f);
            rect.anchorMax = new Vector2(1f, 1f);
            rect.pivot = new Vector2(0.5f, 1f);
            rect.offsetMin = new Vector2(left, 0f);
            rect.offsetMax = new Vector2(-right, 0f);
            rect.sizeDelta = new Vector2(rect.sizeDelta.x, height);
            rect.anchoredPosition = new Vector2(rect.anchoredPosition.x, -top);
        }

        /// <summary>Stretches a rect over its parent with the given margins.</summary>
        public static void Stretch(RectTransform rect, float left, float right, float top, float bottom)
        {
            rect.anchorMin = Vector2.zero;
            rect.anchorMax = Vector2.one;
            rect.pivot = new Vector2(0.5f, 0.5f);
            rect.offsetMin = new Vector2(left, bottom);
            rect.offsetMax = new Vector2(-right, -top);
        }

        /// <summary>
        /// Pins a window to a corner or edge of the screen, <paramref name="inset"/> pixels in from
        /// that corner, and keeps it whole on screen however far it is set in. A middle edge
        /// centres the window vertically.
        /// </summary>
        public static void Anchor(RectTransform rect, ScreenAnchor anchor, Vector2 inset)
        {
            Vector2 point;
            switch (anchor)
            {
                case ScreenAnchor.TopRight: point = new Vector2(1f, 1f); break;
                case ScreenAnchor.MiddleLeft: point = new Vector2(0f, 0.5f); break;
                case ScreenAnchor.MiddleRight: point = new Vector2(1f, 0.5f); break;
                case ScreenAnchor.BottomLeft: point = new Vector2(0f, 0f); break;
                case ScreenAnchor.BottomRight: point = new Vector2(1f, 0f); break;
                default: point = new Vector2(0f, 1f); break;
            }

            rect.anchorMin = point;
            rect.anchorMax = point;
            rect.pivot = point;

            Vector2 room = ((RectTransform)rect.parent).rect.size - Vector2.Scale(rect.sizeDelta, rect.localScale);
            float x = Mathf.Clamp(inset.x, 0f, Mathf.Max(0f, room.x));
            float y = point.y == 0.5f ? 0f : Mathf.Clamp(inset.y, 0f, Mathf.Max(0f, room.y));
            rect.anchoredPosition = new Vector2(point.x == 0f ? x : -x, point.y == 0f ? y : -y);
        }
    }
}
