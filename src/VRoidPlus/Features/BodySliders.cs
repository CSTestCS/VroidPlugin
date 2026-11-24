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

        /// <summary>Resets the body sliders (not the thigh squish settings).</summary>
        public void ResetAll() { foreach (var d in Sliders) File.Values.Remove(d.Id); _dirty = true; }

        // ---------------------------------------------------------------- thigh squish
        // Per leg ("L"/"R"): line height (0 = hip, 1 = knee), depth (0..0.3 slimming below the line),
        // edge softness (0 crisp .. 1 rounded), fade back on/off and fade length (in thigh lengths).

        public static readonly Dictionary<string, float> SquishDefaults = new()
        {
            ["line"] = 0.6f, ["depth"] = 0f, ["soft"] = 0.3f, ["fade"] = 1f, ["fadelen"] = 0.35f,
        };

        public static string SquishKey(string side, string p) => $"squish.{side}.{p}";

        public float GetSquish(string side, string p) =>
            File.Values.TryGetValue(SquishKey(side, p), out var v) ? v : SquishDefaults[p];

        public void SetSquish(string side, string p, float v)
        {
            if (Mathf.Approximately(v, SquishDefaults[p])) File.Values.Remove(SquishKey(side, p)); else File.Values[SquishKey(side, p)] = v;
            _dirty = true;
        }

        public void CopySquish(string from, string to)
        {
            foreach (var p in SquishDefaults.Keys) SetSquish(to, p, GetSquish(from, p));
        }

        public void ResetSquish(string side) { foreach (var p in SquishDefaults.Keys) File.Values.Remove(SquishKey(side, p)); _dirty = true; }

        // ---------------------------------------------------------------- bulges (stomach, groin)
        // amount 0..1 (how far it sticks out), width/height 0.3..2 (size), offset -1..1 (up/down), round 0..1.

        public static readonly string[] Bulges = { "stomach", "groin" };

        public static readonly Dictionary<string, float> BulgeDefaults = new()
        {
            ["amount"] = 0f, ["width"] = 1f, ["height"] = 1f, ["offset"] = 0f, ["round"] = 0.5f,
        };

        public static string BulgeKey(string bulge, string p) => $"bulge.{bulge}.{p}";

        public float GetBulge(string bulge, string p) =>
            File.Values.TryGetValue(BulgeKey(bulge, p), out var v) ? v : BulgeDefaults[p];

        public void SetBulge(string bulge, string p, float v)
        {
            if (Mathf.Approximately(v, BulgeDefaults[p])) File.Values.Remove(BulgeKey(bulge, p)); else File.Values[BulgeKey(bulge, p)] = v;
            _dirty = true;
        }

        public void ResetBulge(string bulge) { foreach (var p in BulgeDefaults.Keys) File.Values.Remove(BulgeKey(bulge, p)); _dirty = true; }

        private struct SquishParams { public float Line, Depth, Soft, FadeLen; public bool Fade; }

        private SquishParams Squish(string side) => new()
        {
            Line = Mathf.Clamp01(GetSquish(side, "line")),
            Depth = Mathf.Clamp(GetSquish(side, "depth"), 0f, 0.5f),
            // softness 0..1 -> edge width 0.4%..10% of the thigh's length
            Soft = Mathf.Lerp(0.004f, 0.10f, Mathf.Clamp01(GetSquish(side, "soft"))),
            Fade = GetSquish(side, "fade") > 0.5f,
            FadeLen = Mathf.Clamp(GetSquish(side, "fadelen"), 0.02f, 1.5f),
        };

        private class MeshState
        {
            public Mesh Mesh;
            public Vector3[] Original;
            public Vector3[] Written;
            public string Key; // the slider values Written was made with
            public string Model, Part;     // e.g. "PreviewModelOrigin", "Body" (to match photo mode's copy)
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

        public string ModelsInfo = "";

        private enum ModelKind { Source, Compare, Skip }

        /// <summary>
        /// Source: VRoid's own fresh builds from your character's data (editor model, PreviewModelOrigin) -
        ///         always reshaped.
        /// Skip:   MeshReductionReference, a simplified clone of another copy - inherits the shape.
        /// Compare: everything else (photo mode's PhotoBoothModel, the ExportAvatar models that export and
        ///         upload build, ...). The same name can be a fresh build (export) or a copy of an already
        ///         reshaped model (upload), so each mesh is compared with the source's mesh of the same part.
        /// </summary>
        private static ModelKind KindOf(Animator a)
        {
            var n = a.gameObject.name;
            if (n.StartsWith("CurrentModelOrigin", StringComparison.Ordinal) || n == "PreviewModelOrigin") return ModelKind.Source;
            if (n == "MeshReductionReference") return ModelKind.Skip;
            return ModelKind.Compare;
        }

        private static bool IsSourceName(string model) =>
            model != null && (model.StartsWith("CurrentModelOrigin", StringComparison.Ordinal) || model == "PreviewModelOrigin");

        /// <summary>Per mesh of a Compare model: 'F' reshape it, 'L' leave it (decided the first time it's seen).</summary>
        private readonly Dictionary<int, char> _decision = new();
        private readonly Dictionary<string, int[]> _decisionCounts = new();   // model -> [inherited, reshaped, left, clearly inherited, clearly fresh]

        private static int Print(Vector3[] v)
        {
            var h = new HashCode();
            h.Add(v.Length);
            int step = Math.Max(1, v.Length / 97);
            for (int i = 0; i < v.Length; i += step) { h.Add(v[i].x); h.Add(v[i].y); h.Add(v[i].z); }
            return h.ToHashCode();
        }

        private float _nextCheck;
        private bool _dirty = true;

        public void MarkDirty() => _dirty = true;

        /// <summary>What the last pass did, for diagnostics.</summary>
        public string LastInfo = "not run yet";
        public int Overwritten, Applied;

        public bool AllDefault => File.Values.Count == 0;

        private string Key() => string.Join("|", Sliders.Select(s => Get(s.Id).ToString("F3"))) + "|" +
                                string.Join("|", new[] { "L", "R" }.SelectMany(side => SquishDefaults.Keys.Select(p => GetSquish(side, p).ToString("F3")))) + "|" +
                                string.Join("|", Bulges.SelectMany(b => BulgeDefaults.Keys.Select(p => GetBulge(b, p).ToString("F3"))));

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
            // Only the copies VRoid builds fresh from its own data are reshaped: the editor model and
            // PreviewModelOrigin (used for photo mode, export and upload). VRoid makes the other copies from those:
            // PhotoBoothModel by exporting PreviewModelOrigin to VRM and importing it again, MeshReductionReference
            // as a simplified clone of that. They inherit the slider shape, so reshaping them too would apply it
            // two or three times.
            var modelNotes = new List<string>();
            foreach (var animator in Avatar.AllHumanoids())
            {
                if (animator == null) continue;
                var kind = KindOf(animator);
                var modelLabel = animator.gameObject.name;
                if (_decisionCounts.TryGetValue(modelLabel, out var c)) modelNotes.Add($"{modelLabel} ({kind}: {c[0]} inherited, {c[1]} reshaped, {c[2]} left)");
                else modelNotes.Add($"{modelLabel} ({kind})");
                if (kind == ModelKind.Skip) continue;
                // New meshes of any model are handled at once (ignoring the per-frame time limit), so the shape is
                // in place before VRoid exports/uploads it or builds further copies from it.
                bool urgent = true;
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
                    if (outOfTime && !(urgent && swapped)) continue;
                    if (_passActive) _passDone.Add(smrId);
                    if (fixSwap) _lastSwapFix[smrId] = now;

                    if (_unaffected.Contains(meshId + "|" + key)) continue;
                    if (!mesh.isReadable) { unreadable++; continue; }
                    rig ??= new Rig(animator);
                    var modelName = animator.gameObject.name;
                    if (kind == ModelKind.Compare && Decide(smr, mesh, modelName) == 'L') continue;
                    Process(smr, mesh, key, rig);
                    if (_states.TryGetValue(mesh.GetInstanceID(), out var done) && done.Model == null) { done.Model = modelName; done.Part = smr.gameObject.name; }

                    if (_clock.Elapsed.TotalMilliseconds > FrameBudgetMs) { outOfTime = true; BudgetHits++; }
                }
            }
            ModelsInfo = string.Join(", ", modelNotes);
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

        /// <summary>
        /// For a mesh of a non-source model seen for the first time: compare it with the source model's mesh of
        /// the same part before (original) and after (reshaped) the sliders.
        ///  - clearly the reshaped one: it was copied from an already reshaped model and inherited the shape.
        ///    It takes over the source's original (so it follows later slider changes) instead of being
        ///    reshaped again.
        ///  - clearly the original: a fresh build; reshape it.
        ///  - neither (no matching part, different layout): photo mode's model is reshaped (it is built from
        ///    the not-yet-reshaped preview); anything else is left as it is.
        /// </summary>
        private char Decide(SkinnedMeshRenderer smr, Mesh mesh, string modelName)
        {
            int id = mesh.GetInstanceID();
            if (_decision.TryGetValue(id, out var d)) return d;
            if (!_decisionCounts.TryGetValue(modelName, out var counts)) _decisionCounts[modelName] = counts = new int[5];

            var part = smr.gameObject.name;
            var p = Copy.Vertices(mesh);
            MeshState source = null;
            foreach (var st in _states.Values)
                if (IsSourceName(st.Model) && st.Part == part && st.Written != null && st.Original != null &&
                    st.Original.Length == p.Length && st.Written.Length == p.Length)
                { source = st; break; }

            char result;
            if (source != null && !Identical(source.Written, source.Original))
            {
                double dO = 0, dW = 0;
                int step = Math.Max(1, p.Length / 500);
                for (int i = 0; i < p.Length; i += step) { dO += (p[i] - source.Original[i]).sqrMagnitude; dW += (p[i] - source.Written[i]).sqrMagnitude; }
                if (dW < dO * 0.25)
                {
                    _states[id] = new MeshState { Mesh = mesh, Original = source.Original, Written = p, Key = source.Key, Model = modelName, Part = part };
                    CopiesSkipped++;
                    counts[0]++; counts[3]++;
                    _decision[id] = 'F';      // follows slider changes from now on
                    return 'F';
                }
                if (dO < dW * 0.25) { counts[1]++; counts[4]++; return _decision[id] = 'F'; }
            }
            else if (source != null)
            {
                // The source part isn't changed by the sliders: nothing to tell apart, and nothing to do.
                counts[1]++;
                return _decision[id] = 'F';
            }
            // Can't tell from this mesh: go with what this model's other meshes clearly showed.
            if (counts[3] + counts[4] >= 3) result = counts[4] > counts[3] ? 'F' : 'L';
            else if (modelName == "PhotoBoothModel") result = 'F';
            else return 'L';                  // not decided yet: look again on the next pass
            counts[result == 'F' ? 1 : 2]++;
            return _decision[id] = result;
        }

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
            var sqSide = new int[bones.Length];      // 0 none, 1 left leg, 2 right leg
            var sqCalf = new bool[bones.Length];
            var sqL = Squish("L"); var sqR = Squish("R");
            // Bulges: which main bones each one may move (vertices on their J_Adj_ helpers count too).
            var bulgeOn = new bool[Bulges.Length];
            var bulgeBones = new HashSet<Transform>[Bulges.Length];
            for (int bi2 = 0; bi2 < Bulges.Length; bi2++)
            {
                bulgeOn[bi2] = GetBulge(Bulges[bi2], "amount") > 0f;
                bulgeBones[bi2] = new HashSet<Transform>();
                var list = Bulges[bi2] == "stomach"
                    ? new[] { HumanBodyBones.Hips, HumanBodyBones.Spine, HumanBodyBones.Chest }
                    : new[] { HumanBodyBones.Hips, HumanBodyBones.LeftUpperLeg, HumanBodyBones.RightUpperLeg, HumanBodyBones.Spine };
                foreach (var hb in list) { var t = rig.Bone(hb); if (t != null) bulgeBones[bi2].Add(t); }
            }
            var bulgeSlot = new bool[Bulges.Length, bones.Length];
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

                if (owner != null)
                    for (int bi2 = 0; bi2 < Bulges.Length; bi2++)
                        if (bulgeOn[bi2] && bulgeBones[bi2].Contains(owner)) { bulgeSlot[bi2, i] = true; any = true; }

                // Thigh squish: thigh and calf bones (and their J_Adj_ helpers) of a leg whose depth is set.
                if (owner != null)
                {
                    for (int side = 0; side < 2; side++)
                    {
                        var sp = side == 0 ? sqL : sqR;
                        if (sp.Depth <= 0f) continue;
                        var upper = rig.Bone(side == 0 ? HumanBodyBones.LeftUpperLeg : HumanBodyBones.RightUpperLeg);
                        var lower = rig.Bone(side == 0 ? HumanBodyBones.LeftLowerLeg : HumanBodyBones.RightLowerLeg);
                        if (upper == null || lower == null) continue;
                        if (owner == upper) { sqSide[i] = side + 1; sqCalf[i] = false; any = true; }
                        else if (owner == lower) { sqSide[i] = side + 1; sqCalf[i] = true; any = true; }
                    }
                }
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
            // Each bulge's frame on this model (world space) and its settings.
            var bulgeFrames = new BulgeFrame?[Bulges.Length];
            for (int bi2 = 0; bi2 < Bulges.Length; bi2++)
            {
                if (!bulgeOn[bi2]) continue;
                bulgeFrames[bi2] = Frame(rig, Bulges[bi2]);
                d.BgAmount[bi2] = Mathf.Clamp01(GetBulge(Bulges[bi2], "amount"));
                d.BgW[bi2] = Mathf.Clamp(GetBulge(Bulges[bi2], "width"), 0.3f, 2f);
                d.BgH[bi2] = Mathf.Clamp(GetBulge(Bulges[bi2], "height"), 0.3f, 2f);
                d.BgRound[bi2] = Bulges[bi2] == "stomach" ? Mathf.Clamp01(GetBulge(Bulges[bi2], "round")) : 0.25f;
            }
            var back = rig.Back;
            var right = rig.Right;
            var hipsBone = rig.Bone(HumanBodyBones.Hips);
            for (int i = 0; i < bones.Length; i++)
            {
                bool inBulge = false;
                for (int bi2 = 0; bi2 < Bulges.Length; bi2++) inBulge |= bulgeSlot[bi2, i];
                if (Mathf.Approximately(girthF[i], 1f) && Mathf.Approximately(sizeF[i], 1f) && Mathf.Approximately(rearF[i], 1f) && Mathf.Approximately(widthF[i], 1f) && sqSide[i] == 0 && !inBulge) continue;
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
                for (int bi2 = 0; bi2 < Bulges.Length; bi2++)
                {
                    if (!bulgeSlot[bi2, i] || bulgeFrames[bi2] == null) continue;
                    var f = bulgeFrames[bi2].Value;
                    d.BgActive[bi2][i] = true;
                    d.BgCenter[bi2][i] = bones[i].InverseTransformPoint(f.Center);
                    d.BgFwd[bi2][i] = bones[i].InverseTransformDirection(f.Forward).normalized;
                    d.BgRight[bi2][i] = bones[i].InverseTransformDirection(f.Right).normalized;
                    d.BgUp[bi2][i] = bones[i].InverseTransformDirection(f.Up).normalized;
                    d.BgScale[bi2][i] = bones[i].InverseTransformVector(f.Right * f.Unit).magnitude;   // world unit in this bone's space
                }
                if (sqSide[i] > 0)
                {
                    // Position along the leg in thigh lengths: 0 at the hip joint, 1 at the knee, beyond 1 on the calf.
                    bool left = sqSide[i] == 1;
                    var upper = rig.Bone(left ? HumanBodyBones.LeftUpperLeg : HumanBodyBones.RightUpperLeg);
                    var lower = rig.Bone(left ? HumanBodyBones.LeftLowerLeg : HumanBodyBones.RightLowerLeg);
                    var foot = rig.Bone(left ? HumanBodyBones.LeftFoot : HumanBodyBones.RightFoot);
                    var from = sqCalf[i] ? lower : upper;
                    var to = sqCalf[i] ? foot : lower;
                    if (from != null && to != null)
                    {
                        var thighLen = Vector3.Distance(upper.position, lower.position);
                        var segLen = Vector3.Distance(from.position, to.position);
                        d.SqActive[i] = thighLen > 1e-5f;
                        d.SqStart[i] = bones[i].InverseTransformPoint(from.position);
                        d.SqAxis[i] = bones[i].InverseTransformPoint(to.position) - d.SqStart[i];
                        d.SqU0[i] = sqCalf[i] ? 1f : 0f;
                        d.SqUScale[i] = thighLen > 1e-5f ? segLen / thighLen : 1f;
                        var sp = left ? sqL : sqR;
                        d.SqLine[i] = sp.Line; d.SqDepth[i] = sp.Depth; d.SqSoft[i] = sp.Soft;
                        d.SqFadeLen[i] = sp.Fade ? sp.FadeLen : 0f;
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
            public readonly bool[][] BgActive = new bool[2][];
            public readonly Vector3[][] BgCenter = new Vector3[2][], BgFwd = new Vector3[2][], BgRight = new Vector3[2][], BgUp = new Vector3[2][];
            public readonly float[][] BgScale = new float[2][];
            public readonly float[] BgAmount = new float[2], BgW = new float[2], BgH = new float[2], BgRound = new float[2];
            public readonly bool[] SqActive;
            public readonly Vector3[] SqStart, SqAxis;
            public readonly float[] SqU0, SqUScale, SqLine, SqDepth, SqSoft, SqFadeLen;
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
                for (int b = 0; b < 2; b++)
                {
                    BgActive[b] = new bool[n]; BgCenter[b] = new Vector3[n]; BgFwd[b] = new Vector3[n];
                    BgRight[b] = new Vector3[n]; BgUp[b] = new Vector3[n]; BgScale[b] = new float[n];
                }
                SqActive = new bool[n]; SqStart = new Vector3[n]; SqAxis = new Vector3[n];
                SqU0 = new float[n]; SqUScale = new float[n]; SqLine = new float[n]; SqDepth = new float[n];
                SqSoft = new float[n]; SqFadeLen = new float[n];
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

            // Bulges: a smooth rounded bump on the front of the body (oval, width x height), pushing the surface
            // forward and - with roundness - also outward from the bump's centre, fading to nothing at its edge.
            for (int b = 0; b < 2; b++)
            {
                if (!d.BgActive[b][bi]) continue;
                var unit = d.BgScale[b][bi];
                if (unit <= 1e-6f) continue;
                var rel = local - d.BgCenter[b][bi];
                var fwd = d.BgFwd[b][bi];
                var depth = Vector3.Dot(rel, fwd);
                if (depth < -0.15f * unit) continue;                         // back of the body: untouched
                var x = Vector3.Dot(rel, d.BgRight[b][bi]) / (d.BgW[b] * unit * (b == 0 ? 0.75f : 0.45f));
                var y = Vector3.Dot(rel, d.BgUp[b][bi]) / (d.BgH[b] * unit * (b == 0 ? 0.85f : 0.45f));
                var r2 = x * x + y * y;
                if (r2 >= 1f) continue;
                var fall = 1f - r2;
                fall = fall * fall * (3f - 2f * fall);                        // smooth dome, zero slope at the edge
                var front = Smooth((depth + 0.15f * unit) / (0.35f * unit)); // fade in from the sides
                var lateral = rel - fwd * depth;
                var dir = fwd;
                if (d.BgRound[b] > 0f && lateral.sqrMagnitude > 1e-10f)
                    dir = Vector3.Lerp(fwd, (fwd + lateral.normalized * 0.8f).normalized, d.BgRound[b]).normalized;
                localDelta += dir * (d.BgAmount[b] * unit * 0.6f * fall * front);
            }

            // Thigh squish: slim the leg below the line (nothing above it changes). The slimming comes in over a
            // short soft edge just below the line and, with fade back on, eases out again further down.
            if (d.SqActive[bi])
            {
                var a = d.SqAxis[bi];
                var len2 = a.sqrMagnitude;
                if (len2 > 1e-8f)
                {
                    var rel = local - d.SqStart[bi];
                    var raw = Vector3.Dot(rel, a) / len2;
                    var u = d.SqU0[bi] + Mathf.Max(0f, raw) * d.SqUScale[bi];
                    var below = u - d.SqLine[bi];
                    if (below > 0f)
                    {
                        var edge = Smooth(below / d.SqSoft[bi]);
                        var keep = d.SqFadeLen[bi] > 0f ? 1f - Smooth((below - d.SqSoft[bi]) / d.SqFadeLen[bi]) : 1f;
                        var k = d.SqDepth[bi] * edge * keep;
                        if (k > 0f)
                        {
                            var radial = rel - a * Mathf.Clamp01(raw);
                            localDelta -= radial * k;
                        }
                    }
                }
            }

            // Size: scale evenly around the joint (applied to the adjusted position).
            if (!Mathf.Approximately(d.SizeF[bi], 1f))
                localDelta += (local + localDelta - d.SizePivot[bi]) * (d.SizeF[bi] - 1f);

            return d.BindInv[bi].MultiplyVector(localDelta) * w;
        }

        private struct BulgeFrame { public Vector3 Center, Forward, Right, Up; public float Unit; }

        /// <summary>
        /// Where a bulge sits on this model (world space), built from the skeleton: forward/right/up of the body,
        /// a size unit (the hip width) and the centre - the spine joint for the stomach, between and just below the
        /// hip joints for the groin - moved up/down by the Position setting.
        /// </summary>
        private BulgeFrame? Frame(Rig rig, string bulge)
        {
            var hips = rig.Bone(HumanBodyBones.Hips); var spine = rig.Bone(HumanBodyBones.Spine);
            var l = rig.Bone(HumanBodyBones.LeftUpperLeg); var rl = rig.Bone(HumanBodyBones.RightUpperLeg);
            if (hips == null || spine == null || l == null || rl == null) return null;
            var right = rig.Right;
            var up = Vector3.ProjectOnPlane(spine.position - hips.position, right);
            up = up.sqrMagnitude > 1e-10f ? up.normalized : Vector3.up;
            var forward = -rig.Back;
            forward = Vector3.ProjectOnPlane(forward, up).normalized;
            var unit = Mathf.Max(0.02f, Vector3.Distance(l.position, rl.position));
            Vector3 center = bulge == "stomach"
                ? spine.position
                : (l.position + rl.position) * 0.5f - up * (unit * 0.35f);
            center += up * (GetBulge(bulge, "offset") * unit * 0.8f);
            return new BulgeFrame { Center = center, Forward = forward, Right = right, Up = up, Unit = unit };
        }

        private static float Smooth(float x)
        {
            x = Mathf.Clamp01(x);
            return x * x * (3f - 2f * x);
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
