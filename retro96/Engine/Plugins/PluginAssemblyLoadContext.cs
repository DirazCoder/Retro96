using System.Reflection;
using System.Runtime.Loader;

namespace Retro96.Plugins;

internal sealed class PluginAssemblyLoadContext : AssemblyLoadContext
{
    private readonly AssemblyDependencyResolver _resolver;

    public PluginAssemblyLoadContext(string mainAssemblyPath) : base("Retro96.PluginSandbox", isCollectible: true)
    {
        _resolver = new AssemblyDependencyResolver(mainAssemblyPath);
    }

    protected override Assembly? Load(AssemblyName assemblyName)
    {
        // The host intentionally compiles PluginApi.cs into Retro96 itself; it
        // does not take a build-time dependency on the standalone SDK project.
        // Plugin authors, however, compile against Retro96.Plugin.SDK.dll.
        // Unify both assembly names to the host contract assembly inside the
        // sandbox so the CLR sees exactly one IRetro96Plugin/IRetro96PluginHost
        // type identity. Comparison is by simple name only (version is
        // ignored on purpose) so the unification cannot be bypassed by
        // version-pinning a reference.
        string? requestedName = assemblyName.Name;
        string hostContractName = typeof(IRetro96Plugin).Assembly.GetName().Name ?? string.Empty;
        if (string.Equals(requestedName, hostContractName, StringComparison.OrdinalIgnoreCase) ||
            string.Equals(requestedName, "Retro96.Plugin.SDK", StringComparison.OrdinalIgnoreCase))
        {
            return typeof(IRetro96Plugin).Assembly;
        }

        // Everything else resolves through the plugin's own .deps.json so
        // private dependencies load from the plugin payload directory.
        string? path = _resolver.ResolveAssemblyToPath(assemblyName);
        return path == null ? null : LoadFromAssemblyPath(path);
    }
}