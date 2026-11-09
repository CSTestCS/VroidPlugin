using System;
using System.Collections.Generic;
using UnityEngine;

namespace VRoidPlus
{
    /// <summary>
    /// A small immediate-mode layout helper built only on GUI.* calls with explicit rectangles.
    ///
    /// Why not GUILayout: VRoid Studio never uses Unity's IMGUI, so its build has most of it stripped out.
    /// BepInEx can put the method names back, but many of them then fail when called. GUI.Box and GUI.Button
    /// are known to work; every other control tries its normal GUI.* call once and, if that fails, switches
    /// to a version drawn with buttons and boxes only.
    /// </summary>
    internal static class Ui
    {
        public const float LineHeight = 22f;
        private const float Gap = 3f;

        // Visible content area on screen, and the scrolled cursor inside it.
        private static Rect _view;
        private static float _scroll;
        private static float _y;               // content-space y of the next element
        private static bool _inRow;
        private static float _rowX, _rowH, _rowReserve;

        public static GUIStyle Wrap, Bold, Small;
        private static bool _stylesTried;

        // ---------------------------------------------------------------- frame

        public static void Begin(Rect view, float scroll)
        {
            _view = view; _scroll = scroll; _y = 0; _inRow = false;
            EnsureStyles();
            HandleTyping(Event.current);
        }

        /// <summary>Returns the total content height (for scroll limits).</summary>
        public static float End()
        {
            if (_inRow) EndRow();
            return _y;
        }

        public static void Space(float px) { if (_inRow) _rowX += px; else _y += px; }

        public static void BeginRow(float reserveRight = 0)
        {
            if (_inRow) EndRow();
            _inRow = true; _rowX = _view.x; _rowH = 0; _rowReserve = reserveRight;
        }

        public static void EndRow()
        {
            if (!_inRow) return;
            _inRow = false;
            _y += Mathf.Max(_rowH, LineHeight) + Gap;
        }

        private static float FillWidth() => _inRow ? Mathf.Max(30, _view.xMax - _rowX - _rowReserve) : _view.width;

        /// <summary>Allocates a rectangle. width 0 = take what's left of the row (minus the reserved right part).</summary>
        private static Rect Place(float width, float height)
        {
            if (_inRow)
            {
                var w = width > 0 ? width : FillWidth();
                var r = new Rect(_rowX, _view.y + _y - _scroll, w, height);
                _rowX += w + Gap;
                if (width <= 0) _rowReserve = 0; // after the flexible element, the rest is laid out normally
                _rowH = Mathf.Max(_rowH, height);
                return r;
            }
            var rect = new Rect(_view.x, _view.y + _y - _scroll, _view.width, height);
            _y += height + Gap;
            return rect;
        }

        /// <summary>Only draw what is fully inside the window, so nothing spills past its edges (no clipping API needed).</summary>
        private static bool Visible(Rect r) => r.y >= _view.y - 1 && r.yMax <= _view.yMax + 1;

        // ---------------------------------------------------------------- controls

        public static void Label(string text, GUIStyle style = null, float width = 0)
        {
            text ??= "";
            var w = width > 0 ? width : FillWidth();
            var r = Place(width, TextHeight(text, w, style));
            if (Visible(r)) DrawLabel(r, text, style);
        }

        public static void Header(string text)
        {
            if (_inRow) EndRow();
            _y += 6;
            Label(text, Bold);
        }

        public static bool Button(string text, float width = 0, float height = LineHeight)
        {
            var r = Place(width, height);
            return Visible(r) && GUI.Button(r, text);
        }

        public static bool Toggle(bool value, string text)
        {
            var r = Place(0, LineHeight);
            if (!Visible(r)) return value;
            if (_toggleOk)
            {
                try { return GUI.Toggle(r, value, text); }
                catch (Exception e) { Fallback(ref _toggleOk, "GUI.Toggle", e); }
            }
            return GUI.Button(r, (value ? "[x] " : "[  ] ") + text) ? !value : value;
        }

        public static float Slider(float value, float min, float max, float width = 0)
        {
            var r = Place(width, LineHeight);
            if (!Visible(r)) return value;
            if (_sliderOk)
            {
                try { return GUI.HorizontalSlider(new Rect(r.x, r.y + 5, r.width, r.height - 5), value, min, max); }
                catch (Exception e) { Fallback(ref _sliderOk, "GUI.HorizontalSlider", e); }
            }
            // Fallback: [-] [bar] [+]
            var step = (max - min) / 30f;
            var bw = 26f;
            if (GUI.Button(new Rect(r.x, r.y, bw, r.height), "-")) value = Mathf.Max(min, value - step);
            var bar = new Rect(r.x + bw + 2, r.y + 6, r.width - 2 * bw - 4, r.height - 12);
            GUI.Box(bar, "");
            var frac = Mathf.InverseLerp(min, max, value);
            GUI.Box(new Rect(bar.x, bar.y, Mathf.Max(4, bar.width * frac), bar.height), "");
            if (GUI.Button(new Rect(r.xMax - bw, r.y, bw, r.height), "+")) value = Mathf.Min(max, value + step);
            return value;
        }

        /// <summary>Single-line text input. <paramref name="id"/> must be unique in the window.</summary>
        public static string TextField(string id, string text, float width = 0)
        {
            text ??= "";
            var r = Place(width, LineHeight);
            if (!Visible(r)) return text;
            if (_textOk)
            {
                try { return GUI.TextField(r, text); }
                catch (Exception e) { Fallback(ref _textOk, "GUI.TextField", e); }
            }
            // Fallback: click to focus, then type. Enter/Esc/click elsewhere ends editing.
            bool focused = _focusId == id;
            if (focused) text = _buffer;
            if (GUI.Button(r, focused ? text + "_" : (text.Length > 0 ? text : " "))) { _focusId = id; _buffer = text; }
            return text;
        }

        public static bool IsTyping => _focusId != null || (_textOk && SafeKeyboardControl() != 0);

        private static int SafeKeyboardControl()
        {
            try { return GUIUtility.keyboardControl; } catch { return 0; }
        }

        // ---------------------------------------------------------------- drawing primitives with fallbacks

        private static bool _labelStyledOk = true, _labelOk = true, _toggleOk = true, _sliderOk = true, _textOk = true, _textureOk = true;
        private static readonly HashSet<string> Reported = new();

        private static void Fallback(ref bool flag, string what, Exception e)
        {
            flag = false;
            if (Reported.Add(what))
                Plugin.Logger.LogWarning($"{what} isn't available in this VRoid build; using a simpler version. ({e.GetType().Name}: {e.Message})");
        }

        private static void DrawLabel(Rect r, string text, GUIStyle style)
        {
            // Small inset instead of style padding (RectOffset's constructor is stripped in VRoid's build).
            r = new Rect(r.x + 2, r.y + 2, r.width - 4, r.height - 2);
            if (style != null && _labelStyledOk)
            {
                try { GUI.Label(r, text, style); return; }
                catch (Exception e) { Fallback(ref _labelStyledOk, "GUI.Label(style)", e); }
            }
            if (_labelOk)
            {
                try { GUI.Label(r, text); return; }
                catch (Exception e) { Fallback(ref _labelOk, "GUI.Label", e); }
            }
            GUI.Box(r, text);
        }

        private static Texture2D _bg;

        /// <summary>An opaque dark panel (the default box is very see-through).</summary>
        public static void Panel(Rect r)
        {
            if (_textureOk)
            {
                try
                {
                    if (_bg == null)
                    {
                        _bg = new Texture2D(1, 1);
                        _bg.SetPixel(0, 0, new Color(0.13f, 0.13f, 0.15f, 0.97f));
                        _bg.Apply();
                        _bg.hideFlags = HideFlags.HideAndDontSave;
                    }
                    GUI.DrawTexture(r, _bg);
                    GUI.Box(r, "");
                    return;
                }
                catch (Exception e) { Fallback(ref _textureOk, "GUI.DrawTexture", e); }
            }
            for (int i = 0; i < 4; i++) GUI.Box(r, "");
        }

        private static void EnsureStyles()
        {
            if (_stylesTried) return;
            _stylesTried = true;
            try
            {
                Wrap = LabelStyle(); Wrap.wordWrap = true;
                Bold = LabelStyle(); Bold.wordWrap = true; Bold.fontStyle = FontStyle.Bold;
                Small = LabelStyle(); Small.wordWrap = true; Small.fontSize = 11;
            }
            catch (Exception e)
            {
                Wrap = Bold = Small = null;
                Plugin.Logger.LogWarning("Custom text styles aren't available; using defaults. " + e.Message);
            }
        }

        /// <summary>Default label look. Built from an empty style because GUIStyle's copy constructor is stripped.</summary>
        private static GUIStyle LabelStyle()
        {
            var s = new GUIStyle();
            s.normal.textColor = new Color(0.92f, 0.92f, 0.92f);
            s.fontSize = 13;
            try { s.font = GUI.skin.label.font; } catch { }
            return s;
        }

        /// <summary>Estimated height of wrapped text (CalcHeight is often stripped, so estimate from character count).</summary>
        private static float TextHeight(string text, float width, GUIStyle style)
        {
            float fontSize = style != null && style.fontSize > 0 ? style.fontSize : 13f;
            float lineH = fontSize + 6f;
            bool wraps = style != null && style.wordWrap;
            int lines = 0;
            foreach (var para in text.Split('\n'))
            {
                if (!wraps) { lines++; continue; }
                float px = 0;
                foreach (var c in para) px += c > 0x2E80 ? fontSize : fontSize * 0.55f; // CJK is about twice as wide
                lines += Mathf.Max(1, Mathf.CeilToInt(px / Mathf.Max(20, width - 6)));
            }
            return Mathf.Max(LineHeight, lines * lineH + 4);
        }

        // ---------------------------------------------------------------- fallback text entry

        private static string _focusId, _buffer = "";

        private static void HandleTyping(Event e)
        {
            if (_focusId == null || e == null) return;
            if (e.type == EventType.MouseDown) { /* a click on another control ends editing; a click on the field re-focuses it */ _focusId = null; return; }
            if (e.type != EventType.KeyDown) return;
            if (e.keyCode == KeyCode.Return || e.keyCode == KeyCode.KeypadEnter || e.keyCode == KeyCode.Escape) { _focusId = null; e.Use(); return; }
            if (e.keyCode == KeyCode.Backspace) { if (_buffer.Length > 0) _buffer = _buffer.Substring(0, _buffer.Length - 1); e.Use(); return; }
            var ch = e.character;
            if (ch != '\0' && !char.IsControl(ch)) { _buffer += ch; e.Use(); return; }
            if (e.keyCode != KeyCode.None) e.Use(); // keep keys from reaching VRoid while typing
        }
    }
}
