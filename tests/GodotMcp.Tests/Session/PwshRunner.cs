using GodotMcp.Server.Session;
using GodotMcp.TestSupport;
using Microsoft.Extensions.Logging.Abstractions;

namespace GodotMcp.Tests.Session;

/// <summary>Runs a pwsh child the way the server does, with the temp folder and the log the ToolProcess tests share.</summary>
internal sealed class PwshRunner : IDisposable
{
    private readonly TempDirectory _temp = new();

    // Wall time: another build loading the machine would stretch a ceiling into its backstop; LoadClockTests cover the load.
    private readonly LoadClock _wallClock = new(TimeProvider.System, new NoLoadSource());

    public string Path => _temp.Path;

    public string LogPath => _temp.Combine("logs", "tool.log");

    public LoadClock Clock => _wallClock;

    public Task<ToolProcessResult> RunPwshAsync(string script, TimeSpan ceiling) => RunPwshAsync(script, ceiling, ToolProcess.DefaultStallLimit);

    public Task<ToolProcessResult> RunPwshAsync(string script, TimeSpan ceiling, TimeSpan stallLimit) =>
        ToolProcess.RunAsync(
            new ToolProcessRequest("pwsh", ["-NoProfile", "-Command", script], _temp.Path, LogPath, ceiling)
            {
                StallLimit = stallLimit,
                Clock = _wallClock,
            },
            NullLogger.Instance,
            CancellationToken.None
        );

    public void Dispose()
    {
        _wallClock.Dispose();
        _temp.Dispose();
    }
}
