namespace Retro96;

using System;
using System.Drawing;
using System.Windows.Forms;

public partial class Form1
{
    private readonly ToolStripButton _btnFind = new("Find");
    private readonly ToolStripButton _btnZoomOut = new("−");
    private readonly ToolStripButton _btnZoomReset = new("100%");
    private readonly ToolStripButton _btnZoomIn = new("+");
    private FindInPageDialog? _findDialog;
    private string _lastFindQuery = string.Empty;
    private bool _lastFindCaseSensitive;

    private void InitializeFindZoomUi()
    {
        _canvas.FindChanged += OnCanvasFindChanged;
        _canvas.ZoomChanged += OnCanvasZoomChanged;
        _canvas.PageChanged += OnCanvasPageChanged;

        _btnFind.DisplayStyle = ToolStripItemDisplayStyle.Text;
        _btnFind.ToolTipText = "Find in page (Ctrl+F)";
        _btnZoomOut.DisplayStyle = ToolStripItemDisplayStyle.Text;
        _btnZoomOut.ToolTipText = "Zoom out (Ctrl+Minus)";
        _btnZoomReset.DisplayStyle = ToolStripItemDisplayStyle.Text;
        _btnZoomReset.AutoSize = false;
        _btnZoomReset.Width = 48;
        _btnZoomReset.ToolTipText = "Reset zoom (Ctrl+0)";
        _btnZoomIn.DisplayStyle = ToolStripItemDisplayStyle.Text;
        _btnZoomIn.ToolTipText = "Zoom in (Ctrl+Plus)";

        _btnFind.Click += (_, _) => ShowFindDialog();
        _btnZoomOut.Click += (_, _) => ChangeZoom(-1);
        _btnZoomReset.Click += (_, _) => _canvas.ZoomFactor = 1f;
        _btnZoomIn.Click += (_, _) => ChangeZoom(1);

        int printIndex = _toolbar.Items.IndexOf(_btnPrint);
        int insertAt = printIndex >= 0 ? printIndex : _toolbar.Items.Count;
        _toolbar.Items.Insert(insertAt++, _btnFind);
        _toolbar.Items.Insert(insertAt++, new ToolStripSeparator());
        _toolbar.Items.Insert(insertAt++, _btnZoomOut);
        _toolbar.Items.Insert(insertAt++, _btnZoomReset);
        _toolbar.Items.Insert(insertAt, _btnZoomIn);

        _viewMenu.DropDownItems.Add(new ToolStripSeparator());
        _viewMenu.DropDownItems.Add("Find in Page\tCtrl+F").Click += (_, _) => ShowFindDialog();
        _viewMenu.DropDownItems.Add("Find Next\tF3").Click += (_, _) => FindNextFromShortcut();
        _viewMenu.DropDownItems.Add("Find Previous\tShift+F3").Click += (_, _) => FindPreviousFromShortcut();
        _viewMenu.DropDownItems.Add(new ToolStripSeparator());
        _viewMenu.DropDownItems.Add("Zoom In\tCtrl+Plus").Click += (_, _) => ChangeZoom(1);
        _viewMenu.DropDownItems.Add("Zoom Out\tCtrl+Minus").Click += (_, _) => ChangeZoom(-1);
        _viewMenu.DropDownItems.Add("Reset Zoom\tCtrl+0").Click += (_, _) => _canvas.ZoomFactor = 1f;

        UpdateZoomUi(_canvas.VisualZoomFactor);
        OnCanvasFindChanged(0, 0);
        FormClosed += (_, _) =>
        {
            try { _findDialog?.Close(); } catch { }
            _findDialog = null;
        };
    }

    private void OnCanvasZoomChanged(float zoom)
    {
        if (InvokeRequired) { BeginInvokeSafe(() => UpdateZoomUi(zoom)); return; }
        UpdateZoomUi(zoom);
    }

    private void UpdateZoomUi(float zoom)
    {
        int percent = Math.Clamp((int)Math.Round(zoom * 100f, MidpointRounding.AwayFromZero), 25, 400);
        _btnZoomReset.Text = $"{percent}%";
        _btnZoomOut.Enabled = zoom > 0.2501f;
        _btnZoomIn.Enabled = zoom < 3.9999f;
        _btnZoomReset.Enabled = Math.Abs(zoom - 1f) > 0.0005f;
    }

    private void ChangeZoom(int direction)
    {
        // Route toolbar/menu zoom through the canvas so button zoom and
        // Ctrl+wheel gesture zoom share one state machine.  When a gesture
        // zoom is active this commits the current visual zoom into the layout
        // baseline instead of accidentally stepping from the old layout zoom.
        if (direction == 0) return;
        _canvas.ChangeZoomStep(direction);
    }

    private void ShowFindDialog()
    {
        if (IsDisposed) return;

        if (_findDialog == null || _findDialog.IsDisposed)
        {
            var dialog = new FindInPageDialog(
                _lastFindQuery,
                _lastFindCaseSensitive,
                (query, caseSensitive) =>
                {
                    _lastFindQuery = query ?? string.Empty;
                    _lastFindCaseSensitive = caseSensitive;
                    _canvas.FindInPage(_lastFindQuery, _lastFindCaseSensitive, wrapAround: true);
                },
                () => EnsureFindSearchActive(previous: true),
                () => EnsureFindSearchActive(previous: false));
            _findDialog = dialog;
            dialog.FormClosed += (_, _) =>
            {
                _lastFindQuery = dialog.Query;
                _lastFindCaseSensitive = dialog.CaseSensitive;
                _canvas.FindClear();
                if (ReferenceEquals(_findDialog, dialog)) _findDialog = null;
            };
            dialog.Show(this);
        }
        else
        {
            _findDialog.BringToFront();
            _findDialog.Activate();
        }

        if (!string.IsNullOrEmpty(_lastFindQuery))
            _canvas.FindInPage(_lastFindQuery, _lastFindCaseSensitive, wrapAround: true);
        _findDialog?.SelectQuery();
        OnCanvasFindChanged(GetFindCount(), GetFindCurrent());
    }

    private void EnsureFindSearchActive(bool previous)
    {
        if (string.IsNullOrEmpty(_lastFindQuery))
        {
            ShowFindDialog();
            return;
        }

        // The dialog updates this query on every search change. Rehydrate the
        // canvas only when navigation cleared its page-local match state. The
        // fresh FindInPage call already selects the first result, so do not
        // advance a second time for the ordinary F3 case.
        bool rehydrated = false;
        if (GetFindCount() == 0)
        {
            _canvas.FindInPage(_lastFindQuery, _lastFindCaseSensitive, wrapAround: true);
            rehydrated = GetFindCount() > 0;
        }

        if (!rehydrated)
        {
            if (previous) _canvas.FindPrevious();
            else _canvas.FindNext();
        }
        else if (previous)
        {
            // Shift+F3 from a cold search means "previous", so move from the
            // initial first result to the wrapped last result.
            _canvas.FindPrevious();
        }
    }

    private int GetFindCount() => _canvas.FindMatchCount;
    private int GetFindCurrent() => _canvas.FindCurrentIndex;

    private void FindNextFromShortcut() => EnsureFindSearchActive(previous: false);
    private void FindPreviousFromShortcut() => EnsureFindSearchActive(previous: true);

    private void OnCanvasFindChanged(int count, int current)
    {
        if (InvokeRequired) { BeginInvokeSafe(() => OnCanvasFindChanged(count, current)); return; }
        _findDialog?.UpdateStatus(count, current);
    }

    private void OnCanvasPageChanged()
    {
        if (_findDialog == null || _findDialog.IsDisposed || string.IsNullOrEmpty(_lastFindQuery))
            return;

        // SetPage clears old match boxes before raising PageChanged. Re-run the
        // active query against the new layout so a persistent find bar never
        // displays stale match counts from the previous document.
        BeginInvokeSafe(() =>
        {
            if (_findDialog == null || _findDialog.IsDisposed || string.IsNullOrEmpty(_lastFindQuery)) return;
            _canvas.FindInPage(_lastFindQuery, _lastFindCaseSensitive, wrapAround: true);
        });
    }
}

internal sealed class FindInPageDialog : Form
{
    private readonly TextBox _queryBox = new();
    private readonly CheckBox _caseBox = new() { Text = "Match case", AutoSize = true };
    private readonly Button _previousButton = new() { Text = "Previous", AutoSize = true };
    private readonly Button _nextButton = new() { Text = "Next", AutoSize = true };
    private readonly Button _closeButton = new() { Text = "Close", AutoSize = true };
    private readonly Label _countLabel = new() { AutoSize = true, Text = "No matches" };
    private readonly TableLayoutPanel _layout = new();
    private readonly Timer _debounceTimer = new() { Interval = 120 };
    private readonly Action<string, bool> _searchChanged;
    private readonly Action _previous;
    private readonly Action _next;

    public FindInPageDialog(string initialQuery, bool initialCaseSensitive,
                            Action<string, bool> searchChanged, Action previous, Action next)
    {
        _searchChanged = searchChanged;
        _previous = previous;
        _next = next;

        Text = "Find in Page — Retro96";
        FormBorderStyle = FormBorderStyle.FixedToolWindow;
        ShowInTaskbar = false;
        StartPosition = FormStartPosition.CenterParent;
        ClientSize = new Size(900, 124);
        MinimumSize = new Size(820, 124);
        MinimizeBox = false;
        MaximizeBox = false;
        KeyPreview = true;
        // The dialog is hand-sized. WinForms DPI autoscaling was compressing
        // the fixed controls and making the text look horizontally/vertically
        // squashed. Keep one predictable coordinate system instead.
        AutoScaleMode = AutoScaleMode.None;
        Font = new Font("Segoe UI", 9f, FontStyle.Regular, GraphicsUnit.Point);

        _layout.Dock = DockStyle.Fill;
        _layout.Padding = new Padding(10);
        _layout.ColumnCount = 1;
        _layout.RowCount = 2;
        _layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 34f));
        _layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100f));

        var topRow = new Panel
        {
            Dock = DockStyle.Fill,
            Margin = Padding.Empty
        };

        var label = new Label
        {
            Text = "Find:",
            AutoSize = false,
            TextAlign = ContentAlignment.MiddleLeft,
            Location = new Point(0, 1),
            Size = new Size(56, 26),
            Margin = Padding.Empty
        };

        _queryBox.BorderStyle = BorderStyle.FixedSingle;
        _queryBox.TextAlign = HorizontalAlignment.Left;
        _queryBox.Location = new Point(60, 1);
        _queryBox.Size = new Size(500, 24);
        _queryBox.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right;
        _queryBox.Margin = Padding.Empty;
        _queryBox.Text = initialQuery ?? string.Empty;

        _previousButton.AutoSize = false;
        _previousButton.TextAlign = ContentAlignment.MiddleCenter;
        _previousButton.Size = new Size(100, 26);
        _previousButton.Location = new Point(576, 0);
        _previousButton.Anchor = AnchorStyles.Top | AnchorStyles.Right;
        _previousButton.Margin = Padding.Empty;

        _nextButton.AutoSize = false;
        _nextButton.TextAlign = ContentAlignment.MiddleCenter;
        _nextButton.Size = new Size(100, 26);
        _nextButton.Location = new Point(682, 0);
        _nextButton.Anchor = AnchorStyles.Top | AnchorStyles.Right;
        _nextButton.Margin = Padding.Empty;

        _closeButton.AutoSize = false;
        _closeButton.TextAlign = ContentAlignment.MiddleCenter;
        _closeButton.Size = new Size(86, 26);
        _closeButton.Location = new Point(788, 0);
        _closeButton.Anchor = AnchorStyles.Top | AnchorStyles.Right;
        _closeButton.Margin = Padding.Empty;

        topRow.Controls.Add(label);
        topRow.Controls.Add(_queryBox);
        topRow.Controls.Add(_previousButton);
        topRow.Controls.Add(_nextButton);
        topRow.Controls.Add(_closeButton);

        var bottomRow = new Panel
        {
            Dock = DockStyle.Fill,
            Margin = Padding.Empty
        };

        _caseBox.Checked = initialCaseSensitive;
        _caseBox.AutoSize = true;
        _caseBox.Location = new Point(0, 3);
        _caseBox.Margin = Padding.Empty;

        _countLabel.AutoSize = true;
        _countLabel.Location = new Point(118, 5);
        _countLabel.Margin = Padding.Empty;

        bottomRow.Controls.Add(_caseBox);
        bottomRow.Controls.Add(_countLabel);

        Controls.Add(_layout);
        _layout.Controls.Add(topRow, 0, 0);
        _layout.Controls.Add(bottomRow, 0, 1);
        AcceptButton = _nextButton;
        CancelButton = _closeButton;

        topRow.Resize += (_, _) => LayoutTopRow(topRow);
        Shown += (_, _) => LayoutTopRow(topRow);

        _queryBox.TextChanged += (_, _) =>
        {
            _debounceTimer.Stop();
            // Clear the visual find state immediately. The user should never
            // see a previous query highlighted while a new query is pending.
            _searchChanged(string.Empty, CaseSensitive);
            if (!string.IsNullOrEmpty(_queryBox.Text))
                _debounceTimer.Start();
        };
        _caseBox.CheckedChanged += (_, _) =>
        {
            ApplySearchNow();
        };
        _previousButton.Click += (_, _) => _previous();
        _nextButton.Click += (_, _) => _next();
        _closeButton.Click += (_, _) => Close();
        _debounceTimer.Tick += (_, _) =>
        {
            _debounceTimer.Stop();
            ApplySearchNow();
        };

        KeyDown += (_, e) =>
        {
            if (e.KeyCode == Keys.Escape)
            {
                Close();
                e.Handled = true;
                e.SuppressKeyPress = true;
            }
            else if (e.KeyCode == Keys.F3)
            {
                if (e.Shift) _previous(); else _next();
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
            else if (e.Control && e.KeyCode == Keys.D0)
            {
                if (Owner is Form1 form) form.InvokeZoomReset();
                e.Handled = true;
                e.SuppressKeyPress = true;
            }
        };

        FormClosed += (_, _) =>
        {
            try { _debounceTimer.Stop(); } catch { }
            try { _searchChanged(string.Empty, CaseSensitive); } catch { }
            _debounceTimer.Dispose();
        };
    }

    private void LayoutTopRow(Control topRow)
    {
        if (topRow.ClientSize.Width <= 0) return;
        const int rightMargin = 2;
        const int gap = 6;
        int right = topRow.ClientSize.Width - rightMargin;

        _closeButton.Left = right - _closeButton.Width;
        _nextButton.Left = _closeButton.Left - gap - _nextButton.Width;
        _previousButton.Left = _nextButton.Left - gap - _previousButton.Width;

        int queryLeft = _queryBox.Left;
        int queryRight = _previousButton.Left - gap;
        _queryBox.Width = Math.Max(180, queryRight - queryLeft);
    }

    public string Query => _queryBox.Text;
    public bool CaseSensitive => _caseBox.Checked;

    public void SelectQuery()
    {
        if (IsDisposed) return;
        BeginInvoke(new Action(() =>
        {
            if (IsDisposed) return;
            _queryBox.Focus();
            _queryBox.SelectAll();
        }));
    }

    public void UpdateStatus(int count, int current)
    {
        if (IsDisposed) return;
        if (count <= 0)
        {
            _countLabel.Text = Query.Length == 0 ? "Type to find" : "No matches";
        }
        else
        {
            int safeCurrent = Math.Clamp(current, 1, count);
            _countLabel.Text = $"{safeCurrent} of {count}";
        }
        _previousButton.Enabled = count > 0;
        _nextButton.Enabled = count > 0;
    }

    private void ApplySearchNow()
    {
        if (IsDisposed) return;
        _searchChanged(Query, CaseSensitive);
    }
}

public partial class Form1
{
    internal void InvokeZoomShortcut(int direction) => ChangeZoom(direction);
    internal void InvokeZoomReset() => _canvas.ZoomFactor = 1f;
}
