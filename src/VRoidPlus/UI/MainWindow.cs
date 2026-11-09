using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEngine;

namespace VRoidPlus
{
    /// <summary>
    /// The VRoid Plus window. Plain IMGUI drawn on top of VRoid's own UI: no dependency on VRoid's UI code,
    /// so it keeps working across VRoid updates.
    /// </summary>
    internal class MainWindow
    {
        private bool _open;
        private Rect _rect;
        private bool _dragging;
        private Vector2 _dragOffset;
        private int _tab;
        private Vector2 _scroll;
        private readonly string[] _tabs = { "Face Sets", "Hair Merge", "Hair Anchors", "Body", "Accessories", "Pose", "Setup" };

        private GUIStyle _wrap, _title, _small;
        private static readonly GUILayoutOption[] None = new GUILayoutOption[0];

        public MainWindow()
        {
            var s = Settings.Current;
            _rect = new Rect(s.WindowX, s.WindowY, s.WindowW, s.WindowH);
        }

        // ------------------------------------------------------------------ frame

        public void OnGUI()
        {
            var e = Event.current;
            if (_recordingKey && e != null && e.type == EventType.KeyDown && e.keyCode != KeyCode.None && !Hotkey.IsModifier(e.keyCode))
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
            else if (Settings.Toggle.Matches(e) && GUIUtility.keyboardControl == 0) { SetOpen(!_open); e.Use(); }

            EnsureStyles();
            var launcher = new Rect(6, Screen.height - 30, 84, 24);
            if (GUI.Button(launcher, _open ? "VRoid+ ✕" : "VRoid+")) SetOpen(!_open);
            if (!_open) { BlockVRoidInput(false); return; }

            HandleDrag(e);
            _rect.x = Mathf.Clamp(_rect.x, 0, Mathf.Max(0, Screen.width - 80));
            _rect.y = Mathf.Clamp(_rect.y, 0, Mathf.Max(0, Screen.height - 40));
            _rect.height = Mathf.Min(_rect.height, Screen.height - _rect.y);

            BlockVRoidInput(e != null && _rect.Contains(e.mousePosition));

            GUI.Box(_rect, "");
            GUI.Box(_rect, "");          // drawn twice: the default skin is quite transparent
            GUILayout.BeginArea(new Rect(_rect.x + 8, _rect.y + 6, _rect.width - 16, _rect.height - 12));
            GUILayout.Label($"VRoid Plus {Plugin.Version}   ({Settings.Current.ToggleKey} to hide, drag here to move)", _title, None);

            DrawTabs();
            GUILayout.Space(4);
            _scroll = GUILayout.BeginScrollView(_scroll, None);
            if (_picker != null) DrawBonePicker();
            else
            {
                switch (_tab)
                {
                    case 0: DrawFaceSets(); break;
                    case 1: DrawHairMerge(); break;
                    case 2: DrawAnchors(Features.HairAnchors, "hair group"); break;
                    case 3: DrawBody(); break;
                    case 4: DrawAnchors(Features.AccessoryAnchors, "accessory"); break;
                    case 5: DrawPose(); break;
                    case 6: DrawSetup(); break;
                }
            }
            GUILayout.EndScrollView();
            GUILayout.EndArea();
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

        private void HandleDrag(Event e)
        {
            if (e == null) return;
            var titleBar = new Rect(_rect.x, _rect.y, _rect.width, 26);
            if (e.type == EventType.MouseDown && titleBar.Contains(e.mousePosition)) { _dragging = true; _dragOffset = e.mousePosition - _rect.position; e.Use(); }
            else if (e.type == EventType.MouseUp && _dragging) { _dragging = false; SaveWindow(); }
            else if (e.type == EventType.MouseDrag && _dragging) { _rect.position = e.mousePosition - _dragOffset; e.Use(); }
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

        private void EnsureStyles()
        {
            if (_wrap != null) return;
            _wrap = new GUIStyle(GUI.skin.label) { wordWrap = true };
            _title = new GUIStyle(GUI.skin.label) { fontStyle = FontStyle.Bold };
            _small = new GUIStyle(GUI.skin.label) { wordWrap = true, fontSize = 11 };
        }

        private void DrawTabs()
        {
            for (int row = 0; row < 2; row++)
            {
                GUILayout.BeginHorizontal(None);
                for (int i = row * 4; i < Math.Min(_tabs.Length, row * 4 + 4); i++)
                {
                    var label = i == _tab ? $"[{_tabs[i]}]" : _tabs[i];
                    if (GUILayout.Button(label, None)) { _tab = i; _picker = null; _scroll = Vector2.zero; }
                }
                GUILayout.EndHorizontal();
            }
        }

        // ------------------------------------------------------------------ helpers

        private void Text(string s) => GUILayout.Label(s, _wrap, None);
        private void Hint(string s) => GUILayout.Label(s, _small, None);
        private bool Button(string s) => GUILayout.Button(s, None);
        private bool Button(string s, float w) => GUILayout.Button(s, new[] { GUILayout.Width(w) });
        private void Header(string s) { GUILayout.Space(6); GUILayout.Label(s, _title, None); }

        private string _confirmDelete;

        /// <summary>Delete needs two clicks.</summary>
        private bool DeleteButton(string id)
        {
            bool armed = _confirmDelete == id;
            if (!GUILayout.Button(armed ? "Sure?" : "Delete", new[] { GUILayout.Width(60) })) return false;
            if (armed) { _confirmDelete = null; return true; }
            _confirmDelete = id;
            return false;
        }

        private void Status(string s) { if (!string.IsNullOrEmpty(s)) { GUILayout.Space(4); Text("» " + s); } }

        // ------------------------------------------------------------------ tabs

        private string _faceName = "My face";

        private void DrawFaceSets()
        {
            var f = Features.FaceSets;
            Text("Save every face selection and slider as a face set, and load it onto any model, like VRoid's built-in face sets.");
            if (!f.IsMapped(out var err))
            {
                Hint($"Face data isn't connected for this VRoid version yet ({err}). Open a model in the Face editor; if this stays, see the Setup tab.");
            }
            GUILayout.BeginHorizontal(None);
            _faceName = GUILayout.TextField(_faceName, None);
            if (Button("Save current face", 140))
            {
                if (f.Store.Exists(_faceName) && _confirmDelete != "overwrite-face:" + _faceName) { _confirmDelete = "overwrite-face:" + _faceName; f.Status = $"'{_faceName}' exists - click Save again to overwrite."; }
                else { _confirmDelete = null; f.Save(_faceName); }
            }
            GUILayout.EndHorizontal();
            Status(f.Status);

            Header("Saved face sets");
            var names = f.Store.List();
            if (names.Count == 0) Hint("None yet.");
            foreach (var n in names)
            {
                GUILayout.BeginHorizontal(None);
                GUILayout.Label(n, None);
                if (Button("Load", 60)) f.Load(n);
                if (DeleteButton("face:" + n)) { f.Store.Delete(n); f.Status = $"Deleted '{n}'."; }
                GUILayout.EndHorizontal();
            }
            Hint("Files: " + f.Store.Folder);
        }

        private string _hairName = "Hair parts";

        private void DrawHairMerge()
        {
            var h = Features.HairMerge;
            Text("Combine hairstyles: add the hair groups of any hairstyle (built-in or custom; Front, Back, Overall Hair, Extensions, Side, Ahoge or Extra) to the hairstyle you're editing.");
            Hint("1. In the hairstyle editor, open the hairstyle you want to take groups FROM and press 'Copy to library'.\n" +
                 "2. Open the hairstyle you want to add them TO and press 'Add to this hairstyle'. Its own groups are kept.");
            if (h.GroupList(out var err) == null)
                Hint($"Hair groups aren't connected for this VRoid version yet ({err}). Open the hairstyle editor; if this stays, see the Setup tab.");

            GUILayout.BeginHorizontal(None);
            _hairName = GUILayout.TextField(_hairName, None);
            if (Button("Copy to library", 130)) h.Capture(_hairName);
            GUILayout.EndHorizontal();
            Status(h.Status);

            Header("Hair library");
            var names = h.Library.List();
            if (names.Count == 0) Hint("Empty.");
            foreach (var n in names)
            {
                GUILayout.BeginHorizontal(None);
                GUILayout.Label(n, None);
                if (Button("Add to this hairstyle", 150)) h.Import(n);
                if (DeleteButton("hair:" + n)) { h.Library.Delete(n); h.Status = $"Deleted '{n}'."; }
                GUILayout.EndHorizontal();
            }
        }

        private bool _showAllParts;
        private List<BoneAnchors.Part> _partsCache = new();
        private float _partsCacheTime;
        private BoneAnchors _partsCacheOwner;

        private void DrawAnchors(BoneAnchors anchors, string what)
        {
            Text($"Pick which bone each {what} follows. By default they all follow the head; choose any bone on the model instead.");
            Hint("The part keeps its current position and follows the chosen bone from then on. VRoid rebuilds parts when you edit them; the choice is re-applied automatically.");
            if (!Avatar.Found) { Text("No model is loaded."); return; }
            _showAllParts = GUILayout.Toggle(_showAllParts, $"Show every mesh (if a {what} is missing from the list)", None);

            if (_partsCacheOwner != anchors || Time.unscaledTime > _partsCacheTime)
            {
                _partsCache = anchors.FindParts(_showAllParts);
                _partsCacheOwner = anchors;
                _partsCacheTime = Time.unscaledTime + 1f;
            }
            if (_partsCache.Count == 0) Hint($"No {what}s found.");
            foreach (var part in _partsCache)
            {
                if (part.Renderer == null) continue;
                var rule = anchors.RuleFor(part.Name);
                GUILayout.BeginHorizontal(None);
                GUILayout.Label($"{part.Name}  ({part.Roots.Count} bone chain{(part.Roots.Count == 1 ? "" : "s")})\n    follows: {(part.CurrentAnchor != null ? part.CurrentAnchor.name : "?")}{(rule != null ? "  (custom)" : "")}", _small, None);
                var p = part;
                if (Button("Choose bone…", 105)) OpenPicker($"Bone for {part.Name}", t => { anchors.SetRule(p, t); _partsCacheTime = 0; });
                if (rule != null && Button("Reset", 50)) { anchors.SetRule(part, null); _partsCacheTime = 0; }
                GUILayout.EndHorizontal();
            }
        }

        private void DrawBody()
        {
            var s = Settings.Current;
            Text("Extra body sliders. They reshape the arms and legs (and the clothes on them) around the bone, so posing doesn't distort them.");
            bool changed = false;
            changed |= Slider("Bicep Size", ref s.BicepSize);
            changed |= Slider("Forearm Size", ref s.ForearmSize);
            changed |= Slider("Thigh Size", ref s.ThighSize);
            changed |= Slider("Calf Size", ref s.CalfSize);
            if (Button("Reset all")) { s.BicepSize = s.ForearmSize = s.ThighSize = s.CalfSize = 1f; changed = true; }
            if (changed) { Features.Body.MarkDirty(); Settings.Save(); }
            if (!Avatar.Found) Hint("No model is loaded.");
            Hint("Values are remembered and re-applied when VRoid rebuilds the body. They are part of the preview; see README for exporting.");
        }

        private bool Slider(string label, ref float v)
        {
            GUILayout.BeginHorizontal(None);
            GUILayout.Label(label, new[] { GUILayout.Width(100) });
            var nv = GUILayout.HorizontalSlider(v, 0.5f, 2f, None);
            GUILayout.Label($"{nv * 100:F0}%", new[] { GUILayout.Width(45) });
            if (Button("↺", 26)) nv = 1f;
            GUILayout.EndHorizontal();
            nv = Mathf.Round(nv * 100f) / 100f;
            if (Mathf.Approximately(nv, v)) return false;
            v = nv;
            return true;
        }

        private void DrawPose()
        {
            var p = Features.Pose;
            var s = Settings.Current;
            Header("Show Controller");
            var off = GUILayout.Toggle(s.ShowControllerDefaultOff, "Start with 'Show Controller' off when the pose editor opens", None);
            if (off != s.ShowControllerDefaultOff) { s.ShowControllerDefaultOff = off; Settings.Save(); }
            Hint(p.LastToggleInfo);

            Header("Load Pose (.fbx)");
            Text("Load a pose or animation from an FBX file (Mixamo, Blender, etc.). Animations play like VRoid's built-in ones: play/pause only, the pose can't be edited while loaded.");
            if (!Avatar.Found) Hint("No model is loaded.");
            if (Button("Load .fbx file…"))
            {
                var path = FileDialog.Open("Load pose or animation", "FBX files (*.fbx)|*.fbx|All files|*.*");
                if (!string.IsNullOrEmpty(path)) p.LoadFbx(path);
            }
            Status(p.Status);
            if (!p.Loaded) return;

            var take = p.Take;
            if (p.Scene.Takes.Count > 1)
            {
                GUILayout.BeginHorizontal(None);
                if (Button("◀", 30)) { p.TakeIndex = (p.TakeIndex - 1 + p.Scene.Takes.Count) % p.Scene.Takes.Count; p.Time0 = 0; }
                GUILayout.Label($"Animation {p.TakeIndex + 1}/{p.Scene.Takes.Count}: {take?.Name}", None);
                if (Button("▶", 30)) { p.TakeIndex = (p.TakeIndex + 1) % p.Scene.Takes.Count; p.Time0 = 0; }
                GUILayout.EndHorizontal();
            }
            if (take != null && take.Length > 0)
            {
                GUILayout.BeginHorizontal(None);
                if (Button(p.Paused ? "▶ Play" : "❚❚ Pause", 90)) p.Paused = !p.Paused;
                GUILayout.Label($"{p.Time0:F1}s / {take.Length:F1}s", None);
                GUILayout.EndHorizontal();
                var loop = GUILayout.Toggle(s.FbxLoop, "Loop", None);
                var rm = GUILayout.Toggle(s.FbxApplyRootMotion, "Allow the model to move around (root motion)", None);
                if (loop != s.FbxLoop || rm != s.FbxApplyRootMotion) { s.FbxLoop = loop; s.FbxApplyRootMotion = rm; Settings.Save(); }
            }
            if (Button("Unload (back to VRoid poses)")) p.Unload();
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
            Header("Shortcut");
            GUILayout.BeginHorizontal(None);
            GUILayout.Label("Open/close VRoid Plus:", new[] { GUILayout.Width(150) });
            GUILayout.Label(_recordingKey ? "press the new keys... (Esc cancels)" : Settings.Current.ToggleKey, None);
            if (!_recordingKey && Button("Change...", 80)) { _recordingKey = true; GUIUtility.keyboardControl = 0; }
            if (!_recordingKey && Button("Default", 60)) { Settings.Current.ToggleKey = Hotkey.Default; Settings.Save(); }
            GUILayout.EndHorizontal();

            Text("Body sliders, anchors and FBX poses work on their own. Face sets and hair merging edit VRoid's internal data, whose names differ between VRoid versions; they are set here (saved in mapping.json).");

            Header("Connections");
            MappingRow("Face data", ref m.FaceData);
            MappingRow("Face refresh (optional)", ref m.FaceRefresh);
            MappingRow("Hair group list", ref m.HairGroupList);
            MappingRow("Hair refresh (optional)", ref m.HairRefresh);
            GUILayout.BeginHorizontal(None);
            if (Button("Save mapping")) { Mapping.Save(); _setupStatus = "Saved " + Mapping.FilePath; }
            if (Button("Reload from file")) { Mapping.Load(); _setupStatus = "Reloaded."; }
            GUILayout.EndHorizontal();
            Status(_setupStatus);

            Header("Find VRoid's data");
            if (Button("Write type dump (for sharing / bug reports)")) _setupStatus = Explorer.DumpTypes();
            GUILayout.BeginHorizontal(None);
            GUILayout.Label("Search singletons:", new[] { GUILayout.Width(120) });
            _keyword = GUILayout.TextField(_keyword, None);
            if (Button("Find", 50)) _candidates = Explorer.FindCandidates(_keyword);
            GUILayout.EndHorizontal();
            if (_candidates != null)
            {
                if (_candidates.Count == 0) Hint("Nothing found.");
                foreach (var c in _candidates.Take(60))
                {
                    GUILayout.BeginHorizontal(None);
                    GUILayout.Label(c, _small, None);
                    if (Button("Explore", 65)) Navigate(c);
                    GUILayout.EndHorizontal();
                }
            }

            Header("Explorer");
            GUILayout.BeginHorizontal(None);
            _explorePath = GUILayout.TextField(_explorePath, None);
            if (Button("Go", 34)) Navigate(_explorePath);
            if (Button("Up", 34)) Navigate(Up(_explorePath));
            GUILayout.EndHorizontal();
            GUILayout.BeginHorizontal(None);
            if (Button("Use as Face data")) { m.FaceData = _explorePath; Mapping.Save(); _setupStatus = "Face data set."; }
            if (Button("Use as Hair group list")) { m.HairGroupList = _explorePath; Mapping.Save(); _setupStatus = "Hair group list set."; }
            GUILayout.EndHorizontal();
            if (_rowsError != null) Hint(_rowsError);
            if (_rows != null)
            {
                foreach (var r in _rows)
                {
                    GUILayout.BeginHorizontal(None);
                    GUILayout.Label($"{r.Name} : {r.Type} = {r.Value}", _small, None);
                    if (r.Expandable && Button("›", 26))
                        Navigate(r.Name.StartsWith("[") ? _explorePath + r.Name : (_explorePath.Contains("::") ? _explorePath + "." + r.Name : _explorePath + "::" + r.Name));
                    GUILayout.EndHorizontal();
                }
            }
        }

        private void MappingRow(string label, ref string value)
        {
            GUILayout.BeginHorizontal(None);
            GUILayout.Label(label, new[] { GUILayout.Width(150) });
            value = GUILayout.TextField(value ?? "", None);
            GUILayout.EndHorizontal();
            if (!string.IsNullOrWhiteSpace(value))
            {
                var o = ReflectionPath.Resolve(value, out var err);
                Hint(o != null ? "    ✓ found " + o.GetType().Name + (ListAccess.IsList(o) ? $" ({ListAccess.Count(o)} items)" : "")
                               : value.TrimEnd().EndsWith("()") ? "    (method, runs when used)" : "    ✗ " + err);
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
            _picker = onPick; _pickerTitle = title; _scroll = Vector2.zero;
        }

        private void DrawBonePicker()
        {
            GUILayout.Label(_pickerTitle, _title, None);
            if (Button("Cancel")) { _picker = null; return; }
            GUILayout.BeginHorizontal(None);
            GUILayout.Label("Filter:", new[] { GUILayout.Width(45) });
            _boneFilter = GUILayout.TextField(_boneFilter, None);
            GUILayout.EndHorizontal();
            _humanoidOnly = GUILayout.Toggle(_humanoidOnly, "Main skeleton bones only", None);

            var bones = Avatar.AllBones.Where(b => b != null);
            if (_humanoidOnly) bones = bones.Where(Avatar.IsHumanoid);
            foreach (var b in bones)
            {
                var human = Avatar.HumanName(b);
                var label = human != null ? $"{human}  ({b.name})" : Avatar.Path(b);
                if (_boneFilter.Length > 0 && label.IndexOf(_boneFilter, StringComparison.OrdinalIgnoreCase) < 0) continue;
                if (GUILayout.Button(label, None))
                {
                    var pick = _picker; _picker = null;
                    pick(b);
                    return;
                }
            }
        }
    }
}
