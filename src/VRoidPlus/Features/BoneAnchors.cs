using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEngine;

namespace VRoidPlus
{
    public class AnchorRule
    {
        /// <summary>VRoid item id (the prefix of its bone names, before ".transferable_").</summary>
        public string Item = "";
        /// <summary>Bone the item should follow (transform name).</summary>
        public string TargetBone = "";
        /// <summary>Old (v0.1) per-mesh rules; ignored.</summary>
        public string Renderer;
    }

    public class AnchorRuleFile
    {
        public List<AnchorRule> Rules = new();
    }

    /// <summary>
    /// Lets a hair part or an accessory follow any bone instead of the one VRoid attaches it to.
    ///
    /// How VRoid 2.x builds the model (from diagnostics): every item's bones are named
    /// "&lt;itemId&gt;.transferable_&lt;bone&gt;". Accessories hang from "&lt;itemId&gt;.transferable_PartOrigin";
    /// hair strands are skinned to the head bone plus "HairJoint-…-1/2/3" chains under the head.
    ///
    /// Accessories: the PartOrigin is re-parented to the chosen bone (keeping its current position).
    /// Hair: the HairJoint chains are re-parented, and in the strand meshes the head bone is swapped for a
    /// stand-in transform under the chosen bone, so the part of each strand weighted to the head moves too
    /// instead of stretching. VRoid rebuilds items when you edit them; rules are re-applied every second.
    /// </summary>
    internal class BoneAnchors
    {
        public enum Kind { Hair, Accessory }

        public readonly Kind Which;
        private readonly string _file;
        public AnchorRuleFile Data;
        private float _nextCheck;

        private const string Marker = ".transferable_";

        public BoneAnchors(Kind which, string file)
        {
            Which = which;
            _file = Path.Combine(Plugin.DataDir, file);
            Data = Json.Read<AnchorRuleFile>(_file);
            Data.Rules.RemoveAll(r => string.IsNullOrEmpty(r.Item));
        }

        public void Save() => Safe.Run("Anchors.Save", () => Json.Write(_file, Data));

        public class Item
        {
            public string Id;
            public string Label;
            public bool IsHair, IsAccessory;
            public Transform PartOrigin;
            public readonly List<Transform> Roots = new();          // HairJoint chain roots (hair)
            public readonly List<SkinnedMeshRenderer> Meshes = new();
            public Transform CurrentAnchor;
        }

        public static string ItemIdOf(Transform t)
        {
            var n = t.name;
            var i = n.IndexOf(Marker, StringComparison.Ordinal);
            return i > 0 ? n.Substring(0, i) : null;
        }

        private static string LocalName(string boneName)
        {
            var i = boneName.IndexOf(Marker, StringComparison.Ordinal);
            return i > 0 ? boneName.Substring(i + Marker.Length) : boneName;
        }

        /// <summary>All items on the current avatar of this kind (or every item when showAll).</summary>
        public List<Item> FindItems(bool showAll)
        {
            var items = new Dictionary<string, Item>();
            if (!Avatar.Found) return new List<Item>();

            Item Get(string id)
            {
                if (!items.TryGetValue(id, out var it)) items[id] = it = new Item { Id = id };
                return it;
            }

            foreach (var t in Avatar.AllBones)
            {
                if (t == null) continue;
                var id = ItemIdOf(t);
                if (id == null) continue;
                var local = LocalName(t.name);
                var it = Get(id);
                if (local == "PartOrigin") it.PartOrigin = t;
                else if (local.StartsWith("HairJoint-", StringComparison.Ordinal))
                {
                    it.IsHair = true;
                    // chain root: its parent is not a HairJoint of the same item
                    var p = t.parent;
                    if (p == null || ItemIdOf(p) != id || !LocalName(p.name).StartsWith("HairJoint-", StringComparison.Ordinal)) it.Roots.Add(t);
                }
                else if (local.StartsWith("J_Opt_", StringComparison.Ordinal) || id.Contains("Accessory")) it.IsAccessory = true;
            }

            foreach (var r in Avatar.Renderers())
            {
                var smr = Compat.As<SkinnedMeshRenderer>(r);
                if (smr == null) continue;
                var bones = smr.bones;
                if (bones == null) continue;
                string id = null;
                for (int i = 0; i < bones.Length && id == null; i++) if (bones[i] != null) id = ItemIdOf(bones[i]);
                if (id != null && items.TryGetValue(id, out var it)) it.Meshes.Add(smr);
            }

            var result = new List<Item>();
            int hairNo = 0;
            foreach (var it in items.Values)
            {
                bool want = showAll || (Which == Kind.Hair ? it.IsHair : it.IsAccessory && !it.IsHair);
                if (!want) continue;
                if (it.IsHair) hairNo++;
                it.Label = MakeLabel(it, hairNo);
                it.CurrentAnchor = it.IsHair
                    ? (it.Roots.Count > 0 ? it.Roots[0].parent : null)
                    : (it.PartOrigin != null ? it.PartOrigin.parent : null);
                if (it.IsHair ? it.Roots.Count > 0 : it.PartOrigin != null) result.Add(it);
            }
            return result;
        }

        private static string MakeLabel(Item it, int hairNo)
        {
            var shortId = it.Id.Length > 8 ? it.Id.Substring(it.Id.Length - 8) : it.Id;
            if (it.IsHair) return $"Hair part {hairNo}  ({it.Meshes.Count} strands, {it.Roots.Count} bone chains)  #{shortId}";
            // Accessory: name it after its own bones, e.g. J_Opt_C_FoxTail1 -> FoxTail
            var names = new SortedSet<string>();
            foreach (var t in Avatar.AllBones)
            {
                if (t == null || ItemIdOf(t) != it.Id) continue;
                var local = LocalName(t.name);
                if (!local.StartsWith("J_Opt_", StringComparison.Ordinal) && !local.StartsWith("J_Sec_", StringComparison.Ordinal)) continue;
                var core = local.Substring(6);
                if (core.Length > 2 && core[1] == '_') core = core.Substring(2);       // side prefix C_/L_/R_
                core = core.Replace("_end", "").TrimEnd('0', '1', '2', '3', '4', '5', '6', '7', '8', '9', '_');
                if (core.Length > 0) names.Add(core);
            }
            var what = names.Count > 0 ? string.Join(", ", names.Take(3)) : "Accessory";
            return $"{what}  #{shortId}";
        }

        public AnchorRule RuleFor(string itemId) => Data.Rules.FirstOrDefault(x => x.Item == itemId);

        public void SetRule(Item item, Transform target)
        {
            Data.Rules.RemoveAll(x => x.Item == item.Id);
            if (target != null) Data.Rules.Add(new AnchorRule { Item = item.Id, TargetBone = target.name });
            Save();
            if (target != null) Apply(item, target); else Reset(item);
        }

        // ------------------------------------------------------------------ apply / reset

        /// <summary>root transform -> the bone it was under before we moved it</summary>
        private readonly Dictionary<Transform, Transform> _originalParent = new();
        /// <summary>stand-in transforms per item (one per original bone they replace)</summary>
        private readonly Dictionary<string, Dictionary<Transform, Transform>> _standIns = new();

        private void Move(Transform t, Transform target)
        {
            if (t == null || t == target || target.IsChildOf(t)) return;
            if (!_originalParent.ContainsKey(t)) _originalParent[t] = t.parent;
            if (t.parent != target) t.SetParent(target, true);
        }

        private void Apply(Item item, Transform target)
        {
            if (!item.IsHair)
            {
                Move(item.PartOrigin, target);
                return;
            }
            foreach (var root in item.Roots) Move(root, target);
            if (item.PartOrigin != null) Move(item.PartOrigin, target);

            // Strand vertices weighted straight to a humanoid bone (the head) follow a stand-in under the target.
            if (!_standIns.TryGetValue(item.Id, out var map)) _standIns[item.Id] = map = new Dictionary<Transform, Transform>();
            foreach (var smr in item.Meshes)
            {
                if (smr == null) continue;
                var bones = smr.bones;
                var arr = new Transform[bones.Length];
                bool changed = false;
                for (int i = 0; i < arr.Length; i++)
                {
                    var b = bones[i];
                    arr[i] = b;
                    if (b == null || !Avatar.IsHumanoid(b) || b == target) continue;
                    if (!map.TryGetValue(b, out var standIn) || standIn == null)
                    {
                        var go = new GameObject();
                        go.name = $"VRoidPlus_StandIn_{b.name}_{item.Id}";
                        standIn = go.transform;
                        standIn.SetParent(b, false);          // same pose as the original bone...
                        standIn.SetParent(target, true);      // ...then follow the target from here on
                        map[b] = standIn;
                    }
                    else if (standIn.parent != target) standIn.SetParent(target, true);
                    arr[i] = standIn;
                    changed = true;
                }
                if (changed) Compat.SetBones(smr, arr);
            }
        }

        private void Reset(Item item)
        {
            void Restore(Transform t)
            {
                if (t != null && _originalParent.TryGetValue(t, out var orig) && orig != null) t.SetParent(orig, true);
                if (t != null) _originalParent.Remove(t);
            }
            Restore(item.PartOrigin);
            foreach (var r in item.Roots) Restore(r);

            if (_standIns.TryGetValue(item.Id, out var map))
            {
                var back = map.Where(kv => kv.Value != null).ToDictionary(kv => kv.Value, kv => kv.Key);
                foreach (var smr in item.Meshes)
                {
                    if (smr == null) continue;
                    var bones = smr.bones;
                    var arr = new Transform[bones.Length];
                    bool changed = false;
                    for (int i = 0; i < arr.Length; i++)
                    {
                        arr[i] = bones[i];
                        if (bones[i] != null && back.TryGetValue(bones[i], out var orig)) { arr[i] = orig; changed = true; }
                    }
                    if (changed) Compat.SetBones(smr, arr);
                }
                foreach (var s in map.Values) if (s != null) UnityEngine.Object.Destroy(s.gameObject);
                _standIns.Remove(item.Id);
            }
        }

        /// <summary>VRoid rebuilds items when you edit them, so rules are re-applied every second.</summary>
        public void Update()
        {
            if (Data.Rules.Count == 0 || Time.unscaledTime < _nextCheck) return;
            _nextCheck = Time.unscaledTime + 1f;
            if (!Avatar.Found) return;

            foreach (var key in _originalParent.Keys.Where(k => k == null).ToList()) _originalParent.Remove(key);
            var bonesByName = new Dictionary<string, Transform>();
            foreach (var b in Avatar.AllBones) if (b != null && !bonesByName.ContainsKey(b.name)) bonesByName[b.name] = b;

            foreach (var item in FindItems(true))
            {
                var rule = RuleFor(item.Id);
                if (rule == null || !bonesByName.TryGetValue(rule.TargetBone, out var target)) continue;
                Apply(item, target);
            }
        }
    }
}
