using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEngine;

namespace VRoidPlus
{
    public class AnchorRule
    {
        /// <summary>Name of the renderer (hair group / accessory) the rule applies to.</summary>
        public string Renderer = "";
        /// <summary>Bone the part should follow (transform name).</summary>
        public string TargetBone = "";
    }

    public class AnchorRuleFile
    {
        public List<AnchorRule> Rules = new();
    }

    /// <summary>
    /// Re-parents the bones of a hair group or accessory from their default bone (usually Head) to any
    /// other bone on the model. The part keeps its current position and then follows the new bone.
    ///
    /// An "attachment root" is a non-humanoid bone whose parent is a humanoid bone: the top of a hair
    /// strand chain or of an accessory's own bones. Moving these moves everything skinned below them.
    /// </summary>
    internal class BoneAnchors
    {
        public readonly string Title;
        private readonly Func<string[]> _patterns;
        private readonly string _file;
        public AnchorRuleFile Data;

        /// <summary>attachment root -> the humanoid bone it was on before we moved it</summary>
        private readonly Dictionary<Transform, Transform> _originalParent = new();
        private float _nextCheck;

        public BoneAnchors(string title, string file, Func<string[]> patterns)
        {
            Title = title;
            _patterns = patterns;
            _file = Path.Combine(Plugin.DataDir, file);
            Data = Json.Read<AnchorRuleFile>(_file);
        }

        public void Save() => Safe.Run("Anchors.Save", () => Json.Write(_file, Data));

        public class Part
        {
            public Renderer Renderer;
            public string Name;
            public List<Transform> Roots = new();
            public Transform CurrentAnchor;
        }

        /// <summary>Hair groups / accessories found on the current avatar.</summary>
        public List<Part> FindParts(bool showAll)
        {
            var parts = new List<Part>();
            var patterns = _patterns();
            foreach (var r in Avatar.Renderers())
            {
                var n = r.gameObject.name;
                if (!showAll && !patterns.Any(p => n.Contains(p) || HasAncestorMatching(r.transform, patterns))) continue;
                var part = new Part { Renderer = r, Name = n };
                part.Roots = AttachmentRoots(r);
                if (part.Roots.Count == 0) continue;
                part.CurrentAnchor = part.Roots[0].parent;
                parts.Add(part);
            }
            return parts;
        }

        private static bool HasAncestorMatching(Transform t, string[] patterns)
        {
            var root = Avatar.Animator != null ? Avatar.Animator.transform : null;
            for (var p = t.parent; p != null && p != root; p = p.parent)
                if (patterns.Any(x => p.name.Contains(x))) return true;
            return false;
        }

        public List<Transform> AttachmentRoots(Renderer r)
        {
            var roots = new HashSet<Transform>();
            IEnumerable<Transform> sources;
            var smr = Compat.As<SkinnedMeshRenderer>(r);
            if (smr != null)
            {
                var list = new List<Transform>();
                foreach (var b in smr.bones) if (b != null) list.Add(b);
                sources = list;
            }
            else sources = new[] { r.transform };

            foreach (var b in sources)
            {
                if (Avatar.IsHumanoid(b)) continue; // vertices weighted straight to Head etc. can't be moved alone
                var t = b;
                while (t.parent != null && !Avatar.IsHumanoid(t.parent) && !IsMovedRoot(t)) t = t.parent;
                if (t.parent == null) continue;
                roots.Add(t);
            }
            return roots.ToList();
        }

        private bool IsMovedRoot(Transform t) => _originalParent.ContainsKey(t);

        public AnchorRule RuleFor(string renderer) => Data.Rules.FirstOrDefault(x => x.Renderer == renderer);

        public void SetRule(Part part, Transform target)
        {
            Data.Rules.RemoveAll(x => x.Renderer == part.Name);
            if (target != null) Data.Rules.Add(new AnchorRule { Renderer = part.Name, TargetBone = target.name });
            Save();
            if (target != null) Apply(part, target);
            else Reset(part);
        }

        private void Apply(Part part, Transform target)
        {
            foreach (var root in part.Roots)
            {
                if (root == null || root == target || target.IsChildOf(root)) continue;
                if (!_originalParent.ContainsKey(root)) _originalParent[root] = root.parent;
                if (root.parent != target) root.SetParent(target, true);
            }
        }

        private void Reset(Part part)
        {
            foreach (var root in part.Roots)
            {
                if (root != null && _originalParent.TryGetValue(root, out var orig) && orig != null)
                    root.SetParent(orig, true);
                _originalParent.Remove(root);
            }
        }

        /// <summary>VRoid rebuilds hair/accessory objects when you edit them, so rules are re-applied every second.</summary>
        public void Update()
        {
            if (Data.Rules.Count == 0 || Time.unscaledTime < _nextCheck) return;
            _nextCheck = Time.unscaledTime + 1f;
            if (!Avatar.Found) return;

            foreach (var key in _originalParent.Keys.Where(k => k == null).ToList()) _originalParent.Remove(key);
            var bonesByName = new Dictionary<string, Transform>();
            foreach (var b in Avatar.AllBones) if (b != null && !bonesByName.ContainsKey(b.name)) bonesByName[b.name] = b;

            foreach (var part in FindParts(true))
            {
                var rule = RuleFor(part.Name);
                if (rule == null || !bonesByName.TryGetValue(rule.TargetBone, out var target)) continue;
                Apply(part, target);
            }
        }
    }
}
