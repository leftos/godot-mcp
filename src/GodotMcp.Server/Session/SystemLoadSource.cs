using System.Runtime.InteropServices;

namespace GodotMcp.Server.Session;

/// <summary>The machine's CPU times from <c>GetSystemTimes</c>, and the server's own work from <see cref="OwnWork.CpuTicks"/>.</summary>
internal sealed partial class SystemLoadSource : ILoadSource
{
    public int Processors => Environment.ProcessorCount;

    public LoadCounters? Read()
    {
        if (!GetSystemTimes(out long idle, out long kernel, out long user))
        {
            int error = Marshal.GetLastPInvokeError();
            Log.LoadSampleFailed(Log.Ambient, error);
            return null;
        }

        return new LoadCounters(idle, kernel, user, OwnWork.CpuTicks());
    }

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetSystemTimes(out long idle, out long kernel, out long user);
}
