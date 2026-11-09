using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace VRoidPlus
{
    /// <summary>
    /// Copies the editable state of a VRoid data object (face, hair group, ...) to JSON and back.
    /// Works by reflection over the interop wrapper types, so it needs no knowledge of VRoid's classes.
    /// Unity object references (textures, meshes) and delegates are skipped; everything else that is
    /// a number, bool, string, enum, struct, nested object or list is captured.
    /// </summary>
    internal class ObjectSnapshot
    {
        public int MaxDepth = 8;
        public HashSet<string> Exclude = new(StringComparer.Ordinal);

        /// <summary>When set, string members with these names that hold a GUID get a fresh GUID on restore.</summary>
        public HashSet<string> RegenerateIds;
        private readonly Dictionary<string, string> _idMap = new();

        public List<string> Warnings = new();

        // ---------------------------------------------------------------- capture

        public JsonObject Capture(object obj) => CaptureObject(obj, 0, new HashSet<object>());

        private JsonObject CaptureObject(object obj, int depth, HashSet<object> path)
        {
            var result = new JsonObject { ["$type"] = obj.GetType().FullName };
            var id = Compat.Identity(obj);
            if (!path.Add(id)) { result["$cycle"] = true; return result; }
            try
            {
                foreach (var m in DataMembers(obj.GetType()))
                {
                    if (Exclude.Contains(m.Name)) continue;
                    object value;
                    try { value = m.Get(obj); } catch { continue; }
                    var node = CaptureValue(value, m.Type, depth + 1, path, m.CanWrite);
                    if (node != null || (value == null && m.CanWrite && IsSimple(m.Type))) result[m.Name] = node;
                }
            }
            finally { path.Remove(id); }
            return result;
        }

        private JsonNode CaptureValue(object value, Type declared, int depth, HashSet<object> path, bool writable)
        {
            if (value == null) return null;
            var t = value.GetType();
            if (IsSimple(t)) return writable ? SimpleToNode(value) : null;
            if (IsUnityObject(t) || IsDelegate(t)) return null;
            if (t.IsValueType) return writable ? CaptureStruct(value) : null;
            if (depth > MaxDepth) return null;
            if (ListAccess.IsList(value))
            {
                var arr = new JsonArray();
                var n = ListAccess.Count(value);
                for (int i = 0; i < n; i++)
                {
                    var item = ListAccess.Get(value, i);
                    if (item == null) { arr.Add(null); continue; }
                    var it = item.GetType();
                    if (IsSimple(it)) arr.Add(SimpleToNode(item));
                    else if (IsUnityObject(it) || IsDelegate(it)) return null; // list of assets: leave untouched
                    else if (it.IsValueType) arr.Add(CaptureStruct(item));
                    else arr.Add(CaptureObject(item, depth + 1, path));
                }
                return arr;
            }
            if (IsUnsupportedCollection(t)) return null;
            return CaptureObject(value, depth, path);
        }

        private static JsonObject CaptureStruct(object value)
        {
            var o = new JsonObject();
            foreach (var f in value.GetType().GetFields(BindingFlags.Instance | BindingFlags.Public))
            {
                var v = f.GetValue(value);
                if (v == null) continue;
                if (IsSimple(f.FieldType)) o[f.Name] = SimpleToNode(v);
                else if (f.FieldType.IsValueType) o[f.Name] = CaptureStruct(v);
            }
            return o;
        }

        // ---------------------------------------------------------------- restore

        public void Restore(object target, JsonObject data) => RestoreObject(target, data, 0);

        /// <summary>Creates a new object of the snapshot's type (or <paramref name="fallback"/>) and fills it.</summary>
        public object CreateFrom(JsonObject data, Type fallback)
        {
            var t = ReflectionPath.FindType((string)data["$type"]) ?? fallback;
            var o = TryCreate(t) ?? (t != fallback ? TryCreate(fallback) : null);
            if (o == null) { Warnings.Add($"cannot create {t?.FullName}"); return null; }
            RestoreObject(o, data, 0);
            return o;
        }

        private void RestoreObject(object target, JsonObject data, int depth)
        {
            var members = DataMembers(target.GetType()).ToDictionary(m => m.Name, StringComparer.Ordinal);
            foreach (var kv in data)
            {
                if (kv.Key.StartsWith("$")) continue;
                if (Exclude.Contains(kv.Key)) continue;
                if (!members.TryGetValue(kv.Key, out var m)) { Warnings.Add($"{target.GetType().Name}.{kv.Key} no longer exists"); continue; }
                try { RestoreMember(target, m, kv.Value, depth); }
                catch (Exception e)
                {
                    var inner = e is TargetInvocationException tie ? tie.InnerException ?? e : e;
                    Warnings.Add($"{target.GetType().Name}.{kv.Key}: {inner.Message}");
                }
            }
        }

        private void RestoreMember(object target, Member m, JsonNode node, int depth)
        {
            if (IsSimple(m.Type))
            {
                if (!m.CanWrite) return;
                var v = NodeToSimple(node, m.Type);
                if (v is string s && RegenerateIds != null && RegenerateIds.Contains(m.Name) && Guid.TryParse(s, out _))
                    v = MapId(s);
                m.Set(target, v);
                return;
            }
            if (node == null) return;

            if (m.Type.IsValueType && node is JsonObject so)
            {
                if (!m.CanWrite) return;
                var boxed = m.Get(target) ?? Activator.CreateInstance(m.Type);
                RestoreStruct(ref boxed, so);
                m.Set(target, boxed);
                return;
            }

            var current = m.Get(target);
            if (node is JsonArray arr)
            {
                if (current == null) { current = TryCreate(m.Type); if (current == null || !m.CanWrite) return; m.Set(target, current); }
                RestoreList(current, arr, depth);
                return;
            }
            if (node is JsonObject obj)
            {
                bool created = false;
                if (current == null)
                {
                    if (!m.CanWrite) return;
                    current = TryCreate(ReflectionPath.FindType((string)obj["$type"]) ?? m.Type);
                    if (current == null) return;
                    created = true;
                }
                RestoreObject(current, obj, depth + 1);
                // Interop wrappers write through to native memory, but a property may hand back a copy:
                // writing it back is harmless and covers that case.
                if (m.CanWrite && (created || !m.Type.IsValueType)) { try { m.Set(target, current); } catch { } }
            }
        }

        private void RestoreList(object list, JsonArray arr, int depth)
        {
            var elem = ListAccess.ElementType(list);
            if (!ListAccess.IsResizable(list))
            {
                // Fixed-size array: overwrite in place when sizes match.
                if (ListAccess.Count(list) != arr.Count) { Warnings.Add("array length changed; skipped"); return; }
                for (int i = 0; i < arr.Count; i++)
                    if (elem != null && IsSimple(elem)) ListAccess.Set(list, i, NodeToSimple(arr[i], elem));
                return;
            }
            ListAccess.Clear(list);
            foreach (var item in arr)
            {
                if (elem == null) return;
                if (IsSimple(elem)) { ListAccess.Add(list, NodeToSimple(item, elem)); continue; }
                if (item is JsonObject o)
                {
                    if (elem.IsValueType) { object boxed = Activator.CreateInstance(elem); RestoreStruct(ref boxed, o); ListAccess.Add(list, boxed); continue; }
                    var created = CreateFrom(o, elem);
                    if (created != null) ListAccess.Add(list, Compat.CastTo(created, elem) ?? created);
                }
            }
        }

        private static void RestoreStruct(ref object boxed, JsonObject data)
        {
            foreach (var kv in data)
            {
                var f = boxed.GetType().GetField(kv.Key, BindingFlags.Instance | BindingFlags.Public);
                if (f == null) continue;
                if (IsSimple(f.FieldType)) f.SetValue(boxed, NodeToSimple(kv.Value, f.FieldType));
                else if (f.FieldType.IsValueType && kv.Value is JsonObject inner)
                {
                    var sub = f.GetValue(boxed);
                    RestoreStruct(ref sub, inner);
                    f.SetValue(boxed, sub);
                }
            }
        }

        private string MapId(string old)
        {
            if (!_idMap.TryGetValue(old, out var fresh))
            {
                // Keep the original formatting style (dashes / no dashes, case).
                var g = Guid.NewGuid();
                fresh = old.Contains('-') ? g.ToString("D") : g.ToString("N");
                if (old.Any(char.IsUpper)) fresh = fresh.ToUpperInvariant();
                _idMap[old] = fresh;
            }
            return fresh;
        }

        // ---------------------------------------------------------------- members

        internal class Member
        {
            public string Name;
            public Type Type;
            public bool CanWrite;
            public Func<object, object> Get;
            public Action<object, object> Set;
        }

        private static readonly Dictionary<Type, List<Member>> MemberCache = new();

        internal static List<Member> DataMembers(Type type)
        {
            if (MemberCache.TryGetValue(type, out var cached)) return cached;
            var list = new List<Member>();
            var seen = new HashSet<string>();
            for (var t = type; t != null && !Compat.IsPlumbingType(t); t = t.BaseType)
            {
                foreach (var p in t.GetProperties(BindingFlags.Instance | BindingFlags.Public | BindingFlags.DeclaredOnly))
                {
                    if (p.GetIndexParameters().Length != 0 || p.GetMethod == null || !seen.Add(p.Name)) continue;
                    var pp = p;
                    list.Add(new Member
                    {
                        Name = p.Name, Type = p.PropertyType, CanWrite = p.SetMethod != null && p.SetMethod.IsPublic,
                        Get = o => pp.GetValue(o), Set = (o, v) => pp.SetValue(o, v),
                    });
                }
                foreach (var f in t.GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.DeclaredOnly))
                {
                    if (!seen.Add(f.Name)) continue;
                    var ff = f;
                    list.Add(new Member
                    {
                        Name = f.Name, Type = f.FieldType, CanWrite = !f.IsInitOnly,
                        Get = o => ff.GetValue(o), Set = (o, v) => ff.SetValue(o, v),
                    });
                }
            }
            MemberCache[type] = list;
            return list;
        }

        // ---------------------------------------------------------------- type helpers

        public static bool IsSimple(Type t) => t.IsPrimitive || t.IsEnum || t == typeof(string) || t == typeof(decimal);

        private static bool IsUnityObject(Type t) => typeof(UnityEngine.Object).IsAssignableFrom(t);

        private static bool IsDelegate(Type t)
        {
            for (var b = t; b != null; b = b.BaseType)
                if (b == typeof(Delegate) || b.Name == "Delegate" || b.Name == "MulticastDelegate") return true;
            return false;
        }

        private static bool IsUnsupportedCollection(Type t)
        {
            var n = t.Name;
            return n.StartsWith("Dictionary") || n.StartsWith("HashSet") || n.StartsWith("Queue") || n.StartsWith("Stack")
                   || n.StartsWith("ReactiveProperty") || n.StartsWith("Subject") || n.StartsWith("IntPtr");
        }

        private static object TryCreate(Type t)
        {
            if (t == null || t.IsAbstract || t.IsInterface) return null;
            try { return t.IsValueType ? Activator.CreateInstance(t) : (t.GetConstructor(Type.EmptyTypes) != null ? Activator.CreateInstance(t) : null); }
            catch { return null; }
        }

        private static JsonNode SimpleToNode(object v) => v switch
        {
            string s => JsonValue.Create(s),
            bool b => JsonValue.Create(b),
            Enum e => JsonValue.Create(e.ToString()),
            float f => JsonValue.Create(f),
            double d => JsonValue.Create(d),
            decimal m => JsonValue.Create(m),
            char c => JsonValue.Create(c.ToString()),
            _ => JsonValue.Create(Convert.ToInt64(v, CultureInfo.InvariantCulture)),
        };

        internal static object NodeToSimple(JsonNode node, Type t)
        {
            if (node == null) return t.IsValueType ? Activator.CreateInstance(t) : null;
            if (!(node is JsonValue jv && jv.TryGetValue<JsonElement>(out var el)))
                el = JsonDocument.Parse(node.ToJsonString()).RootElement;
            if (t == typeof(string)) return el.ValueKind == JsonValueKind.Null ? null : el.ToString();
            if (t == typeof(bool)) return el.ValueKind == JsonValueKind.True || (el.ValueKind == JsonValueKind.String && el.GetString() == "true");
            if (t.IsEnum)
            {
                if (el.ValueKind == JsonValueKind.Number) return Enum.ToObject(t, el.GetInt64());
                return Enum.Parse(t, el.GetString()!, true);
            }
            if (t == typeof(char)) return (el.GetString() ?? "\0")[0];
            return Convert.ChangeType(el.GetDouble(), t, CultureInfo.InvariantCulture);
        }
    }
}
