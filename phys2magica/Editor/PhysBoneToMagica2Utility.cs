#if UNITY_EDITOR
using System;
using System.Collections;
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
            public int advancedMappingsApplied;
            public int advancedMappingsSkipped;
        }

        private struct MappingStats
        {
            public int applied;
            public int skipped;
        }

        private const bool EnableVerboseMappingLogs = false;

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
                    var stats = MapPhysBoneToMagicaBoneCloth(physComp, serializeData);
                    result.advancedMappingsApplied += stats.applied;
                    result.advancedMappingsSkipped += stats.skipped;
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

            Debug.Log($"PhysBone→Magica2: Found {result.physBonesFound}, created {result.magicaCreated}, deleted {result.physBonesDeleted}, advanced mapped {result.advancedMappingsApplied}, advanced skipped {result.advancedMappingsSkipped}.");
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
        private static MappingStats MapPhysBoneToMagicaBoneCloth(Component physBone, object serializeData)
        {
            var stats = new MappingStats();
            if (physBone == null || serializeData == null) return stats;

            // ---- PhysBone scalars (fallbacks) ----
            float pullScalar      = GetFloatMember(physBone, 0.5f, "pull", "Pull", "m_Pull");
            float stiffnessScalar = GetFloatMember(physBone, 0.5f, "stiffness", "Stiffness", "m_Stiffness");
            float springScalar    = GetFloatMember(physBone, 0.5f, "spring", "Spring", "m_Spring");

            float damping   = GetFloatMember(physBone, 0.2f, "damping", "Damping", "m_Damping");
            float gravity   = GetFloatMember(physBone, 0.0f, "gravity", "Gravity", "m_Gravity"); // some SDKs use float gravity
            Vector3 gravityVec = GetVector3Member(physBone, Vector3.zero, "gravity", "Gravity", "m_Gravity"); // some SDKs use Vector3 gravity
            float gravityFalloff = GetFloatMember(physBone, 0f, "gravityFalloff", "GravityFalloff", "m_GravityFalloff");

            float momentum  = GetFloatMember(physBone, 1f, "momentum", "Momentum", "m_Momentum");

            // ---- Additional PhysBone advanced members ----
            float radius = GetFloatMember(physBone, 0f,
                "radius", "Radius", "m_Radius",
                "hitRadius", "HitRadius", "m_HitRadius",
                "colliderRadius", "ColliderRadius", "m_ColliderRadius");
            float immobile = GetFloatMember(physBone, 0f,
                "immobile", "Immobile", "m_Immobile",
                "worldInertia", "WorldInertia", "m_WorldInertia",
                "inertia", "Inertia", "m_Inertia");
            float maxAngle = GetFloatMember(physBone, 0f,
                "maxAngle", "MaxAngle", "m_MaxAngle",
                "maxStretchAngle", "MaxStretchAngle", "m_MaxStretchAngle",
                "angleLimit", "AngleLimit", "m_AngleLimit");
            float limit = GetFloatMember(physBone, maxAngle,
                "limit", "Limit", "m_Limit",
                "limitValue", "LimitValue", "m_LimitValue");
            float angleLimit = GetFloatMember(physBone, maxAngle,
                "angleLimit", "AngleLimit", "m_AngleLimit",
                "maxAngleX", "MaxAngleX", "m_MaxAngleX");

            object collidersObj = GetMember<object>(physBone, null,
                "colliders", "Colliders", "m_Colliders",
                "colliderList", "ColliderList", "m_ColliderList",
                "ignoreColliders", "IgnoreColliders", "m_IgnoreColliders");

            int sourceColliderCount = CountEnumerable(collidersObj);

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

                MapFloat(ref stats, serializeData, root,
                    "stiffnessRoot", "RootStiffness", "m_rootStiffness", "m_stiffnessRoot", "forceStiffnessRoot");
                MapFloat(ref stats, serializeData, tip,
                    "stiffnessTip", "TipStiffness", "m_tipStiffness", "m_stiffnessTip", "forceStiffnessTip");

                // Also try generic names some versions use
                MapFloat(ref stats, serializeData, root, "forceStiffnessRoot", "ForceStiffnessRoot");
                MapFloat(ref stats, serializeData, tip,  "forceStiffnessTip",  "ForceStiffnessTip");
            }
            else
            {
                // Fallback scalar -> Force/Stiffness
                MapFloat(ref stats, serializeData, pullScalar,
                    "stiffness", "Stiffness", "m_stiffness", "m_Stiffness",
                    "forceStiffness", "ForceStiffness", "m_forceStiffness");
            }

            // ---- Momentum -> Velocity Attenuation ----
            MapFloat(ref stats, serializeData, momentum,
                "velocityAttenuation", "VelocityAttenuation", "m_velocityAttenuation", "m_VelocityAttenuation");

            // ---- Spring curve/scalar -> Elasticity-like (best-effort) ----
            // Some Magica versions expose "elasticity" in force section.
            if (springCurve != null)
            {
                float root = springCurve.Evaluate(0f);
                float tip  = springCurve.Evaluate(1f);

                MapFloat(ref stats, serializeData, root,
                    "elasticityRoot", "RootElasticity", "m_rootElasticity", "m_elasticityRoot", "forceElasticityRoot");
                MapFloat(ref stats, serializeData, tip,
                    "elasticityTip", "TipElasticity", "m_tipElasticity", "m_elasticityTip", "forceElasticityTip");
            }
            else
            {
                MapFloat(ref stats, serializeData, springScalar,
                    "elasticity", "Elasticity", "m_elasticity", "m_Elasticity",
                    "forceElasticity", "ForceElasticity", "m_forceElasticity");
            }

            // ---- Stiffness curve -> Angle Restoration Stiffness root/tip ----
            if (stiffnessCurve != null)
            {
                float root = stiffnessCurve.Evaluate(0f);
                float tip  = stiffnessCurve.Evaluate(1f);

                MapFloat(ref stats, serializeData, root,
                    "angleRestorationRootStiffness", "m_angleRestorationRootStiffness", "angleRestoreRootStiffness");
                MapFloat(ref stats, serializeData, tip,
                    "angleRestorationTipStiffness", "m_angleRestorationTipStiffness", "angleRestoreTipStiffness");

                // Common alternate names
                MapFloat(ref stats, serializeData, root, "angleRestorationStiffnessRoot", "AngleRestorationStiffnessRoot");
                MapFloat(ref stats, serializeData, tip,  "angleRestorationStiffnessTip",  "AngleRestorationStiffnessTip");
            }
            else
            {
                // Fallback scalar
                MapFloat(ref stats, serializeData, stiffnessScalar,
                    "angleRestorationStiffness", "AngleRestorationStiffness", "m_angleRestorationStiffness",
                    "angleRestoreStiffness", "AngleRestoreStiffness");
            }

            // ---- Damping ----
            MapFloat(ref stats, serializeData, damping,
                "damping", "Damping", "m_damping", "m_Damping",
                "drag", "Drag", "m_drag");

            // ---- Gravity ----
            // If PhysBone gravity is Vector3, prefer it. Otherwise use scalar with default direction.
            if (gravityVec != Vector3.zero)
            {
                MapFloat(ref stats, serializeData, gravityVec.magnitude,
                    "gravity", "Gravity", "m_gravity", "m_Gravity");
                SetVector3MemberBestEffort(serializeData, gravityVec.normalized,
                    "gravityDirection", "GravityDirection", "m_gravityDirection", "m_GravityDirection");
            }
            else
            {
                MapFloat(ref stats, serializeData, gravity,
                    "gravity", "Gravity", "m_gravity", "m_Gravity");
                // leave direction default unless you want to force (0,-1,0)
            }

            MapFloat(ref stats, serializeData, gravityFalloff,
                "gravityFalloff", "GravityFalloff", "m_gravityFalloff", "m_GravityFalloff");

            // ---- Advanced best-effort mappings ----
            MapFloat(ref stats, serializeData, radius,
                "radius", "Radius", "m_radius", "m_Radius",
                "collisionRadius", "CollisionRadius", "m_collisionRadius");
            MapFloatByKeywords(ref stats, serializeData, radius, "radius");
            MapFloatByKeywords(ref stats, serializeData, radius, "collision", "radius");

            MapFloat(ref stats, serializeData, immobile,
                "worldInertia", "WorldInertia", "m_worldInertia", "m_WorldInertia",
                "inertia", "Inertia", "m_inertia");
            MapFloatByKeywords(ref stats, serializeData, immobile, "inertia");
            MapFloatByKeywords(ref stats, serializeData, immobile, "stability");

            MapFloat(ref stats, serializeData, limit,
                "limit", "Limit", "m_limit", "m_Limit",
                "distanceLimit", "DistanceLimit", "m_distanceLimit");
            MapFloat(ref stats, serializeData, angleLimit,
                "angleLimit", "AngleLimit", "m_angleLimit", "m_AngleLimit",
                "maxAngle", "MaxAngle", "m_maxAngle");
            MapFloat(ref stats, serializeData, maxAngle,
                "maxAngle", "MaxAngle", "m_maxAngle", "m_MaxAngle");
            MapFloatByKeywords(ref stats, serializeData, maxAngle, "angle");
            MapFloatByKeywords(ref stats, serializeData, limit, "limit");

            MapEnum(ref stats, serializeData, "Point", "collisionMode", "CollisionMode", "m_collisionMode");
            MapEnumByKeywords(ref stats, serializeData, "Point", "collision");

            if (sourceColliderCount > 0)
            {
                var copied = SetComponentListMemberBestEffort(serializeData,
                    new[]
                    {
                        "colliders", "Colliders", "m_colliders",
                        "colliderList", "ColliderList", "m_colliderList",
                        "collisionColliders", "CollisionColliders", "m_collisionColliders"
                    },
                    collidersObj,
                    out var copiedCount);

                if (copied && copiedCount > 0) stats.applied += copiedCount;
                else stats.skipped += sourceColliderCount;
            }

            return stats;
        }

        private static void MapFloat(ref MappingStats stats, object obj, float value, params string[] names)
        {
            if (TrySetFloatMemberBestEffort(obj, value, names)) stats.applied++;
            else stats.skipped++;
        }

        private static void MapFloatByKeywords(ref MappingStats stats, object obj, float value, params string[] keywords)
        {
            if (TrySetFloatMemberByKeywords(obj, value, keywords)) stats.applied++;
            else stats.skipped++;
        }

        private static void MapEnum(ref MappingStats stats, object obj, string displayName, params string[] names)
        {
            if (TrySetEnumMemberByDisplayBestEffort(obj, names, displayName)) stats.applied++;
            else stats.skipped++;
        }

        private static void MapEnumByKeywords(ref MappingStats stats, object obj, string displayName, params string[] keywords)
        {
            if (TrySetEnumMemberByKeywords(obj, displayName, keywords)) stats.applied++;
            else stats.skipped++;
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

        private static int CountEnumerable(object enumerable)
        {
            if (enumerable == null) return 0;
            if (enumerable is ICollection c) return c.Count;
            if (enumerable is IEnumerable e)
            {
                int count = 0;
                foreach (var _ in e) count++;
                return count;
            }
            return 0;
        }

        private static bool SetComponentListMemberBestEffort(object obj, string[] names, object sourceEnumerable, out int copiedCount)
        {
            copiedCount = 0;
            if (obj == null || names == null || sourceEnumerable == null) return false;

            const BindingFlags BF = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
            var t = obj.GetType();

            foreach (var n in names)
            {
                try
                {
                    var f = t.GetField(n, BF);
                    if (f != null && TryCopyAssignableList(f.GetValue(obj), sourceEnumerable, out copiedCount)) return true;

                    var p = t.GetProperty(n, BF);
                    if (p != null && p.CanRead && TryCopyAssignableList(p.GetValue(obj, null), sourceEnumerable, out copiedCount)) return true;
                }
                catch { }
            }

            return false;
        }

        private static bool TryCopyAssignableList(object targetListObj, object sourceEnumerable, out int copiedCount)
        {
            copiedCount = 0;
            if (!(targetListObj is IList list) || sourceEnumerable == null) return false;

            var targetType = GetListElementType(targetListObj.GetType()) ?? typeof(object);
            list.Clear();

            if (sourceEnumerable is IEnumerable enumerable)
            {
                foreach (var item in enumerable)
                {
                    if (item == null) continue;
                    if (targetType.IsAssignableFrom(item.GetType()))
                    {
                        list.Add(item);
                        copiedCount++;
                    }
                }
            }

            return copiedCount > 0;
        }

        private static Type GetListElementType(Type listType)
        {
            if (listType == null) return null;
            if (listType.IsArray) return listType.GetElementType();
            if (listType.IsGenericType) return listType.GetGenericArguments()[0];

            foreach (var iface in listType.GetInterfaces())
            {
                if (iface.IsGenericType && iface.GetGenericTypeDefinition() == typeof(IList<>))
                    return iface.GetGenericArguments()[0];
            }

            return null;
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
            => TrySetEnumMemberByDisplayBestEffort(obj, names, displayName);

        private static bool TrySetEnumMemberByDisplayBestEffort(object obj, string[] names, string displayName)
        {
            if (obj == null || names == null || string.IsNullOrEmpty(displayName)) return false;

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
                        if (val != null) { f.SetValue(obj, val); return true; }
                    }

                    var p = t.GetProperty(n, BF);
                    if (p != null && p.CanWrite && p.PropertyType.IsEnum)
                    {
                        var val = FindEnumByInspectorNameOrToString(p.PropertyType, displayName);
                        if (val != null) { p.SetValue(obj, val, null); return true; }
                    }
                }
                catch { }
            }

            if (EnableVerboseMappingLogs)
                Debug.Log($"PhysBone→Magica2: Enum mapping skipped for '{displayName}' on {obj.GetType().Name}.");

            return false;
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
            => TrySetFloatMemberBestEffort(obj, value, names);

        private static bool TrySetFloatMemberBestEffort(object obj, float value, params string[] names)
        {
            if (obj == null || names == null) return false;

            const BindingFlags BF = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
            var t = obj.GetType();

            foreach (var n in names)
            {
                try
                {
                    var f = t.GetField(n, BF);
                    if (f != null && f.FieldType == typeof(float)) { f.SetValue(obj, value); return true; }

                    var p = t.GetProperty(n, BF);
                    if (p != null && p.CanWrite && p.PropertyType == typeof(float)) { p.SetValue(obj, value, null); return true; }
                }
                catch { }
            }

            if (EnableVerboseMappingLogs)
                Debug.Log($"PhysBone→Magica2: Float mapping skipped on {obj.GetType().Name} [{string.Join(",", names)}].");
            return false;
        }

        private static bool TrySetFloatMemberByKeywords(object obj, float value, params string[] keywords)
        {
            if (obj == null || keywords == null || keywords.Length == 0) return false;

            const BindingFlags BF = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
            var t = obj.GetType();

            foreach (var f in t.GetFields(BF))
            {
                if (f.FieldType != typeof(float)) continue;
                if (!ContainsKeywords(f.Name, keywords)) continue;
                try { f.SetValue(obj, value); return true; } catch { }
            }

            foreach (var p in t.GetProperties(BF))
            {
                if (!p.CanWrite || p.PropertyType != typeof(float)) continue;
                if (!ContainsKeywords(p.Name, keywords)) continue;
                try { p.SetValue(obj, value, null); return true; } catch { }
            }

            return false;
        }

        private static bool TrySetEnumMemberByKeywords(object obj, string displayName, params string[] keywords)
        {
            if (obj == null || string.IsNullOrEmpty(displayName) || keywords == null || keywords.Length == 0) return false;

            const BindingFlags BF = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
            var t = obj.GetType();

            foreach (var f in t.GetFields(BF))
            {
                if (!f.FieldType.IsEnum || !ContainsKeywords(f.Name, keywords)) continue;
                try
                {
                    var val = FindEnumByInspectorNameOrToString(f.FieldType, displayName);
                    if (val != null) { f.SetValue(obj, val); return true; }
                }
                catch { }
            }

            foreach (var p in t.GetProperties(BF))
            {
                if (!p.CanWrite || !p.PropertyType.IsEnum || !ContainsKeywords(p.Name, keywords)) continue;
                try
                {
                    var val = FindEnumByInspectorNameOrToString(p.PropertyType, displayName);
                    if (val != null) { p.SetValue(obj, val, null); return true; }
                }
                catch { }
            }

            return false;
        }

        private static bool ContainsKeywords(string source, string[] keywords)
        {
            if (string.IsNullOrEmpty(source) || keywords == null || keywords.Length == 0) return false;

            var norm = source.Replace("_", string.Empty).ToLowerInvariant();
            foreach (var keyword in keywords)
            {
                if (string.IsNullOrEmpty(keyword)) continue;
                if (!norm.Contains(keyword.Replace("_", string.Empty).ToLowerInvariant())) return false;
            }
            return true;
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
    }
}
#endif
