namespace GodotMcp.Capture;

/// <summary>
/// The part of the capture item a run copies, and the frame size it writes: the crop with an odd side padded by one
/// black column or row, since yuv420p stores one chroma sample per 2x2 block and an encoder refuses an odd size.
/// </summary>
internal static class Crop
{
    /// <summary>
    /// The crop a run uses: <paramref name="requested"/>, or the whole item when none was asked for. A crop that reaches
    /// past the item is refused with both rectangles in <paramref name="error"/>, never clamped, so a wrong rectangle from
    /// the caller shows instead of a clip of the wrong part of the window.
    /// </summary>
    public static PixelRect? Resolve(PixelRect? requested, int itemWidth, int itemHeight, out string? error)
    {
        PixelRect item = new(0, 0, itemWidth, itemHeight);
        if (requested is not { } crop)
        {
            error = null;
            return item;
        }
        if (crop.Right > item.Width || crop.Bottom > item.Height)
        {
            error = $"--crop {crop} reaches outside the capture item, which is {itemWidth}x{itemHeight} ({item}); a crop must lie within it.";
            return null;
        }
        error = null;
        return crop;
    }

    public static int RoundUpToEven(int value) => value + (value & 1);

    /// <summary>The frame width a run writes for <paramref name="crop"/>: its width, rounded up to even.</summary>
    public static int OutputWidth(PixelRect crop) => RoundUpToEven(crop.Width);

    /// <summary>The frame height a run writes for <paramref name="crop"/>: its height, rounded up to even.</summary>
    public static int OutputHeight(PixelRect crop) => RoundUpToEven(crop.Height);

    /// <summary>What <c>--probe</c> prints: the frame size a run writes, as <c>WxH</c>.</summary>
    public static string ProbeLine(PixelRect crop) => FormattableString.Invariant($"{OutputWidth(crop)}x{OutputHeight(crop)}");
}
