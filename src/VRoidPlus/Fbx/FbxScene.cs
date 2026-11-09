using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;

namespace VRoidPlus.Fbx
{
    internal class FbxCurve
    {
        public double[] Times;  // seconds
        public float[] Values;

        public float Evaluate(double t)
        {
            if (Times.Length == 0) return 0;
            if (t <= Times[0]) return Values[0];
            int last = Times.Length - 1;
            if (t >= Times[last]) return Values[last];
            int lo = 0, hi = last;
            while (hi - lo > 1) { int mid = (lo + hi) >> 1; if (Times[mid] <= t) lo = mid; else hi = mid; }
            var span = Times[hi] - Times[lo];
            var f = span > 0 ? (float)((t - Times[lo]) / span) : 0f;
            return Mathf.Lerp(Values[lo], Values[hi], f);
        }
    }

    /// <summary>The three curves (X, Y, Z) driving one channel (translation, rotation or scale) of one bone.</summary>
    internal class FbxChannel
    {
        public FbxCurve[] Axes = new FbxCurve[3];
        public Vector3 Defaults;

        public Vector3 Evaluate(double t, Vector3 fallback)
        {
            var d = Defaults;
            return new Vector3(
                Axes[0] != null ? Axes[0].Evaluate(t) : d.x,
                Axes[1] != null ? Axes[1].Evaluate(t) : d.y,
                Axes[2] != null ? Axes[2].Evaluate(t) : d.z);
        }
    }

    internal class FbxModel
    {
        public long Id;
        public string Name;
        public FbxModel Parent;
        public int Depth;
        public Vector3 T, R, S = Vector3.one, Pre, Post;
        public bool UsePrePost;
        public int RotationOrder;
        public Matrix4x4? BindGlobal;

        public Quaternion Rotation(Vector3 euler)
        {
            var r = Euler(euler, RotationOrder);
            if (!UsePrePost) return r;
            return Euler(Pre, 0) * r * Quaternion.Inverse(Euler(Post, 0));
        }

        private static readonly string[] Orders = { "XYZ", "XZY", "YZX", "YXZ", "ZXY", "ZYX", "XYZ" };

        /// <summary>FBX euler order "XYZ" means X is applied first: q = qZ * qY * qX.</summary>
        public static Quaternion Euler(Vector3 e, int order)
        {
            var o = Orders[Mathf.Clamp(order, 0, 6)];
            Quaternion Axis(char c) => c switch
            {
                'X' => Quaternion.AngleAxis(e.x, Vector3.right),
                'Y' => Quaternion.AngleAxis(e.y, Vector3.up),
                _ => Quaternion.AngleAxis(e.z, Vector3.forward),
            };
            return Axis(o[2]) * Axis(o[1]) * Axis(o[0]);
        }
    }

    internal class FbxTake
    {
        public string Name;
        public double Start, End;
        public readonly Dictionary<FbxModel, FbxChannel> Translation = new();
        public readonly Dictionary<FbxModel, FbxChannel> Rotation = new();
        public readonly Dictionary<FbxModel, FbxChannel> Scale = new();
        public double Length => Math.Max(0, End - Start);
    }

    /// <summary>The skeleton and animations of an FBX file, converted to Unity's coordinate system.</summary>
    internal class FbxScene
    {
        private const double TicksPerSecond = 46186158000.0;

        public readonly List<FbxModel> Models = new();
        public readonly List<FbxTake> Takes = new();
        /// <summary>FBX file space -> Unity space.</summary>
        public Matrix4x4 Axis = Matrix4x4.identity;

        public static FbxScene Load(string path)
        {
            var root = FbxReader.Read(path);
            var scene = new FbxScene();
            scene.Build(root);
            return scene;
        }

        private void Build(FbxNode root)
        {
            ReadAxes(root.Child("GlobalSettings"));
            var objects = root.Child("Objects") ?? new FbxNode();
            var models = new Dictionary<long, FbxModel>();
            var curveNodes = new Dictionary<long, FbxChannel>();
            var curves = new Dictionary<long, FbxCurve>();
            var layers = new HashSet<long>();
            var stacks = new Dictionary<long, FbxTake>();

            foreach (var n in objects.Children)
            {
                if (n.Props.Count < 2) continue;
                long id = n.Long(0);
                switch (n.Name)
                {
                    case "Model":
                        var m = new FbxModel { Id = id, Name = CleanName(n.Str(1)) };
                        ReadModelProps(m, n.Child("Properties70"));
                        models[id] = m;
                        break;
                    case "AnimationCurveNode":
                        var ch = new FbxChannel();
                        foreach (var p in Props70(n))
                        {
                            if (p.Str(0) == "d|X") ch.Defaults.x = (float)p.Double(4);
                            else if (p.Str(0) == "d|Y") ch.Defaults.y = (float)p.Double(4);
                            else if (p.Str(0) == "d|Z") ch.Defaults.z = (float)p.Double(4);
                        }
                        curveNodes[id] = ch;
                        break;
                    case "AnimationCurve":
                        var kt = n.Child("KeyTime")?.Props.FirstOrDefault() as double[];
                        var kv = n.Child("KeyValueFloat")?.Props.FirstOrDefault() as double[];
                        if (kt != null && kv != null && kt.Length == kv.Length)
                            curves[id] = new FbxCurve { Times = kt.Select(x => x / TicksPerSecond).ToArray(), Values = kv.Select(x => (float)x).ToArray() };
                        break;
                    case "AnimationLayer":
                        layers.Add(id);
                        break;
                    case "AnimationStack":
                        stacks[id] = new FbxTake { Name = CleanName(n.Str(1)) };
                        break;
                }
            }
            foreach (var pose in objects.All("Pose"))
            {
                foreach (var pn in pose.All("PoseNode"))
                {
                    var node = pn.Child("Node"); var mat = pn.Child("Matrix")?.Props.FirstOrDefault() as double[];
                    if (node == null || mat == null || mat.Length != 16) continue;
                    if (!models.TryGetValue(node.Long(0), out var pm)) continue;
                    var M = new Matrix4x4();
                    for (int i = 0; i < 16; i++) M[i] = (float)mat[i];
                    pm.BindGlobal ??= M;
                }
            }

            // Connections: "OO" child parent | "OP" child parent property
            var curveNodeTarget = new Dictionary<long, (FbxModel model, string prop)>();
            var curveNodeLayer = new Dictionary<long, long>();
            var layerStack = new Dictionary<long, long>();
            foreach (var c in root.Child("Connections")?.All("C") ?? Enumerable.Empty<FbxNode>())
            {
                if (c.Props.Count < 3) continue;
                var kind = c.Str(0); long child = c.Long(1), parent = c.Long(2);
                var prop = c.Props.Count > 3 ? c.Str(3) : "";
                if (models.TryGetValue(child, out var cm) && models.TryGetValue(parent, out var pmod) && kind == "OO") cm.Parent = pmod;
                else if (curves.TryGetValue(child, out var curve) && curveNodes.TryGetValue(parent, out var cn))
                {
                    int axis = prop.EndsWith("X") ? 0 : prop.EndsWith("Y") ? 1 : prop.EndsWith("Z") ? 2 : -1;
                    if (axis >= 0) cn.Axes[axis] = curve;
                }
                else if (curveNodes.ContainsKey(child) && models.TryGetValue(parent, out var target) && kind == "OP") curveNodeTarget[child] = (target, prop);
                else if (curveNodes.ContainsKey(child) && layers.Contains(parent)) curveNodeLayer[child] = parent;
                else if (layers.Contains(child) && stacks.ContainsKey(parent)) layerStack.TryAdd(child, parent);
            }

            foreach (var m in models.Values)
            {
                int d = 0; for (var p = m.Parent; p != null && d < 512; p = p.Parent) d++;
                m.Depth = d;
            }
            Models.AddRange(models.Values.OrderBy(m => m.Depth));

            // Files without stacks (rare) still get one take holding every curve.
            if (stacks.Count == 0) stacks[-1] = new FbxTake { Name = "Take 001" };
            foreach (var kv in curveNodeTarget)
            {
                FbxTake take;
                if (curveNodeLayer.TryGetValue(kv.Key, out var layer) && layerStack.TryGetValue(layer, out var stackId) && stacks.TryGetValue(stackId, out var st)) take = st;
                else take = stacks.Values.First();
                var ch = curveNodes[kv.Key];
                var (model, prop) = kv.Value;
                if (prop == "Lcl Translation") take.Translation[model] = ch;
                else if (prop == "Lcl Rotation") take.Rotation[model] = ch;
                else if (prop == "Lcl Scaling") take.Scale[model] = ch;
            }
            foreach (var take in stacks.Values)
            {
                var all = take.Translation.Values.Concat(take.Rotation.Values).Concat(take.Scale.Values)
                    .SelectMany(c => c.Axes).Where(c => c != null && c.Times.Length > 0).ToList();
                take.Start = all.Count > 0 ? all.Min(c => c.Times[0]) : 0;
                take.End = all.Count > 0 ? all.Max(c => c.Times[c.Times.Length - 1]) : 0;
                Takes.Add(take);
            }
            Takes.Sort((a, b) => b.Rotation.Count.CompareTo(a.Rotation.Count) != 0 ? b.Rotation.Count.CompareTo(a.Rotation.Count) : string.CompareOrdinal(a.Name, b.Name));
        }

        private void ReadAxes(FbxNode globalSettings)
        {
            int up = 1, upSign = 1, front = 2, frontSign = 1, coord = 0, coordSign = 1;
            foreach (var p in Props70(globalSettings))
            {
                switch (p.Str(0))
                {
                    case "UpAxis": up = (int)p.Long(4); break;
                    case "UpAxisSign": upSign = (int)p.Long(4); break;
                    case "FrontAxis": front = (int)p.Long(4); break;
                    case "FrontAxisSign": frontSign = (int)p.Long(4); break;
                    case "CoordAxis": coord = (int)p.Long(4); break;
                    case "CoordAxisSign": coordSign = (int)p.Long(4); break;
                }
            }
            if (up == front || up == coord || front == coord) { up = 1; front = 2; coord = 0; }
            // Same convention as Unity's FBX importer: file right -> Unity -X, up -> +Y, front -> +Z.
            var m = Matrix4x4.zero;
            m.SetColumn(coord, new Vector4(-1, 0, 0, 0) * coordSign);
            m.SetColumn(up, new Vector4(0, 1, 0, 0) * upSign);
            m.SetColumn(front, new Vector4(0, 0, 1, 0) * frontSign);
            m.m33 = 1;
            Axis = m;
        }

        private static void ReadModelProps(FbxModel m, FbxNode props70)
        {
            bool? active = null;
            foreach (var p in Props70Of(props70))
            {
                var name = p.Str(0);
                Vector3 V() => p.Props.Count >= 7 ? new Vector3((float)p.Double(4), (float)p.Double(5), (float)p.Double(6)) : Vector3.zero;
                switch (name)
                {
                    case "Lcl Translation": m.T = V(); break;
                    case "Lcl Rotation": m.R = V(); break;
                    case "Lcl Scaling": m.S = V(); break;
                    case "PreRotation": m.Pre = V(); break;
                    case "PostRotation": m.Post = V(); break;
                    case "RotationOrder": m.RotationOrder = (int)p.Long(4); break;
                    case "RotationActive": active = p.Long(4) != 0; break;
                }
            }
            m.UsePrePost = active ?? (m.Pre != Vector3.zero || m.Post != Vector3.zero);
        }

        private static IEnumerable<FbxNode> Props70(FbxNode n) => Props70Of(n?.Child("Properties70"));
        private static IEnumerable<FbxNode> Props70Of(FbxNode p70) =>
            p70 == null ? Enumerable.Empty<FbxNode>() : p70.All("P").Where(p => p.Props.Count >= 5 && p.Props[0] is string);

        /// <summary>"Hips\0\x01Model" -> "Hips"</summary>
        private static string CleanName(string raw)
        {
            var i = raw.IndexOf('\0');
            return i >= 0 ? raw.Substring(0, i) : raw;
        }

        // ---------------------------------------------------------------- evaluation

        /// <summary>Global transforms of every model in Unity space. take == null gives the rest/bind pose.</summary>
        public Dictionary<FbxModel, Matrix4x4> Evaluate(FbxTake take, double time)
        {
            var globals = new Dictionary<FbxModel, Matrix4x4>(Models.Count);
            var fileGlobals = new Dictionary<FbxModel, Matrix4x4>(Models.Count);
            foreach (var m in Models) // sorted parents first
            {
                Matrix4x4 g;
                if (take == null && m.BindGlobal.HasValue) g = m.BindGlobal.Value;
                else
                {
                    var t = m.T; var r = m.R; var s = m.S;
                    if (take != null)
                    {
                        var tt = take.Start + time;
                        if (take.Translation.TryGetValue(m, out var ct)) t = ct.Evaluate(tt, t);
                        if (take.Rotation.TryGetValue(m, out var cr)) r = cr.Evaluate(tt, r);
                        if (take.Scale.TryGetValue(m, out var cs)) s = cs.Evaluate(tt, s);
                    }
                    var local = Matrix4x4.TRS(t, m.Rotation(r), s);
                    g = m.Parent != null && fileGlobals.TryGetValue(m.Parent, out var pg) ? pg * local : local;
                }
                fileGlobals[m] = g;
                globals[m] = Axis * g * Axis.inverse;
            }
            return globals;
        }

        public static Vector3 Position(Matrix4x4 m) => m.GetColumn(3);

        public static Quaternion Rotation(Matrix4x4 m)
        {
            Vector3 f = m.GetColumn(2), u = m.GetColumn(1);
            if (f.sqrMagnitude < 1e-12f || u.sqrMagnitude < 1e-12f) return Quaternion.identity;
            return Quaternion.LookRotation(f.normalized, u.normalized);
        }
    }
}
