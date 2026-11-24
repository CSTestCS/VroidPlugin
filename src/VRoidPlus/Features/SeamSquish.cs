using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;

namespace VRoidPlus
{
    /// <summary>
    /// Seam squish: tight clothing (underwear, shorts...) keeps its size while the skin and other layers just
    /// outside its edges bulge a little and fade back to normal.
    ///
    /// VRoid's tight clothing is a full-body-shaped layer mesh whose outline comes from its texture's alpha. For
    /// each chosen layer the texture is read, every vertex is marked covered/uncovered, and the outline is found
    /// where a triangle edge crosses alpha 0.5. Vertices of the other meshes that lie outside the garment within
    /// "width" of the outline are pushed out along their normal, most at the edge. Painted-on clothing (part of the
    /// body texture) isn't a separate layer and isn't used.
    /// Settings live in ModelFile.Values: seam.amount, seam.width, seam.sel.&lt;meshName&gt;.
    /// </summary>
    internal class SeamSquish
    {
        private static ModelFile File => ModelFile.Current;

        public float Amount => File.Values.TryGetValue("seam.amount", out var v) ? v : 0f;          // 0..1
        public float WidthSetting => File.Values.TryGetValue("seam.width", out var v) ? v : 0.3f;  // 0..1
        public float WidthMeters => Mathf.Lerp(0.005f, 0.06f, Mathf.Clamp01(WidthSetting));
        public float AmountMeters => Mathf.Clamp01(Amount) * 0.012f;

        public void SetAmount(float v) { if (Mathf.Approximately(v, 0f)) File.Values.Remove("seam.amount"); else File.Values["seam.amount"] = v; Features.Body.MarkDirty(); }
        public void SetWidth(float v) { if (Mathf.Approximately(v, 0.3f)) File.Values.Remove("seam.width"); else File.Values["seam.width"] = v; Features.Body.MarkDirty(); }

        public bool IsSelected(string mesh) => File.Values.ContainsKey("seam.sel." + mesh);
        public void Select(string mesh, bool on)
        {
            if (on) File.Values["seam.sel." + mesh] = 1f; else File.Values.Remove("seam.sel." + mesh);
            Features.Body.MarkDirty();
        }

        public IEnumerable<string> Selected => File.Values.Keys.Where(k => k.StartsWith("seam.sel.", StringComparison.Ordinal)).Select(k => k.Substring(9));

        public bool Active => Amount > 0f && Selected.Any();

        /// <summary>Changes when a chosen layer's texture is replaced (repainted), so meshes get recomputed.</summary>
        public int Version { get; private set; }
        private float _nextTexCheck;
        private string _texIds = "";
        public string Info = "";

        /// <summary>Called by the body sliders each tick (cheap): notices repainted textures.</summary>
        public void Poll(Animator model)
        {
            if (!Active || model == null || Time.unscaledTime < _nextTexCheck) return;
            _nextTexCheck = Time.unscaledTime + 2f;
            var ids = new List<int>();
            var sel = new HashSet<string>(Selected);
            foreach (var smr in model.GetComponentsInChildren<SkinnedMeshRenderer>(true))
            {
                if (smr == null || !sel.Contains(smr.gameObject.name)) continue;
                try { if (TextureOf(smr) is UnityEngine.Object t && t != null) ids.Add(t.GetInstanceID()); } catch { }
            }
            var s = string.Join(",", ids);
            if (s != _texIds) { _texIds = s; Version++; }
        }

        public bool Affects(SkinnedMeshRenderer smr) =>
            Active && !IsSelected(smr.gameObject.name) && !smr.gameObject.name.StartsWith("HairStrand", StringComparison.Ordinal);

        // ------------------------------------------------------------------ layer data

        private class Layer
        {
            public Vector3[] Pos;
            public bool[] Covered;
            public List<Vector3> Edge = new();
            public Dictionary<long, List<int>> VertGrid;
            public Dictionary<long, List<int>> EdgeGrid;
            public float Cell;
        }

        private readonly Dictionary<string, Layer> _layers = new();          // key: model id | mesh name | version
        private readonly Dictionary<int, float[]> _alphaCache = new();         // texture id -> alpha
        private readonly Dictionary<int, (int w, int h)> _alphaSize = new();

        private static object TextureOf(SkinnedMeshRenderer smr)
        {
            var mat = UnityReflect.Get(smr, "sharedMaterial");
            return mat == null ? null : UnityReflect.Get(mat, "mainTexture");
        }

        private Layer GetLayer(Animator model, SkinnedMeshRenderer smr, Func<Mesh, Vector3[]> originalOf)
        {
            var key = $"{model.GetInstanceID()}|{smr.gameObject.name}|{Version}|{WidthMeters:F4}";
            if (_layers.TryGetValue(key, out var cached)) return cached;
            var mesh = smr.sharedMesh;
            var tex = TextureOf(smr) as Texture;
            if (mesh == null || tex == null) { Info = $"'{smr.gameObject.name}' has no texture to read"; return _layers[key] = null; }

            int tid = tex.GetInstanceID();
            if (!_alphaCache.TryGetValue(tid, out var alpha))
            {
                alpha = UnityReflect.ReadAlpha(tex, 1024, out var aw, out var ah);
                _alphaCache[tid] = alpha; _alphaSize[tid] = (aw, ah);
            }
            var (w, h) = _alphaSize[tid];
            var uv = UnityReflect.GetArray<Vector2>(mesh, "uv");
            var tris = UnityReflect.GetArray<int>(mesh, "triangles");
            var pos = originalOf(mesh);
            if (uv.Length != pos.Length) { Info = $"'{smr.gameObject.name}': texture coordinates don't match"; return _layers[key] = null; }

            float Sample(Vector2 t)
            {
                float x = (t.x - (float)Math.Floor(t.x)) * (w - 1), y = (t.y - (float)Math.Floor(t.y)) * (h - 1);
                int x0 = (int)x, y0 = (int)y, x1 = Math.Min(x0 + 1, w - 1), y1 = Math.Min(y0 + 1, h - 1);
                float fx = x - x0, fy = y - y0;
                float a = alpha[y0 * w + x0] * (1 - fx) + alpha[y0 * w + x1] * fx;
                float b = alpha[y1 * w + x0] * (1 - fx) + alpha[y1 * w + x1] * fx;
                return a * (1 - fy) + b * fy;
            }

            var a = new float[pos.Length];
            var layer = new Layer { Pos = pos, Covered = new bool[pos.Length], Cell = Math.Max(0.01f, WidthMeters) };
            for (int i = 0; i < pos.Length; i++) { a[i] = Sample(uv[i]); layer.Covered[i] = a[i] >= 0.5f; }

            // Outline: where an edge goes from covered to uncovered, at the alpha 0.5 crossing.
            var seen = new HashSet<long>();
            void Edge(int i, int j)
            {
                if (layer.Covered[i] == layer.Covered[j]) return;
                long k = i < j ? ((long)i << 32) | (uint)j : ((long)j << 32) | (uint)i;
                if (!seen.Add(k)) return;
                float t = Mathf.Clamp01((0.5f - a[i]) / (a[j] - a[i] + 1e-6f));
                layer.Edge.Add(Vector3.Lerp(pos[i], pos[j], t));
            }
            for (int t = 0; t + 2 < tris.Length; t += 3) { Edge(tris[t], tris[t + 1]); Edge(tris[t + 1], tris[t + 2]); Edge(tris[t + 2], tris[t]); }

            layer.VertGrid = BuildGrid(pos.Length, i => pos[i], 0.02f);
            layer.EdgeGrid = BuildGrid(layer.Edge.Count, i => layer.Edge[i], layer.Cell);
            Info = $"'{smr.gameObject.name}': {layer.Covered.Count(c => c)} of {pos.Length} vertices covered, outline {layer.Edge.Count} points";
            return _layers[key] = layer;
        }

        private static long CellKey(Vector3 p, float cell) =>
            ((long)Math.Floor(p.x / cell) & 0x1FFFFF) << 42 | ((long)Math.Floor(p.y / cell) & 0x1FFFFF) << 21 | ((long)Math.Floor(p.z / cell) & 0x1FFFFF);

        private static Dictionary<long, List<int>> BuildGrid(int n, Func<int, Vector3> at, float cell)
        {
            var g = new Dictionary<long, List<int>>();
            for (int i = 0; i < n; i++)
            {
                var k = CellKey(at(i), cell);
                if (!g.TryGetValue(k, out var l)) g[k] = l = new List<int>();
                l.Add(i);
            }
            return g;
        }

        private static float Nearest(Dictionary<long, List<int>> grid, Func<int, Vector3> at, float cell, Vector3 p, out int best)
        {
            best = -1; float bestD = float.MaxValue;
            int cx = (int)Math.Floor(p.x / cell), cy = (int)Math.Floor(p.y / cell), cz = (int)Math.Floor(p.z / cell);
            for (int dx = -1; dx <= 1; dx++)
            for (int dy = -1; dy <= 1; dy++)
            for (int dz = -1; dz <= 1; dz++)
            {
                long k = ((long)(cx + dx) & 0x1FFFFF) << 42 | ((long)(cy + dy) & 0x1FFFFF) << 21 | ((long)(cz + dz) & 0x1FFFFF);
                if (!grid.TryGetValue(k, out var l)) continue;
                foreach (var i in l)
                {
                    var d = (at(i) - p).sqrMagnitude;
                    if (d < bestD) { bestD = d; best = i; }
                }
            }
            return best >= 0 ? (float)Math.Sqrt(bestD) : float.MaxValue;
        }

        // ------------------------------------------------------------------ offsets for one mesh

        /// <summary>
        /// Mesh-space offsets for a target mesh (from its original vertices), or null when nothing moves.
        /// originalOf gives a mesh's un-reshaped vertices (the body sliders' record of it).
        /// </summary>
        public Vector3[] Offsets(Animator model, SkinnedMeshRenderer target, Vector3[] original, Func<Mesh, Vector3[]> originalOf)
        {
            if (!Affects(target) || model == null) return null;
            var sel = new HashSet<string>(Selected);
            var layers = new List<Layer>();
            foreach (var smr in model.GetComponentsInChildren<SkinnedMeshRenderer>(true))
            {
                if (smr == null || !sel.Contains(smr.gameObject.name)) continue;
                try { var l = GetLayer(model, smr, originalOf); if (l != null) layers.Add(l); }
                catch (Exception e) { Info = $"couldn't read '{smr.gameObject.name}': {(e.InnerException ?? e).Message}"; }
            }
            if (layers.Count == 0) return null;

            Vector3[] normals;
            try { normals = UnityReflect.GetArray<Vector3>(target.sharedMesh, "normals"); }
            catch { return null; }
            if (normals.Length != original.Length) return null;

            var width = WidthMeters; var amount = AmountMeters;
            var off = new Vector3[original.Length];
            bool any = false;
            for (int v = 0; v < original.Length; v++)
            {
                var p = original[v];
                float push = 0;
                foreach (var layer in layers)
                {
                    // Under the garment: keeps its size.
                    var dv = Nearest(layer.VertGrid, i => layer.Pos[i], 0.02f, p, out var nearestVert);
                    if (nearestVert >= 0 && dv < 0.02f && layer.Covered[nearestVert]) { push = 0; break; }
                    var de = Nearest(layer.EdgeGrid, i => layer.Edge[i], layer.Cell, p, out var nearestEdge);
                    if (nearestEdge < 0 || de >= width) continue;
                    var x = de / width;
                    var f = 1f - x * x * (3f - 2f * x);      // most at the edge, fading out smoothly
                    push = Math.Max(push, f);
                }
                if (push <= 0) continue;
                off[v] = normals[v].normalized * (amount * push);
                any = true;
            }
            return any ? off : null;
        }
    }
}
