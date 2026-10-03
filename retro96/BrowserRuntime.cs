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

    /// <summary>True when Preferences selected the native Retro96 compatibility-union engine.</summary>
    public static bool IsRetro96 => Settings.EngineMode == RetroEngineMode.Retro96;

    /// <summary>True when Preferences selected the historical IE3/JScript profile.</summary>
    public static bool IsInternetExplorer3 => Settings.EngineMode == RetroEngineMode.InternetExplorer3;

    /// <summary>True when Preferences selected the historical Navigator 3 profile.</summary>
    public static bool IsNetscape3 => Settings.EngineMode == RetroEngineMode.Netscape3;

    /// <summary>True when the native engine exposes the IE-era host-object surface.</summary>
    public static bool SupportsInternetExplorerLegacy => IsRetro96 || IsInternetExplorer3;

    /// <summary>True when the native engine exposes the Navigator-era surface.</summary>
    public static bool SupportsNetscapeLegacy => IsRetro96 || IsNetscape3;

    /// <summary>Human-readable script engine personality exposed by the compatibility mode.</summary>
    public static string JavaScriptEngineName => Settings.EngineMode switch
    {
        RetroEngineMode.Retro96 => "Retro96 Script Engine",
        RetroEngineMode.InternetExplorer3 => "Microsoft JScript 1.0",
        _ => "Netscape JavaScript 1.1"
    };

    /// <summary>Compatibility-era ECMAScript surface version used by the script runtime.</summary>
    public static string JavaScriptVersion => Settings.EngineMode switch
    {
        RetroEngineMode.Retro96 => "1.0/1.1 + Retro96 extensions",
        RetroEngineMode.InternetExplorer3 => "1.0",
        _ => "1.1"
    };

    public static bool ImagesEnabled => Settings.LoadImages;
    public static bool JavaScriptEnabled => Settings.EnableJavaScript;
    public static bool ScriptingEnabled => JavaScriptEnabled || VbScriptEnabled;
    public static bool JavaScriptTimersEnabled => Settings.EnableJavaScript && Settings.EnableJavaScriptTimers;
    public static bool JavaScriptDialogsEnabled => ScriptingEnabled && Settings.EnableJavaScriptDialogs;
    /// <summary>
    /// VBScript is a legacy execution surface. It is completely disabled in
    /// High trust mode and available in Medium/Low when scripting itself is
    /// enabled. All execution paths consult this single policy gate.
    /// </summary>
    public static bool VbScriptEnabled =>
        Settings.EnableVBScript && Settings.TrustMode != TrustMode.High;
    /// <summary>Java applets are disabled in High trust mode.</summary>
    public static bool JavaAppletsEnabled =>
        Settings.EnableJavaApplets && Settings.TrustMode != TrustMode.High;
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
