using System.Collections.Generic;
using System.Linq;
using UnityEngine;

namespace VRoidPlus
{
    /// <summary>Finds the avatar being edited (the humanoid Animator with the most renderers) and its bones.</summary>
    internal static class Avatar
    {
        private static Animator _animator;
        private static float _nextSearch;
        private static readonly Dictionary<HumanBodyBones, Transform> Bones = new();
        private static readonly HashSet<Transform> HumanSet = new();
        private static List<Transform> _allBones = new();

        public static Animator Animator { get { Refresh(); return _animator; } }
        public static bool Found => Animator != null;
        public static IReadOnlyCollection<Transform> HumanoidBones => HumanSet;
        public static List<Transform> AllBones { get { Refresh(); return _allBones; } }

        /// <summary>Incremented whenever a different avatar (or rebuilt skeleton) is detected.</summary>
        public static int Generation { get; private set; }

        public static Transform Bone(HumanBodyBones b)
        {
            Refresh();
            return Bones.TryGetValue(b, out var t) && t != null ? t : null;
        }

        public static bool IsHumanoid(Transform t) => t != null && HumanSet.Contains(t);

        public static void Refresh(bool force = false)
        {
            bool alive = _animator != null && _animator.gameObject.activeInHierarchy && Bones.TryGetValue(HumanBodyBones.Hips, out var h) && h != null;
            if (alive && !force && Time.unscaledTime < _nextSearch) return;
            if (!force && !alive && Time.unscaledTime < _nextSearch) return;
            _nextSearch = Time.unscaledTime + 2f;

            Animator best = null; int bestScore = -1;
            foreach (var a in Object.FindObjectsOfType<Animator>())
            {
                if (a == null || !a.isHuman || !a.gameObject.activeInHierarchy) continue;
                if (a.GetBoneTransform(HumanBodyBones.Head) == null) continue;
                var score = a.GetComponentsInChildren<SkinnedMeshRenderer>(false).Length;
                if (score > bestScore) { best = a; bestScore = score; }
            }

            var hips = best != null ? best.GetBoneTransform(HumanBodyBones.Hips) : null;
            bool changed = best != _animator || !alive || (hips != null && Bones.TryGetValue(HumanBodyBones.Hips, out var oldHips) && oldHips != hips);
            _animator = best;
            if (!changed)
            {
                // Same model, but VRoid adds/rebuilds hair and items while you edit: refresh the bone list.
                if (hips != null)
                {
                    var r = hips.parent != null ? hips.parent : hips;
                    var fresh = new List<Transform>();
                    foreach (var t in r.GetComponentsInChildren<Transform>(true)) if (t != null) fresh.Add(t);
                    if (fresh.Count != _allBones.Count) Generation++;
                    _allBones = fresh;
                }
                return;
            }

            Bones.Clear(); HumanSet.Clear(); _allBones = new List<Transform>();
            if (best == null) return;
            for (var b = HumanBodyBones.Hips; b < HumanBodyBones.LastBone; b++)
            {
                var t = best.GetBoneTransform(b);
                if (t == null) continue;
                Bones[b] = t; HumanSet.Add(t);
            }
            // Every transform of the skeleton (humanoid + hair + accessory + secondary bones).
            var root = hips.parent != null ? hips.parent : hips;
            foreach (var t in root.GetComponentsInChildren<Transform>(true))
                if (t != null) _allBones.Add(t);
            Generation++;
        }

        /// <summary>Nearest humanoid ancestor (or self), e.g. Head for a hair bone.</summary>
        public static Transform HumanoidAncestor(Transform t)
        {
            for (var p = t; p != null; p = p.parent) if (IsHumanoid(p)) return p;
            return null;
        }

        public static string HumanName(Transform t)
        {
            foreach (var kv in Bones) if (kv.Value == t) return kv.Key.ToString();
            return null;
        }

        public static string Path(Transform t)
        {
            var root = Animator != null ? Animator.transform : null;
            var parts = new List<string>();
            for (var p = t; p != null && p != root; p = p.parent) parts.Add(p.name);
            parts.Reverse();
            return string.Join("/", parts);
        }

        private static List<Animator> _allHumanoids = new();
        private static float _nextAllSearch;

        /// <summary>
        /// Every humanoid model in the scene. VRoid keeps several copies of the avatar (the editor model,
        /// and in photo mode PhotoBoothModel / PreviewModelOrigin / MeshReductionReference).
        /// </summary>
        public static List<Animator> AllHumanoids()
        {
            if (Time.unscaledTime < _nextAllSearch && _allHumanoids.All(a => a != null)) return _allHumanoids;
            _nextAllSearch = Time.unscaledTime + 2f;
            _allHumanoids = new List<Animator>();
            foreach (var a in Object.FindObjectsOfType<Animator>())
                if (a != null && a.isHuman && a.gameObject.activeInHierarchy && a.GetBoneTransform(HumanBodyBones.Hips) != null)
                    _allHumanoids.Add(a);
            return _allHumanoids;
        }

        public static IEnumerable<Renderer> Renderers()
        {
            var a = Animator;
            if (a == null) return Enumerable.Empty<Renderer>();
            return a.GetComponentsInChildren<Renderer>(true).Where(r => r != null);
        }
    }
}
