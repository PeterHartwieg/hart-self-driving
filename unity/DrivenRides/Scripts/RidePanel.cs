#nullable enable
using System;
using System.Collections.Generic;
using System.Globalization;
using TMPro;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;
using Object = UnityEngine.Object;

namespace DrivenRides
{
    // The ride panel: a small card under the game's Objectives panel with the distance left, the clock slider, the
    // speed buttons and Skip. Shown while the ride drives or stops (Ride.ShowPanel), so not during the bridge fade or
    // after Skip. Built from uGUI on the HUD's canvas the first time a ride shows it, and again after a city load.
    // All sizes are design pixels at 1920x1080; the root is scaled to the HUD's canvas units.
    internal sealed class RidePanel : MonoBehaviour
    {
        private static readonly float[] Speeds = { 1f, 2f, 3f };

        // The design (Option B, "small card").
        private const float Left = 12f, Top = 324f, Width = 345f;
        private const float HeaderHeight = 34f, Pad = 18f, BodyTop = 12f, RowGap = 12f, BodyBottom = 14f;
        private const float ClockRow = 20f, ClockLabelWidth = 84f, ClockGap = 10f, SliderWidth = 132f;
        private const float ButtonHeight = 34f, SpeedWidth = 46f, SpeedGap = 4f, SkipPadding = 22f;
        private const float Height = HeaderHeight + BodyTop + ClockRow + RowGap + ButtonHeight + BodyBottom;
        private const float Corner = 4f, ButtonCorner = 3f, TrackHeight = 6f, HandleSize = 16f;
        private const float ShadowBlur = 10f, ShadowDrop = 2f, ShadowAlpha = 0.35f;
        private static readonly Color Body = new Color32(48, 57, 64, 235);
        private static readonly Color HeaderTop = Hex(0xD1D9DC), HeaderBottom = Hex(0xB5C0C6), HeaderText = Hex(0x1E252D);
        private static readonly Color ClockText = Hex(0xC9D1D6), Track = Hex(0x48525C), Handle = Hex(0xF2F6F6);
        private static readonly Color Speed = Hex(0x3A444E), SpeedText = Hex(0xD0D6DB);
        private static readonly Color Picked = Hex(0xE3E8EB), PickedText = Hex(0x1E252D);
        private static readonly Color Accent = Hex(0x2978BD), AccentHover = Hex(0x368ED5);

        private GameObject? root;
        private readonly List<Texture2D> textures = new List<Texture2D>();
        private TextMeshProUGUI? header;
        private TextMeshProUGUI? clockLabel;
        private Slider? slider;
        private readonly PanelButton?[] speedButtons = new PanelButton?[3];
        private PanelButton? skip;
        private int shownSpeed = -1;
        private string shownLeft = "";
        private int shownClock = -1;
        private bool clockDragged;
        private bool buildFailed;
        private float nextBuildTry;

        private void Update()
        {
            var ride = Ride.Active;
            var show = ride != null && ride.ShowPanel;
            if (!show)
            {
                if (root != null && root.activeSelf) Hide();
                return;
            }
            if (root == null && !Build()) return;
            if (!root!.activeSelf)
            {
                root.SetActive(true);
                Relabel();
            }
            try { Refresh(ride!); }
            catch (Exception ex) { Log.Error("Ride panel: " + ex); }
        }

        private void Hide()
        {
            // A drag the panel didn't see let go of still ends up on disk.
            if (clockDragged) { Options.FlushClock(); clockDragged = false; }
            root!.SetActive(false);
        }

        private void Refresh(Ride ride)
        {
            var left = ride.PanelStatus ?? (ride.PanelLeftMetres / 1000.0).ToString("0.0", CultureInfo.CurrentCulture);
            if (left != shownLeft)
            {
                shownLeft = left;
                header!.text = ride.PanelStatus != null ? Loc.Text(ride.PanelStatus) : Loc.Format("drivenrides:panel_left", ("km", left));
            }
            // The options page can change the clock too; the slider follows it unless it is being dragged.
            if (!clockDragged && slider != null && Mathf.RoundToInt(slider.value) != Options.ClockPercent)
                slider.SetValueWithoutNotify(Options.ClockPercent);
            if (Options.ClockPercent != shownClock) ShowClock(Options.ClockPercent);
            var speed = Array.FindIndex(Speeds, s => Mathf.Approximately(ride.RideSpeed, s));
            if (speed != shownSpeed)
            {
                shownSpeed = speed;
                for (var i = 0; i < speedButtons.Length; i++)
                {
                    var picked = i == speed;
                    speedButtons[i]?.Paint(picked ? Picked : Speed, Brighter(picked ? Picked : Speed), picked ? PickedText : SpeedText);
                }
            }
        }

        private void ShowClock(int percent)
        {
            shownClock = percent;
            // Tabular figures: each digit as wide as the next, so the label doesn't shimmer while dragging.
            if (clockLabel != null)
                clockLabel.text = Loc.Format("drivenrides:panel_clock", ("percent", $"<mspace=0.6em>{percent}</mspace>"));
        }

        // Texts that only change with the language: read again each time the panel shows.
        private void Relabel()
        {
            shownLeft = "";
            shownClock = -1;
            for (var i = 0; i < speedButtons.Length; i++) speedButtons[i]?.SetText(Loc.Text($"drivenrides:panel_speed_{i + 1}"));
            if (skip != null)
            {
                skip.SetText(Loc.Text("drivenrides:panel_skip"));
                var rect = (RectTransform)skip.transform;
                rect.sizeDelta = new Vector2(Mathf.Ceil(skip.Label.preferredWidth) + 2f * SkipPadding, ButtonHeight);
            }
        }

        private void OnClock(float value)
        {
            var percent = Mathf.RoundToInt(value);
            if (percent == Options.ClockPercent) return;
            Options.SetClock(percent);
            clockDragged = true;
            ShowClock(Options.ClockPercent);
        }

        private void OnClockReleased()
        {
            if (!clockDragged) return;
            Options.FlushClock();
            clockDragged = false;
        }

        // ---- Building ----

        private bool Build()
        {
            if (buildFailed || Time.unscaledTime < nextBuildTry) return false;
            nextBuildTry = Time.unscaledTime + 1f;
            try
            {
                var slot = GameHud.Slot();
                if (slot == null) return false;
                Discard();
                var (parent, index, canvas) = slot.Value;
                var (regular, bold, fontFrom) = GameHud.Fonts();
                if (regular == null) Log.Error("Ride panel: no TextMeshPro font found; the panel's texts use TMP's default.");
                var unitsPerPixel = GameHud.UnitsPerDesignPixel;
                // Sprites are drawn at four texels per design pixel, sharp up to 4K at UI zoom 2.
                const float texels = 4f;
                var ppuMultiplier = texels * canvas.referencePixelsPerUnit / 100f;

                root = new GameObject("DrivenRides.RidePanel", typeof(RectTransform));
                root.SetActive(false);
                var rootRect = (RectTransform)root.transform;
                rootRect.SetParent(parent, false);
                rootRect.SetSiblingIndex(Mathf.Min(index, parent.childCount - 1));
                rootRect.anchorMin = rootRect.anchorMax = rootRect.pivot = new Vector2(0f, 1f);
                rootRect.localScale = Vector3.one * unitsPerPixel;
                rootRect.anchoredPosition = new Vector2(Left, -Top) * unitsPerPixel;
                rootRect.sizeDelta = new Vector2(Width, Height);
                // A layout group on the canvas must not move it.
                root.AddComponent<LayoutElement>().ignoreLayout = true;

                var rounded = Sprite9(RoundedRect(Corner, texels, top: true, bottom: true, Color.white, Color.white, plain: true), Corner, texels, vertical: true);
                var buttonShape = Sprite9(RoundedRect(ButtonCorner, texels, top: true, bottom: true, Color.white, Color.white, plain: true), ButtonCorner, texels, vertical: true);
                var pill = Sprite9(RoundedRect(TrackHeight / 2f, texels, top: true, bottom: true, Color.white, Color.white, plain: true), TrackHeight / 2f, texels, vertical: true);
                var headerShape = Sprite9(RoundedRect(Corner, texels, top: true, bottom: false, HeaderTop, HeaderBottom, plain: false, height: HeaderHeight), Corner, texels, vertical: false);
                var circle = SpriteWhole(Circle(HandleSize, texels));
                var shadow = Sprite9(Shadow(texels, out var shadowBorder), shadowBorder, texels, vertical: true);

                // Shadow, card, header.
                var shadowImage = Box(root.transform, "Shadow", shadow, new Color(0f, 0f, 0f, ShadowAlpha), ppuMultiplier);
                Place(shadowImage.rectTransform, -ShadowBlur * 1.5f, -ShadowBlur * 1.5f + ShadowDrop, Width + ShadowBlur * 3f, Height + ShadowBlur * 3f);
                shadowImage.raycastTarget = false;
                var card = Box(root.transform, "Card", rounded, Body, ppuMultiplier);
                Place(card.rectTransform, 0f, 0f, Width, Height);
                var headerImage = Box(root.transform, "Header", headerShape, Color.white, ppuMultiplier);
                Place(headerImage.rectTransform, 0f, 0f, Width, HeaderHeight);
                headerImage.raycastTarget = false;
                header = Label(headerImage.transform, "Text", bold ?? regular, bold != null ? FontStyles.UpperCase : FontStyles.UpperCase | FontStyles.Bold, 15f, HeaderText);
                Place(header.rectTransform, Pad, 0f, Width - 2f * Pad, HeaderHeight);

                // Clock row.
                var rowTop = HeaderHeight + BodyTop;
                clockLabel = Label(root.transform, "Clock", regular, FontStyles.Normal, 14f, ClockText);
                clockLabel.fontWeight = FontWeight.Medium;
                Place(clockLabel.rectTransform, Pad, rowTop, ClockLabelWidth, ClockRow);
                slider = MakeSlider(root.transform, pill, circle, ppuMultiplier);
                Place((RectTransform)slider.transform, Pad + ClockLabelWidth + ClockGap, rowTop, SliderWidth, ClockRow);

                // Speed buttons and Skip.
                var buttonTop = rowTop + ClockRow + RowGap;
                for (var i = 0; i < Speeds.Length; i++)
                {
                    var speed = Speeds[i];
                    var button = PanelButton.Make(root.transform, $"Speed{i + 1}", buttonShape, ppuMultiplier, bold ?? regular, bold == null);
                    Place((RectTransform)button.transform, Pad + i * (SpeedWidth + SpeedGap), buttonTop, SpeedWidth, ButtonHeight);
                    button.Clicked = () => Ride.Active?.SetSpeed(speed);
                    button.Paint(Speed, Brighter(Speed), SpeedText);
                    speedButtons[i] = button;
                }
                skip = PanelButton.Make(root.transform, "Skip", buttonShape, ppuMultiplier, bold ?? regular, bold == null);
                var skipRect = (RectTransform)skip.transform;
                skipRect.anchorMin = skipRect.anchorMax = skipRect.pivot = new Vector2(1f, 1f);
                skipRect.anchoredPosition = new Vector2(-Pad, -buttonTop);
                skipRect.sizeDelta = new Vector2(80f, ButtonHeight);
                skip.Clicked = () => Ride.Active?.RequestSkip();
                skip.Paint(Accent, AccentHover, Color.white);
                shownSpeed = -1;

                Log.Trace($"Ride panel built on canvas '{canvas.name}' ({unitsPerPixel:0.##} units per design pixel, sibling {rootRect.GetSiblingIndex()}), font {regular?.name ?? "TMP default"} from {fontFrom}, bold {(bold != null ? bold.name : "by style")}.");
                return true;
            }
            catch (Exception ex)
            {
                buildFailed = true;
                Log.Error("Ride panel: building it failed, so rides have no panel: " + ex);
                Discard();
                return false;
            }
        }

        private Slider MakeSlider(Transform parent, Sprite pill, Sprite circle, float ppu)
        {
            var go = new GameObject("ClockSlider", typeof(RectTransform));
            go.transform.SetParent(parent, false);
            // The whole row height takes clicks, not only the 6 px track.
            var hit = go.AddComponent<Image>();
            hit.color = new Color(0f, 0f, 0f, 0f);

            var track = Box(go.transform, "Track", pill, Track, ppu);
            Middle(track.rectTransform, 0f, 0f, TrackHeight);
            track.raycastTarget = false;

            var fillArea = new GameObject("FillArea", typeof(RectTransform));
            fillArea.transform.SetParent(go.transform, false);
            Middle((RectTransform)fillArea.transform, HandleSize / 2f, HandleSize / 2f, TrackHeight);
            var fill = Box(fillArea.transform, "Fill", pill, Accent, ppu);
            fill.raycastTarget = false;
            // From the track's start to the handle's centre.
            fill.rectTransform.anchorMin = new Vector2(0f, 0f);
            fill.rectTransform.anchorMax = new Vector2(0f, 1f);
            fill.rectTransform.offsetMin = new Vector2(-HandleSize / 2f, 0f);
            fill.rectTransform.offsetMax = Vector2.zero;

            var handleArea = new GameObject("HandleArea", typeof(RectTransform));
            handleArea.transform.SetParent(go.transform, false);
            var handleAreaRect = (RectTransform)handleArea.transform;
            handleAreaRect.anchorMin = Vector2.zero;
            handleAreaRect.anchorMax = Vector2.one;
            handleAreaRect.offsetMin = new Vector2(HandleSize / 2f, 0f);
            handleAreaRect.offsetMax = new Vector2(-HandleSize / 2f, 0f);
            var handle = Box(handleArea.transform, "Handle", circle, Handle, 1f);
            handle.type = Image.Type.Simple;
            handle.rectTransform.sizeDelta = new Vector2(HandleSize, HandleSize - ClockRow);
            var drop = handle.gameObject.AddComponent<Shadow>();
            drop.effectColor = new Color(0f, 0f, 0f, 0.5f);
            drop.effectDistance = new Vector2(0f, -1f);

            var s = go.AddComponent<Slider>();
            s.transition = Selectable.Transition.None;
            s.navigation = new Navigation { mode = Navigation.Mode.None };
            s.targetGraphic = handle;
            s.fillRect = fill.rectTransform;
            s.handleRect = handle.rectTransform;
            s.direction = Slider.Direction.LeftToRight;
            s.minValue = 0f;
            s.maxValue = 100f;
            s.wholeNumbers = true;
            s.SetValueWithoutNotify(Options.ClockPercent);
            s.onValueChanged.AddListener(OnClock);
            var release = go.AddComponent<PointerRelease>();
            release.Released = OnClockReleased;
            return s;
        }

        private void OnDestroy() => Discard();

        private void Discard()
        {
            if (clockDragged) { Options.FlushClock(); clockDragged = false; }
            if (root != null) Destroy(root);
            root = null;
            foreach (var texture in textures) if (texture != null) Destroy(texture);
            textures.Clear();
        }

        // ---- Pieces ----

        private static Image Box(Transform parent, string name, Sprite sprite, Color color, float ppuMultiplier)
        {
            var go = new GameObject(name, typeof(RectTransform));
            go.transform.SetParent(parent, false);
            var image = go.AddComponent<Image>();
            image.sprite = sprite;
            image.type = Image.Type.Sliced;
            image.pixelsPerUnitMultiplier = ppuMultiplier;
            image.color = color;
            return image;
        }

        internal static TextMeshProUGUI Label(Transform parent, string name, TMP_FontAsset? font, FontStyles style, float size, Color color)
        {
            var go = new GameObject(name, typeof(RectTransform));
            go.transform.SetParent(parent, false);
            var text = go.AddComponent<TextMeshProUGUI>();
            if (font != null) text.font = font;
            text.fontStyle = style;
            text.fontSize = size;
            text.color = color;
            text.enableWordWrapping = false;
            text.overflowMode = TextOverflowModes.Overflow;
            text.alignment = TextAlignmentOptions.MidlineLeft;
            text.raycastTarget = false;
            text.richText = true;
            // 0.02 em, as the design's letter spacing for its uppercase texts.
            if ((style & FontStyles.UpperCase) != 0) text.characterSpacing = 2f;
            return text;
        }

        // Places a child by its top-left corner in design pixels from the parent's top-left.
        private static void Place(RectTransform rect, float x, float y, float width, float height)
        {
            rect.anchorMin = rect.anchorMax = rect.pivot = new Vector2(0f, 1f);
            rect.anchoredPosition = new Vector2(x, -y);
            rect.sizeDelta = new Vector2(width, height);
        }

        // Stretches a child across its parent, inset left and right, with a fixed height on the middle line.
        private static void Middle(RectTransform rect, float left, float right, float height)
        {
            rect.anchorMin = new Vector2(0f, 0.5f);
            rect.anchorMax = new Vector2(1f, 0.5f);
            rect.pivot = new Vector2(0.5f, 0.5f);
            rect.offsetMin = new Vector2(left, -height / 2f);
            rect.offsetMax = new Vector2(-right, height / 2f);
        }

        // ---- Sprites, drawn once per build ----

        private Sprite Sprite9(Texture2D texture, float borderPixels, float texels, bool vertical)
        {
            var b = borderPixels * texels;
            var border = vertical ? new Vector4(b, b, b, b) : new Vector4(b, 0f, b, 0f);
            return Sprite.Create(texture, new Rect(0, 0, texture.width, texture.height), new Vector2(0.5f, 0.5f), 100f, 0, SpriteMeshType.FullRect, border);
        }

        private Sprite SpriteWhole(Texture2D texture) =>
            Sprite.Create(texture, new Rect(0, 0, texture.width, texture.height), new Vector2(0.5f, 0.5f), 100f, 0, SpriteMeshType.FullRect);

        private Texture2D NewTexture(int width, int height)
        {
            var texture = new Texture2D(width, height, TextureFormat.RGBA32, false)
            {
                wrapMode = TextureWrapMode.Clamp,
                filterMode = FilterMode.Bilinear,
                hideFlags = HideFlags.DontSave,
            };
            textures.Add(texture);
            return texture;
        }

        // A rounded rectangle for 9-slicing: corners of the given radius, a 2 px stretchable middle. With plain the
        // colour is white (tinted by the Image); otherwise a vertical gradient over the given height, sliced only
        // sideways so the gradient isn't stretched.
        private Texture2D RoundedRect(float radius, float texels, bool top, bool bottom, Color topColor, Color bottomColor, bool plain, float height = 0f)
        {
            var w = Mathf.CeilToInt((2f * radius + 2f) * texels);
            var h = plain ? w : Mathf.CeilToInt(height * texels);
            var r = radius * texels;
            var texture = NewTexture(w, h);
            var pixels = new Color[w * h];
            for (var y = 0; y < h; y++)
            {
                var t = h > 1 ? 1f - y / (float)(h - 1) : 0f; // 0 at the top row, 1 at the bottom
                var color = plain ? Color.white : Color.Lerp(topColor, bottomColor, t);
                for (var x = 0; x < w; x++)
                {
                    var px = x + 0.5f;
                    var py = y + 0.5f;
                    var cx = px < r ? r : px > w - r ? w - r : px;
                    // Texture rows run bottom-up.
                    var roundHere = (py > h - r && top) || (py < r && bottom);
                    var cy = py > h - r ? h - r : py < r ? r : py;
                    var coverage = 1f;
                    if (roundHere && (px < r || px > w - r))
                    {
                        var d = Mathf.Sqrt((px - cx) * (px - cx) + (py - cy) * (py - cy)) - r;
                        coverage = Mathf.Clamp01(0.5f - d);
                    }
                    pixels[y * w + x] = new Color(color.r, color.g, color.b, color.a * coverage);
                }
            }
            texture.SetPixels(pixels);
            texture.Apply(false, true);
            return texture;
        }

        private Texture2D Circle(float diameter, float texels)
        {
            var n = Mathf.CeilToInt(diameter * texels);
            var r = n / 2f;
            var texture = NewTexture(n, n);
            var pixels = new Color[n * n];
            for (var y = 0; y < n; y++)
                for (var x = 0; x < n; x++)
                {
                    var d = Mathf.Sqrt((x + 0.5f - r) * (x + 0.5f - r) + (y + 0.5f - r) * (y + 0.5f - r)) - r;
                    pixels[y * n + x] = new Color(1f, 1f, 1f, Mathf.Clamp01(0.5f - d));
                }
            texture.SetPixels(pixels);
            texture.Apply(false, true);
            return texture;
        }

        // A soft shadow of a box, as CSS draws a 10 px blur (a Gaussian of sigma 5): a margin of 1.5 blurs round a
        // box wide enough that its middle is flat, sliced so the middle stretches to the card's size.
        private Texture2D Shadow(float texels, out float border)
        {
            var margin = ShadowBlur * 1.5f;
            var inner = 2f * margin + 4f;
            var size = 2f * margin + inner;
            border = margin + inner / 2f - 1f;
            var n = Mathf.CeilToInt(size * texels);
            var sigma = ShadowBlur / 2f * texels;
            var a = margin * texels;
            var b = (margin + inner) * texels;
            var texture = NewTexture(n, n);
            var pixels = new Color[n * n];
            var falloff = new float[n];
            for (var i = 0; i < n; i++)
            {
                var p = i + 0.5f;
                falloff[i] = 0.5f * (Erf((p - a) / (sigma * 1.41421356f)) - Erf((p - b) / (sigma * 1.41421356f)));
            }
            for (var y = 0; y < n; y++)
                for (var x = 0; x < n; x++)
                    pixels[y * n + x] = new Color(1f, 1f, 1f, falloff[x] * falloff[y]);
            texture.SetPixels(pixels);
            texture.Apply(false, true);
            return texture;
        }

        // Abramowitz and Stegun 7.1.26, good to 1.5e-7.
        private static float Erf(float x)
        {
            var sign = x < 0f ? -1f : 1f;
            x = Mathf.Abs(x);
            var t = 1f / (1f + 0.3275911f * x);
            var y = 1f - ((((1.061405429f * t - 1.453152027f) * t + 1.421413741f) * t - 0.284496736f) * t + 0.254829592f) * t * Mathf.Exp(-x * x);
            return sign * y;
        }

        private static Color Hex(int rgb) => new Color32((byte)(rgb >> 16), (byte)(rgb >> 8), (byte)rgb, 255);

        // The design's hover: 15 % brighter.
        private static Color Brighter(Color c) => new Color(Mathf.Min(1f, c.r * 1.15f), Mathf.Min(1f, c.g * 1.15f), Mathf.Min(1f, c.b * 1.15f), c.a);
    }

    // A flat button with the game's hover and click sounds and a hover colour.
    internal sealed class PanelButton : MonoBehaviour, IPointerEnterHandler, IPointerExitHandler, IPointerDownHandler, IPointerClickHandler
    {
        public Action? Clicked;
        public TextMeshProUGUI Label = null!;
        private Image image = null!;
        private Color normal, hover;
        private bool hovered;

        public static PanelButton Make(Transform parent, string name, Sprite shape, float ppuMultiplier, TMP_FontAsset? font, bool fauxBold)
        {
            var go = new GameObject(name, typeof(RectTransform));
            go.transform.SetParent(parent, false);
            var image = go.AddComponent<Image>();
            image.sprite = shape;
            image.type = Image.Type.Sliced;
            image.pixelsPerUnitMultiplier = ppuMultiplier;
            var button = go.AddComponent<PanelButton>();
            button.image = image;
            var style = FontStyles.UpperCase | (fauxBold ? FontStyles.Bold : FontStyles.Normal);
            button.Label = RidePanel.Label(go.transform, "Text", font, style, 15f, Color.white);
            button.Label.alignment = TextAlignmentOptions.Center;
            var rect = button.Label.rectTransform;
            rect.anchorMin = Vector2.zero;
            rect.anchorMax = Vector2.one;
            rect.offsetMin = rect.offsetMax = Vector2.zero;
            return button;
        }

        public void Paint(Color background, Color hoverBackground, Color text)
        {
            normal = background;
            hover = hoverBackground;
            image.color = hovered ? hover : normal;
            Label.color = text;
        }

        public void SetText(string text) => Label.text = text;

        public void OnPointerEnter(PointerEventData eventData)
        {
            hovered = true;
            image.color = hover;
            GameHud.HoverSound();
        }

        public void OnPointerExit(PointerEventData eventData)
        {
            hovered = false;
            image.color = normal;
        }

        public void OnPointerDown(PointerEventData eventData)
        {
            if (eventData.button == PointerEventData.InputButton.Left) GameHud.ClickSound();
        }

        public void OnPointerClick(PointerEventData eventData)
        {
            GameHud.Deselect();
            if (eventData.button != PointerEventData.InputButton.Left) return;
            try { Clicked?.Invoke(); }
            catch (Exception ex) { Log.Error("Ride panel button: " + ex); }
        }

        private void OnDisable()
        {
            hovered = false;
            if (image != null) image.color = normal;
        }
    }

    // The clock slider's let-go: the value is written to disk once, then.
    internal sealed class PointerRelease : MonoBehaviour, IPointerUpHandler
    {
        public Action? Released;

        public void OnPointerUp(PointerEventData eventData)
        {
            GameHud.Deselect();
            Released?.Invoke();
        }
    }
}
