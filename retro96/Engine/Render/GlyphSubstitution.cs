using System.Collections.Generic;
using System.Text;

namespace Retro96.Engine.Render;

/// <summary>
/// Era-authentic glyph fallback for characters the 1996 core Windows font
/// set (MS Sans Serif, Arial, Times New Roman, Courier New) does not carry.
///
/// The engine's SkiaSharp fonts render those code points through the shared
/// font-fallback — a foreign family (Segoe UI Symbol and friends) whose
/// ascent has nothing to do with the layout's baseline math.  Visually this
/// produced two distinct defects on real pages:
///   • missing-glyph rectangles ("tofu" boxes ▯ at nav bullets and panel
///     decorations),
///   • narrow filled fallback boxes in the current text colour — the blue
///     slivers at the end of link-styled panel titles.
/// Both vanish when the character never reaches the renderer in the first place.
///
/// Substitution is what a 1996 machine actually showed: box-drawing and
/// geometric decorations collapse to the ASCII art of the period.  Every
/// Latin-1 and typographic-punctuation code point the core fonts DO have
/// (— – ‘ ’ “ ” • … ™ © ® ° ± × ÷ ¼ ½ ¾ ¹ ² ³) passes through untouched.
///
/// CJK glyphs are left intact so text-aware font resolution can select an
/// installed fallback family. Idempotent: substitutions are ordinary western
/// text or engine-private sentinels, so a second pass is safe.
/// </summary>
public static class GlyphSubstitution
{
    // Private-use sentinel consumed by the text renderer as a real round
    // bullet. Keeping it out of the selected western font avoids platform-
    // dependent missing-glyph boxes while preserving the bullet's advance.
    public const char LegacyBulletMarker = '\uE000';

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

        // Windows-1252/1990s inline bullet. The engine's resolved Skia
        // typeface can expose a .notdef box for U+2022 instead of doing
        // per-glyph font fallback, so keep this common era glyph in the
        // Latin-1-safe range that the selected western fonts reliably paint.
        ['•'] = "\uE000",
        ['\u0095'] = "\uE000",
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
            // Keep engine-private sentinels intact when MapGlyphs is called
            // twice by a control/paint path.
            if (c == LegacyBulletMarker)
            {
                continue;
            }

            // Check explicit substitutions before the fast-path cutoff.
            // U+2022 BULLET is intentionally handled here because a
            // resolved western typeface is not guaranteed to provide Skia
            // glyph fallback even when the system has a symbol font.
            if (Map.ContainsKey(c)) { needsMapping = true; break; }
            if (c < '\u2190')
            {
                continue;
            }
            if (c >= '\u2190' && c <= '\u27BF') { needsMapping = true; break; }
        }

        if (!needsMapping)
            return text;

        var sb = new StringBuilder(text.Length + 8);
        foreach (char c in text)
        {
            if (c == LegacyBulletMarker)
            {
                sb.Append(c);
            }
            else if (Map.TryGetValue(c, out var sub))
                sb.Append(sub);
            else if (c < '\u2190')
                sb.Append(c);
            else
                sb.Append(c);              // keep unlisted glyphs available for font fallback
        }
        return sb.ToString();
    }
}
