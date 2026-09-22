// Retro96.Graphics — string formatting state.
//
// GDI-shaped surface (GenericTypographic template + flags) over Skia's
// text engine.  The renderer, the layout measurer and the field overlays
// all construct the same NoWrap|MeasureTrailingSpaces typographic format,
// so measurement and painting can never disagree.
namespace Retro96.Drawing;

[Flags]
public enum StringFormatFlags
{
    None = 0,
    DirectionRightToLeft = 0x0001,
    DirectionVertical = 0x0002,
    NoFitBlackBox = 0x0004,
    NoClip = 0x4000,
    NoWrap = 0x2000,
    LineLimit = 0x0200,
    MeasureTrailingSpaces = 0x0800,
}

public enum StringTrimming
{
    None = 0,
    Character = 1,
    Word = 2,
    EllipsisCharacter = 3,
    EllipsisWord = 4,
    EllipsisPath = 5,
}

public enum StringAlignment
{
    Near = 0,
    Center = 1,
    Far = 2,
}

public sealed class StringFormat : IDisposable
{
    public StringFormatFlags FormatFlags { get; set; }
    public StringTrimming Trimming { get; set; }
    public StringAlignment Alignment { get; set; }
    public StringAlignment LineAlignment { get; set; }

    public StringFormat()
    {
    }

    /// <summary>Copy constructor — the engine's "clone GenericTypographic
    /// then tweak" pattern.</summary>
    public StringFormat(StringFormat template)
    {
        if (template != null)
        {
            FormatFlags = template.FormatFlags;
            Trimming = template.Trimming;
            Alignment = template.Alignment;
            LineAlignment = template.LineAlignment;
        }
    }

    private static StringFormat? _genericTypographic;
    private static StringFormat? _genericDefault;

    /// <summary>Tight advance metrics, no wrap, no clipping — the layout
    /// engine's measurement contract.</summary>
    public static StringFormat GenericTypographic =>
        _genericTypographic ??= new StringFormat
        {
            FormatFlags = StringFormatFlags.NoWrap | StringFormatFlags.MeasureTrailingSpaces,
            Trimming = StringTrimming.None,
            Alignment = StringAlignment.Near,
            LineAlignment = StringAlignment.Near,
        };

    public static StringFormat GenericDefault =>
        _genericDefault ??= new StringFormat();

    public void Dispose()
    {
        // Plain state object.
    }
}
