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
    /// Values belong to the open .vroid file and are only stored when VRoid saves that file (see ModelFile).
    /// </summary>
    internal class BodySliders
    {
        public enum Mode { Girth, Size, Rear, Width }

        public class SliderDef
        {
            public string Id, Label, Group;
            public bool Extra;               // shown under "More sliders"
            public Mode Mode;
            public (HumanBodyBones bone, HumanBodyBones toward)[] Segments;
        }

        private static (HumanBodyBones, HumanBodyBones)[] Both(HumanBodyBones l, HumanBodyBones lt, HumanBodyBones r, HumanBodyBones rt) => new[] { (l, lt), (r, rt) };

        private static (HumanBodyBones, HumanBodyBones)[] Arms() => Both(HumanBodyBones.LeftUpperArm, HumanBodyBones.LeftLowerArm, HumanBodyBones.RightUpperArm, HumanBodyBones.RightLowerArm)
            .Concat(Both(HumanBodyBones.LeftLowerArm, HumanBodyBones.LeftHand, HumanBodyBones.RightLowerArm, HumanBodyBones.RightHand)).ToArray();

        private static (HumanBodyBones, HumanBodyBones)[] Legs() => Both(HumanBodyBones.LeftUpperLeg, HumanBodyBones.LeftLowerLeg, HumanBodyBones.RightUpperLeg, HumanBodyBones.RightLowerLeg)
            .Concat(Both(HumanBodyBones.LeftLowerLeg, HumanBodyBones.LeftFoot, HumanBodyBones.RightLowerLeg, HumanBodyBones.RightFoot)).ToArray();

        private static (HumanBodyBones, HumanBodyBones)[] Torso() => new[]
        {
            (HumanBodyBones.Hips, HumanBodyBones.Spine), (HumanBodyBones.Spine, HumanBodyBones.Chest),
            (HumanBodyBones.Chest, HumanBodyBones.UpperChest), (HumanBodyBones.UpperChest, HumanBodyBones.Neck),
        };

        /// <summary>Each finger bone's girth, measured along the finger (the last joint points back to the one before).</summary>
        private static (HumanBodyBones, HumanBodyBones)[] Fingers(bool thumb)
        {
            var list = new List<(HumanBodyBones, HumanBodyBones)>();
            foreach (var first in new[] { HumanBodyBones.LeftThumbProximal, HumanBodyBones.RightThumbProximal })
            {
                // Left hand bones run LeftThumbProximal..LeftLittleDistal (15), right hand the same from RightThumbProximal.
                for (int f = thumb ? 0 : 1; f < (thumb ? 1 : 5); f++)
                {
                    var prox = (HumanBodyBones)((int)first + f * 3);
                    var mid = prox + 1; var dist = prox + 2;
                    list.Add((prox, mid)); list.Add((mid, dist)); list.Add((dist, mid));
                }
            }
            return list.ToArray();
        }

        public static readonly SliderDef[] Sliders =
        {
            Def("bicep", "Bicep Size", "", false, Mode.Girth, Both(HumanBodyBones.LeftUpperArm, HumanBodyBones.LeftLowerArm, HumanBodyBones.RightUpperArm, HumanBodyBones.RightLowerArm)),
            Def("forearm", "Forearm Size", "", false, Mode.Girth, Both(HumanBodyBones.LeftLowerArm, HumanBodyBones.LeftHand, HumanBodyBones.RightLowerArm, HumanBodyBones.RightHand)),
            Def("thigh", "Thigh Size", "", false, Mode.Girth, Both(HumanBodyBones.LeftUpperLeg, HumanBodyBones.LeftLowerLeg, HumanBodyBones.RightUpperLeg, HumanBodyBones.RightLowerLeg)),
            Def("calf", "Calf Size", "", false, Mode.Girth, Both(HumanBodyBones.LeftLowerLeg, HumanBodyBones.LeftFoot, HumanBodyBones.RightLowerLeg, HumanBodyBones.RightFoot)),

            Def("head", "Head Size", "Head & neck", true, Mode.Size, (HumanBodyBones.Head, HumanBodyBones.Neck)),
            Def("neck", "Neck Thickness", "Head & neck", true, Mode.Girth, (HumanBodyBones.Neck, HumanBodyBones.Head)),

            Def("shoulder", "Shoulder Size", "Torso", true, Mode.Girth, Both(HumanBodyBones.LeftShoulder, HumanBodyBones.LeftUpperArm, HumanBodyBones.RightShoulder, HumanBodyBones.RightUpperArm)),
            Def("upperchest", "Upper Chest Size", "Torso", true, Mode.Girth, (HumanBodyBones.UpperChest, HumanBodyBones.Neck)),
            Def("chest", "Chest Size", "Torso", true, Mode.Girth, (HumanBodyBones.Chest, HumanBodyBones.UpperChest)),
            Def("waist", "Waist Size", "Torso", true, Mode.Girth, (HumanBodyBones.Spine, HumanBodyBones.Chest)),
            Def("hips", "Hip Size", "Torso", true, Mode.Girth, (HumanBodyBones.Hips, HumanBodyBones.Spine)),
            Def("hipwidth", "Hip Width", "Torso", true, Mode.Width, (HumanBodyBones.Hips, HumanBodyBones.Spine),
                (HumanBodyBones.LeftUpperLeg, HumanBodyBones.LeftLowerLeg), (HumanBodyBones.RightUpperLeg, HumanBodyBones.RightLowerLeg)),
            Def("butt", "Butt Size", "Torso", true, Mode.Rear, (HumanBodyBones.Hips, HumanBodyBones.Spine),
                (HumanBodyBones.LeftUpperLeg, HumanBodyBones.LeftLowerLeg), (HumanBodyBones.RightUpperLeg, HumanBodyBones.RightLowerLeg)),
            Def("torso", "Whole Torso Thickness", "Torso", true, Mode.Girth, Torso()),

            Def("arm", "Whole Arm Thickness", "Arms & hands", true, Mode.Girth, Arms()),
            Def("hand", "Hand Size", "Arms & hands", true, Mode.Size, Both(HumanBodyBones.LeftHand, HumanBodyBones.LeftMiddleProximal, HumanBodyBones.RightHand, HumanBodyBones.RightMiddleProximal)),
            Def("handthick", "Hand Thickness", "Arms & hands", true, Mode.Girth, Both(HumanBodyBones.LeftHand, HumanBodyBones.LeftMiddleProximal, HumanBodyBones.RightHand, HumanBodyBones.RightMiddleProximal)),
            Def("finger", "Finger Thickness", "Arms & hands", true, Mode.Girth, Fingers(false)),
            Def("thumb", "Thumb Thickness", "Arms & hands", true, Mode.Girth, Fingers(true)),

            Def("leg", "Whole Leg Thickness", "Legs & feet", true, Mode.Girth, Legs()),
            Def("foot", "Foot Size", "Legs & feet", true, Mode.Size, Both(HumanBodyBones.LeftFoot, HumanBodyBones.LeftToes, HumanBodyBones.RightFoot, HumanBodyBones.RightToes)),
            Def("footwidth", "Foot Width", "Legs & feet", true, Mode.Girth, Both(HumanBodyBones.LeftFoot, HumanBodyBones.LeftToes, HumanBodyBones.RightFoot, HumanBodyBones.RightToes)),
            Def("toe", "Toe Size", "Legs & feet", true, Mode.Size, Both(HumanBodyBones.LeftToes, HumanBodyBones.LeftFoot, HumanBodyBones.RightToes, HumanBodyBones.RightFoot)),

            Def("overall", "Overall Thickness", "Overall", true, Mode.Girth, Torso().Concat(Arms()).Concat(Legs())
                .Concat(new[] { (HumanBodyBones.Neck, HumanBodyBones.Head) })
                .Concat(Both(HumanBodyBones.LeftShoulder, HumanBodyBones.LeftUpperArm, HumanBodyBones.RightShoulder, HumanBodyBones.RightUpperArm)).ToArray()),
        };

        private static SliderDef Def(string id, string label, string group, bool extra, Mode mode, params (HumanBodyBones, HumanBodyBones)[] segs) =>
            new() { Id = id, Label = label, Group = group, Extra = extra, Mode = mode, Segments = segs };

        public ModelFile File => ModelFile.Current;

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
            public BoneWeight[] Weights;   // cached: read once per original mesh
            public Matrix4x4[] Bind;
            public readonly Queue<float> Overwrites = new();
            public float BackoffUntil;
        }

        private readonly Dictionary<int, MeshState> _states = new();
        /// <summary>
        /// Fingerprint of every reshaped vertex set we wrote -> the un-reshaped original it was made from and
        /// the slider values used. VRoid sometimes makes a new mesh that is a copy of one we reshaped (while
        /// rebuilding, exporting or uploading); such a copy takes over that original instead of being reshaped
        /// a second time, and still follows later slider changes.
        /// </summary>
        private readonly Dictionary<int, (Vector3[] original, string key)> _writtenPrints = new();
        public int CopiesSkipped;

        private static int Print(Vector3[] v)
        {
            var h = new HashCode();
            h.Add(v.Length);
            int step = Math.Max(1, v.Length / 97);
            for (int i = 0; i < v.Length; i += step) { h.Add(v[i].x); h.Add(v[i].y); h.Add(v[i].z); }
            return h.ToHashCode();
        }

        /// <summary>
        /// The model copies VRoid shows you: the editor model and photo mode's model. Everything else VRoid
        /// builds (export/upload preview, mesh reduction) is made from the editor's meshes, which already carry
        /// the slider changes, so reshaping those again would double the effect in exports.
        /// </summary>
        private static bool IsShownModel(Animator a)
        {
            var n = a.gameObject.name;
            return n.StartsWith("CurrentModelOrigin", StringComparison.Ordinal) || n == "PhotoBoothModel";
        }
        private float _nextCheck;
        private bool _dirty = true;

        public void MarkDirty() => _dirty = true;

        /// <summary>What the last pass did, for diagnostics.</summary>
        public string LastInfo = "not run yet";
        public int Overwritten, Applied;

        public bool AllDefault => File.Values.Count == 0;

        private string Key() => string.Join("|", Sliders.Select(s => Get(s.Id).ToString("F3")));

        /// <summary>smr instance id -> mesh instance id last seen (VRoid swapping a mesh shows up here at once)</summary>
        private readonly Dictionary<int, int> _meshOf = new();
        /// <summary>"meshId|key" of meshes no slider touches, so they're skipped without copying vertices</summary>
        private readonly HashSet<string> _unaffected = new();
        private string _lastKey = "";

        /// <summary>
        /// Called every frame from LateUpdate (after VRoid's updates, before drawing).
        ///  - Every frame: meshes VRoid just replaced are reshaped immediately.
        ///  - fast (shortly after VRoid edited the model) or every 1.5 s: every mesh is checked, which also
        ///    catches meshes VRoid rewrote in place.
        /// </summary>
        /// <summary>Most time body sliders may use per frame; the rest of a pass continues next frame.</summary>
        private const double FrameBudgetMs = 4.0;
        private readonly HashSet<int> _passDone = new();
        private bool _passActive;
        private float _nextFastPass, _nextSwapScan;
        /// <summary>smr instance id -> when it was last reshaped because VRoid swapped its mesh</summary>
        private readonly Dictionary<int, float> _lastSwapFix = new();
        private readonly System.Diagnostics.Stopwatch _clock = new();
        public int BudgetHits, Backoffs;

        public void Tick(bool fast)
        {
            var now = Time.unscaledTime;
            // A full pass checks every mesh (also catches meshes VRoid rewrote in place): on slider change,
            // every 1.5 s, and 4x a second shortly after VRoid edited the model. It may span several frames.
            if (_dirty || now >= _nextCheck || (fast && now >= _nextFastPass))
            {
                _nextCheck = now + 1.5f;
                _nextFastPass = now + 0.25f;
                _dirty = false;
                _passActive = true;
                _passDone.Clear();
            }
            if (!Avatar.Found) return;
            if (AllDefault && _states.Count == 0) { _passActive = false; return; }
            // Between passes, looking for swapped meshes 10x a second is enough (it walks every mesh).
            if (!_passActive && now < _nextSwapScan) return;
            _nextSwapScan = now + 0.1f;

            _clock.Restart();
            bool outOfTime = false;
            int seen = 0, unreadable = 0, noMesh = 0, models = 0;
            var key = Key();
            if (key != _lastKey) { _unaffected.Clear(); _lastKey = key; }
            var humanoids = Avatar.AllHumanoids();
            bool anyKnown = humanoids.Any(a => a != null && IsShownModel(a));
            foreach (var animator in humanoids)
            {
                if (animator == null) continue;
                // Other VRoid versions may name models differently: then fall back to the chosen avatar only.
                if (anyKnown ? !IsShownModel(animator) : animator != Avatar.Animator) continue;
                models++;
                Rig rig = null;
                foreach (var smr in animator.GetComponentsInChildren<SkinnedMeshRenderer>(false))
                {
                    if (smr == null) continue;
                    var mesh = smr.sharedMesh;
                    seen++;
                    if (mesh == null) { noMesh++; continue; }
                    var smrId = smr.GetInstanceID();
                    var meshId = mesh.GetInstanceID();
                    bool swapped = !_meshOf.TryGetValue(smrId, out var last) || last != meshId;
                    _meshOf[smrId] = meshId;

                    bool inPass = _passActive && !_passDone.Contains(smrId);
                    // A swapped mesh is fixed at once, but at most twice a second per mesh (photo mode can
                    // replace meshes continuously; reshaping them every frame would lag).
                    bool fixSwap = swapped && (!_lastSwapFix.TryGetValue(smrId, out var lastFix) || now - lastFix >= 0.5f);
                    if (!inPass && !fixSwap) continue;
                    if (outOfTime) continue;
                    if (_passActive) _passDone.Add(smrId);
                    if (fixSwap) _lastSwapFix[smrId] = now;

                    if (_unaffected.Contains(meshId + "|" + key)) continue;
                    if (!mesh.isReadable) { unreadable++; continue; }
                    rig ??= new Rig(animator);
                    Process(smr, mesh, key, rig);

                    if (_clock.Elapsed.TotalMilliseconds > FrameBudgetMs) { outOfTime = true; BudgetHits++; }
                }
            }
            if (!outOfTime) _passActive = false;   // pass finished this frame
            if (!outOfTime && !_passActive)
                LastInfo = $"{now:F0}s: {models} models, {seen} skinned meshes, {unreadable} not readable, {noMesh} without mesh, {_states.Count} tracked, applied {Applied}x, overwritten by VRoid {Overwritten}x, frame budget hit {BudgetHits}x, backed off {Backoffs}x, copies of reshaped meshes recognised {CopiesSkipped}x, key {key}, file {File.Path ?? "(unsaved)"}";
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

            /// <summary>The model's left-to-right direction (world space).</summary>
            public Vector3 Right
            {
                get
                {
                    var l = Bone(HumanBodyBones.LeftUpperLeg); var r = Bone(HumanBodyBones.RightUpperLeg);
                    var d = l != null && r != null ? r.position - l.position : Vector3.right;
                    return d.sqrMagnitude > 1e-10f ? d.normalized : Vector3.right;
                }
            }

            /// <summary>The model's backward direction (world space), from its own skeleton so facing doesn't matter.</summary>
            public Vector3 Back
            {
                get
                {
                    var l = Bone(HumanBodyBones.LeftUpperLeg); var r = Bone(HumanBodyBones.RightUpperLeg);
                    var hips = Bone(HumanBodyBones.Hips); var spine = Bone(HumanBodyBones.Spine);
                    if (l == null || r == null || hips == null || spine == null) return Vector3.back;
                    var forward = Vector3.Cross(r.position - l.position, spine.position - hips.position);
                    return forward.sqrMagnitude > 1e-10f ? -forward.normalized : Vector3.back;
                }
            }

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

            // Already reshaped with these values? Then only check whether VRoid overwrote it.
            int meshKey = mesh.GetInstanceID();
            if (_states.TryGetValue(meshKey, out var known) && known.Mesh == mesh)
            {
                if (Time.unscaledTime < known.BackoffUntil) return;
                if (known.Key == key)
                {
                    var now0 = Copy.Vertices(mesh);
                    if (SameAs(now0, known.Written)) return;
                }
            }

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

            // Which segments apply to each bone slot of this mesh? Several sliders can act on the same bone
            // (e.g. Bicep and Whole Arm Thickness); their factors multiply.
            //  Girth: the main bone itself, or a J_Adj_ helper whose nearest main bone is it.
            //  Size:  the main bone or anything below it (fingers, toes, items attached there).
            var girthSeg = new int[bones.Length];
            var sizeSeg = new int[bones.Length];
            var rearSeg = new int[bones.Length];
            var widthSeg = new int[bones.Length];
            var widthF = new float[bones.Length];
            var girthF = new float[bones.Length];
            var sizeF = new float[bones.Length];
            var rearF = new float[bones.Length];
            bool any = false;
            for (int i = 0; i < bones.Length; i++)
            {
                girthSeg[i] = sizeSeg[i] = rearSeg[i] = widthSeg[i] = -1;
                girthF[i] = sizeF[i] = rearF[i] = widthF[i] = 1f;
                var b = bones[i];
                if (b == null) continue;
                var owner = rig.IsHumanoid(b) ? b : (b.name.StartsWith("J_Adj_", StringComparison.Ordinal) ? rig.HumanoidAncestor(b) : null);
                for (int s = 0; s < segs.Count; s++)
                {
                    var seg = segs[s];
                    if (seg.Mode == Mode.Size)
                    {
                        if (b != seg.Bone && !b.IsChildOf(seg.Bone)) continue;
                        // nearest enclosing size segment defines the pivot (e.g. toes inside the foot)
                        if (sizeSeg[i] < 0 || seg.Bone.IsChildOf(segs[sizeSeg[i]].Bone)) sizeSeg[i] = s;
                        sizeF[i] *= seg.Factor;
                    }
                    else if (owner != null && owner == seg.Bone)
                    {
                        if (seg.Mode == Mode.Width)
                        {
                            if (widthSeg[i] < 0) widthSeg[i] = s;
                            widthF[i] *= seg.Factor;
                        }
                        else if (seg.Mode == Mode.Rear)
                        {
                            if (rearSeg[i] < 0) rearSeg[i] = s;
                            rearF[i] *= seg.Factor;
                        }
                        else
                        {
                            if (girthSeg[i] < 0) girthSeg[i] = s;
                            girthF[i] *= seg.Factor;
                        }
                    }
                }
                if (!Mathf.Approximately(girthF[i], 1f) || !Mathf.Approximately(sizeF[i], 1f) || !Mathf.Approximately(rearF[i], 1f) || !Mathf.Approximately(widthF[i], 1f)) any = true;
            }
            if (!any && !_states.ContainsKey(mesh.GetInstanceID())) { _unaffected.Add(mesh.GetInstanceID() + "|" + key); return; }

            int id = mesh.GetInstanceID();
            var current = Copy.Vertices(mesh);
            if (!_states.TryGetValue(id, out var st) || st.Mesh != mesh || st.Original.Length != current.Length || !SameAs(current, st.Written))
            {
                var overwrites = st?.Overwrites;
                if (st != null && st.Written != null)
                {
                    Overwritten++; // VRoid replaced what we wrote
                    // If VRoid keeps rewriting this mesh (e.g. in photo mode), don't fight it every frame.
                    var now = Time.unscaledTime;
                    overwrites.Enqueue(now);
                    while (overwrites.Count > 0 && now - overwrites.Peek() > 3f) overwrites.Dequeue();
                }
                // New mesh, or VRoid regenerated it: what's there now is the new original - unless it is an
                // exact copy of something we wrote (VRoid duplicating an already reshaped mesh).
                var fresh = new MeshState { Mesh = mesh, Original = current, Key = "" };
                if (_writtenPrints.TryGetValue(Print(current), out var source) && source.original.Length == current.Length)
                {
                    fresh.Original = source.original;
                    fresh.Written = current;
                    fresh.Key = source.key;
                    CopiesSkipped++;
                }
                if (overwrites != null)
                {
                    foreach (var t in overwrites) fresh.Overwrites.Enqueue(t);
                    if (fresh.Overwrites.Count >= 3) { fresh.BackoffUntil = Time.unscaledTime + 0.5f; Backoffs++; }
                }
                st = fresh;
                _states[id] = st;
            }
            if (st.Key == key) return;

            st.Weights ??= Copy.BoneWeights(mesh);
            st.Bind ??= Copy.BindPoses(mesh);
            var weights = st.Weights;
            var bind = st.Bind;
            if (weights.Length != st.Original.Length || bind.Length != bones.Length) return;

            // Per bone slot, in that bone's local space: the girth axis (start joint and direction), the size
            // pivot and the rear (butt) axis with the model's backward direction, plus their factors.
            var d = new Slots(bones.Length) { GirthF = girthF, SizeF = sizeF, RearF = rearF, WidthF = widthF, Bind = bind };
            var back = rig.Back;
            var right = rig.Right;
            var hipsBone = rig.Bone(HumanBodyBones.Hips);
            for (int i = 0; i < bones.Length; i++)
            {
                if (Mathf.Approximately(girthF[i], 1f) && Mathf.Approximately(sizeF[i], 1f) && Mathf.Approximately(rearF[i], 1f) && Mathf.Approximately(widthF[i], 1f)) continue;
                d.Active[i] = true;
                d.BindInv[i] = bind[i].inverse;
                if (girthSeg[i] >= 0)
                {
                    var g = segs[girthSeg[i]];
                    d.GirthStart[i] = bones[i].InverseTransformPoint(g.Bone.position);
                    d.GirthAxis[i] = bones[i].InverseTransformPoint(g.Toward.position) - d.GirthStart[i];
                }
                if (sizeSeg[i] >= 0) d.SizePivot[i] = bones[i].InverseTransformPoint(segs[sizeSeg[i]].Bone.position);
                if (widthSeg[i] >= 0 && hipsBone != null)
                {
                    var wseg = segs[widthSeg[i]];
                    d.WidthRight[i] = bones[i].InverseTransformDirection(right).normalized;
                    if (wseg.Bone == hipsBone)
                    {
                        // Pelvis: spread sideways from the body's centre line.
                        d.WidthCenter[i] = bones[i].InverseTransformPoint(hipsBone.position);
                    }
                    else
                    {
                        // Thigh: move the top of the thigh out with the hip joint, without scaling it, fading out
                        // halfway to the knee, so the thigh keeps its size.
                        var lateral = Vector3.Project(wseg.Bone.position - hipsBone.position, right);
                        d.WidthShift[i] = bones[i].InverseTransformVector(lateral);
                        d.WidthStart[i] = bones[i].InverseTransformPoint(wseg.Bone.position);
                        d.WidthAxis[i] = bones[i].InverseTransformPoint(wseg.Toward.position) - d.WidthStart[i];
                        d.WidthIsLeg[i] = true;
                    }
                }
                if (rearSeg[i] >= 0)
                {
                    var r = segs[rearSeg[i]];
                    d.RearStart[i] = bones[i].InverseTransformPoint(r.Bone.position);
                    d.RearAxis[i] = bones[i].InverseTransformPoint(r.Toward.position) - d.RearStart[i];
                    d.RearBack[i] = bones[i].InverseTransformDirection(back).normalized;
                    // On the thighs only the top part belongs to the buttocks; fade out 40% of the way to the knee.
                    d.RearFade[i] = rig.IsHumanoid(r.Bone) && r.Bone == rig.Bone(HumanBodyBones.Hips) ? 0f : 0.4f;
                }
            }

            var result = new Vector3[st.Original.Length];
            for (int v = 0; v < result.Length; v++)
            {
                var p = st.Original[v];
                var w = weights[v];
                var delta = Vector3.zero;
                delta += Push(p, w.boneIndex0, w.weight0, d);
                delta += Push(p, w.boneIndex1, w.weight1, d);
                delta += Push(p, w.boneIndex2, w.weight2, d);
                delta += Push(p, w.boneIndex3, w.weight3, d);
                result[v] = p + delta;
            }

            mesh.vertices = result;
            mesh.RecalculateBounds();
            st.Written = Copy.Vertices(mesh);
            // Only remember results that differ from the original: an unchanged mesh (e.g. every slider back at
            // 100%) would otherwise make every fresh rebuild of it look like a copy of ours.
            if (!Identical(st.Written, st.Original))
            {
                if (_writtenPrints.Count > 20000) _writtenPrints.Clear();
                _writtenPrints[Print(st.Written)] = (st.Original, key);
            }
            st.Key = key;
            Applied++;
        }

        /// <summary>Per-bone-slot deformation data for one mesh.</summary>
        private class Slots
        {
            public readonly bool[] Active;
            public readonly Vector3[] GirthStart, GirthAxis, SizePivot, RearStart, RearAxis, RearBack;
            public readonly float[] RearFade;
            public readonly Vector3[] WidthRight, WidthCenter, WidthShift, WidthStart, WidthAxis;
            public readonly bool[] WidthIsLeg;
            public float[] WidthF;
            public readonly Matrix4x4[] BindInv;
            public float[] GirthF, SizeF, RearF;
            public Matrix4x4[] Bind;

            public Slots(int n)
            {
                Active = new bool[n];
                GirthStart = new Vector3[n]; GirthAxis = new Vector3[n]; SizePivot = new Vector3[n];
                RearStart = new Vector3[n]; RearAxis = new Vector3[n]; RearBack = new Vector3[n];
                RearFade = new float[n];
                WidthRight = new Vector3[n]; WidthCenter = new Vector3[n]; WidthShift = new Vector3[n];
                WidthStart = new Vector3[n]; WidthAxis = new Vector3[n]; WidthIsLeg = new bool[n];
                BindInv = new Matrix4x4[n];
            }
        }

        private static Vector3 Push(Vector3 meshPos, int bi, float w, Slots d)
        {
            if (w <= 0f || bi < 0 || bi >= d.Active.Length || !d.Active[bi]) return Vector3.zero;
            var local = d.Bind[bi].MultiplyPoint3x4(meshPos);
            var localDelta = Vector3.zero;

            // Girth: push away from (or toward) the bone's axis.
            if (!Mathf.Approximately(d.GirthF[bi], 1f))
            {
                var a = d.GirthAxis[bi];
                var len2 = a.sqrMagnitude;
                if (len2 > 1e-8f)
                {
                    var rel = local - d.GirthStart[bi];
                    var t = Mathf.Clamp01(Vector3.Dot(rel, a) / len2);
                    localDelta += (rel - a * t) * (d.GirthF[bi] - 1f);
                }
            }

            // Rear (butt): like girth, but only for the part of the surface facing backwards, and on the thighs
            // only near the hip.
            if (!Mathf.Approximately(d.RearF[bi], 1f))
            {
                var a = d.RearAxis[bi];
                var len2 = a.sqrMagnitude;
                if (len2 > 1e-8f)
                {
                    var rel = local - d.RearStart[bi];
                    var tRaw = Vector3.Dot(rel, a) / len2;
                    var radial = rel - a * Mathf.Clamp01(tRaw);
                    var r = radial.magnitude;
                    if (r > 1e-6f)
                    {
                        // backward direction, flattened onto the cross-section of the bone
                        var back = Vector3.ProjectOnPlane(d.RearBack[bi], a).normalized;
                        var facing = Vector3.Dot(radial / r, back);
                        if (facing > 0f)
                        {
                            var fade = d.RearFade[bi] > 0f ? Mathf.Clamp01(1f - Mathf.Max(0f, tRaw) / d.RearFade[bi]) : 1f;
                            fade = fade * fade * (3f - 2f * fade);              // smooth edge
                            localDelta += radial * ((d.RearF[bi] - 1f) * facing * facing * fade);
                        }
                    }
                }
            }

            // Hip width: pelvis spreads sideways; thighs are carried out with it but not scaled.
            if (!Mathf.Approximately(d.WidthF[bi], 1f))
            {
                if (d.WidthIsLeg[bi])
                {
                    var a = d.WidthAxis[bi];
                    var len2 = a.sqrMagnitude;
                    var t = len2 > 1e-8f ? Vector3.Dot(local - d.WidthStart[bi], a) / len2 : 0f;
                    var fade = Mathf.Clamp01(1f - Mathf.Max(0f, t) / 0.5f);
                    fade = fade * fade * (3f - 2f * fade);
                    localDelta += d.WidthShift[bi] * ((d.WidthF[bi] - 1f) * fade);
                }
                else
                {
                    var r = d.WidthRight[bi];
                    var lateral = Vector3.Dot(local - d.WidthCenter[bi], r);
                    localDelta += r * (lateral * (d.WidthF[bi] - 1f));
                }
            }

            // Size: scale evenly around the joint (applied to the adjusted position).
            if (!Mathf.Approximately(d.SizeF[bi], 1f))
                localDelta += (local + localDelta - d.SizePivot[bi]) * (d.SizeF[bi] - 1f);

            return d.BindInv[bi].MultiplyVector(localDelta) * w;
        }

        private static bool Identical(Vector3[] a, Vector3[] b)
        {
            if (a == null || b == null || a.Length != b.Length) return false;
            for (int i = 0; i < a.Length; i++) if (a[i] != b[i]) return false;
            return true;
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
    /// Per-.vroid-file settings: body slider values and hair/accessory anchor rules. They're kept in memory
    /// while you edit and written to BepInEx/config/VRoidPlus/models/ only when VRoid saves the file (its
    /// timestamp changes, or Save As gives it a new path). Opening a file loads its settings; a new,
    /// never-saved model starts with none.
    /// </summary>
    internal class ModelFile
    {
        public static readonly ModelFile Current = new();

        public Dictionary<string, float> Values = new();
        public List<AnchorRule> HairAnchors = new();
        public List<AnchorRule> AccessoryAnchors = new();
        private string _savedJson = "";
        public string Path { get; private set; }
        private object _engine;
        private DateTime _mtime;
        private float _nextPoll;

        /// <summary>Raised when another model is opened and its settings were loaded.</summary>
        public event Action Loaded;

        public class FileData
        {
            public string Path = "";
            public string SavedAt = "";
            public Dictionary<string, float> Values = new();
            public List<AnchorRule> HairAnchors = new();
            public List<AnchorRule> AccessoryAnchors = new();
        }

        private string Snapshot() => System.Text.Json.JsonSerializer.Serialize(new FileData
        {
            Values = new SortedDictionary<string, float>(Values).ToDictionary(k => k.Key, k => k.Value),
            HairAnchors = HairAnchors, AccessoryAnchors = AccessoryAnchors,
        }, Json.Options);

        public bool HasUnsavedChanges => Snapshot() != _savedJson;

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

        /// <summary>Polls VRoid once a second for an opened file, Save and Save As.</summary>
        public void Update()
        {
            if (Time.unscaledTime < _nextPoll) return;
            _nextPoll = Time.unscaledTime + 1f;

            var model = VRoidApi.FromViewModel("CurrentFile.model", out _);
            var engine = VRoidApi.Get(model, "engine");
            if (engine == null) return;
            var engineId = Compat.Identity(engine);
            var path = VRoidApi.Get(model, "path") as string;
            if (string.IsNullOrEmpty(path)) path = null;

            if (!Equals(engineId, _engine))
            {
                bool firstModel = _engine == null;
                _engine = engineId;
                var previousPath = Path;
                Path = path;
                _mtime = MTime(path);
                bool hasStored = path != null && System.IO.File.Exists(StorePath(path));
                bool justWritten = _mtime != DateTime.MinValue && (DateTime.UtcNow - _mtime).TotalSeconds < 15;
                if (!firstModel && path != null && path != previousPath && !hasStored && justWritten && HasUnsavedChanges)
                {
                    // Looks like "Save As" of the model being edited: keep the current settings with the new file.
                    Persist();
                    return;
                }
                Values = new Dictionary<string, float>();
                HairAnchors = new List<AnchorRule>();
                AccessoryAnchors = new List<AnchorRule>();
                if (hasStored)
                {
                    var data = Json.Read<FileData>(StorePath(path));
                    foreach (var kv in data.Values) Values[kv.Key] = kv.Value;
                    HairAnchors = data.HairAnchors ?? new List<AnchorRule>();
                    AccessoryAnchors = data.AccessoryAnchors ?? new List<AnchorRule>();
                }
                _savedJson = Snapshot();
                Loaded?.Invoke();
                return;
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
        }

        private void Persist()
        {
            if (Path == null) return;
            var file = StorePath(Path);
            var snap = Snapshot();
            if (Values.Count == 0 && HairAnchors.Count == 0 && AccessoryAnchors.Count == 0 && !System.IO.File.Exists(file)) { _savedJson = snap; return; }
            Json.Write(file, new FileData
            {
                Path = Path, SavedAt = DateTime.Now.ToString("s"), Values = new Dictionary<string, float>(Values),
                HairAnchors = HairAnchors, AccessoryAnchors = AccessoryAnchors,
            });
            _savedJson = snap;
            Plugin.Logger.LogInfo($"VRoid Plus settings saved with {System.IO.Path.GetFileName(Path)}");
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
