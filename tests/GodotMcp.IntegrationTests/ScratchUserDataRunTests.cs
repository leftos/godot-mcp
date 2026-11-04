using System.Text.Json;
using System.Text.Json.Nodes;
using GodotMcp.IntegrationTests.Fixtures;
using GodotMcp.Server.Session;
using GodotMcp.Server.Tools;

namespace GodotMcp.IntegrationTests;

/// <summary>
/// run_project's options.userData: a seeded scratch run's game finds its user:// inside the server's and session's folder
/// under .godot/godot-mcp (<see cref="ScratchUserData.RootFor"/>), with the seed copied in, keeps what it wrote across a restart, and the folder
/// goes when the session stops, the real user data folder never written.
/// </summary>
public sealed class ScratchUserDataRunTests : IAsyncDisposable
{
    private const int ScriptTimeoutMs = 10_000;

    private readonly ProbeProject _probe = new();
    private readonly SessionHarness _harness = new();
    private readonly ProjectTools _project;
    private readonly RuntimeTools _runtime;

    public ScratchUserDataRunTests()
    {
        _project = new ProjectTools(_harness.Sessions);
        _runtime = new RuntimeTools(_harness.Sessions, TestCSharp.Unused());
    }

    public async ValueTask DisposeAsync()
    {
        await _harness.DisposeAsync();
        _probe.Dispose();
    }

    [Fact(Timeout = TestTimeouts.OwnLaunchMs)]
    public async Task ASeededScratchRunKeepsItsUserDataAcrossARestartAndLosesItAtStop()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "The real user data folder checked is the Windows one, under APPDATA.");
        CancellationToken cancellation = TestContext.Current.CancellationToken;
        string seed = Path.Combine(_probe.Directory, "seed-data");
        Directory.CreateDirectory(seed);
        File.WriteAllText(Path.Combine(seed, "seed.txt"), "seeded");
        // A name of this run's own, so a real file left by any earlier run cannot pass or fail the check.
        string probeName = $"probe-{Guid.NewGuid():N}.txt";
        string realProbe = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "Godot",
            "app_userdata",
            "InputProbe",
            probeName
        );
        string root = ScratchUserData.RootFor(ProjectPaths.Normalise(_probe.Directory), "InputProbe");

        string launched = await _project.RunProjectAsync(
            _probe.Directory,
            options: new RunOptions(UserData: JsonSerializer.SerializeToElement(new { seed = "seed-data" })),
            cancellationToken: cancellation
        );
        string userDataDir = JsonNode.Parse(launched)!["userDataDir"]!.GetValue<string>();
        JsonNode first = await RunAsync(
            "var seeded := FileAccess.get_file_as_string(\"user://seed.txt\")\n"
                + $"\tvar file := FileAccess.open(\"user://{probeName}\", FileAccess.WRITE)\n"
                + "\tfile.store_string(\"kept\")\n"
                + "\tfile.close()\n"
                + "\treturn [OS.get_user_data_dir(), seeded]"
        );
        await _project.RestartProjectAsync(session: null, cancellationToken: cancellation);
        JsonNode restarted = await RunAsync($"return FileAccess.get_file_as_string(\"user://{probeName}\")");
        bool rootWhileRunning = Directory.Exists(root);

        await _project.StopProjectAsync(session: null, cancellationToken: cancellation);

        Assert.Equal(userDataDir, first[0]!.GetValue<string>());
        Assert.StartsWith(root.Replace('\\', '/') + "/", userDataDir, StringComparison.OrdinalIgnoreCase);
        Assert.Contains($"/.godot/godot-mcp/userdata-{Environment.ProcessId}-InputProbe-", userDataDir, StringComparison.Ordinal);
        Assert.Equal("seeded", first[1]!.GetValue<string>());
        Assert.Equal("kept", restarted.GetValue<string>());
        Assert.True(rootWhileRunning);
        Assert.False(Directory.Exists(root));
        Assert.False(File.Exists(realProbe));
    }

    [Fact(Timeout = TestTimeouts.OwnLaunchMs)]
    public async Task AFeatureTaggedCustomDirIsTheOneTheGameUses()
    {
        CancellationToken cancellation = TestContext.Current.CancellationToken;
        string project = File.ReadAllText(_probe.ProjectFile);
        File.WriteAllText(
            _probe.ProjectFile,
            project.Replace(
                "[application]\n",
                "[application]\n\nconfig/use_custom_user_dir=true\nconfig/custom_user_dir_name=\"ProbePlain\"\n"
                    + "config/custom_user_dir_name.editor=\"ProbeDev\"\n",
                StringComparison.Ordinal
            )
        );
        string seed = Path.Combine(_probe.Directory, "seed-data");
        Directory.CreateDirectory(seed);
        File.WriteAllText(Path.Combine(seed, "seed.txt"), "seeded");

        string launched = await _project.RunProjectAsync(
            _probe.Directory,
            options: new RunOptions(UserData: JsonSerializer.SerializeToElement(new { seed = "seed-data" })),
            cancellationToken: cancellation
        );
        JsonNode result = JsonNode.Parse(launched)!;
        JsonNode game = await RunAsync("return [OS.get_user_data_dir(), FileAccess.get_file_as_string(\"user://seed.txt\")]");
        await _project.StopProjectAsync(session: null, cancellationToken: cancellation);

        string userDataDir = result["userDataDir"]!.GetValue<string>();
        Assert.Equal(game[0]!.GetValue<string>(), userDataDir);
        Assert.EndsWith("/ProbeDev", userDataDir, StringComparison.Ordinal);
        Assert.Equal("seeded", game[1]!.GetValue<string>());
        Assert.Null(result["warning"]);
    }

    /// <summary>Runs a script in the game and returns the value it returns.</summary>
    private async Task<JsonNode> RunAsync(string body)
    {
        string script = $"extends RefCounted\n\n\nfunc execute(scene_tree: SceneTree) -> Variant:\n\t{body}\n";
        string json = await _runtime.RunScriptAsync(script, ScriptTimeoutMs, cancellationToken: TestContext.Current.CancellationToken);
        return JsonNode.Parse(json)!["value"]!;
    }
}
