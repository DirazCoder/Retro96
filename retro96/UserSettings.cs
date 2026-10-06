using System;
using System.Globalization;
using System.IO;
using System.Text;

namespace Retro96;

public enum RetroEngineMode
{
    Retro96,
    Netscape3,
    InternetExplorer3,
    InternetExplorer5,
    Netscape47
}

public enum BackgroundMode
{
    PageDefault,
    Force
}

public enum TrustMode
{
    High,
    Medium,
    Low
}

/// <summary>
/// Persistent browser preferences.  The settings file intentionally stays
/// human-editable: Retro96 is an old-browser project, but its configuration
/// does not need to be opaque.
/// </summary>
public sealed class UserSettings
{
    public const string DefaultSearchUrl = "https://www.frogfind.com/?q=%s";
    public const string DefaultRetro96UserAgent = DefaultIe5UserAgent;
    // Navigator 3 used the Mozilla/3.0 product token with platform and
    // security fields in the parenthesized comment. Keep the default
    // historical rather than carrying the Retro96 product marker.
    public const string DefaultNetscapeUserAgent = "Mozilla/3.0 (Win95; I)";
    public const string DefaultIe3UserAgent = "Mozilla/2.0 (compatible; MSIE 3.02; Windows 95)";
    // 1999 personas: IE5 shipped March 1999 (MSIE 5.0, Win98 token),
    // Navigator 4.7 was the August 1999 maintenance release of the
    // 4.x line. Both use the Mozilla/4 product token.
    public const string DefaultIe5UserAgent = "Mozilla/4.0 (compatible; MSIE 5.0; Windows 98)";
    public const string DefaultNetscape47UserAgent = "Mozilla/4.7 [en] (Win98; I)";
    public const string DefaultBackgroundColor = "#C0C0C0";

    public string SearchQueryUrl { get; set; } = DefaultSearchUrl;
    public string HomePageUrl { get; set; } = "retro96:home";
    public bool WelcomeDismissed { get; set; }

    // Fresh installs use the Retro96 compatibility union. Historical browser
    // personas, including IE5's box-model behavior, remain selectable.
    // Existing installs keep their saved mode.
    public RetroEngineMode EngineMode { get; set; } = RetroEngineMode.Retro96;
    public string UserAgentOverride { get; set; } = "";
    public BackgroundMode BackgroundMode { get; set; } = BackgroundMode.PageDefault;
    public string ForcedBackgroundColor { get; set; } = DefaultBackgroundColor;
    public bool LoadImages { get; set; } = true;
    public bool EnableJavaScript { get; set; } = true;
    public bool EnableVBScript { get; set; } = true;
    public bool EnableJavaApplets { get; set; } = true;
    public bool AllowScriptedWindows { get; set; } = true;

    // Windows display scaling. Enabled by default so the shell and native
    // controls use PerMonitorV2 rather than being bitmap-stretched by Windows.
    // Stored as a normal preference so legacy/portable installs can opt out.
    public bool HighDpiScaleMode { get; set; } = true;
    public int DefaultPageZoomPercent { get; set; } = 100;

    // Advanced browser-engine feature switches.
    public bool LoadStylesheets { get; set; } = true;
    public bool LoadFrames { get; set; } = true;
    public bool AllowFormSubmissions { get; set; } = true;
    public bool EnableExternalScripts { get; set; } = true;
    public bool EnableJavaScriptEval { get; set; } = true;
    public bool EnableJavaScriptTimers { get; set; } = true;
    public bool EnableJavaScriptDialogs { get; set; } = true;
    public int JavaScriptMaxExecutionSeconds { get; set; } = 5;
    public int JavaScriptMemoryLimitMb { get; set; } = 10;
    public int JavaScriptMaxCallDepth { get; set; } = 400;
    public int MaxScriptSpliceTokens { get; set; } = 200000;
    public int JavaMaxCallDepth { get; set; } = 400;
    public bool FollowHttpRedirects { get; set; } = true;
    public bool RequestCompressedResponses { get; set; } = true;
    // HTTP/1.1 validation caching (ETag / If-None-Match / If-Modified-Since,
    // 304 revalidation). Only responses that actually carry validators or
    // explicit freshness are ever cached.
    public bool EnableHttpCache { get; set; } = true;
    public int MaxHttpRedirects { get; set; } = 5;
    public int HttpConnectTimeoutSeconds { get; set; } = 10;
    public int HttpResponseTimeoutSeconds { get; set; } = 30;
    public int MaxConcurrentResourceFetches { get; set; } = 8;
    public int MaxResourceFetchesPerPage { get; set; } = 1000;
    public bool FollowMetaRefresh { get; set; } = true;
    public bool EnableCookies { get; set; } = true;
    public bool SendReferrer { get; set; } = true;
    public bool AnimateImages { get; set; } = true;
    public int AnimatedGifSpeedPercent { get; set; } = 100;
    public bool BlinkText { get; set; } = true;
    public int BlinkIntervalMilliseconds { get; set; } = 500;
    public bool MarqueeText { get; set; } = true;
    public int MarqueeSpeedPercent { get; set; } = 100;
    public TrustMode TrustMode { get; set; } = TrustMode.Medium;
    public bool HostCheckImages { get; set; } = true;
    public bool DiscardPageStateOnClose { get; set; } = true;
    public bool PluginDevMode { get; set; } = false;

    private static string ConfigDirectory =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Retro96");

    private static string FilePath => Path.Combine(ConfigDirectory, "retro96.ini");

    // Keep the executable-directory file as a legacy/portable mirror, but never
    // let a stale copy silently override the user's last applied preferences.
    private static string LegacyFilePath => Path.Combine(AppContext.BaseDirectory, "retro96.ini");

    public string EffectiveUserAgent =>
        string.IsNullOrWhiteSpace(UserAgentOverride)
            ? EngineMode switch
            {
                RetroEngineMode.InternetExplorer3 => DefaultIe3UserAgent,
                RetroEngineMode.InternetExplorer5 => DefaultIe5UserAgent,
                RetroEngineMode.Netscape3 => DefaultNetscapeUserAgent,
                RetroEngineMode.Netscape47 => DefaultNetscape47UserAgent,
                _ => DefaultRetro96UserAgent
            }
            : UserAgentOverride.Trim();

    public UserSettings Clone() => new()
    {
        SearchQueryUrl = SearchQueryUrl,
        HomePageUrl = HomePageUrl,
        WelcomeDismissed = WelcomeDismissed,
        EngineMode = EngineMode,
        UserAgentOverride = UserAgentOverride,
        BackgroundMode = BackgroundMode,
        ForcedBackgroundColor = ForcedBackgroundColor,
        LoadImages = LoadImages,
        EnableJavaScript = EnableJavaScript,
        EnableVBScript = EnableVBScript,
        EnableJavaApplets = EnableJavaApplets,
        AllowScriptedWindows = AllowScriptedWindows,
        HighDpiScaleMode = HighDpiScaleMode,
        DefaultPageZoomPercent = DefaultPageZoomPercent,
        LoadStylesheets = LoadStylesheets,
        LoadFrames = LoadFrames,
        AllowFormSubmissions = AllowFormSubmissions,
        EnableExternalScripts = EnableExternalScripts,
        EnableJavaScriptEval = EnableJavaScriptEval,
        EnableJavaScriptTimers = EnableJavaScriptTimers,
        EnableJavaScriptDialogs = EnableJavaScriptDialogs,
        JavaScriptMaxExecutionSeconds = JavaScriptMaxExecutionSeconds,
        JavaScriptMemoryLimitMb = JavaScriptMemoryLimitMb,
        JavaScriptMaxCallDepth = JavaScriptMaxCallDepth,
        MaxScriptSpliceTokens = MaxScriptSpliceTokens,
        JavaMaxCallDepth = JavaMaxCallDepth,
        FollowHttpRedirects = FollowHttpRedirects,
        RequestCompressedResponses = RequestCompressedResponses,
        EnableHttpCache = EnableHttpCache,
        MaxHttpRedirects = MaxHttpRedirects,
        HttpConnectTimeoutSeconds = HttpConnectTimeoutSeconds,
        HttpResponseTimeoutSeconds = HttpResponseTimeoutSeconds,
        MaxConcurrentResourceFetches = MaxConcurrentResourceFetches,
        MaxResourceFetchesPerPage = MaxResourceFetchesPerPage,
        FollowMetaRefresh = FollowMetaRefresh,
        EnableCookies = EnableCookies,
        SendReferrer = SendReferrer,
        AnimateImages = AnimateImages,
        AnimatedGifSpeedPercent = AnimatedGifSpeedPercent,
        BlinkText = BlinkText,
        BlinkIntervalMilliseconds = BlinkIntervalMilliseconds,
        MarqueeText = MarqueeText,
        MarqueeSpeedPercent = MarqueeSpeedPercent,
        TrustMode = TrustMode,
        HostCheckImages = HostCheckImages,
        DiscardPageStateOnClose = DiscardPageStateOnClose,
        PluginDevMode = PluginDevMode
    };

    public static UserSettings Load()
    {
        var s = new UserSettings();
        string? loadPath = null;

        try
        {
            // Keep one deterministic source when both locations exist. The
            // most recently modified copy wins, then Save() mirrors the chosen
            // preferences to both locations. This fixes the original
            // "edit -> Apply -> restart -> old value came back" split-brain
            // behaviour while still allowing a human to edit either retro96.ini
            // copy deliberately.
            bool hasUser = File.Exists(FilePath);
            bool hasLegacy = File.Exists(LegacyFilePath);
            if (hasUser && hasLegacy)
            {
                loadPath = File.GetLastWriteTimeUtc(FilePath) >=
                           File.GetLastWriteTimeUtc(LegacyFilePath)
                    ? FilePath : LegacyFilePath;
            }
            else if (hasUser)
                loadPath = FilePath;
            else if (hasLegacy)
                loadPath = LegacyFilePath;

            if (loadPath == null) return s;

            foreach (var raw in File.ReadAllLines(loadPath))
            {
                string line = raw.Trim();
                if (line.Length == 0 || line.StartsWith(';') || line.StartsWith('['))
                    continue;

                int eq = line.IndexOf('=');
                if (eq <= 0) continue;

                string key = line[..eq].Trim().ToLowerInvariant();
                string value = line[(eq + 1)..].Trim();

                switch (key)
                {
                    case "search.url":
                        if (value.Length > 0) s.SearchQueryUrl = value;
                        break;
                    case "home.url":
                        if (value.Length > 0) s.HomePageUrl = value;
                        break;
                    case "welcome.dismissed":
                        s.WelcomeDismissed = ParseBool(value, s.WelcomeDismissed);
                        break;
                    case "engine.mode":
                        if (Enum.TryParse<RetroEngineMode>(value, true, out var mode))
                            s.EngineMode = mode;
                        break;
                    case "useragent.override":
                        s.UserAgentOverride = value;
                        break;
                    case "background.mode":
                        if (Enum.TryParse<BackgroundMode>(value, true, out var bgMode))
                            s.BackgroundMode = bgMode;
                        break;
                    case "background.color":
                        if (IsHtmlColor(value)) s.ForcedBackgroundColor = value;
                        break;
                    case "images.enabled":
                        s.LoadImages = ParseBool(value, s.LoadImages);
                        break;
                    case "javascript.enabled":
                        s.EnableJavaScript = ParseBool(value, s.EnableJavaScript);
                        break;
                    case "vbscript.enabled":
                        s.EnableVBScript = ParseBool(value, s.EnableVBScript);
                        break;
                    case "java.applets.enabled":
                        s.EnableJavaApplets = ParseBool(value, s.EnableJavaApplets);
                        break;
                    case "scriptedwindows.enabled":
                        s.AllowScriptedWindows = ParseBool(value, s.AllowScriptedWindows);
                        break;
                    case "ui.highdpi":
                        s.HighDpiScaleMode = ParseBool(value, s.HighDpiScaleMode);
                        break;
                    case "ui.defaultpagezoom":
                        if (int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out int zoom))
                            s.DefaultPageZoomPercent = zoom;
                        break;
                    case "stylesheets.enabled":
                        s.LoadStylesheets = ParseBool(value, s.LoadStylesheets);
                        break;
                    case "frames.enabled":
                        s.LoadFrames = ParseBool(value, s.LoadFrames);
                        break;
                    case "forms.enabled":
                        s.AllowFormSubmissions = ParseBool(value, s.AllowFormSubmissions);
                        break;
                    case "scripts.external":
                        s.EnableExternalScripts = ParseBool(value, s.EnableExternalScripts);
                        break;
                    case "javascript.eval":
                        s.EnableJavaScriptEval = ParseBool(value, s.EnableJavaScriptEval);
                        break;
                    case "javascript.timers":
                        s.EnableJavaScriptTimers = ParseBool(value, s.EnableJavaScriptTimers);
                        break;
                    case "javascript.dialogs":
                        s.EnableJavaScriptDialogs = ParseBool(value, s.EnableJavaScriptDialogs);
                        break;
                    case "javascript.maxexecutionseconds":
                        if (int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out int jsExecutionSeconds))
                            s.JavaScriptMaxExecutionSeconds = jsExecutionSeconds;
                        break;
                    case "javascript.memorylimitmb":
                        if (int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out int jsMemoryLimitMb))
                            s.JavaScriptMemoryLimitMb = jsMemoryLimitMb;
                        break;
                    case "javascript.maxcalldepth":
                        if (int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out int jsMaxCallDepth))
                            s.JavaScriptMaxCallDepth = jsMaxCallDepth;
                        break;
                    case "html.maxscriptsplicetokens":
                        if (int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out int maxScriptSpliceTokens))
                            s.MaxScriptSpliceTokens = maxScriptSpliceTokens;
                        break;
                    case "java.maxcalldepth":
                        if (int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out int javaMaxCallDepth))
                            s.JavaMaxCallDepth = javaMaxCallDepth;
                        break;
                    case "network.redirects":
                        s.FollowHttpRedirects = ParseBool(value, s.FollowHttpRedirects);
                        break;
                    case "network.compressedresponses":
                        s.RequestCompressedResponses = ParseBool(value, s.RequestCompressedResponses);
                        break;
                    case "network.httpcache":
                        s.EnableHttpCache = ParseBool(value, s.EnableHttpCache);
                        break;
                    case "network.maxredirects":
                        if (int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out int maxRedirects))
                            s.MaxHttpRedirects = maxRedirects;
                        break;
                    case "network.connecttimeoutseconds":
                        if (int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out int connectTimeout))
                            s.HttpConnectTimeoutSeconds = connectTimeout;
                        break;
                    case "network.responsetimeoutseconds":
                        if (int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out int responseTimeout))
                            s.HttpResponseTimeoutSeconds = responseTimeout;
                        break;
                    case "network.maxconcurrentresourcefetches":
                        if (int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out int maxConcurrentFetches))
                            s.MaxConcurrentResourceFetches = maxConcurrentFetches;
                        break;
                    case "network.maxresourcefetchesperpage":
                        if (int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out int maxResourceFetches))
                            s.MaxResourceFetchesPerPage = maxResourceFetches;
                        break;
                    case "network.metarefresh":
                        s.FollowMetaRefresh = ParseBool(value, s.FollowMetaRefresh);
                        break;
                    case "network.cookies":
                        s.EnableCookies = ParseBool(value, s.EnableCookies);
                        break;
                    case "network.referrer":
                        s.SendReferrer = ParseBool(value, s.SendReferrer);
                        break;
                    case "render.animation":
                        s.AnimateImages = ParseBool(value, s.AnimateImages);
                        break;
                    case "render.animationspeedpercent":
                        if (int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out int gifSpeed))
                            s.AnimatedGifSpeedPercent = gifSpeed;
                        break;
                    case "render.blink":
                        s.BlinkText = ParseBool(value, s.BlinkText);
                        break;
                    case "render.blinkintervalmilliseconds":
                        if (int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out int blinkInterval))
                            s.BlinkIntervalMilliseconds = blinkInterval;
                        break;
                    case "render.marquee":
                        s.MarqueeText = ParseBool(value, s.MarqueeText);
                        break;
                    case "render.marqueespeedpercent":
                        if (int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out int marqueeSpeed))
                            s.MarqueeSpeedPercent = marqueeSpeed;
                        break;
                    case "security.mode":
                        if (Enum.TryParse<TrustMode>(value, true, out var trust))
                            s.TrustMode = trust;
                        break;
                    case "security.hostimagecheck":
                        s.HostCheckImages = ParseBool(value, s.HostCheckImages);
                        break;
                    case "security.discardstate":
                        s.DiscardPageStateOnClose = ParseBool(value, s.DiscardPageStateOnClose);
                        break;
                    case "plugins.devmode":
                        s.PluginDevMode = ParseBool(value, s.PluginDevMode);
                        break;
                }
            }
        }
        catch { /* unreadable settings -> defaults */ }

        Normalize(s);
        return s;
    }

    public bool Save()
    {
        Normalize(this);
        var sb = new StringBuilder();
        sb.AppendLine("[Preferences]");
        sb.AppendLine("; Retro96 preferences - human editable");
        sb.AppendLine("Search.Url=" + SearchQueryUrl);
        sb.AppendLine("Home.Url=" + HomePageUrl);
        sb.AppendLine("Welcome.Dismissed=" + BoolText(WelcomeDismissed));
        sb.AppendLine("Engine.Mode=" + EngineMode);
        sb.AppendLine("UserAgent.Override=" + UserAgentOverride);
        sb.AppendLine("Background.Mode=" + BackgroundMode);
        sb.AppendLine("Background.Color=" + ForcedBackgroundColor);
        sb.AppendLine("Images.Enabled=" + BoolText(LoadImages));
        sb.AppendLine("JavaScript.Enabled=" + BoolText(EnableJavaScript));
        sb.AppendLine("VBScript.Enabled=" + BoolText(EnableVBScript));
        sb.AppendLine("Java.Applets.Enabled=" + BoolText(EnableJavaApplets));
        sb.AppendLine("ScriptedWindows.Enabled=" + BoolText(AllowScriptedWindows));
        sb.AppendLine("UI.HighDpi=" + BoolText(HighDpiScaleMode));
        sb.AppendLine("UI.DefaultPageZoom=" + DefaultPageZoomPercent.ToString(CultureInfo.InvariantCulture));
        sb.AppendLine("Stylesheets.Enabled=" + BoolText(LoadStylesheets));
        sb.AppendLine("Frames.Enabled=" + BoolText(LoadFrames));
        sb.AppendLine("Forms.Enabled=" + BoolText(AllowFormSubmissions));
        sb.AppendLine("Scripts.External=" + BoolText(EnableExternalScripts));
        sb.AppendLine("JavaScript.Eval=" + BoolText(EnableJavaScriptEval));
        sb.AppendLine("JavaScript.Timers=" + BoolText(EnableJavaScriptTimers));
        sb.AppendLine("JavaScript.Dialogs=" + BoolText(EnableJavaScriptDialogs));
        sb.AppendLine("JavaScript.MaxExecutionSeconds=" + JavaScriptMaxExecutionSeconds.ToString(CultureInfo.InvariantCulture));
        sb.AppendLine("JavaScript.MemoryLimitMb=" + JavaScriptMemoryLimitMb.ToString(CultureInfo.InvariantCulture));
        sb.AppendLine("JavaScript.MaxCallDepth=" + JavaScriptMaxCallDepth.ToString(CultureInfo.InvariantCulture));
        sb.AppendLine("Html.MaxScriptSpliceTokens=" + MaxScriptSpliceTokens.ToString(CultureInfo.InvariantCulture));
        sb.AppendLine("Java.MaxCallDepth=" + JavaMaxCallDepth.ToString(CultureInfo.InvariantCulture));
        sb.AppendLine("Network.Redirects=" + BoolText(FollowHttpRedirects));
        sb.AppendLine("Network.CompressedResponses=" + BoolText(RequestCompressedResponses));
        sb.AppendLine("Network.HttpCache=" + BoolText(EnableHttpCache));
        sb.AppendLine("Network.MaxRedirects=" + MaxHttpRedirects.ToString(CultureInfo.InvariantCulture));
        sb.AppendLine("Network.ConnectTimeoutSeconds=" + HttpConnectTimeoutSeconds.ToString(CultureInfo.InvariantCulture));
        sb.AppendLine("Network.ResponseTimeoutSeconds=" + HttpResponseTimeoutSeconds.ToString(CultureInfo.InvariantCulture));
        sb.AppendLine("Network.MaxConcurrentResourceFetches=" + MaxConcurrentResourceFetches.ToString(CultureInfo.InvariantCulture));
        sb.AppendLine("Network.MaxResourceFetchesPerPage=" + MaxResourceFetchesPerPage.ToString(CultureInfo.InvariantCulture));
        sb.AppendLine("Network.MetaRefresh=" + BoolText(FollowMetaRefresh));
        sb.AppendLine("Network.Cookies=" + BoolText(EnableCookies));
        sb.AppendLine("Network.Referrer=" + BoolText(SendReferrer));
        sb.AppendLine("Render.Animation=" + BoolText(AnimateImages));
        sb.AppendLine("Render.AnimationSpeedPercent=" + AnimatedGifSpeedPercent.ToString(CultureInfo.InvariantCulture));
        sb.AppendLine("Render.Blink=" + BoolText(BlinkText));
        sb.AppendLine("Render.BlinkIntervalMilliseconds=" + BlinkIntervalMilliseconds.ToString(CultureInfo.InvariantCulture));
        sb.AppendLine("Render.Marquee=" + BoolText(MarqueeText));
        sb.AppendLine("Render.MarqueeSpeedPercent=" + MarqueeSpeedPercent.ToString(CultureInfo.InvariantCulture));
        sb.AppendLine("Security.Mode=" + TrustMode);
        sb.AppendLine("Security.HostImageCheck=" + BoolText(HostCheckImages));
        sb.AppendLine("Security.DiscardState=" + BoolText(DiscardPageStateOnClose));
        sb.AppendLine("Plugins.DevMode=" + BoolText(PluginDevMode));
        string contents = sb.ToString();

        bool userSaved = false;
        try
        {
            Directory.CreateDirectory(ConfigDirectory);
            WriteAtomic(FilePath, contents);
            userSaved = true;
        }
        catch { }

        // Also refresh the legacy portable location when it is already writable
        // (or when the per-user location could not be created). This preserves
        // existing portable installs without making that location authoritative.
        try
        {
            if (userSaved || File.Exists(LegacyFilePath))
                WriteAtomic(LegacyFilePath, contents);
        }
        catch { }

        return userSaved || CanReadBack(FilePath) || CanReadBack(LegacyFilePath);
    }

    private static void WriteAtomic(string path, string contents)
    {
        string? dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        string temp = path + ".tmp";
        File.WriteAllText(temp, contents, new UTF8Encoding(false));
        try
        {
            File.Move(temp, path, true);
        }
        finally
        {
            try { if (File.Exists(temp)) File.Delete(temp); } catch { }
        }
    }

    private static bool CanReadBack(string path)
    {
        try { return File.Exists(path) && new FileInfo(path).Length > 0; }
        catch { return false; }
    }

    public static string NormalizeSearchTemplate(string? value)
    {
        return TryNormalizeSearchTemplate(value, out string template)
            ? template
            : DefaultSearchUrl;
    }

    public static bool TryNormalizeSearchTemplate(string? value, out string template)
    {
        string candidate = (value ?? string.Empty).Trim();
        if (candidate.Contains("{searchTerms}", StringComparison.OrdinalIgnoreCase))
            candidate = candidate.Replace("{searchTerms}", "%s", StringComparison.OrdinalIgnoreCase);
        if (candidate.Contains("{query}", StringComparison.OrdinalIgnoreCase))
            candidate = candidate.Replace("{query}", "%s", StringComparison.OrdinalIgnoreCase);

        if (candidate.Contains("%s", StringComparison.Ordinal))
        {
            template = candidate;
            return true;
        }

        template = string.Empty;
        return false;
    }

    private static void Normalize(UserSettings s)
    {
        s.SearchQueryUrl = NormalizeSearchTemplate(s.SearchQueryUrl);
        if (string.IsNullOrWhiteSpace(s.HomePageUrl))
            s.HomePageUrl = "retro96:home";
        if (s.UserAgentOverride.Length > 2048)
            s.UserAgentOverride = s.UserAgentOverride[..2048];
        if (!IsHtmlColor(s.ForcedBackgroundColor))
            s.ForcedBackgroundColor = DefaultBackgroundColor;
        if (s.DefaultPageZoomPercent is not (100 or 125 or 150 or 200))
            s.DefaultPageZoomPercent = 100;
        s.JavaScriptMaxExecutionSeconds = Math.Clamp(s.JavaScriptMaxExecutionSeconds, 1, 60);
        s.JavaScriptMemoryLimitMb = Math.Clamp(s.JavaScriptMemoryLimitMb, 1, 512);
        s.JavaScriptMaxCallDepth = Math.Clamp(s.JavaScriptMaxCallDepth, 32, 2000);
        s.MaxScriptSpliceTokens = Math.Clamp(s.MaxScriptSpliceTokens, 1000, 2_000_000);
        s.JavaMaxCallDepth = Math.Clamp(s.JavaMaxCallDepth, 32, 2000);
        s.MaxHttpRedirects = Math.Clamp(s.MaxHttpRedirects, 0, 20);
        s.HttpConnectTimeoutSeconds = Math.Clamp(s.HttpConnectTimeoutSeconds, 1, 120);
        s.HttpResponseTimeoutSeconds = Math.Clamp(s.HttpResponseTimeoutSeconds, 1, 300);
        s.MaxConcurrentResourceFetches = Math.Clamp(s.MaxConcurrentResourceFetches, 1, 32);
        s.MaxResourceFetchesPerPage = Math.Clamp(s.MaxResourceFetchesPerPage, 1, 10000);
        s.AnimatedGifSpeedPercent = Math.Clamp(s.AnimatedGifSpeedPercent, 25, 400);
        s.BlinkIntervalMilliseconds = Math.Clamp(s.BlinkIntervalMilliseconds, 100, 2000);
        s.MarqueeSpeedPercent = Math.Clamp(s.MarqueeSpeedPercent, 25, 400);
    }

    private static bool IsHtmlColor(string value)
    {
        if (string.IsNullOrWhiteSpace(value)) return false;
        value = value.Trim();
        if (!value.StartsWith('#')) return false;
        if (value.Length is not (4 or 7)) return false;
        for (int i = 1; i < value.Length; i++)
            if (!Uri.IsHexDigit(value[i])) return false;
        return true;
    }

    private static bool ParseBool(string value, bool fallback) =>
        bool.TryParse(value, out bool result) ? result : fallback;

    private static string BoolText(bool value) => value ? "true" : "false";

    public Retro96.Drawing.Color GetForcedBackgroundColor()
    {
        try
        {
            string hex = ForcedBackgroundColor.Trim().TrimStart('#');
            if (hex.Length == 3)
                hex = string.Concat(hex[0], hex[0], hex[1], hex[1], hex[2], hex[2]);
            int value = int.Parse(hex, NumberStyles.HexNumber, CultureInfo.InvariantCulture);
            return Retro96.Drawing.Color.FromArgb((value >> 16) & 0xFF, (value >> 8) & 0xFF, value & 0xFF);
        }
        catch
        {
            return Retro96.Drawing.Color.FromArgb(0xC0, 0xC0, 0xC0);
        }
    }
}
