using System.Diagnostics;
using GodotMcp.Server.Session;
using GodotMcp.Server.Tools;

namespace GodotMcp.Tests.Session;

public sealed class GodotCommandLineTests
{
    private const string Project = @"C:\My Games\Probe";

    [Fact]
    public void PutsPathSceneAndEngineArgsBeforeTheSeparatorAndUserArgsAfter()
    {
        LaunchRequest request = new(Project, "res://levels/test.tscn", ["--resolution", "640x360"], ["--hello", "a b"], false, false, Prepare: true);

        List<string> arguments = GodotCommandLine.BuildArguments(request);

        Assert.Equal(["--path", Project, "res://levels/test.tscn", "--resolution", "640x360", "--", "--hello", "a b"], arguments);
    }

    [Fact]
    public void ADefaultRunChoosesTheDummyAudioDriverBeforeTheEngineArgs()
    {
        RunOptions defaults = new();
        LaunchRequest request = new(Project, null, ["--audio-driver", "WASAPI"], [], defaults.Quiet, defaults.ShutOutRealGamepads, Prepare: true);

        List<string> arguments = GodotCommandLine.BuildArguments(request);

        Assert.Equal(["--path", Project, "--audio-driver", "Dummy", "--audio-driver", "WASAPI"], arguments);
    }

    [Fact]
    public void ANotQuietRunLeavesTheAudioDriverAlone()
    {
        LaunchRequest request = new(Project, null, [], [], false, false, Prepare: true);

        List<string> arguments = GodotCommandLine.BuildArguments(request);

        Assert.DoesNotContain("--audio-driver", arguments);
    }

    [Fact]
    public void OmitsABlankSceneAndTheSeparatorWhenThereAreNoUserArgs()
    {
        LaunchRequest request = new(Project, " ", [], [], false, false, Prepare: true);

        List<string> arguments = GodotCommandLine.BuildArguments(request);

        Assert.Equal(["--path", Project], arguments);
    }

    [Fact]
    public void KeepsArgumentsWithSpacesWhole()
    {
        LaunchRequest request = new(Project, null, [], ["--hello", "a b"], false, false, Prepare: true);

        ProcessStartInfo startInfo = GodotCommandLine.CreateStartInfo("godot.exe", request, new BridgeEndpoint(4321, "t0k3n"));

        Assert.Equal(["--path", Project, "--", "--hello", "a b"], startInfo.ArgumentList);
        Assert.Equal(string.Empty, startInfo.Arguments);
    }

    [Fact]
    public void PassesThePortAndTokenInTheEnvironment()
    {
        BridgeEndpoint bridge = new(4321, "t0k3n");

        ProcessStartInfo startInfo = GodotCommandLine.CreateStartInfo(
            "godot.exe",
            new LaunchRequest(Project, null, [], [], true, false, Prepare: true),
            bridge
        );

        Assert.Equal("4321", startInfo.Environment[GodotCommandLine.PortVariable]);
        Assert.Equal("t0k3n", startInfo.Environment[GodotCommandLine.TokenVariable]);
    }

    [Fact]
    public void RunsQuietByDefault()
    {
        BridgeEndpoint bridge = new(4321, "t0k3n");
        RunOptions defaults = new();

        ProcessStartInfo startInfo = GodotCommandLine.CreateStartInfo(
            "godot.exe",
            new LaunchRequest(Project, null, [], [], defaults.Quiet, defaults.ShutOutRealGamepads, Prepare: true),
            bridge
        );

        Assert.Equal("1", startInfo.Environment[GodotCommandLine.QuietVariable]);
    }

    [Fact]
    public void RemovesTheQuietVariableWhenNotQuiet()
    {
        BridgeEndpoint bridge = new(4321, "t0k3n");

        ProcessStartInfo startInfo = GodotCommandLine.CreateStartInfo(
            "godot.exe",
            new LaunchRequest(Project, null, [], [], false, false, Prepare: true),
            bridge
        );

        Assert.False(startInfo.Environment.ContainsKey(GodotCommandLine.QuietVariable));
    }

    [Fact]
    public void PassesShutOutRealGamepadsOnlyWhenAsked()
    {
        BridgeEndpoint bridge = new(4321, "t0k3n");

        ProcessStartInfo shutOut = GodotCommandLine.CreateStartInfo(
            "godot.exe",
            new LaunchRequest(Project, null, [], [], false, true, Prepare: true),
            bridge
        );
        ProcessStartInfo byDefault = GodotCommandLine.CreateStartInfo(
            "godot.exe",
            new LaunchRequest(Project, null, [], [], false, false, Prepare: true),
            bridge
        );

        Assert.Equal("1", shutOut.Environment[GodotCommandLine.ShutOutRealGamepadsVariable]);
        Assert.False(byDefault.Environment.ContainsKey(GodotCommandLine.ShutOutRealGamepadsVariable));
    }
}
