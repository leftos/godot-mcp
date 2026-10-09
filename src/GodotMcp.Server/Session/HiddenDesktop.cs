using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace GodotMcp.Server.Session;

/// <summary>
/// The desktop quiet runs start on: one per server process, named <c>godot-mcp-&lt;server pid&gt;</c>, created by the first
/// quiet launch in the server's own window station (<c>WinSta0</c> in an interactive session, a service station under ssh or
/// a service), which is the only one <c>CreateDesktopW</c> creates in. A process started there puts every window of it and
/// its children on it, so none shows on the user's desktop. Its handle is held for the server's lifetime and closed by the
/// system at exit.
/// </summary>
[SupportedOSPlatform("windows")]
internal static partial class HiddenDesktop
{
    private const uint GenericAll = 0x10000000;
    private const int UoiName = 2;
    private const int StationNameCapacity = 256;
    private static readonly Lock CreateLock = new();
    private static readonly string DesktopName = $"godot-mcp-{Environment.ProcessId}";
    private static nint _handle;
    private static string _path = "";

    /// <summary>
    /// The desktop's full <c>&lt;station&gt;\&lt;desktop&gt;</c> path, as a child's <c>STARTUPINFO.lpDesktop</c> names it,
    /// creating the desktop on first use. The station is the server's own window station.
    /// </summary>
    /// <exception cref="SessionException">Reading the window station's name or <c>CreateDesktopW</c> failed.</exception>
    public static string Path
    {
        get
        {
            lock (CreateLock)
            {
                if (_handle == 0)
                {
                    _path = $@"{OwnStationName()}\{DesktopName}";
                    _handle = Create();
                }

                return _path;
            }
        }
    }

    /// <summary>The name of the window station this process runs in.</summary>
    private static string OwnStationName()
    {
        // The handle belongs to the process and must not be closed.
        nint station = GetProcessWindowStation();
        if (station == 0)
        {
            throw Failure("GetProcessWindowStation", Marshal.GetLastPInvokeError());
        }

        char[] name = new char[StationNameCapacity];
        if (!GetUserObjectInformationW(station, UoiName, name, (uint)(name.Length * sizeof(char)), out _))
        {
            throw Failure("GetUserObjectInformationW", Marshal.GetLastPInvokeError());
        }

        return new string(name, 0, Array.IndexOf(name, '\0'));
    }

    private static nint Create()
    {
        nint handle = CreateDesktopW(DesktopName, 0, 0, 0, GenericAll, 0);
        return handle != 0 ? handle : throw Failure("CreateDesktopW", Marshal.GetLastPInvokeError());
    }

    private static SessionException Failure(string call, int error) =>
        new(
            $"The hidden desktop {DesktopName} for quiet runs could not be created: {call} failed with Win32 error {error} "
                + $"({Marshal.GetPInvokeErrorMessage(error)}). Pass options.quiet false to run on the user's desktop."
        );

    [LibraryImport("user32.dll", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    private static partial nint CreateDesktopW(string desktop, nint device, nint devMode, uint flags, uint desiredAccess, nint securityAttributes);

    [LibraryImport("user32.dll", SetLastError = true)]
    private static partial nint GetProcessWindowStation();

    [LibraryImport("user32.dll", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetUserObjectInformationW(nint handle, int index, [Out] char[] buffer, uint length, out uint needed);
}
