using Retro96.Engine.Dom;
using Retro96.Engine.Network;

namespace Retro96;

public partial class Form1
{
    private readonly MidiPlayer _midiPlayer = new();
    private readonly ToolStrip _midiBar = new() { GripStyle = ToolStripGripStyle.Hidden, Visible = false };
    private readonly ToolStripLabel _midiLabel = new("MIDI");
    private readonly ToolStripButton _midiPlay = new("Play");
    private readonly ToolStripButton _midiPause = new("Pause");
    private readonly ToolStripButton _midiStop = new("Stop");
    private readonly ToolStripButton _midiLoop = new("Loop") { CheckOnClick = true };
    private bool _midiUiSuppressed;
    private byte[]? _embeddedMidiBytes;
    private string? _embeddedMidiName;
    private DomElement? _embeddedMidiElement;
    private int _embeddedMidiRepeatCount = 1;
    private int _embeddedMidiVolume = 100;

    private void InitializeMidi()
    {
        _midiBar.Items.Add(_midiLabel);
        _midiBar.Items.Add(new ToolStripSeparator());
        _midiBar.Items.Add(_midiPlay); _midiBar.Items.Add(_midiPause); _midiBar.Items.Add(_midiStop); _midiBar.Items.Add(_midiLoop);
        _midiPlay.Click += (_, _) => _midiPlayer.Resume();
        _midiPause.Click += (_, _) => _midiPlayer.Pause();
        _midiStop.Click += (_, _) => _midiPlayer.Stop();
        _midiPlayer.StateChanged += (_, _) => BeginInvokeSafe(UpdateMidiUi);
        _midiPlayer.PlaybackComplete += (_, _) => BeginInvokeSafe(() => { UpdateMidiUi(); _pluginManager?.RaiseAudioComplete(); });
    }

    private void DisposeMidi()
    {
        try { _midiPlayer.Stop(); _midiPlayer.Dispose(); } catch { }
    }

    private void UpdateMidiUi()
    {
        bool visible = !_midiUiSuppressed && !string.IsNullOrWhiteSpace(_midiPlayer.CurrentFileName);
        _midiBar.Visible = visible;
        _midiLabel.Text = visible ? "MIDI: " + _midiPlayer.CurrentFileName : "MIDI";
        _midiLoop.Checked = _midiPlayer.Loop;
        _canvas.UpdateEmbeddedMidiControls(_midiPlayer.Loop, _midiPlayer.IsPlaying,
            _embeddedMidiElement != null ? _embeddedMidiVolume : (int)Math.Round(_midiPlayer.Volume * 100f));
    }

    private void HandleEmbeddedMidiControl(string action)
    {
        if (action.StartsWith("volume:", StringComparison.OrdinalIgnoreCase))
        {
            if (int.TryParse(action[7..], out int volume))
            {
                _embeddedMidiVolume = Math.Clamp(volume, 0, 100);
                _midiPlayer.Volume = _embeddedMidiVolume / 100f;
                UpdateMidiUi();
            }
            return;
        }

        switch (action)
        {
            case "play":
                if (_midiPlayer.CurrentFileName != null) _midiPlayer.Resume();
                else if (_embeddedMidiBytes != null && _embeddedMidiName != null) { _ = StartStoredEmbeddedMidiAsync(); return; }
                break;
            case "pause": _midiPlayer.Pause(); break;
            case "stop": _midiPlayer.Stop(); break;
            case "loop": _midiPlayer.Loop = !_midiPlayer.Loop; break;
        }
        UpdateMidiUi();
    }

    private async Task StartStoredEmbeddedMidiAsync()
    {
        if (_embeddedMidiBytes == null || _embeddedMidiName == null) return;
        try
        {
            _midiPlayer.Volume = _embeddedMidiVolume / 100f;
            await _midiPlayer.PlayBytesAsync(_embeddedMidiBytes, _embeddedMidiName, _embeddedMidiRepeatCount).ConfigureAwait(false);
            BeginInvokeSafe(UpdateMidiUi);
        }
        catch { }
    }

    private async Task StartEmbeddedMidiAsync(DomDocument document, ParsedUrl baseUrl, CancellationToken ct, long generation)
    {
        _midiPlayer.Stop();
        _embeddedMidiBytes = null;
        _embeddedMidiName = null;
        _embeddedMidiElement = null;
        _embeddedMidiRepeatCount = 1;
        _embeddedMidiVolume = 100;
        _midiUiSuppressed = false;
        BeginInvokeSafe(() => _canvas.ClearEmbeddedMidiControls());

        var candidates = document.ElementDescendants()
            .Where(e => e.TagName is "bgsound" or "embed")
            .Where(e => !string.IsNullOrWhiteSpace(e.GetAttr("src")))
            .ToList();
        var selected = candidates.FirstOrDefault(ShouldLegacyMidiAutostart)
                    ?? candidates.FirstOrDefault(e => e.TagName == "embed");
        if (selected == null) { UpdateMidiUi(); return; }

        string? src = selected.GetAttr("src");
        if (!TryResolveMidiSource(baseUrl, src, out string absolute)) { UpdateMidiUi(); return; }

        try
        {
            var result = await _httpClient.GetAsync(ParsedUrl.Parse(absolute), _cookieStore, ct, ResourceKind.Other).ConfigureAwait(false);
            if (result is not HttpSuccess success || generation != _navGeneration) return;

            bool isBackgroundSound = selected.TagName == "bgsound";
            int repeatCount = isBackgroundSound ? LegacyMidiLoop.ParseBgSoundLoop(selected.GetAttr("loop")) : LegacyMidiLoop.ParseEmbedLoop(selected.GetAttr("loop"));
            bool autostart = isBackgroundSound || LegacyMidiLoop.ParseAutoStart(selected.GetAttr("autostart"));
            int volume = isBackgroundSound ? 100 : LegacyMidiLoop.ParseVolume(selected.GetAttr("volume"));
            bool showControls = !isBackgroundSound && EmbedHasVisibleControls(selected);

            _embeddedMidiBytes = success.Body;
            _embeddedMidiName = SuggestDownloadFileName(absolute, success.Headers);
            _embeddedMidiElement = showControls ? selected : null;
            _embeddedMidiRepeatCount = repeatCount;
            _embeddedMidiVolume = volume;
            _midiUiSuppressed = true;
            _midiPlayer.Volume = volume / 100f;
            _midiPlayer.RepeatCount = repeatCount;

            if (autostart) await _midiPlayer.PlayBytesAsync(success.Body, _embeddedMidiName, repeatCount).ConfigureAwait(false);
            if (generation == _navGeneration)
            {
                BeginInvokeSafe(() =>
                {
                    if (showControls) _canvas.SetEmbeddedMidiControls(selected, _embeddedMidiName!, repeatCount != 1, _midiPlayer.IsPlaying, volume);
                    else _canvas.ClearEmbeddedMidiControls();
                    UpdateMidiUi();
                });
            }
        }
        catch { }
    }

    private static bool ShouldLegacyMidiAutostart(DomElement element) =>
        element.TagName == "bgsound" || LegacyMidiLoop.ParseAutoStart(element.GetAttr("autostart"));

    private static bool EmbedHasVisibleControls(DomElement element)
    {
        if (LegacyMidiLoop.IsTrue(element.GetAttr("hidden"))) return false;
        string? width = element.GetAttr("width");
        string? height = element.GetAttr("height");
        if (string.IsNullOrWhiteSpace(width) && string.IsNullOrWhiteSpace(height)) return false;
        if (int.TryParse(width, out int w) && w == 0) return false;
        if (int.TryParse(height, out int h) && h == 0) return false;
        return true;
    }

    private static bool TryResolveMidiSource(ParsedUrl baseUrl, string? src, out string absolute)
    {
        absolute = string.Empty;
        if (string.IsNullOrWhiteSpace(src)) return false;
        try
        {
            var resolved = baseUrl.Resolve(src);
            string path = resolved.Path ?? string.Empty;
            if (!path.EndsWith(".mid", StringComparison.OrdinalIgnoreCase) && !path.EndsWith(".midi", StringComparison.OrdinalIgnoreCase)) return false;
            absolute = resolved.ToAbsolute();
            return true;
        }
        catch { return false; }
    }
}
