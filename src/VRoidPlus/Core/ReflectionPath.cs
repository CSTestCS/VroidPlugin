using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;

namespace VRoidPlus
{
    /// <summary>
    /// Resolves the text paths used in mapping.json to live objects.
    ///   "Ns.Type::A.B[2].C()"       static member A of Ns.Type, then B, element 2, then method C()
    ///   "$find:Ns.Type::A.B"         FindObjectOfType&lt;Ns.Type&gt;(), then A, then B
    ///   "$find:Ns.Type"              just the found object
    /// </summary>
    internal static class ReflectionPath
    {
        public const BindingFlags Inst = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
        public const BindingFlags Stat = BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;

        private static readonly Dictionary<string, Type> TypeCache = new();

        public static Type FindType(string name)
        {
            if (string.IsNullOrWhiteSpace(name)) return null;
            name = name.Trim();
            if (TypeCache.TryGetValue(name, out var cached)) return cached;
            Type found = null;
            foreach (var candidate in new[] { name, "Il2Cpp" + name, "Il2Cpp." + name })
            {
                foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
                {
                    try { found = asm.GetType(candidate, false); } catch { }
                    if (found != null) break;
                }
                if (found != null) break;
            }
            // Last resort: unique match on the short name.
            if (found == null && !name.Contains('.'))
            {
                var matches = AllTypes().Where(t => t.Name == name).Take(2).ToList();
                if (matches.Count == 1) found = matches[0];
            }
            TypeCache[name] = found;
            return found;
        }

        public static IEnumerable<Type> AllTypes()
        {
            foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
            {
                Type[] types;
                try { types = asm.GetTypes(); }
                catch (ReflectionTypeLoadException e) { types = e.Types.Where(t => t != null).ToArray(); }
                catch { continue; }
                foreach (var t in types) yield return t;
            }
        }

        /// <summary>Resolves a path. On failure returns null and sets <paramref name="error"/>.</summary>
        public static object Resolve(string path, out string error)
        {
            error = null;
            if (string.IsNullOrWhiteSpace(path)) { error = "not set"; return null; }
            path = path.Trim();
            bool find = path.StartsWith("$find:", StringComparison.Ordinal);
            if (find) path = path.Substring(6);

            var split = path.IndexOf("::", StringComparison.Ordinal);
            var typeName = split >= 0 ? path.Substring(0, split) : path;
            var rest = split >= 0 ? path.Substring(split + 2) : "";

            var type = FindType(typeName);
            if (type == null) { error = $"type '{typeName}' not found"; return null; }

            object current = null;
            Type currentType = type;
            bool isStatic = true;
            if (find)
            {
                current = Compat.FindObjectOfManagedType(type);
                if (current == null) { error = $"no live object of type '{typeName}'"; return null; }
                isStatic = false;
            }

            foreach (var seg in SplitSegments(rest))
            {
                current = Step(current, currentType, isStatic, seg, out error);
                if (error != null) return null;
                if (current == null) { error = $"'{seg}' is null"; return null; }
                currentType = current.GetType();
                isStatic = false;
            }
            if (isStatic) { error = "path names a type, not an object (add ::Member)"; return null; }
            return current;
        }

        public static IEnumerable<string> SplitSegments(string rest)
        {
            if (string.IsNullOrEmpty(rest)) yield break;
            int depth = 0, start = 0;
            for (int i = 0; i < rest.Length; i++)
            {
                if (rest[i] == '[' || rest[i] == '(') depth++;
                else if (rest[i] == ']' || rest[i] == ')') depth--;
                else if (rest[i] == '.' && depth == 0)
                {
                    if (i > start) yield return rest.Substring(start, i - start);
                    start = i + 1;
                }
            }
            if (start < rest.Length) yield return rest.Substring(start);
        }

        private static object Step(object obj, Type type, bool isStatic, string seg, out string error)
        {
            error = null;
            int? index = null;
            bool call = false;
            var name = seg;
            var b = name.IndexOf('[');
            if (b >= 0 && name.EndsWith("]"))
            {
                if (!int.TryParse(name.Substring(b + 1, name.Length - b - 2), out var ix)) { error = $"bad index in '{seg}'"; return null; }
                index = ix;
                name = name.Substring(0, b);
            }
            if (name.EndsWith("()")) { call = true; name = name.Substring(0, name.Length - 2); }

            object value;
            if (name.Length == 0) value = obj;
            else if (!TryGetMember(obj, type, isStatic, name, call, out value, out error)) return null;

            if (index.HasValue)
            {
                if (!ListAccess.IsList(value)) { error = $"'{name}' is not a list"; return null; }
                var count = ListAccess.Count(value);
                if (index.Value < 0 || index.Value >= count) { error = $"index {index} out of range (count {count})"; return null; }
                value = ListAccess.Get(value, index.Value);
            }
            return value;
        }

        public static bool TryGetMember(object obj, Type type, bool isStatic, string name, bool call, out object value, out string error)
        {
            value = null; error = null;
            var flags = isStatic ? Stat : Inst;
            try
            {
                for (var t = type; t != null; t = t.BaseType)
                {
                    if (call)
                    {
                        var m = t.GetMethods(flags | BindingFlags.DeclaredOnly).FirstOrDefault(x => x.Name == name && x.GetParameters().Length == 0);
                        if (m != null) { value = m.Invoke(isStatic ? null : obj, null); return true; }
                        continue;
                    }
                    var p = t.GetProperty(name, flags | BindingFlags.DeclaredOnly);
                    if (p != null && p.GetIndexParameters().Length == 0) { value = p.GetValue(isStatic ? null : obj); return true; }
                    var f = t.GetField(name, flags | BindingFlags.DeclaredOnly);
                    if (f != null) { value = f.GetValue(isStatic ? null : obj); return true; }
                }
            }
            catch (TargetInvocationException e) { error = $"'{name}' threw {e.InnerException?.GetType().Name}: {e.InnerException?.Message}"; return false; }
            catch (Exception e) { error = $"'{name}' failed: {e.Message}"; return false; }
            error = $"member '{name}' not found on {type.FullName}";
            return false;
        }

        /// <summary>Runs a path whose last segment is a method call, e.g. "Ns.Type::Instance.Refresh()".</summary>
        public static bool Invoke(string path, out string error)
        {
            error = null;
            if (string.IsNullOrWhiteSpace(path)) return true;
            Resolve(path, out error);
            // A void method resolves to null: that's success, not an error.
            if (error != null && error.EndsWith("is null") && path.TrimEnd().EndsWith("()")) error = null;
            return error == null;
        }
    }

    /// <summary>Uniform access to managed lists, IL2CPP lists and IL2CPP arrays.</summary>
    internal static class ListAccess
    {
        public static bool IsList(object o)
        {
            if (o == null || o is string) return false;
            var t = o.GetType();
            return (t.GetProperty("Count") != null || t.GetProperty("Length") != null) && ItemProperty(t) != null;
        }

        public static bool IsResizable(object o) => o != null && o.GetType().GetMethod("Add") != null && o.GetType().GetMethod("Clear", Type.EmptyTypes) != null;

        private static PropertyInfo ItemProperty(Type t) =>
            t.GetProperties().FirstOrDefault(p => p.Name == "Item" && p.GetIndexParameters().Length == 1 && p.GetIndexParameters()[0].ParameterType == typeof(int));

        public static int Count(object o)
        {
            var t = o.GetType();
            var p = t.GetProperty("Count") ?? t.GetProperty("Length");
            return Convert.ToInt32(p!.GetValue(o));
        }

        public static object Get(object o, int i) => ItemProperty(o.GetType())!.GetValue(o, new object[] { i });
        public static void Set(object o, int i, object v) => ItemProperty(o.GetType())!.SetValue(o, v, new object[] { i });
        public static void Clear(object o) => o.GetType().GetMethod("Clear", Type.EmptyTypes)!.Invoke(o, null);

        public static void Add(object o, object v)
        {
            var m = o.GetType().GetMethods().First(x => x.Name == "Add" && x.GetParameters().Length == 1);
            m.Invoke(o, new[] { v });
        }

        public static Type ElementType(object o)
        {
            var t = o.GetType();
            var add = t.GetMethods().FirstOrDefault(x => x.Name == "Add" && x.GetParameters().Length == 1);
            if (add != null) return add.GetParameters()[0].ParameterType;
            return ItemProperty(t)?.PropertyType;
        }
    }
}
