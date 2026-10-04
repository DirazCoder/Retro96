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

    /// <summary>True when Preferences selected the 1999 IE5 / JScript 5.0 persona (the upgrade default).</summary>
    public static bool IsInternetExplorer5 => Settings.EngineMode == RetroEngineMode.InternetExplorer5;

    /// <summary>True when Preferences selected the 1999 Navigator 4.7 / JavaScript 1.3 persona.</summary>
    public static bool IsNetscape47 => Settings.EngineMode == RetroEngineMode.Netscape47;

    /// <summary>Any of the 1999-generation personas (IE5, NS4.7, the upgraded union).</summary>
    public static bool Is1999Persona => IsInternetExplorer5 || IsNetscape47 || IsRetro96;

    /// <summary>True when the native engine exposes the IE-era host-object surface.</summary>
    public static bool SupportsInternetExplorerLegacy => IsRetro96 || IsInternetExplorer3 || IsInternetExplorer5;

    /// <summary>True when the native engine exposes the Navigator-era surface.</summary>
    public static bool SupportsNetscapeLegacy => IsRetro96 || IsNetscape3 || IsNetscape47;

    // ── 1999 persona capabilities (checklist §1/§10 profile logic) ──────
    // Pages sniff document.all → document.layers → getElementById. An engine
    // that exposes both all and layers takes the wrong branch on half the
    // pages, so the historical personas expose exactly one surface.

    /// <summary>document.all exists (IE3/IE5 and the native union).</summary>
    public static bool SupportsDocumentAll => IsRetro96 || IsInternetExplorer3 || IsInternetExplorer5;

    /// <summary>document.layers exists (NS3/NS4.7 and the native union).</summary>
    public static bool SupportsDocumentLayers => IsRetro96 || IsNetscape3 || IsNetscape47;

    /// <summary>getElementById exists. Navigator 4.x predates DOM Level 1
    /// Core adoption in the wild — NS4.7 pages use document.layers or
    /// document.ids instead, so the property is undefined there.</summary>
    public static bool SupportsGetElementById => !IsNetscape3 && !IsNetscape47;

    /// <summary>IE5 quirks-mode box model: width/height include padding and
    /// border (checklist §9). Only the strict IE5 persona applies it.</summary>
    public static bool UsesIe5BoxModel => IsInternetExplorer5;

    /// <summary>Wire protocol: 1999 personas speak HTTP/1.1 (RFC 2616), the
    /// 1996 historical personas stay on HTTP/1.0.</summary>
    public static bool Http11Enabled => Is1999Persona;

    /// <summary>Validation caching (ETag/304) is available in the 1999
    /// personas and behind a preference for the historical ones.</summary>
    public static bool HttpCacheEnabled => Is1999Persona && Settings.EnableHttpCache;

    /// <summary>The era's PNG acceptance. IE3/NS3 never advertised
    /// image/png (checklist §16), the 1999 personas do.</summary>
    public static bool AdvertisesPngImages => !IsInternetExplorer3 && !IsNetscape3;

    /// <summary>VBScript engine version this persona ships (§13).
    /// IE3 shipped 1.0; the 1999 target is IE5's 5.0. VBScript 5.5
    /// arrived with IE5.5 in 2000 — deliberately out of scope.</summary>
    public static double VbScriptVersion => IsInternetExplorer3 ? 1.0 : 5.0;

    /// <summary>Human-readable script engine personality exposed by the compatibility mode.</summary>
    public static string JavaScriptEngineName => Settings.EngineMode switch
    {
        RetroEngineMode.Retro96 => "Retro96 Script Engine",
        RetroEngineMode.InternetExplorer3 => "Microsoft JScript 1.0",
        RetroEngineMode.InternetExplorer5 => "Microsoft JScript 5.0",
        RetroEngineMode.Netscape47 => "Netscape JavaScript 1.3",
        _ => "Netscape JavaScript 1.1"
    };

    /// <summary>Compatibility-era ECMAScript surface version used by the script runtime.</summary>
    public static string JavaScriptVersion => Settings.EngineMode switch
    {
        RetroEngineMode.Retro96 => "5.x/1.3 + Retro96 extensions",
        RetroEngineMode.InternetExplorer3 => "1.0",
        RetroEngineMode.InternetExplorer5 => "5.0 (ES3)",
        RetroEngineMode.Netscape47 => "1.3",
        _ => "1.1"
    };

    public static bool ImagesEnabled => Settings.LoadImages;
    public static bool JavaScriptEnabled => Settings.EnableJavaScript;
    public static bool ScriptingEnabled => JavaScriptEnabled || VbScriptEnabled;
    public static bool ExternalScriptsEnabled => Settings.EnableExternalScripts;
    public static bool JavaScriptEvalEnabled => JavaScriptEnabled && Settings.EnableJavaScriptEval;
    public static bool JavaScriptTimersEnabled => Settings.EnableJavaScript && Settings.EnableJavaScriptTimers;
    public static bool JavaScriptDialogsEnabled => ScriptingEnabled && Settings.EnableJavaScriptDialogs;
    public static int JavaScriptMaxExecutionMilliseconds => Settings.JavaScriptMaxExecutionSeconds * 1000;
    public static int JavaScriptMemoryLimitBytes => Settings.JavaScriptMemoryLimitMb * 1024 * 1024;
    public static int JavaScriptMaxCallDepth => Settings.JavaScriptMaxCallDepth;
    public static int JavaMaxCallDepth => Settings.JavaMaxCallDepth;
    public static int MaxScriptSpliceTokens => Settings.MaxScriptSpliceTokens;
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
    public static int MaxHttpRedirects => Settings.MaxHttpRedirects;
    public static int HttpConnectTimeoutSeconds => Settings.HttpConnectTimeoutSeconds;
    public static int HttpResponseTimeoutSeconds => Settings.HttpResponseTimeoutSeconds;
    public static int MaxConcurrentResourceFetches => Settings.MaxConcurrentResourceFetches;
    public static int MaxResourceFetchesPerPage => Settings.MaxResourceFetchesPerPage;
    public static bool RequestCompressedResponses => Settings.RequestCompressedResponses;
    public static bool MetaRefreshEnabled => Settings.FollowMetaRefresh;
    public static bool StylesheetsEnabled => Settings.LoadStylesheets;
    public static bool FramesEnabled => Settings.LoadFrames;
    public static bool FormSubmissionsEnabled => Settings.AllowFormSubmissions;
    public static bool AnimatedImagesEnabled => Settings.AnimateImages;
    public static bool BlinkEnabled => Settings.BlinkText;
    public static int BlinkIntervalMilliseconds => Settings.BlinkIntervalMilliseconds;
    public static int AnimatedGifSpeedPercent => Settings.AnimatedGifSpeedPercent;
    public static int MarqueeSpeedPercent => Settings.MarqueeSpeedPercent;
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
