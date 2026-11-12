using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using UnityEngine;

namespace VRoidPlus
{
    /// <summary>
    /// Access to VRoid Studio 2.x's own editing API, found through diagnostics:
    ///   AvatarEditor._viewModel.CurrentFile.model.engine.Context  (VRoidCore.Editing.Context)
    /// The open model is a set of "transferable groups" (items) with a type such as
    /// "TransferableGroupType.N00.Level1.Nose" and named parameters (sliders and colors).
    /// Everything goes through reflection, so VRoid-version differences show up as messages, not build errors.
    /// </summary>
    internal static class VRoidApi
    {
        public const string ContextPath = "$find:VRoidStudio.GUI.AvatarEditor.AvatarEditor::_viewModel.CurrentFile.model.engine.Context";

        private static object _ctx;
        private static string _ctxError;
        private static float _ctxTime = -10f;

        /// <summary>The open model's editing context (cached for a second: finding it scans the scene).</summary>
        public static object Context(out string error)
        {
            if (Time.unscaledTime - _ctxTime > 1f)
            {
                _ctxTime = Time.unscaledTime;
                try { _ctx = ReflectionPath.Resolve(ContextPath, out _ctxError); }
                catch (Exception e) { _ctx = null; _ctxError = e.Message; }
                if (_ctx == null) _ctxError = "no model is open (" + _ctxError + ")";
            }
            error = _ctxError;
            return _ctx;
        }

        /// <summary>Resolves a path below the AvatarEditor's view model, e.g. "CurrentFile.model.path".</summary>
        public static object FromViewModel(string rest, out string error) =>
            ReflectionPath.Resolve("$find:VRoidStudio.GUI.AvatarEditor.AvatarEditor::_viewModel." + rest, out error);

        public class Group
        {
            public string Id, Type;
            public string ResourceName;
            public object Info;
        }

        public class Param
        {
            public string Key;
            public string Kind;          // BoneTransform, BlendShapeTransform, Color, ...
            public float Value;
            public Color Color;
        }

        public static List<Group> Groups(object ctx)
        {
            var list = new List<Group>();
            foreach (var (k, v) in Explorer.Pairs(Call(ctx, "GetActiveLeafTransferableGroups")))
            {
                if (k == null) continue;
                list.Add(new Group
                {
                    Id = k.ToString(),
                    Type = Get(v, "Type") as string ?? Call(ctx, "GetTypeOfTransferableGroup", k.ToString()) as string ?? "",
                    ResourceName = Get(v, "ResourceName") as string,
                    Info = v,
                });
            }
            return list;
        }

        public static List<Param> Parameters(object ctx, string groupId)
        {
            var list = new List<Param>();
            foreach (var (k, v) in Explorer.Pairs(Call(ctx, "GetParametersForTransferableGroup", groupId)))
            {
                if (k == null || v == null) continue;
                var p = new Param { Key = k.ToString(), Kind = Get(v, "Type")?.ToString() ?? "" };
                var bone = Get(v, "boneTransform"); var blend = Get(v, "blendShapeTransform"); var color = Get(v, "color");
                if (bone != null) { p.Kind = "BoneTransform"; p.Value = Convert.ToSingle(Get(bone, "Value") ?? 0f); }
                else if (blend != null) { p.Kind = "BlendShapeTransform"; p.Value = Convert.ToSingle(Get(blend, "Value") ?? 0f); }
                else if (color != null && Get(color, "Color") is Color c) { p.Kind = "Color"; p.Color = c; }
                else continue;
                // The parameter list describes each slider (its value there is the default); ask the model
                // for the value actually set on this avatar.
                ReadCurrent(ctx, groupId, p);
                list.Add(p);
            }
            return list;
        }

        /// <summary>
        /// Sets one parameter on an item. Uses the data model's per-parameter setters
        /// (VRoidCore.Common.SpecificTypes.Model.SetBoneTransformParameter etc.), then asks VRoid to rebuild.
        /// Returns null on success, otherwise why it failed.
        /// </summary>
        public static string SetParameter(object ctx, string groupId, Param p)
        {
            var model = Get(ctx, "ActiveModel");
            if (model == null) return "no active model";
            try
            {
                switch (p.Kind)
                {
                    case "BoneTransform":
                        return InvokeSetter(model, "SetBoneTransformParameter", groupId,
                            MakePath("VRoidCore.Common.TransferableTypes.Path.BoneTransformPath", "VRoidCore.Common.TransferableTypes.Path.IBoneTransformPath", p.Key), p.Value, true);
                    case "BlendShapeTransform":
                        return InvokeSetter(model, "SetBlendShapeTransformParameter", groupId,
                            MakePath("VRoidCore.Common.TransferableTypes.Path.BlendShapeTransformPath", "VRoidCore.Common.TransferableTypes.Path.IBlendShapeTransformPath", p.Key), p.Value, false);
                    case "Color":
                        return InvokeSetter(model, "SetColorParameter", groupId,
                            MakePath("VRoidCore.Common.TransferableTypes.Path.ColorPath", "VRoidCore.Common.TransferableTypes.Path.IColorPath", p.Key), p.Color, false);
                }
            }
            catch (Exception e) { return (e is TargetInvocationException t ? t.InnerException ?? e : e).Message; }
            return "unsupported kind " + p.Kind;
        }

        /// <summary>Overwrites p.Value / p.Color with the avatar's current setting (Model.Get…Parameter).</summary>
        private static void ReadCurrent(object ctx, string groupId, Param p)
        {
            var model = Get(ctx, "ActiveModel");
            if (model == null) return;
            try
            {
                switch (p.Kind)
                {
                    case "BoneTransform":
                    {
                        var v = InvokeGetter(model, "GetBoneTransformParameter", groupId,
                            MakePath("VRoidCore.Common.TransferableTypes.Path.BoneTransformPath", "VRoidCore.Common.TransferableTypes.Path.IBoneTransformPath", p.Key), true);
                        if (v != null) p.Value = Convert.ToSingle(v);
                        break;
                    }
                    case "BlendShapeTransform":
                    {
                        var v = InvokeGetter(model, "GetBlendShapeTransformParameter", groupId,
                            MakePath("VRoidCore.Common.TransferableTypes.Path.BlendShapeTransformPath", "VRoidCore.Common.TransferableTypes.Path.IBlendShapeTransformPath", p.Key), false);
                        if (v != null) p.Value = Convert.ToSingle(v);
                        break;
                    }
                    case "Color":
                    {
                        var v = InvokeGetter(model, "GetColorParameter", groupId,
                            MakePath("VRoidCore.Common.TransferableTypes.Path.ColorPath", "VRoidCore.Common.TransferableTypes.Path.IColorPath", p.Key), false);
                        if (v is Color c) p.Color = c;
                        break;
                    }
                }
            }
            catch (Exception e)
            {
                _readFailures++;
                if (_readFailures <= 3) Plugin.Logger.LogWarning($"Reading {p.Key}: {(e is TargetInvocationException t ? t.InnerException ?? e : e).Message}");
            }
        }

        private static int _readFailures;

        private static object InvokeGetter(object model, string method, string groupId, object path, bool hasTemplateArg)
        {
            if (path == null) return null;
            var m = model.GetType().GetMethods(BindingFlags.Instance | BindingFlags.Public)
                .FirstOrDefault(x => x.Name == method && x.GetParameters().Length == (hasTemplateArg ? 3 : 2));
            if (m == null) return null;
            return m.Invoke(model, hasTemplateArg ? new[] { groupId, path, null } : new[] { groupId, path });
        }

        private static string InvokeSetter(object model, string method, string groupId, object path, object value, bool hasTemplateArg)
        {
            if (path == null) return "could not create a path for the parameter";
            var m = model.GetType().GetMethods(BindingFlags.Instance | BindingFlags.Public)
                .FirstOrDefault(x => x.Name == method && x.GetParameters().Length == (hasTemplateArg ? 4 : 3));
            if (m == null) return method + " not found";
            var args = hasTemplateArg ? new[] { groupId, path, value, null } : new[] { groupId, path, value };
            m.Invoke(model, args);
            return null;
        }

        /// <summary>Creates e.g. a BoneTransformPath for a key, cast to its interface type.</summary>
        private static object MakePath(string concreteName, string interfaceName, string key)
        {
            var concrete = ReflectionPath.FindType(concreteName);
            var iface = ReflectionPath.FindType(interfaceName);
            if (concrete == null) return null;
            object obj = null;
            var ctor = concrete.GetConstructor(new[] { typeof(string) });
            if (ctor != null) obj = ctor.Invoke(new object[] { key });
            else
            {
                // Some versions only have a static factory.
                var f = concrete.GetMethods(BindingFlags.Static | BindingFlags.Public)
                    .FirstOrDefault(x => x.ReturnType == concrete && x.GetParameters().Length == 1 && x.GetParameters()[0].ParameterType == typeof(string));
                obj = f?.Invoke(null, new object[] { key });
            }
            if (obj == null) return null;
            return iface != null ? Compat.CastTo(obj, iface) ?? obj : obj;
        }

        /// <summary>Tells VRoid the model changed so it rebuilds the 3D model and refreshes the sliders.</summary>
        public static void NotifyChanged(object ctx, IEnumerable<string> groupIds)
        {
            Call(ctx, "MarkNeedsUpdate", true, false);
            foreach (var id in groupIds) Call(ctx, "InvokeTranferableGroupParameterUpdated", id);
        }

        // ------------------------------------------------------------------ hair groups

        public class HairGroupInfo
        {
            public string ItemPrefix;   // bone name prefix of the hair item, e.g. "e116d2fb…" (before ".transferable_")
            public string Slot;         // HairFront, HairBack, BaseHair, HairSideburns, ...
            public string GroupId;
            public string Name;
            public bool HasPhysicsBones;
            public readonly HashSet<string> StrandIds = new();   // normalised: lower case, no dashes
        }

        public static string NormId(string id) => (id ?? "").Replace("-", "").ToLowerInvariant();

        /// <summary>
        /// Every hair group of every hair item on the open model, with its strand ids, read from VRoid's data
        /// (item -> Collection.Addition.Hair.HairGroups -> Brush/ProceduralHairGroup -> strands).
        /// Returns null (with error) if VRoid's data couldn't be read.
        /// </summary>
        public static List<HairGroupInfo> HairGroups(object ctx, out string error)
        {
            error = null;
            var result = new List<HairGroupInfo>();
            try
            {
                foreach (var g in Groups(ctx).Where(x => x.Type.Contains("Hair")))
                {
                    var slot = g.Type.Substring(g.Type.LastIndexOf('.') + 1);
                    var transferables = Call(ctx, "CloneAndGetAllTransferablesInTransferableGroup", g.Id);
                    if (transferables == null || !ListAccess.IsList(transferables)) continue;
                    for (int t = 0; t < ListAccess.Count(transferables); t++)
                    {
                        var tr = ListAccess.Get(transferables, t);
                        var trId = Get(Get(tr, "Header"), "Id") as string ?? "";
                        var prefix = trId.EndsWith(".transferable") ? trId.Substring(0, trId.Length - ".transferable".Length) : trId;
                        var hair = Get(Get(Get(tr, "Collection"), "Addition"), "Hair");
                        var groups = Get(hair, "HairGroups");
                        if (groups == null) continue;
                        int n = Convert.ToInt32(Get(groups, "Count") ?? 0);
                        for (int i = 0; i < n; i++)
                        {
                            var hg = Call(groups, "GetNthValue", i);
                            var body = Get(hg, "BrushHairGroup") ?? Get(hg, "ProceduralHairGroup");
                            if (body == null) continue;
                            var info = new HairGroupInfo
                            {
                                ItemPrefix = prefix, Slot = slot,
                                GroupId = Get(body, "Id") as string ?? $"{prefix}#{i}",
                                Name = DisplayName(Get(body, "Name")) ?? $"Group {i + 1}",
                            };
                            CollectStrandIds(Get(body, "_hairs"), info.StrandIds);
                            result.Add(info);
                        }
                    }
                }
            }
            catch (Exception e) { error = (e is TargetInvocationException t ? t.InnerException ?? e : e).Message; return null; }
            if (result.Count == 0) { error = "no hair groups found in VRoid's data"; return null; }
            return result;
        }

        /// <summary>Strand ids from a BrushHairGroup's grouplets (paired/unpaired) or a ProceduralHairGroup's hairs.</summary>
        private static void CollectStrandIds(object hairs, HashSet<string> into)
        {
            if (hairs == null || !ListAccess.IsList(hairs)) return;
            for (int i = 0; i < ListAccess.Count(hairs); i++)
            {
                var h = ListAccess.Get(hairs, i);
                if (h == null) continue;
                // Procedural strand, or brush grouplet holding one or two strands.
                var direct = Get(h, "ControlPoints") != null ? Get(h, "Id") as string : null;
                if (direct != null) { into.Add(NormId(direct)); continue; }
                var unpaired = Get(Get(h, "UnpairedGrouplet"), "Hair");
                var paired = Get(h, "PairedGrouplet");
                foreach (var strand in new[] { unpaired, Get(paired, "Hair0"), Get(paired, "Hair1") })
                    if (Get(strand, "Id") is string sid) into.Add(NormId(sid));
            }
        }

        /// <summary>A VRoid LocalizedString as text (user-defined name, then translation, then key).</summary>
        public static string DisplayName(object localized)
        {
            if (localized == null) return null;
            foreach (var m in new[] { "UserDefinedString", "FallbackString", "TranslatedString", "LocalizationKey", "Value" })
            {
                if (Get(localized, m) is string str && !string.IsNullOrWhiteSpace(str)) return str;
            }
            var proto = Call(localized, "ToProtobuf");
            foreach (var m in new[] { "UserDefinedString", "FallbackString", "LocalizationKey" })
                if (Get(proto, m) is string str && !string.IsNullOrWhiteSpace(str)) return str;
            return null;
        }

        // ------------------------------------------------------------------ reflection helpers

        public static object Get(object o, string member)
        {
            if (o == null) return null;
            ReflectionPath.TryGetMember(o, o.GetType(), false, member, false, out var v, out _);
            return v;
        }

        public static object Call(object o, string method, params object[] args)
        {
            if (o == null) return null;
            var m = o.GetType().GetMethods(BindingFlags.Instance | BindingFlags.Public)
                .FirstOrDefault(x => x.Name == method && x.GetParameters().Length == args.Length && !x.IsGenericMethodDefinition);
            if (m == null) return null;
            try { return m.Invoke(o, args); }
            catch (TargetInvocationException e) { Plugin.Logger.LogWarning($"{method}: {e.InnerException?.Message}"); return null; }
        }
    }
}
