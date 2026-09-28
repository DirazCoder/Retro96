using Retro96.Plugins;

namespace RetroTests;

public sealed class PluginApiTests
{
    [Fact]
    public void PermissionNamesRoundTrip()
    {
        var permissions = PluginPermission.BrowserRead |
                          PluginPermission.BrowserEvents |
                          PluginPermission.UserInterface |
                          PluginPermission.Storage |
                          PluginPermission.BrowserZoom |
                          PluginPermission.BrowserCookies |
                          PluginPermission.BrowserFind |
                          PluginPermission.BrowserScreenshot |
                          PluginPermission.UiPanel |
                          PluginPermission.AudioPlayback |
                          PluginPermission.Notifications |
                          PluginPermission.Dialogs;

        var names = PluginPermissionNames.ToNames(permissions).ToArray();
        var reparsed = PluginPermissionNames.Parse(names);

        Assert.Equal(permissions, reparsed);
        Assert.Contains("browser.read", names);
        Assert.Contains("browser.events", names);
        Assert.Contains("ui", names);
        Assert.Contains("storage", names);
        Assert.Contains("browser.zoom", names);
        Assert.Contains("browser.cookies", names);
        Assert.Contains("browser.find", names);
        Assert.Contains("browser.screenshot", names);
        Assert.Contains("ui.panel", names);
        Assert.Contains("audio.playback", names);
        Assert.Contains("notifications", names);
        Assert.Contains("dialogs", names);
    }

    [Fact]
    public void ManifestExposesRequestedPermissionSet()
    {
        var manifest = new PluginManifest
        {
            Id = "tests.example",
            Name = "Example",
            ApiVersion = Retro96PluginApi.ApiVersion,
            Permissions = new List<string> { "ui", "browser.read", "browser.events" }
        };

        Assert.True(manifest.RequestedPermissions.HasFlag(PluginPermission.UserInterface));
        Assert.True(manifest.RequestedPermissions.HasFlag(PluginPermission.BrowserRead));
        Assert.True(manifest.RequestedPermissions.HasFlag(PluginPermission.BrowserEvents));
        Assert.False(manifest.RequestedPermissions.HasFlag(PluginPermission.Network));
    }
}


public sealed class PluginPermissionCatalogTests
{
    [Fact]
    public void EveryPermissionHasMetadata()
    {
        var permissions = Enum.GetValues<PluginPermission>().Where(p => p != PluginPermission.None).ToArray();
        Assert.Equal(permissions.Length, PluginPermissionCatalog.All.Count);
        foreach (var permission in permissions)
        {
            var info = PluginPermissionCatalog.Get(permission);
            Assert.Equal(permission, info.Permission);
            Assert.False(string.IsNullOrWhiteSpace(info.Name));
            Assert.False(string.IsNullOrWhiteSpace(info.FriendlyName));
            Assert.False(string.IsNullOrWhiteSpace(info.Description));
            Assert.False(string.IsNullOrWhiteSpace(info.Allows));
            Assert.False(string.IsNullOrWhiteSpace(info.DoesNotAllow));
            Assert.False(string.IsNullOrWhiteSpace(info.RiskNote));
            Assert.Equal(info.Name, PluginPermissionNames.ToNames(permission).Single());
        }
    }
}
