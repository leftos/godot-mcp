using System.Diagnostics;
using System.Text.Json.Nodes;
using GodotMcp.IntegrationTests.Fixtures;

namespace GodotMcp.IntegrationTests;

/// <summary>
/// --run-scratches run from the built exe against the InputProbe fixture's scratch scenes, in the real Godot: a green scene
/// exits 0 with its verdict line and the passing summary, a red one exits 1 and names the step it failed at, and --json prints
/// the tool's own result in place of the lines.
/// </summary>
public sealed class ScratchCommandRunTests : ScratchRunBase
{
    [Fact(Timeout = TestTimeouts.OwnLaunchMs)]
    public async Task AGreenSceneExitsZeroAndPrintsItsVerdictLine()
    {
        (int status, string[] output, _) = await RunCommandAsync(TestContext.Current.CancellationToken, "--scene", "ScratchGreen");

        Assert.Equal(0, status);
        Assert.NotEmpty(output);
        Assert.StartsWith("ScratchGreen: green (", output[0], StringComparison.Ordinal);
        Assert.Equal("scratch: passed (1 green, 0 known, 0 no steps)", output[^1]);
    }

    [Fact(Timeout = TestTimeouts.OwnLaunchMs)]
    public async Task ARedSceneExitsOneAndNamesTheStep()
    {
        (int status, string[] output, _) = await RunCommandAsync(TestContext.Current.CancellationToken, "--scene", "ScratchPushError");

        Assert.Equal(1, status);
        Assert.NotEmpty(output);
        Assert.StartsWith("ScratchPushError: red (", output[0], StringComparison.Ordinal);
        Assert.Contains(output, line => line.StartsWith("  failed at step ", StringComparison.Ordinal));
        Assert.StartsWith("scratch: failed (", output[^1], StringComparison.Ordinal);
    }

    [Fact(Timeout = TestTimeouts.OwnLaunchMs)]
    public async Task JsonPrintsTheToolsResultWhole()
    {
        (int status, string[] output, _) = await RunCommandAsync(TestContext.Current.CancellationToken, "--scene", "ScratchGreen", "--json");

        Assert.Equal(0, status);
        Assert.NotEmpty(output);
        JsonObject result = JsonNode.Parse(string.Join('\n', output))!.AsObject();
        Assert.True(result["passed"]!.GetValue<bool>(), result.ToJsonString());
        Assert.Equal("green", result["scenes"]!.AsArray()[0]!["verdict"]!.GetValue<string>());
    }

    /// <summary>The built exe run over the probe copy, its output and error read whole, its tree ended if it outlasts the test.</summary>
    private async Task<(int Status, string[] Output, string Error)> RunCommandAsync(CancellationToken cancellation, params string[] arguments)
    {
        ProcessStartInfo start = new(ServerPath)
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        start.ArgumentList.Add("--run-scratches");
        start.ArgumentList.Add(_probe.Directory);
        foreach (string argument in arguments)
        {
            start.ArgumentList.Add(argument);
        }

        using Process command = Process.Start(start) ?? throw new InvalidOperationException($"the server {ServerPath} did not start");
        Task<string> output = command.StandardOutput.ReadToEndAsync(cancellation);
        Task<string> error = command.StandardError.ReadToEndAsync(cancellation);
        try
        {
            await command.WaitForExitAsync(cancellation);
        }
        finally
        {
            // The games are the command's own children, so the tree is what ends them when the test gives up on it.
            if (!command.HasExited)
            {
                command.Kill(entireProcessTree: true);
            }
        }

        return (command.ExitCode, Lines(await output), await error);
    }

    private static string[] Lines(string text) => [.. text.Split('\n').Select(line => line.TrimEnd('\r')).Where(line => line.Length > 0)];

    private static string ServerPath => Path.Combine(AppContext.BaseDirectory, OperatingSystem.IsWindows() ? "godot-mcp.exe" : "godot-mcp");
}
