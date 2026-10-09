using System.Globalization;

namespace GodotMcp.Capture;

/// <summary>A rectangle in the capture item's physical pixels: its top-left corner and its size.</summary>
internal readonly record struct PixelRect(int X, int Y, int Width, int Height)
{
    /// <summary>The right edge, one past the last column, without overflow.</summary>
    public long Right => (long)X + Width;

    /// <summary>The bottom edge, one past the last row, without overflow.</summary>
    public long Bottom => (long)Y + Height;

    /// <summary>
    /// Reads <c>x,y,w,h</c>: four decimals, the corner at or above zero and the size above zero. False for anything else.
    /// </summary>
    public static bool TryParse(string text, out PixelRect rect)
    {
        rect = default;
        string[] parts = text.Split(',');
        if (parts.Length != 4)
        {
            return false;
        }
        int[] values = new int[4];
        for (int i = 0; i < parts.Length; i++)
        {
            if (!int.TryParse(parts[i], NumberStyles.None, CultureInfo.InvariantCulture, out values[i]))
            {
                return false;
            }
        }
        if (values[2] == 0 || values[3] == 0)
        {
            return false;
        }
        rect = new PixelRect(values[0], values[1], values[2], values[3]);
        return true;
    }

    /// <summary>The <c>x,y,w,h</c> form <see cref="TryParse"/> reads.</summary>
    public override string ToString() => FormattableString.Invariant($"{X},{Y},{Width},{Height}");
}
