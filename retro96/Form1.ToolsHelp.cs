using System.Drawing;

namespace Retro96;

public partial class Form1
{
    private void BuildToolsMenu()
    {
        ToolStripDropDown menu = (_pluginToolsMenu ??= new ToolStripDropDownButton("Tools")).DropDown;
        menu.Items.Clear();
        menu.Items.Add("Page Inspector").Click += (_, _) => new PageInspector(_canvas, null).Show(this);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Reload Without Cache").Click += (_, _) =>
        {
            _imageCache.Clear();
            _resourceLoader?.ClearHistory();
            Reload();
        };
        menu.Items.Add("Clear Browser Data…").Click += (_, _) =>
        {
            if (MessageBox.Show(this, "Clear browsing history, cookies, and cached page resources?", "Retro96", MessageBoxButtons.YesNo, MessageBoxIcon.Question, MessageBoxDefaultButton.Button2) != DialogResult.Yes) return;
            _historyStore.Clear();
            _cookieStore.ClearAll();
            _imageCache.Clear();
            _resourceLoader?.ClearHistory();
            BuildHistoryMenu();
            if (_currentPageUrl != null) Reload();
        };
    }

    private void BuildHelpMenu()
    {
        ToolStripDropDown menu = (_pluginHelpMenu ??= new ToolStripDropDownButton("Help")).DropDown;
        menu.Items.Clear();
        menu.Items.Add("Keyboard Shortcuts").Click += (_, _) => ShowKeyboardShortcuts();
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("About Retro96").Click += (_, _) => ShowAboutDialog();
    }

    private void ShowKeyboardShortcuts()
    {
        MessageBox.Show(this,
            "Enter          Navigate or search\n" +
            "Ctrl+F         Find in page\n" +
            "F3 / Shift+F3  Find next / previous\n" +
            "Ctrl+Plus/Minus Zoom in / out\n" +
            "Ctrl+0         Reset zoom\n" +
            "Ctrl+J         Open downloads\n" +
            "F11            Toggle box outlines\n" +
            "F12            Write layout diagnostics",
            "Retro96 Keyboard Shortcuts", MessageBoxButtons.OK, MessageBoxIcon.Information);
    }

    private void ShowAboutDialog()
    {
        using var dialog = new Form
        {
            Text = "About Retro96",
            StartPosition = FormStartPosition.CenterParent,
            FormBorderStyle = FormBorderStyle.FixedDialog,
            ClientSize = new Size(440, 500),
            MaximizeBox = false,
            MinimizeBox = false,
            ShowInTaskbar = false
        };

        using Stream? logoStream = typeof(Form1).Assembly.GetManifestResourceStream("Retro96.assets.logo.png");
        if (logoStream == null)
            throw new InvalidOperationException("The embedded Retro96 logo resource was not found.");
        using var decodedLogo = System.Drawing.Image.FromStream(logoStream);
        var logo = new Bitmap(decodedLogo);

        var layout = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 6,
            Padding = new Padding(24, 20, 24, 16)
        };
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 190));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 48));

        var logoView = new PictureBox
        {
            Image = logo,
            SizeMode = PictureBoxSizeMode.Zoom,
            Size = new Size(190, 170),
            Anchor = AnchorStyles.None,
            Margin = new Padding(0, 0, 0, 12)
        };
        dialog.Disposed += (_, _) => logo.Dispose();
        layout.Controls.Add(logoView, 0, 0);

        layout.Controls.Add(new Label
        {
            Text = "Retro96",
            Dock = DockStyle.Fill,
            TextAlign = ContentAlignment.MiddleCenter,
            Font = new Font(Font.FontFamily, 18, FontStyle.Bold),
            AutoSize = true,
            Margin = new Padding(0, 4, 0, 12)
        }, 0, 1);

        layout.Controls.Add(new Label
        {
            Text = "A hand-built browser for the web as it was in 1996, with classic HTML and CSS, JavaScript and VBScript, and a built-in Java applet runtime. Made for exploring the early web and keeping its sites working.",
            Dock = DockStyle.Fill,
            TextAlign = ContentAlignment.TopCenter,
            Font = Font,
            Margin = new Padding(4, 8, 4, 12)
        }, 0, 2);

        var repositoryLink = new LinkLabel
        {
            Text = "https://github.com/DirazCoder/Retro96",
            Dock = DockStyle.Fill,
            TextAlign = ContentAlignment.MiddleCenter,
            AutoSize = true,
            Margin = new Padding(0, 4, 0, 8)
        };
        repositoryLink.LinkClicked += (_, _) =>
        {
            try
            {
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(repositoryLink.Text)
                {
                    UseShellExecute = true
                })?.Dispose();
            }
            catch (Exception ex)
            {
                MessageBox.Show(dialog, $"Could not open the Retro96 repository in your default browser.\n\n{ex.Message}",
                    "Retro96", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        };
        layout.Controls.Add(repositoryLink, 0, 3);

        layout.Controls.Add(new Label
        {
            Text = "© DirazCoder 2026",
            Dock = DockStyle.Fill,
            TextAlign = ContentAlignment.MiddleCenter,
            ForeColor = SystemColors.GrayText,
            AutoSize = true,
            Margin = new Padding(0, 8, 0, 8)
        }, 0, 4);

        var buttons = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            FlowDirection = FlowDirection.RightToLeft,
            WrapContents = false
        };
        var closeButton = new Button
        {
            Text = "Close",
            DialogResult = DialogResult.OK,
            AutoSize = true,
            Anchor = AnchorStyles.Right
        };
        buttons.Controls.Add(closeButton);
        layout.Controls.Add(buttons, 0, 5);

        dialog.Controls.Add(layout);
        dialog.AcceptButton = closeButton;
        dialog.CancelButton = closeButton;
        dialog.ShowDialog(this);
    }
}
