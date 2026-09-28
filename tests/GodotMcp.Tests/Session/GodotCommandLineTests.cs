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

        List<string> arguments = GodotCommandLine.BuildArguments(request, moviePath: null);

        Assert.Equal(["--path", Project, "res://levels/test.tscn", "--resolution", "640x360", "--", "--hello", "a b"], arguments);
    }

    [Fact]
    public void ADefaultRunChoosesTheDummyAudioDriverBeforeTheEngineArgs()
    {
        LaunchRequest request = ProjectProfile.Empty(Project).Merge(null, [], ["--audio-driver", "WASAPI"], new RunOptions()).Request;

        List<string> arguments = GodotCommandLine.BuildArguments(request, moviePath: null);

        Assert.Equal(["--path", Project, "--audio-driver", "Dummy", "--audio-driver", "WASAPI"], arguments);
    }

    [Fact]
    public void ANotQuietRunLeavesTheAudioDriverAlone()
    {
        LaunchRequest request = new(Project, null, [], [], false, false, Prepare: true);

        List<string> arguments = GodotCommandLine.BuildArguments(request, moviePath: null);

        Assert.DoesNotContain("--audio-driver", arguments);
    }

    [Fact]
    public void ARecordingRunWritesAMovieAtSixtyFpsWithACap()
    {
        const string movie = @"C:\My Games\Probe\.godot\godot-mcp\recordings\20260926-120000-000-Probe.avi";
        LaunchRequest request = ProjectProfile
            .Empty(Project)
            .Merge("res://main.tscn", ["--smoke"], ["--resolution", "640x360"], new RunOptions(Record: true))
            .Request;

        List<string> arguments = GodotCommandLine.BuildArguments(request, movie);

        Assert.Equal(
            [
                "--path",
                Project,
                "res://main.tscn",
                "--audio-driver",
                "Dummy",
                "--write-movie",
                movie,
                "--fixed-fps",
                "60",
                "--quit-after",
                "36000",
                "--resolution",
                "640x360",
                "--",
                "--smoke",
            ],
            arguments
        );
    }

    [Fact]
    public void ARecordingWithHeadlessIsRefused()
    {
        LaunchRequest headless = ProjectProfile.Empty(Project).Merge(null, [], ["--headless"], new RunOptions(Record: true)).Request;
        LaunchRequest notRecording = headless with { Record = false };

        SessionException refused = Assert.Throws<SessionException>(() => GodotCommandLine.RefuseUnrecordable(headless));
        GodotCommandLine.RefuseUnrecordable(notRecording);

        Assert.Equal(
            "options.record cannot record a --headless run: the headless renderer draws nothing. Drop --headless; a quiet run is already hidden.",
            refused.Message
        );
    }

    [Theory]
    [InlineData(
        "--display-driver",
        "headless",
        "options.record cannot record a --display-driver headless run: the headless renderer draws nothing. Drop --display-driver "
            + "headless; a quiet run is already hidden."
    )]
    [InlineData(
        "--fixed-fps",
        "30",
        "options.record sets --fixed-fps itself (60, which record_mark's frames and the clip cuts rely on); drop it from engineArgs or "
            + "godot-mcp.json."
    )]
    [InlineData(
        "--write-movie",
        "mine.avi",
        "options.record sets --write-movie itself (the movie under .godot/godot-mcp/recordings/, which stop_project finalises and "
            + "cuts); drop it from engineArgs or godot-mcp.json."
    )]
    [InlineData(
        "--quit-after",
        "100",
        "options.record sets --quit-after itself (36000 frames, the 10 minute cap); drop it from engineArgs or godot-mcp.json."
    )]
    public void ARecordingRefusesTheEngineArgumentsItSetsItself(string flag, string value, string refusal)
    {
        LaunchRequest recording = ProjectProfile
            .Empty(Project)
            .Merge(null, [], ["--resolution", "640x360", flag, value], new RunOptions(Record: true))
            .Request;

        SessionException refused = Assert.Throws<SessionException>(() => GodotCommandLine.RefuseUnrecordable(recording));
        GodotCommandLine.RefuseUnrecordable(recording with { Record = false });

        Assert.Equal(refusal, refused.Message);
    }

    [Fact]
    public void ARecordingAcceptsAnotherDisplayDriver()
    {
        LaunchRequest recording = ProjectProfile
            .Empty(Project)
            .Merge(null, [], ["--display-driver", "windows"], new RunOptions(Record: true))
            .Request;

        GodotCommandLine.RefuseUnrecordable(recording);
    }

    [Fact]
    public void OmitsABlankSceneAndTheSeparatorWhenThereAreNoUserArgs()
    {
        LaunchRequest request = new(Project, " ", [], [], false, false, Prepare: true);

        List<string> arguments = GodotCommandLine.BuildArguments(request, moviePath: null);

        Assert.Equal(["--path", Project], arguments);
    }

    [Fact]
    public void KeepsArgumentsWithSpacesWhole()
    {
        LaunchRequest request = new(Project, null, [], ["--hello", "a b"], false, false, Prepare: true);

        ProcessStartInfo startInfo = GodotCommandLine.CreateStartInfo("godot.exe", request, new BridgeEndpoint(4321, "t0k3n"), moviePath: null);

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
            bridge,
            moviePath: null
        );

        Assert.Equal("4321", startInfo.Environment[GodotCommandLine.PortVariable]);
        Assert.Equal("t0k3n", startInfo.Environment[GodotCommandLine.TokenVariable]);
    }

    [Fact]
    public void RunsQuietByDefault()
    {
        BridgeEndpoint bridge = new(4321, "t0k3n");
        LaunchRequest defaults = ProjectProfile.Empty(Project).Merge(null, [], [], new RunOptions()).Request;

        ProcessStartInfo startInfo = GodotCommandLine.CreateStartInfo("godot.exe", defaults, bridge, moviePath: null);

        Assert.Equal("1", startInfo.Environment[GodotCommandLine.QuietVariable]);
    }

    [Fact]
    public void RemovesTheQuietVariableWhenNotQuiet()
    {
        BridgeEndpoint bridge = new(4321, "t0k3n");

        ProcessStartInfo startInfo = GodotCommandLine.CreateStartInfo(
            "godot.exe",
            new LaunchRequest(Project, null, [], [], false, false, Prepare: true),
            bridge,
            moviePath: null
        );

        Assert.False(startInfo.Environment.ContainsKey(GodotCommandLine.QuietVariable));
    }

    [Fact]
    public void TellsTheBridgeItIsOnTheHiddenDesktopOnlyForAQuietWindowsRun()
    {
        BridgeEndpoint bridge = new(4321, "t0k3n");

        ProcessStartInfo quiet = GodotCommandLine.CreateStartInfo(
            "godot.exe",
            new LaunchRequest(Project, null, [], [], true, false, Prepare: true),
            bridge,
            moviePath: null
        );
        ProcessStartInfo shown = GodotCommandLine.CreateStartInfo(
            "godot.exe",
            new LaunchRequest(Project, null, [], [], false, false, Prepare: true),
            bridge,
            moviePath: null
        );

        Assert.Equal(OperatingSystem.IsWindows(), GodotCommandLine.UsesHiddenDesktop(quiet: true));
        Assert.False(GodotCommandLine.UsesHiddenDesktop(quiet: false));
        Assert.Equal(OperatingSystem.IsWindows(), quiet.Environment.ContainsKey(GodotCommandLine.HiddenDesktopVariable));
        if (OperatingSystem.IsWindows())
        {
            Assert.Equal("1", quiet.Environment[GodotCommandLine.HiddenDesktopVariable]);
        }

        Assert.False(shown.Environment.ContainsKey(GodotCommandLine.HiddenDesktopVariable));
    }

    [Fact]
    public void PassesShutOutRealGamepadsOnlyWhenAsked()
    {
        BridgeEndpoint bridge = new(4321, "t0k3n");

        ProcessStartInfo shutOut = GodotCommandLine.CreateStartInfo(
            "godot.exe",
            new LaunchRequest(Project, null, [], [], false, true, Prepare: true),
            bridge,
            moviePath: null
        );
        ProcessStartInfo byDefault = GodotCommandLine.CreateStartInfo(
            "godot.exe",
            new LaunchRequest(Project, null, [], [], false, false, Prepare: true),
            bridge,
            moviePath: null
        );

        Assert.Equal("1", shutOut.Environment[GodotCommandLine.ShutOutRealGamepadsVariable]);
        Assert.False(byDefault.Environment.ContainsKey(GodotCommandLine.ShutOutRealGamepadsVariable));
    }
}
