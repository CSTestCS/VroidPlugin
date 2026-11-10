using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;

namespace VRoidPlus
{
    /// <summary>
    /// The VRoid Plus window, drawn on top of VRoid's own UI with the Ui helper (explicit rectangles,
    /// no GUILayout: VRoid's Unity build has most of GUILayout stripped). Plain ASCII labels are used
    /// because Unity's built-in font has few symbols.
    /// </summary>
    internal class MainWindow
    {
        private bool _open;
        private Rect _rect;
        private bool _dragging, _resizing;
        private Vector2 _dragOffset;
        private int _tab;
        private float _scroll, _contentHeight;
        private readonly string[] _tabs = { "Face Sets", "Hair Merge", "Hair Anchors", "Body", "Accessories", "Pose", "Setup" };

        private const float TitleH = 24f, TabRowH = 24f, Pad = 8f;

        public MainWindow()
        {
            var s = Settings.Current;
            _rect = new Rect(s.WindowX, s.WindowY, Mathf.Max(380, s.WindowW), Mathf.Max(300, s.WindowH));
        }

        // ------------------------------------------------------------------ frame

        public void OnGUI()
        {
            var e = Event.current;
            HandleHotkeys(e);

            var launcher = new Rect(6, Screen.height - 30, 84, 24);
            if (GUI.Button(launcher, _open ? "VRoid+ [x]" : "VRoid+")) SetOpen(!_open);
            if (!_open) { BlockVRoidInput(false); return; }

            HandleDragAndResize(e);
            _rect.width = Mathf.Clamp(_rect.width, 380, Mathf.Max(380, Screen.width - 10));
            _rect.height = Mathf.Clamp(_rect.height, 300, Mathf.Max(300, Screen.height - 10));
            _rect.x = Mathf.Clamp(_rect.x, 0, Mathf.Max(0, Screen.width - 80));
            _rect.y = Mathf.Clamp(_rect.y, 0, Mathf.Max(0, Screen.height - 40));
            bool mouseOver = e != null && _rect.Contains(e.mousePosition);
            BlockVRoidInput(mouseOver);

            Ui.Panel(_rect);

            // Title bar (drag area) and close button.
            var title = new Rect(_rect.x + Pad, _rect.y + 2, _rect.width - 2 * Pad - 30, TitleH);
            Ui.Begin(title, 0);
            Ui.Label($"VRoid Plus {Plugin.Version}  -  {Settings.Current.ToggleKey} to hide, drag here to move", Ui.Bold);
            Ui.End();
            if (GUI.Button(new Rect(_rect.xMax - Pad - 26, _rect.y + 3, 26, 20), "x")) { SetOpen(false); return; }

            // Tabs: two rows of buttons.
            float tabY = _rect.y + TitleH + 4;
            float tabW = (_rect.width - 2 * Pad - 3 * 3) / 4f;
            for (int i = 0; i < _tabs.Length; i++)
            {
                int row = i / 4, col = i % 4;
                var r = new Rect(_rect.x + Pad + col * (tabW + 3), tabY + row * (TabRowH + 3), tabW, TabRowH);
                if (GUI.Button(r, i == _tab ? "> " + _tabs[i] + " <" : _tabs[i])) { _tab = i; _picker = null; _scroll = 0; }
            }

            // Scrollable content.
            float top = tabY + 2 * (TabRowH + 3) + 6;
            var view = new Rect(_rect.x + Pad, top, _rect.width - 2 * Pad - 14, _rect.yMax - top - Pad);
            if (mouseOver && e.type == EventType.ScrollWheel) { _scroll += e.delta.y * 24f; e.Use(); }
            _scroll = Mathf.Clamp(_scroll, 0, Mathf.Max(0, _contentHeight - view.height));

            Ui.Begin(view, _scroll);
            DrawDiagnosticsBanner();
            if (_picker != null) DrawBonePicker();
            else
            {
                switch (_tab)
                {
                    case 0: DrawFaceSets(); break;
                    case 1: DrawHairMerge(); break;
                    case 2: DrawAnchors(Features.HairAnchors, "hair part"); break;
                    case 3: DrawBody(); break;
                    case 4: DrawAnchors(Features.AccessoryAnchors, "accessory"); break;
                    case 5: DrawPose(); break;
                    case 6: DrawSetup(); break;
                }
            }
            _contentHeight = Ui.End() + 10;
            DrawScrollBar(view);

            // Resize grip (bottom-right corner).
            GUI.Box(new Rect(_rect.xMax - 14, _rect.yMax - 14, 14, 14), "");
        }

        private void DrawScrollBar(Rect view)
        {
            if (_contentHeight <= view.height) return;
            var track = new Rect(view.xMax + 4, view.y, 10, view.height);
            GUI.Box(track, "");
            float frac = view.height / _contentHeight;
            float h = Mathf.Max(20, track.height * frac);
            float y = track.y + (track.height - h) * (_scroll / Mathf.Max(1, _contentHeight - view.height));
            GUI.Box(new Rect(track.x, y, track.width, h), "");
            // Click on the track to jump.
            var e = Event.current;
            if (e != null && e.type == EventType.MouseDown && track.Contains(e.mousePosition))
            {
                _scroll = (e.mousePosition.y - track.y) / track.height * _contentHeight - view.height / 2;
                e.Use();
            }
        }

        private void HandleHotkeys(Event e)
        {
            if (e == null) return;
            if (_recordingKey && e.type == EventType.KeyDown && e.keyCode != KeyCode.None && !Hotkey.IsModifier(e.keyCode))
            {
                if (e.keyCode != KeyCode.Escape)
                {
                    var h = new Hotkey { Key = e.keyCode, Ctrl = e.control, Shift = e.shift, Alt = e.alt };
                    Settings.Current.ToggleKey = h.ToString();
                    Settings.Save();
                }
                _recordingKey = false;
                e.Use();
            }
            else if (Settings.Toggle.Matches(e) && !Ui.IsTyping) { SetOpen(!_open); e.Use(); }
        }

        private void SetOpen(bool open)
        {
            _open = open;
            if (!open) { BlockVRoidInput(false); SaveWindow(); }
        }

        private void SaveWindow()
        {
            var s = Settings.Current;
            s.WindowX = _rect.x; s.WindowY = _rect.y; s.WindowW = _rect.width; s.WindowH = _rect.height;
            Settings.Save();
        }

        private void HandleDragAndResize(Event e)
        {
            if (e == null) return;
            var titleBar = new Rect(_rect.x, _rect.y, _rect.width - 40, TitleH + 2);
            var grip = new Rect(_rect.xMax - 18, _rect.yMax - 18, 18, 18);
            if (e.type == EventType.MouseDown && grip.Contains(e.mousePosition)) { _resizing = true; _dragOffset = new Vector2(_rect.xMax, _rect.yMax) - e.mousePosition; e.Use(); }
            else if (e.type == EventType.MouseDown && titleBar.Contains(e.mousePosition)) { _dragging = true; _dragOffset = e.mousePosition - _rect.position; e.Use(); }
            else if (e.type == EventType.MouseUp && (_dragging || _resizing)) { _dragging = _resizing = false; SaveWindow(); }
            else if (e.type == EventType.MouseDrag && _dragging) { _rect.position = e.mousePosition - _dragOffset; e.Use(); }
            else if (e.type == EventType.MouseDrag && _resizing)
            {
                var corner = e.mousePosition + _dragOffset;
                _rect.width = corner.x - _rect.x; _rect.height = corner.y - _rect.y;
                e.Use();
            }
        }

        // While the mouse is over this window, VRoid's buttons underneath shouldn't react to clicks.
        private bool _blocking;
        private readonly List<Behaviour> _disabledEventSystems = new();

        private void BlockVRoidInput(bool block)
        {
            if (block == _blocking) return;
            _blocking = block;
            if (block)
            {
                foreach (var c in Compat.FindComponentsByTypeName("UnityEngine.EventSystems.EventSystem, UnityEngine.UI"))
                {
                    var b = Compat.As<Behaviour>(c);
                    if (b != null && b.enabled) { b.enabled = false; _disabledEventSystems.Add(b); }
                }
            }
            else
            {
                foreach (var b in _disabledEventSystems) if (b != null) b.enabled = true;
                _disabledEventSystems.Clear();
            }
        }

        // ------------------------------------------------------------------ helpers

        private static void Text(string s) => Ui.Label(s, Ui.Wrap);
        private static void Hint(string s) => Ui.Label(s, Ui.Small);
        private static void Header(string s) => Ui.Header(s);

        private string _confirm;

        /// <summary>Delete needs two clicks.</summary>
        private bool DeleteButton(string id)
        {
            bool armed = _confirm == id;
            if (!Ui.Button(armed ? "Sure?" : "Delete", 60)) return false;
            if (armed) { _confirm = null; return true; }
            _confirm = id;
            return false;
        }

        private static void Status(string s) { if (!string.IsNullOrEmpty(s)) { Ui.Space(4); Text(">> " + s); } }

        // ------------------------------------------------------------------ diagnostics

        private string _diagStatus;

        private void DrawDiagnosticsBanner()
        {
            if (!Settings.Current.ShowDiagnosticsBanner) return;
            Ui.Label("Help fit VRoid Plus to your VRoid: open a model and press the button. It saves a ZIP on your Desktop to send to the developer. It only reads; nothing in VRoid is changed.", Ui.Small);
            Ui.BeginRow(0);
            DiagnosticsButton();
            if (Ui.Button("Hide (still in Setup tab)", 180)) { Settings.Current.ShowDiagnosticsBanner = false; Settings.Save(); }
            Ui.EndRow();
            Status(_diagStatus);
            Ui.Space(8);
        }

        private void DiagnosticsButton()
        {
            if (!Ui.Button("Save diagnostics ZIP", 170)) return;
            try { _diagStatus = "Saved: " + Diagnostics.Capture(); }
            catch (Exception ex) { _diagStatus = "Failed: " + ex.Message; Plugin.Logger.LogError(ex); }
        }

        // ------------------------------------------------------------------ tabs

        private string _faceName = "My face";

        private void DrawFaceSets()
        {
            var f = Features.FaceSets;
            Text("Save all face sliders and colors (face shape, eyes, eyebrows, nose, mouth, ears, skin...) as a face set, and apply it to any model, like VRoid's built-in face sets.");
            if (!f.IsMapped(out var err)) Hint("Open a model to save or apply face sets.");
            Hint("Tip: choose the same part presets (e.g. eye type) first; sliders are applied to whatever preset the part currently uses.");

            Ui.BeginRow(143);
            _faceName = Ui.TextField("faceName", _faceName);
            if (Ui.Button("Save current face", 140))
            {
                if (f.Store.Exists(_faceName) && _confirm != "overwrite-face:" + _faceName) { _confirm = "overwrite-face:" + _faceName; f.Status = $"'{_faceName}' exists - click Save again to overwrite."; }
                else { _confirm = null; f.Save(_faceName); }
            }
            Ui.EndRow();
            Status(f.Status);

            Header("Saved face sets");
            var names = f.Store.List();
            if (names.Count == 0) Hint("None yet.");
            foreach (var n in names)
            {
                Ui.BeginRow(126);
                Ui.Label(n, Ui.Wrap);
                if (Ui.Button("Load", 60)) f.Load(n);
                if (DeleteButton("face:" + n)) { f.Store.Delete(n); f.Status = $"Deleted '{n}'."; }
                Ui.EndRow();
            }
            Hint("Files: " + f.Store.Folder);
        }


        private void DrawHairMerge()
        {
            var h = Features.HairMerge;
            Text("Combine hairstyles: add the hair groups of any hairstyle (built-in or custom; Front, Back, Overall Hair, Extensions, Side, Ahoge or Extra) to the hairstyle you're editing.");
            Text("Not working yet. VRoid stores hair groups together with their materials and bones, and copying them safely needs one more diagnostics capture (see the button above, taken in the Hair editor).");
        }

        private bool _showAllParts;
        private List<BoneAnchors.Item> _partsCache = new();
        private float _partsCacheTime;
        private BoneAnchors _partsCacheOwner;

        private void DrawAnchors(BoneAnchors anchors, string what)
        {
            Text($"Pick which bone each {what} follows instead of the one VRoid attaches it to. Pick any bone on the model.");
            Hint("It keeps its current position and follows the chosen bone from then on. VRoid rebuilds items when you edit them; your choice is re-applied automatically. This changes the 3D preview; exporting is not covered yet.");
            if (!Avatar.Found) { Text("No model is loaded."); return; }
            var all = Ui.Toggle(_showAllParts, "Also show other items (clothing etc.)");
            if (all != _showAllParts) { _showAllParts = all; _partsCacheTime = 0; }

            if (_partsCacheOwner != anchors || Time.unscaledTime > _partsCacheTime)
            {
                _partsCache = anchors.FindItems(_showAllParts);
                _partsCacheOwner = anchors;
                _partsCacheTime = Time.unscaledTime + 1f;
            }
            if (_partsCache.Count == 0) Hint($"No {what}s found on this model.");
            foreach (var item in _partsCache)
            {
                var rule = anchors.RuleFor(item.Id);
                Ui.BeginRow(166);
                Ui.Label($"{item.Label}\n   follows: {(item.CurrentAnchor != null ? item.CurrentAnchor.name : "?")}{(rule != null ? "  (changed)" : "")}", Ui.Small);
                var it = item;
                if (Ui.Button("Choose bone...", 105)) OpenPicker($"Bone for {item.Label}", t => { anchors.SetRule(it, t); _partsCacheTime = 0; });
                if (rule != null && Ui.Button("Reset", 52)) { anchors.SetRule(item, null); _partsCacheTime = 0; }
                Ui.EndRow();
            }
        }

        private void DrawBody()
        {
            var body = Features.Body;
            var s = Settings.Current;
            Text("Extra body sliders. They reshape the body and the clothes on it around the bones, so posing doesn't distort them.");
            if (!Avatar.Found) { Hint("No model is loaded."); return; }

            var file = body.File.Path != null ? System.IO.Path.GetFileName(body.File.Path) : "this new model (not saved yet)";
            Hint($"Values for {file}. They are saved together with the .vroid file when you save it in VRoid" +
                 (body.File.HasUnsavedChanges ? " - you have unsaved slider changes." : "."));

            bool changed = false;
            foreach (var def in BodySliders.Sliders.Where(d => !d.Extra)) changed |= Slider(def);

            Ui.Space(4);
            if (Ui.Button(s.ShowMoreBodySliders ? "Hide more sliders" : "More sliders (neck, shoulders, chest, waist, hips, hands, feet)...", 0))
            { s.ShowMoreBodySliders = !s.ShowMoreBodySliders; Settings.Save(); }
            if (s.ShowMoreBodySliders)
                foreach (var def in BodySliders.Sliders.Where(d => d.Extra)) changed |= Slider(def);

            Ui.Space(4);
            if (Ui.Button("Reset all to 100%", 140)) { body.ResetAll(); changed = true; }
            if (changed) body.MarkDirty();
        }

        private static bool Slider(BodySliders.SliderDef def)
        {
            var body = Features.Body;
            var v = body.Get(def.Id);
            Ui.BeginRow(110);
            Ui.Label(def.Label, Ui.Wrap, 120);
            var nv = Ui.Slider(v, 0.5f, 2f);
            Ui.Label($"{nv * 100:F0}%", Ui.Wrap, 48);
            if (Ui.Button("Reset", 52)) nv = 1f;
            Ui.EndRow();
            nv = Mathf.Round(nv * 100f) / 100f;
            if (Mathf.Approximately(nv, v)) return false;
            body.Set(def.Id, nv);
            return true;
        }

        private void DrawPose()
        {
            var p = Features.Pose;
            var s = Settings.Current;
            Header("Show Controller");
            var off = Ui.Toggle(s.ShowControllerDefaultOff, "Start with 'Show Controller' off when the pose editor opens");
            if (off != s.ShowControllerDefaultOff) { s.ShowControllerDefaultOff = off; Settings.Save(); }
            Hint(p.LastToggleInfo);

            Header("Load Pose (.fbx)");
            Text("Load a pose or animation from an FBX file (Mixamo, Blender, etc.). Animations play like VRoid's built-in ones: play/pause only, the pose can't be edited while loaded.");
            if (!Avatar.Found) Hint("No model is loaded.");
            if (Ui.Button("Load .fbx file...", 160))
            {
                var path = FileDialog.Open("Load pose or animation", "FBX files (*.fbx)|*.fbx|All files|*.*");
                if (!string.IsNullOrEmpty(path)) p.LoadFbx(path);
            }
            Status(p.Status);
            if (!p.Loaded) return;

            var take = p.Take;
            if (p.Scene.Takes.Count > 1)
            {
                Ui.BeginRow(0);
                if (Ui.Button("<", 30)) { p.TakeIndex = (p.TakeIndex - 1 + p.Scene.Takes.Count) % p.Scene.Takes.Count; p.Time0 = 0; }
                if (Ui.Button(">", 30)) { p.TakeIndex = (p.TakeIndex + 1) % p.Scene.Takes.Count; p.Time0 = 0; }
                Ui.Label($"Animation {p.TakeIndex + 1}/{p.Scene.Takes.Count}: {take?.Name}", Ui.Wrap);
                Ui.EndRow();
            }
            if (take != null && take.Length > 0)
            {
                Ui.BeginRow(0);
                if (Ui.Button(p.Paused ? "Play" : "Pause", 90)) p.Paused = !p.Paused;
                Ui.Label($"{p.Time0:F1}s / {take.Length:F1}s", Ui.Wrap);
                Ui.EndRow();
                var loop = Ui.Toggle(s.FbxLoop, "Loop");
                var rm = Ui.Toggle(s.FbxApplyRootMotion, "Allow the model to move around (root motion)");
                if (loop != s.FbxLoop || rm != s.FbxApplyRootMotion) { s.FbxLoop = loop; s.FbxApplyRootMotion = rm; Settings.Save(); }
            }
            if (Ui.Button("Unload (back to VRoid poses)", 220)) p.Unload();
        }

        // ------------------------------------------------------------------ setup / explorer

        private string _explorePath = "";
        private List<Explorer.Row> _rows;
        private string _rowsError;
        private string _keyword = "Face";
        private List<string> _candidates;
        private string _setupStatus;
        private bool _recordingKey;

        private void DrawSetup()
        {
            var m = Mapping.Current;
            Header("Diagnostics");
            Ui.BeginRow(0);
            DiagnosticsButton();
            var show = Ui.Toggle(Settings.Current.ShowDiagnosticsBanner, "Show at the top of every tab");
            if (show != Settings.Current.ShowDiagnosticsBanner) { Settings.Current.ShowDiagnosticsBanner = show; Settings.Save(); }
            Ui.EndRow();
            Status(_diagStatus);

            Header("Shortcut");
            Ui.BeginRow(_recordingKey ? 0 : 146);
            Ui.Label("Open/close: " + (_recordingKey ? "press the new keys... (Esc cancels)" : Settings.Current.ToggleKey), Ui.Wrap);
            if (!_recordingKey && Ui.Button("Change...", 80)) _recordingKey = true;
            if (!_recordingKey && Ui.Button("Default", 60)) { Settings.Current.ToggleKey = Hotkey.Default; Settings.Save(); }
            Ui.EndRow();

            Text("Body sliders, anchors and FBX poses work on their own. Face sets and hair merging edit VRoid's internal data, whose names differ between VRoid versions; they are set here (saved in mapping.json).");

            Header("Connections");
            MappingRow("Face data", "map.face", ref m.FaceData);
            MappingRow("Face refresh (optional)", "map.faceRefresh", ref m.FaceRefresh);
            MappingRow("Hair group list", "map.hair", ref m.HairGroupList);
            MappingRow("Hair refresh (optional)", "map.hairRefresh", ref m.HairRefresh);
            Ui.BeginRow(0);
            if (Ui.Button("Save mapping", 120)) { Mapping.Save(); _setupStatus = "Saved " + Mapping.FilePath; }
            if (Ui.Button("Reload from file", 130)) { Mapping.Load(); _setupStatus = "Reloaded."; }
            Ui.EndRow();
            Status(_setupStatus);

            Header("Find VRoid's data");
            if (Ui.Button("Write type dump (for sharing / bug reports)", 300)) _setupStatus = Explorer.DumpTypes();
            Ui.BeginRow(53);
            Ui.Label("Search:", Ui.Wrap, 60);
            _keyword = Ui.TextField("keyword", _keyword);
            if (Ui.Button("Find", 50)) _candidates = Explorer.FindCandidates(_keyword);
            Ui.EndRow();
            if (_candidates != null)
            {
                if (_candidates.Count == 0) Hint("Nothing found.");
                foreach (var c in _candidates.Take(60))
                {
                    Ui.BeginRow(68);
                    Ui.Label(c, Ui.Small);
                    if (Ui.Button("Explore", 65)) Navigate(c);
                    Ui.EndRow();
                }
            }

            Header("Explorer");
            Ui.BeginRow(80);
            _explorePath = Ui.TextField("explore", _explorePath);
            if (Ui.Button("Go", 36)) Navigate(_explorePath);
            if (Ui.Button("Up", 36)) Navigate(Up(_explorePath));
            Ui.EndRow();
            Ui.BeginRow(0);
            if (Ui.Button("Use as Face data", 150)) { m.FaceData = _explorePath; Mapping.Save(); _setupStatus = "Face data set."; }
            if (Ui.Button("Use as Hair group list", 170)) { m.HairGroupList = _explorePath; Mapping.Save(); _setupStatus = "Hair group list set."; }
            Ui.EndRow();
            if (_rowsError != null) Hint(_rowsError);
            if (_rows != null)
            {
                foreach (var r in _rows)
                {
                    Ui.BeginRow(r.Expandable ? 30 : 0);
                    Ui.Label($"{r.Name} : {r.Type} = {r.Value}", Ui.Small);
                    if (r.Expandable && Ui.Button(">", 28))
                        Navigate(r.Name.StartsWith("[") ? _explorePath + r.Name : (_explorePath.Contains("::") ? _explorePath + "." + r.Name : _explorePath + "::" + r.Name));
                    Ui.EndRow();
                }
            }
        }

        private static void MappingRow(string label, string id, ref string value)
        {
            Ui.BeginRow(0);
            Ui.Label(label, Ui.Wrap, 160);
            value = Ui.TextField(id, value ?? "");
            Ui.EndRow();
            if (!string.IsNullOrWhiteSpace(value))
            {
                var o = ReflectionPath.Resolve(value, out var err);
                Hint(o != null ? "    OK: found " + o.GetType().Name + (ListAccess.IsList(o) ? $" ({ListAccess.Count(o)} items)" : "")
                               : value.TrimEnd().EndsWith("()") ? "    (method, runs when used)" : "    Not found: " + err);
            }
        }

        private void Navigate(string path)
        {
            _explorePath = path ?? "";
            var o = ReflectionPath.Resolve(_explorePath, out var err);
            _rowsError = o == null ? err : $"{o.GetType().FullName}";
            _rows = o != null ? Explorer.Describe(o) : null;
        }

        private static string Up(string path)
        {
            if (string.IsNullOrEmpty(path)) return path;
            if (path.EndsWith("]")) { var b = path.LastIndexOf('['); if (b > 0) return path.Substring(0, b); }
            var sep = path.IndexOf("::", StringComparison.Ordinal);
            var dot = path.LastIndexOf('.');
            if (sep >= 0 && dot > sep) return path.Substring(0, dot);
            if (sep >= 0) return path.Substring(0, sep);
            return path;
        }

        // ------------------------------------------------------------------ bone picker

        private Action<Transform> _picker;
        private string _pickerTitle;
        private string _boneFilter = "";
        private bool _humanoidOnly = true;

        private void OpenPicker(string title, Action<Transform> onPick)
        {
            _picker = onPick; _pickerTitle = title; _scroll = 0;
        }

        private void DrawBonePicker()
        {
            Ui.Label(_pickerTitle, Ui.Bold);
            if (Ui.Button("Cancel", 80)) { _picker = null; return; }
            Ui.BeginRow(0);
            Ui.Label("Filter:", Ui.Wrap, 50);
            _boneFilter = Ui.TextField("boneFilter", _boneFilter);
            Ui.EndRow();
            _humanoidOnly = Ui.Toggle(_humanoidOnly, "Main skeleton bones only");

            var bones = Avatar.AllBones.Where(b => b != null);
            if (_humanoidOnly) bones = bones.Where(Avatar.IsHumanoid);
            foreach (var b in bones)
            {
                var human = Avatar.HumanName(b);
                var label = human != null ? $"{human}  ({b.name})" : Avatar.Path(b);
                if (_boneFilter.Length > 0 && label.IndexOf(_boneFilter, StringComparison.OrdinalIgnoreCase) < 0) continue;
                if (Ui.Button(label))
                {
                    var pick = _picker; _picker = null;
                    pick(b);
                    return;
                }
            }
        }
    }
}
