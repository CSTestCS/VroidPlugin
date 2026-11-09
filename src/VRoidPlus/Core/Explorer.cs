using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;

namespace VRoidPlus
{
    /// <summary>
    /// Tools for filling in mapping.json: a type dump of VRoid's code, a list of likely singletons,
    /// and a live object browser used by the Explorer tab.
    /// </summary>
    internal static class Explorer
    {
        private static readonly string[] SkipAssemblyPrefixes =
        {
            "System", "Microsoft", "mscorlib", "netstandard", "Il2Cppmscorlib", "Il2CppSystem", "Il2CppMono", "Il2CppInterop",
            "UnityEngine", "Unity.", "BepInEx", "0Harmony", "HarmonyX", "Mono.", "MonoMod", "Iced", "Cpp2IL", "AsmResolver",
            "LibCpp2IL", "StableNameDotNet", "WasmDisassembler", "Disarm", "Gee.", "VRoidPlus", "SemanticVersioning",
        };

        public static IEnumerable<Assembly> GameAssemblies() =>
            AppDomain.CurrentDomain.GetAssemblies().Where(a =>
            {
                var n = a.GetName().Name ?? "";
                return !SkipAssemblyPrefixes.Any(p => n.StartsWith(p, StringComparison.OrdinalIgnoreCase));
            });

        /// <summary>Writes every game type with its members to BepInEx/config/VRoidPlus/type-dump.txt.</summary>
        public static string DumpTypes()
        {
            var path = Path.Combine(Plugin.DataDir, "type-dump.txt");
            using var w = new StreamWriter(path, false, Encoding.UTF8);
            int count = 0;
            foreach (var asm in GameAssemblies())
            {
                w.WriteLine($"######## {asm.GetName().Name}");
                Type[] types;
                try { types = asm.GetTypes(); } catch (ReflectionTypeLoadException e) { types = e.Types.Where(t => t != null).ToArray(); }
                foreach (var t in types.OrderBy(t => t.FullName))
                {
                    count++;
                    w.WriteLine($"{(t.IsEnum ? "enum" : t.IsValueType ? "struct" : "class")} {t.FullName} : {t.BaseType?.FullName}");
                    const BindingFlags all = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly;
                    try
                    {
                        if (t.IsEnum) { w.WriteLine("    = " + string.Join(", ", Enum.GetNames(t))); continue; }
                        foreach (var p in t.GetProperties(all))
                            w.WriteLine($"    {(p.GetMethod?.IsStatic == true ? "static " : "")}prop {Short(p.PropertyType)} {p.Name}{(p.SetMethod != null ? " {get;set;}" : " {get;}")}");
                        foreach (var f in t.GetFields(all).Where(f => !f.Name.StartsWith("NativeFieldInfoPtr") && !f.Name.StartsWith("NativeMethodInfoPtr")))
                            w.WriteLine($"    {(f.IsStatic ? "static " : "")}field {Short(f.FieldType)} {f.Name}");
                        foreach (var m in t.GetMethods(all).Where(m => !m.IsSpecialName))
                            w.WriteLine($"    {(m.IsStatic ? "static " : "")}{Short(m.ReturnType)} {m.Name}({string.Join(", ", m.GetParameters().Select(p => Short(p.ParameterType) + " " + p.Name))})");
                    }
                    catch (Exception e) { w.WriteLine("    <error: " + e.Message + ">"); }
                }
            }
            return $"Wrote {count} types to {path}";
        }

        /// <summary>Static "Instance"-style members whose type name hints at faces, hair, avatars or editors.</summary>
        public static List<string> FindCandidates(string keyword)
        {
            var result = new List<string>();
            foreach (var asm in GameAssemblies())
            {
                Type[] types;
                try { types = asm.GetTypes(); } catch { continue; }
                foreach (var t in types)
                {
                    PropertyInfo[] props;
                    try { props = t.GetProperties(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.DeclaredOnly); }
                    catch { continue; }
                    foreach (var p in props)
                    {
                        if (p.GetIndexParameters().Length > 0) continue;
                        bool selfType = p.PropertyType == t;
                        bool hit = !string.IsNullOrEmpty(keyword) &&
                                   (t.FullName?.IndexOf(keyword, StringComparison.OrdinalIgnoreCase) >= 0 || p.PropertyType.Name.IndexOf(keyword, StringComparison.OrdinalIgnoreCase) >= 0);
                        if (selfType && (hit || string.IsNullOrEmpty(keyword)) || hit && p.Name.Contains("Instance"))
                            result.Add($"{t.FullName}::{p.Name}");
                    }
                }
            }
            return result.Distinct().OrderBy(x => x).Take(300).ToList();
        }

        public class Row
        {
            public string Name, Type, Value;
            public bool Expandable;
            public bool IsList;
        }

        /// <summary>Members of a live object, for the browser.</summary>
        public static List<Row> Describe(object obj, int max = 250)
        {
            var rows = new List<Row>();
            if (obj == null) return rows;
            if (ListAccess.IsList(obj))
            {
                int n = ListAccess.Count(obj);
                rows.Add(new Row { Name = "Count", Type = "int", Value = n.ToString() });
                for (int i = 0; i < Math.Min(n, max); i++)
                {
                    object v; try { v = ListAccess.Get(obj, i); } catch (Exception e) { v = "<" + e.Message + ">"; }
                    rows.Add(new Row { Name = $"[{i}]", Type = v?.GetType().Name ?? "null", Value = Preview(v), Expandable = v != null && !ObjectSnapshot.IsSimple(v.GetType()) });
                }
                return rows;
            }
            foreach (var m in ObjectSnapshot.DataMembers(obj.GetType()).Take(max))
            {
                object v; string val;
                try { v = m.Get(obj); val = Preview(v); } catch (Exception e) { v = null; val = "<" + (e.InnerException?.Message ?? e.Message) + ">"; }
                rows.Add(new Row
                {
                    Name = m.Name, Type = Short(m.Type) + (m.CanWrite ? "" : " (read-only)"), Value = val,
                    Expandable = v != null && !ObjectSnapshot.IsSimple(v.GetType()), IsList = v != null && ListAccess.IsList(v),
                });
            }
            return rows;
        }

        private static string Preview(object v)
        {
            if (v == null) return "null";
            if (v is string s) return "\"" + (s.Length > 60 ? s.Substring(0, 60) + "…" : s) + "\"";
            if (ObjectSnapshot.IsSimple(v.GetType())) return v.ToString();
            if (ListAccess.IsList(v)) { try { return $"list ({ListAccess.Count(v)})"; } catch { return "list"; } }
            if (v is UnityEngine.Object uo) { try { return $"{uo.name} ({Compat.RealTypeName(uo)})"; } catch { } }
            return Short(v.GetType());
        }

        private static string Short(Type t)
        {
            if (t == null) return "?";
            if (!t.IsGenericType) return t.Name;
            var n = t.Name; var i = n.IndexOf('`'); if (i > 0) n = n.Substring(0, i);
            return n + "<" + string.Join(",", t.GetGenericArguments().Select(Short)) + ">";
        }
    }
}
