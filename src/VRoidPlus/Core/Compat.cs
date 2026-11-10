using System;
using System.Collections.Generic;
using UnityEngine;
using Object = UnityEngine.Object;
#if IL2CPP
using Il2CppInterop.Runtime;
using Il2CppInterop.Runtime.InteropTypes;
#endif

namespace VRoidPlus
{
    /// <summary>
    /// Everything that differs between IL2CPP (the real game) and a normal Mono Unity build
    /// is kept in this file, so the rest of the plugin is plain Unity code.
    /// </summary>
    internal static class Compat
    {
        /// <summary>Safe downcast of a Unity object ("is"/"as" don't work on IL2CPP wrappers).</summary>
        public static T As<T>(Object o) where T : Object
        {
            if (o == null) return null;
#if IL2CPP
            return o.TryCast<T>();
#else
            return o as T;
#endif
        }

        /// <summary>Replaces a skinned mesh's bone list (through reflection, so a stripped setter can't break the build).</summary>
        public static void SetBones(SkinnedMeshRenderer smr, Transform[] bones)
        {
            var prop = smr.GetType().GetProperty("bones");
            if (prop == null || !prop.CanWrite) throw new MissingMemberException("SkinnedMeshRenderer.bones setter");
#if IL2CPP
            prop.SetValue(smr, new Il2CppInterop.Runtime.InteropTypes.Arrays.Il2CppReferenceArray<Transform>(bones));
#else
            prop.SetValue(smr, bones);
#endif
        }

        /// <summary>Finds every live Unity object whose IL2CPP type is <paramref name="assemblyQualifiedName"/>.</summary>
        public static List<Component> FindComponentsByTypeName(string assemblyQualifiedName)
        {
            var result = new List<Component>();
#if IL2CPP
            var t = Il2CppSystem.Type.GetType(assemblyQualifiedName);
            if (t == null) return result;
            var arr = Object.FindObjectsOfType(t);
            if (arr == null) return result;
            foreach (var o in arr)
            {
                var c = o?.TryCast<Component>();
                if (c != null) result.Add(c);
            }
#else
            var t = Type.GetType(assemblyQualifiedName);
            if (t == null) return result;
            foreach (var o in Object.FindObjectsOfType(t))
                if (o is Component c) result.Add(c);
#endif
            return result;
        }

        /// <summary>Full name of the object's real (most derived) game type.</summary>
        public static string RealTypeName(Object o)
        {
            if (o == null) return "";
#if IL2CPP
            return o.GetIl2CppType().FullName;
#else
            return o.GetType().FullName;
#endif
        }

        /// <summary>Reads a property from the real game type as a string (works for TextMeshPro etc. without referencing them).</summary>
        public static string GetPropertyAsString(Object o, string property)
        {
            if (o == null) return null;
            try
            {
#if IL2CPP
                var p = o.GetIl2CppType().GetProperty(property);
                if (p == null) return null;
                var v = p.GetValue(o, null);
                return v?.ToString();
#else
                var p = o.GetType().GetProperty(property);
                return p?.GetValue(o)?.ToString();
#endif
            }
            catch { return null; }
        }

        /// <summary>UI text components: Unity's, TextMeshPro, and VRoid's own (VRoid.UI.Component.VRoidText).</summary>
        public static bool IsTextComponent(string typeName) =>
            typeName == "UnityEngine.UI.Text" || typeName.StartsWith("TMPro.TextMeshPro", StringComparison.Ordinal) || typeName.EndsWith("VRoidText", StringComparison.Ordinal);

        /// <summary>Visible text on a UI element: looks at Text and TextMeshPro children.</summary>
        public static string GetLabelText(Component c)
        {
            var parts = new List<string>();
            foreach (var child in c.GetComponentsInChildren<Component>(true))
            {
                if (child == null) continue;
                var tn = RealTypeName(child);
                if (IsTextComponent(tn))
                {
                    var s = GetPropertyAsString(child, "text");
                    if (!string.IsNullOrEmpty(s)) parts.Add(s);
                }
            }
            return string.Join(" ", parts);
        }

        /// <summary>
        /// Turns a uGUI Toggle off (fires its normal onValueChanged so VRoid reacts as if the user clicked it).
        /// Returns false if it could not be done.
        /// </summary>
        public static bool TrySetToggle(Component toggle, bool value, out bool previous)
        {
            previous = false;
#if IL2CPP && HAS_UGUI
            var t = toggle.TryCast<UnityEngine.UI.Toggle>();
            if (t == null) return false;
            previous = t.isOn;
            if (t.isOn != value) t.isOn = value;
            return true;
#else
            var p = toggle.GetType().GetProperty("isOn");
            if (p == null) return false;
            previous = (bool)p.GetValue(toggle);
            if (previous != value) p.SetValue(toggle, value);
            return true;
#endif
        }

        /// <summary>
        /// FindObjectOfType for a MonoBehaviour type only known at runtime (an interop wrapper type).
        /// Object.FindObjectOfType(Type) is stripped in VRoid's build, so this scans the generic
        /// FindObjectsOfType&lt;MonoBehaviour&gt;() (which works) and matches the real type name.
        /// </summary>
        public static object FindObjectOfManagedType(Type managedType)
        {
            var wanted = managedType.FullName;
            // Interop may prefix namespaces with "Il2Cpp"; compare without it.
            if (wanted != null && wanted.StartsWith("Il2Cpp", StringComparison.Ordinal) && !wanted.StartsWith("Il2CppSystem", StringComparison.Ordinal))
                wanted = wanted.Substring(6).TrimStart('.');
            foreach (var mb in Object.FindObjectsOfType<MonoBehaviour>())
            {
                if (mb == null || RealTypeName(mb) != wanted) continue;
                return CastTo(mb, managedType);
            }
            return null;
        }

        /// <summary>Casts an interop object to a more specific interop type (no-op outside IL2CPP).</summary>
        public static object CastTo(object obj, Type target)
        {
            if (obj == null || target.IsInstanceOfType(obj)) return obj;
#if IL2CPP
            if (obj is Il2CppObjectBase b)
            {
                var m = typeof(Il2CppObjectBase).GetMethod("TryCast")?.MakeGenericMethod(target);
                return m?.Invoke(b, null);
            }
#endif
            return null;
        }

        /// <summary>
        /// Identity of an object that survives re-wrapping: interop creates a new managed wrapper each time
        /// the same native object is fetched, so reference equality is not enough.
        /// </summary>
        public static object Identity(object o)
        {
#if IL2CPP
            if (o is Il2CppObjectBase b) return b.Pointer;
#endif
            return o;
        }

        /// <summary>Whether a managed type is part of the interop/runtime plumbing rather than game data.</summary>
        public static bool IsPlumbingType(Type t)
        {
            if (t == null) return true;
            var ns = t.Namespace ?? "";
#if IL2CPP
            if (t == typeof(Il2CppObjectBase)) return true;
            if (ns.StartsWith("Il2CppInterop", StringComparison.Ordinal)) return true;
#endif
            return t == typeof(object) || ns == "Il2CppSystem" && t.Name == "Object";
        }
    }
}
