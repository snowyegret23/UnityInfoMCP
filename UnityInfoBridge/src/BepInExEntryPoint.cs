#if BEPINEX
using BepInEx;
#if MONO
using BepInEx.Unity.Mono;
#else
using BepInEx.Unity.IL2CPP;
#endif

namespace UnityInfoBridge
{
    [BepInPlugin(PluginMetadata.Guid, PluginMetadata.Name, PluginMetadata.Version)]
#if MONO
    public sealed class UnityInfoBepInExPlugin : BaseUnityPlugin
    {
        private void Awake()
        {
            BridgeBootstrap.Start(
                delegate(string msg) { Logger.LogInfo(msg); },
                delegate(string msg) { Logger.LogWarning(msg); },
                delegate(string msg) { Logger.LogError(msg); });
        }
    }
#else
    public sealed class UnityInfoBepInExPlugin : BasePlugin
    {
        public override void Load()
        {
            BridgeBootstrap.Start(
                delegate(string msg) { Log.LogInfo(msg); },
                delegate(string msg) { Log.LogWarning(msg); },
                delegate(string msg) { Log.LogError(msg); });
        }
    }
#endif
}
#endif
