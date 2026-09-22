using System;
using System.IO;
using System.Text;

namespace Retro96;

/// <summary>
/// User preferences, persisted as a retro96.ini next to the exe — the
/// era-appropriate settings mechanism.  Edit the file directly or via
/// File → Preferences….
/// </summary>
public class UserSettings
{
    /// <summary>
    /// Default search engine: FrogFind.  %s marks where the query goes.
    /// </summary>
    public const string DefaultSearchUrl = "https://www.frogfind.com/";

    public string SearchQueryUrl { get; set; } = DefaultSearchUrl;
    public string HomePageUrl { get; set; } = "retro96://home";

    private static string FilePath =>
        Path.Combine(AppContext.BaseDirectory, "retro96.ini");

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
                if (key == "search.url" && value.Length > 0)
                    s.SearchQueryUrl = value;
                if (key == "home.url" && value.Length > 0)
                    s.HomePageUrl = value;
            }
        }
        catch { /* unreadable settings → defaults */ }
        return s;
    }

    public void Save()
    {
        try
        {
            var sb = new StringBuilder();
            sb.AppendLine("[Preferences]");
            sb.AppendLine("; %s marks where the search query goes");
            sb.AppendLine("; Default: " + DefaultSearchUrl + "  (FrogFind)");
            sb.AppendLine("Search.Url=" + SearchQueryUrl);
            sb.AppendLine("Home.Url=" + HomePageUrl);
            File.WriteAllText(FilePath, sb.ToString());
        }
        catch { /* read-only dir etc. — keep running with in-memory value */ }
    }
}