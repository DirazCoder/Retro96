namespace Retro96;

public partial class Form1
{
    private readonly ToolStripDropDownButton _viewMenu = new("View");
    private readonly ToolStripProgressBar _pluginProgress = new() { Width = 120, Visible = false };
    private bool _builtInsInitialized;

    private void InitializeBuiltInFeatures()
    {
        if (_builtInsInitialized) return;
        _builtInsInitialized = true;

        InitializeBookmarksAndHistory();
        InitializeDownloads();
        InitializeMidi();
        _viewMenu.DropDownItems.Add(new ToolStripSeparator());
        _viewMenu.DropDownItems.Add("Mute MIDI").Click += (_, _) => _midiPlayer.Stop();

        _statusStrip.Items.Add(_pluginProgress);
        InitializePluginHostUi();
        BuildToolsMenu();
        BuildHelpMenu();
        _canvas.PluginContextMenuRequested += (menu, context) => _pluginManager?.PopulateContextMenu(menu, context);
        _canvas.EmbeddedMidiControlRequested += HandleEmbeddedMidiControl;

        int fileIndex = _toolbar.Items.IndexOf(_btnFile);
        if (fileIndex >= 0)
        {
            int insertAt = fileIndex + 1;
            _toolbar.Items.Insert(insertAt++, _bookmarksMenu);
            _toolbar.Items.Insert(insertAt++, _historyMenu);
            _toolbar.Items.Insert(insertAt, _viewMenu);
        }

        _midiBar.BringToFront();
        Controls.Add(_midiBar);
        _midiBar.BringToFront();

        Activated += (_, _) => _pluginManager?.RaiseWindowFocusChanged(true);
        Deactivate += (_, _) => _pluginManager?.RaiseWindowFocusChanged(false);
    }

    private void DisposeBuiltInFeatures()
    {
        DisposeMidi();
        try { _clipboardPollTimer.Stop(); } catch { }
        try { _pluginNotifyIcon.Visible = false; _pluginNotifyIcon.Dispose(); } catch { }
        DisposeDownloads();
    }

    private void BeginInvokeSafe(Action action)
    {
        if (IsDisposed || !IsHandleCreated) return;
        try { BeginInvoke(action); } catch { }
    }
}
