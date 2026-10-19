using GodotMcp.Server.Session;
using GodotMcp.Server.Tools;
using GodotMcp.TestSupport;
using ModelContextProtocol;

namespace GodotMcp.Tests.Tools;

/// <summary>run_process's argument checks and the command it assembles; no process starts here.</summary>
public sealed class RunProcessValidationTests : IDisposable
{
    private readonly TempDirectory _temp = new();

    public void Dispose() => _temp.Dispose();

    private string FilePath => _temp.Combine(ProjectProfile.FileName);

    private const string Declares = """
        {
          "processes": {
            "server": {
              "command": ["dotnet", "run", "--project", "../Delve.Server"],
              "cwd": ".",
              "env": {"Delve__DataDir": "../../.tmp/drive-runs"},
              "description": "the game server; pass --port <n> as args"
            },
            "ticker": {
              "command": ["pwsh", "-NoProfile", "-File", "ticker.ps1"]
            }
          }
        }
        """;

    [Fact]
    public void AProjectWithNoProfileIsRefused()
    {
        McpException refused = Assert.Throws<McpException>(() => ProcessTools.LoadProfile(_temp.Path));

        Assert.Equal(
            $"No godot-mcp.json beside project.godot in {_temp.Path}: run_process starts only the processes its \"processes\" " + "section declares.",
            refused.Message
        );
    }

    [Fact]
    public void AnEmptyProjectPathIsRefused()
    {
        McpException refused = Assert.Throws<McpException>(() => ProcessTools.LoadProfile(""));

        Assert.Equal("projectPath is empty. Pass the folder that holds the project's project.godot.", refused.Message);
    }

    [Fact]
    public void AnUnknownProcessNamesWhatTheFileDeclares()
    {
        Write(Declares);
        ProjectProfile profile = ProcessTools.LoadProfile(_temp.Path);

        McpException refused = Assert.Throws<McpException>(() => ProcessTools.ProcessOf(profile, "srv"));

        Assert.Equal($"{FilePath} has no process 'srv'; it declares: server (the game server; pass --port <n> as args), ticker.", refused.Message);
    }

    [Fact]
    public void AnEmptyProcessesSectionSaysItDeclaresNone()
    {
        Write("""{ "processes": {} }""");
        ProjectProfile profile = ProcessTools.LoadProfile(_temp.Path);

        McpException refused = Assert.Throws<McpException>(() => ProcessTools.ProcessOf(profile, "srv"));

        Assert.Equal($"{FilePath} has no process 'srv'; it declares: none.", refused.Message);
    }

    [Fact]
    public void AKnownProcessIsFound()
    {
        Write(Declares);
        ProjectProfile profile = ProcessTools.LoadProfile(_temp.Path);

        ProcessProfile server = ProcessTools.ProcessOf(profile, "server");

        Assert.Equal(["dotnet", "run", "--project", "../Delve.Server"], server.Command);
        Assert.Equal("the game server; pass --port <n> as args", server.Description);
        Assert.Null(ProcessTools.ProcessOf(profile, "ticker").Description);
    }

    [Theory]
    [InlineData("my-server", "my-server")]
    [InlineData("My Server", "My_Server")]
    [InlineData("server", "server")]
    public void ADefaultSessionIsTheProcessKeyFittedToTheNameRule(string process, string expected) =>
        Assert.Equal(expected, ProcessTools.DefaultSession(process));

    [Fact]
    public void ArgsAreAppendedAfterTheCommand()
    {
        Write(Declares);
        ProcessProfile server = ProcessTools.ProcessOf(ProcessTools.LoadProfile(_temp.Path), "server");

        string[] command = ProcessTools.CommandFor(server, _temp.Path, ["--port", "5000"]);

        Assert.Equal(["dotnet", "run", "--project", "../Delve.Server", "--port", "5000"], command);
    }

    [Fact]
    public void AProgramWithAFolderInItResolvesAgainstTheProjectFolder()
    {
        Write("""{ "processes": { "server": { "command": ["./tools/server.exe", "-x"] } } }""");
        ProcessProfile server = ProcessTools.ProcessOf(ProcessTools.LoadProfile(_temp.Path), "server");

        string[] command = ProcessTools.CommandFor(server, _temp.Path, []);

        Assert.Equal(Path.GetFullPath(Path.Combine(_temp.Path, "tools", "server.exe")), command[0]);
        Assert.Equal([command[0], "-x"], command);
    }

    [Fact]
    public void ABareProgramIsLeftForThePathToFind()
    {
        Write(Declares);
        ProcessProfile server = ProcessTools.ProcessOf(ProcessTools.LoadProfile(_temp.Path), "server");

        string[] command = ProcessTools.CommandFor(server, _temp.Path, []);

        Assert.Equal("dotnet", command[0]);
    }

    private void Write(string json) => File.WriteAllText(FilePath, json);
}
