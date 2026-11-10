using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using UnityEngine;

namespace VRoidPlus
{
    /// <summary>
    /// Extra body sliders (bicep, forearm, thigh, calf, plus more under "More sliders").
    ///
    /// Each vertex is moved relative to the limb/torso bone it is weighted to, scaled by its skin weight,
    /// so joints blend smoothly:
    ///   Girth sliders push vertices away from (or toward) the bone's axis.
    ///   Size sliders (hand, foot) scale everything below the joint evenly, fingers and toes included.
    /// It edits the meshes themselves (body and clothes alike), on every copy of the model VRoid keeps
    /// (editor and photo mode). VRoid skins the body to "J_Adj_" helper bones under each main bone, so those
    /// count as part of the bone they hang from.
    ///
    /// Values belong to the open .vroid file and are only stored when VRoid saves that file (see BodyFile).
    /// </summary>
    internal class BodySliders
    {
        public enum Mode { Girth, Size }

        public class SliderDef
        {
            public string Id, Label;
            public bool Extra;               // shown under "More sliders"
            public Mode Mode;
            public (HumanBodyBones bone, HumanBodyBones toward)[] Segments;
        }

        public static readonly SliderDef[] Sliders =
        {
            Def("bicep", "Bicep Size", false, Mode.Girth, (HumanBodyBones.LeftUpperArm, HumanBodyBones.LeftLowerArm), (HumanBodyBones.RightUpperArm, HumanBodyBones.RightLowerArm)),
            Def("forearm", "Forearm Size", false, Mode.Girth, (HumanBodyBones.LeftLowerArm, HumanBodyBones.LeftHand), (HumanBodyBones.RightLowerArm, HumanBodyBones.RightHand)),
            Def("thigh", "Thigh Size", false, Mode.Girth, (HumanBodyBones.LeftUpperLeg, HumanBodyBones.LeftLowerLeg), (HumanBodyBones.RightUpperLeg, HumanBodyBones.RightLowerLeg)),
            Def("calf", "Calf Size", false, Mode.Girth, (HumanBodyBones.LeftLowerLeg, HumanBodyBones.LeftFoot), (HumanBodyBones.RightLowerLeg, HumanBodyBones.RightFoot)),

            Def("neck", "Neck Size", true, Mode.Girth, (HumanBodyBones.Neck, HumanBodyBones.Head)),
            Def("shoulder", "Shoulder Size", true, Mode.Girth, (HumanBodyBones.LeftShoulder, HumanBodyBones.LeftUpperArm), (HumanBodyBones.RightShoulder, HumanBodyBones.RightUpperArm)),
            Def("upperchest", "Upper Chest Size", true, Mode.Girth, (HumanBodyBones.UpperChest, HumanBodyBones.Neck)),
            Def("chest", "Chest Size", true, Mode.Girth, (HumanBodyBones.Chest, HumanBodyBones.UpperChest)),
            Def("waist", "Waist Size", true, Mode.Girth, (HumanBodyBones.Spine, HumanBodyBones.Chest)),
            Def("hips", "Hip Size", true, Mode.Girth, (HumanBodyBones.Hips, HumanBodyBones.Spine)),
            Def("hand", "Hand Size", true, Mode.Size, (HumanBodyBones.LeftHand, HumanBodyBones.LeftMiddleProximal), (HumanBodyBones.RightHand, HumanBodyBones.RightMiddleProximal)),
            Def("foot", "Foot Size", true, Mode.Size, (HumanBodyBones.LeftFoot, HumanBodyBones.LeftToes), (HumanBodyBones.RightFoot, HumanBodyBones.RightToes)),
        };

        private static SliderDef Def(string id, string label, bool extra, Mode mode, params (HumanBodyBones, HumanBodyBones)[] segs) =>
            new() { Id = id, Label = label, Extra = extra, Mode = mode, Segments = segs };

        public readonly BodyFile File = new();

        public float Get(string id) => File.Values.TryGetValue(id, out var v) ? v : 1f;

        public void Set(string id, float v)
        {
            if (Mathf.Approximately(v, 1f)) File.Values.Remove(id); else File.Values[id] = v;
            _dirty = true;
        }

        public void ResetAll() { File.Values.Clear(); _dirty = true; }

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

        public bool AllDefault => File.Values.Count == 0;

        private string Key() => string.Join("|", Sliders.Select(s => Get(s.Id).ToString("F3")));

        public void Update()
        {
            if (File.Update()) _dirty = true;   // another .vroid file was opened
            if (!_dirty && Time.unscaledTime < _nextCheck) return;
            _nextCheck = Time.unscaledTime + 1.5f;
            _dirty = false;
            if (!Avatar.Found) return;
            if (AllDefault && _states.Count == 0) return;

            int seen = 0, unreadable = 0, noMesh = 0, models = 0;
            var key = Key();
            // Every copy of the avatar (editor model, photo mode models), each with its own skeleton.
            foreach (var animator in Avatar.AllHumanoids())
            {
                if (animator == null) continue;
                models++;
                var rig = new Rig(animator);
                foreach (var smr in animator.GetComponentsInChildren<SkinnedMeshRenderer>(false))
                {
                    if (smr == null) continue;
                    var mesh = smr.sharedMesh;
                    seen++;
                    if (mesh == null) { noMesh++; continue; }
                    if (!mesh.isReadable) { unreadable++; continue; }
                    Process(smr, mesh, key, rig);
                }
            }
            LastInfo = $"{Time.unscaledTime:F0}s: {models} models, {seen} skinned meshes, {unreadable} not readable, {noMesh} without mesh, {_states.Count} tracked, applied {Applied}x, overwritten by VRoid {Overwritten}x, key {key}, file {File.Path ?? "(unsaved)"}";
        }

        /// <summary>One model's skeleton: its humanoid bones and helpers to classify other bones.</summary>
        private class Rig
        {
            private readonly Dictionary<HumanBodyBones, Transform> _bones = new();
            private readonly HashSet<int> _human = new();

            public Rig(Animator a)
            {
                for (var b = HumanBodyBones.Hips; b < HumanBodyBones.LastBone; b++)
                {
                    var t = a.GetBoneTransform(b);
                    if (t == null) continue;
                    _bones[b] = t; _human.Add(t.GetInstanceID());
                }
            }

            public Transform Bone(HumanBodyBones b) => _bones.TryGetValue(b, out var t) ? t : null;
            public bool IsHumanoid(Transform t) => t != null && _human.Contains(t.GetInstanceID());

            public Transform HumanoidAncestor(Transform t)
            {
                for (var p = t; p != null; p = p.parent) if (_human.Contains(p.GetInstanceID())) return p;
                return null;
            }
        }

        /// <summary>A slider's bone pair resolved on one model.</summary>
        private struct Seg { public Transform Bone, Toward; public Mode Mode; public float Factor; }

        private void Process(SkinnedMeshRenderer smr, Mesh mesh, string key, Rig rig)
        {
            var bones = smr.bones;
            if (bones == null || bones.Length == 0) return;

            // Resolve every active slider's segments on this model.
            var segs = new List<Seg>();
            foreach (var def in Sliders)
            {
                var f = Mathf.Clamp(Get(def.Id), 0.3f, 3f);
                foreach (var (bone, toward) in def.Segments)
                {
                    var b = rig.Bone(bone); var t = rig.Bone(toward);
                    if (b != null && t != null) segs.Add(new Seg { Bone = b, Toward = t, Mode = def.Mode, Factor = f });
                }
            }

            // Which segment does each bone slot of this mesh belong to?
            //  Girth: the main bone itself, or a J_Adj_ helper whose nearest main bone is it.
            //  Size:  the main bone or anything below it (fingers, toes, items attached there).
            var segIndex = new int[bones.Length];
            bool any = false;
            for (int i = 0; i < bones.Length; i++)
            {
                segIndex[i] = -1;
                var b = bones[i];
                if (b == null) continue;
                for (int s = 0; s < segs.Count && segIndex[i] < 0; s++)
                {
                    if (segs[s].Mode == Mode.Size)
                    {
                        if (b == segs[s].Bone || b.IsChildOf(segs[s].Bone)) segIndex[i] = s;
                    }
                    else
                    {
                        var owner = rig.IsHumanoid(b) ? b : (b.name.StartsWith("J_Adj_", StringComparison.Ordinal) ? rig.HumanoidAncestor(b) : null);
                        if (owner != null && owner == segs[s].Bone) segIndex[i] = s;
                    }
                }
                if (segIndex[i] >= 0) any = true;
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

            // Per bone slot: the segment's start joint and axis in that bone's local space, mode and factor.
            var axisStart = new Vector3[bones.Length];
            var axis = new Vector3[bones.Length];
            var factor = new float[bones.Length];
            var size = new bool[bones.Length];
            var bindInv = new Matrix4x4[bones.Length];
            for (int i = 0; i < bones.Length; i++)
            {
                factor[i] = 1f;
                if (segIndex[i] < 0) continue;
                var seg = segs[segIndex[i]];
                axisStart[i] = bones[i].InverseTransformPoint(seg.Bone.position);
                axis[i] = bones[i].InverseTransformPoint(seg.Toward.position) - axisStart[i];
                factor[i] = seg.Factor;
                size[i] = seg.Mode == Mode.Size;
                bindInv[i] = bind[i].inverse;
            }

            var result = new Vector3[st.Original.Length];
            for (int v = 0; v < result.Length; v++)
            {
                var p = st.Original[v];
                var w = weights[v];
                var delta = Vector3.zero;
                delta += Push(p, w.boneIndex0, w.weight0, axisStart, axis, factor, size, bind, bindInv);
                delta += Push(p, w.boneIndex1, w.weight1, axisStart, axis, factor, size, bind, bindInv);
                delta += Push(p, w.boneIndex2, w.weight2, axisStart, axis, factor, size, bind, bindInv);
                delta += Push(p, w.boneIndex3, w.weight3, axisStart, axis, factor, size, bind, bindInv);
                result[v] = p + delta;
            }

            mesh.vertices = result;
            mesh.RecalculateBounds();
            st.Written = Copy.Vertices(mesh);
            st.Key = key;
            Applied++;
        }

        private static Vector3 Push(Vector3 meshPos, int bi, float w, Vector3[] axisStart, Vector3[] axis, float[] factor, bool[] size,
            Matrix4x4[] bind, Matrix4x4[] bindInv)
        {
            if (w <= 0f || bi < 0 || bi >= factor.Length || Mathf.Approximately(factor[bi], 1f)) return Vector3.zero;
            var local = bind[bi].MultiplyPoint3x4(meshPos) - axisStart[bi];
            Vector3 localDelta;
            if (size[bi])
            {
                localDelta = local * (factor[bi] - 1f);                 // scale evenly around the joint
            }
            else
            {
                var a = axis[bi];
                var len2 = a.sqrMagnitude;
                if (len2 < 1e-8f) return Vector3.zero;
                var t = Mathf.Clamp01(Vector3.Dot(local, a) / len2);
                var radial = local - a * t;
                localDelta = radial * (factor[bi] - 1f);                // push away from the bone's axis
            }
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

    /// <summary>
    /// Body slider values for the open .vroid file. They're kept in memory while you edit and written to
    /// BepInEx/config/VRoidPlus/models/ only when VRoid saves the file (its timestamp changes, or Save As
    /// gives it a new path). Opening a file loads its values; a new, never-saved model starts at 100%.
    /// </summary>
    internal class BodyFile
    {
        public Dictionary<string, float> Values = new();
        private Dictionary<string, float> _saved = new();
        public string Path { get; private set; }
        private object _engine;
        private DateTime _mtime;
        private float _nextPoll;

        public bool HasUnsavedChanges =>
            Values.Count != _saved.Count || Values.Any(kv => !_saved.TryGetValue(kv.Key, out var s) || !Mathf.Approximately(s, kv.Value));

        public class FileData
        {
            public string Path = "";
            public string SavedAt = "";
            public Dictionary<string, float> Values = new();
        }

        private static string StorePath(string vroidPath)
        {
            using var sha = SHA1.Create();
            var hash = sha.ComputeHash(Encoding.UTF8.GetBytes(System.IO.Path.GetFullPath(vroidPath).ToLowerInvariant()));
            var name = string.Concat(hash.Take(10).Select(b => b.ToString("x2")));
            return System.IO.Path.Combine(Plugin.DataDir, "models", name + ".json");
        }

        private static DateTime MTime(string p)
        {
            try { return !string.IsNullOrEmpty(p) && System.IO.File.Exists(p) ? System.IO.File.GetLastWriteTimeUtc(p) : DateTime.MinValue; }
            catch { return DateTime.MinValue; }
        }

        /// <summary>Polls VRoid once a second. Returns true when a different model was opened (values replaced).</summary>
        public bool Update()
        {
            if (Time.unscaledTime < _nextPoll) return false;
            _nextPoll = Time.unscaledTime + 1f;

            var model = VRoidApi.FromViewModel("CurrentFile.model", out _);
            var engine = VRoidApi.Get(model, "engine");
            if (engine == null) return false;
            var engineId = Compat.Identity(engine);
            var path = VRoidApi.Get(model, "path") as string;
            if (string.IsNullOrEmpty(path)) path = null;

            if (!Equals(engineId, _engine))
            {
                // A model was opened or created: load what was saved with it.
                bool firstModel = _engine == null;
                _engine = engineId;
                var previousPath = Path;
                Path = path;
                _mtime = MTime(path);
                bool hasStored = path != null && System.IO.File.Exists(StorePath(path));
                bool justWritten = _mtime != DateTime.MinValue && (DateTime.UtcNow - _mtime).TotalSeconds < 15;
                if (!firstModel && path != null && path != previousPath && !hasStored && justWritten && Values.Count > 0)
                {
                    // Looks like "Save As" of the model being edited: keep the current values with the new file.
                    Persist();
                    return false;
                }
                Values = new Dictionary<string, float>();
                if (hasStored)
                {
                    var data = Json.Read<FileData>(StorePath(path));
                    foreach (var kv in data.Values) Values[kv.Key] = kv.Value;
                }
                _saved = new Dictionary<string, float>(Values);
                return true;
            }

            if (path != Path && path != null)
            {
                // Save As: the same model now lives in a new file.
                Path = path;
                _mtime = MTime(path);
                Persist();
            }
            else if (path != null)
            {
                var m = MTime(path);
                if (m > _mtime) { _mtime = m; Persist(); }   // VRoid wrote the file: the user saved
            }
            return false;
        }

        private void Persist()
        {
            if (Path == null) return;
            var file = StorePath(Path);
            if (Values.Count == 0 && !System.IO.File.Exists(file)) { _saved = new Dictionary<string, float>(); return; }
            Json.Write(file, new FileData { Path = Path, SavedAt = DateTime.Now.ToString("s"), Values = new Dictionary<string, float>(Values) });
            _saved = new Dictionary<string, float>(Values);
            Plugin.Logger.LogInfo($"Body sliders saved with {System.IO.Path.GetFileName(Path)}");
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
