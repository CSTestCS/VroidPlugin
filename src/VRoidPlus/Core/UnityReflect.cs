using System;
using System.Linq;
using System.Reflection;
using UnityEngine;

namespace VRoidPlus
{
    /// <summary>
    /// Unity members used by the cloth physics and seam squish features, reached through reflection: VRoid's
    /// Unity build lacks some members, and a missing one must show up as a message, not as a build error.
    /// </summary>
    internal static class UnityReflect
    {
        public static object Get(object o, string prop)
        {
            if (o == null) return null;
            var p = o.GetType().GetProperty(prop, BindingFlags.Public | BindingFlags.Instance);
            if (p == null) throw new MissingMemberException(o.GetType().Name + "." + prop);
            return p.GetValue(o);
        }

        public static void Set(object o, string prop, object value)
        {
            var p = o.GetType().GetProperty(prop, BindingFlags.Public | BindingFlags.Instance);
            if (p == null || !p.CanWrite) throw new MissingMemberException(o.GetType().Name + "." + prop + " (set)");
            p.SetValue(o, value);
        }

        /// <summary>Reads an array property (Unity or IL2CPP array) into a managed array.</summary>
        public static T[] GetArray<T>(object o, string prop)
        {
            var v = Get(o, prop);
            if (v == null) return Array.Empty<T>();
            if (v is T[] direct) return direct;
            int n = ListAccess.Count(v);
            var a = new T[n];
            for (int i = 0; i < n; i++) a[i] = (T)ListAccess.Get(v, i);
            return a;
        }

        /// <summary>Writes a managed array to an array property (converting to the IL2CPP array type if needed).</summary>
        public static void SetArray<T>(object o, string prop, T[] values)
        {
            var p = o.GetType().GetProperty(prop, BindingFlags.Public | BindingFlags.Instance);
            if (p == null || !p.CanWrite) throw new MissingMemberException(o.GetType().Name + "." + prop + " (set)");
            object v = values;
            if (!p.PropertyType.IsAssignableFrom(typeof(T[])))
                v = Activator.CreateInstance(p.PropertyType, new object[] { values });
            p.SetValue(o, v);
        }

        public static object Call(object o, string method, params object[] args)
        {
            var m = o.GetType().GetMethods(BindingFlags.Public | BindingFlags.Instance)
                .FirstOrDefault(x => x.Name == method && x.GetParameters().Length == args.Length);
            if (m == null) throw new MissingMethodException(o.GetType().Name, method);
            return m.Invoke(o, args);
        }

        public static object CallStatic(Type t, string method, params object[] args)
        {
            var m = t.GetMethods(BindingFlags.Public | BindingFlags.Static)
                .FirstOrDefault(x => x.Name == method && !x.IsGenericMethodDefinition && x.GetParameters().Length == args.Length &&
                                     x.GetParameters().Select(p => p.ParameterType).Zip(args, (pt, a) => a == null || pt.IsInstanceOfType(a) || pt.IsPrimitive || pt.IsEnum).All(b => b));
            if (m == null) throw new MissingMethodException(t.Name, method);
            return m.Invoke(null, args);
        }

        /// <summary>A BoneWeight built through reflection (its property setters may be missing in VRoid's build).</summary>
        public static BoneWeight MakeBoneWeight(int i0, float w0, int i1, float w1, int i2, float w2, int i3, float w3)
        {
            object b = new BoneWeight();
            var t = typeof(BoneWeight);
            void SetOne(string prop, string field, object v)
            {
                var p = t.GetProperty(prop);
                if (p != null && p.CanWrite) { p.SetValue(b, v); return; }
                var f = t.GetField(field, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                if (f == null) throw new MissingMemberException("BoneWeight." + prop);
                f.SetValue(b, v);
            }
            SetOne("boneIndex0", "m_BoneIndex0", i0); SetOne("weight0", "m_Weight0", w0);
            SetOne("boneIndex1", "m_BoneIndex1", i1); SetOne("weight1", "m_Weight1", w1);
            SetOne("boneIndex2", "m_BoneIndex2", i2); SetOne("weight2", "m_Weight2", w2);
            SetOne("boneIndex3", "m_BoneIndex3", i3); SetOne("weight3", "m_Weight3", w3);
            return (BoneWeight)b;
        }

        /// <summary>A translation matrix, built from fields only.</summary>
        public static Matrix4x4 Translation(Vector3 t)
        {
            var m = Matrix4x4.identity;
            m.m03 = t.x; m.m13 = t.y; m.m23 = t.z;
            return m;
        }

        /// <summary>Adds a component of a type only known at runtime (an IL2CPP wrapper type).</summary>
        public static object AddComponent(GameObject go, Type managedType)
        {
#if IL2CPP
            var il2cppType = Il2CppInterop.Runtime.Il2CppType.From(managedType, true);
            var m = typeof(GameObject).GetMethods().FirstOrDefault(x => x.Name == "AddComponent" && !x.IsGenericMethodDefinition &&
                                                                         x.GetParameters().Length == 1);
            if (m == null) throw new MissingMethodException("GameObject", "AddComponent(Type)");
            var c = m.Invoke(go, new object[] { il2cppType });
            return Compat.CastTo(c, managedType);
#else
            return go.AddComponent(managedType);
#endif
        }

        /// <summary>
        /// The alpha channel of a texture as a w x h array (0..1), scaled down to at most maxSize. Textures that
        /// only live on the graphics card are copied through a temporary RenderTexture.
        /// </summary>
        public static float[] ReadAlpha(Texture tex, int maxSize, out int w, out int h)
        {
            w = Math.Max(1, Convert.ToInt32(Get(tex, "width"))); h = Math.Max(1, Convert.ToInt32(Get(tex, "height")));
            float scale = Math.Min(1f, maxSize / (float)Math.Max(w, h));
            w = Math.Max(1, (int)Math.Round(w * scale)); h = Math.Max(1, (int)Math.Round(h * scale));

            var rt = CallStatic(typeof(RenderTexture), "GetTemporary", w, h, 0);
            var prevActive = typeof(RenderTexture).GetProperty("active")?.GetValue(null);
            object readable = null;
            try
            {
                CallStatic(typeof(Graphics), "Blit", tex, rt);
                typeof(RenderTexture).GetProperty("active")!.SetValue(null, rt);
                var ctor = typeof(Texture2D).GetConstructors().FirstOrDefault(c =>
                {
                    var ps = c.GetParameters();
                    return ps.Length == 4 && ps[0].ParameterType == typeof(int) && ps[1].ParameterType == typeof(int) && ps[2].ParameterType == typeof(TextureFormat) && ps[3].ParameterType == typeof(bool);
                });
                if (ctor == null) throw new MissingMethodException("Texture2D", ".ctor(int,int,TextureFormat,bool)");
                readable = ctor.Invoke(new object[] { w, h, TextureFormat.RGBA32, false });
                Call(readable, "ReadPixels", new Rect(0, 0, w, h), 0, 0);
                Call(readable, "Apply");
                var px = Call(readable, "GetPixels32");
                int n = ListAccess.Count(px);
                var alpha = new float[n];
                for (int i = 0; i < n; i++) alpha[i] = ((Color32)ListAccess.Get(px, i)).a / 255f;
                return alpha;
            }
            finally
            {
                typeof(RenderTexture).GetProperty("active")?.SetValue(null, prevActive);
                try { CallStatic(typeof(RenderTexture), "ReleaseTemporary", rt); } catch { }
                if (readable is UnityEngine.Object o && o != null) UnityEngine.Object.Destroy(o);
            }
        }
    }
}
