using System.Collections.Generic;
using UnityEngine;

namespace VRoidPlus
{
    /// <summary>
    /// Bicep / forearm / thigh / calf girth.
    ///
    /// Each vertex is pushed away from (or toward) the axis of the limb bone it is weighted to, scaled by
    /// its skin weight, so joints blend smoothly. This is done on the mesh itself instead of scaling bones,
    /// which avoids the skewing you get from non-uniform bone scale when the limb is posed. It is applied
    /// to every skinned mesh using those bones, so clothes follow the body.
    /// </summary>
    internal class BodySliders
    {
        private struct Segment { public HumanBodyBones Bone, Child; public System.Func<float> Size; }

        private readonly Segment[] _segments =
        {
            new() { Bone = HumanBodyBones.LeftUpperArm, Child = HumanBodyBones.LeftLowerArm, Size = () => Settings.Current.BicepSize },
            new() { Bone = HumanBodyBones.RightUpperArm, Child = HumanBodyBones.RightLowerArm, Size = () => Settings.Current.BicepSize },
            new() { Bone = HumanBodyBones.LeftLowerArm, Child = HumanBodyBones.LeftHand, Size = () => Settings.Current.ForearmSize },
            new() { Bone = HumanBodyBones.RightLowerArm, Child = HumanBodyBones.RightHand, Size = () => Settings.Current.ForearmSize },
            new() { Bone = HumanBodyBones.LeftUpperLeg, Child = HumanBodyBones.LeftLowerLeg, Size = () => Settings.Current.ThighSize },
            new() { Bone = HumanBodyBones.RightUpperLeg, Child = HumanBodyBones.RightLowerLeg, Size = () => Settings.Current.ThighSize },
            new() { Bone = HumanBodyBones.LeftLowerLeg, Child = HumanBodyBones.LeftFoot, Size = () => Settings.Current.CalfSize },
            new() { Bone = HumanBodyBones.RightLowerLeg, Child = HumanBodyBones.RightFoot, Size = () => Settings.Current.CalfSize },
        };

        private class MeshState
        {
            public Mesh Mesh;
            public Vector3[] Original;
            public Vector3[] Written;
            public string Key; // the slider values Written was made with
        }

        private readonly Dictionary<int, MeshState> _states = new();
        private float _nextCheck;
        private bool _dirty = true;

        public void MarkDirty() => _dirty = true;

        /// <summary>What the last pass did, for diagnostics.</summary>
        public string LastInfo = "not run yet";
        public int Overwritten, Applied;

        public bool AllDefault =>
            Mathf.Approximately(Settings.Current.BicepSize, 1) && Mathf.Approximately(Settings.Current.ForearmSize, 1) &&
            Mathf.Approximately(Settings.Current.ThighSize, 1) && Mathf.Approximately(Settings.Current.CalfSize, 1);

        public void Update()
        {
            if (!_dirty && Time.unscaledTime < _nextCheck) return;
            _nextCheck = Time.unscaledTime + 1.5f;
            _dirty = false;
            if (!Avatar.Found) return;
            if (AllDefault && _states.Count == 0) return;

            int seen = 0, unreadable = 0, noMesh = 0;
            var key = $"{Settings.Current.BicepSize:F3}|{Settings.Current.ForearmSize:F3}|{Settings.Current.ThighSize:F3}|{Settings.Current.CalfSize:F3}";
            foreach (var r in Avatar.Renderers())
            {
                var smr = Compat.As<SkinnedMeshRenderer>(r);
                if (smr == null) continue;
                var mesh = smr.sharedMesh;
                seen++;
                if (mesh == null) { noMesh++; continue; }
                if (!mesh.isReadable) { unreadable++; continue; }
                Process(smr, mesh, key);
            }
            LastInfo = $"{Time.unscaledTime:F0}s: {seen} skinned meshes, {unreadable} not readable, {noMesh} without mesh, {_states.Count} tracked, applied {Applied}x, overwritten by VRoid {Overwritten}x, key {key}";
        }

        private void Process(SkinnedMeshRenderer smr, Mesh mesh, string key)
        {
            var bones = smr.bones;
            if (bones == null || bones.Length == 0) return;

            // Which bone slots in this mesh belong to a limb segment?
            var segIndex = new int[bones.Length];
            bool any = false;
            for (int i = 0; i < bones.Length; i++)
            {
                segIndex[i] = -1;
                for (int s = 0; s < _segments.Length; s++)
                    if (bones[i] != null && bones[i] == Avatar.Bone(_segments[s].Bone)) { segIndex[i] = s; any = true; }
            }
            if (!any) return;

            int id = mesh.GetInstanceID();
            var current = Copy.Vertices(mesh);
            if (!_states.TryGetValue(id, out var st) || st.Mesh != mesh || st.Original.Length != current.Length || !SameAs(current, st.Written))
            {
                if (st != null && st.Written != null) Overwritten++; // VRoid replaced what we wrote
                // New mesh, or VRoid regenerated it: what's there now is the new original.
                st = new MeshState { Mesh = mesh, Original = current, Key = "" };
                _states[id] = st;
            }
            if (st.Key == key) return;

            var weights = Copy.BoneWeights(mesh);
            var bind = Copy.BindPoses(mesh);
            if (weights.Length != st.Original.Length || bind.Length != bones.Length) return;

            // Per bone slot: limb axis (bone-local space, from bone origin to child joint) and size factor.
            var axis = new Vector3[bones.Length];
            var factor = new float[bones.Length];
            var bindInv = new Matrix4x4[bones.Length];
            for (int i = 0; i < bones.Length; i++)
            {
                factor[i] = 1f;
                if (segIndex[i] < 0) continue;
                var seg = _segments[segIndex[i]];
                var child = Avatar.Bone(seg.Child);
                if (child == null) continue;
                axis[i] = bones[i].InverseTransformPoint(child.position);
                factor[i] = Mathf.Clamp(seg.Size(), 0.3f, 3f);
                bindInv[i] = bind[i].inverse;
            }

            var result = new Vector3[st.Original.Length];
            for (int v = 0; v < result.Length; v++)
            {
                var p = st.Original[v];
                var w = weights[v];
                var delta = Vector3.zero;
                delta += Push(p, w.boneIndex0, w.weight0, axis, factor, bind, bindInv);
                delta += Push(p, w.boneIndex1, w.weight1, axis, factor, bind, bindInv);
                delta += Push(p, w.boneIndex2, w.weight2, axis, factor, bind, bindInv);
                delta += Push(p, w.boneIndex3, w.weight3, axis, factor, bind, bindInv);
                result[v] = p + delta;
            }

            mesh.vertices = result;
            mesh.RecalculateBounds();
            st.Written = Copy.Vertices(mesh);
            st.Key = key;
            Applied++;
        }

        private static Vector3 Push(Vector3 meshPos, int bi, float w, Vector3[] axis, float[] factor,
            Matrix4x4[] bind, Matrix4x4[] bindInv)
        {
            if (w <= 0f || bi < 0 || bi >= factor.Length || Mathf.Approximately(factor[bi], 1f)) return Vector3.zero;
            var a = axis[bi];
            var len2 = a.sqrMagnitude;
            if (len2 < 1e-8f) return Vector3.zero;
            var local = bind[bi].MultiplyPoint3x4(meshPos);
            var t = Mathf.Clamp01(Vector3.Dot(local, a) / len2);
            var radial = local - a * t;
            var localDelta = radial * (factor[bi] - 1f);
            return bindInv[bi].MultiplyVector(localDelta) * w;
        }

        private static bool SameAs(Vector3[] a, Vector3[] b)
        {
            if (b == null || a.Length != b.Length) return false;
            int step = Mathf.Max(1, b.Length / 64);
            for (int i = 0; i < b.Length; i += step) if (a[i] != b[i]) return false;
            return true;
        }
    }

    /// <summary>Copies Unity arrays into plain managed arrays (IL2CPP returns wrapper arrays).</summary>
    internal static class Copy
    {
        public static Vector3[] Vertices(Mesh m)
        {
            var src = m.vertices;
            var a = new Vector3[src.Length];
            for (int i = 0; i < a.Length; i++) a[i] = src[i];
            return a;
        }

        public static BoneWeight[] BoneWeights(Mesh m)
        {
            var src = m.boneWeights;
            var a = new BoneWeight[src.Length];
            for (int i = 0; i < a.Length; i++) a[i] = src[i];
            return a;
        }

        public static Matrix4x4[] BindPoses(Mesh m)
        {
            var src = m.bindposes;
            var a = new Matrix4x4[src.Length];
            for (int i = 0; i < a.Length; i++) a[i] = src[i];
            return a;
        }
    }
}
