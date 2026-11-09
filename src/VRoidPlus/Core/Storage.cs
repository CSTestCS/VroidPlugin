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
        public string ToggleKey = "F8";
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
        public static void Load() => Current = Json.Read<SettingsData>(FilePath);
        public static void Save() => Safe.Run("Settings.Save", () => Json.Write(FilePath, Current));

        public static KeyCode ToggleKeyCode =>
            Enum.TryParse<KeyCode>(Current.ToggleKey, true, out var k) ? k : KeyCode.F8;
    }
}
