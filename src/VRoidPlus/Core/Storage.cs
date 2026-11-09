using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using UnityEngine;

namespace VRoidPlus
{
    internal static class Json
    {
        public static readonly JsonSerializerOptions Options = new()
        {
            WriteIndented = true,
            IncludeFields = true,
            ReadCommentHandling = JsonCommentHandling.Skip,
            AllowTrailingCommas = true,
            DefaultIgnoreCondition = JsonIgnoreCondition.Never,
        };

        public static T Read<T>(string path) where T : new()
        {
            try
            {
                if (File.Exists(path)) return JsonSerializer.Deserialize<T>(File.ReadAllText(path), Options) ?? new T();
            }
            catch (Exception e) { Plugin.Logger.LogError($"Could not read {path}: {e.Message}"); }
            return new T();
        }

        public static void Write<T>(string path, T value)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, JsonSerializer.Serialize(value, Options));
        }
    }

    /// <summary>A folder of named JSON files (face sets, hair library).</summary>
    internal class NamedStore
    {
        private readonly string _dir;
        public NamedStore(string folder) { _dir = Path.Combine(Plugin.DataDir, folder); Directory.CreateDirectory(_dir); }

        public string Folder => _dir;

        public List<string> List() =>
            Directory.GetFiles(_dir, "*.json").Select(Path.GetFileNameWithoutExtension).OrderBy(n => n, StringComparer.OrdinalIgnoreCase).ToList();

        public string PathFor(string name) => Path.Combine(_dir, Sanitize(name) + ".json");
        public bool Exists(string name) => File.Exists(PathFor(name));
        public void Save(string name, string json) => File.WriteAllText(PathFor(name), json);
        public string Load(string name) => File.ReadAllText(PathFor(name));
        public void Delete(string name) { if (Exists(name)) File.Delete(PathFor(name)); }

        public static string Sanitize(string name)
        {
            foreach (var c in Path.GetInvalidFileNameChars()) name = name.Replace(c, '_');
            name = name.Trim();
            return name.Length == 0 ? "Untitled" : name;
        }
    }

    public class SettingsData
    {
        /// <summary>Shortcut that opens/closes the window, e.g. "Ctrl+Shift+V". F8 is avoided: VRoid uses it.</summary>
        public string ToggleKey = Hotkey.Default;
        public int SettingsVersion = 0; // 0 = file from before versioning (or none); Load() upgrades it
        public bool ShowControllerDefaultOff = true;
        public string[] ShowControllerLabels =
        {
            "Show Controller", "Show Controllers", "Show controller", "Display Controller",
            "コントローラーを表示", "コントローラー表示", "コントローラ表示", "显示控制器", "顯示控制器", "컨트롤러 표시",
        };
        public float BicepSize = 1f;
        public float ForearmSize = 1f;
        public float ThighSize = 1f;
        public float CalfSize = 1f;
        public bool FbxLoop = true;
        public bool FbxApplyRootMotion = false;
        public float WindowX = 20, WindowY = 60, WindowW = 460, WindowH = 620;
    }

    internal static class Settings
    {
        public static SettingsData Current = new();
        private static string FilePath => Path.Combine(Plugin.DataDir, "settings.json");
        public static void Load()
        {
            Current = Json.Read<SettingsData>(FilePath);
            // Version 1 defaulted to F8, which clashes with a VRoid shortcut.
            if (Current.SettingsVersion < 2)
            {
                if (string.Equals(Current.ToggleKey, "F8", StringComparison.OrdinalIgnoreCase)) Current.ToggleKey = Hotkey.Default;
                Current.SettingsVersion = 2;
                Save();
            }
            if (!Hotkey.TryParse(Current.ToggleKey, out _)) Current.ToggleKey = Hotkey.Default;
        }
        public static void Save() => Safe.Run("Settings.Save", () => Json.Write(FilePath, Current));

        public static Hotkey Toggle => Hotkey.TryParse(Current.ToggleKey, out var h) ? h : Hotkey.Parse(Hotkey.Default);
    }

    /// <summary>A key plus optional Ctrl/Shift/Alt, written like "Ctrl+Shift+V".</summary>
    internal struct Hotkey
    {
        public const string Default = "Ctrl+Shift+V";
        public KeyCode Key;
        public bool Ctrl, Shift, Alt;

        public static Hotkey Parse(string s) => TryParse(s, out var h) ? h : new Hotkey { Key = KeyCode.V, Ctrl = true, Shift = true };

        public static bool TryParse(string s, out Hotkey h)
        {
            h = default;
            if (string.IsNullOrWhiteSpace(s)) return false;
            foreach (var raw in s.Split('+'))
            {
                var part = raw.Trim();
                if (part.Equals("Ctrl", StringComparison.OrdinalIgnoreCase) || part.Equals("Control", StringComparison.OrdinalIgnoreCase)) h.Ctrl = true;
                else if (part.Equals("Shift", StringComparison.OrdinalIgnoreCase)) h.Shift = true;
                else if (part.Equals("Alt", StringComparison.OrdinalIgnoreCase)) h.Alt = true;
                else if (Enum.TryParse<KeyCode>(part, true, out var k) && k != KeyCode.None) h.Key = k;
                else return false;
            }
            return h.Key != KeyCode.None;
        }

        public bool Matches(Event e) =>
            e != null && e.type == EventType.KeyDown && e.keyCode == Key && e.control == Ctrl && e.shift == Shift && e.alt == Alt;

        public static bool IsModifier(KeyCode k) =>
            k == KeyCode.LeftControl || k == KeyCode.RightControl || k == KeyCode.LeftShift || k == KeyCode.RightShift ||
            k == KeyCode.LeftAlt || k == KeyCode.RightAlt || k == KeyCode.LeftCommand || k == KeyCode.RightCommand;

        public override string ToString() => (Ctrl ? "Ctrl+" : "") + (Shift ? "Shift+" : "") + (Alt ? "Alt+" : "") + Key;
    }
}
