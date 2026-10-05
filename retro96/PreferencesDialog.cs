using System;
using System.Drawing;
using System.Globalization;
using System.Windows.Forms;
using Retro96.Plugins;

namespace Retro96;

internal sealed class PreferencesDialog : Form
{
    private UserSettings _settings;
    private readonly ComboBox _engine = new();
    private readonly TextBox _home = new();
    private readonly TextBox _search = new();
    private readonly CheckBox _customUa = new();
    private readonly TextBox _ua = new();
    private readonly Label _uaHint = new();
    private readonly RadioButton _pageBg = new();
    private readonly RadioButton _forcedBg = new();
    private readonly TextBox _bgHex = new();
    private readonly Button _pickBg = new();
    private readonly ComboBox _defaultPageZoom = new();
    private readonly CheckBox _images = new();
    private readonly CheckBox _javascript = new();
    private readonly CheckBox _vbscript = new();
    private readonly CheckBox _javaApplets = new();
    private readonly CheckBox _highDpiScaleMode = new();
    private readonly CheckBox _scriptWindows = new();
    private readonly TrackBar _trust = new();
    private readonly Label _trustTitle = new();
    private readonly TextBox _trustDetails = new();
    private readonly CheckBox _hostImageCheck = new();
    private readonly CheckBox _discardState = new();
    private readonly PluginManager? _pluginManager;
    private readonly Dictionary<string, Control> _pluginSettingControls = new(StringComparer.OrdinalIgnoreCase);
    private readonly CheckBox _pluginDevMode = new();
    private readonly ToolTip _advancedToolTips = new();

    // Advanced engine feature switches.
    private readonly CheckBox _loadStylesheets = new();
    private readonly CheckBox _loadFrames = new();
    private readonly CheckBox _allowForms = new();
    private readonly CheckBox _externalScripts = new();
    private readonly CheckBox _javascriptEval = new();
    private readonly CheckBox _jsTimers = new();
    private readonly CheckBox _jsDialogs = new();
    private readonly CheckBox _followRedirects = new();
    private readonly CheckBox _compressedResponses = new();
    private readonly CheckBox _httpCache = new();
    private readonly CheckBox _metaRefresh = new();
    private readonly CheckBox _cookies = new();
    private readonly CheckBox _referrer = new();
    private readonly CheckBox _animateImages = new();
    private readonly CheckBox _blink = new();
    private readonly CheckBox _marquee = new();
    private readonly NumericUpDown _jsExecutionSeconds = new();
    private readonly NumericUpDown _jsMemoryLimitMb = new();
    private readonly NumericUpDown _jsMaxCallDepth = new();
    private readonly NumericUpDown _scriptSpliceTokens = new();
    private readonly NumericUpDown _javaMaxCallDepth = new();
    private readonly NumericUpDown _maxRedirects = new();
    private readonly NumericUpDown _connectTimeoutSeconds = new();
    private readonly NumericUpDown _responseTimeoutSeconds = new();
    private readonly NumericUpDown _maxConcurrentFetches = new();
    private readonly NumericUpDown _maxFetchesPerPage = new();
    private readonly NumericUpDown _gifSpeedPercent = new();
    private readonly NumericUpDown _blinkIntervalMs = new();
    private readonly NumericUpDown _marqueeSpeedPercent = new();

    public PreferencesDialog(UserSettings source, PluginManager? pluginManager = null)
    {
        _settings = source.Clone();
        _pluginManager = pluginManager;
        FormClosed += (_, _) => _advancedToolTips.Dispose();

        Text = "Retro96 — Preferences";
        StartPosition = FormStartPosition.CenterParent;
        ClientSize = new Size(820, 640);
        MinimumSize = new Size(760, 600);
        FormBorderStyle = FormBorderStyle.Sizable;
        MaximizeBox = false;
        Font = new Font("Segoe UI", 9f);

        var tabs = new TabControl { Dock = DockStyle.Fill, Padding = new Point(12, 4) };
        tabs.TabPages.Add(BuildGeneralTab());
        tabs.TabPages.Add(BuildCompatibilityTab());
        tabs.TabPages.Add(BuildAppearanceTab());
        tabs.TabPages.Add(BuildSecurityTab());
        tabs.TabPages.Add(BuildAdvancedTab());
        if (_pluginManager != null)
            tabs.TabPages.Add(BuildPluginSettingsTab());

        var buttons = new Panel { Dock = DockStyle.Bottom, Height = 58, Padding = new Padding(10, 8, 10, 8) };
        var defaults = new Button { Text = "Restore Defaults", Width = 140, Height = 34, Dock = DockStyle.Left };
        var buttonRow = new FlowLayoutPanel
        {
            Dock = DockStyle.Right,
            AutoSize = true,
            WrapContents = false,
            FlowDirection = FlowDirection.RightToLeft,
            Margin = new Padding(0),
            Padding = new Padding(0)
        };
        var cancel = new Button { Text = "Cancel", Width = 96, Height = 34, Margin = new Padding(8, 0, 0, 0) };
        var ok = new Button { Text = "Apply", Width = 96, Height = 34, Margin = new Padding(8, 0, 0, 0) };
        buttonRow.Controls.Add(ok);
        buttonRow.Controls.Add(cancel);
        buttons.Controls.Add(defaults);
        buttons.Controls.Add(buttonRow);

        defaults.Click += (_, _) =>
        {
            _settings = new UserSettings();
            BindFromSettings();
            UpdateSecurityUi();
        };

        ok.Click += (_, _) =>
        {
            if (!UserSettings.TryNormalizeSearchTemplate(_search.Text, out _))
            {
                MessageBox.Show(this,
                    "The search URL must include a query placeholder: %s, {query}, or {searchTerms}.",
                    "Invalid search URL", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                _search.Focus();
                return;
            }

            // Do not give the button a DialogResult: WinForms may close the
            // modal form as part of the button activation before a later Click
            // subscriber gets a chance to copy the edited controls. Explicitly
            // copy first, then close with OK so the caller receives the values
            // that are actually visible in the dialog.
            CopyInto(_settings);
            DialogResult = DialogResult.OK;
            Close();
        };

        cancel.Click += (_, _) =>
        {
            DialogResult = DialogResult.Cancel;
            Close();
        };

        Controls.Add(tabs);
        Controls.Add(buttons);
        AcceptButton = ok;
        CancelButton = cancel;

        BindFromSettings();
        UpdateSecurityUi();
    }

    public UserSettings Settings => _settings.Clone();

    private TabPage BuildGeneralTab()
    {
        var page = NewTab("General");
        var panel = StackPanel();

        _home.Name = "home";
        _home.Width = 690;
        _home.Height = 30;
        _home.Anchor = AnchorStyles.Left | AnchorStyles.Right;

        panel.Controls.Add(Group("Home page", new Control[]
        {
            new Label { Text = "Startup / home URL", AutoSize = true },
            _home
        }));

        _search.Name = "search";
        _search.Width = 690;
        _search.Height = 30;
        _search.Anchor = AnchorStyles.Left | AnchorStyles.Right;
        panel.Controls.Add(Group("Address bar search", new Control[]
        {
            new Label { Text = "Search URL template (%s is replaced with the query)", AutoSize = true },
            _search,
            new Label { Text = "Example: https://www.frogfind.com/?q=%s", AutoSize = true, ForeColor = SystemColors.GrayText }
        }));

        ConfigureCheckBox(_scriptWindows, "Allow scripted pop-ups and new windows");
        ConfigureCheckBox(_images, "Load images from web pages");

        panel.Controls.Add(Group("Page behaviour", new Control[]
        {
            _scriptWindows,
            _images
        }));

        _defaultPageZoom.DropDownStyle = ComboBoxStyle.DropDownList;
        _defaultPageZoom.Width = 140;
        _defaultPageZoom.Items.AddRange(new object[] { "100%", "125%", "150%", "200%" });
        ConfigureCheckBox(_highDpiScaleMode, "Enable Windows Per-Monitor V2 high-DPI scaling (recommended)");
        panel.Controls.Add(Group("Display scaling", new Control[]
        {
            new Label { Text = "Default page zoom", AutoSize = true },
            _defaultPageZoom,
            new Label
            {
                Text = "Sets the starting zoom for web pages. Use the toolbar or View menu to adjust the current page; the selected default is used again after restarting Retro96.",
                AutoSize = true, MaximumSize = new Size(690, 0),
                ForeColor = Color.FromArgb(90, 96, 104)
            },
            _highDpiScaleMode,
            new Label
            {
                Text = "Per-Monitor V2 follows Windows display scaling when moving Retro96 between monitors. Changing this Windows DPI-awareness mode requires restarting Retro96.",
                AutoSize = true, MaximumSize = new Size(690, 0),
                ForeColor = Color.FromArgb(90, 96, 104)
            }
        }));

        page.Controls.Add(panel);
        return page;
    }

    private TabPage BuildCompatibilityTab()
    {
        var page = NewTab("Compatibility");
        var panel = StackPanel();

        _engine.DropDownStyle = ComboBoxStyle.DropDownList;
        _engine.Width = 690;
        _engine.Height = 30;
        _engine.DropDownWidth = 690;
        _engine.Items.Add("Retro96 Engine (all compatibility features)");
        _engine.Items.Add("Internet Explorer 5 (1999)");
        _engine.Items.Add("Netscape Navigator 4.7 (1999)");
        _engine.SelectedIndexChanged += (_, _) => UpdateUserAgentHint();

        panel.Controls.Add(Group("Browser personality", new Control[]
        {
            new Label { Text = "Engine mode", AutoSize = true },
            _engine,
            new Label
            {
                Text = "Internet Explorer 5 is the 1999 default: JScript 5.0, DOM Level 1 + DHTML object model, " +
                       "document.all, the IE5 box model and HTTP/1.1. " +
                       "Netscape Navigator 4.7 is the 1999 Netscape personality: JavaScript 1.3, " +
                       "the layer DOM and capture events, without document.all or getElementById. " +
                       "Retro96 Engine is the native mode and exposes the full compatibility union.",
                AutoSize = true,
                MaximumSize = new Size(690, 0),
                ForeColor = Color.FromArgb(90, 96, 104)
            }
        }));

        ConfigureCheckBox(_customUa, "Override the User-Agent sent to sites and exposed as navigator.userAgent");
        _customUa.AutoSize = true;
        _customUa.CheckedChanged += (_, _) => UpdateUaEnabled();

        _ua.Multiline = true;
        _ua.Height = 72;
        _ua.Width = 690;
        _ua.ScrollBars = ScrollBars.Vertical;
        _ua.Anchor = AnchorStyles.Left | AnchorStyles.Right;
        _uaHint.AutoSize = true;
        _uaHint.Width = 690;
        _uaHint.MaximumSize = new Size(690, 0);
        _uaHint.Padding = new Padding(0, 0, 0, 2);
        _uaHint.ForeColor = Color.FromArgb(90, 96, 104);

        panel.Controls.Add(Group("User-Agent spoofing", new Control[]
        {
            _customUa,
            _ua,
            _uaHint
        }));

        panel.Controls.Add(Group("Era compatibility", new Control[]
        {
            new Label
            {
                Text = "Retro96 Engine: native compatibility-union surface with both IE-style and Navigator-style legacy APIs plus the engine's broader DOM/runtime support. " +
                       "Internet Explorer 5: the 1999 JScript and DHTML profile. " +
                       "Netscape Navigator 4.7: the 1999 JavaScript and layer-DOM profile.",
                AutoSize = false, Height = 82, Width = 690,
                ForeColor = Color.FromArgb(55, 60, 68)
            }
        }));

        page.Controls.Add(panel);
        return page;
    }

    private TabPage BuildAppearanceTab()
    {
        var page = NewTab("Appearance");
        var panel = StackPanel();

        _pageBg.Text = "Use the page's requested background colour";
        _pageBg.AutoSize = true;
        _forcedBg.Text = "Force this background colour";
        _forcedBg.AutoSize = true;
        _pageBg.CheckedChanged += (_, _) => UpdateBgEnabled();
        _forcedBg.CheckedChanged += (_, _) => UpdateBgEnabled();

        _bgHex.Width = 120;
        _bgHex.Height = 28;
        _pickBg.Text = "Choose…";
        _pickBg.Width = 96;
        _pickBg.Height = 30;
        _pickBg.Click += (_, _) =>
        {
            using var dialog = new ColorDialog { FullOpen = true };
            if (TryParseColor(_bgHex.Text, out var color)) dialog.Color = color;
            if (dialog.ShowDialog(this) == DialogResult.OK)
                _bgHex.Text = $"#{dialog.Color.R:X2}{dialog.Color.G:X2}{dialog.Color.B:X2}";
        };

        var bgRow = new FlowLayoutPanel
        {
            AutoSize = true, WrapContents = false, Dock = DockStyle.Top, Padding = new Padding(0, 4, 0, 0),
            FlowDirection = FlowDirection.LeftToRight
        };
        bgRow.Controls.Add(_bgHex);
        bgRow.Controls.Add(_pickBg);

        panel.Controls.Add(Group("Page background", new Control[]
        {
            _pageBg,
            _forcedBg,
            bgRow,
            new Label { Text = "This only affects the canvas background; authored BODY/CSS colours remain untouched in default mode.", AutoSize = false, Width = 690, Height = 34, ForeColor = Color.FromArgb(90, 96, 104) }
        }));

        panel.Controls.Add(Group("Images", new Control[]
        {
            new Label
            {
                Text = "When image loading is disabled, IMG/background-image requests and JavaScript Image() preloads are blocked before network access.",
                AutoSize = false, Width = 690, Height = 55
            }
        }));

        page.Controls.Add(panel);
        return page;
    }

    private TabPage BuildSecurityTab()
    {
        var page = NewTab("Security");
        var panel = StackPanel();

        _trust.Minimum = 0;
        _trust.Maximum = 2;
        _trust.TickFrequency = 1;
        _trust.LargeChange = 1;
        _trust.SmallChange = 1;
        _trust.Dock = DockStyle.Top;
        _trust.Height = 48;
        _trust.ValueChanged += (_, _) => UpdateSecurityUi();

        _trustTitle.AutoSize = true;
        _trustTitle.Font = new Font(Font, FontStyle.Bold);

        _trustDetails.Multiline = true;
        _trustDetails.ReadOnly = true;
        _trustDetails.BorderStyle = BorderStyle.None;
        _trustDetails.BackColor = SystemColors.Window;
        _trustDetails.Width = 690;
        _trustDetails.Height = 108;
        _trustDetails.ForeColor = Color.FromArgb(55, 60, 68);

        panel.Controls.Add(Group("Trust level", new Control[]
        {
            new Label { Text = "Strict ← security → compatibility", AutoSize = true },
            _trust,
            _trustTitle,
            _trustDetails
        }));

        ConfigureCheckBox(_hostImageCheck, "Always route site images through the host fetch/decode/check path");
        _hostImageCheck.CheckedChanged += (_, _) => { if (_trust.Value == 0) _hostImageCheck.Checked = true; };

        ConfigureCheckBox(_discardState, "Discard page state when a browser window closes");

        panel.Controls.Add(Group("Isolation policy", new Control[]
        {
            _hostImageCheck,
            _discardState,
            new Label
            {
                Text = "Page scripts are not given .NET, file, process, or reflection APIs; browser-host VBScript also denies CreateObject/GetObject. " +
                       "High and Medium modes additionally reject page-directed file resources and keep image downloads host-mediated.",
                AutoSize = false, Width = 690, Height = 78, ForeColor = Color.FromArgb(75, 84, 96)
            }
        }));

        panel.Controls.Add(Group("Windows process isolation", new Control[]
        {
            new Label
            {
                Text = WindowsSecurity.GetHighModeStatusText(),
                AutoSize = false, Width = 690, Height = 52, ForeColor = Color.FromArgb(35, 42, 52)
            },
            new Label
            {
                Text = "High and Medium modes launch each page engine in a separate Windows AppContainer worker. Network and local-resource access are brokered by the trusted host, child-process creation is blocked, and the worker is terminated by its Job Object when the window closes. Native-AOT workers additionally use LPAC.",
                AutoSize = false, Width = 690, Height = 110, ForeColor = Color.FromArgb(90, 96, 104)
            }
        }));

        page.Controls.Add(panel);
        return page;
    }

    private TabPage BuildAdvancedTab()
    {
        var page = NewTab("Advanced");
        page.AutoScroll = false;

        var settingsBox = new SettingsGroupBox
        {
            Text = "Settings",
            Dock = DockStyle.Fill,
            Padding = new Padding(12, 18, 12, 10)
        };

        var scrollPanel = new Panel
        {
            Dock = DockStyle.Fill,
            AutoScroll = true,
            Padding = new Padding(8, 4, 8, 4)
        };
        var list = new FlowLayoutPanel
        {
            Dock = DockStyle.Top,
            AutoSize = true,
            FlowDirection = FlowDirection.TopDown,
            WrapContents = false,
            Padding = new Padding(0),
            Margin = new Padding(0),
            Width = 680
        };

        list.Controls.Add(AdvancedCategory("Page loading", new Control[]
        {
            AdvancedToggle(_loadStylesheets, "Load external CSS stylesheets",
                "Disable to ignore <link rel=stylesheet> resources."),
            AdvancedToggle(_loadFrames, "Load frames and IFRAMEs",
                "Disable to leave frame boxes in place without fetching their documents."),
            AdvancedToggle(_allowForms, "Allow HTML form submissions",
                "Disable to keep form controls usable but block GET/POST submission.")
        }));
        list.Controls.Add(AdvancedCategory("Scripting", new Control[]
        {
            AdvancedToggle(_javascript, "Enable JavaScript",
                "Controls JavaScript script blocks. VBScript can be enabled independently with the setting below."),
            AdvancedToggle(_vbscript, "Enable VBScript",
                "Disable to prevent VBScript blocks and event procedures from running. High trust mode always blocks VBScript."),
            AdvancedToggle(_externalScripts, "Run external scripts",
                "Disable to skip scripts loaded from a script element's src attribute while keeping inline scripts available."),
            AdvancedToggle(_javascriptEval, "Allow JavaScript eval()",
                "Disable to remove the eval() function without disabling other JavaScript features."),
            AdvancedToggle(_jsTimers, "Run JavaScript timers (setTimeout / setInterval)",
                "Disable to keep JavaScript enabled while stopping scheduled script callbacks."),
            AdvancedToggle(_jsDialogs, "Allow script dialogs (JavaScript alert / confirm / prompt; VBScript MsgBox / InputBox)",
                "Disable to suppress modal dialogs requested by either scripting engine."),
            AdvancedNumber(_jsExecutionSeconds, "JavaScript execution timeout (seconds)", 1, 60,
                "Maximum wall-clock time allowed for a single script execution."),
            AdvancedNumber(_jsMemoryLimitMb, "JavaScript memory limit (MiB)", 1, 512,
                "Approximate allocation budget for one JavaScript execution."),
            AdvancedNumber(_jsMaxCallDepth, "JavaScript maximum call depth", 32, 2000,
                "Maximum nested JavaScript function calls."),
            AdvancedNumber(_scriptSpliceTokens, "document.write script-splice token limit", 1000, 2000000,
                "Maximum token count accepted when document.write inserts markup during parsing.")
        }));
        list.Controls.Add(AdvancedCategory("Java", new Control[]
        {
            AdvancedToggle(_javaApplets, "Enable Java applets",
                "Runs Java 1.0/1.1 bytecode plus selected later runtime APIs in Retro96's built-in interpreter (no external JRE). High trust mode always blocks applets."),
            AdvancedNumber(_javaMaxCallDepth, "Java maximum call depth", 32, 2000,
                "Maximum nested method calls in the built-in Java interpreter.")
        }));
        list.Controls.Add(AdvancedCategory("Networking", new Control[]
        {
            AdvancedToggle(_followRedirects, "Follow HTTP redirects",
                "Disable to stop after the first 301/302/303/307/308 response."),
            AdvancedToggle(_metaRefresh, "Follow HTML META refresh navigation",
                "Disable automatic <meta http-equiv=refresh> navigation."),
            AdvancedToggle(_cookies, "Enable HTTP cookies",
                "Controls both Cookie/Set-Cookie handling and document.cookie."),
            AdvancedToggle(_referrer, "Send the Referer request header",
                "Disable to omit the previous page URL from outgoing requests."),
            AdvancedToggle(_compressedResponses, "Request gzip-compressed responses",
                "Advertises gzip support to servers; gzip responses are decoded before page content is processed."),
            AdvancedToggle(_httpCache, "HTTP/1.1 validation cache (ETag / 304)",
                "Revalidates cached pages and images with If-None-Match / If-Modified-Since and serves 304 responses from cache. Only applies to the 1999 engine profiles."),
            AdvancedNumber(_maxRedirects, "Maximum HTTP redirects", 0, 20,
                "Maximum number of redirects followed for one request."),
            AdvancedNumber(_connectTimeoutSeconds, "Connection timeout (seconds)", 1, 120,
                "Maximum time to establish a network connection."),
            AdvancedNumber(_responseTimeoutSeconds, "Response timeout (seconds)", 1, 300,
                "Maximum time to wait for a response after connecting."),
            AdvancedNumber(_maxConcurrentFetches, "Concurrent resource requests", 1, 32,
                "Maximum number of simultaneous page-resource requests."),
            AdvancedNumber(_maxFetchesPerPage, "Maximum resource requests per page", 1, 10000,
                "Page-wide limit shared with nested frames.")
        }));
        list.Controls.Add(AdvancedCategory("Legacy rendering", new Control[]
        {
            AdvancedToggle(_animateImages, "Animate GIF images",
                "Disable to freeze animated images on their current frame."),
            AdvancedNumber(_gifSpeedPercent, "Animated GIF speed (%)", 25, 400,
                "Playback speed relative to each GIF's encoded frame delays."),
            AdvancedToggle(_blink, "Animate <blink> text",
                "Disable to keep blinking text permanently visible."),
            AdvancedNumber(_blinkIntervalMs, "Blink interval (milliseconds)", 100, 2000,
                "Time between visibility changes for legacy blinking text."),
            AdvancedToggle(_marquee, "Animate <marquee> text",
                "Disable to stop the legacy marquee repaint loop."),
            AdvancedNumber(_marqueeSpeedPercent, "Marquee speed (%)", 25, 400,
                "Speed multiplier applied to legacy marquee motion.")
        }));

        scrollPanel.Controls.Add(list);
        settingsBox.Controls.Add(scrollPanel);

        var note = new Label
        {
            Text = "Security mode takes precedence over conflicting compatibility settings. Disabling a setting here cannot grant a page access that the selected trust level forbids.",
            Dock = DockStyle.Fill,
            AutoSize = true,
            ForeColor = Color.FromArgb(75, 84, 96),
            Margin = new Padding(4, 8, 4, 0)
        };
        var layout = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 2,
            Padding = new Padding(0)
        };
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.Controls.Add(settingsBox, 0, 0);
        layout.Controls.Add(note, 0, 1);
        page.Controls.Add(layout);
        return page;
    }

    private Control AdvancedCategory(string title, Control[] options)
    {
        var section = new FlowLayoutPanel
        {
            AutoSize = true,
            FlowDirection = FlowDirection.TopDown,
            WrapContents = false,
            Padding = new Padding(0),
            Margin = new Padding(0, 0, 0, 5),
            Width = 660
        };
        var header = new Button
        {
            Text = "\u25bc  " + title,
            TextAlign = ContentAlignment.MiddleLeft,
            FlatStyle = FlatStyle.Flat,
            BackColor = SystemColors.Window,
            UseVisualStyleBackColor = false,
            Font = new Font("Segoe UI", 9f, FontStyle.Bold),
            Width = 640,
            Height = 34,
            Margin = new Padding(0, 1, 0, 2),
            Padding = new Padding(6, 0, 0, 0)
        };
        header.FlatAppearance.BorderSize = 0;
        var content = new FlowLayoutPanel
        {
            AutoSize = true,
            FlowDirection = FlowDirection.TopDown,
            WrapContents = false,
            Padding = new Padding(0),
            Margin = new Padding(0, 0, 0, 0),
            Width = 640
        };
        foreach (Control option in options)
            content.Controls.Add(option);
        header.Click += (_, _) =>
        {
            content.Visible = !content.Visible;
            header.Text = (content.Visible ? "\u25bc  " : "\u25b6  ") + title;
        };
        section.Controls.Add(header);
        section.Controls.Add(content);
        return section;
    }

    private Control AdvancedToggle(CheckBox box, string text, string description)
    {
        ConfigureCheckBox(box, text);
        box.Margin = new Padding(20, 0, 0, 1);
        _advancedToolTips.SetToolTip(box, description);
        return box;
    }

    private Control AdvancedNumber(NumericUpDown input, string label, int minimum, int maximum, string description)
    {
        input.Minimum = minimum;
        input.Maximum = maximum;
        input.Width = 90;
        input.Margin = new Padding(0, 0, 0, 1);
        var row = new FlowLayoutPanel
        {
            AutoSize = true,
            FlowDirection = FlowDirection.LeftToRight,
            WrapContents = false,
            Margin = new Padding(20, 2, 0, 3),
            Width = 620
        };
        row.Controls.Add(new Label
        {
            Text = label,
            AutoSize = false,
            Width = 440,
            Height = 25,
            TextAlign = ContentAlignment.MiddleLeft
        });
        row.Controls.Add(input);
        _advancedToolTips.SetToolTip(input, description);
        _advancedToolTips.SetToolTip(row, description);
        return row;
    }

    private TabPage BuildPluginSettingsTab()
    {
        var page = NewTab("Plugins");
        var panel = StackPanel();
        ConfigureCheckBox(_pluginDevMode, "Enable developer mode");
        _pluginDevMode.CheckedChanged += (_, _) => UpdatePluginDevWarning(panel);
        panel.Controls.Add(Group("Developer mode", new Control[]
        {
            _pluginDevMode,
            new Label { Text = "Load Unpacked is intended only for development. It disables the normal packaged-plugin workflow for developer-loaded plugins and is not a security boundary.", AutoSize = true, MaximumSize = new Size(680, 0), ForeColor = SystemColors.GrayText }
        }));
        bool any = false;
        foreach (var record in _pluginManager!.Plugins)
        {
            if (record.Manifest.Settings.Count == 0) continue;
            var controls = new List<Control>();
            if (!record.HasPermission(PluginPermission.Settings))
            {
                controls.Add(new Label
                {
                    Text = "This plugin has declared settings, but the settings permission is not granted. Grant it in Addons → Permissions to edit these values.",
                    AutoSize = true, MaximumSize = new Size(680, 0), ForeColor = SystemColors.GrayText
                });
            }
            else
            {
                var values = _pluginManager.GetPluginSettings(record);
                foreach (var definition in record.Manifest.Settings)
                {
                    Control control = CreatePluginSettingControl(definition, values.TryGetValue(definition.Name, out var value) ? value : definition.DefaultValue ?? "");
                    _pluginSettingControls[SettingKey(record, definition)] = control;
                    controls.Add(new Label { Text = definition.Description ?? "", AutoSize = true, MaximumSize = new Size(650, 0), ForeColor = SystemColors.GrayText, Visible = !string.IsNullOrWhiteSpace(definition.Description) });
                    controls.Add(new Label { Text = string.IsNullOrWhiteSpace(definition.Label) ? definition.Name : definition.Label, AutoSize = true });
                    controls.Add(control);
                }
            }
            panel.Controls.Add(Group(record.Manifest.Name, controls.ToArray()));
            any = true;
        }
        if (!any)
            panel.Controls.Add(new Label { Text = "No installed plugins declare host-rendered settings.", AutoSize = true, ForeColor = SystemColors.GrayText });
        page.Controls.Add(panel);
        return page;
    }

    private void UpdatePluginDevWarning(Control panel)
    {
        var warning = panel.Controls.OfType<GroupBox>().FirstOrDefault(g => g.Text == "Developer mode");
        if (warning == null) return;
        var label = warning.Controls.OfType<FlowLayoutPanel>().SelectMany(x => x.Controls.OfType<Label>()).FirstOrDefault(l => l.Text.StartsWith("Developer mode is ON", StringComparison.Ordinal));
        if (_pluginDevMode.Checked && label == null)
        {
            warning.Controls[0].Controls.Add(new Label { Text = "Developer mode is ON. Unpacked plugins are code under active development; this warning remains visible while the mode is enabled.", AutoSize = true, MaximumSize = new Size(680, 0), ForeColor = SystemColors.WindowText });
        }
    }

    private static string SettingKey(PluginManager.PluginRecord record, PluginSettingDefinition definition) => record.Manifest.Id + "\n" + definition.Name;

    private static Control CreatePluginSettingControl(PluginSettingDefinition definition, string value)
    {
        string type = definition.Type.Trim().ToLowerInvariant();
        if (type == "toggle")
            return new CheckBox { Text = "Enabled", AutoSize = true, Checked = bool.TryParse(value, out var b) && b, MinimumSize = new Size(0, 24), Tag = definition };
        if (type == "select")
        {
            var combo = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = 420, Tag = definition };
            foreach (string option in definition.Options ?? Array.Empty<string>()) combo.Items.Add(option);
            int selected = combo.Items.IndexOf(value);
            combo.SelectedIndex = selected >= 0 ? selected : (combo.Items.Count > 0 ? 0 : -1);
            return combo;
        }
        return new TextBox { Width = 620, Text = value, Tag = definition };
    }

    private void BindFromSettings()
    {
        _home.Text = _settings.HomePageUrl;
        _search.Text = _settings.SearchQueryUrl;
        _engine.SelectedIndex = _settings.EngineMode switch
        {
            RetroEngineMode.InternetExplorer5 or
            RetroEngineMode.InternetExplorer3 or
            RetroEngineMode.Netscape3 => 1,
            RetroEngineMode.Netscape47 => 2,
            _ => 0
        };
        _customUa.Checked = !string.IsNullOrWhiteSpace(_settings.UserAgentOverride);
        _ua.Text = _settings.UserAgentOverride;
        _pageBg.Checked = _settings.BackgroundMode == BackgroundMode.PageDefault;
        _forcedBg.Checked = _settings.BackgroundMode == BackgroundMode.Force;
        _bgHex.Text = _settings.ForcedBackgroundColor;
        _images.Checked = _settings.LoadImages;
        _javascript.Checked = _settings.EnableJavaScript;
        _vbscript.Checked = _settings.EnableVBScript;
        _javaApplets.Checked = _settings.EnableJavaApplets;
        _scriptWindows.Checked = _settings.AllowScriptedWindows;
        _highDpiScaleMode.Checked = _settings.HighDpiScaleMode;
        _defaultPageZoom.SelectedItem = $"{_settings.DefaultPageZoomPercent}%";
        // Clamp: a corrupt/hand-edited settings file with an out-of-range enum
        // value would otherwise throw when assigned to the TrackBar.
        _trust.Value = Math.Clamp((int)_settings.TrustMode, _trust.Minimum, _trust.Maximum);
        _hostImageCheck.Checked = _settings.HostCheckImages;
        _discardState.Checked = _settings.DiscardPageStateOnClose;
        _pluginDevMode.Checked = _settings.PluginDevMode;
        _loadStylesheets.Checked = _settings.LoadStylesheets;
        _loadFrames.Checked = _settings.LoadFrames;
        _allowForms.Checked = _settings.AllowFormSubmissions;
        _externalScripts.Checked = _settings.EnableExternalScripts;
        _javascriptEval.Checked = _settings.EnableJavaScriptEval;
        _jsTimers.Checked = _settings.EnableJavaScriptTimers;
        _jsDialogs.Checked = _settings.EnableJavaScriptDialogs;
        _followRedirects.Checked = _settings.FollowHttpRedirects;
        _compressedResponses.Checked = _settings.RequestCompressedResponses;
        _httpCache.Checked = _settings.EnableHttpCache;
        _metaRefresh.Checked = _settings.FollowMetaRefresh;
        _cookies.Checked = _settings.EnableCookies;
        _referrer.Checked = _settings.SendReferrer;
        _animateImages.Checked = _settings.AnimateImages;
        _blink.Checked = _settings.BlinkText;
        _marquee.Checked = _settings.MarqueeText;
        _jsExecutionSeconds.Value = _settings.JavaScriptMaxExecutionSeconds;
        _jsMemoryLimitMb.Value = _settings.JavaScriptMemoryLimitMb;
        _jsMaxCallDepth.Value = _settings.JavaScriptMaxCallDepth;
        _scriptSpliceTokens.Value = _settings.MaxScriptSpliceTokens;
        _javaMaxCallDepth.Value = _settings.JavaMaxCallDepth;
        _maxRedirects.Value = _settings.MaxHttpRedirects;
        _connectTimeoutSeconds.Value = _settings.HttpConnectTimeoutSeconds;
        _responseTimeoutSeconds.Value = _settings.HttpResponseTimeoutSeconds;
        _maxConcurrentFetches.Value = _settings.MaxConcurrentResourceFetches;
        _maxFetchesPerPage.Value = _settings.MaxResourceFetchesPerPage;
        _gifSpeedPercent.Value = _settings.AnimatedGifSpeedPercent;
        _blinkIntervalMs.Value = _settings.BlinkIntervalMilliseconds;
        _marqueeSpeedPercent.Value = _settings.MarqueeSpeedPercent;
        UpdateUserAgentHint();
        UpdateUaEnabled();
        UpdateBgEnabled();
    }

    private void CopyInto(UserSettings target)
    {
        target.EngineMode = _engine.SelectedIndex switch
        {
            2 => RetroEngineMode.Netscape47,
            1 => RetroEngineMode.InternetExplorer5,
            _ => RetroEngineMode.Retro96
        };
        target.UserAgentOverride = _customUa.Checked ? _ua.Text.Trim() : "";
        target.BackgroundMode = _forcedBg.Checked ? BackgroundMode.Force : BackgroundMode.PageDefault;
        target.ForcedBackgroundColor = NormalizeHex(_bgHex.Text);
        target.LoadImages = _images.Checked;
        target.EnableJavaScript = _javascript.Checked;
        target.EnableVBScript = _vbscript.Checked;
        target.EnableJavaApplets = _javaApplets.Checked;
        target.AllowScriptedWindows = _scriptWindows.Checked;
        target.HighDpiScaleMode = _highDpiScaleMode.Checked;
        target.DefaultPageZoomPercent = int.TryParse(
            Convert.ToString(_defaultPageZoom.SelectedItem)?.TrimEnd('%'),
            NumberStyles.Integer, CultureInfo.InvariantCulture, out int zoomPercent)
            ? zoomPercent
            : 100;
        target.TrustMode = (TrustMode)Math.Clamp(_trust.Value, 0, 2);
        target.HostCheckImages = _hostImageCheck.Checked || target.TrustMode == TrustMode.High;
        target.DiscardPageStateOnClose = _discardState.Checked;
        target.PluginDevMode = _pluginDevMode.Checked;
        target.LoadStylesheets = _loadStylesheets.Checked;
        target.LoadFrames = _loadFrames.Checked;
        target.AllowFormSubmissions = _allowForms.Checked;
        target.EnableExternalScripts = _externalScripts.Checked;
        target.EnableJavaScriptEval = _javascriptEval.Checked;
        target.EnableJavaScriptTimers = _jsTimers.Checked;
        target.EnableJavaScriptDialogs = _jsDialogs.Checked;
        target.FollowHttpRedirects = _followRedirects.Checked;
        target.RequestCompressedResponses = _compressedResponses.Checked;
        target.EnableHttpCache = _httpCache.Checked;
        target.FollowMetaRefresh = _metaRefresh.Checked;
        target.EnableCookies = _cookies.Checked;
        target.SendReferrer = _referrer.Checked;
        target.AnimateImages = _animateImages.Checked;
        target.BlinkText = _blink.Checked;
        target.MarqueeText = _marquee.Checked;
        target.JavaScriptMaxExecutionSeconds = decimal.ToInt32(_jsExecutionSeconds.Value);
        target.JavaScriptMemoryLimitMb = decimal.ToInt32(_jsMemoryLimitMb.Value);
        target.JavaScriptMaxCallDepth = decimal.ToInt32(_jsMaxCallDepth.Value);
        target.MaxScriptSpliceTokens = decimal.ToInt32(_scriptSpliceTokens.Value);
        target.JavaMaxCallDepth = decimal.ToInt32(_javaMaxCallDepth.Value);
        target.MaxHttpRedirects = decimal.ToInt32(_maxRedirects.Value);
        target.HttpConnectTimeoutSeconds = decimal.ToInt32(_connectTimeoutSeconds.Value);
        target.HttpResponseTimeoutSeconds = decimal.ToInt32(_responseTimeoutSeconds.Value);
        target.MaxConcurrentResourceFetches = decimal.ToInt32(_maxConcurrentFetches.Value);
        target.MaxResourceFetchesPerPage = decimal.ToInt32(_maxFetchesPerPage.Value);
        target.AnimatedGifSpeedPercent = decimal.ToInt32(_gifSpeedPercent.Value);
        target.BlinkIntervalMilliseconds = decimal.ToInt32(_blinkIntervalMs.Value);
        target.MarqueeSpeedPercent = decimal.ToInt32(_marqueeSpeedPercent.Value);

        if (_pluginManager != null)
        {
            foreach (var record in _pluginManager.Plugins.Where(r => r.HasPermission(PluginPermission.Settings) && r.Manifest.Settings.Count > 0))
            foreach (var definition in record.Manifest.Settings)
            {
                if (!_pluginSettingControls.TryGetValue(SettingKey(record, definition), out var control)) continue;
                string value = control switch
                {
                    CheckBox check => check.Checked ? "true" : "false",
                    ComboBox combo => combo.SelectedItem?.ToString() ?? "",
                    TextBox text => text.Text,
                    _ => ""
                };
                _pluginManager.SetPluginSetting(record, definition, value);
            }
        }

        if (!string.IsNullOrWhiteSpace(_home.Text)) target.HomePageUrl = _home.Text.Trim();
        if (!string.IsNullOrWhiteSpace(_search.Text))
            target.SearchQueryUrl = UserSettings.NormalizeSearchTemplate(_search.Text);
    }

    private void UpdateUserAgentHint()
    {
        (string def, string engine) = _engine.SelectedIndex switch
        {
            2 => (UserSettings.DefaultNetscape47UserAgent, "Netscape JavaScript 1.3"),
            1 => (UserSettings.DefaultIe5UserAgent, "Microsoft JScript 5.0 (ES3)"),
            _ => (UserSettings.DefaultRetro96UserAgent, "Retro96 Script Engine (IE5 navigator identity; 1999 compatibility union)")
        };
        _uaHint.Text = "Profile default: " + def + Environment.NewLine +
                       "Script engine personality: " + engine + ". The same User-Agent is sent in HTTP and exposed as navigator.userAgent.";
    }

    private void UpdateUaEnabled() => _ua.Enabled = _customUa.Checked;

    private void UpdateBgEnabled()
    {
        _bgHex.Enabled = _forcedBg.Checked;
        _pickBg.Enabled = _forcedBg.Checked;
    }

    private void UpdateSecurityUi()
    {
        var mode = (TrustMode)_trust.Value;
        switch (mode)
        {
            case TrustMode.High:
                _trustTitle.Text = "High — strict / untrusted sites";
                _trustDetails.Text =
                    "No page-directed file access. No Process/IO/Reflection capability. " +
                    "Images stay host-mediated and checked before decode; scripted new windows are blocked. " +
                    "Child processes are blocked and the worker is Job-object isolated. VBScript and Java applets are blocked regardless of their Advanced settings. 8 MiB image ceiling.";
                break;
            case TrustMode.Medium:
                _trustTitle.Text = "Medium — balanced";
                _trustDetails.Text =
                    "Still denies page-directed local file access and keeps all site networking/image fetching in the host broker. " +
                    "Scripted windows are allowed by their preference; JavaScript, VBScript, and Java applets have independent Advanced settings. Child-process creation is blocked. 16 MiB image ceiling.";
                break;
            default:
                _trustTitle.Text = "Low — trusted / compatibility";
                _trustDetails.Text =
                    "Compatibility first. Page-directed file URLs/resources are allowed and network bypasses the host broker. " +
                    "JavaScript, VBScript, and Java applet availability are controlled independently in Advanced. Scripts expose no .NET file/process API, and the worker remains separately process-isolated. 32 MiB image ceiling.";
                break;
        }
        _hostImageCheck.Checked = mode == TrustMode.High || _hostImageCheck.Checked;
        _hostImageCheck.Enabled = mode != TrustMode.High;
        _scriptWindows.Enabled = mode != TrustMode.High;
        _javaApplets.Enabled = mode != TrustMode.High;
    }

    private TabPage NewTab(string text) => new(text) { Padding = new Padding(14), AutoScroll = true };

    private static void ConfigureCheckBox(CheckBox box, string text)
    {
        box.Text = text;
        box.AutoSize = true;
        box.MinimumSize = new Size(0, 24);
        box.MaximumSize = new Size(680, 0);
        box.Padding = new Padding(0, 2, 0, 2);
    }

    private sealed class SettingsGroupBox : GroupBox
    {
        public SettingsGroupBox()
        {
            FlatStyle = FlatStyle.Flat;
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);

            Size titleSize = TextRenderer.MeasureText(e.Graphics, Text, Font, Size.Empty, TextFormatFlags.NoPadding);
            int top = Math.Max(8, Font.Height / 2);
            int titleStart = 12;
            int titleEnd = titleStart + titleSize.Width + 8;
            int right = ClientSize.Width - 2;
            int bottom = ClientSize.Height - 2;

            using var border = new Pen(Color.FromArgb(75, 75, 75), 2f);
            e.Graphics.DrawLine(border, 1, top, titleStart - 4, top);
            e.Graphics.DrawLine(border, titleEnd, top, right, top);
            e.Graphics.DrawLine(border, 1, top, 1, bottom);
            e.Graphics.DrawLine(border, right, top, right, bottom);
            e.Graphics.DrawLine(border, 1, bottom, right, bottom);

            var titleBounds = new Rectangle(titleStart, 0, titleSize.Width + 4, Font.Height);
            TextRenderer.DrawText(e.Graphics, Text, Font, titleBounds, ForeColor,
                BackColor, TextFormatFlags.NoPadding | TextFormatFlags.VerticalCenter);
        }
    }

    private static GroupBox Group(string title, Control[] controls)
    {
        var group = new GroupBox
        {
            Text = title,
            Dock = DockStyle.Top,
            AutoSize = true,
            Padding = new Padding(12, 18, 12, 10),
            Margin = new Padding(0, 0, 0, 14),
            Width = 710
        };

        var flow = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            AutoSize = true,
            FlowDirection = FlowDirection.TopDown,
            WrapContents = false,
            Padding = new Padding(0),
            Margin = new Padding(0),
            Width = 684
        };
        foreach (var control in controls)
        {
            control.Margin = new Padding(0, 0, 0, 7);
            flow.Controls.Add(control);
        }
        group.Controls.Add(flow);
        group.Height = Math.Max(72, 36 + flow.PreferredSize.Height);
        return group;
    }

    private static FlowLayoutPanel StackPanel() => new()
    {
        Dock = DockStyle.Top,
        AutoSize = true,
        FlowDirection = FlowDirection.TopDown,
        WrapContents = false
    };

    private static string NormalizeHex(string input)
    {
        input = (input ?? "").Trim();
        if (TryParseColor(input, out var c))
            return $"#{c.R:X2}{c.G:X2}{c.B:X2}";
        return UserSettings.DefaultBackgroundColor;
    }

    private static bool TryParseColor(string input, out Color color)
    {
        color = Color.CornflowerBlue;
        input = (input ?? "").Trim().TrimStart('#');
        if (input.Length == 3)
            input = string.Concat(input[0], input[0], input[1], input[1], input[2], input[2]);
        if (input.Length != 6) return false;
        try
        {
            color = Color.FromArgb(Convert.ToInt32(input[..2], 16),
                                   Convert.ToInt32(input.Substring(2, 2), 16),
                                   Convert.ToInt32(input.Substring(4, 2), 16));
            return true;
        }
        catch { return false; }
    }
}