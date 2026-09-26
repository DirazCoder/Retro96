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
