using System.Diagnostics;
using System.Globalization;
using GodotMcp.Server.Session;
using GodotMcp.TestSupport;

namespace GodotMcp.Tests.Session;

public sealed class OverrideFileTests : IDisposable
{
    private const string UserOverride = "[application]\nconfig/name=\"Mine\"\n";

    /// <summary>An owner that has exited: this process's id with a start time it never had.</summary>
    private static readonly OverrideOwner Dead = new(Environment.ProcessId, OverrideOwner.Current.StartTicks - 1);

    private readonly TempDirectory _project = new();

    public void Dispose() => _project.Dispose();

    [Fact]
    public void WriteCreatesAMarkedAutoloadForTheBridge()
    {
        string bridge = _project.Combine("tools", "bridge.gd");

        OverrideFile.Write(_project.Path, bridge, false, false, unfocused: false);

        string[] lines = File.ReadAllLines(OverrideFile.PathIn(_project.Path));
        Assert.Equal(OverrideFile.Marker, lines[0]);
        Assert.Contains("[autoload]", lines);
        Assert.Contains($"GodotMcpBridge=\"*{bridge.Replace('\\', '/')}\"", lines);
    }

    [Fact]
    public void WriteKeepsJoypadsOnFocusLossByDefault() => AssertJoypadSetting(shutOutRealGamepads: false, "false");

    [Fact]
    public void WriteIgnoresUnfocusedJoypadsWhenShuttingOutRealGamepads() => AssertJoypadSetting(shutOutRealGamepads: true, "true");

    [Fact]
    public void WriteCreatesTheWindowUnfocusedAndOffScreenWhenQuiet()
    {
        OverrideFile.Write(_project.Path, _project.Combine("bridge.gd"), false, quiet: true, unfocused: false);

        string[] lines = File.ReadAllLines(OverrideFile.PathIn(_project.Path));
        int section = Array.IndexOf(lines, "[display]");
        Assert.True(section > 0, string.Join('\n', lines));
        Assert.Equal(
            ["", "window/size/no_focus=true", "window/size/initial_position_type=0", "window/size/initial_position=Vector2i(-9999, -9999)"],
            lines[(section + 1)..]
        );
    }

    [Fact]
    public void WriteCreatesTheWindowUnfocusedAndMinimizedInPlaceWhenUnfocusedAndNotQuiet()
    {
        OverrideFile.Write(_project.Path, _project.Combine("bridge.gd"), false, quiet: false, unfocused: true);

        string[] lines = File.ReadAllLines(OverrideFile.PathIn(_project.Path));
        int section = Array.IndexOf(lines, "[display]");
        Assert.True(section > 0, string.Join('\n', lines));
        Assert.Equal(["", "window/size/no_focus=true", "window/size/mode=1"], lines[(section + 1)..]);
    }

    [Fact]
    public void WriteLeavesTheWindowAloneWhenNotQuiet()
    {
        OverrideFile.Write(_project.Path, _project.Combine("bridge.gd"), false, quiet: false, unfocused: false);

        string[] lines = File.ReadAllLines(OverrideFile.PathIn(_project.Path));
        Assert.DoesNotContain("[display]", lines);
        Assert.DoesNotContain(lines, line => line.StartsWith("window/", StringComparison.Ordinal));
    }

    private void AssertJoypadSetting(bool shutOutRealGamepads, string expected)
    {
        OverrideFile.Write(_project.Path, _project.Combine("bridge.gd"), shutOutRealGamepads, false, unfocused: false);

        string[] lines = File.ReadAllLines(OverrideFile.PathIn(_project.Path));
        int section = Array.IndexOf(lines, "[input_devices]");
        int setting = Array.IndexOf(lines, $"joypads/ignore_joypad_on_unfocused_application={expected}");
        Assert.Equal(OverrideFile.Marker, lines[0]);
        Assert.True(section > 0, string.Join('\n', lines));
        Assert.True(setting > section, string.Join('\n', lines));
        Assert.DoesNotContain(lines[(section + 1)..setting], line => line.StartsWith('['));
    }

    [Fact]
    public void WriteRefusesAnUnmarkedFileAndLeavesItByteIdentical()
    {
        string path = OverrideFile.PathIn(_project.Path);
        File.WriteAllText(path, UserOverride);
        byte[] before = File.ReadAllBytes(path);

        SessionException refused = Assert.Throws<SessionException>(() =>
            OverrideFile.Write(_project.Path, _project.Combine("bridge.gd"), false, false, unfocused: false)
        );

        Assert.Contains(path, refused.Message, StringComparison.Ordinal);
        Assert.Equal(before, File.ReadAllBytes(path));
    }

    [Fact]
    public void WriteReplacesAStaleMarkedFile()
    {
        string path = OverrideFile.PathIn(_project.Path);
        File.WriteAllText(path, $"{OverrideFile.Marker}\n[autoload]\n\nGodotMcpBridge=\"*D:/gone/old_bridge.gd\"\n");
        string bridge = _project.Combine("new_bridge.gd");

        OverrideFile.Write(_project.Path, bridge, false, false, unfocused: false);

        string content = File.ReadAllText(path);
        Assert.DoesNotContain("old_bridge.gd", content, StringComparison.Ordinal);
        Assert.Contains(bridge.Replace('\\', '/'), content, StringComparison.Ordinal);
    }

    [Fact]
    public void ReleaseDeletesAMarkedFile()
    {
        OverrideFile.Write(_project.Path, _project.Combine("bridge.gd"), false, false, unfocused: false);

        Assert.True(OverrideFile.Release(_project.Path));
        Assert.False(File.Exists(OverrideFile.PathIn(_project.Path)));
    }

    [Fact]
    public void ReleaseLeavesAnUnmarkedFileByteIdentical()
    {
        string path = OverrideFile.PathIn(_project.Path);
        File.WriteAllText(path, UserOverride);
        byte[] before = File.ReadAllBytes(path);

        Assert.False(OverrideFile.Release(_project.Path));
        Assert.Equal(before, File.ReadAllBytes(path));
    }

    [Fact]
    public void ReleaseWithoutAFileDoesNothing() => Assert.False(OverrideFile.Release(_project.Path));

    [Fact]
    public void RemoveIfStaleDeletesAMarkedFileWhoseOwnersHaveExited()
    {
        WriteMarked($"{OverrideFile.OwnersPrefix}{Dead}\n");

        Assert.Equal(new LeftoverRemoval(LeftoverState.Removed), OverrideFile.RemoveIfStale(_project.Path));
        Assert.False(File.Exists(OverrideFile.PathIn(_project.Path)));
    }

    [Fact]
    public void RemoveIfStaleDeletesAMarkedFileWithoutAnOwnersLine()
    {
        WriteMarked("");

        Assert.Equal(new LeftoverRemoval(LeftoverState.Removed), OverrideFile.RemoveIfStale(_project.Path));
        Assert.False(File.Exists(OverrideFile.PathIn(_project.Path)));
    }

    [Fact]
    public void RemoveIfStaleKeepsAMarkedFileALiveOwnerNames()
    {
        Process foreign = RegistryHarness.StartStandInGame();
        try
        {
            WriteMarked($"{OverrideFile.OwnersPrefix}{Dead},{new OverrideOwner(foreign.Id, foreign.StartTime.ToUniversalTime().Ticks)}\n");

            LeftoverRemoval removal = OverrideFile.RemoveIfStale(_project.Path);

            Assert.Equal(new LeftoverRemoval(LeftoverState.LiveOwner, foreign.Id.ToString(CultureInfo.InvariantCulture)), removal);
            Assert.True(File.Exists(OverrideFile.PathIn(_project.Path)));
        }
        finally
        {
            RegistryHarness.EndStandInGame(foreign);
        }
    }

    [Fact]
    public void RemoveIfStaleKeepsAMarkedFileWithAnOwnerItCannotRead()
    {
        WriteMarked($"{OverrideFile.OwnersPrefix}{Dead},v2:host/1234\n");

        Assert.Equal(new LeftoverRemoval(LeftoverState.UnreadableOwner, "v2:host/1234"), OverrideFile.RemoveIfStale(_project.Path));
        Assert.True(File.Exists(OverrideFile.PathIn(_project.Path)));
    }

    [Fact]
    public void RemoveIfStaleLeavesAnUnmarkedFileByteIdentical()
    {
        string path = OverrideFile.PathIn(_project.Path);
        File.WriteAllText(path, UserOverride);
        byte[] before = File.ReadAllBytes(path);

        Assert.Equal(new LeftoverRemoval(LeftoverState.NotOurs), OverrideFile.RemoveIfStale(_project.Path));
        Assert.Equal(before, File.ReadAllBytes(path));
    }

    [Fact]
    public void RemoveIfStaleKeepsAFileAnotherHandleHoldsOpen()
    {
        WriteMarked($"{OverrideFile.OwnersPrefix}{Dead}\n");
        string path = OverrideFile.PathIn(_project.Path);

        using (new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
        {
            Assert.Equal(new LeftoverRemoval(LeftoverState.InUse), OverrideFile.RemoveIfStale(_project.Path));
        }

        Assert.True(File.Exists(path));
    }

    [Fact]
    public void RemoveIfStaleLeavesAFileWrittenWhileItWasJudged()
    {
        WriteMarked($"{OverrideFile.OwnersPrefix}{Dead}\n");
        string path = OverrideFile.PathIn(_project.Path);
        OverrideOwner otherDead = new(Environment.ProcessId, OverrideOwner.Current.StartTicks - 2);
        long judgedLength = new FileInfo(path).Length;

        LeftoverRemoval removal = LeftoverFile.RemoveIfStale(
            path,
            text =>
            {
                File.WriteAllText(path, text.Replace(Dead.ToString(), otherDead.ToString(), StringComparison.Ordinal));
                return null;
            }
        );

        Assert.Equal(new LeftoverRemoval(LeftoverState.Changed), removal);
        Assert.Equal(judgedLength, new FileInfo(path).Length);
        Assert.Contains(otherDead.ToString(), File.ReadAllText(path), StringComparison.Ordinal);
    }

    [Fact]
    public void RemoveIfStaleDeletesWhileItsHandleRefusesAWrite()
    {
        WriteMarked($"{OverrideFile.OwnersPrefix}{Dead}\n");
        string path = OverrideFile.PathIn(_project.Path);
        bool writeRefused = false;

        LeftoverRemoval removal = LeftoverFile.RemoveIfStale(
            path,
            _ => null,
            () =>
            {
                Assert.Throws<IOException>(() => File.WriteAllText(path, "a peer's write\n"));
                writeRefused = true;
            }
        );

        Assert.Equal(new LeftoverRemoval(LeftoverState.Removed), removal);
        Assert.True(writeRefused, "the delete ran while its handle was open");
        Assert.False(File.Exists(path));
    }

    [Fact]
    public void RemoveIfStaleLetsAnotherReaderOpenAFileItKeepsWhileItJudges()
    {
        WriteMarked($"{OverrideFile.OwnersPrefix}{OverrideOwner.Current}\n");
        string path = OverrideFile.PathIn(_project.Path);

        LeftoverRemoval removal = LeftoverFile.RemoveIfStale(
            path,
            _ =>
            {
                using FileStream reader = new(path, FileMode.Open, FileAccess.Read, FileShare.Read);
                return LeftoverFile.KeepReason([OverrideOwner.Current.ToString()]);
            }
        );

        Assert.Equal(new LeftoverRemoval(LeftoverState.OwnedHere), removal);
        Assert.True(File.Exists(path));
    }

    [Fact]
    public void RemoveIfStaleWithoutAFileFindsNothing() =>
        Assert.Equal(new LeftoverRemoval(LeftoverState.Absent), OverrideFile.RemoveIfStale(_project.Path));

    /// <summary>Writes a marked file whose owners line is <paramref name="ownersLine"/> (empty for none) and a bridge autoload.</summary>
    private void WriteMarked(string ownersLine) =>
        File.WriteAllText(
            OverrideFile.PathIn(_project.Path),
            $"{OverrideFile.Marker}\n{ownersLine}[autoload]\n\nGodotMcpBridge=\"*D:/gone/bridge.gd\"\n"
        );
}
