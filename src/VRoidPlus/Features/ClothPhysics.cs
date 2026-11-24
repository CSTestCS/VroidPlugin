using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;

namespace VRoidPlus
{
    public class ClothPhysicsRule
    {
        /// <summary>Name of the clothing mesh (its GameObject).</summary>
        public string Mesh = "";
        /// <summary>"skirt" (ring of chains), "hem" (ring of chains on a top's lower part), "hanging" (one chain: tie, ribbon...).</summary>
        public string Kind = "skirt";
        public int Chains = 12;
        public int Joints = 4;
        /// <summary>How much of the item moves, from its bottom edge up (0..1).</summary>
        public float Reach = 1f;

        public string Signature => $"{Kind}|{Chains}|{Joints}|{Reach:F2}";
    }

    /// <summary>
    /// Adds physics bones to clothing in photo mode (where VRoid runs VRM spring bones): new bone chains are
    /// placed in the item (a ring of chains for skirts/dresses and shirt hems, one chain for ties and other hanging
    /// items), the moving part of the mesh is re-weighted to them, and a VRM spring bone drives them using the same
    /// physics settings and colliders as VRoid's own skirt physics, so the body still pushes them away.
    /// Rules are stored with the .vroid file (ModelFile).
    /// </summary>
    internal class ClothPhysics
    {
        public static readonly (string Id, string Label)[] Kinds =
        {
            ("off", "Off"), ("skirt", "Skirt / dress"), ("hem", "Shirt hem"), ("hanging", "Tie / hanging item"),
        };

        public List<ClothPhysicsRule> Rules => ModelFile.Current.ClothPhysics;
        public ClothPhysicsRule RuleFor(string mesh) => Rules.FirstOrDefault(r => r.Mesh == mesh);
        public readonly Dictionary<string, string> Status = new();   // mesh name -> what happened

        private class Built
        {
            public SkinnedMeshRenderer Smr;
            public Mesh Mesh;
            public string Signature;
            public Transform[] OrigBones;
            public Matrix4x4[] OrigBind;
            public BoneWeight[] OrigWeights;
            public readonly List<GameObject> Created = new();
        }

        private readonly Dictionary<int, Built> _built = new();
        private float _next;

        private static readonly string[] NotClothing = { "Body", "Face", "Head", "Hand", "Foot", "Ear", "EyeExtra", "BaseHair" };

        public static bool IsClothingCandidate(SkinnedMeshRenderer smr)
        {
            var n = smr.gameObject.name;
            return !NotClothing.Contains(n) && !n.StartsWith("HairStrand", StringComparison.Ordinal) && !n.StartsWith("VRoidPlus", StringComparison.Ordinal);
        }

        public static string Label(SkinnedMeshRenderer smr)
        {
            string mat = null;
            try { mat = (UnityReflect.Get(smr, "sharedMaterial") as UnityEngine.Object)?.name; } catch { }
            var n = smr.gameObject.name;
            var shortName = n.Length > 10 ? n.Substring(0, 8) + "…" : n;
            return string.IsNullOrEmpty(mat) ? shortName : $"{mat}  ({shortName})";
        }

        /// <summary>Photo mode's model (where physics runs), or the editor model to pick items from.</summary>
        public static Animator PickModel(out bool physicsRunning)
        {
            var photo = Avatar.AllHumanoids().FirstOrDefault(a => a != null && a.gameObject.name == "PhotoBoothModel");
            physicsRunning = photo != null;
            return photo ?? Avatar.Animator;
        }

        public void SetRule(string mesh, string kind)
        {
            var r = RuleFor(mesh);
            if (kind == "off") { Rules.RemoveAll(x => x.Mesh == mesh); }
            else if (r == null)
            {
                Rules.Add(new ClothPhysicsRule
                {
                    Mesh = mesh, Kind = kind,
                    Chains = kind == "hanging" ? 1 : 12, Joints = 4, Reach = kind == "hem" ? 0.35f : 1f,
                });
            }
            else
            {
                r.Kind = kind;
                if (kind == "hanging") r.Chains = 1; else if (r.Chains < 3) r.Chains = 12;
                if (kind == "hem" && r.Reach > 0.6f) r.Reach = 0.35f;
            }
            _next = 0;
        }

        public void Changed() => _next = Time.unscaledTime + 0.4f;   // rebuild shortly after a slider settles

        // ------------------------------------------------------------------ update

        public void Update()
        {
            if (Time.unscaledTime < _next) return;
            _next = Time.unscaledTime + 0.5f;

            // Forget meshes that are gone (photo mode closed).
            foreach (var k in _built.Where(kv => kv.Value.Smr == null || kv.Value.Mesh == null).Select(kv => kv.Key).ToList()) _built.Remove(k);

            var springType = ReflectionPath.FindType("VRM.VRMSpringBone");
            if (springType == null) return;
            var springs = Compat.FindComponentsByTypeName("VRM.VRMSpringBone, VRM");

            foreach (var animator in Avatar.AllHumanoids())
            {
                if (animator == null) continue;
                // Only models whose physics VRoid is running (photo mode).
                var modelSprings = springs.Where(c => c != null && c.transform.IsChildOf(animator.transform) && !c.gameObject.name.StartsWith("VRoidPlus")).ToList();
                if (modelSprings.Count == 0) continue;

                foreach (var smr in animator.GetComponentsInChildren<SkinnedMeshRenderer>(true))
                {
                    if (smr == null || !IsClothingCandidate(smr)) continue;
                    var id = smr.GetInstanceID();
                    var rule = RuleFor(smr.gameObject.name);
                    _built.TryGetValue(id, out var built);
                    if (built != null && (rule == null || built.Signature != rule.Signature)) { Undo(built); _built.Remove(id); built = null; }
                    if (rule == null || built != null) continue;
                    try
                    {
                        built = Build(smr, rule, animator, springType, modelSprings);
                        if (built != null) _built[id] = built;
                    }
                    catch (Exception e)
                    {
                        var msg = (e is System.Reflection.TargetInvocationException t ? t.InnerException ?? e : e).Message;
                        Status[smr.gameObject.name] = "couldn't add physics: " + msg;
                        Plugin.Logger.LogWarning($"Cloth physics on {smr.gameObject.name}: {e}");
                        _built[id] = new Built { Smr = smr, Mesh = smr.sharedMesh, Signature = rule.Signature };   // don't retry every half second
                    }
                }
            }
        }

        // ------------------------------------------------------------------ build / undo

        private Built Build(SkinnedMeshRenderer smr, ClothPhysicsRule rule, Animator animator, Type springType, List<Component> modelSprings)
        {
            var mesh = smr.sharedMesh;
            if (mesh == null) return null;
            var name = smr.gameObject.name;
            var bonesSrc = smr.bones;
            var bones = new Transform[bonesSrc.Length];
            for (int i = 0; i < bones.Length; i++) bones[i] = bonesSrc[i];
            var verts = Copy.Vertices(mesh);
            var weights = Copy.BoneWeights(mesh);
            var bind = Copy.BindPoses(mesh);
            if (weights.Length != verts.Length || bind.Length != bones.Length) { Status[name] = "this mesh's skin data doesn't line up"; return null; }

            // Skeleton bits.
            var human = new Dictionary<int, HumanBodyBones>();
            for (var b = HumanBodyBones.Hips; b < HumanBodyBones.LastBone; b++) { var t = animator.GetBoneTransform(b); if (t != null) human[t.GetInstanceID()] = b; }
            Transform Owner(Transform t) { for (var p = t; p != null; p = p.parent) if (human.ContainsKey(p.GetInstanceID())) return p; return null; }
            var hips = animator.GetBoneTransform(HumanBodyBones.Hips);
            var spine = animator.GetBoneTransform(HumanBodyBones.Spine);
            var chest = animator.GetBoneTransform(HumanBodyBones.Chest);
            if (hips == null || spine == null) { Status[name] = "no hips/spine bones"; return null; }

            var allowed = new HashSet<int>();
            switch (rule.Kind)
            {
                case "skirt":
                    foreach (var hb in new[] { HumanBodyBones.Hips, HumanBodyBones.Spine, HumanBodyBones.LeftUpperLeg, HumanBodyBones.RightUpperLeg })
                    { var t = animator.GetBoneTransform(hb); if (t != null) allowed.Add(t.GetInstanceID()); }
                    break;
                case "hem":
                    foreach (var hb in new[] { HumanBodyBones.Hips, HumanBodyBones.Spine, HumanBodyBones.Chest, HumanBodyBones.UpperChest })
                    { var t = animator.GetBoneTransform(hb); if (t != null) allowed.Add(t.GetInstanceID()); }
                    break;
            }

            // Dominant bone of each vertex -> its humanoid owner; pick the vertices this rule moves.
            var ownerOf = new Transform[verts.Length];
            var counts = new Dictionary<int, (Transform t, int n)>();
            for (int v = 0; v < verts.Length; v++)
            {
                var w = weights[v];
                int bi = w.boneIndex0; float bw = w.weight0;
                if (w.weight1 > bw) { bi = w.boneIndex1; bw = w.weight1; }
                if (w.weight2 > bw) { bi = w.boneIndex2; bw = w.weight2; }
                if (w.weight3 > bw) { bi = w.boneIndex3; }
                var o = bi >= 0 && bi < bones.Length && bones[bi] != null ? Owner(bones[bi]) : null;
                ownerOf[v] = o;
                if (o != null) { var oid = o.GetInstanceID(); counts[oid] = (o, counts.TryGetValue(oid, out var c) ? c.n + 1 : 1); }
            }
            bool InRegion(int v) => rule.Kind == "hanging" || (ownerOf[v] != null && allowed.Contains(ownerOf[v].GetInstanceID()));

            // The bone the chains hang from, and its local frame (bind space).
            Transform parent = rule.Kind == "skirt" ? hips : rule.Kind == "hem" ? (chest ?? spine) : counts.Values.OrderByDescending(x => x.n).Select(x => x.t).FirstOrDefault() ?? hips;
            int pIndex = -1;
            var pid = parent.GetInstanceID();
            for (int i = 0; i < bones.Length; i++) if (bones[i] != null && bones[i].GetInstanceID() == pid) { pIndex = i; break; }
            Matrix4x4 pBind;
            if (pIndex >= 0) pBind = bind[pIndex];
            else pBind = (Matrix4x4)UnityReflect.Get(parent, "worldToLocalMatrix") * (Matrix4x4)UnityReflect.Get(smr.transform, "localToWorldMatrix");
            var head = animator.GetBoneTransform(HumanBodyBones.Head);
            var bodyUpW = head != null ? head.position - hips.position : spine.position - hips.position;
            Vector3 up = rule.Kind == "hanging" ? parent.InverseTransformDirection(bodyUpW)
                       : rule.Kind == "skirt" ? spine.localPosition : parent.InverseTransformDirection(spine.position - hips.position);
            if (up.sqrMagnitude < 1e-10f) up = Vector3.up;
            up.Normalize();
            var l = animator.GetBoneTransform(HumanBodyBones.LeftUpperLeg); var r = animator.GetBoneTransform(HumanBodyBones.RightUpperLeg);
            if (l == null || r == null) { Status[name] = "no leg bones"; return null; }
            var right = Vector3.ProjectOnPlane(parent.InverseTransformDirection(r.position - l.position), up).normalized;
            // Unity's Cross(right, up) is the direction the body faces.
            var fwd = Vector3.Cross(right, up).normalized;

            // Region in the parent's space.
            var q = new Vector3[verts.Length];
            float hMin = float.MaxValue, hMax = float.MinValue;
            int regionCount = 0;
            for (int v = 0; v < verts.Length; v++)
            {
                q[v] = pBind.MultiplyPoint3x4(verts[v]);
                if (!InRegion(v)) continue;
                var h = Vector3.Dot(q[v], up);
                hMin = Mathf.Min(hMin, h); hMax = Mathf.Max(hMax, h);
                regionCount++;
            }
            if (regionCount < 8 || hMax - hMin < 1e-4f) { Status[name] = "nothing to move on this item for that type"; return null; }
            float hTop = hMin + Mathf.Clamp(rule.Reach, 0.1f, 1f) * (hMax - hMin);
            int J = Math.Clamp(rule.Joints, 2, 10);
            int N = rule.Kind == "hanging" ? 1 : Math.Clamp(rule.Chains, 3, 32);
            float dh = (hTop - hMin) / (J - 1);

            float Angle(Vector3 p) { var x = Vector3.Dot(p, right); var z = Vector3.Dot(p, fwd); return (float)Math.Atan2(x, z); }   // 0 = front, radians
            float AngleDiffDeg(float aRad, float bRad)
            {
                var d = (aRad - bRad) * (180.0 / Math.PI);
                d = ((d % 360) + 540) % 360 - 180;
                return (float)Math.Abs(d);
            }
            Vector3 Horizontal(Vector3 p) => p - up * Vector3.Dot(p, up);

            // Joint positions (parent space).
            var pos = new Vector3[N, J];
            for (int j = 0; j < J; j++)
            {
                float h = hTop - dh * j;
                if (N == 1)
                {
                    var sum = Vector3.zero; int n = 0;
                    for (int v = 0; v < verts.Length; v++)
                        if (InRegion(v) && Mathf.Abs(Vector3.Dot(q[v], up) - h) <= dh * 0.75f) { sum += Horizontal(q[v]); n++; }
                    pos[0, j] = up * h + (n > 0 ? sum / n : Vector3.zero);
                    continue;
                }
                float avgAll = 0; int nAll = 0;
                for (int v = 0; v < verts.Length; v++)
                    if (InRegion(v) && Mathf.Abs(Vector3.Dot(q[v], up) - h) <= dh * 0.75f) { avgAll += Horizontal(q[v]).magnitude; nAll++; }
                avgAll = nAll > 0 ? avgAll / nAll : 0.1f;
                for (int k = 0; k < N; k++)
                {
                    float th = (float)(2 * Math.PI * k / N);
                    float sumR = 0; int n = 0;
                    for (int v = 0; v < verts.Length; v++)
                    {
                        if (!InRegion(v) || Mathf.Abs(Vector3.Dot(q[v], up) - h) > dh * 0.75f) continue;
                        if (AngleDiffDeg(Angle(q[v]), th) > 180f / N) continue;
                        sumR += Horizontal(q[v]).magnitude; n++;
                    }
                    float radius = n > 0 ? sumR / n : avgAll;
                    pos[k, j] = up * h + (fwd * (float)Math.Cos(th) + right * (float)Math.Sin(th)) * radius;
                }
            }

            // Create the chains under the parent bone; joint frames share the parent's orientation, so a joint's
            // bind pose is the parent's bind pose moved to the joint.
            var built = new Built { Smr = smr, Mesh = mesh, Signature = rule.Signature, OrigBones = bones, OrigBind = bind, OrigWeights = weights };
            var kindName = rule.Kind == "hanging" ? "Tie" : rule.Kind == "hem" ? "HemSkirt" : "Skirt";
            var newBones = new List<Transform>(bones);
            var newBind = new List<Matrix4x4>(bind);
            var roots = new List<Transform>();
            var jointIndex = new int[N, J];
            for (int k = 0; k < N; k++)
            {
                Transform prev = parent;
                Vector3 prevPos = Vector3.zero;
                for (int j = 0; j < J; j++)
                {
                    var go = new GameObject();
                    go.name = $"VRoidPlus_{kindName}_{name}_{k}_{j}";
                    var t = go.transform;
                    t.SetParent(prev, false);
                    t.localPosition = pos[k, j] - prevPos;
                    t.localRotation = Quaternion.identity;
                    if (j == 0) { roots.Add(t); built.Created.Add(go); }
                    jointIndex[k, j] = newBones.Count;
                    newBones.Add(t);
                    newBind.Add(UnityReflect.Translation(-pos[k, j]) * pBind);
                    prev = t; prevPos = pos[k, j];
                }
            }

            // Re-weight the moving part: between the two nearest chains around the item and the two nearest joints
            // along them. Vertices above the reach keep their weights, so the top edge stays on the body.
            var newWeights = (BoneWeight[])weights.Clone();
            int moved = 0;
            for (int v = 0; v < verts.Length; v++)
            {
                if (!InRegion(v)) continue;
                var h = Vector3.Dot(q[v], up);
                if (h > hTop) continue;
                float u = Mathf.Clamp((hTop - h) / dh, 0f, J - 1 - 1e-4f);
                int j0 = (int)Math.Floor(u), j1 = Math.Min(j0 + 1, J - 1);
                float s = u - j0;
                int k0 = 0, k1 = 0; float t = 0;
                if (N > 1)
                {
                    var a = Angle(q[v]); if (a < 0) a += (float)(2 * Math.PI);
                    var f = a / (float)(2 * Math.PI) * N;
                    k0 = (int)Math.Floor(f) % N; k1 = (k0 + 1) % N; t = f - (float)Math.Floor(f);
                }
                newWeights[v] = UnityReflect.MakeBoneWeight(
                    jointIndex[k0, j0], (1 - t) * (1 - s), jointIndex[k0, j1], (1 - t) * s,
                    jointIndex[k1, j0], t * (1 - s), jointIndex[k1, j1], t * s);
                moved++;
            }

            // Apply to the mesh (through reflection: setters VRoid's build may lack fail with a message).
            Compat.SetBones(smr, newBones.ToArray());
            UnityReflect.SetArray(mesh, "bindposes", newBind.ToArray());
            UnityReflect.SetArray(mesh, "boneWeights", newWeights);
            Features.Body.InvalidateSkin(mesh.GetInstanceID());

            // Drive the chains with a VRM spring bone set up like VRoid's own skirt physics (same colliders).
            var template = modelSprings.FirstOrDefault(c => SpringRootNames(c).IndexOf("Skirt", StringComparison.OrdinalIgnoreCase) >= 0) ?? modelSprings[0];
            var tw = Compat.CastTo(template, springType);
            var holder = new GameObject();
            holder.name = $"VRoidPlus_Spring_{kindName}_{name}";
            holder.transform.SetParent(animator.transform, false);
            built.Created.Add(holder);
            var spring = UnityReflect.AddComponent(holder, springType);
            foreach (var p in new[] { "m_stiffnessForce", "m_gravityPower", "m_gravityDir", "m_dragForce", "m_center", "m_hitRadius", "ColliderGroups", "m_updateType" })
            {
                try { UnityReflect.Set(spring, p, UnityReflect.Get(tw, p)); } catch { }
            }
            var rootsProp = springType.GetProperty("RootBones");
            var list = Activator.CreateInstance(rootsProp!.PropertyType);
            foreach (var rt in roots) ListAccess.Add(list, rt);
            rootsProp.SetValue(spring, list);
            try { UnityReflect.Call(spring, "Setup", true); } catch { }

            Status[name] = $"{N} chain{(N == 1 ? "" : "s")} x {J} joints, {moved} vertices moving";
            return built;
        }

        private static string SpringRootNames(Component spring)
        {
            try
            {
                var w = Compat.CastTo(spring, ReflectionPath.FindType("VRM.VRMSpringBone"));
                var roots = UnityReflect.Get(w, "RootBones");
                var names = new List<string>();
                for (int i = 0; i < ListAccess.Count(roots); i++) if (ListAccess.Get(roots, i) is Transform t && t != null) names.Add(t.name);
                return string.Join(" ", names);
            }
            catch { return ""; }
        }

        private void Undo(Built b)
        {
            try
            {
                if (b.Smr != null && b.OrigBones != null) Compat.SetBones(b.Smr, b.OrigBones);
                if (b.Mesh != null && b.OrigBind != null) UnityReflect.SetArray(b.Mesh, "bindposes", b.OrigBind);
                if (b.Mesh != null && b.OrigWeights != null) UnityReflect.SetArray(b.Mesh, "boneWeights", b.OrigWeights);
                if (b.Mesh != null) Features.Body.InvalidateSkin(b.Mesh.GetInstanceID());
            }
            catch (Exception e) { Plugin.Logger.LogWarning("Cloth physics undo: " + e.Message); }
            foreach (var go in b.Created) if (go != null) UnityEngine.Object.Destroy(go);
            if (b.Smr != null) Status.Remove(b.Smr.gameObject.name);
        }
    }
}
