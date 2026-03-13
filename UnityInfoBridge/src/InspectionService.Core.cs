using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Reflection;
using Newtonsoft.Json.Linq;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace UnityInfoBridge
{
    internal static partial class UnityInspectionService
    {
        private static readonly string[] DefaultTextTypes =
        {
            "*"
        };

        private static readonly string[] MethodNames =
        {
            "ping", "get_capabilities", "get_runtime_summary", "list_scenes",
            "get_scene_hierarchy", "find_gameobjects_by_name", "resolve_instance_id",
            "get_gameobject", "get_gameobject_by_path", "get_gameobject_children",
            "get_components", "get_component", "get_component_fields", "search_component_fields",
            "list_text_elements", "search_text", "get_text_context",
            "snapshot_gameobject", "snapshot_scene",
            "set_gameobject_active", "set_component_member", "set_text", "capture_screenshot"
        };

        public static object Dispatch(string method, JObject args)
        {
            switch (method)
            {
                case "ping": return Ping(args);
                case "get_capabilities": return GetCapabilities();
                case "get_runtime_summary": return RuntimeSummary();
                case "list_scenes": return ListScenes();
                case "get_scene_hierarchy": return SceneHierarchy(args);
                case "find_gameobjects_by_name": return FindByName(args);
                case "resolve_instance_id": return ResolveInstanceId(args);
                case "get_gameobject": return GetGameObject(args);
                case "get_gameobject_by_path": return GetGameObjectByPath(args);
                case "get_gameobject_children": return GetGameObjectChildren(args);
                case "get_components": return GetComponents(args);
                case "get_component": return GetComponent(args);
                case "get_component_fields": return GetComponentFields(args);
                case "search_component_fields": return SearchComponentFields(args);
                case "list_text_elements": return ListTextElements(args);
                case "search_text": return SearchText(args);
                case "get_text_context": return GetTextContext(args);
                case "snapshot_gameobject": return SnapshotGameObject(args);
                case "snapshot_scene": return SnapshotScene(args);
                case "set_gameobject_active": return SetGameObjectActive(args);
                case "set_component_member": return SetComponentMember(args);
                case "set_text": return SetText(args);
                case "capture_screenshot": return CaptureScreenshot(args);
                default:
                    throw new BridgeRpcException(-32601, "method_not_found", new Dictionary<string, object> { { "method", method } });
            }
        }

        private static object Ping(JObject args)
        {
            Dictionary<string, object> payload = new Dictionary<string, object>
            {
                { "status", "ok" },
                { "bridge", "UnityInfoBridge" },
                { "game_connected", true }
            };
            if (ArgBool(args, "include_runtime", false)) payload["runtime"] = RuntimeSummary();
            return payload;
        }

        private static object GetCapabilities()
        {
            return new Dictionary<string, object> { { "methods", MethodNames } };
        }

        private static object RuntimeSummary()
        {
            Scene active = SceneManager.GetActiveScene();
            int bridgePort = BridgeServer.Instance.BoundPort;
            return new Dictionary<string, object>
            {
                { "process_name", Process.GetCurrentProcess().ProcessName },
                { "process_id", Process.GetCurrentProcess().Id },
                { "unity_version", Application.unityVersion },
                { "platform", Application.platform.ToString() },
                { "product_name", Application.productName },
                { "company_name", Application.companyName },
                { "active_scene", active.IsValid() ? active.name : string.Empty },
                { "loaded_scene_count", SceneManager.sceneCount },
                { "frame", Time.frameCount },
                { "time_since_startup", Time.realtimeSinceStartup },
                { "bridge_host", BridgeServer.BindHost },
                { "bridge_port", bridgePort },
                { "bridge_port_range_start", BridgeServer.PortRangeStart },
                { "bridge_port_range_end", BridgeServer.PortRangeEnd }
            };
        }

        private static object ListScenes()
        {
            List<object> items = new List<object>();
            Scene active = SceneManager.GetActiveScene();
            for (int i = 0; i < SceneManager.sceneCount; i++)
            {
                Scene scene = SceneManager.GetSceneAt(i);
                items.Add(SceneInfo(scene, active.handle));
            }
            return new Dictionary<string, object> { { "items", items }, { "count", items.Count } };
        }

        private static object SceneHierarchy(JObject args)
        {
            Scene scene = ResolveScene(args);
            if (!scene.IsValid() || !scene.isLoaded) throw SceneNotFound(args);

            int depth = Clamp(ArgInt(args, "depth_limit", 2), 0, 12);
            bool includeInactive = ArgBool(args, "include_inactive", true);
            bool includeComponents = ArgBool(args, "include_components", false);
            List<object> roots = new List<object>();

            GameObject[] rootObjects = scene.GetRootGameObjects();
            for (int i = 0; i < rootObjects.Length; i++)
            {
                if (!includeInactive && !rootObjects[i].activeInHierarchy) continue;
                roots.Add(GameObjectTree(rootObjects[i], depth, includeComponents, false, 0, includeInactive));
            }

            return new Dictionary<string, object>
            {
                { "scene_name", scene.name },
                { "scene_handle", scene.handle },
                { "roots", roots },
                { "root_count", roots.Count }
            };
        }

        private static object FindByName(JObject args)
        {
            string query = ArgRequiredString(args, "name_query");
            string mode = NormalizeMode(ArgString(args, "match_mode", "contains"));
            string sceneName = ArgString(args, "scene_name", null);
            bool includeInactive = ArgBool(args, "include_inactive", true);
            int limit = Clamp(ArgInt(args, "limit", 200), 1, 10000);

            List<GameObject> all = CollectGameObjects(sceneName, includeInactive);
            List<object> items = new List<object>();
            for (int i = 0; i < all.Count; i++)
            {
                if (!TextMatch(all[i].name, query, mode)) continue;
                items.Add(GameObjectInfo(all[i]));
                if (items.Count >= limit) break;
            }
            return new Dictionary<string, object> { { "items", items }, { "count", items.Count } };
        }

        private static object ResolveInstanceId(JObject args)
        {
            int id = ArgRequiredInt(args, "instance_id");
            GameObject go = FindGameObjectById(id);
            if (go != null) return new Dictionary<string, object> { { "kind", "GameObject" }, { "value", GameObjectInfo(go) } };

            Component component = FindComponentById(id);
            if (component != null)
            {
                Dictionary<string, object> info = ComponentInfo(component, false, 0, false);
                info["owner"] = GameObjectInfo(component.gameObject);
                return new Dictionary<string, object> { { "kind", "Component" }, { "value", info } };
            }

            throw new BridgeRpcException(-32011, "object_not_found", new Dictionary<string, object> { { "instance_id", id } });
        }

        private static object GetGameObject(JObject args)
        {
            int id = ArgRequiredInt(args, "instance_id");
            GameObject go = FindGameObjectById(id);
            if (go == null) throw new BridgeRpcException(-32011, "object_not_found", new Dictionary<string, object> { { "instance_id", id } });
            return GameObjectInfo(go);
        }

        private static object GetGameObjectByPath(JObject args)
        {
            string path = ArgRequiredString(args, "path");
            string sceneName = ArgString(args, "scene_name", null);
            bool includeInactive = ArgBool(args, "include_inactive", true);
            List<GameObject> all = CollectGameObjects(sceneName, includeInactive);
            for (int i = 0; i < all.Count; i++)
            {
                if (string.Equals(PathOf(all[i].transform), path, StringComparison.Ordinal)) return GameObjectInfo(all[i]);
            }
            throw new BridgeRpcException(-32011, "object_not_found", new Dictionary<string, object> { { "path", path } });
        }

        private static object GetGameObjectChildren(JObject args)
        {
            int id = ArgRequiredInt(args, "instance_id");
            GameObject parent = FindGameObjectById(id);
            if (parent == null) throw new BridgeRpcException(-32011, "object_not_found", new Dictionary<string, object> { { "instance_id", id } });

            bool recursive = ArgBool(args, "recursive", false);
            int depth = Clamp(ArgInt(args, "depth_limit", 2), 0, 12);
            bool includeComponents = ArgBool(args, "include_components", false);
            bool includeInactive = ArgBool(args, "include_inactive", true);
            List<object> items = new List<object>();

            for (int i = 0; i < parent.transform.childCount; i++)
            {
                Transform child = parent.transform.GetChild(i);
                if (!includeInactive && !child.gameObject.activeInHierarchy) continue;
                items.Add(GameObjectTree(child.gameObject, recursive ? depth : 0, includeComponents, false, 0, includeInactive));
            }

            return new Dictionary<string, object> { { "items", items }, { "count", items.Count } };
        }

        private static object GetComponents(JObject args)
        {
            int goId = ArgRequiredInt(args, "gameobject_instance_id");
            GameObject go = FindGameObjectById(goId);
            if (go == null) throw new BridgeRpcException(-32011, "object_not_found", new Dictionary<string, object> { { "gameobject_instance_id", goId } });

            bool includeFields = ArgBool(args, "include_fields", false);
            int fieldDepth = Clamp(ArgInt(args, "field_depth", 1), 0, 8);
            bool includeNonPublic = ArgBool(args, "include_non_public", false);
            List<object> comps = ComponentsOf(go, includeFields, fieldDepth, includeNonPublic);

            return new Dictionary<string, object> { { "gameobject", GameObjectInfo(go) }, { "items", comps }, { "count", comps.Count } };
        }

        private static object GetComponent(JObject args)
        {
            int id = ArgRequiredInt(args, "component_instance_id");
            Component component = FindComponentById(id);
            if (component == null) throw new BridgeRpcException(-32013, "component_not_found", new Dictionary<string, object> { { "component_instance_id", id } });

            bool includeFields = ArgBool(args, "include_fields", true);
            int fieldDepth = Clamp(ArgInt(args, "field_depth", 2), 0, 8);
            bool includeNonPublic = ArgBool(args, "include_non_public", false);
            Dictionary<string, object> info = ComponentInfo(component, includeFields, fieldDepth, includeNonPublic);
            info["owner_gameobject"] = GameObjectInfo(component.gameObject);
            return info;
        }

        private static object GetComponentFields(JObject args)
        {
            int id = ArgRequiredInt(args, "component_instance_id");
            Component component = FindComponentById(id);
            if (component == null) throw new BridgeRpcException(-32013, "component_not_found", new Dictionary<string, object> { { "component_instance_id", id } });

            bool includeNonPublic = ArgBool(args, "include_non_public", false);
            int maxDepth = Clamp(ArgInt(args, "max_depth", 2), 0, 8);
            bool includeProperties = ArgBool(args, "include_properties", false);
            object reflectionTarget = ResolveComponentReflectionTarget(component);
            return new Dictionary<string, object>
            {
                { "component", ComponentInfo(component, false, 0, false) },
                { "fields", ReadMembers(reflectionTarget, includeNonPublic, maxDepth, includeProperties) },
                { "reflection_target_type", SafeTypeName(reflectionTarget) }
            };
        }

        private static object SearchComponentFields(JObject args)
        {
            string query = ArgRequiredString(args, "value_query");
            string sceneName = ArgString(args, "scene_name", null);
            string typeFilter = ArgString(args, "component_type", null);
            string fieldFilter = ArgString(args, "field_name", null);
            string mode = NormalizeMode(ArgString(args, "match_mode", "contains"));
            bool includeInactive = ArgBool(args, "include_inactive", true);
            int limit = Clamp(ArgInt(args, "limit", 200), 1, 10000);
            return SearchFields(query, sceneName, typeFilter, fieldFilter, mode, includeInactive, limit);
        }

        private static object ListTextElements(JObject args)
        {
            string sceneName = ArgString(args, "scene_name", null);
            bool includeInactive = ArgBool(args, "include_inactive", true);
            int limit = Clamp(ArgInt(args, "limit", 500), 1, 20000);
            string[] types = ArgStringArray(args, "component_types", DefaultTextTypes);
            return EnumerateTextElements(sceneName, includeInactive, types, limit);
        }

        private static object SearchText(JObject args)
        {
            string query = ArgRequiredString(args, "query");
            string mode = NormalizeMode(ArgString(args, "match_mode", "contains"));
            string sceneName = ArgString(args, "scene_name", null);
            bool includeInactive = ArgBool(args, "include_inactive", true);
            int limit = Clamp(ArgInt(args, "limit", 500), 1, 20000);
            string[] types = ArgStringArray(args, "component_types", DefaultTextTypes);
            return SearchTextElements(query, mode, sceneName, includeInactive, types, limit);
        }

        private static object GetTextContext(JObject args)
        {
            int id = ArgRequiredInt(args, "component_instance_id");
            bool includeNeighbors = ArgBool(args, "include_neighbors", true);
            bool includeSiblingTexts = ArgBool(args, "include_sibling_texts", true);
            return BuildTextContext(id, includeNeighbors, includeSiblingTexts);
        }

        private static object SnapshotGameObject(JObject args)
        {
            int id = ArgRequiredInt(args, "instance_id");
            GameObject go = FindGameObjectById(id);
            if (go == null) throw new BridgeRpcException(-32011, "object_not_found", new Dictionary<string, object> { { "instance_id", id } });
            int depth = Clamp(ArgInt(args, "include_children_depth", 1), 0, 10);
            bool includeComponents = ArgBool(args, "include_components", true);
            bool includeFields = ArgBool(args, "include_fields", false);
            int fieldDepth = Clamp(ArgInt(args, "field_depth", 1), 0, 6);
            return GameObjectTree(go, depth, includeComponents, includeFields, fieldDepth, true);
        }

        private static object SnapshotScene(JObject args)
        {
            Scene scene = ResolveScene(args);
            if (!scene.IsValid() || !scene.isLoaded) throw SceneNotFound(args);
            int depth = Clamp(ArgInt(args, "hierarchy_depth", 2), 0, 10);
            bool includeComponents = ArgBool(args, "include_components", false);
            bool includeFields = ArgBool(args, "include_fields", false);
            int fieldDepth = Clamp(ArgInt(args, "field_depth", 1), 0, 6);
            return BuildSceneSnapshot(scene, depth, includeComponents, includeFields, fieldDepth);
        }

        private static object SetGameObjectActive(JObject args)
        {
            int id = ArgRequiredInt(args, "instance_id");
            bool active = ArgBool(args, "active", true);
            GameObject go = FindGameObjectById(id);
            if (go == null) throw new BridgeRpcException(-32011, "object_not_found", new Dictionary<string, object> { { "instance_id", id } });

            bool beforeSelf = go.activeSelf;
            bool beforeHierarchy = go.activeInHierarchy;
            go.SetActive(active);

            return new Dictionary<string, object>
            {
                { "gameobject", GameObjectInfo(go) },
                { "active_before_self", beforeSelf },
                { "active_before_hierarchy", beforeHierarchy },
                { "active_after_self", go.activeSelf },
                { "active_after_hierarchy", go.activeInHierarchy }
            };
        }

        private static object SetComponentMember(JObject args)
        {
            int id = ArgRequiredInt(args, "component_instance_id");
            string memberName = ArgRequiredString(args, "member_name");
            JToken valueToken = ArgRequiredToken(args, "value");
            bool includeNonPublic = ArgBool(args, "include_non_public", false);

            Component component = FindComponentById(id);
            if (component == null) throw new BridgeRpcException(-32013, "component_not_found", new Dictionary<string, object> { { "component_instance_id", id } });

            object reflectionTarget = ResolveComponentReflectionTarget(component);
            Dictionary<string, object> write = SetMemberValue(reflectionTarget, memberName, valueToken, includeNonPublic);

            Dictionary<string, object> result = new Dictionary<string, object>
            {
                { "component", ComponentInfo(component, false, 0, false) },
                { "member_name", memberName },
                { "write_result", write }
            };

            return result;
        }

        private static object SetText(JObject args)
        {
            int id = ArgRequiredInt(args, "component_instance_id");
            string text = ArgRequiredString(args, "text");
            bool includeNonPublic = ArgBool(args, "include_non_public", true);

            Component component = FindComponentById(id);
            if (component == null) throw new BridgeRpcException(-32013, "component_not_found", new Dictionary<string, object> { { "component_instance_id", id } });

            object reflectionTarget = ResolveComponentReflectionTarget(component);
            string beforeSource;
            string beforeText = ReadText(reflectionTarget, out beforeSource);
            Dictionary<string, object> write = TrySetText(reflectionTarget, text, includeNonPublic);
            string afterSource;
            string afterText = ReadText(reflectionTarget, out afterSource);

            return new Dictionary<string, object>
            {
                { "component", ComponentInfo(component, false, 0, false) },
                { "before_text", beforeText },
                { "before_text_source", beforeSource },
                { "after_text", afterText },
                { "after_text_source", afterSource },
                { "write_result", write },
                { "text_details", ReadTextDetails(component, reflectionTarget) }
            };
        }

        private static object CaptureScreenshot(JObject args)
        {
            int superSize = Clamp(ArgInt(args, "super_size", 1), 1, 8);
            bool overwrite = ArgBool(args, "overwrite", true);
            string outputPathArg = ArgString(args, "output_path", null);

            string baseDir = ResolveCaptureBaseDirectory();
            Directory.CreateDirectory(baseDir);

            string outputPath = outputPathArg;
            if (string.IsNullOrEmpty(outputPath))
            {
                string fileName = "capture_" + DateTime.Now.ToString("yy-MM-dd-HH-mm-ss-fff", CultureInfo.InvariantCulture) + ".png";
                outputPath = Path.Combine(baseDir, fileName);
            }
            else
            {
                outputPath = Path.IsPathRooted(outputPath) ? outputPath : Path.GetFullPath(Path.Combine(baseDir, outputPath));
                if (string.IsNullOrEmpty(Path.GetExtension(outputPath))) outputPath += ".png";
            }

            string dir = Path.GetDirectoryName(outputPath);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            if (!overwrite && File.Exists(outputPath))
            {
                throw new BridgeRpcException(-32021, "file_exists", new Dictionary<string, object> { { "output_path", outputPath } });
            }

            if (overwrite && File.Exists(outputPath))
            {
                try { File.Delete(outputPath); }
                catch { }
            }

            Stopwatch sw = Stopwatch.StartNew();
            bool fileReady = false;
            string captureMode = "immediate_texture";
            string fallbackReason = string.Empty;
            long bytes = 0;

            Exception immediateError;
            fileReady = TryWriteScreenshotPngNow(outputPath, superSize, out bytes, out immediateError);
            if (!fileReady)
            {
                captureMode = "queued_file";
                fallbackReason = immediateError != null ? immediateError.Message : "immediate_capture_unavailable";
                try
                {
                    QueueScreenshotToFile(outputPath, superSize);
                }
                catch (Exception ex)
                {
                    throw new BridgeRpcException(-32022, "capture_failed", new Dictionary<string, object>
                    {
                        { "message", ex.Message },
                        { "output_path", outputPath },
                        { "fallback_reason", fallbackReason }
                    });
                }
            }

            return new Dictionary<string, object>
            {
                { "output_path", outputPath },
                { "capture_base_dir", baseDir },
                { "bytes_written", bytes },
                { "super_size", superSize },
                { "frame", Time.frameCount },
                { "capture_time_ms", sw.ElapsedMilliseconds },
                { "capture_mode", captureMode },
                { "file_ready", fileReady },
                { "fallback_reason", fallbackReason }
            };
        }

        private static string ResolveCaptureBaseDirectory()
        {
            try
            {
                string dataPath = Application.dataPath;
                if (!string.IsNullOrEmpty(dataPath))
                {
                    string fullDataPath = Path.GetFullPath(dataPath);
                    DirectoryInfo dataDir = new DirectoryInfo(fullDataPath);
                    if (dataDir.Parent != null)
                    {
                        return Path.Combine(Path.Combine(dataDir.Parent.FullName, "UnityInfoBridge"), "captures");
                    }
                }
            }
            catch { }

            try
            {
                string currentDirectory = Environment.CurrentDirectory;
                if (!string.IsNullOrEmpty(currentDirectory))
                {
                    return Path.Combine(Path.Combine(Path.GetFullPath(currentDirectory), "UnityInfoBridge"), "captures");
                }
            }
            catch { }

            return Path.Combine(Path.Combine(Application.persistentDataPath, "UnityInfoBridge"), "captures");
        }

        private static bool TryWriteScreenshotPngNow(string outputPath, int superSize, out long bytesWritten, out Exception error)
        {
            bytesWritten = 0;
            error = null;
            Texture2D texture = null;
            try
            {
                texture = TryCaptureScreenshotTexture(superSize);
                if (texture == null) throw new InvalidOperationException("Texture capture returned null.");

                byte[] png = TryEncodeTextureToPng(texture);
                if (png == null || png.Length == 0) throw new InvalidOperationException("EncodeToPNG produced empty bytes.");

                File.WriteAllBytes(outputPath, png);
                bytesWritten = png.LongLength;
                return true;
            }
            catch (Exception ex)
            {
                error = ex;
                return false;
            }
            finally
            {
                if (texture != null)
                {
                    try { UnityEngine.Object.Destroy(texture); }
                    catch { }
                }
            }
        }

        private static Texture2D TryCaptureScreenshotTexture(int superSize)
        {
            MethodInfo captureAsTexture = ResolveCaptureScreenshotAsTextureMethod();
            if (captureAsTexture != null)
            {
                ParameterInfo[] parameters = captureAsTexture.GetParameters();
                object[] invokeArgs = parameters.Length == 0 ? null : new object[] { superSize };
                Texture2D texture = captureAsTexture.Invoke(null, invokeArgs) as Texture2D;
                if (texture != null) return texture;
            }

            // Fallback path for runtimes without ScreenCapture.CaptureScreenshotAsTexture.
            int width = Mathf.Max(1, Screen.width);
            int height = Mathf.Max(1, Screen.height);
            Texture2D fallback = new Texture2D(width, height, TextureFormat.RGB24, false);
            fallback.ReadPixels(new Rect(0f, 0f, width, height), 0, 0);
            fallback.Apply();
            return fallback;
        }

        private static byte[] TryEncodeTextureToPng(Texture2D texture)
        {
            if (texture == null) return null;

            // Mono runtimes usually expose Texture2D.EncodeToPNG instance method.
            try
            {
                MethodInfo instance = texture.GetType().GetMethod("EncodeToPNG", BindingFlags.Public | BindingFlags.Instance, null, Type.EmptyTypes, null);
                if (instance != null)
                {
                    object png = instance.Invoke(texture, null);
                    byte[] bytes = TryCoerceByteArray(png);
                    if (bytes != null && bytes.Length > 0) return bytes;
                }
            }
            catch { }

            // Some IL2CPP targets expose static ImageConversion.EncodeToPNG(Texture2D).
            try
            {
                Type imageConversion = Type.GetType("UnityEngine.ImageConversion, UnityEngine.ImageConversionModule");
                if (imageConversion == null) imageConversion = Type.GetType("UnityEngine.ImageConversion, UnityEngine");
                if (imageConversion != null)
                {
                    MethodInfo staticEncode = imageConversion.GetMethod("EncodeToPNG", BindingFlags.Public | BindingFlags.Static, null, new[] { typeof(Texture2D) }, null);
                    if (staticEncode != null)
                    {
                        object png = staticEncode.Invoke(null, new object[] { texture });
                        byte[] bytes = TryCoerceByteArray(png);
                        if (bytes != null && bytes.Length > 0) return bytes;
                    }
                }
            }
            catch { }

            return null;
        }

        private static byte[] TryCoerceByteArray(object value)
        {
            if (value == null) return null;

            byte[] direct = value as byte[];
            if (direct != null && direct.Length > 0) return direct;

            Array array = value as Array;
            if (array != null)
            {
                byte[] copied = CopyByteArray(array.Length, delegate(int index) { return array.GetValue(index); });
                if (copied != null && copied.Length > 0) return copied;
            }

            try
            {
                IEnumerable enumerable = value as IEnumerable;
                if (enumerable != null && !(value is string))
                {
                    List<byte> bytes = new List<byte>();
                    foreach (object item in enumerable)
                    {
                        byte parsed;
                        if (!TryConvertToByte(item, out parsed)) return null;
                        bytes.Add(parsed);
                    }

                    if (bytes.Count > 0) return bytes.ToArray();
                }
            }
            catch { }

            try
            {
                Type valueType = value.GetType();
                PropertyInfo lengthProp = valueType.GetProperty("Length", BindingFlags.Public | BindingFlags.Instance)
                    ?? valueType.GetProperty("Count", BindingFlags.Public | BindingFlags.Instance);
                PropertyInfo indexer = valueType.GetProperty("Item", BindingFlags.Public | BindingFlags.Instance, null, null, new[] { typeof(int) }, null);
                if (lengthProp != null && indexer != null)
                {
                    object rawLength = lengthProp.GetValue(value, null);
                    int length = rawLength == null ? 0 : Convert.ToInt32(rawLength, CultureInfo.InvariantCulture);
                    byte[] copied = CopyByteArray(length, delegate(int index) { return indexer.GetValue(value, new object[] { index }); });
                    if (copied != null && copied.Length > 0) return copied;
                }
            }
            catch { }

            try
            {
                MethodInfo toArray = value.GetType().GetMethod("ToArray", BindingFlags.Public | BindingFlags.Instance, null, Type.EmptyTypes, null);
                if (toArray != null)
                {
                    object arrayValue = toArray.Invoke(value, null);
                    if (!ReferenceEquals(arrayValue, value))
                    {
                        byte[] copied = TryCoerceByteArray(arrayValue);
                        if (copied != null && copied.Length > 0) return copied;
                    }
                }
            }
            catch { }

            return null;
        }

        private static byte[] CopyByteArray(int length, Func<int, object> getter)
        {
            if (length <= 0 || getter == null) return null;

            byte[] output = new byte[length];
            for (int i = 0; i < length; i++)
            {
                byte parsed;
                if (!TryConvertToByte(getter(i), out parsed)) return null;
                output[i] = parsed;
            }
            return output;
        }

        private static bool TryConvertToByte(object value, out byte output)
        {
            output = 0;
            if (value == null) return false;

            if (value is byte) { output = (byte)value; return true; }
            if (value is sbyte) { output = unchecked((byte)(sbyte)value); return true; }

            try
            {
                output = Convert.ToByte(value, CultureInfo.InvariantCulture);
                return true;
            }
            catch { }

            try
            {
                output = byte.Parse(value.ToString(), NumberStyles.Integer, CultureInfo.InvariantCulture);
                return true;
            }
            catch { }

            return false;
        }

        private static void QueueScreenshotToFile(string outputPath, int superSize)
        {
            MethodInfo captureToFile = ResolveCaptureScreenshotToFileMethod();
            if (captureToFile != null)
            {
                ParameterInfo[] parameters = captureToFile.GetParameters();
                if (parameters.Length == 1)
                {
                    captureToFile.Invoke(null, new object[] { outputPath });
                    return;
                }

                if (parameters.Length >= 2)
                {
                    captureToFile.Invoke(null, new object[] { outputPath, superSize });
                    return;
                }
            }

            Application.CaptureScreenshot(outputPath, superSize);
        }

        private static MethodInfo ResolveCaptureScreenshotAsTextureMethod()
        {
            Type screenCaptureType = Type.GetType("UnityEngine.ScreenCapture, UnityEngine.CoreModule");
            if (screenCaptureType == null) screenCaptureType = Type.GetType("UnityEngine.ScreenCapture, UnityEngine");
            if (screenCaptureType == null) return null;

            MethodInfo withSuperSize = screenCaptureType.GetMethod("CaptureScreenshotAsTexture", BindingFlags.Public | BindingFlags.Static, null, new[] { typeof(int) }, null);
            if (withSuperSize != null) return withSuperSize;

            MethodInfo noArg = screenCaptureType.GetMethod("CaptureScreenshotAsTexture", BindingFlags.Public | BindingFlags.Static, null, Type.EmptyTypes, null);
            return noArg;
        }

        private static MethodInfo ResolveCaptureScreenshotToFileMethod()
        {
            Type screenCaptureType = Type.GetType("UnityEngine.ScreenCapture, UnityEngine.CoreModule");
            if (screenCaptureType == null) screenCaptureType = Type.GetType("UnityEngine.ScreenCapture, UnityEngine");
            if (screenCaptureType == null) return null;

            MethodInfo withSuperSize = screenCaptureType.GetMethod("CaptureScreenshot", BindingFlags.Public | BindingFlags.Static, null, new[] { typeof(string), typeof(int) }, null);
            if (withSuperSize != null) return withSuperSize;

            MethodInfo noArg = screenCaptureType.GetMethod("CaptureScreenshot", BindingFlags.Public | BindingFlags.Static, null, new[] { typeof(string) }, null);
            return noArg;
        }
    }
}
