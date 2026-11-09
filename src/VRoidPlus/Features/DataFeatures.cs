using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace VRoidPlus
{
    /// <summary>
    /// Custom face sets: saves every face selection and slider (the whole face object VRoid edits) under a
    /// name, and loads it back onto any avatar - the same idea as VRoid's built-in face sets.
    /// </summary>
    internal class FaceSets
    {
        public readonly NamedStore Store = new("FaceSets");
        public string Status = "";

        public bool IsMapped(out string error)
        {
            var o = ReflectionPath.Resolve(Mapping.Current.FaceData, out error);
            return o != null;
        }

        public void Save(string name)
        {
            var face = ReflectionPath.Resolve(Mapping.Current.FaceData, out var err);
            if (face == null) { Status = "Face data not found: " + err; return; }
            var snap = new ObjectSnapshot { Exclude = new HashSet<string>(Mapping.Current.FaceExclude ?? Array.Empty<string>()) };
            var json = snap.Capture(face);
            json["$vroidplus"] = "faceset";
            json["$saved"] = DateTime.Now.ToString("s");
            Store.Save(name, json.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
            Status = $"Saved face set '{name}' ({json.Count - 3} values).";
        }

        public void Load(string name)
        {
            var face = ReflectionPath.Resolve(Mapping.Current.FaceData, out var err);
            if (face == null) { Status = "Face data not found: " + err; return; }
            var data = JsonNode.Parse(Store.Load(name)) as JsonObject;
            if (data == null) { Status = "File is not a face set."; return; }
            var snap = new ObjectSnapshot { Exclude = new HashSet<string>(Mapping.Current.FaceExclude ?? Array.Empty<string>()) };
            snap.Restore(face, data);
            var refreshed = ReflectionPath.Invoke(Mapping.Current.FaceRefresh, out var rerr);
            Status = $"Applied face set '{name}'." +
                     (snap.Warnings.Count > 0 ? $" {snap.Warnings.Count} value(s) skipped (see log)." : "") +
                     (refreshed ? "" : " Refresh failed: " + rerr);
            foreach (var w in snap.Warnings.Take(50)) Plugin.Logger.LogWarning("Face set: " + w);
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
        public static readonly BoneAnchors HairAnchors = new("Hair", "hair-anchors.json", () => Mapping.Current.HairNamePatterns);
        public static readonly BoneAnchors AccessoryAnchors = new("Accessory", "accessory-anchors.json", () => Mapping.Current.AccessoryNamePatterns);
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
