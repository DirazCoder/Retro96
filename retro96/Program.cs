using System;
using System.Text;
using System.Windows.Forms;

namespace Retro96;

static class Program
{
    [STAThread]
    static void Main()
    {
        // ISO-8859-1 and friends are NOT included in .NET Core/5+ by
        // default — without this registration every GetEncoding("iso-8859-1")
        // call throws and no 1996 page would ever decode.
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);

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