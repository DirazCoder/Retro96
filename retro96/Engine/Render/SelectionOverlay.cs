using Retro96.Drawing;

namespace Retro96.Engine.Render;

/// <summary>
/// Geometry-only helpers for page text selection. Keeping the span merge here
/// lets the shell paint the result while the actual selection contract can be
/// regression-tested without a WinForms BrowserCanvas.
/// </summary>
public static class SelectionOverlay
{
    public static List<RectangleF> MergeSpans(IEnumerable<RectangleF> spans)
    {
        var ordered = spans.ToList();
        if (ordered.Count == 0) return [];

        ordered.Sort((a, b) =>
        {
            int y = a.Y.CompareTo(b.Y);
            return y != 0 ? y : a.X.CompareTo(b.X);
        });

        var merged = new List<RectangleF>();
        RectangleF current = ordered[0];
        const float lineTolerance = 1.5f;
        const float joinTolerance = 2f;

        for (int i = 1; i < ordered.Count; i++)
        {
            var next = ordered[i];
            bool sameLine = Math.Abs(next.Y - current.Y) <= lineTolerance &&
                Math.Abs(next.Height - current.Height) <= lineTolerance;
            bool touching = next.X <= current.Right + joinTolerance;

            if (sameLine && touching)
            {
                float right = Math.Max(current.Right, next.Right);
                float top = Math.Min(current.Y, next.Y);
                float bottom = Math.Max(current.Bottom, next.Bottom);
                current = new RectangleF(current.X, top,
                    right - current.X, bottom - top);
            }
            else
            {
                merged.Add(current);
                current = next;
            }
        }

        merged.Add(current);
        return merged;
    }
}
