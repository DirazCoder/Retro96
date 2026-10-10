// DebugLog lives in Form1.cs (WinForms file, excluded here) — provide the
// same API in the same namespace so the engine sources compile unchanged.
namespace Retro96;

public static class DebugLog
{
    public static readonly bool Enabled = false;
    public static void Write(string message) => Console.WriteLine("[log] " + message);
    public static void WritePerformanceOnce(string key, string message) { }
    public static void WriteException(string context, Exception ex) =>
        Console.WriteLine($"[log] {context} THREW: {ex.GetType().Name}: {ex.Message}");
    public static readonly bool JsEnabled = false;
    public static readonly bool JsTraceEnabled = false;
    public static void JsWrite(string message) { }
}
