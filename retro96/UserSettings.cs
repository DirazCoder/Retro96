using System;
using System.Globalization;
using System.IO;
using System.Text;

namespace Retro96;

public enum RetroEngineMode
{
    Netscape3,
    InternetExplorer3
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
    public const string DefaultNetscapeUserAgent = "Mozilla/3.0 (compatible; Retro96/1.0; Windows 95)";
    public const string DefaultIe3UserAgent = "Mozilla/2.0 (compatible; MSIE 3.02; Windows 95)";
    public const string DefaultBackgroundColor = "#C0C0C0";

    public string SearchQueryUrl { get; set; } = DefaultSearchUrl;
    public string HomePageUrl { get; set; } = "retro96:home";

    public RetroEngineMode EngineMode { get; set; } = RetroEngineMode.Netscape3;
    public string UserAgentOverride { get; set; } = "";
    public BackgroundMode BackgroundMode { get; set; } = BackgroundMode.PageDefault;
    public string ForcedBackgroundColor { get; set; } = DefaultBackgroundColor;
    public bool LoadImages { get; set; } = true;
    public bool EnableJavaScript { get; set; } = true;
    public bool AllowScriptedWindows { get; set; } = true;

    // Advanced browser-engine feature switches.
    public bool LoadStylesheets { get; set; } = true;
    public bool LoadFrames { get; set; } = true;
    public bool AllowFormSubmissions { get; set; } = true;
    public bool EnableJavaScriptTimers { get; set; } = true;
    public bool EnableJavaScriptDialogs { get; set; } = true;
    public bool FollowHttpRedirects { get; set; } = true;
    public bool FollowMetaRefresh { get; set; } = true;
    public bool EnableCookies { get; set; } = true;
    public bool SendReferrer { get; set; } = true;
    public bool AnimateImages { get; set; } = true;
    public bool BlinkText { get; set; } = true;
    public bool MarqueeText { get; set; } = true;
    public TrustMode TrustMode { get; set; } = TrustMode.High;
    public bool HostCheckImages { get; set; } = true;
    public bool DiscardPageStateOnClose { get; set; } = true;

    private static string FilePath =>
        Path.Combine(AppContext.BaseDirectory, "retro96.ini");

    public string EffectiveUserAgent =>
        string.IsNullOrWhiteSpace(UserAgentOverride)
            ? (EngineMode == RetroEngineMode.InternetExplorer3
                ? DefaultIe3UserAgent
                : DefaultNetscapeUserAgent)
            : UserAgentOverride.Trim();

    public UserSettings Clone() => new()
    {
        SearchQueryUrl = SearchQueryUrl,
        HomePageUrl = HomePageUrl,
        EngineMode = EngineMode,
        UserAgentOverride = UserAgentOverride,
        BackgroundMode = BackgroundMode,
        ForcedBackgroundColor = ForcedBackgroundColor,
        LoadImages = LoadImages,
        EnableJavaScript = EnableJavaScript,
        AllowScriptedWindows = AllowScriptedWindows,
        LoadStylesheets = LoadStylesheets,
        LoadFrames = LoadFrames,
        AllowFormSubmissions = AllowFormSubmissions,
        EnableJavaScriptTimers = EnableJavaScriptTimers,
        EnableJavaScriptDialogs = EnableJavaScriptDialogs,
        FollowHttpRedirects = FollowHttpRedirects,
        FollowMetaRefresh = FollowMetaRefresh,
        EnableCookies = EnableCookies,
        SendReferrer = SendReferrer,
        AnimateImages = AnimateImages,
        BlinkText = BlinkText,
        MarqueeText = MarqueeText,
        TrustMode = TrustMode,
        HostCheckImages = HostCheckImages,
        DiscardPageStateOnClose = DiscardPageStateOnClose
    };

    public static UserSettings Load()
    {
        var s = new UserSettings();
        try
        {
            if (!File.Exists(FilePath)) return s;
            foreach (var raw in File.ReadAllLines(FilePath))
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
                    case "scriptedwindows.enabled":
                        s.AllowScriptedWindows = ParseBool(value, s.AllowScriptedWindows);
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
                    case "javascript.timers":
                        s.EnableJavaScriptTimers = ParseBool(value, s.EnableJavaScriptTimers);
                        break;
                    case "javascript.dialogs":
                        s.EnableJavaScriptDialogs = ParseBool(value, s.EnableJavaScriptDialogs);
                        break;
                    case "network.redirects":
                        s.FollowHttpRedirects = ParseBool(value, s.FollowHttpRedirects);
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
                    case "render.blink":
                        s.BlinkText = ParseBool(value, s.BlinkText);
                        break;
                    case "render.marquee":
                        s.MarqueeText = ParseBool(value, s.MarqueeText);
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
                }
            }
        }
        catch { /* unreadable settings → defaults */ }

        Normalize(s);
        return s;
    }

    public void Save()
    {
        Normalize(this);
        try
        {
            var sb = new StringBuilder();
            sb.AppendLine("[Preferences]");
            sb.AppendLine("; Retro96 preferences — human editable");
            sb.AppendLine("Search.Url=" + SearchQueryUrl);
            sb.AppendLine("Home.Url=" + HomePageUrl);
            sb.AppendLine("Engine.Mode=" + EngineMode);
            sb.AppendLine("UserAgent.Override=" + UserAgentOverride);
            sb.AppendLine("Background.Mode=" + BackgroundMode);
            sb.AppendLine("Background.Color=" + ForcedBackgroundColor);
            sb.AppendLine("Images.Enabled=" + BoolText(LoadImages));
            sb.AppendLine("JavaScript.Enabled=" + BoolText(EnableJavaScript));
            sb.AppendLine("ScriptedWindows.Enabled=" + BoolText(AllowScriptedWindows));
            sb.AppendLine("Stylesheets.Enabled=" + BoolText(LoadStylesheets));
            sb.AppendLine("Frames.Enabled=" + BoolText(LoadFrames));
            sb.AppendLine("Forms.Enabled=" + BoolText(AllowFormSubmissions));
            sb.AppendLine("JavaScript.Timers=" + BoolText(EnableJavaScriptTimers));
            sb.AppendLine("JavaScript.Dialogs=" + BoolText(EnableJavaScriptDialogs));
            sb.AppendLine("Network.Redirects=" + BoolText(FollowHttpRedirects));
            sb.AppendLine("Network.MetaRefresh=" + BoolText(FollowMetaRefresh));
            sb.AppendLine("Network.Cookies=" + BoolText(EnableCookies));
            sb.AppendLine("Network.Referrer=" + BoolText(SendReferrer));
            sb.AppendLine("Render.Animation=" + BoolText(AnimateImages));
            sb.AppendLine("Render.Blink=" + BoolText(BlinkText));
            sb.AppendLine("Render.Marquee=" + BoolText(MarqueeText));
            sb.AppendLine("Security.Mode=" + TrustMode);
            sb.AppendLine("Security.HostImageCheck=" + BoolText(HostCheckImages));
            sb.AppendLine("Security.DiscardState=" + BoolText(DiscardPageStateOnClose));
            File.WriteAllText(FilePath, sb.ToString(), new UTF8Encoding(false));
        }
        catch { /* read-only dir etc. — keep running with in-memory value */ }
    }

    private static void Normalize(UserSettings s)
    {
        if (string.IsNullOrWhiteSpace(s.SearchQueryUrl) || !s.SearchQueryUrl.Contains("%s", StringComparison.Ordinal))
            s.SearchQueryUrl = DefaultSearchUrl;
        if (string.IsNullOrWhiteSpace(s.HomePageUrl))
            s.HomePageUrl = "retro96:home";
        if (s.UserAgentOverride.Length > 2048)
            s.UserAgentOverride = s.UserAgentOverride[..2048];
        if (!IsHtmlColor(s.ForcedBackgroundColor))
            s.ForcedBackgroundColor = DefaultBackgroundColor;
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
