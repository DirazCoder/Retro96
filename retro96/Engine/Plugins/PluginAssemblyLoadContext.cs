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
        // The host intentionally compiles PluginApi.cs into Retro96 itself; it does
        // not take a build-time dependency on the standalone SDK project. Plugin
        // authors, however, compile against Retro96.Plugin.SDK.dll. Unify both
        // assembly names to the host contract assembly inside the sandbox so the
        // CLR sees exactly one IRetro96Plugin/IRetro96PluginHost type identity.
        string? requestedName = assemblyName.Name;
        string hostContractName = typeof(IRetro96Plugin).Assembly.GetName().Name ?? string.Empty;
        if (string.Equals(requestedName, hostContractName, StringComparison.OrdinalIgnoreCase) ||
            string.Equals(requestedName, "Retro96.Plugin.SDK", StringComparison.OrdinalIgnoreCase))
        {
            return typeof(IRetro96Plugin).Assembly;
        }

        string? path = _resolver.ResolveAssemblyToPath(assemblyName);
        return path == null ? null : LoadFromAssemblyPath(path);
    }
}
