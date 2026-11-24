using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using UnityEngine;

namespace VRoidPlus
{
    /// <summary>
    /// Makes VRoid's physics flowier or springier. Photo mode simulates hair, skirts, dresses etc. with VRM spring
    /// bones (VRM.VRMSpringBone: stiffness, drag, gravity) and VRoid's own colliders. This scales those settings per
    /// category; the original values are remembered, so 100% always means VRoid's own settings.
    /// Settings belong to the open .vroid file (ModelFile) like the body sliders.
    /// </summary>
    internal class PhysicsTuning
    {
        public static readonly (string Id, string Label)[] Categories =
        {
            ("skirt", "Skirts & dresses"), ("hair", "Hair"), ("bust", "Bust"), ("other", "Other (accessories, ties...)"),
        };

        public static readonly (string Id, string Label, float Min, float Max)[] Params =
        {
            ("stiff", "Stiffness (lower = flowier)", 0.05f, 3f),
            ("drag", "Damping (lower = bouncier)", 0.05f, 3f),
            ("gravity", "Gravity", 0f, 3f),
        };

        private static ModelFile File => ModelFile.Current;
        private static string Key(string cat, string p) => $"phys.{cat}.{p}";

        public float Get(string cat, string p) => File.Values.TryGetValue(Key(cat, p), out var v) ? v : 1f;

        public void Set(string cat, string p, float v)
        {
            if (Mathf.Approximately(v, 1f)) File.Values.Remove(Key(cat, p)); else File.Values[Key(cat, p)] = v;
            _next = 0;
        }

        public void Reset(string cat) { foreach (var p in Params) File.Values.Remove(Key(cat, p.Id)); _next = 0; }

        private class Spring
        {
            public object Wrapper;
            public string Category;
            public float Stiff, Drag, Gravity;      // VRoid's own values
            public string Applied = "";
            /// <summary>Every bone the spring moves, with its rotation when we first saw it (its resting shape).</summary>
            public readonly List<(Transform bone, Quaternion rest)> Rest = new();
        }

        /// <summary>
        /// Re-initialising a spring (needed for new settings to take effect) makes it treat the bones' current
        /// rotations as its resting shape. Done mid-swing, that locks the swung-out shape in. So the bones are put
        /// back to the rotations recorded when the spring first appeared, then the spring is re-initialised.
        /// </summary>
        private static void RestoreAndSetup(Spring sp)
        {
            foreach (var (bone, rest) in sp.Rest) if (bone != null) bone.localRotation = rest;
            _setup?.Invoke(sp.Wrapper, new object[] { true });
        }

        private bool _resetRequested;

        /// <summary>Puts every spring back to its resting shape (in case one got stuck).</summary>
        public void ResetAll() { _resetRequested = true; _next = 0; }

        private readonly Dictionary<int, Spring> _springs = new();
        private float _next;
        public string Info = "not run yet";
        private static Type _type;
        private static PropertyInfo _pStiff, _pDrag, _pGravity, _pRoots;
        private static MethodInfo _setup;

        public void Update()
        {
            if (Time.unscaledTime < _next) return;
            _next = Time.unscaledTime + 0.1f;   // often enough to see new springs before they start swinging

            var comps = Compat.FindComponentsByTypeName("VRM.VRMSpringBone, VRM");
            if (comps.Count == 0) { Info = "no spring bones in the scene (physics runs in photo mode)"; return; }
            if (_type == null)
            {
                _type = ReflectionPath.FindType("VRM.VRMSpringBone");
                if (_type == null) { Info = "VRM.VRMSpringBone type not found"; return; }
                _pStiff = _type.GetProperty("m_stiffnessForce");
                _pDrag = _type.GetProperty("m_dragForce");
                _pGravity = _type.GetProperty("m_gravityPower");
                _pRoots = _type.GetProperty("RootBones");
                _setup = _type.GetMethods().FirstOrDefault(m => m.Name == "Setup" && m.GetParameters().Length == 1);
            }

            var alive = new HashSet<int>();
            int changed = 0;
            var perCat = new Dictionary<string, int>();
            foreach (var c in comps)
            {
                if (c == null) continue;
                int id = c.GetInstanceID();
                alive.Add(id);
                if (!_springs.TryGetValue(id, out var sp))
                {
                    var w = Compat.CastTo(c, _type);
                    if (w == null) continue;
                    sp = new Spring
                    {
                        Wrapper = w,
                        Category = Categorize(w),
                        Stiff = Convert.ToSingle(_pStiff.GetValue(w)),
                        Drag = Convert.ToSingle(_pDrag.GetValue(w)),
                        Gravity = Convert.ToSingle(_pGravity.GetValue(w)),
                    };
                    RecordRest(sp, w);
                    _springs[id] = sp;
                }
                perCat[sp.Category] = perCat.TryGetValue(sp.Category, out var n) ? n + 1 : 1;

                float s = Get(sp.Category, "stiff"), dr = Get(sp.Category, "drag"), g = Get(sp.Category, "gravity");
                var key = $"{s:F3}|{dr:F3}|{g:F3}";
                if (sp.Applied == key) continue;
                try
                {
                    _pStiff.SetValue(sp.Wrapper, sp.Stiff * s);
                    _pDrag.SetValue(sp.Wrapper, Mathf.Clamp01(sp.Drag * dr));
                    _pGravity.SetValue(sp.Wrapper, sp.Gravity * g);
                    // Re-initialise (from the resting shape) so the spring system picks the new values up.
                    RestoreAndSetup(sp);
                    sp.Applied = key;
                    changed++;
                }
                catch (Exception e) { Info = "couldn't change a spring: " + (e.InnerException ?? e).Message; }
            }
            if (_resetRequested)
            {
                _resetRequested = false;
                foreach (var sp in _springs.Values) { try { RestoreAndSetup(sp); } catch { } }
            }
            foreach (var gone in _springs.Keys.Where(k => !alive.Contains(k)).ToList()) _springs.Remove(gone);
            Info = $"{comps.Count} spring bones ({string.Join(", ", perCat.Select(kv => $"{kv.Value} {kv.Key}"))}), {changed} updated just now";
        }

        private static void RecordRest(Spring sp, object spring)
        {
            try
            {
                var roots = _pRoots?.GetValue(spring);
                if (roots == null || !ListAccess.IsList(roots)) return;
                var seen = new HashSet<int>();
                for (int i = 0; i < ListAccess.Count(roots); i++)
                {
                    if (!(ListAccess.Get(roots, i) is Transform root) || root == null) continue;
                    foreach (var t in root.GetComponentsInChildren<Transform>(true))
                        if (t != null && seen.Add(t.GetInstanceID())) sp.Rest.Add((t, t.localRotation));
                }
            }
            catch { }
        }

        /// <summary>Category from the names of the bones a spring moves (VRoid names: J_Sec_*Skirt*, HairJoint-*, J_Sec_*Bust*).</summary>
        private static string Categorize(object spring)
        {
            var names = new List<string>();
            try
            {
                var roots = _pRoots?.GetValue(spring);
                if (roots != null && ListAccess.IsList(roots))
                    for (int i = 0; i < ListAccess.Count(roots) && i < 8; i++)
                        if (ListAccess.Get(roots, i) is Transform t && t != null) names.Add(t.name);
            }
            catch { }
            var all = string.Join(" ", names);
            bool Has(params string[] words) => words.Any(wd => all.IndexOf(wd, StringComparison.OrdinalIgnoreCase) >= 0);
            if (Has("Hair")) return "hair";
            if (Has("Bust", "Breast")) return "bust";
            if (Has("Skirt", "Dress", "Onepiece", "Coat", "Cape", "Robe")) return "skirt";
            return "other";
        }
    }
}
