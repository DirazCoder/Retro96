// DebugLog lives in Form1.cs (WinForms file, excluded here) — provide the
// same API in the same namespace so the engine sources compile unchanged.
namespace Retro96;

public static class DebugLog
{
    public static void Write(string message) => Console.WriteLine("[log] " + message);
    public static void WriteException(string context, Exception ex) =>
        Console.WriteLine($"[log] {context} THREW: {ex.GetType().Name}: {ex.Message}");
}
