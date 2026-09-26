using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace GodotMcp.Server.Session;

/// <summary>
/// The desktop quiet runs start on: one per server process, named <c>godot-mcp-&lt;server pid&gt;</c> in the interactive
/// window station, created by the first quiet launch. A process started there puts every window of it and its children on
/// it, so none shows on the user's desktop. Its handle is held for the server's lifetime and closed by the system at exit.
/// </summary>
[SupportedOSPlatform("windows")]
internal static partial class HiddenDesktop
{
    private const uint GenericAll = 0x10000000;
    private static readonly Lock CreateLock = new();
    private static readonly string DesktopName = $"godot-mcp-{Environment.ProcessId}";
    private static nint _handle;

    /// <summary>The desktop's name within <c>WinSta0</c>, creating the desktop on first use.</summary>
    /// <exception cref="SessionException"><c>CreateDesktopW</c> failed.</exception>
    public static string Name
    {
        get
        {
            lock (CreateLock)
            {
                if (_handle == 0)
                {
                    _handle = Create();
                }
            }

            return DesktopName;
        }
    }

    private static nint Create()
    {
        nint handle = CreateDesktopW(DesktopName, 0, 0, 0, GenericAll, 0);
        if (handle == 0)
        {
            int error = Marshal.GetLastPInvokeError();
            throw new SessionException(
                $"The hidden desktop {DesktopName} for quiet runs could not be created: CreateDesktopW failed with Win32 error {error} "
                    + $"({Marshal.GetPInvokeErrorMessage(error)}). Pass options.quiet false to run on the user's desktop."
            );
        }

        return handle;
    }

    [LibraryImport("user32.dll", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    private static partial nint CreateDesktopW(string desktop, nint device, nint devMode, uint flags, uint desiredAccess, nint securityAttributes);
}
