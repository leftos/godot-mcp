using System.Diagnostics;
using GodotMcp.Server.Session;

namespace GodotMcp.Tests.Session;

public sealed class GodotCommandLineTests
{
    private const string Project = @"C:\My Games\Probe";

    [Fact]
    public void PutsPathSceneAndEngineArgsBeforeTheSeparatorAndUserArgsAfter()
    {
        LaunchRequest request = new(Project, "res://levels/test.tscn", ["--resolution", "640x360"], ["--hello", "a b"], false, false);

        List<string> arguments = GodotCommandLine.BuildArguments(request);

        Assert.Equal(["--path", Project, "res://levels/test.tscn", "--resolution", "640x360", "--", "--hello", "a b"], arguments);
    }

    [Fact]
    public void OmitsABlankSceneAndTheSeparatorWhenThereAreNoUserArgs()
    {
        LaunchRequest request = new(Project, " ", [], [], false, false);

        List<string> arguments = GodotCommandLine.BuildArguments(request);

        Assert.Equal(["--path", Project], arguments);
    }

    [Fact]
    public void KeepsArgumentsWithSpacesWhole()
    {
        LaunchRequest request = new(Project, null, [], ["--hello", "a b"], false, false);

        ProcessStartInfo startInfo = GodotCommandLine.CreateStartInfo("godot.exe", request, new BridgeEndpoint(4321, "t0k3n"));

        Assert.Equal(["--path", Project, "--", "--hello", "a b"], startInfo.ArgumentList);
        Assert.Equal(string.Empty, startInfo.Arguments);
    }

    [Fact]
    public void PassesThePortAndTokenAndBackgroundOnlyWhenAskedInTheEnvironment()
    {
        BridgeEndpoint bridge = new(4321, "t0k3n");

        ProcessStartInfo background = GodotCommandLine.CreateStartInfo("godot.exe", new LaunchRequest(Project, null, [], [], true, false), bridge);
        ProcessStartInfo windowed = GodotCommandLine.CreateStartInfo("godot.exe", new LaunchRequest(Project, null, [], [], false, false), bridge);

        Assert.Equal("4321", background.Environment[GodotCommandLine.PortVariable]);
        Assert.Equal("t0k3n", background.Environment[GodotCommandLine.TokenVariable]);
        Assert.Equal("1", background.Environment[GodotCommandLine.BackgroundVariable]);
        Assert.False(windowed.Environment.ContainsKey(GodotCommandLine.BackgroundVariable));
    }

    [Fact]
    public void PassesShutOutRealGamepadsOnlyWhenAsked()
    {
        BridgeEndpoint bridge = new(4321, "t0k3n");

        ProcessStartInfo shutOut = GodotCommandLine.CreateStartInfo("godot.exe", new LaunchRequest(Project, null, [], [], false, true), bridge);
        ProcessStartInfo byDefault = GodotCommandLine.CreateStartInfo("godot.exe", new LaunchRequest(Project, null, [], [], false, false), bridge);

        Assert.Equal("1", shutOut.Environment[GodotCommandLine.ShutOutRealGamepadsVariable]);
        Assert.False(byDefault.Environment.ContainsKey(GodotCommandLine.ShutOutRealGamepadsVariable));
    }
}
