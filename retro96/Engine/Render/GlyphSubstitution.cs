using System.Collections.Generic;
using System.Text;

namespace Retro96.Engine.Render;

/// <summary>
/// Era-authentic glyph fallback for characters the 1996 core Windows font
/// set (MS Sans Serif, Arial, Times New Roman, Courier New) does not carry.
///
/// The engine's fonts render those code points through GDI+'s SILEENT
/// font-fallback — a foreign family (Segoe UI Symbol and friends) whose
/// ascent has nothing to do with the layout's baseline math.  Visually this
/// produced two distinct defects on real pages:
///   • missing-glyph rectangles ("tofu" boxes ▯ at nav bullets and panel
///     decorations),
///   • narrow filled fallback boxes in the current text colour — the blue
///     slivers at the end of link-styled panel titles.
/// Both vanish when the character never reaches GDI+ in the first place.
///
/// Substitution is what a 1996 machine actually showed: box-drawing and
/// geometric decorations collapse to the ASCII art of the period.  Every
/// Latin-1 and typographic-punctuation code point the core fonts DO have
/// (— – ‘ ’ “ ” • … ™ © ® ° ± × ÷ ¼ ½ ¾ ¹ ² ³) passes through untouched.
///
/// Idempotent: substitutes are plain ASCII/Latin-1, so a second pass over
/// already-mapped text is a no-op — safe to apply at both the layout
/// (TextRun creation) and paint (control labels) sides.
/// </summary>
public static class GlyphSubstitution
{
    private static readonly Dictionary<char, string> Map = new()
    {
        // ── Box drawing (U+2500–U+257F): the BBS/terminal frame charset ──
        ['─'] = "-", ['│'] = "|", ['┌'] = "+", ['┐'] = "+", ['└'] = "+", ['┘'] = "+",
        ['├'] = "+", ['┤'] = "+", ['┬'] = "+", ['┴'] = "+", ['┼'] = "+",
        ['═'] = "=", ['║'] = "|", ['╔'] = "+", ['╗'] = "+", ['╚'] = "+", ['╝'] = "+",
        ['╠'] = "+", ['╣'] = "+", ['╦'] = "+", ['╩'] = "+", ['╬'] = "+",
        ['╭'] = "+", ['╮'] = "+", ['╯'] = "+", ['╰'] = "+",
        ['▤'] = "#", ['▥'] = "#", ['▦'] = "#", ['▧'] = "%", ['▨'] = "%", ['▩'] = "#",

        // ── Block elements (U+2580–U+259F): progress bars, title slivers ──
        ['█'] = "#", ['▓'] = "%", ['▒'] = "%", ['░'] = ".",
        ['▀'] = "\"", ['▄'] = "_", ['▌'] = "|", ['▐'] = "|",
        ['▁'] = "_", ['▂'] = "_", ['▃'] = "_", ['▄'] = "_",
        ['▅'] = "=", ['▆'] = "=", ['▇'] = "#", ['█'] = "#",
        ['▉'] = "|", ['▊'] = "|", ['▋'] = "|", ['▍'] = "|", ['▎'] = "|", ['▏'] = "|",

        // ── Geometric shapes (U+25A0–U+25FF) beyond the core • ──
        ['■'] = "#", ['□'] = "[", ['▪'] = "*", ['▫'] = "o",
        ['▬'] = "=", ['▭'] = "[", ['▮'] = "|", ['▯'] = "[",
        ['▲'] = "^", ['△'] = "^", ['▼'] = "v", ['▽'] = "v",
        ['►'] = ">", ['▷'] = ">", ['◄'] = "<", ['◁'] = "<",
        ['▶'] = ">", ['◀'] = "<",                            // ▶ (25B6) — the nav-bullet workhorse
        ['▴'] = "^", ['▾'] = "v", ['◂'] = "<", ['▸'] = ">",
        ['◸'] = "^", ['◹'] = "^", ['◺'] = "v", ['◿'] = "v",
        ['◽'] = "o", ['◾'] = "*",
        ['▣'] = "[", ['▤'] = "#", ['▥'] = "#", ['▦'] = "#",
        ['▧'] = "%", ['▨'] = "%", ['▩'] = "#",
        ['◆'] = "*", ['◇'] = "o", ['◈'] = "*", ['◉'] = "O", ['○'] = "o", ['●'] = "*",
        ['◐'] = "((", ['◑'] = "))", ['◒'] = "()", ['◓'] = "()",
        ['★'] = "*", ['☆'] = "*", ['☉'] = "o", ['☻'] = ":)", ['☺'] = ":)",

        // ── Arrows (U+2190–U+21FF) beyond the core ←↑→↓ ↔ ↕ ──
        ['↻'] = "»", ['↺'] = "«", ['⇄'] = "<->", ['⇆'] = "<->",
        ['↩'] = "<-", ['↪'] = "->", ['➔'] = "->", ['➜'] = "->",
        ['➤'] = ">", ['⇒'] = "=>", ['⇐'] = "<=", ['⇑'] = "^", ['⇓'] = "v",

        // ── Miscellaneous symbols (U+2600–U+26FF) ──
        ['⚠'] = "!!", ['⚡'] = "!", ['⏱'] = "»", ['⏰'] = "»", ['⌛'] = "o", ['⏳'] = "o",
        ['♪'] = "~", ['♫'] = "~~", ['♬'] = "~~", ['☎'] = "tel", ['☏'] = "tel",
        ['☞'] = ">>", ['☜'] = "<<", ['✌'] = "V", ['✍'] = "W", ['✎'] = "W",
        ['✔'] = "v", ['✗'] = "x", ['✘'] = "x", ['✚'] = "+", ['✖'] = "x",
        ['♥'] = "H", ['♦'] = "*", ['♣'] = "*", ['♠'] = "*",
        ['⌂'] = "n", ['⌐'] = "r",
        ['☁'] = "o", ['☂'] = "*", ['☼'] = "*", ['☽'] = "(", ['☾'] = ")",
        ['ⓐ'] = "(a)", ['ⓑ'] = "(b)", ['Ⓒ'] = "(c)",

        // ── Letterlike (U+2100–U+214F) beyond ™ ──
        ['⅓'] = "1/3", ['⅔'] = "2/3", ['⅛'] = "1/8", ['⅜'] = "3/8",
        ['⅝'] = "5/8", ['⅞'] = "7/8", ['∞'] = "oo", ['≈'] = "~=",
        ['≠'] = "!=", ['≤'] = "<=", ['≥'] = ">=", ['∅'] = "0",

        // ── CJK-adjacent full-width forms that leak onto western pages ──
        ['　'] = " ",
    };

    /// <summary>
    /// Fast path: most page text is pure ASCII + Latin-1 + the typographic
    /// punctuation the core fonts carry.  One scan decides; only runs with
    /// at least one code point at or above U+2500 (plus the handful of
    /// lower exceptions) pay for the rebuild.
    /// </summary>
    public static string MapGlyphs(string text)
    {
        if (string.IsNullOrEmpty(text))
            return text;

        bool needsMapping = false;
        foreach (char c in text)
        {
            if (c < '\u2190')
            {
                // Below the arrow block everything is either core-font
                // material or handled elsewhere — except the few map
                // entries under 0x2190 (none today).
                continue;
            }
            if (Map.ContainsKey(c)) { needsMapping = true; break; }
            if (c >= '\u2190' && c <= '\u27BF') { needsMapping = true; break; }
            if (c >= '\u2E80') { needsMapping = true; break; }   // CJK etc.
        }

        if (!needsMapping)
            return text;

        var sb = new StringBuilder(text.Length + 8);
        foreach (char c in text)
        {
            if (c < '\u2190')
                sb.Append(c);
            else if (Map.TryGetValue(c, out var sub))
                sb.Append(sub);
            else if (c >= '\u2E80')
                sb.Append('?');            // genuinely foreign glyph
            else
                sb.Append(c);              // ← ↑ → ↓ ↔ etc. — core fonts carry these
        }
        return sb.ToString();
    }
}
