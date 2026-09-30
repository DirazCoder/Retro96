using System.Text.RegularExpressions;
using Retro96.Engine.Network;

namespace Retro96;

public partial class Form1
{
    private readonly ToolStripMenuItem _downloadsMenu = new("Downloads");
    private readonly DownloadManager _downloadManager = new();

    private void InitializeDownloads()
    {
        _viewMenu.DropDownItems.Add(_downloadsMenu);
        _downloadsMenu.Click += (_, _) => OpenDownloadsWindow();
    }

    private void OpenDownloadsWindow()
    {
        var dialog = new DownloadsDialog(_downloadManager);
        dialog.Show(this);
    }

    private static string SuggestDownloadFileName(string url, IReadOnlyDictionary<string, string> headers)
    {
        if (headers.TryGetValue("content-disposition", out var disposition))
        {
            var m = Regex.Match(disposition, "filename\\s*=\\s*\"?([^\";]+)\"?", RegexOptions.IgnoreCase);
            if (m.Success && !string.IsNullOrWhiteSpace(m.Groups[1].Value)) return Path.GetFileName(m.Groups[1].Value.Trim());
        }
        try
        {
            string name = Path.GetFileName(ParsedUrl.Parse(url).Path);
            if (!string.IsNullOrWhiteSpace(name)) return name;
        }
        catch { }
        return "download";
    }

    private static bool LooksLikeMidi(ParsedUrl url, HttpSuccess success)
    {
        string path = url.Path ?? string.Empty;
        return success.ContentType.Equals("audio/midi", StringComparison.OrdinalIgnoreCase) ||
               success.ContentType.Equals("audio/x-midi", StringComparison.OrdinalIgnoreCase) ||
               path.EndsWith(".mid", StringComparison.OrdinalIgnoreCase) ||
               path.EndsWith(".midi", StringComparison.OrdinalIgnoreCase);
    }

    private static bool ShouldDownload(HttpSuccess success, ParsedUrl url)
    {
        if (success.Headers.TryGetValue("content-disposition", out var cd) && cd.Contains("attachment", StringComparison.OrdinalIgnoreCase)) return true;
        if (LooksLikeMidi(url, success)) return false;
        string ct = success.ContentType ?? "";
        if (ct.StartsWith("text/html", StringComparison.OrdinalIgnoreCase) || ct.StartsWith("text/plain", StringComparison.OrdinalIgnoreCase) ||
            ct.StartsWith("image/", StringComparison.OrdinalIgnoreCase) || ct.StartsWith("text/css", StringComparison.OrdinalIgnoreCase)) return false;
        string path = url.Path.ToLowerInvariant();
        string[] renderable = [".htm", ".html", ".txt", ".css", ".gif", ".jpg", ".jpeg", ".png", ".xbm"];
        if (renderable.Any(path.EndsWith)) return false;
        return ct.StartsWith("application/", StringComparison.OrdinalIgnoreCase) || ct.StartsWith("audio/", StringComparison.OrdinalIgnoreCase) || ct.StartsWith("video/", StringComparison.OrdinalIgnoreCase);
    }

    private async Task<bool> TryHandleBinaryNavigationAsync(HttpSuccess success, ParsedUrl url, CancellationToken ct, long generation)
    {
        if (LooksLikeMidi(url, success))
        {
            _embeddedMidiBytes = null;
            _embeddedMidiName = null;
            _embeddedMidiElement = null;
            _midiUiSuppressed = false;
            string name = SuggestDownloadFileName(url.ToAbsolute(), success.Headers);
            if (generation != _navGeneration) return true;
            await _midiPlayer.PlayBytesAsync(success.Body, name, false).ConfigureAwait(false);
            if (generation == _navGeneration)
            {
                BeginInvokeSafe(() => { UpdateMidiUi(); _statusLabel.Text = "Playing MIDI: " + name; });
                _canvas.ClearForNavigation();
            }
            return true;
        }
        if (!ShouldDownload(success, url)) return false;
        string fileName = SuggestDownloadFileName(url.ToAbsolute(), success.Headers);
        using var save = new SaveFileDialog { FileName = fileName, Title = "Save Download", OverwritePrompt = true };
        if (save.ShowDialog(this) != DialogResult.OK) return true;
        _downloadManager.Start(url.ToAbsolute(), success.Body, fileName, save.FileName);
        _statusLabel.Text = "Download started: " + fileName;
        return true;
    }

    protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
    {
        if (keyData == (Keys.Control | Keys.J))
        {
            OpenDownloadsWindow();
            return true;
        }

        if (keyData == (Keys.Control | Keys.F))
        {
            ShowFindDialog();
            return true;
        }

        if (keyData == Keys.F3)
        {
            FindNextFromShortcut();
            return true;
        }

        if (keyData == (Keys.Shift | Keys.F3))
        {
            FindPreviousFromShortcut();
            return true;
        }

        if (keyData == (Keys.Control | Keys.D0))
        {
            InvokeZoomReset();
            return true;
        }

        if (IsZoomInShortcut(keyData))
        {
            InvokeZoomShortcut(1);
            return true;
        }

        if (IsZoomOutShortcut(keyData))
        {
            InvokeZoomShortcut(-1);
            return true;
        }

        return base.ProcessCmdKey(ref msg, keyData);
    }

    private static bool IsZoomInShortcut(Keys keyData) =>
        keyData == (Keys.Control | Keys.Oemplus) ||
        keyData == (Keys.Control | Keys.Shift | Keys.Oemplus) ||
        keyData == (Keys.Control | Keys.Add);

    private static bool IsZoomOutShortcut(Keys keyData) =>
        keyData == (Keys.Control | Keys.OemMinus) ||
        keyData == (Keys.Control | Keys.Subtract);

    private void DisposeDownloads()
    {
        try { _downloadManager.Dispose(); } catch { }
    }
}
