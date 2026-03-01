#if UNITY_EDITOR
using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using UnityEditor;
using UnityEngine;

namespace FloppyDogTools.Tools.PhysBoneToMagica2
{
    public static class PhysBoneToMagica2Utility
    {
        public struct ConvertResult
        {
            public int physBonesFound;
            public int magicaCreated;
            public int physBonesDeleted;
            public int physCollidersConverted;
        }

        /// <summary>
        /// Standalone converter: VRC PhysBone -> MagicaCloth2 MagicaCloth (forced Bone Cloth).
        /// Uses reflection to avoid hard references to either SDK.
        /// </summary>
        public static ConvertResult Convert(GameObject root, bool includeInactive, bool deletePhysBonesAfter)
        {
            var result = new ConvertResult();
            if (root == null) return result;

            var physBoneType =
                FindTypeInDomain("VRC.SDK3.Dynamics.PhysBone.Components.VRCPhysBone") ??
                FindTypeInDomain("VRCPhysBone");

            if (physBoneType == null)
            {
                Debug.LogWarning("PhysBone→Magica2: VRCPhysBone type not found.");
                return result;
            }

            var magicaClothType =
                FindTypeInDomain("MagicaCloth2.MagicaCloth") ??
                FindTypeInDomain("MagicaCloth");

            var magicaSphereColliderType =
                FindTypeInDomain("MagicaCloth2.MagicaSphereCollider") ??
                FindTypeInDomain("MagicaSphereCollider");
            var magicaCapsuleColliderType =
                FindTypeInDomain("MagicaCloth2.MagicaCapsuleCollider") ??
                FindTypeInDomain("MagicaCapsuleCollider");
            var magicaPlaneColliderType =
                FindTypeInDomain("MagicaCloth2.MagicaPlaneCollider") ??
                FindTypeInDomain("MagicaPlaneCollider");

            if (magicaClothType == null)
            {
                Debug.LogWarning("PhysBone→Magica2: MagicaCloth type not found. Is MagicaCloth2 installed?");
                return result;
            }

            var physBones = root.GetComponentsInChildren(physBoneType, includeInactive);
            result.physBonesFound = physBones != null ? physBones.Length : 0;

            if (result.physBonesFound == 0)
            {
                Debug.Log("PhysBone→Magica2: No PhysBones found under target.");
                return result;
            }

            Undo.RegisterFullObjectHierarchyUndo(root, "PhysBone → Magica2");

            var convertedColliderMap = new Dictionary<Component, Component>();

            foreach (var pb in physBones)
            {
                if (!(pb is Component physComp) || physComp == null) continue;

                var newComp = Undo.AddComponent(physComp.gameObject, magicaClothType) as Component;
                if (newComp == null) continue;

                // Resolve root: PhysBone rootTransform if present, else component transform
                var rootT = ResolvePhysBoneRootOrSelf(physComp);

                // Get SerializeData from MagicaCloth component
                object serializeData = TryGetSerializeData(magicaClothType, newComp);

                // Force Cloth Type = "Bone Cloth" (by display name)
                SetEnumMemberByDisplayBestEffort(
                    serializeData,
                    new[] { "clothType", "ClothType", "m_clothType", "m_ClothType" },
                    "Bone Cloth"
                );
                SetEnumMemberByDisplayBestEffort(
                    newComp,
                    new[] { "clothType", "ClothType", "m_clothType", "m_ClothType" },
                    "Bone Cloth"
                );

                // Set root list on SerializeData
                if (serializeData != null && rootT != null)
                {
                    SetTransformListMember(
                        serializeData,
                        new[]
                        {
                            "rootBones", "RootBones",
                            "rootTransforms", "RootTransforms",
                            "roots", "Roots",
                            "rootList", "RootList",
                            "root", "Root",
                            "rootTransform", "RootTransform"
                        },
                        new[] { rootT }
                    );

                    // Map PhysBone -> Magica Bone Cloth parameters (includes curve weighting)
                    MapPhysBoneToMagicaBoneCloth(physComp, serializeData);

                    // Convert PhysBone colliders and populate Magica cloth collider list
                    result.physCollidersConverted += MapPhysBoneColliders(
                        physComp,
                        serializeData,
                        convertedColliderMap,
                        magicaSphereColliderType,
                        magicaCapsuleColliderType,
                        magicaPlaneColliderType
                    );
                }

                result.magicaCreated++;
            }

            if (deletePhysBonesAfter && result.magicaCreated > 0)
            {
                foreach (var pb in physBones)
                {
                    if (pb is Component c && c != null)
                    {
                        Undo.DestroyObjectImmediate(c);
                        result.physBonesDeleted++;
                    }
                }
            }

            Debug.Log($"PhysBone→Magica2: Found {result.physBonesFound}, created {result.magicaCreated}, deleted {result.physBonesDeleted}, colliders converted {result.physCollidersConverted}.");
            return result;
        }

        // ======================================================
        // Root resolution
        // ======================================================
        private static Transform ResolvePhysBoneRootOrSelf(Component physBoneComp)
        {
            if (physBoneComp == null) return null;

            var explicitRoot = GetMember(physBoneComp, (Transform)null,
                "rootTransform", "RootTransform",
                "m_RootTransform", "m_rootTransform",
                "root", "Root",
                "m_Root", "m_root",
                "rootBone", "RootBone",
                "m_RootBone", "m_rootBone"
            );

            return explicitRoot != null ? explicitRoot : physBoneComp.transform;
        }

        // ======================================================
        // Mapping (focus: pull/spring/stiffness weighting)
        // ======================================================
        private static void MapPhysBoneToMagicaBoneCloth(Component physBone, object serializeData)
        {
            if (physBone == null || serializeData == null) return;

            // ---- PhysBone scalars (fallbacks) ----
            float pullScalar      = GetFloatMember(physBone, 0.5f, "pull", "Pull", "m_Pull");
            float stiffnessScalar = GetFloatMember(physBone, 0.5f, "stiffness", "Stiffness", "m_Stiffness");
            float springScalar    = GetFloatMember(physBone, 0.5f, "spring", "Spring", "m_Spring");

            float damping   = GetFloatMember(physBone, 0.2f, "damping", "Damping", "m_Damping");
            float gravity   = GetFloatMember(physBone, 0.0f, "gravity", "Gravity", "m_Gravity"); // some SDKs use float gravity
            Vector3 gravityVec = GetVector3Member(physBone, Vector3.zero, "gravity", "Gravity", "m_Gravity"); // some SDKs use Vector3 gravity
            float gravityFalloff = GetFloatMember(physBone, 0f, "gravityFalloff", "GravityFalloff", "m_GravityFalloff");

            float momentum  = GetFloatMember(physBone, 1f, "momentum", "Momentum", "m_Momentum");

            // ---- PhysBone curves (the real "weights") ----
            var pullCurve      = GetCurveMember(physBone, "pullCurve", "PullCurve", "m_PullCurve");
            var springCurve    = GetCurveMember(physBone, "springCurve", "SpringCurve", "m_SpringCurve");
            var stiffnessCurve = GetCurveMember(physBone, "stiffnessCurve", "StiffnessCurve", "m_StiffnessCurve");

            // ======================================================
            // MagicaCloth2 mapping based on your inspector:
            // - Force -> Stiffness         (use PhysBone Pull)
            // - Velocity Attenuation       (use PhysBone Momentum)
            // - Angle Restoration -> Stiffness (use PhysBone Stiffness)
            // - Force -> Gravity / Gravity Direction / Gravity Falloff
            // - Damping
            //
            // Magica often uses root/tip weighting rather than curves, so we sample t=0/t=1.
            // ======================================================

            // ---- Pull curve -> Force/Stiffness root/tip ----
            if (pullCurve != null)
            {
                float root = pullCurve.Evaluate(0f);
                float tip  = pullCurve.Evaluate(1f);

                SetFloatMemberBestEffort(serializeData, root,
                    "stiffnessRoot", "RootStiffness", "m_rootStiffness", "m_stiffnessRoot", "forceStiffnessRoot");
                SetFloatMemberBestEffort(serializeData, tip,
                    "stiffnessTip", "TipStiffness", "m_tipStiffness", "m_stiffnessTip", "forceStiffnessTip");

                // Also try generic names some versions use
                SetFloatMemberBestEffort(serializeData, root, "forceStiffnessRoot", "ForceStiffnessRoot");
                SetFloatMemberBestEffort(serializeData, tip,  "forceStiffnessTip",  "ForceStiffnessTip");
            }
            else
            {
                // Fallback scalar -> Force/Stiffness
                SetFloatMemberBestEffort(serializeData, pullScalar,
                    "stiffness", "Stiffness", "m_stiffness", "m_Stiffness",
                    "forceStiffness", "ForceStiffness", "m_forceStiffness");
            }

            // ---- Momentum -> Velocity Attenuation ----
            SetFloatMemberBestEffort(serializeData, momentum,
                "velocityAttenuation", "VelocityAttenuation", "m_velocityAttenuation", "m_VelocityAttenuation");

            // ---- Spring curve/scalar -> Elasticity-like (best-effort) ----
            // Some Magica versions expose "elasticity" in force section.
            if (springCurve != null)
            {
                float root = springCurve.Evaluate(0f);
                float tip  = springCurve.Evaluate(1f);

                SetFloatMemberBestEffort(serializeData, root,
                    "elasticityRoot", "RootElasticity", "m_rootElasticity", "m_elasticityRoot", "forceElasticityRoot");
                SetFloatMemberBestEffort(serializeData, tip,
                    "elasticityTip", "TipElasticity", "m_tipElasticity", "m_elasticityTip", "forceElasticityTip");
            }
            else
            {
                SetFloatMemberBestEffort(serializeData, springScalar,
                    "elasticity", "Elasticity", "m_elasticity", "m_Elasticity",
                    "forceElasticity", "ForceElasticity", "m_forceElasticity");
            }

            // ---- Stiffness curve -> Angle Restoration Stiffness root/tip ----
            if (stiffnessCurve != null)
            {
                float root = stiffnessCurve.Evaluate(0f);
                float tip  = stiffnessCurve.Evaluate(1f);

                SetFloatMemberBestEffort(serializeData, root,
                    "angleRestorationRootStiffness", "m_angleRestorationRootStiffness", "angleRestoreRootStiffness");
                SetFloatMemberBestEffort(serializeData, tip,
                    "angleRestorationTipStiffness", "m_angleRestorationTipStiffness", "angleRestoreTipStiffness");

                // Common alternate names
                SetFloatMemberBestEffort(serializeData, root, "angleRestorationStiffnessRoot", "AngleRestorationStiffnessRoot");
                SetFloatMemberBestEffort(serializeData, tip,  "angleRestorationStiffnessTip",  "AngleRestorationStiffnessTip");
            }
            else
            {
                // Fallback scalar
                SetFloatMemberBestEffort(serializeData, stiffnessScalar,
                    "angleRestorationStiffness", "AngleRestorationStiffness", "m_angleRestorationStiffness",
                    "angleRestoreStiffness", "AngleRestoreStiffness");
            }

            // ---- Damping ----
            SetFloatMemberBestEffort(serializeData, damping,
                "damping", "Damping", "m_damping", "m_Damping",
                "drag", "Drag", "m_drag");

            // ---- Gravity ----
            // If PhysBone gravity is Vector3, prefer it. Otherwise use scalar with default direction.
            if (gravityVec != Vector3.zero)
            {
                SetFloatMemberBestEffort(serializeData, gravityVec.magnitude,
                    "gravity", "Gravity", "m_gravity", "m_Gravity");
                SetVector3MemberBestEffort(serializeData, gravityVec.normalized,
                    "gravityDirection", "GravityDirection", "m_gravityDirection", "m_GravityDirection");
            }
            else
            {
                SetFloatMemberBestEffort(serializeData, gravity,
                    "gravity", "Gravity", "m_gravity", "m_Gravity");
                // leave direction default unless you want to force (0,-1,0)
            }

            SetFloatMemberBestEffort(serializeData, gravityFalloff,
                "gravityFalloff", "GravityFalloff", "m_gravityFalloff", "m_GravityFalloff");
        }

        private static int MapPhysBoneColliders(
            Component physBone,
            object serializeData,
            Dictionary<Component, Component> convertedColliderMap,
            Type magicaSphereColliderType,
            Type magicaCapsuleColliderType,
            Type magicaPlaneColliderType)
        {
            if (physBone == null || serializeData == null) return 0;

            var physColliders = GetListMember(physBone,
                "colliders", "Colliders", "m_Colliders",
                "colliderList", "ColliderList", "m_ColliderList");

            if (physColliders == null || physColliders.Count == 0) return 0;

            var convertedForCloth = new List<Component>();
            var convertedCount = 0;

            foreach (var raw in physColliders)
            {
                if (!(raw is Component physCollider) || physCollider == null) continue;

                if (!convertedColliderMap.TryGetValue(physCollider, out var magicaCollider) || magicaCollider == null)
                {
                    magicaCollider = ConvertSinglePhysCollider(
                        physCollider,
                        magicaSphereColliderType,
                        magicaCapsuleColliderType,
                        magicaPlaneColliderType);

                    convertedColliderMap[physCollider] = magicaCollider;
                    if (magicaCollider != null) convertedCount++;
                }

                if (magicaCollider != null)
                    convertedForCloth.Add(magicaCollider);
            }

            if (convertedForCloth.Count == 0) return convertedCount;

            TrySetListMemberBestEffort(
                serializeData,
                convertedForCloth,
                "colliders", "Colliders", "m_colliders", "m_Colliders",
                "collisionColliders", "CollisionColliders", "m_collisionColliders", "m_CollisionColliders");

            return convertedCount;
        }

        private static Component ConvertSinglePhysCollider(
            Component physCollider,
            Type magicaSphereColliderType,
            Type magicaCapsuleColliderType,
            Type magicaPlaneColliderType)
        {
            if (physCollider == null) return null;

            var shape = GetMember(physCollider, "", "shapeType", "ShapeType", "m_ShapeType");
            var shapeName = (shape ?? string.Empty).ToString();

            Type targetType = null;
            if (shapeName.IndexOf("capsule", StringComparison.OrdinalIgnoreCase) >= 0)
                targetType = magicaCapsuleColliderType;
            else if (shapeName.IndexOf("plane", StringComparison.OrdinalIgnoreCase) >= 0)
                targetType = magicaPlaneColliderType;
            else
                targetType = magicaSphereColliderType;

            if (targetType == null) return null;

            var existing = physCollider.GetComponent(targetType);
            var magicaCollider = existing != null
                ? existing
                : Undo.AddComponent(physCollider.gameObject, targetType) as Component;
            if (magicaCollider == null) return null;

            CopyColliderTransformAndCommonSettings(physCollider, magicaCollider);
            return magicaCollider;
        }

        private static void CopyColliderTransformAndCommonSettings(Component physCollider, Component magicaCollider)
        {
            if (physCollider == null || magicaCollider == null) return;

            float radius = GetFloatMember(physCollider, 0.05f, "radius", "Radius", "m_Radius");
            float height = GetFloatMember(physCollider, 0f, "height", "Height", "m_Height");
            Vector3 center = GetVector3Member(physCollider, Vector3.zero, "position", "Position", "m_Position", "center", "Center", "m_Center");
            Quaternion rotation = GetMember(physCollider, Quaternion.identity, "rotation", "Rotation", "m_Rotation");

            SetFloatMemberBestEffort(magicaCollider, radius,
                "radius", "Radius", "m_radius", "m_Radius",
                "size", "Size", "m_size", "m_Size");

            if (height > 0f)
            {
                SetFloatMemberBestEffort(magicaCollider, height,
                    "height", "Height", "m_height", "m_Height",
                    "length", "Length", "m_length", "m_Length");
            }

            SetVector3MemberBestEffort(magicaCollider, center,
                "center", "Center", "m_center", "m_Center",
                "offset", "Offset", "m_offset", "m_Offset",
                "position", "Position", "m_position", "m_Position");

            SetQuaternionMemberBestEffort(magicaCollider, rotation,
                "rotation", "Rotation", "m_rotation", "m_Rotation");

            // VRC uses bones to define capsule endpoints; copy endpoints if available.
            var rootTransform = GetMember(physCollider, (Transform)null,
                "rootTransform", "RootTransform", "m_RootTransform",
                "rootBone", "RootBone", "m_RootBone");
            var endTransform = GetMember(physCollider, (Transform)null,
                "endTransform", "EndTransform", "m_EndTransform",
                "tailTransform", "TailTransform", "m_TailTransform");

            if (rootTransform != null)
            {
                SetTransformMemberBestEffort(magicaCollider, rootTransform,
                    "rootTransform", "RootTransform", "m_rootTransform", "m_RootTransform",
                    "startTransform", "StartTransform", "m_startTransform", "m_StartTransform");
            }

            if (endTransform != null)
            {
                SetTransformMemberBestEffort(magicaCollider, endTransform,
                    "endTransform", "EndTransform", "m_endTransform", "m_EndTransform",
                    "tailTransform", "TailTransform", "m_tailTransform", "m_TailTransform");
            }
        }

        private static IList GetListMember(object obj, params string[] names)
        {
            if (obj == null || names == null) return null;

            const BindingFlags BF = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
            var t = obj.GetType();

            foreach (var n in names)
            {
                try
                {
                    var f = t.GetField(n, BF);
                    if (f?.GetValue(obj) is IList flist) return flist;

                    var p = t.GetProperty(n, BF);
                    if (p != null && p.CanRead && p.GetValue(obj, null) is IList plist) return plist;
                }
                catch { }
            }

            return null;
        }

        private static bool TrySetListMemberBestEffort(object obj, IList values, params string[] names)
        {
            if (obj == null || values == null || names == null) return false;

            const BindingFlags BF = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
            var t = obj.GetType();

            foreach (var n in names)
            {
                try
                {
                    var f = t.GetField(n, BF);
                    if (f != null && TryPopulateListObject(f.GetValue(obj), values)) return true;

                    var p = t.GetProperty(n, BF);
                    if (p != null && p.CanRead && TryPopulateListObject(p.GetValue(obj, null), values)) return true;
                }
                catch { }
            }

            return false;
        }

        private static bool TryPopulateListObject(object listObj, IList sourceValues)
        {
            var targetList = listObj as IList;
            if (targetList == null) return false;

            targetList.Clear();

            var listType = targetList.GetType();
            var elementType = GetListElementType(listType) ?? typeof(object);

            foreach (var value in sourceValues)
            {
                if (value == null)
                {
                    targetList.Add(null);
                    continue;
                }

                if (elementType.IsAssignableFrom(value.GetType()))
                    targetList.Add(value);
            }

            return true;
        }

        private static Type GetListElementType(Type listType)
        {
            if (listType == null) return null;

            if (listType.IsArray) return listType.GetElementType();
            if (listType.IsGenericType) return listType.GetGenericArguments()[0];

            foreach (var iface in listType.GetInterfaces())
            {
                if (iface.IsGenericType && iface.GetGenericTypeDefinition() == typeof(System.Collections.Generic.IList<>))
                    return iface.GetGenericArguments()[0];
            }

            return null;
        }

        // ======================================================
        // Reflection helpers
        // ======================================================
        private static Type FindTypeInDomain(string fullName)
        {
            if (string.IsNullOrEmpty(fullName)) return null;

            foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
            {
                try
                {
                    var t = asm.GetType(fullName, false);
                    if (t != null) return t;
                }
                catch { }
            }
            return null;
        }

        private static object TryGetSerializeData(Type magicaClothType, Component magicaClothComp)
        {
            if (magicaClothType == null || magicaClothComp == null) return null;

            try
            {
                const BindingFlags flags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;
                var sdProp = magicaClothType.GetProperty("SerializeData", flags);
                if (sdProp != null && sdProp.CanRead)
                    return sdProp.GetValue(magicaClothComp, null);
            }
            catch { }

            return null;
        }

        private static T GetMember<T>(object obj, T fallback, params string[] names)
        {
            if (obj == null || names == null) return fallback;

            const BindingFlags BF = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
            var t = obj.GetType();

            foreach (var n in names)
            {
                try
                {
                    var f = t.GetField(n, BF);
                    if (f != null)
                    {
                        var v = f.GetValue(obj);
                        if (v is T tv) return tv;
                    }

                    var p = t.GetProperty(n, BF);
                    if (p != null && p.CanRead)
                    {
                        var v = p.GetValue(obj, null);
                        if (v is T tv) return tv;
                    }
                }
                catch { }
            }

            return fallback;
        }

        private static float GetFloatMember(object obj, float fallback, params string[] names)
            => GetMember(obj, fallback, names);

        private static Vector3 GetVector3Member(object obj, Vector3 fallback, params string[] names)
            => GetMember(obj, fallback, names);

        private static AnimationCurve GetCurveMember(object obj, params string[] names)
            => GetMember(obj, (AnimationCurve)null, names);

        private static void SetTransformListMember(object obj, string[] names, Transform[] values)
        {
            if (obj == null || names == null || values == null) return;

            const BindingFlags BF = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
            var t = obj.GetType();

            foreach (var n in names)
            {
                try
                {
                    var f = t.GetField(n, BF);
                    if (f != null && TrySetListObject(f.GetValue(obj), values)) return;

                    var p = t.GetProperty(n, BF);
                    if (p != null && p.CanRead && TrySetListObject(p.GetValue(obj, null), values)) return;
                }
                catch { }
            }
        }

        private static bool TrySetListObject(object listObj, Transform[] values)
        {
            if (listObj is IList ilist)
            {
                ilist.Clear();
                foreach (var v in values) ilist.Add(v);
                return true;
            }
            return false;
        }

        private static void SetEnumMemberByDisplayBestEffort(object obj, string[] names, string displayName)
        {
            if (obj == null || names == null || string.IsNullOrEmpty(displayName)) return;

            const BindingFlags BF = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
            var t = obj.GetType();

            foreach (var n in names)
            {
                try
                {
                    var f = t.GetField(n, BF);
                    if (f != null && f.FieldType.IsEnum)
                    {
                        var val = FindEnumByInspectorNameOrToString(f.FieldType, displayName);
                        if (val != null) { f.SetValue(obj, val); return; }
                    }

                    var p = t.GetProperty(n, BF);
                    if (p != null && p.CanWrite && p.PropertyType.IsEnum)
                    {
                        var val = FindEnumByInspectorNameOrToString(p.PropertyType, displayName);
                        if (val != null) { p.SetValue(obj, val, null); return; }
                    }
                }
                catch { }
            }
        }

        private static object FindEnumByInspectorNameOrToString(Type enumType, string displayName)
        {
            try
            {
                foreach (var v in Enum.GetValues(enumType))
                {
                    var name = v.ToString();
                    if (string.Equals(name, displayName, StringComparison.OrdinalIgnoreCase))
                        return v;

                    var mi = enumType.GetMember(name);
                    if (mi != null && mi.Length > 0)
                    {
                        var insp = mi[0].GetCustomAttribute<InspectorNameAttribute>();
                        if (insp != null && string.Equals(insp.displayName, displayName, StringComparison.OrdinalIgnoreCase))
                            return v;
                    }
                }
            }
            catch { }

            try
            {
                var compact = displayName.Replace(" ", "");
                return Enum.Parse(enumType, compact, true);
            }
            catch { }

            return null;
        }

        private static void SetFloatMemberBestEffort(object obj, float value, params string[] names)
        {
            if (obj == null || names == null) return;

            const BindingFlags BF = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
            var t = obj.GetType();

            foreach (var n in names)
            {
                try
                {
                    var f = t.GetField(n, BF);
                    if (f != null && f.FieldType == typeof(float)) { f.SetValue(obj, value); return; }

                    var p = t.GetProperty(n, BF);
                    if (p != null && p.CanWrite && p.PropertyType == typeof(float)) { p.SetValue(obj, value, null); return; }
                }
                catch { }
            }
        }

        private static void SetVector3MemberBestEffort(object obj, Vector3 value, params string[] names)
        {
            if (obj == null || names == null) return;

            const BindingFlags BF = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
            var t = obj.GetType();

            foreach (var n in names)
            {
                try
                {
                    var f = t.GetField(n, BF);
                    if (f != null && f.FieldType == typeof(Vector3)) { f.SetValue(obj, value); return; }

                    var p = t.GetProperty(n, BF);
                    if (p != null && p.CanWrite && p.PropertyType == typeof(Vector3)) { p.SetValue(obj, value, null); return; }
                }
                catch { }
            }
        }

        private static void SetQuaternionMemberBestEffort(object obj, Quaternion value, params string[] names)
        {
            if (obj == null || names == null) return;

            const BindingFlags BF = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
            var t = obj.GetType();

            foreach (var n in names)
            {
                try
                {
                    var f = t.GetField(n, BF);
                    if (f != null && f.FieldType == typeof(Quaternion)) { f.SetValue(obj, value); return; }

                    var p = t.GetProperty(n, BF);
                    if (p != null && p.CanWrite && p.PropertyType == typeof(Quaternion)) { p.SetValue(obj, value, null); return; }
                }
                catch { }
            }
        }

        private static void SetTransformMemberBestEffort(object obj, Transform value, params string[] names)
        {
            if (obj == null || names == null || value == null) return;

            const BindingFlags BF = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
            var t = obj.GetType();

            foreach (var n in names)
            {
                try
                {
                    var f = t.GetField(n, BF);
                    if (f != null && f.FieldType == typeof(Transform)) { f.SetValue(obj, value); return; }

                    var p = t.GetProperty(n, BF);
                    if (p != null && p.CanWrite && p.PropertyType == typeof(Transform)) { p.SetValue(obj, value, null); return; }
                }
                catch { }
            }
        }
    }
}
#endif
