using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using UnityEngine;

namespace VRoidPlus.Fbx
{
    /// <summary>Recognises humanoid bones by name (Mixamo, VRoid/VRM, Unity, 3ds Max Biped, Blender Rigify-like, ...).</summary>
    internal static class HumanBoneNames
    {
        private static Dictionary<string, HumanBodyBones> _table;

        public static HumanBodyBones? Match(string name)
        {
            _table ??= Build();
            var n = Normalize(name);
            if (_table.TryGetValue(n, out var b)) return b;
            foreach (var prefix in new[] { "mixamorig", "jbip", "bip001", "bip01", "bip", "def", "ctrl", "armature", "skeleton", "character" })
                if (n.StartsWith(prefix) && _table.TryGetValue(n.Substring(prefix.Length), out b)) return b;
            return null;
        }

        public static string Normalize(string name)
        {
            var i = name.LastIndexOf(':');
            if (i >= 0) name = name.Substring(i + 1);
            var sb = new StringBuilder();
            foreach (var c in name.ToLowerInvariant()) if (char.IsLetterOrDigit(c)) sb.Append(c);
            return sb.ToString();
        }

        private static Dictionary<string, HumanBodyBones> Build()
        {
            var t = new Dictionary<string, HumanBodyBones>();
            void Center(HumanBodyBones b, params string[] names)
            {
                foreach (var n in names) { t.TryAdd(n, b); t.TryAdd("c" + n, b); }
            }
            void Sided(HumanBodyBones left, HumanBodyBones right, params string[] names)
            {
                foreach (var n in names)
                {
                    foreach (var (side, bone) in new[] { ("left", left), ("l", left), ("right", right), ("r", right) })
                    {
                        t.TryAdd(side + n, bone);
                        t.TryAdd(n + side, bone);
                        t.TryAdd(side + "hand" + n, bone); // Mixamo: LeftHandThumb1
                    }
                }
            }
            Center(HumanBodyBones.Hips, "hips", "hip", "pelvis");
            Center(HumanBodyBones.Spine, "spine", "spine0", "spine00", "spine01", "abdomen", "waist");
            Center(HumanBodyBones.Chest, "chest", "spine1", "spine02");
            Center(HumanBodyBones.UpperChest, "upperchest", "spine2", "spine03", "chest1");
            Center(HumanBodyBones.Neck, "neck", "neck1", "neck01");
            Center(HumanBodyBones.Head, "head");
            Center(HumanBodyBones.Jaw, "jaw");
            Sided(HumanBodyBones.LeftShoulder, HumanBodyBones.RightShoulder, "shoulder", "clavicle", "collar");
            Sided(HumanBodyBones.LeftUpperArm, HumanBodyBones.RightUpperArm, "upperarm", "arm", "uparm");
            Sided(HumanBodyBones.LeftLowerArm, HumanBodyBones.RightLowerArm, "lowerarm", "forearm", "elbow");
            Sided(HumanBodyBones.LeftHand, HumanBodyBones.RightHand, "hand", "wrist");
            Sided(HumanBodyBones.LeftUpperLeg, HumanBodyBones.RightUpperLeg, "upperleg", "upleg", "thigh");
            Sided(HumanBodyBones.LeftLowerLeg, HumanBodyBones.RightLowerLeg, "lowerleg", "leg", "calf", "shin", "knee");
            Sided(HumanBodyBones.LeftFoot, HumanBodyBones.RightFoot, "foot", "ankle");
            Sided(HumanBodyBones.LeftToes, HumanBodyBones.RightToes, "toes", "toebase", "toe", "toe0");
            Sided(HumanBodyBones.LeftEye, HumanBodyBones.RightEye, "eye");

            var fingers = new[]
            {
                (new[] { "thumb", "finger0" }, HumanBodyBones.LeftThumbProximal, HumanBodyBones.RightThumbProximal),
                (new[] { "index", "finger1" }, HumanBodyBones.LeftIndexProximal, HumanBodyBones.RightIndexProximal),
                (new[] { "middle", "finger2" }, HumanBodyBones.LeftMiddleProximal, HumanBodyBones.RightMiddleProximal),
                (new[] { "ring", "finger3" }, HumanBodyBones.LeftRingProximal, HumanBodyBones.RightRingProximal),
                (new[] { "little", "pinky", "finger4" }, HumanBodyBones.LeftLittleProximal, HumanBodyBones.RightLittleProximal),
            };
            foreach (var (names, l, r) in fingers)
            {
                for (int seg = 0; seg < 3; seg++)
                {
                    var lb = (HumanBodyBones)((int)l + seg); var rb = (HumanBodyBones)((int)r + seg);
                    foreach (var n in names)
                    {
                        var forms = n.StartsWith("finger")
                            ? new[] { seg == 0 ? n : n + seg }            // Biped: Finger0, Finger01, Finger02
                            : new[] { n + (seg + 1), n + "0" + (seg + 1), n + new[] { "proximal", "intermediate", "distal" }[seg] };
                        Sided(lb, rb, forms);
                    }
                }
            }
            return t;
        }
    }

    /// <summary>
    /// Plays an FBX take on the VRoid avatar. Each bone gets the source bone's world-space rotation change
    /// from its rest pose, so differences in bone orientation between rigs don't matter as long as both
    /// rest poses are a T-pose (Mixamo, VRoid and most humanoid rigs are).
    /// </summary>
    internal class Retargeter
    {
        private readonly FbxScene _scene;
        private readonly Dictionary<HumanBodyBones, FbxModel> _source = new();
        private readonly Dictionary<HumanBodyBones, Quaternion> _srcRestRot = new();
        private Vector3 _srcHipsRest;
        private float _scale = 1f;
        private Quaternion _facing = Quaternion.identity;

        private Animator _target;
        private readonly List<(HumanBodyBones bone, Transform t)> _ordered = new();
        private readonly Dictionary<Transform, Quaternion> _savedLocal = new();
        private Vector3 _savedHipsLocal;
        private Vector3 _tgtHipsRestLocal;

        public int MappedCount => _source.Count;
        public IEnumerable<string> Unmapped => _scene.Models.Where(m => HumanBoneNames.Match(m.Name) == null).Select(m => m.Name);

        public Retargeter(FbxScene scene)
        {
            _scene = scene;
            foreach (var m in scene.Models)
            {
                var b = HumanBoneNames.Match(m.Name);
                if (b.HasValue && !_source.ContainsKey(b.Value)) _source[b.Value] = m;
            }
            if (!_source.ContainsKey(HumanBodyBones.Hips))
                throw new InvalidOperationException("No hips bone found in this FBX, so it can't be used as a humanoid animation. Bone names: " +
                                                    string.Join(", ", scene.Models.Take(20).Select(m => m.Name)));
            var rest = scene.Evaluate(null, 0);
            foreach (var kv in _source) _srcRestRot[kv.Key] = FbxScene.Rotation(rest[kv.Value]);
            _srcHipsRest = FbxScene.Position(rest[_source[HumanBodyBones.Hips]]);

            // Leg length (unit-free scale) and which way the source character faces.
            _srcLegLength = LegLength(b => _source.TryGetValue(b, out var m) ? FbxScene.Position(rest[m]) : (Vector3?)null);
            if (_source.TryGetValue(HumanBodyBones.LeftUpperLeg, out var ll) && _source.TryGetValue(HumanBodyBones.RightUpperLeg, out var rl))
                _srcRight = FbxScene.Position(rest[rl]) - FbxScene.Position(rest[ll]);
            else if (_source.TryGetValue(HumanBodyBones.LeftUpperArm, out var la) && _source.TryGetValue(HumanBodyBones.RightUpperArm, out var ra))
                _srcRight = FbxScene.Position(rest[ra]) - FbxScene.Position(rest[la]);
        }

        private readonly float _srcLegLength;
        private readonly Vector3 _srcRight = Vector3.right;

        private static float LegLength(Func<HumanBodyBones, Vector3?> pos)
        {
            var a = pos(HumanBodyBones.LeftUpperLeg); var b = pos(HumanBodyBones.LeftLowerLeg); var c = pos(HumanBodyBones.LeftFoot);
            if (a == null || b == null || c == null) return 0;
            return Vector3.Distance(a.Value, b.Value) + Vector3.Distance(b.Value, c.Value);
        }

        /// <summary>Binds to the avatar and remembers its current pose so Release() can put it back.</summary>
        public void Attach(Animator target)
        {
            _target = target;
            _ordered.Clear(); _savedLocal.Clear();
            for (var b = HumanBodyBones.Hips; b < HumanBodyBones.LastBone; b++)
            {
                var t = target.GetBoneTransform(b);
                if (t == null) continue;
                _ordered.Add((b, t));
                _savedLocal[t] = t.localRotation;
            }
            int Depth(Transform t) { int d = 0; for (var p = t; p != null; p = p.parent) d++; return d; }
            _ordered.Sort((x, y) => Depth(x.t).CompareTo(Depth(y.t)));

            var hips = target.GetBoneTransform(HumanBodyBones.Hips);
            _savedHipsLocal = hips.localPosition;
            _tgtHipsRestLocal = hips.localPosition;

            // VRoid bones have identity local rotation in the rest (T) pose, so rest world rotation == root rotation.
            var root = target.transform;
            float tgtLeg = LegLength(b => target.GetBoneTransform(b)?.position);
            _scale = _srcLegLength > 1e-5f && tgtLeg > 1e-5f ? tgtLeg / _srcLegLength : 1f;

            var l = target.GetBoneTransform(HumanBodyBones.LeftUpperLeg); var r = target.GetBoneTransform(HumanBodyBones.RightUpperLeg);
            var tgtRightLocal = l != null && r != null ? Quaternion.Inverse(hips.rotation) * (r.position - l.position) : Vector3.right;
            var tgtRight = Vector3.ProjectOnPlane(root.rotation * tgtRightLocal, Vector3.up);
            var srcRight = Vector3.ProjectOnPlane(_srcRight, Vector3.up);
            _facing = srcRight.sqrMagnitude > 1e-8f && tgtRight.sqrMagnitude > 1e-8f ? Quaternion.FromToRotation(srcRight.normalized, tgtRight.normalized) : Quaternion.identity;
        }

        public void Apply(FbxTake take, double time, bool rootMotion)
        {
            if (_target == null) return;
            var g = _scene.Evaluate(take, time);
            var root = _target.transform;
            var tgtRest = root.rotation;

            foreach (var (bone, t) in _ordered)
            {
                if (bone == HumanBodyBones.LeftEye || bone == HumanBodyBones.RightEye || bone == HumanBodyBones.Jaw) continue;
                if (!_source.TryGetValue(bone, out var src)) { t.localRotation = Quaternion.identity; continue; }
                var delta = FbxScene.Rotation(g[src]) * Quaternion.Inverse(_srcRestRot[bone]);
                t.rotation = _facing * delta * Quaternion.Inverse(_facing) * tgtRest;
            }

            var hips = _target.GetBoneTransform(HumanBodyBones.Hips);
            var move = _facing * (FbxScene.Position(g[_source[HumanBodyBones.Hips]]) - _srcHipsRest) * _scale;
            if (!rootMotion) move = new Vector3(0, move.y, 0);
            var restWorld = hips.parent != null ? hips.parent.TransformPoint(_tgtHipsRestLocal) : _tgtHipsRestLocal;
            hips.position = restWorld + move;
        }

        /// <summary>Gives the avatar back the pose it had before playback.</summary>
        public void Release()
        {
            if (_target == null) return;
            foreach (var kv in _savedLocal) if (kv.Key != null) kv.Key.localRotation = kv.Value;
            var hips = _target.GetBoneTransform(HumanBodyBones.Hips);
            if (hips != null) hips.localPosition = _savedHipsLocal;
            _target = null;
        }
    }
}
