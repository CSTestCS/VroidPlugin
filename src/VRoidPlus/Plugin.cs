using System;
using System.IO;
using BepInEx;
using BepInEx.Logging;
using BepInEx.Unity.IL2CPP;
using UnityEngine;

namespace VRoidPlus
{
    [BepInPlugin(Guid, Name, Version)]
    public class Plugin : BasePlugin
    {
        public const string Guid = "com.vroidplus.plugin";
        public const string Name = "VRoid Plus";
        public const string Version = "0.1.7";

        internal static ManualLogSource Logger;

        /// <summary>BepInEx/config/VRoidPlus — every file the plugin saves lives here.</summary>
        internal static string DataDir;

        public override void Load()
        {
            Logger = Log;
            DataDir = Path.Combine(Paths.ConfigPath, "VRoidPlus");
            Directory.CreateDirectory(DataDir);

            Settings.Load();
            Mapping.Load();

#if IL2CPP
            AddComponent<Runner>();
#endif
            Logger.LogInfo($"{Name} {Version} loaded. Press {Settings.Current.ToggleKey} in VRoid Studio to open the window.");
        }
    }

    /// <summary>
    /// The one MonoBehaviour the plugin injects. It forwards Unity callbacks to the features.
    /// </summary>
    public class Runner : MonoBehaviour
    {
#if IL2CPP
        public Runner(IntPtr ptr) : base(ptr) { }
#endif
        private MainWindow _window;

        public void Awake()
        {
            DontDestroyOnLoad(gameObject);
            gameObject.hideFlags = HideFlags.HideAndDontSave;
            _window = new MainWindow();
        }

        public void Update()
        {
            Safe.Run("Update", () => Features.Update());
        }

        public void LateUpdate()
        {
            Safe.Run("LateUpdate", () => Features.LateUpdate());
        }

        public void OnGUI()
        {
            Safe.Run("OnGUI", () => _window.OnGUI());
        }
    }

    internal static class Safe
    {
        private static readonly System.Collections.Generic.Dictionary<string, int> ErrorCounts = new();

        /// <summary>Runs an action and logs (rate-limited) instead of letting an exception reach Unity.</summary>
        public static void Run(string where, Action a)
        {
            try { a(); }
            catch (Exception e)
            {
                ErrorCounts.TryGetValue(where, out var n);
                ErrorCounts[where] = n + 1;
                if (n < 5) Plugin.Logger?.LogError($"[{where}] {e}");
                else if (n == 5) Plugin.Logger?.LogError($"[{where}] further errors suppressed");
            }
        }
    }
}
