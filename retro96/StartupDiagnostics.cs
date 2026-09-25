using System;
using System.IO;
using System.Text;
using System.Threading;

namespace Retro96;

internal static class StartupDiagnostics
{
    private static readonly object Sync = new();
    private static string? _path;

    public static string Path
    {
        get
        {
            if (_path != null) return _path;
            lock (Sync)
            {
                if (_path != null) return _path;

                string root;
                try
                {
                    root = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
                    if (string.IsNullOrWhiteSpace(root))
                        root = System.IO.Path.GetTempPath();
                }
                catch
                {
                    root = System.IO.Path.GetTempPath();
                }

                string dir = System.IO.Path.Combine(root, "Retro96");
                try { Directory.CreateDirectory(dir); }
                catch { dir = System.IO.Path.GetTempPath(); }

                _path = System.IO.Path.Combine(dir, "startup.log");
                return _path;
            }
        }
    }

    public static void Begin(string role, string[] args)
    {
        Write(role, "START", "PID=" + Environment.ProcessId +
            " OS=" + Environment.OSVersion +
            " 64bit=" + Environment.Is64BitProcess +
            " base=" + AppContext.BaseDirectory +
            " args=" + string.Join(" | ", args));
    }

    public static void Step(string role, string message) => Write(role, "STEP", message);

    public static void Error(string role, string stage, Exception ex) =>
        Write(role, "ERROR", stage + ": " + ex);

    public static void Win32Error(string role, string stage, int error) =>
        Write(role, "WIN32", stage + ": error=" + error + " (0x" + error.ToString("X8") + ")");

    public static void Exit(string role, string message) => Write(role, "EXIT", message);

    private static void Write(string role, string kind, string message)
    {
        string line = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} [{role}] [{kind}] {message}";
        try
        {
            lock (Sync)
            {
                File.AppendAllText(Path, line + Environment.NewLine, new UTF8Encoding(false));
            }
        }
        catch
        {
            try { System.Diagnostics.Debug.WriteLine(line); } catch { }
        }
    }
}
