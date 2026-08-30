using System.Reflection;
using System.Runtime.InteropServices;

// Emby reads the plugin id from the assembly GUID; keep this in sync with
// BangumiConstants.PluginGuid.
[assembly: Guid("a3f5b1c2-6d4e-4b8a-9c17-2e5f7d9a0b31")]
[assembly: AssemblyMetadata("EmbyPluginTargetAbi", "4.9.1.90")]