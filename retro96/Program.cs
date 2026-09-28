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

        WindowsSecurity.SweepStaleAppContainerProfiles();

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

        // Blurry UI/canvas fix: with no DPI awareness declared, Windows treats
        // this as a DPI-unaware app and bitmap-stretches the entire window to
        // match the display's scale factor (125%/150%/200% are the common
        // case on modern displays). Combined with WinForms' own AutoScaleMode
        // doing a SECOND non-integer scale on top (Form1 uses
        // AutoScaleMode.None precisely to avoid that), every pixel this
        // engine deliberately renders crisp and unsmoothed (SmoothingMode.
        // None, PixelOffsetMode.None — the whole point of the 1996 look)
        // would get smeared by stacked stretches before it reaches the
        // screen. PerMonitorV2 tells Windows not to do its own scaling at
        // all; the app then draws at native pixels and stays sharp.
        Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);

        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);

        try
        {
            Application.Run(new Form1());
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