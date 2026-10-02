namespace Retro96;

using System;
using System.Drawing;
using System.Windows.Forms;

public partial class Form1
{
    // Zoom limits. These must match the canvas's own clamping — ideally the
    // canvas would expose them; until then they live in exactly one place
    // instead of being duplicated as magic numbers.
    private const float MinZoom = 0.25f;
    private const float MaxZoom = 4f;
    private const float ZoomCompareEpsilon = 0.0005f;

    private readonly ToolStripButton _btnFind = new("Find");
    private readonly ToolStripButton _btnZoomOut = new("−");
    private readonly ToolStripButton _btnZoomReset = new("100%");
    private readonly ToolStripButton _btnZoomIn = new("+");
    private FindInPageDialog? _findDialog;
    private string _lastFindQuery = string.Empty;
    private bool _lastFindCaseSensitive;

    // True when a find query is live on the canvas (even with zero matches).
    // Unlike the match count, this distinguishes "no search" from "search
    // with no results", so F3 on a non-matching query doesn't re-run the
    // full search every time.
    private bool _findActive;

    private bool _findZoomUiInitialized;

    private void InitializeFindZoomUi()
    {
        if (_findZoomUiInitialized) return; // never wire the UI twice
        _findZoomUiInitialized = true;

        _canvas.FindChanged += OnCanvasFindChanged;
        _canvas.ZoomChanged += OnCanvasZoomChanged;
        _canvas.PageChanged += OnCanvasPageChanged;

        _btnFind.DisplayStyle = ToolStripItemDisplayStyle.Text;
        _btnFind.ToolTipText = "Find in page (Ctrl+F)";
        _btnFind.AccessibleName = "Find in page";
        _btnZoomOut.DisplayStyle = ToolStripItemDisplayStyle.Text;
        _btnZoomOut.ToolTipText = "Zoom out (Ctrl+Minus)";
        _btnZoomOut.AccessibleName = "Zoom out";
        _btnZoomReset.DisplayStyle = ToolStripItemDisplayStyle.Text;
        _btnZoomReset.AutoSize = false;
        // WinForms doesn't scale explicit ToolStripItem widths; convert the
        // 96-DPI design width so "400%" doesn't clip at high DPI.
        _btnZoomReset.Width = ScaleX(48);
        _btnZoomReset.ToolTipText = "Reset zoom (Ctrl+0)";
        _btnZoomReset.AccessibleName = "Reset zoom";
        _btnZoomIn.DisplayStyle = ToolStripItemDisplayStyle.Text;
        _btnZoomIn.ToolTipText = "Zoom in (Ctrl+Plus)";
        _btnZoomIn.AccessibleName = "Zoom in";

        _btnFind.Click += (_, _) => ShowFindDialog();
        _btnZoomOut.Click += (_, _) => ChangeZoom(-1);
        _btnZoomReset.Click += (_, _) => ResetZoom();
        _btnZoomIn.Click += (_, _) => ChangeZoom(1);

        int printIndex = _toolbar.Items.IndexOf(_btnPrint);
        int insertAt = printIndex >= 0 ? printIndex : _toolbar.Items.Count;
        _toolbar.Items.Insert(insertAt++, _btnFind);
        _toolbar.Items.Insert(insertAt++, new ToolStripSeparator());
        _toolbar.Items.Insert(insertAt++, _btnZoomOut);
        _toolbar.Items.Insert(insertAt++, _btnZoomReset);
        _toolbar.Items.Insert(insertAt, _btnZoomIn);

        // ShortcutKeys both implements the accelerator and renders it in the
        // menu, replacing the old hand-written "\tCtrl+..." text. NOTE: if
        // any of these keys are also handled in a ProcessCmdKey override in
        // another partial file, remove those cases — the menu owns them now.
        // Numpad +/- and Ctrl+NumPad0 are still handled by key-level logic
        // (ShortcutKeys can only bind one combination per item).
        var miFind = new ToolStripMenuItem("Find in Page") { ShortcutKeys = Keys.Control | Keys.F };
        miFind.Click += (_, _) => ShowFindDialog();
        var miFindNext = new ToolStripMenuItem("Find Next") { ShortcutKeys = Keys.F3 };
        miFindNext.Click += (_, _) => EnsureFindSearchActive(previous: false);
        var miFindPrevious = new ToolStripMenuItem("Find Previous") { ShortcutKeys = Keys.Shift | Keys.F3 };
        miFindPrevious.Click += (_, _) => EnsureFindSearchActive(previous: true);
        var miZoomIn = new ToolStripMenuItem("Zoom In") { ShortcutKeys = Keys.Control | Keys.Oemplus };
        miZoomIn.Click += (_, _) => ChangeZoom(1);
        var miZoomOut = new ToolStripMenuItem("Zoom Out") { ShortcutKeys = Keys.Control | Keys.OemMinus };
        miZoomOut.Click += (_, _) => ChangeZoom(-1);
        var miZoomReset = new ToolStripMenuItem("Reset Zoom") { ShortcutKeys = Keys.Control | Keys.D0 };
        miZoomReset.Click += (_, _) => ResetZoom();

        _viewMenu.DropDownItems.AddRange(new ToolStripItem[]
        {
            new ToolStripSeparator(),
            miFind,
            miFindNext,
            miFindPrevious,
            new ToolStripSeparator(),
            miZoomIn,
            miZoomOut,
            miZoomReset
        });

        // Seed the label from the visual zoom so it matches what's on screen
        // even if a pinch gesture is mid-flight at startup.
        UpdateZoomUi(_canvas.VisualZoomFactor);

        FormClosed += (_, _) =>
        {
            // Closing explicitly (rather than relying on the automatic
            // owned-form close) makes the dialog's close logic — saving the
            // query and clearing the canvas search — run now, while
            // everything is still alive.
            try { _findDialog?.Close(); }
            catch (ObjectDisposedException) { }
            _findDialog = null;
        };
    }

    private int ScaleX(int logicalPixels) =>
        (int)Math.Round(logicalPixels * (DeviceDpi / 96f));

    private void OnCanvasZoomChanged(float zoom)
    {
        if (InvokeRequired) { BeginInvokeSafe(() => UpdateZoomUi(zoom)); return; }
        UpdateZoomUi(zoom);
    }

    private void UpdateZoomUi(float zoom)
    {
        int percent = Math.Clamp(
            (int)Math.Round(zoom * 100f, MidpointRounding.AwayFromZero),
            (int)(MinZoom * 100f), (int)(MaxZoom * 100f));
        _btnZoomReset.Text = $"{percent}%";
        _btnZoomOut.Enabled = zoom > MinZoom + ZoomCompareEpsilon;
        _btnZoomIn.Enabled = zoom < MaxZoom - ZoomCompareEpsilon;
        _btnZoomReset.Enabled = Math.Abs(zoom - 1f) > ZoomCompareEpsilon;
    }

    private void ChangeZoom(int direction)
    {
        // Route toolbar/menu zoom through the canvas so button zoom and
        // Ctrl+wheel gesture zoom share one state machine. When a gesture
        // zoom is active this commits the current visual zoom into the layout
        // baseline instead of accidentally stepping from the old layout zoom.
        if (direction == 0) return;
        _canvas.ChangeZoomStep(direction);
    }

    private void ResetZoom() => _canvas.ZoomFactor = 1f;

    private void ShowFindDialog()
    {
        if (IsDisposed || Disposing) return;

        if (_findDialog is { IsDisposed: false })
        {
            _findDialog.BringToFront();
            _findDialog.Activate();

            // Do NOT re-run the search unconditionally: FindInPage selects
            // the first result, so that would reset the current match (and
            // scroll the view) every time Ctrl+F is pressed while the bar is
            // already open. Only re-arm a search the canvas actually lost
            // (e.g. a page change).
            if (!_findActive && !string.IsNullOrEmpty(_findDialog.Query))
            {
                _findDialog.CommitPendingSearch();
                if (!_findActive)
                    RunFind(_findDialog.Query, _findDialog.CaseSensitive);
            }
        }
        else
        {
            var dialog = new FindInPageDialog(
                this,
                _lastFindQuery,
                _lastFindCaseSensitive,
                (query, caseSensitive) =>
                {
                    _lastFindQuery = query;
                    _lastFindCaseSensitive = caseSensitive;
                    RunFind(query, caseSensitive);
                },
                caseSensitive =>
                {
                    // Clear the visible matches ONLY. This must not touch the
                    // persisted find state: the old "clear" path also blanked
                    // _lastFindQuery, which made Next/Prev/Enter/F3 see an
                    // empty query during the 120 ms debounce window and
                    // silently swallowed the action (or re-selected the text).
                    _canvas.FindInPage(string.Empty, caseSensitive, wrapAround: true);
                    _findActive = false;
                },
                () => EnsureFindSearchActive(previous: true),
                () => EnsureFindSearchActive(previous: false));

            // The ONLY writer of the persisted find state. Because the dialog
            // no longer pokes Form1 state on close, persistence no longer
            // depends on FormClosed handler subscription order. FormClosed
            // fires before the controls are disposed, so reading
            // Query/CaseSensitive here is safe.
            dialog.FormClosed += (_, _) =>
            {
                _lastFindQuery = dialog.Query;
                _lastFindCaseSensitive = dialog.CaseSensitive;
                try { _canvas.FindClear(); }
                catch (ObjectDisposedException) { /* owner is tearing down */ }
                _findActive = false;
                if (ReferenceEquals(_findDialog, dialog)) _findDialog = null;
            };

            _findDialog = dialog;
            dialog.Show(this);

            if (!string.IsNullOrEmpty(_lastFindQuery))
                RunFind(_lastFindQuery, _lastFindCaseSensitive);
        }

        _findDialog?.SelectQuery();
        _findDialog?.UpdateStatus(GetFindCount(), GetFindCurrent());
    }

    private void RunFind(string query, bool caseSensitive)
    {
        if (string.IsNullOrEmpty(query)) return;
        _canvas.FindInPage(query, caseSensitive, wrapAround: true);
        _findActive = true;
    }

    private void EnsureFindSearchActive(bool previous)
    {
        // While the dialog is open its text box is the source of truth for
        // the query — the persisted _lastFindQuery can be stale mid-debounce.
        if (_findDialog is { IsDisposed: false })
        {
            if (string.IsNullOrEmpty(_findDialog.Query))
            {
                _findDialog.SelectQuery();
                return;
            }

            // Commit any pending (debounced) edits first so navigation always
            // acts on the query the user currently sees. This is a no-op when
            // the visible search is current — it never resets the match.
            _findDialog.CommitPendingSearch();

            if (!_findActive)
            {
                // The canvas lost the search (e.g. a page change cleared it).
                // FindInPage selects the first result; Shift+F3 then wraps to
                // the last one.
                RunFind(_findDialog.Query, _findDialog.CaseSensitive);
                if (previous) _canvas.FindPrevious();
            }
            else if (previous)
            {
                _canvas.FindPrevious();
            }
            else
            {
                _canvas.FindNext();
            }
            return;
        }

        // Dialog closed: use the persisted query.
        if (string.IsNullOrEmpty(_lastFindQuery))
        {
            ShowFindDialog();
            return;
        }

        if (!_findActive)
        {
            // Rehydrate the search that FindClear dropped when the dialog
            // closed. _findActive — not the match count — decides this, so a
            // query with zero matches doesn't re-run a full search on every F3.
            RunFind(_lastFindQuery, _lastFindCaseSensitive);
            if (previous) _canvas.FindPrevious();
            return;
        }

        if (previous) _canvas.FindPrevious();
        else _canvas.FindNext();
    }

    private int GetFindCount() => _canvas.FindMatchCount;
    private int GetFindCurrent() => _canvas.FindCurrentIndex;

    // Kept as entry points for any keyboard plumbing in other partial files.
    private void FindNextFromShortcut() => EnsureFindSearchActive(previous: false);
    private void FindPreviousFromShortcut() => EnsureFindSearchActive(previous: true);

    private void OnCanvasFindChanged(int count, int current)
    {
        if (InvokeRequired) { BeginInvokeSafe(() => OnCanvasFindChanged(count, current)); return; }
        _findDialog?.UpdateStatus(count, current);
    }

    private void OnCanvasPageChanged()
    {
        if (InvokeRequired) { BeginInvokeSafe(() => OnCanvasPageChanged()); return; }
        if (_findDialog is not { IsDisposed: false }) return;
        if (string.IsNullOrEmpty(_findDialog.Query)) return;

        // Deferred on purpose: if the page change came from find navigation
        // itself (which may SetPage and then re-select a match), the match
        // count will be non-zero by the time this runs and we must not fight
        // it by re-running the search. A genuine page change clears the
        // matches (SetPage clears old match boxes before raising
        // PageChanged), so re-run the live query to restore them.
        BeginInvokeSafe(() =>
        {
            if (_findDialog is not { IsDisposed: false }) return;
            if (string.IsNullOrEmpty(_findDialog.Query)) return;
            if (GetFindCount() > 0) return;
            RunFind(_findDialog.Query, _findDialog.CaseSensitive);
        });
    }
}

internal sealed class FindInPageDialog : Form
{
    private const int DebounceIntervalMs = 120;

    private readonly TextBox _queryBox = new();
    private readonly CheckBox _caseBox = new() { Text = "Match case", AutoSize = true };
    private readonly Button _previousButton = new() { Text = "Previous" };
    private readonly Button _nextButton = new() { Text = "Next" };
    private readonly Button _closeButton = new() { Text = "Close" };
    private readonly Label _findLabel = new();
    private readonly Label _countLabel = new() { AutoSize = true, Text = "Type to find" };
    private readonly TableLayoutPanel _layout = new();
    private readonly Timer _debounceTimer = new() { Interval = DebounceIntervalMs };
    private readonly Font _dialogFont = new("Segoe UI", 9f, FontStyle.Regular, GraphicsUnit.Point);

    // Fires for real (non-empty) searches only.
    private readonly Action<string, bool> _searchChanged;
    // Clears the canvas visuals only; never mutates Form1's persisted state.
    private readonly Action<bool> _searchCleared;
    private readonly Action _previous;
    private readonly Action _next;

    // All pixel constants in this class are authored at 96 DPI and scaled
    // once, here. AutoScaleMode.None keeps a single predictable coordinate
    // system, and the scale factor keeps the geometry in step with the
    // point-sized font (which renders DPI-dependent).
    private readonly float _dpiScale;

    // The query/case state already applied to the canvas. Anything else is
    // "pending" (waiting on the debounce) and gets committed by
    // CommitPendingSearch before navigation.
    private string _appliedQuery = string.Empty;
    private bool _appliedCase;
    private bool _selectOnShown;

    public FindInPageDialog(Form owner, string initialQuery, bool initialCaseSensitive,
                            Action<string, bool> searchChanged, Action<bool> searchCleared,
                            Action previous, Action next)
    {
        _searchChanged = searchChanged;
        _searchCleared = searchCleared;
        _previous = previous;
        _next = next;

        // Prefer the owner's DPI: its handle exists, so this is accurate even
        // on a secondary monitor with a different scale.
        float dpi = owner is not null && owner.DeviceDpi > 0 ? owner.DeviceDpi : DeviceDpi;
        _dpiScale = (dpi <= 0 ? 96f : dpi) / 96f;

        Text = "Find in Page — Retro96";
        FormBorderStyle = FormBorderStyle.FixedToolWindow;
        ShowInTaskbar = false;
        StartPosition = FormStartPosition.CenterParent;
        ClientSize = new Size(S(900), S(142));
        AutoScaleMode = AutoScaleMode.None;
        KeyPreview = true;
        Font = _dialogFont;
        // Deliberately no MinimumSize: a FixedToolWindow can't be resized, and
        // MinimumSize applies to the outer frame (borders + title), not the
        // client area, so mixing it with ClientSize only invites clipping.
        // ControlBox stays enabled so there's a title-bar close affordance
        // (Esc and the Close button still work too).

        _layout.Dock = DockStyle.Fill;
        _layout.Padding = new Padding(S(10));
        _layout.ColumnCount = 1;
        _layout.RowCount = 2;
        _layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100f));
        _layout.RowStyles.Add(new RowStyle(SizeType.Absolute, S(40)));
        _layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100f));

        var topRow = new Panel
        {
            Dock = DockStyle.Fill,
            Margin = Padding.Empty
        };

        _findLabel.Text = "Find:";
        _findLabel.AutoSize = false;
        _findLabel.TextAlign = ContentAlignment.MiddleLeft;
        _findLabel.Size = new Size(S(56), S(26));
        _findLabel.Location = new Point(0, S(1));
        _findLabel.Margin = Padding.Empty;

        _queryBox.BorderStyle = BorderStyle.FixedSingle;
        _queryBox.Location = new Point(S(60), S(7));
        // A single-line TextBox derives its height from the font; only the
        // width is meaningful here.
        _queryBox.Width = S(500);
        _queryBox.Margin = Padding.Empty;
        // Assigned BEFORE TextChanged is wired so opening with a previous
        // query does not count as an edit.
        _queryBox.Text = initialQuery ?? string.Empty;

        _previousButton.TextAlign = ContentAlignment.MiddleCenter;
        _previousButton.Size = new Size(S(100), S(30));
        _previousButton.Location = new Point(S(576), S(5));
        _previousButton.Margin = Padding.Empty;

        _nextButton.TextAlign = ContentAlignment.MiddleCenter;
        _nextButton.Size = new Size(S(100), S(30));
        _nextButton.Location = new Point(S(682), S(5));
        _nextButton.Margin = Padding.Empty;

        _closeButton.TextAlign = ContentAlignment.MiddleCenter;
        _closeButton.Size = new Size(S(86), S(30));
        _closeButton.Location = new Point(S(788), S(5));
        _closeButton.Margin = Padding.Empty;

        topRow.Controls.Add(_findLabel);
        topRow.Controls.Add(_queryBox);
        topRow.Controls.Add(_previousButton);
        topRow.Controls.Add(_nextButton);
        topRow.Controls.Add(_closeButton);

        var bottomRow = new Panel
        {
            Dock = DockStyle.Fill,
            Margin = Padding.Empty
        };

        // Assigned BEFORE CheckedChanged is wired (same reason as above).
        _caseBox.Checked = initialCaseSensitive;
        _caseBox.Location = new Point(0, S(3));
        _caseBox.Margin = Padding.Empty;

        _countLabel.Location = new Point(S(118), S(5)); // refined by LayoutBottomRow
        _countLabel.Margin = Padding.Empty;

        bottomRow.Controls.Add(_caseBox);
        bottomRow.Controls.Add(_countLabel);

        Controls.Add(_layout);
        _layout.Controls.Add(topRow, 0, 0);
        _layout.Controls.Add(bottomRow, 0, 1);
        AcceptButton = _nextButton;
        CancelButton = _closeButton;

        // The initial state counts as "applied" — Form1 runs the persisted
        // query itself right after Show().
        _appliedQuery = _queryBox.Text;
        _appliedCase = _caseBox.Checked;

        // ONE layout mechanism: manual, in the two Layout* methods, driven by
        // Resize/Shown. No Anchor settings — they used to fight the manual
        // pass and cause a second layout per resize.
        topRow.Resize += (_, _) => LayoutTopRow(topRow);
        bottomRow.Resize += (_, _) => LayoutBottomRow(bottomRow);
        Shown += (_, _) =>
        {
            LayoutTopRow(topRow);
            LayoutBottomRow(bottomRow);
            if (_selectOnShown)
            {
                _selectOnShown = false;
                _queryBox.Focus();
                _queryBox.SelectAll();
            }
        };

        _queryBox.TextChanged += (_, _) =>
        {
            _debounceTimer.Stop();
            // Clear the visible matches right away so a stale query is never
            // highlighted while the new one is pending. _searchCleared only
            // resets canvas visuals — it must not clobber Form1's persisted
            // query (that was the debounce-window bug).
            _searchCleared(_caseBox.Checked);
            if (_queryBox.TextLength > 0)
                _debounceTimer.Start();
        };
        _caseBox.CheckedChanged += (_, _) => ApplySearchNow();
        _previousButton.Click += (_, _) => _previous();
        _nextButton.Click += (_, _) => _next();
        _closeButton.Click += (_, _) => Close();
        _debounceTimer.Tick += (_, _) =>
        {
            _debounceTimer.Stop();
            ApplySearchNow();
        };

        KeyDown += OnDialogKeyDown;

        // Only stops the timer here; disposal happens in Dispose(bool), which
        // also runs if the form is ever disposed without a Close.
        FormClosed += (_, _) => _debounceTimer.Stop();
    }

    private void OnDialogKeyDown(object? sender, KeyEventArgs e)
    {
        // Escape is handled by CancelButton (_closeButton).
        if (e.KeyCode == Keys.F3)
        {
            if (e.Shift) _previous(); else _next();
            e.Handled = true;
            e.SuppressKeyPress = true;
        }
        else if (e.Control && e.KeyCode == Keys.F)
        {
            // Ctrl+F while the bar already has focus: return to the query box.
            SelectQuery();
            e.Handled = true;
            e.SuppressKeyPress = true;
        }
        else if (e.Control && (e.KeyCode == Keys.Oemplus || e.KeyCode == Keys.Add))
        {
            if (Owner is Form1 form) form.InvokeZoomShortcut(1);
            e.Handled = true;
            e.SuppressKeyPress = true;
        }
        else if (e.Control && (e.KeyCode == Keys.OemMinus || e.KeyCode == Keys.Subtract))
        {
            if (Owner is Form1 form) form.InvokeZoomShortcut(-1);
            e.Handled = true;
            e.SuppressKeyPress = true;
        }
        else if (e.Control && (e.KeyCode == Keys.D0 || e.KeyCode == Keys.NumPad0))
        {
            if (Owner is Form1 form) form.InvokeZoomReset();
            e.Handled = true;
            e.SuppressKeyPress = true;
        }
    }

    private void LayoutTopRow(Control topRow)
    {
        if (topRow.ClientSize.Width <= 0 || topRow.ClientSize.Height <= 0) return;

        int gap = S(6);
        int right = topRow.ClientSize.Width - S(2);

        _findLabel.Left = 0;
        _findLabel.Top = Math.Max(0, (topRow.ClientSize.Height - _findLabel.Height) / 2);

        int buttonTop = Math.Max(0, (topRow.ClientSize.Height - _previousButton.Height) / 2);
        _previousButton.Top = buttonTop;
        _nextButton.Top = buttonTop;
        _closeButton.Top = buttonTop;

        _closeButton.Left = right - _closeButton.Width;
        _nextButton.Left = _closeButton.Left - gap - _nextButton.Width;
        _previousButton.Left = _nextButton.Left - gap - _previousButton.Width;

        int queryLeft = S(60);
        int queryRight = _previousButton.Left - gap;
        _queryBox.Left = queryLeft;
        _queryBox.Top = Math.Max(0, (topRow.ClientSize.Height - _queryBox.Height) / 2);
        _queryBox.Width = Math.Max(S(180), queryRight - queryLeft);
    }

    private void LayoutBottomRow(Control bottomRow)
    {
        if (bottomRow.ClientSize.Width <= 0) return;

        _caseBox.Left = 0;
        _caseBox.Top = Math.Max(0, (bottomRow.ClientSize.Height - _caseBox.Height) / 2);

        // Position relative to the checkbox's actual rendered width instead
        // of a hardcoded offset that breaks under other fonts/DPI/locales.
        int desired = _caseBox.Right + S(12);
        int maxLeft = Math.Max(_caseBox.Right, bottomRow.ClientSize.Width - _countLabel.Width);
        _countLabel.Left = Math.Min(desired, maxLeft);
        _countLabel.Top = Math.Max(0, (bottomRow.ClientSize.Height - _countLabel.Height) / 2);
    }

    public string Query => _queryBox.Text;
    public bool CaseSensitive => _caseBox.Checked;

    // True when the text box contents differ from the last search applied to
    // the canvas, or a debounce is still running.
    private bool HasPendingSearch =>
        _debounceTimer.Enabled ||
        !string.Equals(_queryBox.Text, _appliedQuery, StringComparison.Ordinal) ||
        _caseBox.Checked != _appliedCase;

    // Applies the current query immediately if edits are pending; no-op
    // otherwise (never resets the current match on its own).
    public void CommitPendingSearch()
    {
        if (IsDisposed) return;
        _debounceTimer.Stop();
        if (!HasPendingSearch) return;
        ApplySearchNow();
    }

    public void SelectQuery()
    {
        if (IsDisposed) return;
        if (IsHandleCreated)
        {
            BeginInvokeSafe(() =>
            {
                if (IsDisposed) return;
                _queryBox.Focus();
                _queryBox.SelectAll();
            });
        }
        else
        {
            // No handle yet — do it as soon as the dialog is shown.
            _selectOnShown = true;
        }
    }

    public void UpdateStatus(int count, int current)
    {
        if (IsDisposed) return;
        if (InvokeRequired)
        {
            BeginInvokeSafe(() => UpdateStatus(count, current));
            return;
        }

        if (count <= 0)
        {
            if (_queryBox.TextLength == 0) _countLabel.Text = "Type to find";
            else if (HasPendingSearch) _countLabel.Text = "Searching…";
            else _countLabel.Text = "No matches";
        }
        else
        {
            // FindCurrentIndex is 1-based; 0 means "no selection".
            int safeCurrent = Math.Clamp(current, 1, count);
            _countLabel.Text = $"{safeCurrent} of {count}";
        }

        // Keep navigation usable whenever there is a query, including the
        // debounce window: clicking Next/Previous commits the pending search
        // first (see Form1.EnsureFindSearchActive) instead of being dead.
        bool hasQuery = _queryBox.TextLength > 0;
        _previousButton.Enabled = hasQuery;
        _nextButton.Enabled = hasQuery;
    }

    private void ApplySearchNow()
    {
        if (IsDisposed) return;
        _debounceTimer.Stop();

        string query = _queryBox.Text;
        bool caseSensitive = _caseBox.Checked;
        _appliedQuery = query;
        _appliedCase = caseSensitive;

        if (query.Length == 0)
            _searchCleared(caseSensitive);
        else
            _searchChanged(query, caseSensitive);
    }

    private void BeginInvokeSafe(Action action)
    {
        try
        {
            if (!IsDisposed && IsHandleCreated)
                BeginInvoke(action);
        }
        catch (InvalidOperationException)
        {
            // The handle went away between the check and the call.
        }
    }

    // Scale a 96-DPI design pixel value to device pixels.
    private int S(int logicalPixels) => (int)Math.Round(logicalPixels * _dpiScale);

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _debounceTimer.Dispose();
        }
        base.Dispose(disposing);
        if (disposing)
        {
            // After base.Dispose the controls that referenced the font are gone.
            _dialogFont.Dispose();
        }
    }
}

public partial class Form1
{
    internal void InvokeZoomShortcut(int direction) => ChangeZoom(direction);
    internal void InvokeZoomReset() => ResetZoom();
}