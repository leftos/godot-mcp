using System.Runtime.InteropServices;

namespace GodotMcp.Server.Session;

/// <summary>A rectangle in physical pixels: its top-left corner and its size.</summary>
internal readonly record struct PixelRect(int X, int Y, int Width, int Height)
{
    public int Right => X + Width;

    public int Bottom => Y + Height;

    /// <summary>Whether <paramref name="inner"/> lies wholly within this rectangle; edges may touch.</summary>
    public bool Contains(PixelRect inner) => inner.X >= X && inner.Y >= Y && inner.Right <= Right && inner.Bottom <= Bottom;

    /// <summary>The rectangle as messages name it: <c>(x,y wxh)</c>.</summary>
    public override string ToString() => FormattableString.Invariant($"({X},{Y} {Width}x{Height})");
}

/// <summary>
/// What <see cref="WindowRect.Read"/> found for a window, every rectangle in physical pixels on the virtual desktop: its
/// extended frame (what a capture of the window covers), its client area's top-left corner and size, whether it is
/// minimized, and the virtual desktop's bounds.
/// </summary>
internal sealed record WindowReading(
    PixelRect Frame,
    (int X, int Y) ClientOrigin,
    (int Width, int Height) ClientSize,
    bool Minimized,
    PixelRect Desktop
);

/// <summary>
/// The rectangles a real-time recording crops by: the game window's client area within its extended frame, which is what
/// a capture of the window covers, and the size the capture helper writes for that crop. The arithmetic is pure;
/// <see cref="Read"/> reads the rectangles of a live window on Windows.
/// </summary>
internal static partial class WindowRect
{
    /// <summary><c>DWMWA_EXTENDED_FRAME_BOUNDS</c>: the window's bounds as the compositor draws them, in physical pixels.</summary>
    private const uint ExtendedFrameBounds = 9;

    /// <summary><c>DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE_V2</c>.</summary>
    private const nint PerMonitorAwareV2 = -4;

    private const int VirtualScreenX = 76;
    private const int VirtualScreenY = 77;
    private const int VirtualScreenWidth = 78;
    private const int VirtualScreenHeight = 79;

    /// <summary>
    /// The client area's place within the frame: its corner relative to the frame's top-left, and its size.
    /// </summary>
    /// <exception cref="SessionException">
    /// The client area is empty (a side below 1) or does not lie wholly within the frame; both rectangles are named.
    /// </exception>
    public static PixelRect Crop(PixelRect frameBounds, (int X, int Y) clientOrigin, (int Width, int Height) clientSize)
    {
        PixelRect client = new(clientOrigin.X, clientOrigin.Y, clientSize.Width, clientSize.Height);
        if (client.Width < 1 || client.Height < 1)
        {
            throw new SessionException($"the game's client area {client} is empty, so there is nothing to crop from its window frame {frameBounds}.");
        }

        if (!frameBounds.Contains(client))
        {
            throw new SessionException(
                $"the game's client area {client} does not lie within its window frame {frameBounds}, so it cannot be cropped from it."
            );
        }

        return client with
        {
            X = client.X - frameBounds.X,
            Y = client.Y - frameBounds.Y,
        };
    }

    /// <summary>
    /// The client area's place within the frame for a window read by <see cref="Read"/>: a minimized window is refused, since
    /// Windows composes no picture of it and its client rect reads empty.
    /// </summary>
    /// <exception cref="SessionException">The window is minimized, or its client area is empty or outside its frame.</exception>
    public static PixelRect Crop(WindowReading reading)
    {
        if (reading.Minimized)
        {
            throw new SessionException(
                "The game's window is minimized, so Windows composes no picture of it; restore it, then record_mark start again."
            );
        }

        return Crop(reading.Frame, reading.ClientOrigin, reading.ClientSize);
    }

    /// <summary>
    /// The frame size the capture helper writes for <paramref name="crop"/>: each side rounded up to even, since yuv420p
    /// stores one chroma sample per 2x2 block; the helper pads an odd side with one black column or row.
    /// </summary>
    public static (int Width, int Height) OutputSize(PixelRect crop) => (RoundUpToEven(crop.Width), RoundUpToEven(crop.Height));

    /// <summary>
    /// The warning for a client area whose screen rectangle lies partly outside the desktop, naming both rectangles; null
    /// when it lies within it. Only the client's own rectangle is checked, so a frame border reaching past the desktop's
    /// edge does not warn.
    /// </summary>
    public static string? DescribeOffDesktop(PixelRect client, PixelRect desktop) =>
        desktop.Contains(client)
            ? null
            : $"the game's window {client} lies partly outside the desktop {desktop}; "
                + "the parts outside it keep their first picture and do not update.";

    /// <summary>
    /// Reads a live window's extended frame, client area, minimized state and the virtual desktop's bounds, all in physical
    /// pixels: every read runs with the calling thread made per-monitor DPI aware (v2), its previous awareness restored
    /// after, so a DPI-unaware server does not get user32's 96-DPI virtualized values beside the compositor's physical
    /// frame. Scaling above 100% is unverified: only 100% has been measured.
    /// </summary>
    /// <exception cref="SessionException">
    /// Not on Windows; the window no longer exists; or a Win32 call failed, named with its error code.
    /// </exception>
    public static WindowReading Read(long hwnd)
    {
        if (!OperatingSystem.IsWindows())
        {
            throw new SessionException("Reading a window's rectangles needs Windows.");
        }

        nint previous = SetThreadDpiAwarenessContext(PerMonitorAwareV2);
        if (previous == 0)
        {
            throw new SessionException("the thread's DPI awareness could not be set (invalid DPI context).");
        }

        try
        {
            return ReadAware((nint)hwnd, hwnd);
        }
        finally
        {
            SetThreadDpiAwarenessContext(previous);
        }
    }

    private static WindowReading ReadAware(nint handle, long hwnd)
    {
        if (!IsWindow(handle))
        {
            throw new SessionException($"window {hwnd} no longer exists.");
        }

        bool minimized = IsIconic(handle);
        int result = DwmGetWindowAttribute(handle, ExtendedFrameBounds, out Rect frame, (uint)Marshal.SizeOf<Rect>());
        if (result != 0)
        {
            throw new SessionException(
                FormattableString.Invariant(
                    $"DwmGetWindowAttribute(DWMWA_EXTENDED_FRAME_BOUNDS) failed for window {hwnd} with HRESULT 0x{result:X8}."
                )
            );
        }

        Point origin = default;
        if (!ClientToScreen(handle, ref origin))
        {
            throw Failure(nameof(ClientToScreen), hwnd, Marshal.GetLastPInvokeError());
        }

        if (!GetClientRect(handle, out Rect client))
        {
            throw Failure(nameof(GetClientRect), hwnd, Marshal.GetLastPInvokeError());
        }

        PixelRect desktop = new(
            GetSystemMetrics(VirtualScreenX),
            GetSystemMetrics(VirtualScreenY),
            GetSystemMetrics(VirtualScreenWidth),
            GetSystemMetrics(VirtualScreenHeight)
        );
        PixelRect frameBounds = new(frame.Left, frame.Top, frame.Right - frame.Left, frame.Bottom - frame.Top);
        return new WindowReading(frameBounds, (origin.X, origin.Y), (client.Right - client.Left, client.Bottom - client.Top), minimized, desktop);
    }

    private static int RoundUpToEven(int value) => value + (value & 1);

    private static SessionException Failure(string call, long hwnd, int error) =>
        new($"{call} failed for window {hwnd} with Win32 error {error} ({Marshal.GetPInvokeErrorMessage(error)}).");

    [LibraryImport("user32.dll")]
    private static partial nint SetThreadDpiAwarenessContext(nint dpiContext);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool IsWindow(nint window);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool IsIconic(nint window);

    [LibraryImport("dwmapi.dll")]
    private static partial int DwmGetWindowAttribute(nint window, uint attribute, out Rect value, uint size);

    [LibraryImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool ClientToScreen(nint window, ref Point point);

    [LibraryImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetClientRect(nint window, out Rect rect);

    [LibraryImport("user32.dll")]
    private static partial int GetSystemMetrics(int index);

    [StructLayout(LayoutKind.Sequential)]
    private struct Rect
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Point
    {
        public int X;
        public int Y;
    }
}
