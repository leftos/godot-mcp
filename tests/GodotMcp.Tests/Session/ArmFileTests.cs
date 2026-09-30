using System.Diagnostics;
using System.Text.Json.Nodes;
using GodotMcp.Server.Session;
using GodotMcp.TestSupport;

namespace GodotMcp.Tests.Session;

/// <summary>
/// The armed.json that arms a folder: its settings and its owners. A live foreign owner is a real child process; a dead one is
/// this process's id with a start time it never had.
/// </summary>
public sealed class ArmFileTests : IDisposable
{
    private static readonly OverrideOwner Dead = new(Environment.ProcessId, OverrideOwner.Current.StartTicks - 1);
    private static readonly ArmSettings QuietShutOut = new(Quiet: true, ShutOutRealGamepads: true, Mute: false);

    private readonly TempDirectory _project = new();
    private Process? _child;

    public void Dispose()
    {
        if (_child is not null)
        {
            _child.Kill(entireProcessTree: true);
            _child.WaitForExit(10_000);
            _child.Dispose();
        }

        _project.Dispose();
    }

    [Fact]
    public void WriteCreatesTheFolderAndHoldsTheSettingsAndThisServer()
    {
        ArmFile.Write(_project.Path, QuietShutOut);

        JsonNode content = JsonNode.Parse(File.ReadAllText(_project.Combine(".godot", "godot-mcp", "armed.json")))!;
        Assert.Equal((true, true), (content["quiet"]!.GetValue<bool>(), content["shutOutRealGamepads"]!.GetValue<bool>()));
        Assert.Equal([OverrideOwner.Current.ToString()], content["owners"]!.AsArray().Select(owner => owner!.GetValue<string>()));
    }

    [Fact]
    public void WriteHoldsTheMuteAndReadGivesItBack()
    {
        ArmSettings muted = new(Quiet: false, ShutOutRealGamepads: false, Mute: true);

        ArmFile.Write(_project.Path, muted);

        JsonNode content = JsonNode.Parse(File.ReadAllText(ArmFile.PathIn(_project.Path)))!;
        Assert.Equal((false, true), (content["quiet"]!.GetValue<bool>(), content["mute"]!.GetValue<bool>()));
        Assert.Equal(muted, ArmFile.Read(_project.Path)!.Settings);
    }

    [Fact]
    public void WriteKeepsALiveOwnerAndDropsADeadOne()
    {
        OverrideOwner foreign = StartForeignOwner();
        WriteRaw($"{{\"quiet\":false,\"shutOutRealGamepads\":false,\"owners\":[\"{foreign}\",\"{Dead}\"]}}");

        ArmFile.Write(_project.Path, QuietShutOut);

        ArmedFile armed = ArmFile.Read(_project.Path)!;
        Assert.Equal([foreign, OverrideOwner.Current], armed.Owners);
        Assert.Equal(QuietShutOut, armed.Settings);
    }

    [Fact]
    public void ReleaseDeletesTheFileWhenThisServerWasItsOnlyLiveOwner()
    {
        WriteRaw($"{{\"quiet\":true,\"shutOutRealGamepads\":false,\"owners\":[\"{OverrideOwner.Current}\",\"{Dead}\"]}}");

        bool deleted = ArmFile.Release(_project.Path);

        Assert.True(deleted);
        Assert.False(File.Exists(ArmFile.PathIn(_project.Path)));
    }

    [Fact]
    public void ReleaseKeepsTheFileForAnotherLiveOwnerWithItsSettings()
    {
        OverrideOwner foreign = StartForeignOwner();
        WriteRaw($"{{\"quiet\":true,\"shutOutRealGamepads\":false,\"owners\":[\"{foreign}\",\"{OverrideOwner.Current}\"]}}");

        bool deleted = ArmFile.Release(_project.Path);

        Assert.False(deleted);
        ArmedFile armed = ArmFile.Read(_project.Path)!;
        Assert.Equal([foreign], armed.Owners);
        Assert.Equal(new ArmSettings(Quiet: true, ShutOutRealGamepads: false, Mute: false), armed.Settings);
    }

    [Fact]
    public void ReleaseWithoutAFileDoesNothing()
    {
        Assert.False(ArmFile.Release(_project.Path));
        Assert.Null(ArmFile.Read(_project.Path));
    }

    [Fact]
    public void AFileThatIsNotTheExpectedJsonReadsAsStale()
    {
        WriteRaw("{\"quiet\": \"loud\"");

        ArmedFile armed = ArmFile.Read(_project.Path)!;

        Assert.Empty(armed.Owners);
        Assert.Empty(ArmFile.LiveOwners(_project.Path));
        Assert.True(ArmFile.Release(_project.Path));
    }

    [Fact]
    public void AnEntryThatIsNotAnOwnerIsSkipped()
    {
        WriteRaw($"{{\"quiet\":false,\"shutOutRealGamepads\":false,\"owners\":[\"nonsense\",7,\"{OverrideOwner.Current}\"]}}");

        Assert.Equal([OverrideOwner.Current], ArmFile.Read(_project.Path)!.Owners);
    }

    private void WriteRaw(string content)
    {
        string path = ArmFile.PathIn(_project.Path);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
    }

    /// <summary>A child process that runs until the test ends, as the owner another live server would be.</summary>
    private OverrideOwner StartForeignOwner()
    {
        _child = Process.Start(
            new ProcessStartInfo("cmd.exe", "/c ping -n 60 127.0.0.1 >nul")
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardInput = true,
            }
        )!;
        return new OverrideOwner(_child.Id, _child.StartTime.ToUniversalTime().Ticks);
    }
}
