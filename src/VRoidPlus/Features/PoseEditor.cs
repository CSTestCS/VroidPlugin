using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEngine;
using VRoidPlus.Fbx;

namespace VRoidPlus
{
    /// <summary>
    /// Pose editor additions:
    ///  * "Show Controller" starts switched off every time the pose editor opens.
    ///  * Load an .fbx (static pose or animation). It plays like VRoid's built-in animations: play/pause only,
    ///    the pose can't be edited while it is loaded.
    /// </summary>
    internal class PoseEditor
    {
        // ------------------------------------------------ Show Controller default off

        private readonly HashSet<int> _visibleToggles = new();
        private float _nextToggleScan;
        public string LastToggleInfo = "Not seen yet - open the pose editor.";

        private void UpdateShowControllerToggle()
        {
            if (Time.unscaledTime < _nextToggleScan) return;
            _nextToggleScan = Time.unscaledTime + 0.4f;
            var labels = Settings.Current.ShowControllerLabels ?? Array.Empty<string>();

            var nowVisible = new HashSet<int>();
            foreach (var toggle in Compat.FindComponentsByTypeName("UnityEngine.UI.Toggle, UnityEngine.UI"))
            {
                if (toggle == null || !toggle.gameObject.activeInHierarchy) continue;
                var text = Compat.GetLabelText(toggle);
                var goName = toggle.gameObject.name;
                bool match = labels.Any(l => !string.IsNullOrEmpty(l) &&
                    (text.IndexOf(l, StringComparison.OrdinalIgnoreCase) >= 0 || goName.IndexOf(l.Replace(" ", ""), StringComparison.OrdinalIgnoreCase) >= 0));
                if (!match) continue;

                int id = toggle.GetInstanceID();
                nowVisible.Add(id);
                if (_visibleToggles.Contains(id)) continue; // already handled while it stays on screen

                // Just appeared (the pose editor was opened): switch it off once. The user can turn it back on.
                if ((Settings.Current.ShowControllerDefaultOff || Playing) && Compat.TrySetToggle(toggle, false, out var was))
                    LastToggleInfo = $"Found '{(text.Length > 0 ? text : goName)}' (was {(was ? "on" : "off")}) - set off.";
            }
            _visibleToggles.Clear();
            foreach (var id in nowVisible) _visibleToggles.Add(id);
        }

        /// <summary>While an FBX is loaded, keep the controllers hidden (the pose isn't editable then).</summary>
        private void ForceControllersOff()
        {
            foreach (var toggle in Compat.FindComponentsByTypeName("UnityEngine.UI.Toggle, UnityEngine.UI"))
            {
                if (toggle == null || !toggle.gameObject.activeInHierarchy) continue;
                var text = Compat.GetLabelText(toggle);
                if (Settings.Current.ShowControllerLabels.Any(l => text.IndexOf(l, StringComparison.OrdinalIgnoreCase) >= 0))
                    Compat.TrySetToggle(toggle, false, out _);
            }
        }

        // ------------------------------------------------ FBX playback

        public FbxScene Scene;
        public Retargeter Retarget;
        public string FilePath;
        public int TakeIndex;
        public double Time0;
        public bool Paused;
        public string Status = "";
        private float _nextForceOff;

        public bool Loaded => Scene != null && Retarget != null;
        public bool Playing => Loaded && !Paused;
        public FbxTake Take => Loaded && Scene.Takes.Count > 0 ? Scene.Takes[Mathf.Clamp(TakeIndex, 0, Scene.Takes.Count - 1)] : null;

        public void LoadFbx(string path)
        {
            Unload();
            try
            {
                var scene = FbxScene.Load(path);
                var rt = new Retargeter(scene);
                var animator = Avatar.Animator;
                if (animator == null) throw new InvalidOperationException("No avatar found in the scene.");
                rt.Attach(animator);
                Scene = scene; Retarget = rt; FilePath = path; TakeIndex = 0; Time0 = 0; Paused = false;
                var take = Take;
                Status = $"Loaded {Path.GetFileName(path)}: {rt.MappedCount} humanoid bones, " +
                         (take != null && take.Length > 0 ? $"{Scene.Takes.Count} animation(s), {take.Length:F1}s" : "static pose");
                ForceControllersOff();
            }
            catch (Exception e)
            {
                Status = "Could not load: " + e.Message;
                Plugin.Logger.LogError(e);
                Unload(false);
            }
        }

        public void Unload(bool clearStatus = true)
        {
            Retarget?.Release();
            Scene = null; Retarget = null; FilePath = null;
            if (clearStatus) Status = "";
        }

        public void Update()
        {
            UpdateShowControllerToggle();
            if (!Loaded) return;
            if (!Paused)
            {
                var take = Take;
                var len = take?.Length ?? 0;
                Time0 += Time.unscaledDeltaTime;
                if (len > 0 && Time0 > len) Time0 = Settings.Current.FbxLoop ? Time0 % len : len;
            }
            if (Time.unscaledTime > _nextForceOff) { _nextForceOff = Time.unscaledTime + 1f; ForceControllersOff(); }
        }

        /// <summary>Runs after VRoid's animator/pose has been applied, so the FBX pose wins.</summary>
        public void LateUpdate()
        {
            if (!Loaded) return;
            if (Avatar.Animator == null) { Unload(); Status = "Avatar was unloaded."; return; }
            Retarget.Apply(Take, Time0, Settings.Current.FbxApplyRootMotion);
        }
    }
}
