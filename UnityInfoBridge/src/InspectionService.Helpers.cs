using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using Newtonsoft.Json.Linq;
using UnityEngine;
using UnityEngine.SceneManagement;
#if IL2CPP
using Il2CppInterop.Runtime;
using Il2CppInterop.Runtime.InteropTypes;
#endif

namespace UnityInfoBridge
{
    internal static partial class UnityInspectionService
    {
        private static readonly Dictionary<string, Type> LoadedTypeCache = new Dictionary<string, Type>(StringComparer.Ordinal);
        private static readonly string[] TextScalarMemberCandidates =
        {
            "text",
            "m_Text",
            "formattedText",
            "_formattedText",
            "caption",
            "label",
            "displayText",
            "currentText",
            "value"
        };

        private static readonly string[] TextCollectionMemberCandidates =
        {
            "lines",
            "m_Lines",
            "displayLines"
        };

        private static BridgeRpcException SceneNotFound(JObject args)
        {
            return new BridgeRpcException(-32012, "scene_not_found", new Dictionary<string, object>
            {
                { "scene_name", ArgString(args, "scene_name", null) },
                { "scene_handle", ArgInt(args, "scene_handle", -1) }
            });
        }

        private static Scene ResolveScene(JObject args)
        {
            string name = ArgString(args, "scene_name", null);
            if (!string.IsNullOrEmpty(name))
            {
                for (int i = 0; i < SceneManager.sceneCount; i++)
                {
                    Scene scene = SceneManager.GetSceneAt(i);
                    if (string.Equals(scene.name, name, StringComparison.Ordinal)) return scene;
                }
            }

            if (args["scene_handle"] != null)
            {
                int handle = ArgInt(args, "scene_handle", -1);
                for (int i = 0; i < SceneManager.sceneCount; i++)
                {
                    Scene scene = SceneManager.GetSceneAt(i);
                    if (scene.handle == handle) return scene;
                }
            }

            return SceneManager.GetActiveScene();
        }

        private static Dictionary<string, object> SceneInfo(Scene scene, int activeHandle)
        {
            return new Dictionary<string, object>
            {
                { "name", scene.name },
                { "handle", scene.handle },
                { "is_loaded", scene.isLoaded },
                { "is_active", scene.handle == activeHandle },
                { "root_count", scene.isLoaded ? scene.rootCount : 0 }
            };
        }

        private static List<GameObject> CollectGameObjects(string sceneName, bool includeInactive)
        {
            List<GameObject> output = new List<GameObject>();
            HashSet<int> seen = new HashSet<int>();
            for (int i = 0; i < SceneManager.sceneCount; i++)
            {
                Scene scene = SceneManager.GetSceneAt(i);
                if (!scene.isLoaded) continue;
                if (!string.IsNullOrEmpty(sceneName) && !string.Equals(scene.name, sceneName, StringComparison.Ordinal)) continue;

                GameObject[] roots = scene.GetRootGameObjects();
                for (int r = 0; r < roots.Length; r++)
                {
                    WalkHierarchy(roots[r], includeInactive, output, seen);
                }
            }

            GameObject[] allObjects = Resources.FindObjectsOfTypeAll<GameObject>();
            for (int i = 0; i < allObjects.Length; i++)
            {
                GameObject go = allObjects[i];
                if (go == null) continue;
                if (!go.scene.IsValid()) continue;
                if (!string.IsNullOrEmpty(sceneName) && !string.Equals(go.scene.name, sceneName, StringComparison.Ordinal)) continue;
                if (go.transform.parent != null) continue;

                WalkHierarchy(go, includeInactive, output, seen);
            }

            return output;
        }

        private static void WalkHierarchy(GameObject go, bool includeInactive, List<GameObject> output, HashSet<int> seen)
        {
            if (go == null) return;
            if (!seen.Add(go.GetInstanceID())) return;
            if (includeInactive || go.activeInHierarchy) output.Add(go);

            for (int i = 0; i < go.transform.childCount; i++)
            {
                Transform child = go.transform.GetChild(i);
                if (child != null) WalkHierarchy(child.gameObject, includeInactive, output, seen);
            }
        }

        private static GameObject FindGameObjectById(int id)
        {
            List<GameObject> all = CollectGameObjects(null, true);
            for (int i = 0; i < all.Count; i++) if (all[i].GetInstanceID() == id) return all[i];
            return null;
        }

        private static Component FindComponentById(int id)
        {
            List<GameObject> all = CollectGameObjects(null, true);
            for (int i = 0; i < all.Count; i++)
            {
                Component[] comps = all[i].GetComponents<Component>();
                for (int c = 0; c < comps.Length; c++)
                {
                    Component comp = comps[c];
                    if (comp != null && comp.GetInstanceID() == id) return comp;
                }
            }
            return null;
        }

        private static Dictionary<string, object> GameObjectInfo(GameObject go)
        {
            return new Dictionary<string, object>
            {
                { "instance_id", go.GetInstanceID() },
                { "name", go.name },
                { "path", PathOf(go.transform) },
                { "scene_name", go.scene.IsValid() ? go.scene.name : string.Empty },
                { "active_self", go.activeSelf },
                { "active_in_hierarchy", go.activeInHierarchy },
                { "parent_instance_id", go.transform.parent != null ? (object)go.transform.parent.gameObject.GetInstanceID() : null }
            };
        }

        private static string PathOf(Transform transform)
        {
            if (transform == null) return string.Empty;
            List<string> names = new List<string>();
            Transform cur = transform;
            while (cur != null)
            {
                names.Add(cur.name);
                cur = cur.parent;
            }
            names.Reverse();
            return string.Join("/", names.ToArray());
        }

        private static Dictionary<string, object> GameObjectTree(GameObject go, int depth, bool includeComponents, bool includeFields, int fieldDepth, bool includeInactive)
        {
            Dictionary<string, object> node = GameObjectInfo(go);
            if (includeComponents) node["components"] = ComponentsOf(go, includeFields, fieldDepth, false);

            List<object> children = new List<object>();
            if (depth > 0)
            {
                for (int i = 0; i < go.transform.childCount; i++)
                {
                    Transform child = go.transform.GetChild(i);
                    if (!includeInactive && !child.gameObject.activeInHierarchy) continue;
                    children.Add(GameObjectTree(child.gameObject, depth - 1, includeComponents, includeFields, fieldDepth, includeInactive));
                }
            }
            node["children"] = children;
            node["child_count"] = go.transform.childCount;
            return node;
        }

        private static List<object> ComponentsOf(GameObject go, bool includeFields, int fieldDepth, bool includeNonPublic)
        {
            List<object> items = new List<object>();
            Component[] components = go.GetComponents<Component>();
            for (int i = 0; i < components.Length; i++)
            {
                Component component = components[i];
                if (component == null) continue;
                items.Add(ComponentInfo(component, includeFields, fieldDepth, includeNonPublic));
            }
            return items;
        }

        private static Dictionary<string, object> ComponentInfo(Component component, bool includeFields, int fieldDepth, bool includeNonPublic)
        {
            object reflectionTarget = includeFields ? ResolveComponentReflectionTarget(component) : (object)component;
            string managedType = SafeTypeName(component);
            string resolvedType = SafeTypeName(reflectionTarget);
            Dictionary<string, object> il2cppType = GetIl2CppTypeMetadata(component);
            string runtimeType = DictString(il2cppType, "full_name");
            string componentType = FirstNonEmpty(runtimeType, resolvedType, managedType);

            Dictionary<string, object> info = new Dictionary<string, object>
            {
                { "component_instance_id", component.GetInstanceID() },
                { "component_type", componentType },
                { "component_type_managed", managedType },
                { "enabled", ComponentEnabled(component) }
            };

            if (!string.IsNullOrEmpty(runtimeType)) info["component_type_runtime"] = runtimeType;
            if (!string.IsNullOrEmpty(resolvedType) && !string.Equals(resolvedType, componentType, StringComparison.Ordinal)) info["component_type_resolved"] = resolvedType;
            if (il2cppType != null) info["component_type_il2cpp"] = il2cppType;
            if (includeFields) info["fields"] = ReadMembers(reflectionTarget, includeNonPublic, fieldDepth, false);
            return info;
        }

        private static object ComponentEnabled(Component component)
        {
            Behaviour behavior = component as Behaviour;
            return behavior == null ? null : (object)behavior.enabled;
        }

        private static string SafeTypeName(object value)
        {
            if (value == null) return string.Empty;
            Type type = value.GetType();
            if (type == null) return string.Empty;
            return string.IsNullOrEmpty(type.FullName) ? type.Name : type.FullName;
        }

        private static string FirstNonEmpty(string first, string second, string third)
        {
            if (!string.IsNullOrEmpty(first)) return first;
            if (!string.IsNullOrEmpty(second)) return second;
            return third ?? string.Empty;
        }

        private static string DictString(Dictionary<string, object> map, string key)
        {
            if (map == null || string.IsNullOrEmpty(key)) return string.Empty;
            object value;
            if (!map.TryGetValue(key, out value) || value == null) return string.Empty;
            return value.ToString();
        }

        private static string NormalizeTypeName(string typeName)
        {
            if (string.IsNullOrEmpty(typeName)) return string.Empty;

            string trimmed = typeName.Trim();
            int asmSeparator = trimmed.IndexOf(',');
            if (asmSeparator > 0) trimmed = trimmed.Substring(0, asmSeparator).Trim();

            if (trimmed.StartsWith("Il2Cpp.", StringComparison.Ordinal)) return trimmed.Substring("Il2Cpp.".Length);
            if (trimmed.StartsWith("Il2Cpp", StringComparison.Ordinal) && trimmed.Length > "Il2Cpp".Length) return trimmed.Substring("Il2Cpp".Length);

            return trimmed;
        }

        private static void AddTypeNameCandidate(HashSet<string> names, string typeName)
        {
            if (names == null || string.IsNullOrEmpty(typeName)) return;
            names.Add(typeName);

            string normalized = NormalizeTypeName(typeName);
            if (!string.IsNullOrEmpty(normalized)) names.Add(normalized);
        }

        private static void AddTypeCandidates(HashSet<string> names, Type type)
        {
            Type cursor = type;
            while (cursor != null)
            {
                AddTypeNameCandidate(names, cursor.FullName);
                cursor = cursor.BaseType;
            }
        }

        private static HashSet<string> CollectComponentTypeCandidates(Component component, object reflectionTarget)
        {
            HashSet<string> candidates = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (component != null) AddTypeCandidates(candidates, component.GetType());
            if (reflectionTarget != null) AddTypeCandidates(candidates, reflectionTarget.GetType());

            Dictionary<string, object> il2cppMeta = GetIl2CppTypeMetadata(component);
            AddTypeNameCandidate(candidates, DictString(il2cppMeta, "full_name"));
            AddTypeNameCandidate(candidates, DictString(il2cppMeta, "type_name"));
            AddTypeNameCandidate(candidates, DictString(il2cppMeta, "assembly_qualified_name"));

            return candidates;
        }

        private static bool TypeFilterMatches(string typeFilter, Component component, object reflectionTarget)
        {
            if (string.IsNullOrEmpty(typeFilter)) return true;

            HashSet<string> candidates = CollectComponentTypeCandidates(component, reflectionTarget);
            if (candidates.Contains(typeFilter)) return true;

            string normalizedFilter = NormalizeTypeName(typeFilter);
            return !string.IsNullOrEmpty(normalizedFilter) && candidates.Contains(normalizedFilter);
        }

        private static bool AllowedTypeMatches(HashSet<string> allowed, Component component, object reflectionTarget)
        {
            if (allowed == null || allowed.Count == 0) return true;

            HashSet<string> candidates = CollectComponentTypeCandidates(component, reflectionTarget);
            foreach (string candidate in candidates)
            {
                if (allowed.Contains(candidate)) return true;
            }

            return false;
        }

        private static Type FindLoadedTypeCached(string fullName)
        {
            if (string.IsNullOrEmpty(fullName)) return null;

            Type cached;
            if (LoadedTypeCache.TryGetValue(fullName, out cached)) return cached;

            Type found = null;
            Assembly[] assemblies = AppDomain.CurrentDomain.GetAssemblies();
            for (int i = 0; i < assemblies.Length; i++)
            {
                try
                {
                    found = assemblies[i].GetType(fullName, false);
                    if (found != null) break;
                }
                catch { }
            }

            LoadedTypeCache[fullName] = found;
            return found;
        }

        private static Type ResolveRuntimeComponentType(string runtimeFullName)
        {
            if (string.IsNullOrEmpty(runtimeFullName)) return null;

            Type found = FindLoadedTypeCached(runtimeFullName);
            if (found != null) return found;

            string normalized = NormalizeTypeName(runtimeFullName);
            if (!string.IsNullOrEmpty(normalized) && !string.Equals(normalized, runtimeFullName, StringComparison.Ordinal))
            {
                found = FindLoadedTypeCached(normalized);
                if (found != null) return found;
            }

            if (!runtimeFullName.StartsWith("Il2Cpp", StringComparison.Ordinal))
            {
                found = FindLoadedTypeCached("Il2Cpp." + runtimeFullName);
                if (found != null) return found;

                found = FindLoadedTypeCached("Il2Cpp" + runtimeFullName);
                if (found != null) return found;
            }

            return null;
        }

        private static object ResolveComponentReflectionTarget(Component component)
        {
            if (component == null) return null;

            object resolved = component;
            string runtimeType = DictString(GetIl2CppTypeMetadata(component), "full_name");
            if (string.IsNullOrEmpty(runtimeType)) return resolved;

            string managedType = SafeTypeName(component);
            if (string.Equals(runtimeType, managedType, StringComparison.Ordinal)) return resolved;
            if (string.Equals(NormalizeTypeName(runtimeType), NormalizeTypeName(managedType), StringComparison.Ordinal)) return resolved;

            Type runtimeManagedType = ResolveRuntimeComponentType(runtimeType);
            if (runtimeManagedType == null || !typeof(Component).IsAssignableFrom(runtimeManagedType)) return resolved;

#if IL2CPP
            IntPtr pointer;
            if (!TryGetIl2CppPointer(component, out pointer) || pointer == IntPtr.Zero) return resolved;

            try
            {
                ConstructorInfo ctor = runtimeManagedType.GetConstructor(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance, null, new[] { typeof(IntPtr) }, null);
                if (ctor == null) return resolved;

                object promoted = ctor.Invoke(new object[] { pointer });
                if (promoted != null) return promoted;
            }
            catch { }
#endif

            return resolved;
        }

        private static Dictionary<string, object> GetIl2CppTypeMetadata(object value)
        {
#if IL2CPP
            IntPtr objectPointer;
            if (!TryGetIl2CppPointer(value, out objectPointer) || objectPointer == IntPtr.Zero) return null;

            IntPtr classPointer = IL2CPP.il2cpp_object_get_class(objectPointer);
            if (classPointer == IntPtr.Zero) return null;

            IntPtr typePointer = IL2CPP.il2cpp_class_get_type(classPointer);
            IntPtr imagePointer = IL2CPP.il2cpp_class_get_image(classPointer);

            string className = PtrToAnsi(IL2CPP.il2cpp_class_get_name(classPointer));
            string @namespace = PtrToAnsi(IL2CPP.il2cpp_class_get_namespace(classPointer));
            string fullName = string.IsNullOrEmpty(@namespace) ? className : (@namespace + "." + className);

            return new Dictionary<string, object>
            {
                { "pointer", objectPointer.ToInt64() },
                { "class_pointer", classPointer.ToInt64() },
                { "type_pointer", typePointer != IntPtr.Zero ? (object)typePointer.ToInt64() : null },
                { "image_pointer", imagePointer != IntPtr.Zero ? (object)imagePointer.ToInt64() : null },
                { "namespace", @namespace },
                { "class_name", className },
                { "full_name", fullName },
                { "type_name", typePointer != IntPtr.Zero ? (object)PtrToAnsi(IL2CPP.il2cpp_type_get_name(typePointer)) : string.Empty },
                { "assembly_qualified_name", typePointer != IntPtr.Zero ? (object)PtrToAnsi(IL2CPP.il2cpp_type_get_assembly_qualified_name(typePointer)) : string.Empty },
                { "assembly_name", PtrToAnsi(IL2CPP.il2cpp_class_get_assemblyname(classPointer)) },
                { "image_name", imagePointer != IntPtr.Zero ? (object)PtrToAnsi(IL2CPP.il2cpp_image_get_name(imagePointer)) : string.Empty }
            };
#else
            return null;
#endif
        }

#if IL2CPP
        private static bool TryGetIl2CppPointer(object value, out IntPtr pointer)
        {
            pointer = IntPtr.Zero;
            Il2CppObjectBase cppBase = value as Il2CppObjectBase;
            if (cppBase == null) return false;
            pointer = cppBase.Pointer;
            return pointer != IntPtr.Zero;
        }

        private static string PtrToAnsi(IntPtr ptr)
        {
            if (ptr == IntPtr.Zero) return string.Empty;
            try
            {
                string value = Marshal.PtrToStringAnsi(ptr);
                return value ?? string.Empty;
            }
            catch
            {
                return string.Empty;
            }
        }
#endif

        private static Dictionary<string, object> ReadMembers(object target, bool includeNonPublic, int maxDepth, bool includeProperties)
        {
            Dictionary<string, object> members = new Dictionary<string, object>();
            BindingFlags flags = BindingFlags.Instance | BindingFlags.Public;
            if (includeNonPublic) flags |= BindingFlags.NonPublic;

            FieldInfo[] fields = target.GetType().GetFields(flags);
            for (int i = 0; i < fields.Length; i++)
            {
                FieldInfo field = fields[i];
                if (!includeNonPublic && !field.IsPublic) continue;
                try { members[field.Name] = SerializeValue(field.GetValue(target), maxDepth); }
                catch (Exception ex) { members[field.Name] = "<error: " + ex.GetType().Name + ">"; }
            }

            if (includeProperties)
            {
                PropertyInfo[] props = target.GetType().GetProperties(flags);
                for (int i = 0; i < props.Length; i++)
                {
                    PropertyInfo prop = props[i];
                    if (!prop.CanRead || prop.GetIndexParameters().Length > 0) continue;
                    try { members[prop.Name] = SerializeValue(prop.GetValue(target, null), maxDepth); }
                    catch (Exception ex) { members[prop.Name] = "<error: " + ex.GetType().Name + ">"; }
                }
            }

            return members;
        }

        private static object SerializeValue(object value, int depth)
        {
            if (value == null) return null;
            Type type = value.GetType();
            if (type.IsPrimitive || value is string || value is decimal) return value;
            if (value is Enum) return value.ToString();

            UnityEngine.Object uo = value as UnityEngine.Object;
            if (uo != null)
            {
                return new Dictionary<string, object>
                {
                    { "type", uo.GetType().FullName },
                    { "name", uo.name },
                    { "instance_id", uo.GetInstanceID() }
                };
            }

            if (depth <= 0) return value.ToString();

            IDictionary dict = value as IDictionary;
            if (dict != null)
            {
                Dictionary<string, object> output = new Dictionary<string, object>();
                int count = 0;
                foreach (DictionaryEntry entry in dict)
                {
                    output[Convert.ToString(entry.Key, CultureInfo.InvariantCulture)] = SerializeValue(entry.Value, depth - 1);
                    if (++count >= 32) break;
                }
                return output;
            }

            IEnumerable enumerable = value as IEnumerable;
            if (enumerable != null && !(value is string))
            {
                List<object> list = new List<object>();
                int count = 0;
                foreach (object item in enumerable)
                {
                    list.Add(SerializeValue(item, depth - 1));
                    if (++count >= 64) break;
                }
                return list;
            }

            return value.ToString();
        }

        private static object SearchFields(string query, string sceneName, string typeFilter, string fieldFilter, string mode, bool includeInactive, int limit)
        {
            List<GameObject> all = CollectGameObjects(sceneName, includeInactive);
            List<object> hits = new List<object>();
            for (int i = 0; i < all.Count; i++)
            {
                Component[] comps = all[i].GetComponents<Component>();
                for (int c = 0; c < comps.Length; c++)
                {
                    Component comp = comps[c];
                    if (comp == null) continue;
                    object reflectionTarget = ResolveComponentReflectionTarget(comp);
                    if (!TypeFilterMatches(typeFilter, comp, reflectionTarget)) continue;

                    Dictionary<string, object> il2cppType = GetIl2CppTypeMetadata(comp);
                    string managedType = SafeTypeName(comp);
                    string resolvedType = SafeTypeName(reflectionTarget);
                    string runtimeType = DictString(il2cppType, "full_name");
                    string componentType = FirstNonEmpty(runtimeType, resolvedType, managedType);

                    Dictionary<string, object> members = ReadMembers(reflectionTarget, false, 1, false);
                    foreach (KeyValuePair<string, object> kvp in members)
                    {
                        if (!string.IsNullOrEmpty(fieldFilter) && !string.Equals(fieldFilter, kvp.Key, StringComparison.Ordinal)) continue;
                        string value = kvp.Value == null ? string.Empty : kvp.Value.ToString();
                        if (!TextMatch(value, query, mode)) continue;

                        hits.Add(new Dictionary<string, object>
                        {
                            { "gameobject", GameObjectInfo(comp.gameObject) },
                            { "component_instance_id", comp.GetInstanceID() },
                            { "component_type", componentType },
                            { "component_type_managed", managedType },
                            { "field_name", kvp.Key },
                            { "field_value", kvp.Value }
                        });
                        if (!string.IsNullOrEmpty(runtimeType)) ((Dictionary<string, object>)hits[hits.Count - 1])["component_type_runtime"] = runtimeType;
                        if (il2cppType != null) ((Dictionary<string, object>)hits[hits.Count - 1])["component_type_il2cpp"] = il2cppType;

                        if (hits.Count >= limit)
                        {
                            return new Dictionary<string, object> { { "items", hits }, { "count", hits.Count } };
                        }
                    }
                }
            }
            return new Dictionary<string, object> { { "items", hits }, { "count", hits.Count } };
        }

        private static object EnumerateTextElements(string sceneName, bool includeInactive, string[] componentTypes, int limit)
        {
            HashSet<string> allowed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            for (int i = 0; i < componentTypes.Length; i++) AddTypeNameCandidate(allowed, componentTypes[i]);
            bool wildcard = IsWildcardTypeFilter(allowed);

            List<GameObject> all = CollectGameObjects(sceneName, includeInactive);
            List<object> items = new List<object>();
            for (int i = 0; i < all.Count; i++)
            {
                Component[] comps = all[i].GetComponents<Component>();
                for (int c = 0; c < comps.Length; c++)
                {
                    Component comp = comps[c];
                    if (comp == null) continue;
                    object reflectionTarget = ResolveComponentReflectionTarget(comp);
                    if (wildcard)
                    {
                        if (!LooksLikeTextComponent(comp, reflectionTarget)) continue;
                    }
                    else
                    {
                        if (!AllowedTypeMatches(allowed, comp, reflectionTarget)) continue;
                    }

                    Dictionary<string, object> textDetails = ReadTextDetails(comp, reflectionTarget);
                    string text = DictString(textDetails, "text");
                    Dictionary<string, object> il2cppType = GetIl2CppTypeMetadata(comp);
                    string componentType = FirstNonEmpty(DictString(il2cppType, "full_name"), SafeTypeName(reflectionTarget), SafeTypeName(comp));

                    items.Add(new Dictionary<string, object>
                    {
                        { "gameobject", GameObjectInfo(all[i]) },
                        { "component_instance_id", comp.GetInstanceID() },
                        { "component_type", componentType },
                        { "component_type_managed", SafeTypeName(comp) },
                        { "enabled", ComponentEnabled(comp) },
                        { "text", text },
                        { "text_details", textDetails },
                        { "text_length", text.Length }
                    });
                    if (!string.IsNullOrEmpty(DictString(il2cppType, "full_name"))) ((Dictionary<string, object>)items[items.Count - 1])["component_type_runtime"] = DictString(il2cppType, "full_name");
                    if (il2cppType != null) ((Dictionary<string, object>)items[items.Count - 1])["component_type_il2cpp"] = il2cppType;

                    if (items.Count >= limit)
                    {
                        return new Dictionary<string, object> { { "items", items }, { "count", items.Count } };
                    }
                }
            }
            return new Dictionary<string, object> { { "items", items }, { "count", items.Count } };
        }

        private static object SearchTextElements(string query, string mode, string sceneName, bool includeInactive, string[] componentTypes, int limit)
        {
            HashSet<string> allowed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            for (int i = 0; i < componentTypes.Length; i++) AddTypeNameCandidate(allowed, componentTypes[i]);
            bool wildcard = IsWildcardTypeFilter(allowed);

            List<GameObject> all = CollectGameObjects(sceneName, includeInactive);
            List<object> items = new List<object>();
            for (int i = 0; i < all.Count; i++)
            {
                Component[] comps = all[i].GetComponents<Component>();
                for (int c = 0; c < comps.Length; c++)
                {
                    Component comp = comps[c];
                    if (comp == null) continue;

                    object reflectionTarget = ResolveComponentReflectionTarget(comp);
                    if (wildcard)
                    {
                        if (!LooksLikeTextComponent(comp, reflectionTarget)) continue;
                    }
                    else
                    {
                        if (!AllowedTypeMatches(allowed, comp, reflectionTarget)) continue;
                    }

                    Dictionary<string, object> textDetails = ReadTextDetails(comp, reflectionTarget);
                    string text = DictString(textDetails, "text");
                    if (!TextMatch(text, query, mode)) continue;

                    Dictionary<string, object> il2cppType = GetIl2CppTypeMetadata(comp);
                    string componentType = FirstNonEmpty(DictString(il2cppType, "full_name"), SafeTypeName(reflectionTarget), SafeTypeName(comp));

                    items.Add(new Dictionary<string, object>
                    {
                        { "gameobject", GameObjectInfo(all[i]) },
                        { "component_instance_id", comp.GetInstanceID() },
                        { "component_type", componentType },
                        { "component_type_managed", SafeTypeName(comp) },
                        { "enabled", ComponentEnabled(comp) },
                        { "text", text },
                        { "text_details", textDetails },
                        { "text_length", text.Length }
                    });
                    if (!string.IsNullOrEmpty(DictString(il2cppType, "full_name"))) ((Dictionary<string, object>)items[items.Count - 1])["component_type_runtime"] = DictString(il2cppType, "full_name");
                    if (il2cppType != null) ((Dictionary<string, object>)items[items.Count - 1])["component_type_il2cpp"] = il2cppType;

                    if (items.Count >= limit)
                    {
                        return new Dictionary<string, object> { { "items", items }, { "count", items.Count } };
                    }
                }
            }

            return new Dictionary<string, object> { { "items", items }, { "count", items.Count } };
        }

        private static object FilterTextItems(List<object> items, string query, string mode)
        {
            List<object> filtered = new List<object>();
            for (int i = 0; i < items.Count; i++)
            {
                Dictionary<string, object> item = (Dictionary<string, object>)items[i];
                string text = item["text"] == null ? string.Empty : item["text"].ToString();
                if (TextMatch(text, query, mode)) filtered.Add(item);
            }
            return new Dictionary<string, object> { { "items", filtered }, { "count", filtered.Count } };
        }

        private static object BuildTextContext(int componentId, bool includeNeighbors, bool includeSiblingTexts)
        {
            Component comp = FindComponentById(componentId);
            if (comp == null) throw new BridgeRpcException(-32013, "component_not_found", new Dictionary<string, object> { { "component_instance_id", componentId } });
            object reflectionTarget = ResolveComponentReflectionTarget(comp);
            Dictionary<string, object> textDetails = ReadTextDetails(comp, reflectionTarget);
            Dictionary<string, object> il2cppType = GetIl2CppTypeMetadata(comp);
            string componentType = FirstNonEmpty(DictString(il2cppType, "full_name"), SafeTypeName(reflectionTarget), SafeTypeName(comp));

            Dictionary<string, object> payload = new Dictionary<string, object>
            {
                { "component_instance_id", componentId },
                { "component_type", componentType },
                { "component_type_managed", SafeTypeName(comp) },
                { "owner", GameObjectInfo(comp.gameObject) },
                { "text", DictString(textDetails, "text") },
                { "text_details", textDetails }
            };
            if (!string.IsNullOrEmpty(DictString(il2cppType, "full_name"))) payload["component_type_runtime"] = DictString(il2cppType, "full_name");
            if (il2cppType != null) payload["component_type_il2cpp"] = il2cppType;

            if (includeNeighbors) payload["parent_chain"] = ParentChain(comp.transform);
            if (includeSiblingTexts) payload["sibling_texts"] = SiblingTexts(comp);
            return payload;
        }

        private static object BuildSceneSnapshot(Scene scene, int depth, bool includeComponents, bool includeFields, int fieldDepth)
        {
            List<object> roots = new List<object>();
            GameObject[] rootObjects = scene.GetRootGameObjects();
            for (int i = 0; i < rootObjects.Length; i++)
            {
                roots.Add(GameObjectTree(rootObjects[i], depth, includeComponents, includeFields, fieldDepth, true));
            }
            return new Dictionary<string, object>
            {
                { "scene", SceneInfo(scene, SceneManager.GetActiveScene().handle) },
                { "roots", roots },
                { "root_count", roots.Count }
            };
        }

        private static List<object> ParentChain(Transform transform)
        {
            List<object> items = new List<object>();
            Transform cur = transform;
            while (cur != null)
            {
                items.Add(new Dictionary<string, object>
                {
                    { "name", cur.name },
                    { "instance_id", cur.gameObject.GetInstanceID() },
                    { "path", PathOf(cur) }
                });
                cur = cur.parent;
            }
            items.Reverse();
            return items;
        }

        private static List<object> SiblingTexts(Component source)
        {
            List<object> items = new List<object>();
            if (source.transform == null || source.transform.parent == null) return items;
            Transform parent = source.transform.parent;

            for (int i = 0; i < parent.childCount; i++)
            {
                Transform child = parent.GetChild(i);
                Component[] comps = child.gameObject.GetComponents<Component>();
                for (int c = 0; c < comps.Length; c++)
                {
                    Component comp = comps[c];
                    if (comp == null) continue;
                    object reflectionTarget = ResolveComponentReflectionTarget(comp);
                    Dictionary<string, object> textDetails = ReadTextDetails(comp, reflectionTarget);
                    string text = DictString(textDetails, "text");
                    if (string.IsNullOrEmpty(text)) continue;
                    items.Add(new Dictionary<string, object>
                    {
                        { "component_instance_id", comp.GetInstanceID() },
                        { "component_type", FirstNonEmpty(DictString(GetIl2CppTypeMetadata(comp), "full_name"), SafeTypeName(reflectionTarget), SafeTypeName(comp)) },
                        { "component_type_managed", SafeTypeName(comp) },
                        { "path", PathOf(comp.transform) },
                        { "text", text },
                        { "text_details", textDetails }
                    });
                }
            }

            return items;
        }

        private static bool IsWildcardTypeFilter(HashSet<string> allowed)
        {
            if (allowed == null || allowed.Count == 0) return true;
            if (allowed.Contains("*")) return true;
            if (allowed.Contains("any")) return true;
            if (allowed.Contains("auto")) return true;
            return false;
        }

        private static bool LooksLikeTextComponent(Component component, object reflectionTarget)
        {
            object target = reflectionTarget ?? (object)component;
            string text;
            string source;
            return TryReadTextCandidate(target, out text, out source);
        }

        private static bool TryReadMemberValue(object target, string memberName, out object value, out string source)
        {
            value = null;
            source = string.Empty;
            if (target == null || string.IsNullOrEmpty(memberName)) return false;

            Type type = target.GetType();
            BindingFlags flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;

            try
            {
                PropertyInfo prop = type.GetProperty(memberName, flags);
                if (prop != null && prop.CanRead && prop.GetIndexParameters().Length == 0)
                {
                    value = prop.GetValue(target, null);
                    source = "property:" + memberName;
                    return true;
                }
            }
            catch { }

            try
            {
                FieldInfo field = type.GetField(memberName, flags);
                if (field != null)
                {
                    value = field.GetValue(target);
                    source = "field:" + memberName;
                    return true;
                }
            }
            catch { }

            return false;
        }

        private static bool TryReadFirstMember(object target, string[] memberNames, out object value, out string source)
        {
            value = null;
            source = string.Empty;
            if (memberNames == null) return false;

            for (int i = 0; i < memberNames.Length; i++)
            {
                if (TryReadMemberValue(target, memberNames[i], out value, out source)) return true;
            }

            return false;
        }

        private static bool IsStringLikeType(Type type)
        {
            if (type == null) return false;
            if (type == typeof(string)) return true;

            string fullName = type.FullName;
            if (string.IsNullOrEmpty(fullName)) return false;
            return string.Equals(fullName, "System.String", StringComparison.Ordinal) ||
                   string.Equals(fullName, "Il2CppSystem.String", StringComparison.Ordinal);
        }

        private static bool TryCoerceTextValue(object value, Type declaredType, out string text)
        {
            text = string.Empty;
            Type actualType = value != null ? value.GetType() : declaredType;
            if (IsStringLikeType(actualType) || IsStringLikeType(declaredType))
            {
                text = value == null ? string.Empty : value.ToString();
                return true;
            }

            if (value == null || value is string || value is UnityEngine.Object) return false;

            IEnumerable enumerable = value as IEnumerable;
            if (enumerable == null) return false;

            List<string> parts = new List<string>();
            bool sawAny = false;
            foreach (object item in enumerable)
            {
                if (item == null)
                {
                    sawAny = true;
                    parts.Add(string.Empty);
                    continue;
                }

                Type itemType = item.GetType();
                if (!IsStringLikeType(itemType)) return false;

                sawAny = true;
                parts.Add(item.ToString());
            }

            if (!sawAny) return false;

            text = string.Join("\n", parts.ToArray());
            return true;
        }

        private static bool TryReadTextLikeMember(object target, string memberName, out string text, out string source)
        {
            text = string.Empty;
            source = string.Empty;
            if (target == null || string.IsNullOrEmpty(memberName)) return false;

            Type type = target.GetType();
            BindingFlags flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;

            try
            {
                PropertyInfo prop = type.GetProperty(memberName, flags);
                if (prop != null && prop.CanRead && prop.GetIndexParameters().Length == 0)
                {
                    object value = prop.GetValue(target, null);
                    if (TryCoerceTextValue(value, prop.PropertyType, out text))
                    {
                        source = "property:" + memberName;
                        return true;
                    }
                }
            }
            catch { }

            try
            {
                FieldInfo field = type.GetField(memberName, flags);
                if (field != null)
                {
                    object value = field.GetValue(target);
                    if (TryCoerceTextValue(value, field.FieldType, out text))
                    {
                        source = "field:" + memberName;
                        return true;
                    }
                }
            }
            catch { }

            return false;
        }

        private static bool TryReadFirstTextLikeMember(object target, string[] memberNames, out string text, out string source)
        {
            text = string.Empty;
            source = string.Empty;
            if (memberNames == null) return false;

            for (int i = 0; i < memberNames.Length; i++)
            {
                if (TryReadTextLikeMember(target, memberNames[i], out text, out source)) return true;
            }

            return false;
        }

        private static bool TryReadTextCandidate(object target, out string text, out string source)
        {
            if (TryReadFirstTextLikeMember(target, TextScalarMemberCandidates, out text, out source)) return true;
            return TryReadFirstTextLikeMember(target, TextCollectionMemberCandidates, out text, out source);
        }

        private static Dictionary<string, object> ReadTextDetails(Component component, object reflectionTarget)
        {
            object target = reflectionTarget ?? (object)component;
            Dictionary<string, object> details = new Dictionary<string, object>();

            string textSource;
            string text = ReadText(target, out textSource);
            details["text"] = text;
            details["text_length"] = text.Length;
            details["text_source"] = textSource;

            object value;
            string source;

            if (TryReadFirstMember(target, new[] { "font", "fontAsset", "m_Font", "m_fontAsset" }, out value, out source))
            {
                details["font"] = SerializeValue(value, 1);
                details["font_source"] = source;

                UnityEngine.Object fontObj = value as UnityEngine.Object;
                if (fontObj != null)
                {
                    details["font_name"] = fontObj.name;
                    details["font_instance_id"] = fontObj.GetInstanceID();
                    details["font_type"] = FirstNonEmpty(DictString(GetIl2CppTypeMetadata(fontObj), "full_name"), SafeTypeName(fontObj), SafeTypeName(fontObj));
                }
            }

            if (TryReadFirstMember(target, new[] { "fontSize", "m_FontSize" }, out value, out source))
            {
                details["font_size"] = SerializeValue(value, 1);
                details["font_size_source"] = source;
            }

            if (TryReadFirstMember(target, new[] { "fontSharedMaterial", "fontMaterial", "material", "m_Material" }, out value, out source))
            {
                details["material"] = SerializeValue(value, 1);
                details["material_source"] = source;
            }

            if (TryReadFirstMember(target, new[] { "alignment", "m_Alignment" }, out value, out source))
            {
                details["alignment"] = SerializeValue(value, 1);
                details["alignment_source"] = source;
            }

            if (TryReadFirstMember(target, new[] { "color", "m_Color" }, out value, out source))
            {
                details["color"] = SerializeValue(value, 1);
                details["color_source"] = source;
            }

            if (TryReadFirstMember(target, new[] { "richText", "supportRichText", "m_RichText" }, out value, out source))
            {
                details["rich_text"] = SerializeValue(value, 1);
                details["rich_text_source"] = source;
            }

            if (TryReadFirstMember(target, new[] { "enableWordWrapping", "m_enableWordWrapping", "horizontalOverflow", "verticalOverflow" }, out value, out source))
            {
                details["wrapping"] = SerializeValue(value, 1);
                details["wrapping_source"] = source;
            }

            return details;
        }

        private static string ReadText(Component component)
        {
            string source;
            return ReadText((object)component, out source);
        }

        private static string ReadText(object target, out string source)
        {
            source = string.Empty;
            string text;
            if (TryReadTextCandidate(target, out text, out source)) return text;
            return string.Empty;
        }

        private static Dictionary<string, object> TrySetText(object target, string text, bool includeNonPublic)
        {
            Dictionary<string, object> write = null;

            try
            {
                write = SetMemberValue(target, "text", JToken.FromObject(text), includeNonPublic);
                write["member_selected"] = "text";
                return write;
            }
            catch { }

            write = SetMemberValue(target, "m_Text", JToken.FromObject(text), includeNonPublic);
            write["member_selected"] = "m_Text";
            return write;
        }

        private static Dictionary<string, object> SetMemberValue(object target, string memberName, JToken valueToken, bool includeNonPublic)
        {
            if (target == null) throw new BridgeRpcException(-32023, "invalid_target", "Target is null.");
            if (string.IsNullOrEmpty(memberName)) throw new BridgeRpcException(-32602, "invalid_params", "Missing required param: member_name");

            BindingFlags flags = BindingFlags.Instance | BindingFlags.Public;
            if (includeNonPublic) flags |= BindingFlags.NonPublic;

            Type targetType = target.GetType();
            Exception lastError = null;

            PropertyInfo prop = targetType.GetProperty(memberName, flags);
            if (prop != null)
            {
                if (!prop.CanWrite)
                {
                    throw new BridgeRpcException(-32024, "member_not_writable", new Dictionary<string, object>
                    {
                        { "member_name", memberName },
                        { "member_kind", "property" },
                        { "declaring_type", SafeTypeName(target) }
                    });
                }

                object before = null;
                try { if (prop.CanRead) before = prop.GetValue(target, null); } catch { }

                try
                {
                    object converted = ConvertJTokenToType(valueToken, prop.PropertyType);
                    prop.SetValue(target, converted, null);
                }
                catch (Exception ex)
                {
                    lastError = ex;
                }

                if (lastError == null)
                {
                    object after = null;
                    try { if (prop.CanRead) after = prop.GetValue(target, null); } catch { }
                    return new Dictionary<string, object>
                    {
                        { "success", true },
                        { "member_name", memberName },
                        { "member_kind", "property" },
                        { "member_type", prop.PropertyType.FullName },
                        { "declaring_type", SafeTypeName(target) },
                        { "before", SerializeValue(before, 2) },
                        { "after", SerializeValue(after, 2) }
                    };
                }
            }

            FieldInfo field = targetType.GetField(memberName, flags);
            if (field != null)
            {
                object before = null;
                try { before = field.GetValue(target); } catch { }

                try
                {
                    object converted = ConvertJTokenToType(valueToken, field.FieldType);
                    field.SetValue(target, converted);
                }
                catch (Exception ex)
                {
                    lastError = ex;
                }

                if (lastError == null)
                {
                    object after = null;
                    try { after = field.GetValue(target); } catch { }
                    return new Dictionary<string, object>
                    {
                        { "success", true },
                        { "member_name", memberName },
                        { "member_kind", "field" },
                        { "member_type", field.FieldType.FullName },
                        { "declaring_type", SafeTypeName(target) },
                        { "before", SerializeValue(before, 2) },
                        { "after", SerializeValue(after, 2) }
                    };
                }
            }

            if (prop == null && field == null)
            {
                throw new BridgeRpcException(-32025, "member_not_found", new Dictionary<string, object>
                {
                    { "member_name", memberName },
                    { "declaring_type", SafeTypeName(target) }
                });
            }

            throw new BridgeRpcException(-32026, "member_write_failed", new Dictionary<string, object>
            {
                { "member_name", memberName },
                { "declaring_type", SafeTypeName(target) },
                { "error", lastError == null ? "unknown" : lastError.Message }
            });
        }

        private static object ConvertJTokenToType(JToken token, Type targetType)
        {
            if (targetType == null) return token == null ? null : token.ToObject<object>();

            Type nullableUnderlying = Nullable.GetUnderlyingType(targetType);
            Type actualType = nullableUnderlying ?? targetType;

            if (token == null || token.Type == JTokenType.Null)
            {
                if (nullableUnderlying != null || !actualType.IsValueType) return null;
                return Activator.CreateInstance(actualType);
            }

            if (actualType == typeof(string)) return token.ToString();
            if (actualType == typeof(bool)) return token.Value<bool>();

            if (actualType.IsEnum)
            {
                if (token.Type == JTokenType.String) return Enum.Parse(actualType, token.Value<string>(), true);
                object numeric = Convert.ChangeType(token.ToObject<object>(), Enum.GetUnderlyingType(actualType), CultureInfo.InvariantCulture);
                return Enum.ToObject(actualType, numeric);
            }

            if (actualType == typeof(int)) return token.Value<int>();
            if (actualType == typeof(long)) return token.Value<long>();
            if (actualType == typeof(short)) return token.Value<short>();
            if (actualType == typeof(byte)) return token.Value<byte>();
            if (actualType == typeof(uint)) return token.Value<uint>();
            if (actualType == typeof(ulong)) return token.Value<ulong>();
            if (actualType == typeof(ushort)) return token.Value<ushort>();
            if (actualType == typeof(float)) return token.Value<float>();
            if (actualType == typeof(double)) return token.Value<double>();
            if (actualType == typeof(decimal)) return token.Value<decimal>();

            if (actualType == typeof(Vector2)) return ParseVector2(token);
            if (actualType == typeof(Vector3)) return ParseVector3(token);
            if (actualType == typeof(Vector4)) return ParseVector4(token);
            if (actualType == typeof(Quaternion)) return ParseQuaternion(token);
            if (actualType == typeof(Color)) return ParseColor(token);

            if (typeof(UnityEngine.Object).IsAssignableFrom(actualType))
            {
                UnityEngine.Object unityRef = ParseUnityObjectReference(token, actualType);
                if (unityRef == null && actualType.IsValueType) return Activator.CreateInstance(actualType);
                return unityRef;
            }

            if (token is JValue scalar)
            {
                object raw = scalar.Value;
                if (raw == null) return null;
                return Convert.ChangeType(raw, actualType, CultureInfo.InvariantCulture);
            }

            return token.ToObject(actualType);
        }

        private static float ReadFloatToken(JToken token, string name, float fallback)
        {
            if (token == null) return fallback;
            JToken child = token[name];
            if (child == null || child.Type == JTokenType.Null) return fallback;
            return child.Value<float>();
        }

        private static float ReadArrayFloat(JArray array, int index, float fallback)
        {
            if (array == null || index < 0 || index >= array.Count) return fallback;
            JToken token = array[index];
            if (token == null || token.Type == JTokenType.Null) return fallback;
            return token.Value<float>();
        }

        private static float[] ReadFloatSequenceFromString(string raw)
        {
            if (raw == null || raw.Trim().Length == 0) return new float[0];

            MatchCollection matches = Regex.Matches(
                raw,
                @"[-+]?(?:\d+\.?\d*|\.\d+)(?:[eE][-+]?\d+)?",
                RegexOptions.CultureInvariant);

            if (matches.Count == 0) return new float[0];

            List<float> values = new List<float>(matches.Count);
            for (int i = 0; i < matches.Count; i++)
            {
                float parsed;
                if (!float.TryParse(matches[i].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out parsed))
                {
                    return new float[0];
                }

                values.Add(parsed);
            }

            return values.ToArray();
        }

        private static Vector2 ParseVector2(JToken token)
        {
            if (token != null && token.Type == JTokenType.String)
            {
                float[] values = ReadFloatSequenceFromString(token.Value<string>());
                if (values.Length >= 2) return new Vector2(values[0], values[1]);
            }

            if (token is JArray arr) return new Vector2(ReadArrayFloat(arr, 0, 0f), ReadArrayFloat(arr, 1, 0f));
            return new Vector2(ReadFloatToken(token, "x", 0f), ReadFloatToken(token, "y", 0f));
        }

        private static Vector3 ParseVector3(JToken token)
        {
            if (token != null && token.Type == JTokenType.String)
            {
                float[] values = ReadFloatSequenceFromString(token.Value<string>());
                if (values.Length >= 3) return new Vector3(values[0], values[1], values[2]);
            }

            if (token is JArray arr) return new Vector3(ReadArrayFloat(arr, 0, 0f), ReadArrayFloat(arr, 1, 0f), ReadArrayFloat(arr, 2, 0f));
            return new Vector3(ReadFloatToken(token, "x", 0f), ReadFloatToken(token, "y", 0f), ReadFloatToken(token, "z", 0f));
        }

        private static Vector4 ParseVector4(JToken token)
        {
            if (token != null && token.Type == JTokenType.String)
            {
                float[] values = ReadFloatSequenceFromString(token.Value<string>());
                if (values.Length >= 4) return new Vector4(values[0], values[1], values[2], values[3]);
            }

            if (token is JArray arr) return new Vector4(ReadArrayFloat(arr, 0, 0f), ReadArrayFloat(arr, 1, 0f), ReadArrayFloat(arr, 2, 0f), ReadArrayFloat(arr, 3, 0f));
            return new Vector4(
                ReadFloatToken(token, "x", 0f),
                ReadFloatToken(token, "y", 0f),
                ReadFloatToken(token, "z", 0f),
                ReadFloatToken(token, "w", 0f));
        }

        private static Quaternion ParseQuaternion(JToken token)
        {
            if (token != null && token.Type == JTokenType.String)
            {
                float[] values = ReadFloatSequenceFromString(token.Value<string>());
                if (values.Length >= 4) return new Quaternion(values[0], values[1], values[2], values[3]);
                if (values.Length == 3) return Quaternion.Euler(values[0], values[1], values[2]);
            }

            if (token["euler"] is JObject)
            {
                Vector3 euler = ParseVector3(token["euler"]);
                return Quaternion.Euler(euler);
            }

            if (token["euler"] is JArray eulerArr)
            {
                Vector3 euler = ParseVector3(eulerArr);
                return Quaternion.Euler(euler);
            }

            if (token is JArray arr) return new Quaternion(ReadArrayFloat(arr, 0, 0f), ReadArrayFloat(arr, 1, 0f), ReadArrayFloat(arr, 2, 0f), ReadArrayFloat(arr, 3, 1f));
            return new Quaternion(
                ReadFloatToken(token, "x", 0f),
                ReadFloatToken(token, "y", 0f),
                ReadFloatToken(token, "z", 0f),
                ReadFloatToken(token, "w", 1f));
        }

        private static Color ParseColor(JToken token)
        {
            if (token is JArray arr)
            {
                return new Color(
                    ReadArrayFloat(arr, 0, 0f),
                    ReadArrayFloat(arr, 1, 0f),
                    ReadArrayFloat(arr, 2, 0f),
                    ReadArrayFloat(arr, 3, 1f));
            }

            if (token.Type == JTokenType.String)
            {
                string raw = token.Value<string>();
                Color parsed;
                if (ColorUtility.TryParseHtmlString(raw, out parsed)) return parsed;

                float[] values = ReadFloatSequenceFromString(raw);
                if (values.Length >= 4) return new Color(values[0], values[1], values[2], values[3]);
                if (values.Length == 3) return new Color(values[0], values[1], values[2], 1f);
            }

            return new Color(
                ReadFloatToken(token, "r", 0f),
                ReadFloatToken(token, "g", 0f),
                ReadFloatToken(token, "b", 0f),
                ReadFloatToken(token, "a", 1f));
        }

        private static UnityEngine.Object ParseUnityObjectReference(JToken token, Type targetType)
        {
            int id;
            if (token.Type == JTokenType.Integer)
            {
                id = token.Value<int>();
            }
            else
            {
                JToken idToken = token["instance_id"];
                if (idToken == null || idToken.Type == JTokenType.Null) return null;
                id = idToken.Value<int>();
            }

            GameObject go = FindGameObjectById(id);
            if (go != null && targetType.IsAssignableFrom(typeof(GameObject))) return go;

            Component comp = FindComponentById(id);
            if (comp != null)
            {
                object reflectionTarget = ResolveComponentReflectionTarget(comp);
                if (targetType.IsAssignableFrom(comp.GetType())) return comp;
                if (reflectionTarget is UnityEngine.Object refObj && targetType.IsAssignableFrom(refObj.GetType())) return refObj;
                if (targetType == typeof(UnityEngine.Object)) return reflectionTarget as UnityEngine.Object ?? comp;
            }

            if (go != null && targetType == typeof(UnityEngine.Object)) return go;
            return null;
        }

        private static string NormalizeMode(string mode)
        {
            if (string.IsNullOrEmpty(mode)) return "contains";
            string lowered = mode.ToLowerInvariant();
            if (lowered == "contains" || lowered == "exact" || lowered == "regex" || lowered == "starts_with" || lowered == "ends_with") return lowered;
            return "contains";
        }

        private static bool TextMatch(string actual, string query, string mode)
        {
            actual = actual ?? string.Empty;
            query = query ?? string.Empty;

            switch (mode)
            {
                case "exact": return string.Equals(actual, query, StringComparison.OrdinalIgnoreCase);
                case "starts_with": return actual.StartsWith(query, StringComparison.OrdinalIgnoreCase);
                case "ends_with": return actual.EndsWith(query, StringComparison.OrdinalIgnoreCase);
                case "regex":
                    try { return Regex.IsMatch(actual, query, RegexOptions.IgnoreCase); }
                    catch { return false; }
                default: return actual.IndexOf(query, StringComparison.OrdinalIgnoreCase) >= 0;
            }
        }

        private static int Clamp(int value, int min, int max)
        {
            if (value < min) return min;
            if (value > max) return max;
            return value;
        }

        private static bool ArgBool(JObject args, string name, bool fallback)
        {
            JToken token = args[name];
            if (token == null) return fallback;
            bool parsed;
            return bool.TryParse(token.ToString(), out parsed) ? parsed : fallback;
        }

        private static int ArgInt(JObject args, string name, int fallback)
        {
            JToken token = args[name];
            if (token == null) return fallback;
            int parsed;
            return int.TryParse(token.ToString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out parsed) ? parsed : fallback;
        }

        private static int ArgRequiredInt(JObject args, string name)
        {
            JToken token = args[name];
            if (token == null) throw new BridgeRpcException(-32602, "invalid_params", "Missing required param: " + name);
            int parsed;
            if (!int.TryParse(token.ToString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out parsed))
                throw new BridgeRpcException(-32602, "invalid_params", "Invalid int param: " + name);
            return parsed;
        }

        private static string ArgString(JObject args, string name, string fallback)
        {
            JToken token = args[name];
            if (token == null) return fallback;
            string value = token.Value<string>();
            return value ?? fallback;
        }

        private static string ArgRequiredString(JObject args, string name)
        {
            string value = ArgString(args, name, null);
            if (string.IsNullOrEmpty(value)) throw new BridgeRpcException(-32602, "invalid_params", "Missing required param: " + name);
            return value;
        }

        private static JToken ArgRequiredToken(JObject args, string name)
        {
            JToken token = args[name];
            if (token == null) throw new BridgeRpcException(-32602, "invalid_params", "Missing required param: " + name);
            return token;
        }

        private static string[] ArgStringArray(JObject args, string name, string[] fallback)
        {
            JArray array = args[name] as JArray;
            if (array == null) return fallback;
            List<string> values = new List<string>();
            for (int i = 0; i < array.Count; i++)
            {
                string value = array[i].Value<string>();
                if (!string.IsNullOrEmpty(value)) values.Add(value);
            }
            return values.Count > 0 ? values.ToArray() : fallback;
        }
    }
}
