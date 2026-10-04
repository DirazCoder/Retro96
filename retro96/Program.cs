using System;
using System.Text;
using System.Windows.Forms;

namespace Retro96;

static class Program
{
    [STAThread]
    static void Main(string[] args)
    {
        // Register legacy code-page encodings for both the browser host and
        // the isolated plugin worker.
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);

        if (Retro96.Plugins.PluginSandboxWorker.IsInvocation(args))
        {
            Retro96.Plugins.PluginSandboxWorker.RunAsync(args).GetAwaiter().GetResult();
            return;
        }

        // ISO-8859-1 and other legacy code pages are now registered above
        // before either the browser or plugin worker starts.

        Application.ThreadException += (sender, e) =>
        {
            MessageBox.Show(
                e.Exception.ToString(),
                "Unhandled UI Exception",
                MessageBoxButtons.OK,
                MessageBoxIcon.Error);
        };

        AppDomain.CurrentDomain.UnhandledException += (sender, e) =>
        {
            try
            {
                MessageBox.Show(
                    e.ExceptionObject?.ToString() ?? "Unknown error",
                    "Unhandled Exception",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Error);
            }
            catch
            {
                // No UI available during shutdown — nothing more we can do
            }
        };

        Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);

        // Select the process DPI context before creating any WinForms controls.
        // High-DPI mode is enabled by default and stored in UserSettings so
        // portable/legacy installs can explicitly opt out. PerMonitorV2 keeps
        // the Skia canvas and native shell controls in real device pixels on
        // each monitor; the compatibility mode below intentionally restores
        // Windows' legacy bitmap-scaled behaviour.
        var startupSettings = UserSettings.Load();
        Application.SetHighDpiMode(
            startupSettings.HighDpiScaleMode ? HighDpiMode.PerMonitorV2 : HighDpiMode.DpiUnaware);

        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);

        try
        {
            var browser = new Form1(startupSettings);
            browser.Shown += (_, _) =>
                _ = System.Threading.Tasks.Task.Run(
                    WindowsSecurity.SweepStaleAppContainerProfiles);
            Application.Run(browser);
        }
        catch (Exception ex)
        {
            MessageBox.Show(
                ex.ToString(),
                "Startup Error",
                MessageBoxButtons.OK,
                MessageBoxIcon.Error);
        }
    }
}