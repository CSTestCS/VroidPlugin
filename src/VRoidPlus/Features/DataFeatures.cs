using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using UnityEngine;

namespace VRoidPlus
{
    /// <summary>
    /// Custom face sets: saves every slider and color of every face item (face shape, eyebrows, eyelids,
    /// eyelashes, eyelines, irises, eye whites, highlights, nose, mouth, lips, cheeks, ears, face skin)
    /// under a name, and applies them to the open model, like VRoid's built-in face sets.
    /// Uses VRoid's own item/parameter data (see VRoidApi), so no mapping is needed.
    /// </summary>
    internal class FaceSets
    {
        public readonly NamedStore Store = new("FaceSets");
        public string Status = "";

        private static readonly string[] FaceTypes =
        {
            "FaceShape", "Eyebrow", "Eyelid", "Eyeline", "Eyelash", "Iris", "EyeWhite", "EyeHighlight", "EyeSurrounding",
            "Nose", "NoseLighting", "Mouth", "MouthLine", "Lip", "Cheek", "FaceSkin", "Ear",
        };

        public static bool IsFaceType(string type)
        {
            var i = type.LastIndexOf('.');
            var last = i >= 0 ? type.Substring(i + 1) : type;
            return FaceTypes.Contains(last);
        }

        public static string ShortType(string type) { var i = type.LastIndexOf('.'); return i >= 0 ? type.Substring(i + 1) : type; }

        public bool IsMapped(out string error) => VRoidApi.Context(out error) != null;

        public void Save(string name)
        {
            var ctx = VRoidApi.Context(out var err);
            if (ctx == null) { Status = "Can't save: " + err; return; }
            var parts = new JsonArray();
            int values = 0;
            foreach (var g in VRoidApi.Groups(ctx).Where(g => IsFaceType(g.Type)))
            {
                var ps = new JsonArray();
                foreach (var p in VRoidApi.Parameters(ctx, g.Id))
                {
                    var o = new JsonObject { ["key"] = p.Key, ["kind"] = p.Kind };
                    if (p.Kind == "Color") { o["r"] = p.Color.r; o["g"] = p.Color.g; o["b"] = p.Color.b; o["a"] = p.Color.a; }
                    else o["value"] = p.Value;
                    ps.Add(o);
                    values++;
                }
                parts.Add(new JsonObject { ["type"] = g.Type, ["resource"] = g.ResourceName, ["params"] = ps });
            }
            if (parts.Count == 0) { Status = "No face items found on this model."; return; }
            var file = new JsonObject { ["$vroidplus"] = "faceset2", ["$saved"] = DateTime.Now.ToString("s"), ["parts"] = parts };
            Store.Save(name, file.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
            Status = $"Saved face set '{name}': {parts.Count} face parts, {values} values.";
        }

        public void Load(string name)
        {
            var ctx = VRoidApi.Context(out var err);
            if (ctx == null) { Status = "Can't load: " + err; return; }
            var file = JsonNode.Parse(Store.Load(name)) as JsonObject;
            if (file?["parts"] is not JsonArray parts) { Status = "This face set was saved by an older VRoid Plus version and can't be loaded. Please save it again."; return; }

            var groups = VRoidApi.Groups(ctx).Where(g => IsFaceType(g.Type)).ToList();
            var used = new HashSet<string>();
            var changed = new List<string>();
            int ok = 0, failed = 0, missingParts = 0;
            string firstError = null;
            var presetDiffers = new List<string>();

            foreach (var part in parts.OfType<JsonObject>())
            {
                var type = (string)part["type"];
                var target = groups.FirstOrDefault(g => g.Type == type && !used.Contains(g.Id));
                if (target == null) { missingParts++; continue; }
                used.Add(target.Id);
                var savedRes = (string)part["resource"];
                if (!string.IsNullOrEmpty(savedRes) && savedRes != target.ResourceName) presetDiffers.Add(ShortType(type));

                var current = VRoidApi.Parameters(ctx, target.Id).ToDictionary(p => p.Key);
                foreach (var po in (part["params"] as JsonArray ?? new JsonArray()).OfType<JsonObject>())
                {
                    var key = (string)po["key"];
                    if (key == null || !current.ContainsKey(key)) continue;   // this preset has no such slider
                    var p = new VRoidApi.Param { Key = key, Kind = (string)po["kind"] };
                    if (p.Kind == "Color") p.Color = new Color(F(po, "r"), F(po, "g"), F(po, "b"), F(po, "a", 1f));
                    else p.Value = F(po, "value");
                    var e = VRoidApi.SetParameter(ctx, target.Id, p);
                    if (e == null) ok++; else { failed++; firstError ??= $"{ShortType(type)}.{key}: {e}"; }
                }
                changed.Add(target.Id);
            }
            VRoidApi.NotifyChanged(ctx, changed);

            Status = $"Applied face set '{name}': {ok} values set" +
                     (failed > 0 ? $", {failed} failed (first: {firstError})" : "") +
                     (missingParts > 0 ? $", {missingParts} parts not on this model" : "") + "." +
                     (presetDiffers.Count > 0 ? $" These parts use a different preset than when saved, so their shape may differ: {string.Join(", ", presetDiffers)}." : "") +
                     " Undo (Ctrl+Z) doesn't cover this change yet.";
            if (failed > 0) Plugin.Logger.LogWarning("Face set: " + firstError);
        }

        private static float F(JsonObject o, string k, float def = 0f)
        {
            var n = o[k];
            if (n == null) return def;
            try { return (float)n.GetValue<double>(); } catch { try { return n.GetValue<float>(); } catch { return def; } }
        }
    }

    /// <summary>
    /// Hair merge: copy the hair groups of any hairstyle you can open in VRoid (built-in or custom, any slot -
    /// Front, Back, Overall, Extensions, Side, Ahoge, Extra) into a library, then add them to the hairstyle you
    /// are editing. The edited hairstyle keeps all its own groups and gains the imported ones.
    /// </summary>
    internal class HairMerge
    {
        public readonly NamedStore Library = new("HairLibrary");
        public string Status = "";

        public object GroupList(out string error)
        {
            var list = ReflectionPath.Resolve(Mapping.Current.HairGroupList, out error);
            if (list != null && !ListAccess.IsList(list)) { error = "HairGroupList does not point at a list"; return null; }
            return list;
        }

        /// <summary>Stores every group of the hairstyle currently open in the editor.</summary>
        public void Capture(string name)
        {
            var list = GroupList(out var err);
            if (list == null) { Status = "Hair groups not found: " + err; return; }
            var snap = new ObjectSnapshot();
            var arr = new JsonArray();
            int n = ListAccess.Count(list);
            for (int i = 0; i < n; i++)
            {
                var g = ListAccess.Get(list, i);
                if (g != null) arr.Add(snap.Capture(g));
            }
            var file = new JsonObject { ["$vroidplus"] = "hairgroups", ["$saved"] = DateTime.Now.ToString("s"), ["groups"] = arr };
            Library.Save(name, file.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
            Status = $"Copied {arr.Count} hair group(s) to the library as '{name}'.";
        }

        /// <summary>Appends a library entry's groups to the hairstyle being edited (its own groups are kept).</summary>
        public void Import(string name)
        {
            var list = GroupList(out var err);
            if (list == null) { Status = "Hair groups not found: " + err; return; }
            if (!ListAccess.IsResizable(list)) { Status = "The hair group list can't be added to."; return; }
            var file = JsonNode.Parse(Library.Load(name)) as JsonObject;
            if (file?["groups"] is not JsonArray groups) { Status = "Not a hair library file."; return; }

            var elem = ListAccess.ElementType(list);
            var snap = new ObjectSnapshot { RegenerateIds = new HashSet<string>(Mapping.Current.HairIdProperties ?? Array.Empty<string>()) };
            int added = 0;
            foreach (var g in groups.OfType<JsonObject>())
            {
                var obj = snap.CreateFrom(g, elem);
                if (obj == null) continue;
                ListAccess.Add(list, Compat.CastTo(obj, elem) ?? obj);
                added++;
            }
            var refreshed = ReflectionPath.Invoke(Mapping.Current.HairRefresh, out var rerr);
            Status = $"Imported {added} of {groups.Count} group(s) from '{name}'." +
                     (snap.Warnings.Count > 0 ? $" {snap.Warnings.Count} value(s) skipped (see log)." : "") +
                     (refreshed ? "" : " Refresh failed: " + rerr);
            foreach (var w in snap.Warnings.Take(50)) Plugin.Logger.LogWarning("Hair import: " + w);
        }
    }

    /// <summary>All features, so the window and the runner can reach them.</summary>
    internal static class Features
    {
        public static readonly FaceSets FaceSets = new();
        public static readonly HairMerge HairMerge = new();
        public static readonly BoneAnchors HairAnchors = new(BoneAnchors.Kind.Hair, "hair-anchors-v2.json");
        public static readonly BoneAnchors AccessoryAnchors = new(BoneAnchors.Kind.Accessory, "accessory-anchors-v2.json");
        public static readonly BodySliders Body = new();
        public static readonly PoseEditor Pose = new();

        public static void Update()
        {
            Avatar.Refresh();
            Safe.Run("HairAnchors", HairAnchors.Update);
            Safe.Run("AccessoryAnchors", AccessoryAnchors.Update);
            Safe.Run("Body", Body.Update);
            Safe.Run("Pose", Pose.Update);
        }

        public static void LateUpdate()
        {
            Safe.Run("Pose.Late", Pose.LateUpdate);
        }
    }
}
