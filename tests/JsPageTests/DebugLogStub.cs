// DebugLog lives in Form1.cs (excluded here) — same API, same namespace.
namespace Retro96;

public static class DebugLog
{
    public static void Write(string message) { }
    public static void WriteException(string context, Exception ex) { }
}
