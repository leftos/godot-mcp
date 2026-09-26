using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using GodotMcp.Server.Session;

namespace GodotMcp.Tests.Session;

/// <summary>The command line and the environment block a quiet run is started with on the hidden desktop.</summary>
[SupportedOSPlatform("windows")]
public sealed class DesktopProcessTests
{
    private const string Godot = @"C:\Program Files\Godot\Godot_console.exe";
    private const string WindowsOnly = "A quiet run starts on a hidden desktop only on Windows.";

    [Theory]
    [InlineData("plain")]
    [InlineData(@"C:\My Games\Probe")]
    [InlineData("a b\tc")]
    [InlineData("say \"hi\"")]
    [InlineData("\"")]
    [InlineData(@"C:\folder\")]
    [InlineData(@"C:\folder with space\")]
    [InlineData(@"ends\\")]
    [InlineData(@"a\""b")]
    [InlineData(@"a\\""b c")]
    [InlineData(@"\\server\share\")]
    [InlineData(@"mid\dle")]
    [InlineData("")]
    [InlineData(" ")]
    public void TheCommandLineParsesBackToTheArguments(string argument)
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), WindowsOnly);
        string[] arguments = ["--path", argument, "--", argument, "last"];

        string commandLine = DesktopProcess.BuildCommandLine(Godot, arguments);

        Assert.Equal([Godot, .. arguments], Parse(commandLine));
    }

    [Fact]
    public void AnEmptyListIsTheFileAlone()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), WindowsOnly);
        string commandLine = DesktopProcess.BuildCommandLine(Godot, []);

        Assert.Equal([Godot], Parse(commandLine));
    }

    [Fact]
    public void TheEnvironmentBlockIsSortedWithoutRegardToCaseAndEndsInTwoNuls()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), WindowsOnly);
        Dictionary<string, string?> environment = new(StringComparer.OrdinalIgnoreCase)
        {
            ["b"] = "2",
            ["C"] = "3",
            ["A"] = "1",
            ["_x"] = "4",
        };

        string block = DesktopProcess.BuildEnvironmentBlock(environment);

        // Windows compares names upper-cased, so '_' (0x5F) sorts after the letters.
        Assert.Equal("A=1\0b=2\0C=3\0_x=4\0\0", block);
    }

    [Fact]
    public void TheEnvironmentBlockCarriesTheBridgeAndDropsTheFlagsThatAreOff()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), WindowsOnly);
        string? quiet = Environment.GetEnvironmentVariable(GodotCommandLine.QuietVariable);
        string? shutOut = Environment.GetEnvironmentVariable(GodotCommandLine.ShutOutRealGamepadsVariable);
        Environment.SetEnvironmentVariable(GodotCommandLine.QuietVariable, "1");
        Environment.SetEnvironmentVariable(GodotCommandLine.ShutOutRealGamepadsVariable, "1");
        try
        {
            string on = BlockFor(quiet: true, shutOutRealGamepads: true);
            string off = BlockFor(quiet: false, shutOutRealGamepads: false);

            Assert.Contains($"\0{GodotCommandLine.PortVariable}=4321\0", "\0" + off, StringComparison.Ordinal);
            Assert.Contains($"\0{GodotCommandLine.TokenVariable}=t0k3n\0", "\0" + off, StringComparison.Ordinal);
            Assert.Contains($"\0{GodotCommandLine.QuietVariable}=1\0", "\0" + on, StringComparison.Ordinal);
            Assert.Contains($"\0{GodotCommandLine.ShutOutRealGamepadsVariable}=1\0", "\0" + on, StringComparison.Ordinal);
            Assert.DoesNotContain($"\0{GodotCommandLine.QuietVariable}=", "\0" + off, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain($"\0{GodotCommandLine.ShutOutRealGamepadsVariable}=", "\0" + off, StringComparison.OrdinalIgnoreCase);
            Assert.EndsWith("\0\0", off, StringComparison.Ordinal);
        }
        finally
        {
            Environment.SetEnvironmentVariable(GodotCommandLine.QuietVariable, quiet);
            Environment.SetEnvironmentVariable(GodotCommandLine.ShutOutRealGamepadsVariable, shutOut);
        }
    }

    private static string BlockFor(bool quiet, bool shutOutRealGamepads)
    {
        LaunchRequest request = new(@"C:\My Games\Probe", null, [], [], quiet, shutOutRealGamepads, Prepare: false);
        ProcessStartInfo startInfo = GodotCommandLine.CreateStartInfo(Godot, request, new BridgeEndpoint(4321, "t0k3n"), moviePath: null);
        return DesktopProcess.BuildEnvironmentBlock(startInfo.Environment);
    }

    /// <summary>The arguments Windows' own parser reads from <paramref name="commandLine"/>, the file first.</summary>
    private static string[] Parse(string commandLine)
    {
        nint argv = CommandLineToArgvW(commandLine, out int count);
        Assert.NotEqual(0, argv);
        try
        {
            return [.. Enumerable.Range(0, count).Select(index => Marshal.PtrToStringUni(Marshal.ReadIntPtr(argv, index * nint.Size)) ?? "")];
        }
        finally
        {
            _ = LocalFree(argv);
        }
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern nint CommandLineToArgvW(string commandLine, out int count);

    [DllImport("kernel32.dll")]
    private static extern nint LocalFree(nint memory);
}
