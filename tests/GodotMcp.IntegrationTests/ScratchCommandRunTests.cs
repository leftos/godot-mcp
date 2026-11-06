using System.Diagnostics;
using System.Text.Json.Nodes;
using GodotMcp.IntegrationTests.Fixtures;

namespace GodotMcp.IntegrationTests;

/// <summary>
/// --run-scratches run from the built exe against the InputProbe fixture's scratch scenes, in the real Godot: a green scene
/// exits 0 with its verdict line and the passing summary, a red one exits 1 and names the step it failed at, --json prints
/// the tool's own result in place of the lines, --steps plays only the steps it names, --batch plays each block in its own
/// game as its own result entry, --ceiling kills a scene past the seconds it names, and a --windowed --capture run names the
/// folder holding its shots.
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

    [Fact(Timeout = TestTimeouts.OwnLaunchMs)]
    public async Task StepsPlaysOnlyTheStepTheCommandSelects()
    {
        (int status, string[] output, string error) = await RunCommandAsync(
            TestContext.Current.CancellationToken,
            "--scene",
            "ScratchTwoRed",
            "--steps",
            "calm",
            "--json"
        );

        Assert.True(status == 0, error);
        JsonObject result = JsonNode.Parse(string.Join('\n', output))!.AsObject();
        Assert.True(result["passed"]!.GetValue<bool>(), result.ToJsonString());
        JsonObject scene = result["scenes"]!.AsArray()[0]!.AsObject();
        Assert.Equal("green", scene["verdict"]!.GetValue<string>());
        Assert.Equal([0], scene["selected"]!.AsArray().Select(index => index!.GetValue<int>()));
        Assert.Equal(1, scene["steps"]!["played"]!.GetValue<int>());
        Assert.Equal(["0 calm"], scene["stepList"]!.AsArray().Select(step => step!.GetValue<string>()));
    }

    [Fact(Timeout = TestTimeouts.OwnLaunchMs)]
    public async Task ABatchPlaysEachBlockInItsOwnGameAsItsOwnEntry()
    {
        (int status, string[] output, string error) = await RunCommandAsync(
            TestContext.Current.CancellationToken,
            "--scene",
            "ScratchTwoRed",
            "--batch",
            "ScratchTwoRed=calm",
            "--batch",
            "ScratchTwoRed=0,last",
            "--json"
        );

        Assert.True(status == 0, error);
        JsonObject result = JsonNode.Parse(string.Join('\n', output))!.AsObject();
        JsonArray scenes = result["scenes"]!.AsArray();
        Assert.Equal(2, scenes.Count);
        foreach (JsonNode? node in scenes)
        {
            Assert.Equal(2, node!["batches"]!.GetValue<int>());
            Assert.Equal("green", node["verdict"]!.GetValue<string>());
        }

        JsonObject first = scenes.Single(scene => scene!["batch"]!.GetValue<int>() == 0)!.AsObject();
        JsonObject second = scenes.Single(scene => scene!["batch"]!.GetValue<int>() == 1)!.AsObject();
        Assert.Equal([0], first["selected"]!.AsArray().Select(index => index!.GetValue<int>()));
        Assert.Equal([0, 3], second["selected"]!.AsArray().Select(index => index!.GetValue<int>()));
    }

    [Fact(Timeout = TestTimeouts.OwnLaunchMs)]
    public async Task ACeilingGivenOnTheCommandLineKillsTheScenePastIt()
    {
        (int status, string[] output, string error) = await RunCommandAsync(
            TestContext.Current.CancellationToken,
            "--scene",
            "ScratchGreen",
            "--pace",
            "2",
            "--ceiling",
            "ScratchGreen=0.05"
        );

        Assert.True(status == 1, error);
        Assert.True(output.Length > 0, error);
        Assert.StartsWith("ScratchGreen: killed (", output[0], StringComparison.Ordinal);
        Assert.Contains(
            output,
            line =>
                line.StartsWith("  failed at step 0 ", StringComparison.Ordinal) && line.Contains("set by options.ceilings", StringComparison.Ordinal)
        );
    }

    [Fact(Timeout = TestTimeouts.OwnLaunchMs)]
    public async Task AWindowedCaptureRunNamesTheFolderHoldingTheShots()
    {
        (int status, string[] output, string error) = await RunCommandAsync(
            TestContext.Current.CancellationToken,
            "--scene",
            "ScratchGreen",
            "--windowed",
            "--capture"
        );

        Assert.True(status == 0, error);
        string line = Assert.Single(output, candidate => candidate.StartsWith("  shots: ", StringComparison.Ordinal));
        string folder = line["  shots: ".Length..];
        Assert.True(Directory.Exists(folder), $"{folder} does not exist: {error}");
        Assert.NotEmpty(Directory.GetFiles(folder, "*.png"));
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
