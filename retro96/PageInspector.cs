using System;
using System.Drawing;
using System.Linq;
using System.Text;
using System.Collections.Generic;
using System.Windows.Forms;
using EngColor = Retro96.Drawing.Color;
using EngColorTranslator = Retro96.Drawing.ColorTranslator;
using EngRectangleF = Retro96.Drawing.RectangleF;
using Retro96.Engine.Css;
using Retro96.Engine.Dom;
using Retro96.Engine.Layout;

namespace Retro96;

/// <summary>Interactive DOM, style and layout inspector for the current page.</summary>
public class PageInspector : Form
{
    private sealed record ConsoleLine(string Level, string Message, DateTime Timestamp);
    private static readonly List<ConsoleLine> ConsoleLines = new();
    private static readonly object ConsoleLock = new();
    private static event Action? ConsoleUpdated;

    private readonly BrowserCanvas _canvas;
    private readonly TreeView _tree = new();
    private readonly ListView _attrs = new();
    private readonly TextBox _computed = new();
    private readonly TextBox _boxInfo = new();
    private readonly TextBox _source = new();
    private readonly ListView _console = new();
    private readonly TextBox _consoleFilter = new();
    private readonly TextBox _sources = new();
    private readonly ListView _network = new();
    private readonly ToolStripTextBox _search = new();
    private readonly ToolStripLabel _selectionLabel = new();
    private readonly Label _summary = new();
    private readonly Label _crumbs = new();
    private readonly CheckBox _showBoxes = new();
    private DomElement? _current;
    private int _elementCount;
    private bool _sourceDirty;
    private bool _loadingSource;
    private bool _sourceLoaded;
    private DomDocument? _sourceDocument;
    private DomDocument? _inspectedDocument;

    public static void PublishConsole(string level, string message, DateTime timestamp)
    {
        lock (ConsoleLock)
        {
            ConsoleLines.Add(new ConsoleLine(level, message, timestamp));
            if (ConsoleLines.Count > 500) ConsoleLines.RemoveAt(0);
        }
        ConsoleUpdated?.Invoke();
    }

    public PageInspector(BrowserCanvas canvas, DomElement? initialElement)
    {
        _canvas = canvas ?? throw new ArgumentNullException(nameof(canvas));

        Text = "Retro96 Inspector";
        StartPosition = FormStartPosition.CenterParent;
        ClientSize = new Size(980, 650);
        MinimumSize = new Size(620, 400);
        ShowInTaskbar = false;
        Font = new Font("Segoe UI", 9f);
        BackColor = SystemColors.Control;
        ForeColor = Color.FromArgb(35, 42, 52);

        var commands = new ToolStrip { GripStyle = ToolStripGripStyle.Hidden, Padding = new Padding(5, 3, 5, 3) };
        var refresh = new ToolStripButton("Refresh");
        var expand = new ToolStripButton("Expand all");
        var collapse = new ToolStripButton("Collapse all");
        var parent = new ToolStripButton("Parent");
        var copy = new ToolStripButton("Copy details");
        var clearConsole = new ToolStripButton("Clear console");
        var clearNetwork = new ToolStripButton("Clear network");
        var applySource = new ToolStripButton("Apply source");
        _showBoxes.Text = "Outline boxes";
        _showBoxes.AutoSize = true;
        _showBoxes.Checked = canvas.ShowBoxOutlines;
        _search.AutoSize = false;
        _search.Width = 190;
        _search.ToolTipText = "Find a tag, id, class, or text";
        _selectionLabel.Alignment = ToolStripItemAlignment.Right;
        _selectionLabel.ForeColor = SystemColors.GrayText;

        commands.Items.Add(refresh);
        commands.Items.Add(new ToolStripSeparator());
        commands.Items.Add(expand);
        commands.Items.Add(collapse);
        commands.Items.Add(parent);
        commands.Items.Add(copy);
        commands.Items.Add(clearConsole);
        commands.Items.Add(clearNetwork);
        commands.Items.Add(applySource);
        commands.Items.Add(new ToolStripSeparator());
        commands.Items.Add(new ToolStripLabel("Find:"));
        commands.Items.Add(_search);
        commands.Items.Add(new ToolStripControlHost(_showBoxes));
        commands.Items.Add(_selectionLabel);

        refresh.Click += (s, e) => RefreshInspector();
        expand.Click += (s, e) => _tree.ExpandAll();
        collapse.Click += (s, e) => _tree.CollapseAll();
        parent.Click += (s, e) => SelectElement(_current?.Parent as DomElement);
        copy.Click += (s, e) => CopyDetails();
        clearConsole.Click += (s, e) =>
        {
            lock (ConsoleLock) ConsoleLines.Clear();
            RefreshToolTab(1);
        };
        clearNetwork.Click += (s, e) =>
        {
            _canvas.ResourceLoader?.ClearHistory();
            RefreshToolTab(3);
        };
        applySource.Click += (s, e) => ApplySource();
        _showBoxes.CheckedChanged += (s, e) => _canvas.SetBoxOutlines(_showBoxes.Checked);
        _search.TextChanged += (s, e) => FindInTree(_search.Text);

        _tree.Dock = DockStyle.Fill;
        _tree.HideSelection = false;
        _tree.ShowLines = true;
        _tree.ShowNodeToolTips = true;
        _tree.BackColor = Color.White;
        _tree.AfterSelect += (s, e) => ShowElement(e.Node?.Tag as DomElement);
        _tree.NodeMouseDoubleClick += (s, e) => e.Node?.Toggle();
        _tree.NodeMouseClick += (s, e) =>
        {
            if (e.Button == MouseButtons.Right) _tree.SelectedNode = e.Node;
        };
        _tree.ContextMenuStrip = BuildTreeMenu();

        _attrs.View = View.Details;
        _attrs.Dock = DockStyle.Fill;
        _attrs.FullRowSelect = true;
        _attrs.GridLines = true;
        _attrs.HeaderStyle = ColumnHeaderStyle.Nonclickable;
        _attrs.Columns.Add("Name", 170);
        _attrs.Columns.Add("Value", 520);

        ConfigureTextBox(_computed);
        ConfigureTextBox(_boxInfo);
        ConfigureTextBox(_source);
        ConfigureConsoleList();
        ConfigureSourceEditor();
        _sources.BackColor = Color.White;
        _sources.ForeColor = Color.FromArgb(30, 35, 42);
        _sources.AcceptsTab = true;
        _sources.TextChanged += (s, e) =>
        {
            if (!_loadingSource) _sourceDirty = true;
        };
        _sources.MouseDown += (s, e) =>
        {
            if (!_sources.Focused) _sources.Focus();
        };
        ConfigureNetworkList();
        _sources.ContextMenuStrip = BuildTextMenu(_sources);
        _network.ContextMenuStrip = BuildNetworkMenu();

        _summary.AutoSize = false;
        _summary.Dock = DockStyle.Top;
        _summary.Height = 72;
        _summary.Padding = new Padding(12, 7, 12, 5);
        _summary.Font = new Font("Segoe UI", 9f, FontStyle.Regular);
        _summary.BackColor = Color.FromArgb(245, 247, 250);

        _crumbs.Dock = DockStyle.Bottom;
        _crumbs.Height = 22;
        _crumbs.Padding = new Padding(12, 3, 12, 0);
        _crumbs.ForeColor = Color.FromArgb(80, 90, 105);
        _crumbs.BackColor = Color.FromArgb(235, 238, 243);

        var split = new SplitContainer
        {
            Dock = DockStyle.Fill,
            FixedPanel = FixedPanel.Panel1
        };
        split.Panel1.Controls.Add(_tree);
        split.Panel2.Controls.Add(BuildElementDetails());

        var tabs = new TabControl { Dock = DockStyle.Fill };
        tabs.TabPages.Add(MakeTab("Elements", split));
        tabs.TabPages.Add(MakeTab("Console", BuildConsoleView()));
        tabs.TabPages.Add(MakeTab("Sources", BuildSourceView()));
        tabs.TabPages.Add(MakeTab("Network", _network));
        tabs.SelectedIndexChanged += (s, e) =>
        {
            RefreshToolTab(tabs.SelectedIndex);
        };

        Controls.Add(tabs);
        Controls.Add(_crumbs);
        Controls.Add(_summary);
        Controls.Add(commands);

        Load += (s, e) =>
        {
            split.Panel1MinSize = 220;
            split.Panel2MinSize = 300;
            int minimumDistance = split.Panel1MinSize;
            int maximumDistance = Math.Max(minimumDistance, split.ClientSize.Width - split.Panel2MinSize);
            split.SplitterDistance = Math.Min(350, maximumDistance);
        };

        _canvas.PageChanged += OnPageChanged;
        ConsoleUpdated += OnConsoleUpdated;
        FormClosed += (s, e) =>
        {
            _canvas.PageChanged -= OnPageChanged;
            ConsoleUpdated -= OnConsoleUpdated;
            if (_showBoxes.Checked) _canvas.SetBoxOutlines(false);
        };

        BuildTree();
        RefreshToolTab(0);
        if (initialElement != null) SelectElement(initialElement);
    }

    private Control BuildElementDetails()
    {
        var details = new TabControl { Dock = DockStyle.Fill };
        details.TabPages.Add(MakeTab("Attributes", _attrs));
        details.TabPages.Add(MakeTab("Computed style", _computed));
        details.TabPages.Add(MakeTab("Box model", _boxInfo));
        details.TabPages.Add(MakeTab("DOM text", _source));
        return details;
    }

    private Control BuildSourceView()
    {
        var panel = new Panel { Dock = DockStyle.Fill, Padding = new Padding(0) };
        var bar = new Panel { Dock = DockStyle.Top, Height = 30, BackColor = Color.FromArgb(238, 241, 245) };
        var hint = new Label
        {
            Dock = DockStyle.Fill,
            Text = "Editable page source",
            TextAlign = ContentAlignment.MiddleLeft,
            Padding = new Padding(8, 0, 0, 0),
            ForeColor = Color.FromArgb(75, 84, 96)
        };
        bar.Controls.Add(hint);
        panel.Controls.Add(_sources);
        panel.Controls.Add(bar);
        return panel;
    }

    private static void ConfigureTextBox(TextBox box)
    {
        box.Dock = DockStyle.Fill;
        box.Multiline = true;
        box.ReadOnly = true;
        box.ScrollBars = ScrollBars.Both;
        box.WordWrap = false;
        box.BackColor = Color.White;
        box.Font = new Font("Consolas", 9f);
    }

    private void ConfigureSourceEditor()
    {
        _sources.Dock = DockStyle.Fill;
        _sources.Multiline = true;
        _sources.ReadOnly = false;
        _sources.Enabled = true;
        _sources.TabStop = true;
        _sources.AcceptsTab = true;
        _sources.ShortcutsEnabled = true;
        _sources.ScrollBars = ScrollBars.Both;
        _sources.WordWrap = false;
        _sources.HideSelection = false;
        _sources.TabIndex = 0;
        _sources.CausesValidation = false;
        _sources.BorderStyle = BorderStyle.Fixed3D;
        _sources.Font = new Font("Consolas", 9f);
    }

    private void ConfigureConsoleList()
    {
        _console.Dock = DockStyle.Fill;
        _console.View = View.Details;
        _console.FullRowSelect = true;
        _console.GridLines = false;
        _console.HideSelection = false;
        _console.BackColor = Color.FromArgb(24, 29, 36);
        _console.ForeColor = Color.FromArgb(225, 231, 239);
        _console.Font = new Font("Consolas", 9f);
        _console.Columns.Add("Time", 78);
        _console.Columns.Add("Level", 72);
        _console.Columns.Add("Message", 680);
    }

    private Control BuildConsoleView()
    {
        var panel = new Panel { Dock = DockStyle.Fill, BackColor = Color.FromArgb(24, 29, 36), Padding = new Padding(0) };
        _consoleFilter.Dock = DockStyle.Top;
        _consoleFilter.Height = 30;
        _consoleFilter.BorderStyle = BorderStyle.FixedSingle;
        _consoleFilter.BackColor = Color.FromArgb(38, 45, 55);
        _consoleFilter.ForeColor = Color.White;
        _consoleFilter.Font = new Font("Segoe UI", 9f);
        _consoleFilter.PlaceholderText = "Filter console messages...";
        _consoleFilter.TextChanged += (s, e) => RefreshConsole();
        panel.Controls.Add(_console);
        panel.Controls.Add(_consoleFilter);
        _console.ContextMenuStrip = BuildConsoleMenu();
        return panel;
    }

    private static TabPage MakeTab(string title, Control content)
    {
        var page = new TabPage(title) { Padding = new Padding(5) };
        page.Controls.Add(content);
        return page;
    }

    private void ConfigureNetworkList()
    {
        _network.View = View.Details;
        _network.Dock = DockStyle.Fill;
        _network.FullRowSelect = true;
        _network.GridLines = true;
        _network.Columns.Add("URL", 420);
        _network.Columns.Add("Status", 180);
        _network.Columns.Add("Time", 90);
        _network.Columns.Add("Duration", 90);
    }

    private ContextMenuStrip BuildConsoleMenu()
    {
        var menu = new ContextMenuStrip();
        menu.Items.Add("Copy message", null, (s, e) =>
        {
            if (_console.SelectedItems.Count > 0)
                Clipboard.SetText(string.Join(" ", _console.SelectedItems[0].SubItems.Cast<ListViewItem.ListViewSubItem>().Select(x => x.Text)));
        });
        menu.Items.Add("Clear console", null, (s, e) =>
        {
            lock (ConsoleLock) ConsoleLines.Clear();
            RefreshConsole();
        });
        return menu;
    }

    private ContextMenuStrip BuildTextMenu(TextBoxBase textBox)
    {
        var menu = new ContextMenuStrip();
        if (!textBox.ReadOnly)
        {
            menu.Items.Add("Undo", null, (s, e) => { if (textBox.CanUndo) textBox.Undo(); });
            menu.Items.Add("Cut", null, (s, e) => textBox.Cut());
            menu.Items.Add(new ToolStripSeparator());
        }
        menu.Items.Add("Copy", null, (s, e) => textBox.Copy());
        if (!textBox.ReadOnly) menu.Items.Add("Paste", null, (s, e) => textBox.Paste());
        menu.Items.Add("Select all", null, (s, e) => textBox.SelectAll());
        return menu;
    }

    private ContextMenuStrip BuildTreeMenu()
    {
        var menu = new ContextMenuStrip();
        menu.Opening += (s, e) => e.Cancel = _tree.SelectedNode?.Tag is not DomElement;
        menu.Items.Add("Inspect element", null, (s, e) => ShowElement(_tree.SelectedNode?.Tag as DomElement));
        menu.Items.Add("Copy selector", null, (s, e) =>
        {
            if (_tree.SelectedNode?.Tag is DomElement element)
                Clipboard.SetText(CssPath(element));
        });
        menu.Items.Add("Copy element details", null, (s, e) => CopyDetails());
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Expand subtree", null, (s, e) => _tree.SelectedNode?.ExpandAll());
        menu.Items.Add("Collapse subtree", null, (s, e) => _tree.SelectedNode?.Collapse(false));
        return menu;
    }

    private ContextMenuStrip BuildNetworkMenu()
    {
        var menu = new ContextMenuStrip();
        menu.Items.Add("Copy URL", null, (s, e) =>
        {
            if (_network.SelectedItems.Count > 0) Clipboard.SetText(_network.SelectedItems[0].SubItems[0].Text);
        });
        menu.Items.Add("Clear network", null, (s, e) =>
        {
            _canvas.ResourceLoader?.ClearHistory();
            RefreshToolTab(3);
        });
        return menu;
    }

    private void RefreshToolTab(int tabIndex)
    {
        if (tabIndex == 1)
        {
            RefreshConsole();
        }
        else if (tabIndex == 2)
        {
            if (_sources.Focused || _sourceDirty) return;
            if (!_sourceDirty && !_sourceLoaded)
            {
                _loadingSource = true;
                _sources.Text = BuildPageSource();
                _loadingSource = false;
                _sourceLoaded = true;
                _sourceDocument = _canvas.PageDocument;
            }
        }
        else if (tabIndex == 3)
        {
            _network.BeginUpdate();
            _network.Items.Clear();
            foreach (var item in _canvas.ResourceLoader?.History ?? Array.Empty<Engine.Network.ResourceLoader.FetchRecord>())
            {
                double duration = (item.CompletedUtc - item.StartedUtc).TotalMilliseconds;
                _network.Items.Add(new ListViewItem(new[]
                {
                    item.Url,
                    item.StatusCode.HasValue ? $"{item.StatusCode} {item.Status}" : item.Status,
                    item.CompletedUtc.ToLocalTime().ToString("HH:mm:ss"),
                    $"{duration:0} ms"
                }));
            }
            _network.EndUpdate();
        }
    }

    private void OnConsoleUpdated()
    {
        if (IsDisposed) return;
        if (InvokeRequired)
        {
            try { BeginInvoke(OnConsoleUpdated); } catch (InvalidOperationException) { }
            return;
        }
        RefreshConsole();
    }

    private void RefreshConsole()
    {
        string filter = _consoleFilter.Text.Trim();
        ConsoleLine[] lines;
        lock (ConsoleLock) lines = ConsoleLines.ToArray();
        _console.BeginUpdate();
        _console.Items.Clear();
        foreach (var line in lines.Where(line => filter.Length == 0 ||
                     line.Message.Contains(filter, StringComparison.OrdinalIgnoreCase) ||
                     line.Level.Contains(filter, StringComparison.OrdinalIgnoreCase)))
        {
            var item = new ListViewItem(new[] { line.Timestamp.ToString("HH:mm:ss"), line.Level.ToUpperInvariant(), line.Message });
            item.ForeColor = line.Level.Equals("error", StringComparison.OrdinalIgnoreCase) ? Color.FromArgb(255, 125, 125) :
                             line.Level.Equals("warn", StringComparison.OrdinalIgnoreCase) ? Color.FromArgb(255, 205, 115) :
                             Color.FromArgb(225, 231, 239);
            _console.Items.Add(item);
        }
        _console.EndUpdate();
        if (_console.Items.Count > 0) _console.Items[^1].EnsureVisible();
    }

    private void ApplySource()
    {
        if (_canvas.ApplyEditedSource(_sources.Text))
        {
            _sourceDirty = false;
            _sourceLoaded = true;
            _summary.Text = "Source applied. The document was reparsed and relaid out.";
        }
        else
            MessageBox.Show(this, "The edited source could not be applied to the current document.",
                "Source editor", MessageBoxButtons.OK, MessageBoxIcon.Warning);
    }

    private string BuildPageSource()
    {
        var doc = _canvas.PageDocument;
        if (doc == null) return "No document loaded.";
        var sb = new StringBuilder();
        foreach (var node in doc.Children) AppendSource(sb, node, 0);
        return sb.ToString();
    }

    private static void AppendSource(StringBuilder sb, DomNode node, int depth)
    {
        string indent = new(' ', depth * 2);
        if (node is DomElement element)
        {
            sb.Append(indent).Append('<').Append(element.TagName);
            foreach (var attr in element.Attrs.OrderBy(a => a.Key))
                sb.Append(' ').Append(attr.Key).Append("=\"").Append(attr.Value.Replace("\"", "&quot;")).Append('"');
            if (element.Children.Count == 0 && new[] { "br", "img", "meta", "link", "input", "hr" }.Contains(element.TagName))
            {
                sb.AppendLine(">\r");
                return;
            }
            sb.AppendLine(">");
            foreach (var child in element.Children) AppendSource(sb, child, depth + 1);
            sb.Append(indent).Append("</").Append(element.TagName).AppendLine(">");
        }
        else if (node is DomText text)
        {
            string value = text.Data.Trim();
            if (value.Length > 0) sb.Append(indent).AppendLine(value);
        }
        else if (node is DomComment comment)
        {
            sb.Append(indent).Append("<!-- ").Append(comment.Text).AppendLine(" -->");
        }
    }

    private void OnPageChanged()
    {
        if (IsDisposed) return;
        if (InvokeRequired) { BeginInvoke(OnPageChanged); return; }
        bool documentChanged = !ReferenceEquals(_inspectedDocument, _canvas.PageDocument);
        bool editingSource = _sources.Focused || _sourceDirty;
        if (documentChanged && !editingSource)
        {
            _sourceDirty = false;
            _sourceLoaded = false;
            _sourceDocument = null;
            BuildTree();
            RefreshToolTab(1);
            RefreshToolTab(2);
            RefreshToolTab(3);
        }
        else if (_current != null)
        {
            ShowElement(_current);
        }
    }

    private void RefreshInspector()
    {
        _canvas.Invalidate();
        BuildTree();
        RefreshToolTab(1);
        RefreshToolTab(2);
        RefreshToolTab(3);
        if (_current != null) ShowElement(_current);
    }

    private void BuildTree()
    {
        _inspectedDocument = _canvas.PageDocument;
        _tree.BeginUpdate();
        _tree.Nodes.Clear();
        _elementCount = 0;
        var doc = _canvas.PageDocument;
        var html = doc?.ElementChildren().FirstOrDefault(e => e.TagName == "html")
                ?? doc?.ElementChildren().FirstOrDefault();
        if (html != null)
        {
            var root = MakeNode(html);
            _tree.Nodes.Add(root);
            AddChildren(html, root);
            root.Expand();
            foreach (TreeNode child in root.Nodes) child.Expand();
        }
        _tree.EndUpdate();
        _selectionLabel.Text = $"{_elementCount} elements";
        if (_current != null) SelectElement(_current);
    }

    private TreeNode MakeNode(DomElement element)
    {
        _elementCount++;
        return new TreeNode(LabelFor(element)) { Tag = element, ToolTipText = Describe(element) };
    }

    private void AddChildren(DomElement parent, TreeNode node)
    {
        foreach (var child in parent.ElementChildren())
        {
            var childNode = MakeNode(child);
            node.Nodes.Add(childNode);
            AddChildren(child, childNode);
        }
    }

    private static string LabelFor(DomElement element)
    {
        var label = "<" + element.TagName;
        var id = element.GetAttr("id");
        if (!string.IsNullOrWhiteSpace(id)) label += " #" + id;
        var cls = element.GetAttr("class");
        if (!string.IsNullOrWhiteSpace(cls)) label += " ." + cls.Replace(' ', '.');
        return label + ">";
    }

    private static string Describe(DomElement element)
    {
        var text = element.InnerText.Trim().Replace(Environment.NewLine, " ");
        return text.Length == 0 ? LabelFor(element) : LabelFor(element) + " - " + (text.Length > 80 ? text[..80] + "..." : text);
    }

    private static string CssPath(DomElement element)
    {
        var parts = new List<string>();
        for (DomNode? node = element; node is DomElement current; node = current.Parent)
        {
            string part = current.TagName;
            string? id = current.GetAttr("id");
            if (!string.IsNullOrWhiteSpace(id))
                part += "#" + id;
            else if (current.Parent is DomElement parent)
            {
                int index = parent.ElementChildren().TakeWhile(child => !ReferenceEquals(child, current)).Count() + 1;
                part += $":nth-child({index})";
            }
            parts.Add(part);
        }
        parts.Reverse();
        return string.Join(" > ", parts);
    }

    private void SelectElement(DomElement? element)
    {
        if (element == null) return;
        _current = element;
        var node = FindNode(_tree.Nodes, element);
        if (node != null)
        {
            _tree.SelectedNode = node;
            node.EnsureVisible();
        }
        ShowElement(element);
    }

    private static TreeNode? FindNode(TreeNodeCollection nodes, DomElement element)
    {
        foreach (TreeNode node in nodes)
        {
            if (ReferenceEquals(node.Tag, element)) return node;
            var found = FindNode(node.Nodes, element);
            if (found != null) return found;
        }
        return null;
    }

    private void FindInTree(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return;
        var match = _canvas.PageDocument?.ElementDescendants().FirstOrDefault(e =>
            LabelFor(e).Contains(text, StringComparison.OrdinalIgnoreCase) ||
            e.InnerText.Contains(text, StringComparison.OrdinalIgnoreCase));
        if (match != null) SelectElement(match);
    }

    private void ShowElement(DomElement? element)
    {
        if (element == null) return;
        _current = element;
        var box = FindBox(element);
        var style = element.Style;
        var text = element.InnerText.Trim();
        var childCount = element.ElementChildren().Count();
        _summary.Text = $"{LabelFor(element)}\r\n{childCount} child elements   |   {element.Attrs.Count} attributes   |   " +
                        $"{(box == null ? "not laid out" : $"{box.BoxType} box {box.BorderRect.Width:0.#} x {box.BorderRect.Height:0.#}")}";
        _crumbs.Text = BuildBreadcrumbs(element);

        _attrs.BeginUpdate();
        _attrs.Items.Clear();
        foreach (var attr in element.Attrs.OrderBy(a => a.Key))
            _attrs.Items.Add(new ListViewItem(new[] { attr.Key, attr.Value }));
        _attrs.EndUpdate();

        _computed.Text = style == null ? "No computed style is available." : BuildStyleInfo(style);
        _boxInfo.Text = box == null ? "This element has no layout box (it may be display:none or not laid out yet)." : BuildBoxInfo(box);
        _source.Text = BuildDomInfo(element, text);
    }

    private static string BuildBreadcrumbs(DomElement element)
    {
        var parts = new System.Collections.Generic.List<string>();
        for (DomNode? node = element; node is DomElement current; node = current.Parent)
            parts.Add(LabelFor(current));
        parts.Reverse();
        return string.Join("  >  ", parts);
    }

    private static string BuildStyleInfo(ComputedStyle style)
    {
        var sb = new StringBuilder();
        sb.AppendLine("TYPOGRAPHY");
        sb.AppendLine($"  font-family       {string.Join(", ", style.FontFamily)}");
        sb.AppendLine($"  font-size         {style.FontSize:0.##} px");
        sb.AppendLine($"  font-weight       {style.FontWeight}    style: {style.FontStyle}");
        sb.AppendLine($"  color             {ColorName(style.Color)}");
        sb.AppendLine($"  text-align        {style.TextAlign}    white-space: {style.WhiteSpace}");
        sb.AppendLine($"  line-height       {style.LineHeight:0.##}");
        sb.AppendLine();
        sb.AppendLine("LAYOUT");
        sb.AppendLine($"  display           {style.Display}    visibility: {style.Visibility}");
        sb.AppendLine($"  position          {style.Position}    float: {style.Float}    z-index: {style.ZIndex}");
        sb.AppendLine($"  width / height    {Length(style.Width)} / {Length(style.Height)}");
        sb.AppendLine();
        sb.AppendLine("PAINT");
        sb.AppendLine($"  background        {ColorName(style.BackgroundColor)}");
        sb.AppendLine($"  background image  {style.BackgroundImage ?? "none"}");
        return sb.ToString();
    }

    private static string BuildBoxInfo(LayoutBox box)
    {
        var sb = new StringBuilder();
        sb.AppendLine("RECTANGLES (document coordinates)");
        AppendRect(sb, "margin", box.MarginRect);
        AppendRect(sb, "border", box.BorderRect);
        AppendRect(sb, "padding", box.PaddingRect);
        AppendRect(sb, "content", box.ContentRect);
        sb.AppendLine();
        sb.AppendLine("SPACING");
        sb.AppendLine($"  margin    {Edges(box.MarginTop, box.MarginRight, box.MarginBottom, box.MarginLeft)}");
        sb.AppendLine($"  border    {Edges(box.BorderTop, box.BorderRight, box.BorderBottom, box.BorderLeft)}");
        sb.AppendLine($"  padding   {Edges(box.PaddingTop, box.PaddingRight, box.PaddingBottom, box.PaddingLeft)}");
        sb.AppendLine();
        sb.AppendLine($"type      {box.BoxType}");
        sb.AppendLine($"float     {(box.IsFloated ? box.FloatSide.ToString() : "none")}");
        sb.AppendLine($"position  {(box.IsAbsolutelyPositioned ? "absolute" : "normal")}");
        return sb.ToString();
    }

    private static void AppendRect(StringBuilder sb, string name, EngRectangleF rect) =>
        sb.AppendLine($"  {name,-9} X={rect.X:0.##}, Y={rect.Y:0.##}, W={rect.Width:0.##}, H={rect.Height:0.##}");

    private static string BuildDomInfo(DomElement element, string text)
    {
        var sb = new StringBuilder();
        sb.AppendLine("ELEMENT");
        sb.AppendLine($"  tag          {element.TagName}");
        sb.AppendLine($"  parent       {(element.Parent as DomElement)?.TagName ?? "(document)"}");
        sb.AppendLine($"  child nodes  {element.Children.Count}");
        sb.AppendLine($"  event hooks  {(element.EventHandlers.Count == 0 ? "none" : string.Join(", ", element.EventHandlers.Keys))}");
        sb.AppendLine();
        sb.AppendLine("INNER TEXT");
        sb.AppendLine(text.Length == 0 ? "(empty)" : text);
        return sb.ToString();
    }

    private void CopyDetails()
    {
        if (_current == null) return;
        try { Clipboard.SetText(_source.Text + "\r\n" + _computed.Text + "\r\n" + _boxInfo.Text); }
        catch (Exception) { }
    }

    private LayoutBox? FindBox(DomElement element)
    {
        var root = _canvas.RootBox;
        if (root == null) return null;
        return ReferenceEquals(root.Element, element) ? root : root.Descendants().FirstOrDefault(box => ReferenceEquals(box.Element, element));
    }

    private static string ColorName(EngColor color) =>
        color == EngColor.Transparent ? "transparent" : EngColorTranslator.ToHtml(color);

    private static string Length(float? value) => value.HasValue ? value.Value.ToString("0.##") + " px" : "auto";

    private static string Edges(float top, float right, float bottom, float left) =>
        $"top {top:0.##}, right {right:0.##}, bottom {bottom:0.##}, left {left:0.##}";
}