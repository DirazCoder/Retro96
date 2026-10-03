using System;
using System.Windows.Forms;

namespace Retro96;

static class Program
{
    [STAThread]
    static void Main()
    {
        // Catch exceptions thrown on the UI thread (e.g. during Form construction)
        Application.ThreadException += (sender, e) =>
        {
            MessageBox.Show(
                e.Exception.ToString(),
                "Unhandled UI Exception",
                MessageBoxButtons.OK,
                MessageBoxIcon.Error);
        };

        // Catch exceptions thrown on background threads
        AppDomain.CurrentDomain.UnhandledException += (sender, e) =>
        {
            MessageBox.Show(
                e.ExceptionObject?.ToString() ?? "Unknown error",
                "Unhandled Exception",
                MessageBoxButtons.OK,
                MessageBoxIcon.Error);
        };

        Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
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