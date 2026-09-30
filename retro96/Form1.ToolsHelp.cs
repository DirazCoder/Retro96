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
        MessageBox.Show(this,
            "Retro96\n\nA 1996-era HTML 3.2 / CSS1 / ES3 browser engine.\n\n" +
            "Engine version " + typeof(Form1).Assembly.GetName().Version?.ToString() +
            "\n\nBuilt-in tools include the DOM inspector, cache controls, browser data management, and plugin support.",
            "About Retro96", MessageBoxButtons.OK, MessageBoxIcon.Information);
    }
}
