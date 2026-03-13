using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using UnityEngine;

#if IL2CPP
using Il2CppInterop.Runtime.Injection;
#endif

namespace UnityInfoBridge
{
    internal static class BridgeBootstrap
    {
        private static readonly object Gate = new object();
        private static bool _started;

        public static void Start(Action<string> info, Action<string> warn, Action<string> error)
        {
            lock (Gate)
            {
                if (_started)
                {
                    return;
                }

                BridgeLog.Set(info, warn, error);

                try
                {
#if IL2CPP
                    ClassInjector.RegisterTypeInIl2Cpp<BridgeRuntimeBehaviour>();
#endif
                }
                catch (Exception ex)
                {
                    BridgeLog.Warn("Class injection skipped: " + ex.Message);
                }

                GameObject host = new GameObject("UnityInfoBridge.Host");
                UnityEngine.Object.DontDestroyOnLoad(host);
                host.hideFlags = HideFlags.HideAndDontSave;
                host.AddComponent<BridgeRuntimeBehaviour>();

                _started = true;
                BridgeLog.Info("Bootstrapped.");
            }
        }
    }

    internal sealed class BridgeRuntimeBehaviour : MonoBehaviour
    {
#if IL2CPP
        public BridgeRuntimeBehaviour(IntPtr ptr) : base(ptr) { }
#endif
        private float _nextRunInBackgroundCheckAt;

        private void Awake()
        {
            EnsureRunInBackground("Awake");
            MainThreadDispatcher.Initialize();
            BridgeServer.Instance.Start();
        }

        private void Update()
        {
            MainThreadDispatcher.Pump();

            if (Time.unscaledTime >= _nextRunInBackgroundCheckAt)
            {
                _nextRunInBackgroundCheckAt = Time.unscaledTime + 1f;
                EnsureRunInBackground("Update");
            }
        }

        private void OnApplicationFocus(bool hasFocus)
        {
            if (!hasFocus)
            {
                EnsureRunInBackground("OnApplicationFocus(false)");
            }
        }

        private void OnApplicationPause(bool pauseStatus)
        {
            if (pauseStatus)
            {
                EnsureRunInBackground("OnApplicationPause(true)");
            }
        }

        private void OnDestroy()
        {
            BridgeServer.Instance.Stop();
        }

        private static void EnsureRunInBackground(string reason)
        {
            try
            {
                if (!Application.runInBackground)
                {
                    Application.runInBackground = true;
                    BridgeLog.Info("Forced Application.runInBackground=true (" + reason + ")");
                }
            }
            catch (Exception ex)
            {
                BridgeLog.Warn("Failed to force runInBackground (" + reason + "): " + ex.Message);
            }
        }
    }

    internal static class BridgeLog
    {
        private static Action<string> _info = delegate { };
        private static Action<string> _warn = delegate { };
        private static Action<string> _error = delegate { };

        public static void Set(Action<string> info, Action<string> warn, Action<string> error)
        {
            _info = info ?? delegate { };
            _warn = warn ?? delegate { };
            _error = error ?? delegate { };
        }

        public static void Info(string msg) { _info("[UnityInfoBridge] " + msg); }
        public static void Warn(string msg) { _warn("[UnityInfoBridge] " + msg); }
        public static void Error(string msg) { _error("[UnityInfoBridge] " + msg); }
    }

    internal static class MainThreadDispatcher
    {
        private sealed class Work
        {
            public Func<object> Callback;
            public ManualResetEvent Wait;
            public object Result;
            public Exception Error;
        }

        private static readonly Queue<Work> Queue = new Queue<Work>();
        private static readonly object Sync = new object();
        private static int _mainThreadId;

        public static void Initialize()
        {
            _mainThreadId = Thread.CurrentThread.ManagedThreadId;
        }

        public static object Invoke(Func<object> callback, int timeoutMs)
        {
            if (_mainThreadId == 0 || Thread.CurrentThread.ManagedThreadId == _mainThreadId)
            {
                return callback();
            }

            Work work = new Work { Callback = callback, Wait = new ManualResetEvent(false) };
            lock (Sync)
            {
                Queue.Enqueue(work);
            }

            if (!work.Wait.WaitOne(timeoutMs))
            {
                int pending = 0;
                lock (Sync)
                {
                    pending = Queue.Count;
                }
                throw new TimeoutException("Main thread call timed out after " + timeoutMs + "ms. Pending queue: " + pending);
            }

            if (work.Error != null)
            {
                throw work.Error;
            }

            return work.Result;
        }

        public static void Pump()
        {
            if (_mainThreadId == 0)
            {
                _mainThreadId = Thread.CurrentThread.ManagedThreadId;
            }

            while (true)
            {
                Work work = null;
                lock (Sync)
                {
                    if (Queue.Count > 0)
                    {
                        work = Queue.Dequeue();
                    }
                }

                if (work == null)
                {
                    break;
                }

                try
                {
                    work.Result = work.Callback();
                }
                catch (Exception ex)
                {
                    work.Error = ex;
                }
                finally
                {
                    work.Wait.Set();
                }
            }
        }
    }

    internal sealed class BridgeServer
    {
        internal static readonly BridgeServer Instance = new BridgeServer();
        internal const int PortRangeStart = 16001;
        internal const int PortRangeEnd = 16100;
        internal const string BindHost = "127.0.0.1";

        private readonly object _gate = new object();
        private Thread _thread;
        private volatile bool _running;
        private TcpListener _listener;
        private volatile int _boundPort;

        private BridgeServer() { }
        public int BoundPort { get { return _boundPort; } }

        public void Start()
        {
            lock (_gate)
            {
                if (_running) return;
                _running = true;
                _thread = new Thread(ListenLoop);
                _thread.IsBackground = true;
                _thread.Name = "UnityInfoBridge.Listener";
                _thread.Start();
            }
        }

        public void Stop()
        {
            lock (_gate)
            {
                _running = false;
                _boundPort = 0;
                if (_listener != null)
                {
                    try { _listener.Stop(); } catch { }
                }
            }
        }

        private void ListenLoop()
        {
            try
            {
                _listener = TryBindInRange();
                if (_listener == null)
                {
                    BridgeLog.Error("Failed to start listener: no free port in range " + PortRangeStart + "-" + PortRangeEnd);
                    _running = false;
                    return;
                }
            }
            catch (Exception ex)
            {
                BridgeLog.Error("Failed to start listener: " + ex.Message);
                _running = false;
                return;
            }

            while (_running)
            {
                try
                {
                    TcpClient client = _listener.AcceptTcpClient();
                    ThreadPool.QueueUserWorkItem(HandleClient, client);
                }
                catch
                {
                    if (_running)
                    {
                        BridgeLog.Warn("Accept failed.");
                    }
                }
            }
        }

        private TcpListener TryBindInRange()
        {
            IPAddress host = IPAddress.Parse(BindHost);
            for (int port = PortRangeStart; port <= PortRangeEnd; port++)
            {
                TcpListener candidate = null;
                try
                {
                    candidate = new TcpListener(host, port);
                    candidate.Start();
                    _boundPort = port;
                    BridgeLog.Info("Listening on " + BindHost + ":" + port);
                    return candidate;
                }
                catch (SocketException)
                {
                    if (candidate != null)
                    {
                        try { candidate.Stop(); } catch { }
                    }
                }
                catch
                {
                    if (candidate != null)
                    {
                        try { candidate.Stop(); } catch { }
                    }
                }
            }

            _boundPort = 0;
            return null;
        }

        private void HandleClient(object state)
        {
            TcpClient client = (TcpClient)state;
            try
            {
                using (client)
                using (NetworkStream stream = client.GetStream())
                using (StreamReader reader = new StreamReader(stream, Encoding.UTF8))
                using (StreamWriter writer = new StreamWriter(stream, new UTF8Encoding(false)))
                {
                    writer.NewLine = "\n";
                    writer.AutoFlush = true;

                    string line = reader.ReadLine();
                    if (string.IsNullOrEmpty(line)) return;

                    string response = BridgeRequestRouter.Handle(line);
                    writer.WriteLine(response);
                }
            }
            catch (Exception ex)
            {
                BridgeLog.Warn("Client error: " + ex.Message);
            }
        }
    }

    internal static class BridgeRequestRouter
    {
        private const int DefaultDispatchTimeoutMs = 15000;
        private const int HeavyDispatchTimeoutMs = 60000;
        private static bool _disableJsonConvertSerialization;
        private static bool _jsonConvertDisableLogged;

        public static string Handle(string line)
        {
            string id = null;
            string method = null;
            int timeoutMs = DefaultDispatchTimeoutMs;
            try
            {
                JObject req = JObject.Parse(line);
                id = req.Value<string>("id");
                method = req.Value<string>("method");
                JObject args = req["params"] as JObject ?? new JObject();

                timeoutMs = ResolveDispatchTimeoutMs(method);
                object result = MainThreadDispatcher.Invoke(delegate { return UnityInspectionService.Dispatch(method, args); }, timeoutMs);
                return BuildSuccess(id, result);
            }
            catch (BridgeRpcException ex)
            {
                return BuildError(id, ex.Code, ex.Message, ex.ErrorData);
            }
            catch (JsonException ex)
            {
                return BuildError(id, -32700, "parse_error", ex.Message);
            }
            catch (TimeoutException ex)
            {
                return BuildError(id, -32050, "main_thread_timeout", new Dictionary<string, object>
                {
                    { "message", ex.Message },
                    { "method", method ?? string.Empty },
                    { "timeout_ms", timeoutMs }
                });
            }
            catch (Exception ex)
            {
                return BuildError(id, -32050, "internal_bridge_error", ex.Message);
            }
        }

        private static int ResolveDispatchTimeoutMs(string method)
        {
            if (string.IsNullOrEmpty(method)) return DefaultDispatchTimeoutMs;

            switch (method)
            {
                case "snapshot_scene":
                case "snapshot_gameobject":
                case "get_scene_hierarchy":
                case "capture_screenshot":
                case "search_component_fields":
                case "search_text":
                case "list_text_elements":
                    return HeavyDispatchTimeoutMs;
                default:
                    return DefaultDispatchTimeoutMs;
            }
        }

        private static string BuildSuccess(string id, object result)
        {
            Dictionary<string, object> payload = new Dictionary<string, object>
            {
                { "jsonrpc", "2.0" },
                { "id", id },
                { "result", result }
            };
            return SerializePayload(payload);
        }

        private static string BuildError(string id, int code, string message, object data)
        {
            Dictionary<string, object> error = new Dictionary<string, object>
            {
                { "code", code },
                { "message", message }
            };
            if (data != null) error["data"] = data;

            Dictionary<string, object> payload = new Dictionary<string, object>
            {
                { "jsonrpc", "2.0" },
                { "id", id },
                { "error", error }
            };
            return SerializePayload(payload);
        }

        private static string SerializePayload(object payload)
        {
            if (_disableJsonConvertSerialization)
            {
                return JsonWire.Serialize(payload);
            }

            try
            {
                return JsonConvert.SerializeObject(payload, Formatting.None, new JsonSerializerSettings
                {
                    Culture = CultureInfo.InvariantCulture,
                    NullValueHandling = NullValueHandling.Include,
                    ReferenceLoopHandling = ReferenceLoopHandling.Ignore
                });
            }
            catch (Exception ex)
            {
                _disableJsonConvertSerialization = true;
                if (!_jsonConvertDisableLogged)
                {
                    _jsonConvertDisableLogged = true;
                    BridgeLog.Warn("JsonConvert serialization disabled for this session; using JsonWire fallback: " + ex.Message);
                }
                return JsonWire.Serialize(payload);
            }
        }
    }

    internal static class JsonWire
    {
        public static string Serialize(object value)
        {
            StringBuilder sb = new StringBuilder(512);
            WriteValue(sb, value, 0);
            return sb.ToString();
        }

        private static void WriteValue(StringBuilder sb, object value, int depth)
        {
            if (depth > 64)
            {
                sb.Append("null");
                return;
            }

            if (value == null)
            {
                sb.Append("null");
                return;
            }

            if (value is string)
            {
                WriteString(sb, (string)value);
                return;
            }

            if (value is bool)
            {
                sb.Append((bool)value ? "true" : "false");
                return;
            }

            if (value is char)
            {
                WriteString(sb, value.ToString());
                return;
            }

            if (value is sbyte || value is byte || value is short || value is ushort ||
                value is int || value is uint || value is long || value is ulong)
            {
                sb.Append(Convert.ToString(value, CultureInfo.InvariantCulture));
                return;
            }

            if (value is float)
            {
                float f = (float)value;
                if (float.IsNaN(f) || float.IsInfinity(f)) sb.Append("null");
                else sb.Append(f.ToString("R", CultureInfo.InvariantCulture));
                return;
            }

            if (value is double)
            {
                double d = (double)value;
                if (double.IsNaN(d) || double.IsInfinity(d)) sb.Append("null");
                else sb.Append(d.ToString("R", CultureInfo.InvariantCulture));
                return;
            }

            if (value is decimal)
            {
                sb.Append(((decimal)value).ToString(CultureInfo.InvariantCulture));
                return;
            }

            if (value is Enum)
            {
                WriteString(sb, value.ToString());
                return;
            }

            JValue jValue = value as JValue;
            if (jValue != null)
            {
                WriteValue(sb, jValue.Value, depth + 1);
                return;
            }

            JObject jObject = value as JObject;
            if (jObject != null)
            {
                sb.Append('{');
                bool first = true;
                foreach (JProperty property in jObject.Properties())
                {
                    if (!first) sb.Append(',');
                    first = false;
                    WriteString(sb, property.Name);
                    sb.Append(':');
                    WriteValue(sb, property.Value, depth + 1);
                }
                sb.Append('}');
                return;
            }

            JArray jArray = value as JArray;
            if (jArray != null)
            {
                sb.Append('[');
                for (int i = 0; i < jArray.Count; i++)
                {
                    if (i > 0) sb.Append(',');
                    WriteValue(sb, jArray[i], depth + 1);
                }
                sb.Append(']');
                return;
            }

            IDictionary dict = value as IDictionary;
            if (dict != null)
            {
                sb.Append('{');
                bool first = true;
                foreach (DictionaryEntry entry in dict)
                {
                    if (!first) sb.Append(',');
                    first = false;
                    WriteString(sb, Convert.ToString(entry.Key, CultureInfo.InvariantCulture));
                    sb.Append(':');
                    WriteValue(sb, entry.Value, depth + 1);
                }
                sb.Append('}');
                return;
            }

            IEnumerable enumerable = value as IEnumerable;
            if (enumerable != null)
            {
                sb.Append('[');
                bool first = true;
                foreach (object item in enumerable)
                {
                    if (!first) sb.Append(',');
                    first = false;
                    WriteValue(sb, item, depth + 1);
                }
                sb.Append(']');
                return;
            }

            WriteString(sb, value.ToString());
        }

        private static void WriteString(StringBuilder sb, string value)
        {
            if (value == null)
            {
                sb.Append("null");
                return;
            }

            sb.Append('"');
            for (int i = 0; i < value.Length; i++)
            {
                char ch = value[i];
                switch (ch)
                {
                    case '"': sb.Append("\\\""); break;
                    case '\\': sb.Append("\\\\"); break;
                    case '\b': sb.Append("\\b"); break;
                    case '\f': sb.Append("\\f"); break;
                    case '\n': sb.Append("\\n"); break;
                    case '\r': sb.Append("\\r"); break;
                    case '\t': sb.Append("\\t"); break;
                    default:
                        if (ch < 32)
                        {
                            sb.Append("\\u");
                            sb.Append(((int)ch).ToString("x4", CultureInfo.InvariantCulture));
                        }
                        else
                        {
                            sb.Append(ch);
                        }
                        break;
                }
            }
            sb.Append('"');
        }
    }

    internal sealed class BridgeRpcException : Exception
    {
        public int Code { get; private set; }
        public object ErrorData { get; private set; }

        public BridgeRpcException(int code, string message, object data) : base(message)
        {
            Code = code;
            ErrorData = data;
        }
    }
}

