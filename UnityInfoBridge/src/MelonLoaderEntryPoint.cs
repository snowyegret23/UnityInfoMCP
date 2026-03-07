#if MELONLOADER
using MelonLoader;

#if IL2CPP
[assembly: MelonPlatformDomain(MelonPlatformDomainAttribute.CompatibleDomains.IL2CPP)]
#else
[assembly: MelonPlatformDomain(MelonPlatformDomainAttribute.CompatibleDomains.MONO)]
#endif
[assembly: MelonInfo(typeof(UnityInfoBridge.UnityInfoMelonMod), UnityInfoBridge.PluginMetadata.Name, UnityInfoBridge.PluginMetadata.Version, "UnityInfoMCP")]
[assembly: MelonGame(null, null)]

namespace UnityInfoBridge
{
    public sealed class UnityInfoMelonMod : MelonMod
    {
        public override void OnInitializeMelon()
        {
            BridgeBootstrap.Start(
                delegate(string msg) { LoggerInstance.Msg(msg); },
                delegate(string msg) { LoggerInstance.Warning(msg); },
                delegate(string msg) { LoggerInstance.Error(msg); });
        }
    }
}
#endif
