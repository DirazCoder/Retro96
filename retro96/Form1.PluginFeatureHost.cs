using System.Drawing;
using System.Collections.Concurrent;
using System.IO;
using System.Text;
using System.Security;
using System.Windows.Forms;
using Retro96.Engine.Network;
using Retro96.Plugins;

namespace Retro96;

public partial class Form1
{
    private readonly ToolStrip _pluginToolbar = new() { GripStyle = ToolStripGripStyle.Hidden, Visible = false };
    private readonly Panel _pluginPanelContainer = new() { Dock = DockStyle.Right, Width = 300, Visible = false, BorderStyle = BorderStyle.FixedSingle };
    private readonly TabControl _pluginPanelTabs = new() { Dock = DockStyle.Fill };
    private readonly NotifyIcon _pluginNotifyIcon = new() { Icon = SystemIcons.Application, Visible = false, Text = "Retro96" };
    private readonly List<PluginContextMenuRegistration> _pluginContextItems = new();
    private readonly Dictionary<string, PluginPanelHost> _pluginPanels = new(StringComparer.Ordinal);
    private readonly object _pluginUiSync = new();
    private readonly System.Windows.Forms.Timer _clipboardPollTimer = new() { Interval = 750 };
    private string? _clipboardFingerprint;
    private readonly PluginPcmMixer _pluginPcmMixer = new();

    private void InitializePluginHostUi()
    {
        _pluginToolbar.Visible = false;
        Controls.Add(_pluginPanelContainer);
        _pluginPanelContainer.Controls.Add(_pluginPanelTabs);
        Controls.Add(_pluginToolbar);
        _pluginToolbar.Dock = DockStyle.Top;
        _pluginPanelContainer.BringToFront();
        _pluginNotifyIcon.BalloonTipClicked += PluginNotificationBalloonClicked;
        _clipboardPollTimer.Tick += (_, _) => PollClipboardChanged();
        try { _clipboardFingerprint = ClipboardFingerprint(); } catch { }
        _clipboardPollTimer.Start();
    }

    internal IDisposable AddPluginToolbarButton(string pluginId, string label, string tooltip, Action callback)
    {
        if (InvokeRequired) return (IDisposable)Invoke(() => AddPluginToolbarButton(pluginId, label, tooltip, callback));
        var button = new ToolStripButton(label) { ToolTipText = tooltip ?? string.Empty, Tag = pluginId, DisplayStyle = ToolStripItemDisplayStyle.Text };
        button.Click += (_, _) => { try { callback(); } catch (Exception ex) { DebugLog.WriteException($"Plugin toolbar '{pluginId}'", ex); } };
        _pluginToolbar.Items.Add(button); _pluginToolbar.Visible = true;
        return new DelegateDisposable(() =>
        {
            try { _pluginToolbar.Items.Remove(button); button.Dispose(); } catch { }
            if (_pluginToolbar.Items.Count == 0) _pluginToolbar.Visible = false;
        });
    }

    internal IDisposable AddPluginContextMenuItem(string pluginId, string label,
        Func<ContextMenuContext, bool> shouldShow, Action<ContextMenuContext> onClick)
    {
        var reg = new PluginContextMenuRegistration(pluginId, label, shouldShow, onClick);
        lock (_pluginUiSync) _pluginContextItems.Add(reg);
        return new DelegateDisposable(() => { lock (_pluginUiSync) _pluginContextItems.Remove(reg); });
    }

    internal void PopulatePluginContextMenu(ContextMenuStrip menu, ContextMenuContext context)
    {
        PluginContextMenuRegistration[] items;
        lock (_pluginUiSync) items = _pluginContextItems.ToArray();
        foreach (var item in items)
        {
            bool visible = false;
            try { visible = item.ShouldShow(context); } catch { }
            if (!visible) continue;
            var menuItem = new ToolStripMenuItem(item.Label);
            menuItem.Click += (_, _) => { try { item.OnClick(context); } catch (Exception ex) { DebugLog.WriteException($"Plugin context item '{item.PluginId}'", ex); } };
            menu.Items.Add(menuItem);
        }
    }

    internal PluginPanelHost CreatePluginPanel(string pluginId, string title, Action<string, string?, bool?, int?> invoke)
    {
        if (InvokeRequired) return (PluginPanelHost)Invoke(() => CreatePluginPanel(pluginId, title, invoke));
        string token = Guid.NewGuid().ToString("N");
        var page = new TabPage(title) { Tag = token };
        var flow = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.TopDown, WrapContents = false, AutoScroll = true, Padding = new Padding(8) };
        page.Controls.Add(flow);
        _pluginPanelTabs.TabPages.Add(page);
        var host = new PluginPanelHost(token, pluginId, page, flow, invoke, () =>
        {
            _pluginPanelTabs.TabPages.Remove(page); page.Dispose(); _pluginPanels.Remove(token); if (_pluginPanelTabs.TabPages.Count == 0) _pluginPanelContainer.Visible = false;
        });
        _pluginPanels[token] = host;
        return host;
    }

    internal void SetPluginProgress(double? fraction)
    {
        if (InvokeRequired) { BeginInvoke(() => SetPluginProgress(fraction)); return; }
        _pluginProgress.Visible = fraction.HasValue;
        if (fraction.HasValue) _pluginProgress.Value = Math.Clamp((int)Math.Round(fraction.Value * 100d), 0, 100);
    }

    internal Task<string?> ShowPluginInputDialogAsync(string title, string prompt, string? defaultValue)
    {
        if (InvokeRequired) return (Task<string?>)Invoke(() => ShowPluginInputDialogAsync(title, prompt, defaultValue));
        using var form = new Form { Text = title, StartPosition = FormStartPosition.CenterParent, ClientSize = new Size(460, 160), FormBorderStyle = FormBorderStyle.FixedDialog, MinimizeBox = false, MaximizeBox = false, AutoScaleMode = AutoScaleMode.Font };
        var label = new Label { Text = prompt, Dock = DockStyle.Top, Height = 50, Padding = new Padding(8) };
        var input = new TextBox { Dock = DockStyle.Top, Text = defaultValue ?? "", Margin = new Padding(8) };
        var buttons = new FlowLayoutPanel { Dock = DockStyle.Bottom, Height = 46, FlowDirection = FlowDirection.RightToLeft, Padding = new Padding(8), WrapContents = false };
        var ok = new Button { Text = "OK", DialogResult = DialogResult.OK, Width = 80 }; var cancel = new Button { Text = "Cancel", DialogResult = DialogResult.Cancel, Width = 80 };
        buttons.Controls.Add(ok); buttons.Controls.Add(cancel); form.Controls.Add(input); form.Controls.Add(label); form.Controls.Add(buttons); form.AcceptButton = ok; form.CancelButton = cancel;
        string? result = form.ShowDialog(this) == DialogResult.OK ? input.Text : null;
        return Task.FromResult(result);
    }

    internal string? GetPluginCookie(string name)
    {
        if (_currentPageUrl == null) return null;
        try { return _cookieStore.GetCookieValue(name, ParsedUrl.Parse(_currentPageUrl)); } catch { return null; }
    }

    internal void SetPluginCookie(string name, string value, CookieOptions options)
    {
        if (_currentPageUrl == null) throw new InvalidOperationException("There is no current page.");
        var url = ParsedUrl.Parse(_currentPageUrl);
        _cookieStore.SetCookieValue(name, value, url, options.Path, options.Domain, options.Expires, options.Secure);
    }

    [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    [System.ComponentModel.Browsable(false)]
    internal float PluginZoom { get => _canvas.ZoomFactor; set => _canvas.ZoomFactor = value; }
    internal Size PluginViewportSize => _canvas.GetViewportSize();
    internal void PluginFind(string text, bool caseSensitive, bool wrapAround) => _canvas.FindInPage(text, caseSensitive, wrapAround);
    internal void PluginFindNext() => _canvas.FindNext();
    internal void PluginFindClear() => _canvas.FindClear();
    internal byte[] PluginCaptureViewport() => _canvas.CaptureViewportPng();

    internal string PluginGetPageText() => _canvas.PageDocument?.InnerText ?? string.Empty;

    internal IReadOnlyList<PluginPageLink> PluginGetPageLinks()
    {
        var document = _canvas.PageDocument;
        if (document == null) return Array.Empty<PluginPageLink>();
        var list = new List<PluginPageLink>();
        foreach (var element in document.Links.Take(2000))
        {
            string href = element.GetAttr("href") ?? string.Empty;
            if (href.Length == 0) continue;
            string absolute = href;
            try { if (document.BaseUrl != null) absolute = document.BaseUrl.Resolve(href).ToAbsolute(); } catch { }
            list.Add(new PluginPageLink(absolute, TruncatePluginText(element.InnerText.Trim(), 512), TruncatePluginText(element.GetAttr("title") ?? string.Empty, 512)));
        }
        return list;
    }

    internal string? PluginGetSelection()
    {
        string value = _canvas.GetPluginSelectedText();
        return string.IsNullOrWhiteSpace(value) ? null : TruncatePluginText(value, 64 * 1024);
    }

    internal IReadOnlyList<HistoryStoreEntry> PluginHistorySearch(string? query, int maxResults) =>
        _historyStore.Search(query ?? string.Empty).Take(Math.Clamp(maxResults, 1, 100)).ToArray();

    internal IReadOnlyList<BookmarkEntry> PluginBookmarksList(int maxResults) =>
        _bookmarkStore.Items.Take(Math.Clamp(maxResults, 1, 500)).ToArray();

    internal void PluginBookmarkAdd(string title, string url)
    {
        if (string.IsNullOrWhiteSpace(url) || url.Length > 8192) throw new ArgumentException("Invalid bookmark URL.", nameof(url));
        if (!_bookmarkStore.Contains(url)) _bookmarkStore.Toggle(TruncatePluginText(title ?? string.Empty, 512), url);
    }

    internal void PluginBookmarkRemove(string url)
    {
        if (!string.IsNullOrWhiteSpace(url)) _bookmarkStore.Delete(url);
    }

    private static string TruncatePluginText(string value, int max) =>
        value.Length <= max ? value : value[..max];

    internal async Task<string> PluginNetworkGetStringAsync(string url, CancellationToken ct) => await PluginNetworkSendAsync(new HttpPluginRequest("GET", url), ct).ConfigureAwait(false);

    internal async Task<byte[]> PluginNetworkGetBytesAsync(string url, CancellationToken ct) => Convert.FromBase64String(await PluginNetworkSendAsync(new HttpPluginRequest("GET", url), ct, binary: true).ConfigureAwait(false));

    internal async Task<string> PluginNetworkPostStringAsync(string url, string body, string contentType, CancellationToken ct) => await PluginNetworkSendAsync(new HttpPluginRequest("POST", url, new Dictionary<string, string>(), Encoding.UTF8.GetBytes(body ?? string.Empty), contentType), ct).ConfigureAwait(false);

    internal async Task<byte[]> PluginNetworkPostBytesAsync(string url, byte[] body, string contentType, CancellationToken ct) => Convert.FromBase64String(await PluginNetworkSendAsync(new HttpPluginRequest("POST", url, new Dictionary<string, string>(), body, contentType), ct, binary: true).ConfigureAwait(false));

    internal async Task<string> PluginNetworkSendRequestAsync(HttpPluginRequest request, CancellationToken ct, bool binary = false) => await PluginNetworkSendAsync(request, ct, binary).ConfigureAwait(false);

    private async Task<string> PluginNetworkSendAsync(HttpPluginRequest request, CancellationToken ct, bool binary = false)
    {
        var parsed = ParsedUrl.Parse(request.Url);
        if (!parsed.IsHttp) throw new SecurityException("Plugin network access is limited to http/https URLs.");
        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var pair in request.Headers ?? new Dictionary<string, string>())
        {
            if (pair.Key.Equals("Host", StringComparison.OrdinalIgnoreCase) || pair.Key.Equals("Content-Length", StringComparison.OrdinalIgnoreCase) || pair.Key.Equals("Connection", StringComparison.OrdinalIgnoreCase)) continue;
            if (pair.Key.Equals("Cookie", StringComparison.OrdinalIgnoreCase) && string.IsNullOrWhiteSpace(pair.Value)) continue;
            headers[pair.Key] = pair.Value;
        }
        HttpResult result = await _httpClient.SendPluginAsync(request.Method, parsed, request.Body, headers, request.ContentType, _cookieStore, ct).ConfigureAwait(false);
        if (result is not HttpSuccess success) throw new InvalidOperationException(result switch { HttpError e => e.Message, CertError e => e.Message, _ => "Plugin network request failed." });
        return binary ? Convert.ToBase64String(success.Body) : BodyDecoder.Decode(success.Body, success.Charset);
    }

    internal async Task PlayPluginAudioAsync(string absolutePath, string displayName, bool loop)
    {
        await Task.Run(() => _midiPlayer.PlayFile(absolutePath, displayName, loop)).ConfigureAwait(false);
        BeginInvokeSafe(UpdateMidiUi);
    }
    internal void StopPluginAudio() => _midiPlayer.Stop();
    internal bool PluginAudioPlaying => _midiPlayer.IsPlaying;
    [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    [System.ComponentModel.Browsable(false)]
    internal Task PlayPluginPcmAsync(string sourceKey, byte[] pcm, PluginPcmFormat format, CancellationToken cancellationToken) =>
        _pluginPcmMixer.PlayAsync(sourceKey, pcm, format, cancellationToken);
    internal Task PushPluginEmbeddedAudioAsync(string sourceKey, byte[] pcm, PluginPcmFormat format, CancellationToken cancellationToken, bool initiallyMuted) =>
        _pluginPcmMixer.PushAsync(sourceKey, pcm, format, cancellationToken, initiallyMuted);
    internal void SetPluginEmbeddedAudioMuted(string sourceKey, bool muted) => _pluginPcmMixer.SetMuted(sourceKey, muted);
    internal void ActivatePluginEmbeddedAudio(string sourceKey) => _pluginPcmMixer.Unmute(sourceKey);
    internal void RemovePluginEmbeddedAudio(string sourceKey) => _pluginPcmMixer.Remove(sourceKey);

    internal float PluginAudioVolume { get => _midiPlayer.Volume; set => _midiPlayer.Volume = Math.Clamp(value, 0f, 1f); }
    [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    [System.ComponentModel.Browsable(false)]
    internal bool PluginAudioLoop { get => _midiPlayer.Loop; set => _midiPlayer.Loop = value; }
    internal string? PluginAudioFileName => _midiPlayer.CurrentFileName;

    internal void ShowPluginNotification(string title, string body, Action? onClick = null)
    {
        if (InvokeRequired) { BeginInvoke(() => ShowPluginNotification(title, body, onClick)); return; }
        _pluginNotificationCallback = onClick;
        _pluginNotifyIcon.Visible = true;
        _pluginNotifyIcon.ShowBalloonTip(5000, title, body, ToolTipIcon.Info);
        BeginInvokeSafe(async () =>
        {
            try { await Task.Delay(5500).ConfigureAwait(true); } catch { }
            if (!IsDisposed) _pluginNotifyIcon.Visible = false;
        });
    }

    private Action? _pluginNotificationCallback;
    private void PluginNotificationBalloonClicked(object? sender, EventArgs e)
    {
        var callback = Interlocked.Exchange(ref _pluginNotificationCallback, null);
        try { callback?.Invoke(); } catch { }
    }

    internal Task<string?> PluginOpenFilePickerAsync(string pluginId, string title, string filter, string pluginDataRoot)
    {
        if (InvokeRequired) return (Task<string?>)Invoke(() => PluginOpenFilePickerAsync(pluginId, title, filter, pluginDataRoot));
        using var dialog = new OpenFileDialog { Title = title, Filter = string.IsNullOrWhiteSpace(filter) ? "All files (*.*)|*.*" : filter, Multiselect = false, CheckFileExists = true };
        if (dialog.ShowDialog(this) != DialogResult.OK) return Task.FromResult<string?>(null);
        string relative = Path.Combine("imports", Guid.NewGuid().ToString("N") + Path.GetExtension(dialog.FileName));
        string dest = SafePluginDataPath(pluginDataRoot, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(dest)!); File.Copy(dialog.FileName, dest, true);
        return Task.FromResult<string?>(relative.Replace(Path.DirectorySeparatorChar, '/'));
    }

    internal Task<bool> PluginSaveFilePickerAsync(string pluginDataRoot, string relativePath, string suggestedFilename, string filter)
    {
        if (InvokeRequired) return (Task<bool>)Invoke(() => PluginSaveFilePickerAsync(pluginDataRoot, relativePath, suggestedFilename, filter));
        string source = SafePluginDataPath(pluginDataRoot, relativePath);
        if (!File.Exists(source)) return Task.FromResult(false);
        using var dialog = new SaveFileDialog { FileName = suggestedFilename, Filter = string.IsNullOrWhiteSpace(filter) ? "All files (*.*)|*.*" : filter, OverwritePrompt = true };
        if (dialog.ShowDialog(this) != DialogResult.OK) return Task.FromResult(false);
        File.Copy(source, dialog.FileName, true); return Task.FromResult(true);
    }

    internal byte[]? GetClipboardImagePng()
    {
        if (!Clipboard.ContainsImage()) return null;
        using var image = Clipboard.GetImage(); if (image == null) return null;
        using var ms = new MemoryStream(); image.Save(ms, System.Drawing.Imaging.ImageFormat.Png); return ms.ToArray();
    }

    internal void SetClipboardImagePng(byte[] pngBytes)
    {
        if (pngBytes == null || pngBytes.Length == 0) return;
        using var ms = new MemoryStream(pngBytes); using var image = Image.FromStream(ms); Clipboard.SetImage(new Bitmap(image));
    }

    private static string SafePluginDataPath(string root, string relative)
    {
        string rootFull = Path.GetFullPath(root.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar);
        string full = Path.GetFullPath(Path.Combine(rootFull, relative));
        if (!full.StartsWith(rootFull, StringComparison.OrdinalIgnoreCase)) throw new SecurityException("Plugin path escapes its private data directory.");
        return full;
    }

    private void PollClipboardChanged()
    {
        try
        {
            string fingerprint = ClipboardFingerprint();
            if (_clipboardFingerprint == null) { _clipboardFingerprint = fingerprint; return; }
            if (!string.Equals(_clipboardFingerprint, fingerprint, StringComparison.Ordinal))
            {
                _clipboardFingerprint = fingerprint;
                _pluginManager?.RaiseClipboardChanged();
            }
        }
        catch { }
    }

    private static string ClipboardFingerprint()
    {
        if (Clipboard.ContainsText()) return "text:" + Clipboard.GetText(TextDataFormat.UnicodeText).GetHashCode(StringComparison.Ordinal);
        if (Clipboard.ContainsImage())
        {
            using var image = Clipboard.GetImage();
            return image == null ? "image:none" : $"image:{image.Width}x{image.Height}";
        }
        return "empty";
    }

    private readonly record struct PluginContextMenuRegistration(string PluginId, string Label, Func<ContextMenuContext, bool> ShouldShow, Action<ContextMenuContext> OnClick);

    internal sealed class PluginPanelHost : IDisposable
    {
        private readonly TabPage _page; private readonly FlowLayoutPanel _flow; private readonly Action<string, string?, bool?, int?> _invoke; private readonly Action _dispose;
        internal string Token { get; }
        internal PluginPanelHost(string token, string pluginId, TabPage page, FlowLayoutPanel flow, Action<string, string?, bool?, int?> invoke, Action dispose) { Token = token; _page = page; _flow = flow; _invoke = invoke; _dispose = dispose; }
        internal string Add(string kind, string? text, string? placeholder, bool? check, string[]? items)
        {
            string token = Guid.NewGuid().ToString("N"); Control control = kind switch
            {
                "label" => new Label { Text = text ?? "", AutoSize = true, Margin = new Padding(0, 0, 0, 8) },
                "button" => new Button { Text = text ?? "Button", AutoSize = true, Margin = new Padding(0, 0, 0, 8) },
                "textbox" => new TextBox { Width = 230, PlaceholderText = placeholder ?? "", Margin = new Padding(0, 0, 0, 8) },
                "checkbox" => new CheckBox { Text = text ?? "", Checked = check ?? false, AutoSize = true, Margin = new Padding(0, 0, 0, 8) },
                "listbox" => new ListBox { Width = 230, Height = 120, DataSource = (items ?? Array.Empty<string>()).ToList(), Margin = new Padding(0, 0, 0, 8) },
                _ => new Label { Text = text ?? "", AutoSize = true }
            };
            control.Tag = token;
            switch (control)
            {
                case Button b: b.Click += (_, _) => _invoke(token, null, null, null); break;
                case TextBox tb: tb.TextChanged += (_, _) => _invoke(token, tb.Text, null, null); break;
                case CheckBox cb: cb.CheckedChanged += (_, _) => _invoke(token, null, cb.Checked, null); break;
                case ListBox lb: lb.SelectedIndexChanged += (_, _) => _invoke(token, null, null, lb.SelectedIndex); break;
            }
            _flow.Controls.Add(control); return token;
        }
        internal void Set(string widgetToken, string? text, bool? enabled)
        {
            foreach (Control c in _flow.Controls)
                if (string.Equals(c.Tag as string, widgetToken, StringComparison.Ordinal)) { if (text != null) c.Text = text; if (enabled.HasValue) c.Enabled = enabled.Value; break; }
        }
        internal void Clear() => _flow.Controls.Clear();
        internal void Show()
        {
            if (_page.Parent is not TabControl tabs) return;
            tabs.Visible = true;
            _page.Visible = true;
            tabs.SelectedTab = _page;
        }
        internal void Hide()
        {
            if (_page.Parent is not TabControl tabs) return;
            _page.Visible = false;
            if (ReferenceEquals(tabs.SelectedTab, _page))
            {
                foreach (TabPage other in tabs.TabPages)
                {
                    if (!other.Visible || ReferenceEquals(other, _page)) continue;
                    tabs.SelectedTab = other;
                    return;
                }
            }
            if (tabs.TabPages.Count == 0) tabs.Visible = false;
        }
        public void Dispose() { try { _dispose(); } catch { } }
    }
}
