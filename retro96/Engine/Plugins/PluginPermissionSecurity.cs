using System.Security;

namespace Retro96.Plugins;

internal static class PluginPermissionSecurity
{
    internal static void Require(PluginPermission granted, PluginPermission required)
    {
        if ((granted & required) != required)
            throw new SecurityException($"Plugin lacks permission '{string.Join(", ", PluginPermissionNames.ToNames(required))}'.");
    }
}
