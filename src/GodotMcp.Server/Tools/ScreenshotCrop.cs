using System.ComponentModel;

namespace GodotMcp.Server.Tools;

/// <summary>
/// The rectangle of a screenshot to keep, in viewport coordinates from the viewport's top-left corner; the bridge maps it to the
/// captured frame's pixels.
/// </summary>
internal sealed record ScreenshotCrop(
    [property: Description("The left edge, in viewport coordinates from the viewport's left.")] int X,
    [property: Description("The top edge, in viewport coordinates from the viewport's top.")] int Y,
    [property: Description("The width, in viewport coordinates; at least 1.")] int Width,
    [property: Description("The height, in viewport coordinates; at least 1.")] int Height
);

/// <summary>What <c>take_screenshot</c> returns besides the saved file's path and size.</summary>
internal enum ScreenshotMode
{
    PathOnly,
    Preview,
    Full,
}

/// <summary>The files the bridge saved for one screenshot, with native paths; the preview only when one was made.</summary>
internal sealed record ScreenshotFiles(string Path, int Width, int Height, string? PreviewPath, int? PreviewWidth, int? PreviewHeight);
