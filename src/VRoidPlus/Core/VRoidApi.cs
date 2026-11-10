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

        public static object Context(out string error)
        {
            var ctx = ReflectionPath.Resolve(ContextPath, out error);
            if (ctx == null) error = "no model is open (" + error + ")";
            return ctx;
        }

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
