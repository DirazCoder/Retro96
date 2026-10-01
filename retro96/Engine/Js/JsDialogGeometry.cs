// JsDialogGeometry — the geometry contract of the JS dialogs, extracted
// from the WinForms PromptDialog construction in BrowserCanvas so the
// "prompt input squashed / too narrow to use" contract is testable
// headlessly.  The shell consumes these rects verbatim; nothing here
// touches WinForms.
//
// Contract under test (Bug_JsPromptDialogSquashed):
//   • prompt input field: at least 200px wide and 20px tall
//   • dialog stays tall enough for label + input + buttons
//   • alert()/confirm() use the OS-native MessageBox — geometry is
//     OS-rendered, so their legibility is not layout-dependent.
using Retro96.Drawing;

namespace Retro96.Engine.Js;

public readonly record struct PromptDialogLayout(
    SizeF ClientSize,
    RectangleF Label,
    RectangleF Input,
    RectangleF Ok,
    RectangleF Cancel);

public static class JsDialogGeometry
{
    public const float MinInputWidth  = 420f;
    public const float MinInputHeight = 34f;

    // The era dialog chrome, as built by BrowserCanvas.PromptDialog:
    // Large desktop prompt: generous room for the message and input while
    // keeping the controls aligned in one predictable geometry contract.
    private const float DialogWidth     = 560f;
    private const float BaseHeight      = 190f;
    private const float Margin          = 24f;
    private const float LabelLineHeight = 26f;
    private const float InputWidth      = DialogWidth - 2 * Margin;
    private const float ButtonWidth     = 100f;
    private const float ButtonHeight    = 32f;

    /// <summary>
    /// Era-approximate label measurement: ~7px per character at the 8.25pt
    /// dialog font (historical Segoe UI 8.25pt-era average metrics).
    /// The shell may pass a real measured width via <paramref name="measure"/>
    /// when it has a live Graphics; null falls back to this estimate.
    /// </summary>
    public static PromptDialogLayout PromptLayout(
        string message,
        string? defaultValue = null,
        Func<string, float>? measure = null)
    {
        float labelWidth = measure != null
            ? measure(message)
            : message.Length * 7f;

        // Label wraps into the dialog band (margin .. width-margin).
        int labelLines = Math.Max(1, (int)Math.Ceiling(labelWidth / InputWidth));

        float labelBottom = Margin + labelLines * LabelLineHeight;
        float inputY = Math.Max(68f, labelBottom + 18f);
        float clientHeight = Math.Max(BaseHeight,
            inputY + MinInputHeight + 24f + ButtonHeight + 24f);

        var size = new SizeF(DialogWidth, clientHeight);
        var label = new RectangleF(Margin, Margin, InputWidth, labelLines * LabelLineHeight);
        var input = new RectangleF(Margin, inputY, InputWidth, MinInputHeight);
        var ok = new RectangleF(DialogWidth - Margin - 2 * ButtonWidth - 8f,
                                clientHeight - ButtonHeight - 12f, ButtonWidth, ButtonHeight);
        var cancel = new RectangleF(DialogWidth - Margin - ButtonWidth,
                                    clientHeight - ButtonHeight - 12f, ButtonWidth, ButtonHeight);
        return new PromptDialogLayout(size, label, input, ok, cancel);
    }
}
