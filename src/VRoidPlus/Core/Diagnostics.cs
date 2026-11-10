using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Reflection;
using System.Text;
using UnityEngine;

namespace VRoidPlus
{
    /// <summary>
    /// Writes everything needed to fit VRoid Plus to the user's VRoid version into one ZIP on the Desktop:
    ///   scene.txt     every object in the scene with its components (model layout, meshes, bones, UI labels)
    ///   data.txt      live contents of VRoid's own objects and singletons (where face/hair/accessory data lives)
    ///   types.txt     VRoid's internal classes and their members
    ///   status.txt    what each VRoid Plus feature found
    ///   LogOutput.log, settings and mapping files
    /// Nothing is changed in VRoid; this only reads.
    /// </summary>
    internal static class Diagnostics
    {
        private const int MaxSceneLines = 250_000;

        // Members read through reflection: if VRoid's Unity build lacks one, the plugin still compiles
        // and the value just shows as "?".
        private static object P(object o, string name)
        {
            if (o == null) return null;
            try
            {
                var p = o.GetType().GetProperty(name, BindingFlags.Instance | BindingFlags.Public);
                return p != null ? p.GetValue(o) : "?";
            }
            catch { return "?"; }
        }

        private static object SP(Type t, string name)
        {
            try { return t.GetProperty(name, BindingFlags.Static | BindingFlags.Public)?.GetValue(null) ?? "?"; }
            catch { return "?"; }
        }

        private static string N(object unityObj) => unityObj is UnityEngine.Object uo ? (uo != null ? uo.name : "null") : (unityObj?.ToString() ?? "null");

        private static System.Collections.IEnumerable CallGeneric(object target, Type owner, string method, Type arg, params object[] args)
        {
            var m = owner.GetMethods(BindingFlags.Public | (target == null ? BindingFlags.Static : BindingFlags.Instance))
                .FirstOrDefault(x => x.Name == method && x.IsGenericMethodDefinition && x.GetParameters().Length == args.Length);
            if (m == null) return null;
            return m.MakeGenericMethod(arg).Invoke(target, args) as System.Collections.IEnumerable;
        }
        private const int MaxDataLines = 200_000;

        public static string Capture()
        {
            var stamp = DateTime.Now.ToString("yyyyMMdd-HHmmss");
            var dir = Path.Combine(Plugin.DataDir, "diagnostics", stamp);
            Directory.CreateDirectory(dir);
            var notes = new StringBuilder();

            var desktop = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
            if (string.IsNullOrEmpty(desktop) || !Directory.Exists(desktop)) desktop = Plugin.DataDir;
            var zip = Path.Combine(desktop, $"VRoidPlus-diagnostics-{stamp}.zip");

            Section(notes, "status", () => File.WriteAllText(Path.Combine(dir, "status.txt"), Status()));
            Section(notes, "scene", () => File.WriteAllText(Path.Combine(dir, "scene.txt"), Scene()));
            Section(notes, "types", () => { Explorer.DumpTypes(); File.Copy(Path.Combine(Plugin.DataDir, "type-dump.txt"), Path.Combine(dir, "types.txt"), true); });
            Section(notes, "files", () =>
            {
                CopyShared(Path.Combine(BepInEx.Paths.BepInExRootPath, "LogOutput.log"), Path.Combine(dir, "LogOutput.log"));
                foreach (var f in new[] { "settings.json", "mapping.json", "hair-anchors.json", "accessory-anchors.json" })
                    if (File.Exists(Path.Combine(Plugin.DataDir, f))) File.Copy(Path.Combine(Plugin.DataDir, f), Path.Combine(dir, f), true);
            });
            // Zip once before reading VRoid's live data: if that step crashes VRoid, this ZIP still exists.
            notes.AppendLine("data: not captured yet (if this line is in the ZIP, VRoid closed while reading its data)");
            MakeZip(dir, zip, notes);
            Section(notes, "data", () => Data(Path.Combine(dir, "data.txt")));
            MakeZip(dir, zip, notes);
            return zip;
        }

        private static void MakeZip(string dir, string zip, StringBuilder notes)
        {
            File.WriteAllText(Path.Combine(dir, "capture-notes.txt"), notes.ToString());
            if (File.Exists(zip)) File.Delete(zip);
            ZipFile.CreateFromDirectory(dir, zip, System.IO.Compression.CompressionLevel.Optimal, false);
        }

        private static void Section(StringBuilder notes, string name, Action a)
        {
            var t = DateTime.Now;
            try { a(); notes.AppendLine($"{name}: ok ({(DateTime.Now - t).TotalSeconds:F1}s)"); }
            catch (Exception e) { notes.AppendLine($"{name}: FAILED {e}"); Plugin.Logger.LogError($"Diagnostics {name}: {e}"); }
        }

        private static void CopyShared(string src, string dst)
        {
            if (!File.Exists(src)) return;
            using var input = new FileStream(src, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var output = File.Create(dst);
            input.CopyTo(output);
        }

        // ------------------------------------------------------------------ status

        private static string Status()
        {
            var sb = new StringBuilder();
            sb.AppendLine($"VRoid Plus {Plugin.Version}, Unity {SP(typeof(Application), "unityVersion")}, app {SP(typeof(Application), "productName")} {SP(typeof(Application), "version")}");
            sb.AppendLine($"Screen {Screen.width}x{Screen.height}, time {Time.unscaledTime:F0}s");
            Avatar.Refresh(true);
            sb.AppendLine();
            sb.AppendLine("== Humanoid animators in scene");
            foreach (var a in UnityEngine.Object.FindObjectsOfType<Animator>())
            {
                if (a == null) continue;
                Safe.Run("diag.animator", () =>
                    sb.AppendLine($"  {PathOf(a.transform)}  human={a.isHuman} enabled={a.enabled} active={a.gameObject.activeInHierarchy} avatar={N(P(a, "avatar"))} smr={a.GetComponentsInChildren<SkinnedMeshRenderer>(true).Length} head={(a.isHuman && a.GetBoneTransform(HumanBodyBones.Head) != null ? PathOf(a.GetBoneTransform(HumanBodyBones.Head)) : "-")}"));
            }
            sb.AppendLine($"Chosen avatar: {(Avatar.Animator != null ? PathOf(Avatar.Animator.transform) : "NONE")}, bones {Avatar.AllBones.Count}, humanoid {Avatar.HumanoidBones.Count}");
            sb.AppendLine();
            sb.AppendLine("== Body sliders");
            sb.AppendLine("  " + Features.Body.LastInfo);
            sb.AppendLine($"  values: {string.Join(", ", BodySliders.Sliders.Select(d => $"{d.Id} {Features.Body.Get(d.Id)}"))}; file {Features.Body.File.Path ?? "(unsaved)"}, unsaved changes {Features.Body.File.HasUnsavedChanges}");
            foreach (var (title, anchors) in new[] { ("Hair", Features.HairAnchors), ("Accessory", Features.AccessoryAnchors) })
            {
                sb.AppendLine();
                sb.AppendLine($"== {title} anchors (pattern match / all meshes)");
                Safe.Run("diag.anchors", () =>
                {
                    var matched = anchors.FindItems(false);
                    var all = anchors.FindItems(true);
                    sb.AppendLine($"  matched {matched.Count}, all items {all.Count}");
                    foreach (var p in all.Take(60)) sb.AppendLine($"    {(p.IsHair ? "hair" : p.IsAccessory ? "accessory" : "other")}: {p.Label}: id {p.Id}, {p.Roots.Count} roots, {p.Meshes.Count} meshes, origin {(p.PartOrigin != null ? PathOf(p.PartOrigin) : "-")}, anchor {p.CurrentAnchor?.name}");
                });
            }
            sb.AppendLine();
            sb.AppendLine("== Pose");
            sb.AppendLine($"  {Features.Pose.Status} | loaded={Features.Pose.Loaded} | toggle: {Features.Pose.LastToggleInfo}");
            sb.AppendLine();
            sb.AppendLine("== Mapping");
            sb.AppendLine($"  FaceData='{Mapping.Current.FaceData}' HairGroupList='{Mapping.Current.HairGroupList}'");
            return sb.ToString();
        }

        // ------------------------------------------------------------------ scene

        private static string Scene()
        {
            var sb = new StringBuilder();
            var all = new List<Transform>();
            try
            {
                var found = CallGeneric(null, typeof(Resources), "FindObjectsOfTypeAll", typeof(Transform));
                if (found == null) throw new MissingMethodException("Resources.FindObjectsOfTypeAll");
                foreach (var o in found) if (o is Transform t && t != null) all.Add(t);
                sb.AppendLine($"# all transforms incl. inactive: {all.Count}");
            }
            catch (Exception e)
            {
                sb.AppendLine("# FindObjectsOfTypeAll unavailable (" + (e.InnerException ?? e).Message + "); active objects only");
                foreach (var t in UnityEngine.Object.FindObjectsOfType<Transform>()) if (t != null) all.Add(t);
            }
            // Children lists from parent links (no childCount/GetChild needed). Inactive scene objects are
            // included; assets (prefabs) are too, which is fine.
            var children = new Dictionary<int, List<Transform>>();
            var roots = new List<Transform>();
            foreach (var t in all)
            {
                var p = t.parent;
                if (p == null) { roots.Add(t); continue; }
                var key = p.GetInstanceID();
                if (!children.TryGetValue(key, out var list)) children[key] = list = new List<Transform>();
                list.Add(t);
            }
            int lines = 0;
            foreach (var r in roots.OrderBy(r => r.name)) Walk(sb, r, 0, children, ref lines);
            if (lines >= MaxSceneLines) sb.AppendLine("# (truncated)");
            return sb.ToString();
        }

        private static void Walk(StringBuilder sb, Transform t, int depth, Dictionary<int, List<Transform>> children, ref int lines)
        {
            if (lines++ >= MaxSceneLines || t == null) return;
            var go = t.gameObject;
            sb.Append(' ', depth * 2).Append(go.activeInHierarchy ? "" : "(off) ").Append(t.name);
            bool rect = Compat.RealTypeName(t) == "UnityEngine.RectTransform";
            if (!rect)
            {
                try
                {
                    var lp = t.localPosition; var lr = t.localRotation;
                    sb.Append($"  p({lp.x:F3},{lp.y:F3},{lp.z:F3}) q({lr.x:F2},{lr.y:F2},{lr.z:F2},{lr.w:F2})");
                }
                catch { }
            }
            sb.Append("  [");
            bool first = true;
            System.Collections.IEnumerable comps = null;
            try { comps = CallGeneric(go, typeof(GameObject), "GetComponents", typeof(Component)); } catch { }
            if (comps == null)
            {
                // Fallback: this object's own components out of GetComponentsInChildren.
                var own = new List<Component>();
                foreach (var c in go.GetComponentsInChildren<Component>(true)) if (c != null && c.gameObject == go) own.Add(c);
                comps = own;
            }
            if (comps != null)
            {
                foreach (var o in comps)
                {
                    if (!(o is Component c) || c == null) continue;
                    var tn = Compat.RealTypeName(c);
                    if (tn == "UnityEngine.Transform" || tn == "UnityEngine.RectTransform") continue;
                    if (!first) sb.Append(", ");
                    first = false;
                    sb.Append(Short(tn)).Append(Detail(c, tn));
                }
            }
            sb.AppendLine("]");
            if (children.TryGetValue(t.GetInstanceID(), out var kids))
                foreach (var k in kids) Walk(sb, k, depth + 1, children, ref lines);
        }

        private static string Short(string fullName)
        {
            var i = fullName.LastIndexOf('.');
            var ns = i > 0 ? fullName.Substring(0, i) : "";
            return ns.StartsWith("UnityEngine") ? fullName.Substring(i + 1) : fullName;
        }

        private static string Detail(Component c, string tn)
        {
            try
            {
                switch (tn)
                {
                    case "UnityEngine.SkinnedMeshRenderer":
                    {
                        var s = Compat.As<SkinnedMeshRenderer>(c);
                        var m = s.sharedMesh;
                        var bones = s.bones;
                        var names = new List<string>();
                        for (int i = 0; i < bones.Length && i < 8; i++) names.Add(bones[i] != null ? bones[i].name : "null");
                        return $"{{mesh={(m != null ? m.name : "null")}#{(m != null ? m.GetInstanceID() : 0)} verts={P(m, "vertexCount")} readable={(m != null && m.isReadable)} blendshapes={P(m, "blendShapeCount")} bones={bones.Length} root={N(P(s, "rootBone"))} enabled={P(s, "enabled")} first bones=[{string.Join(",", names)}]}}";
                    }
                    case "UnityEngine.MeshFilter":
                    {
                        var m = P(Compat.As<MeshFilter>(c), "sharedMesh");
                        return $"{{mesh={N(m)} verts={P(m, "vertexCount")}}}";
                    }
                    case "UnityEngine.Animator":
                    {
                        var a = Compat.As<Animator>(c);
                        return $"{{human={a.isHuman} enabled={a.enabled} avatar={N(P(a, "avatar"))}}}";
                    }
                    case var textType when Compat.IsTextComponent(textType):
                    {
                        var s = Compat.GetPropertyAsString(c, "text") ?? "";
                        if (s.Length > 50) s = s.Substring(0, 50) + "...";
                        return "{\"" + s.Replace("\n", "\\n") + "\"}";
                    }
                    case "UnityEngine.UI.Toggle":
                        return "{isOn=" + Compat.GetPropertyAsString(c, "isOn") + "}";
                }
            }
            catch (Exception e) { return "{error " + e.GetType().Name + "}"; }
            return "";
        }

        public static string PathOf(Transform t)
        {
            var parts = new List<string>();
            for (var p = t; p != null; p = p.parent) parts.Add(p.name);
            parts.Reverse();
            return string.Join("/", parts);
        }

        // ------------------------------------------------------------------ data (VRoid's own objects)

        private static int _dataLines;

        /// <summary>
        /// Targeted walk from the AvatarEditor component to the open model's editing context, then every item
        /// ("transferable group") with its type, info, parameters and data (protobuf messages as JSON).
        /// Written line by line with flushing, so if something crashes VRoid the file shows how far it got.
        /// </summary>
        private static void Data(string path)
        {
            _dataLines = 0;
            using var w = new StreamWriter(path, false, Encoding.UTF8) { AutoFlush = true };
            const string editorType = "VRoidStudio.GUI.AvatarEditor.AvatarEditor";
            object editor = null;
            foreach (var mb in UnityEngine.Object.FindObjectsOfType<MonoBehaviour>())
            {
                if (mb == null || Compat.RealTypeName(mb) != editorType) continue;
                var managed = ReflectionPath.FindType(editorType);
                editor = managed != null ? Compat.CastTo(mb, managed) : null;
                break;
            }
            w.WriteLine($"AvatarEditor: {(editor != null ? "found" : "NOT FOUND")}");
            if (editor == null) return;

            var root = "$find:" + editorType + "::";
            var vm = Step(w, editor, "_viewModel", root + "_viewModel");
            Shallow(w, vm, root + "_viewModel");
            var file = Step(w, vm, "CurrentFile", root + "_viewModel.CurrentFile");
            var fileModel = Step(w, file, "model", root + "_viewModel.CurrentFile.model");
            var engine = Step(w, fileModel, "engine", root + "_viewModel.CurrentFile.model.engine");
            Shallow(w, engine, root + "_viewModel.CurrentFile.model.engine");
            var ctx = Step(w, engine, "Context", root + "_viewModel.CurrentFile.model.engine.Context");
            Shallow(w, ctx, root + "_viewModel.CurrentFile.model.engine.Context");
            if (ctx == null) { w.WriteLine("No model open (open a .vroid file and capture again)."); return; }

            w.WriteLine();
            w.WriteLine("######## Model base collection (shared hair materials etc.)");
            FullData(w, VRoidApi.Get(VRoidApi.Get(ctx, "ActiveModel"), "BaseCollection"), 1);

            w.WriteLine();
            w.WriteLine("######## Leaf transferables (GetActiveLeafTransferables)");
            foreach (var (key, value) in Explorer.Pairs(Call(w, ctx, "GetActiveLeafTransferables")))
            {
                w.WriteLine($"--- transferable {key}");
                DumpObject(w, value, "", 1, 2, new HashSet<object>());
            }

            w.WriteLine();
            w.WriteLine("######## Leaf transferable groups (GetActiveLeafTransferableGroups) with parameters and data");
            foreach (var (key, value) in Explorer.Pairs(Call(w, ctx, "GetActiveLeafTransferableGroups")))
            {
                var id = key?.ToString();
                var groupType = Call(w, ctx, "GetTypeOfTransferableGroup", id)?.ToString() ?? "";
                w.WriteLine();
                w.WriteLine($"=== group {id}   type: {groupType}");
                DumpObject(w, value, "", 1, 2, new HashSet<object>());
                w.WriteLine("  -- parameters");
                int n = 0;
                foreach (var (pk, pv) in Explorer.Pairs(Call(w, ctx, "GetParametersForTransferableGroup", id)))
                {
                    if (n++ > 300) { w.WriteLine("    ..."); break; }
                    w.WriteLine($"    {pk} = {Explorer.PreviewValue(pv)}");
                    DumpObject(w, pv, "", 3, 3, new HashSet<object>());
                }
                w.WriteLine("  -- transferables (cloned copies)");
                var list = Call(w, ctx, "CloneAndGetAllTransferablesInTransferableGroup", id);
                if (list != null && ListAccess.IsList(list))
                {
                    for (int i = 0; i < ListAccess.Count(list) && i < 20; i++)
                    {
                        var tr = ListAccess.Get(list, i);
                        w.WriteLine($"    [{i}] {tr?.GetType().FullName}");
                        DumpObject(w, tr, "", 3, 3, new HashSet<object>());
                        if (WantsFullData(groupType)) FullData(w, VRoidApi.Get(tr, "Collection"), 3);
                    }
                }
                if (_dataLines > MaxDataLines) { w.WriteLine("# (truncated)"); break; }
            }
        }

        private static bool WantsFullData(string type) =>
            type.Contains("Hair") || type.Contains("Accessory") || type.Contains("AnimalEar") || type.Contains("Tail");

        /// <summary>The complete protobuf form of a SpecificCollection (hair groups, materials, bones...) as JSON.</summary>
        private static void FullData(TextWriter w, object collection, int depth)
        {
            if (collection == null) { Line(w, depth, "full data: (no collection)"); return; }
            try
            {
                var proto = collection.GetType().GetMethod("ToProtobuf", Type.EmptyTypes)?.Invoke(collection, null);
                var json = proto != null ? Explorer.ProtobufJson(proto) : "(ToProtobuf unavailable)";
                if (json.Length > 400000) json = json.Substring(0, 400000) + "...(cut)";
                Line(w, depth, "full data (protobuf JSON): " + json);
            }
            catch (Exception e) { Line(w, depth, "full data failed: " + (e.InnerException ?? e).Message); }
        }

        private static object Step(TextWriter w, object obj, string member, string path)
        {
            if (obj == null) { w.WriteLine($"{path}: (parent missing)"); return null; }
            ReflectionPath.TryGetMember(obj, obj.GetType(), false, member, false, out var v, out var err);
            w.WriteLine($"{path}: {(v != null ? v.GetType().FullName : "null " + err)}");
            return v;
        }

        private static void Shallow(TextWriter w, object obj, string path)
        {
            if (obj == null) return;
            DumpObject(w, obj, path + ".", 1, 1, new HashSet<object>());
        }

        private static object Call(TextWriter w, object obj, string method, params object[] args)
        {
            if (obj == null) return null;
            try
            {
                var m = obj.GetType().GetMethods(BindingFlags.Instance | BindingFlags.Public)
                    .FirstOrDefault(x => x.Name == method && x.GetParameters().Length == args.Length && !x.IsGenericMethodDefinition);
                if (m == null) { w.WriteLine($"  ({method} not found)"); return null; }
                return m.Invoke(obj, args);
            }
            catch (Exception e) { w.WriteLine($"  ({method} failed: {(e.InnerException ?? e).Message})"); return null; }
        }

        private static void DumpObject(TextWriter w, object obj, string path, int depth, int maxDepth, HashSet<object> seen)
        {
            if (obj == null || _dataLines > MaxDataLines) return;
            if (Explorer.IsProtobufMessage(obj))
            {
                string json;
                json = Explorer.ProtobufJson(obj);
                if (json.Length > 60000) json = json.Substring(0, 60000) + "...(cut)";
                Line(w, depth, "protobuf " + obj.GetType().FullName + " = " + json);
                return;
            }
            var id = Compat.Identity(obj);
            if (!seen.Add(id)) { Line(w, depth, "(already shown above)"); return; }
            List<Explorer.Row> rows;
            try { rows = Explorer.Describe(obj, 120); } catch (Exception e) { Line(w, depth, "<" + e.Message + ">"); return; }
            bool isList = ListAccess.IsList(obj);
            int shown = 0;
            foreach (var r in rows)
            {
                Line(w, depth, $"{r.Name} : {r.Type} = {r.Value}");
                if (!r.Expandable || r.Obj == null || depth >= maxDepth || r.Obj is UnityEngine.Object) continue;
                if (isList && ++shown > 3) continue;
                DumpObject(w, r.Obj, path + r.Name + ".", depth + 1, maxDepth, seen);
            }
        }

        private static void Line(TextWriter w, int depth, string text)
        {
            _dataLines++;
            w.Write(new string(' ', depth * 2));
            w.WriteLine(text);
        }
    }
}
