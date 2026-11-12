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
        /// <summary>Bone VRoid had it on, so Reset can put it back even after VRoid rebuilt the model.</summary>
        public string OriginalBone = "";
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
        private float _nextCheck;

        private const string Marker = ".transferable_";

        public BoneAnchors(Kind which) { Which = which; }

        /// <summary>Rules belong to the open .vroid file and are stored only when VRoid saves it (ModelFile).</summary>
        public List<AnchorRule> Rules => Which == Kind.Hair ? ModelFile.Current.HairAnchors : ModelFile.Current.AccessoryAnchors;

        public void Save() { /* kept in memory until the .vroid file is saved */ }

        /// <summary>Why hair is listed per item instead of per group (empty when groups were read fine).</summary>
        public string GroupNote = "";
        private List<VRoidApi.HairGroupInfo> _groupCache;
        private float _groupCacheTime = -10f;

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
            if (Which == Kind.Hair && !showAll)
            {
                var groups = HairGroupItems();
                if (groups != null) return groups;
            }
            return FindItemsByBones(showAll);
        }

        /// <summary>
        /// One entry per hair group as VRoid's hair editor lists them (with or without physics), matched to the
        /// "HairStrand-&lt;strandId&gt;" meshes VRoid builds for its strands.
        /// </summary>
        private List<Item> HairGroupItems()
        {
            var ctx = VRoidApi.Context(out var err);
            if (ctx == null) { GroupNote = err; return null; }
            if (_groupCache == null || Time.unscaledTime - _groupCacheTime > 3f)
            {
                _groupCacheTime = Time.unscaledTime;
                _groupCache = VRoidApi.HairGroups(ctx, out err);
                if (_groupCache == null) { GroupNote = "VRoid's hair group data couldn't be read (" + err + "), so hair is listed per part."; return null; }
            }
            GroupNote = "";

            var strandMeshes = new Dictionary<string, SkinnedMeshRenderer>();
            foreach (var r in Avatar.Renderers())
            {
                var n = r.gameObject.name;
                if (!n.StartsWith("HairStrand-", StringComparison.Ordinal)) continue;
                var smr = Compat.As<SkinnedMeshRenderer>(r);
                if (smr != null) strandMeshes[VRoidApi.NormId(n.Substring("HairStrand-".Length))] = smr;
            }

            var items = new List<Item>();
            foreach (var g in _groupCache)
            {
                var it = new Item { Id = "hairgroup:" + g.GroupId, IsHair = true };
                var seenRoots = new HashSet<int>();
                foreach (var sid in g.StrandIds)
                {
                    if (!strandMeshes.TryGetValue(sid, out var smr)) continue;
                    it.Meshes.Add(smr);
                    var bones = smr.bones;
                    for (int i = 0; i < bones.Length; i++)
                    {
                        var b = bones[i];
                        if (b == null || Avatar.IsHumanoid(b) || !b.name.Contains("HairJoint-")) continue;
                        var root = b;
                        while (root.parent != null && root.parent.name.Contains("HairJoint-")) root = root.parent;
                        if (seenRoots.Add(root.GetInstanceID())) it.Roots.Add(root);
                    }
                }
                if (it.Meshes.Count == 0) continue;   // hidden group, or not built yet
                it.CurrentAnchor = it.Roots.Count > 0 ? it.Roots[0].parent : CurrentMeshAnchor(it.Meshes[0]);
                it.Label = $"{SlotLabel(g.Slot)} > {g.Name}  ({it.Meshes.Count} strands{(it.Roots.Count > 0 ? ", with physics bones" : "")})";
                items.Add(it);
            }
            LastGroupInfo = $"{_groupCache.Count} hair groups in VRoid's data, {strandMeshes.Count} strand meshes, {items.Count} groups matched to meshes";
            if (items.Count == 0)
            {
                GroupNote = "VRoid's hair groups didn't match the model's strands (" + LastGroupInfo + "), so hair is listed per part.";
                return null;
            }
            return items;
        }

        public string LastGroupInfo = "";

        private static Transform CurrentMeshAnchor(SkinnedMeshRenderer smr)
        {
            var bones = smr.bones;
            for (int i = 0; i < bones.Length; i++)
            {
                var b = bones[i];
                if (b == null) continue;
                if (b.name.StartsWith("VRoidPlus_StandIn_", StringComparison.Ordinal)) return b.parent;
                if (Avatar.IsHumanoid(b)) return b;
            }
            return null;
        }

        private static string SlotLabel(string slot) => slot switch
        {
            "HairFront" => "Front hair",
            "HairBack" => "Back hair",
            "BaseHair" => "Base hair",
            "HairSideburns" => "Side hair",
            "HairHanege" => "Extensions",
            "HairAhoge" => "Ahoge",
            _ => slot.StartsWith("Hair") ? slot.Substring(4) + " hair" : slot,
        };

        /// <summary>Items found from bone names alone (accessories; hair when VRoid's group data can't be read).</summary>
        private List<Item> FindItemsByBones(bool showAll)
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

        public AnchorRule RuleFor(string itemId) => Rules.FirstOrDefault(x => x.Item == itemId);

        public void SetRule(Item item, Transform target)
        {
            var old = RuleFor(item.Id);
            Rules.RemoveAll(x => x.Item == item.Id);
            if (target != null)
            {
                var original = old?.OriginalBone;
                if (string.IsNullOrEmpty(original)) original = item.CurrentAnchor != null ? item.CurrentAnchor.name : "";
                Rules.Add(new AnchorRule { Item = item.Id, TargetBone = target.name, OriginalBone = original });
                Save();
                Apply(item, target);
            }
            else
            {
                Save();
                var original = old?.OriginalBone;
                if (string.IsNullOrEmpty(original) && item.IsHair) original = "J_Bip_C_Head";
                Reset(item, original);
                // Ask VRoid to rebuild the model too: that puts everything exactly where VRoid wants it.
                var ctx = VRoidApi.Context(out _);
                if (ctx != null) VRoidApi.Call(ctx, "MarkNeedsUpdate", true, false);
            }
        }

        // ------------------------------------------------------------------ apply / reset
        // Keyed by Unity instance id: IL2CPP hands out a new wrapper object each time the same transform is
        // fetched, so the transforms themselves are not reliable dictionary keys.

        /// <summary>instance id of a moved transform -> the bone it was under before we moved it</summary>
        private readonly Dictionary<int, Transform> _originalParent = new();
        /// <summary>stand-in transforms per item: original bone's instance id -> stand-in</summary>
        private readonly Dictionary<string, Dictionary<int, Transform>> _standIns = new();

        private void Move(Transform t, Transform target)
        {
            if (t == null || t == target || target.IsChildOf(t)) return;
            var id = t.GetInstanceID();
            if (!_originalParent.ContainsKey(id)) _originalParent[id] = t.parent;
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
            if (!_standIns.TryGetValue(item.Id, out var map)) _standIns[item.Id] = map = new Dictionary<int, Transform>();
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
                    var key = b.GetInstanceID();
                    if (!map.TryGetValue(key, out var standIn) || standIn == null)
                    {
                        var go = new GameObject();
                        go.name = $"VRoidPlus_StandIn_{b.name}_{item.Id}";
                        standIn = go.transform;
                        standIn.SetParent(b, false);          // same pose as the original bone...
                        standIn.SetParent(target, true);      // ...then follow the target from here on
                        map[key] = standIn;
                    }
                    else if (standIn.parent != target) standIn.SetParent(target, true);
                    arr[i] = standIn;
                    changed = true;
                }
                if (changed) Compat.SetBones(smr, arr);
            }
        }

        private void Reset(Item item, string originalBoneName)
        {
            Transform byName = null;
            if (!string.IsNullOrEmpty(originalBoneName))
                foreach (var b in Avatar.AllBones) if (b != null && b.name == originalBoneName) { byName = b; break; }

            void Restore(Transform t)
            {
                if (t == null) return;
                var id = t.GetInstanceID();
                _originalParent.TryGetValue(id, out var orig);
                if (orig == null) orig = byName;
                if (orig != null && t.parent != orig) t.SetParent(orig, true);
                _originalParent.Remove(id);
            }
            Restore(item.PartOrigin);
            foreach (var r in item.Roots) Restore(r);

            // Put the real bones back into the strand meshes in place of our stand-ins.
            var standInNames = "VRoidPlus_StandIn_";
            foreach (var smr in item.Meshes)
            {
                if (smr == null) continue;
                var bones = smr.bones;
                var arr = new Transform[bones.Length];
                bool changed = false;
                for (int i = 0; i < arr.Length; i++)
                {
                    arr[i] = bones[i];
                    if (bones[i] == null || !bones[i].name.StartsWith(standInNames, StringComparison.Ordinal)) continue;
                    // "VRoidPlus_StandIn_<bone>_<itemId>" -> <bone>
                    var rest = bones[i].name.Substring(standInNames.Length);
                    var cut = rest.LastIndexOf("_" + item.Id, StringComparison.Ordinal);
                    var boneName = cut > 0 ? rest.Substring(0, cut) : rest;
                    foreach (var b in Avatar.AllBones) if (b != null && b.name == boneName && !b.name.StartsWith(standInNames, StringComparison.Ordinal)) { arr[i] = b; changed = true; break; }
                }
                if (changed) Compat.SetBones(smr, arr);
            }
            if (_standIns.TryGetValue(item.Id, out var map))
            {
                foreach (var s in map.Values) if (s != null) UnityEngine.Object.Destroy(s.gameObject);
                _standIns.Remove(item.Id);
            }
        }

        /// <summary>VRoid rebuilds items when you edit them, so rules are re-applied every second.</summary>
        public void Update()
        {
            if (Rules.Count == 0 || Time.unscaledTime < _nextCheck) return;
            _nextCheck = Time.unscaledTime + 1f;
            if (!Avatar.Found) return;

            foreach (var key in _originalParent.Where(kv => kv.Value == null).Select(kv => kv.Key).ToList()) _originalParent.Remove(key);
            var bonesByName = new Dictionary<string, Transform>();
            foreach (var b in Avatar.AllBones) if (b != null && !bonesByName.ContainsKey(b.name)) bonesByName[b.name] = b;

            var all = FindItems(false);
            if (Which == Kind.Hair) all.AddRange(FindItemsByBones(true).Where(i => all.All(a => a.Id != i.Id)));
            foreach (var item in all)
            {
                var rule = RuleFor(item.Id);
                if (rule == null || !bonesByName.TryGetValue(rule.TargetBone, out var target)) continue;
                Apply(item, target);
            }
        }
    }
}
