using System;
using System.Threading;

namespace Retro96;

/// <summary>
/// Process-wide browser policy snapshot.  All page-facing subsystems consult
/// the same immutable snapshot so the network, JS bindings and renderer do
/// not drift apart when Preferences changes.
/// </summary>
public static class BrowserRuntime
{
    private static UserSettings _settings = new();

    public static UserSettings Settings => Volatile.Read(ref _settings);

    public static void Apply(UserSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        Volatile.Write(ref _settings, settings.Clone());
    }

    public static string UserAgent => Settings.EffectiveUserAgent;
    public static bool ImagesEnabled => Settings.LoadImages;
    public static bool JavaScriptEnabled => Settings.EnableJavaScript;
    public static bool JavaScriptTimersEnabled => Settings.EnableJavaScript && Settings.EnableJavaScriptTimers;
    public static bool JavaScriptDialogsEnabled => Settings.EnableJavaScript && Settings.EnableJavaScriptDialogs;
    public static bool CookiesEnabled => Settings.EnableCookies;
    public static bool ReferrerEnabled => Settings.SendReferrer;
    public static bool RedirectsEnabled => Settings.FollowHttpRedirects;
    public static bool MetaRefreshEnabled => Settings.FollowMetaRefresh;
    public static bool StylesheetsEnabled => Settings.LoadStylesheets;
    public static bool FramesEnabled => Settings.LoadFrames;
    public static bool FormSubmissionsEnabled => Settings.AllowFormSubmissions;
    public static bool AnimatedImagesEnabled => Settings.AnimateImages;
    public static bool BlinkEnabled => Settings.BlinkText;
    public static bool MarqueeEnabled => Settings.MarqueeText;

    public static bool ScriptedWindowsAllowed
    {
        get
        {
            var s = Settings;
            return s.AllowScriptedWindows && s.TrustMode != TrustMode.High;
        }
    }

    public static bool AllowPageFileAccess => Settings.TrustMode == TrustMode.Low;
    public static bool HostImageChecking => Settings.HostCheckImages;

    public static long MaxImageBytes => Settings.TrustMode switch
    {
        TrustMode.High => 8L * 1024 * 1024,
        TrustMode.Medium => 16L * 1024 * 1024,
        _ => 32L * 1024 * 1024
    };

    public static bool IsHighSecurity => Settings.TrustMode == TrustMode.High;

    public static bool IsLocalFileResourceAllowed(bool hostOpenedLocalPage) =>
        hostOpenedLocalPage || AllowPageFileAccess;

    public static bool IsNetworkImageUrl(string absoluteUrl)
    {
        if (string.IsNullOrWhiteSpace(absoluteUrl)) return false;
        return absoluteUrl.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
               absoluteUrl.StartsWith("https://", StringComparison.OrdinalIgnoreCase);
    }

    public static bool IsSafeImageUrl(string absoluteUrl, bool hostOpenedLocalPage)
    {
        if (!ImagesEnabled) return false;
        if (absoluteUrl.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
            return true;
        if (IsNetworkImageUrl(absoluteUrl))
            return true;
        if (absoluteUrl.StartsWith("file:", StringComparison.OrdinalIgnoreCase))
            return IsLocalFileResourceAllowed(hostOpenedLocalPage);
        return false;
    }

    public static Retro96.Drawing.Color ResolveBackground(Retro96.Drawing.Color pageColor)
    {
        var s = Settings;
        return s.BackgroundMode == BackgroundMode.Force
            ? s.GetForcedBackgroundColor()
            : pageColor;
    }
}
