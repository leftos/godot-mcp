using GodotMcp.Server.Session;
using GodotMcp.TestSupport;

namespace GodotMcp.Tests.Session;

public sealed class OverrideFileTests : IDisposable
{
    private const string UserOverride = "[application]\nconfig/name=\"Mine\"\n";
    private readonly TempDirectory _project = new();

    public void Dispose() => _project.Dispose();

    [Fact]
    public void WriteCreatesAMarkedAutoloadForTheBridge()
    {
        string bridge = _project.Combine("tools", "bridge.gd");

        OverrideFile.Write(_project.Path, bridge, false);

        string[] lines = File.ReadAllLines(OverrideFile.PathIn(_project.Path));
        Assert.Equal(OverrideFile.Marker, lines[0]);
        Assert.Contains("[autoload]", lines);
        Assert.Contains($"GodotMcpBridge=\"*{bridge.Replace('\\', '/')}\"", lines);
    }

    [Fact]
    public void WriteKeepsJoypadsOnFocusLossByDefault() => AssertJoypadSetting(shutOutRealGamepads: false, "false");

    [Fact]
    public void WriteIgnoresUnfocusedJoypadsWhenShuttingOutRealGamepads() => AssertJoypadSetting(shutOutRealGamepads: true, "true");

    private void AssertJoypadSetting(bool shutOutRealGamepads, string expected)
    {
        OverrideFile.Write(_project.Path, _project.Combine("bridge.gd"), shutOutRealGamepads);

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

        SessionException refused = Assert.Throws<SessionException>(() => OverrideFile.Write(_project.Path, _project.Combine("bridge.gd"), false));

        Assert.Contains(path, refused.Message, StringComparison.Ordinal);
        Assert.Equal(before, File.ReadAllBytes(path));
    }

    [Fact]
    public void WriteReplacesAStaleMarkedFile()
    {
        string path = OverrideFile.PathIn(_project.Path);
        File.WriteAllText(path, $"{OverrideFile.Marker}\n[autoload]\n\nGodotMcpBridge=\"*D:/gone/old_bridge.gd\"\n");
        string bridge = _project.Combine("new_bridge.gd");

        OverrideFile.Write(_project.Path, bridge, false);

        string content = File.ReadAllText(path);
        Assert.DoesNotContain("old_bridge.gd", content, StringComparison.Ordinal);
        Assert.Contains(bridge.Replace('\\', '/'), content, StringComparison.Ordinal);
    }

    [Fact]
    public void RemoveDeletesAMarkedFile()
    {
        OverrideFile.Write(_project.Path, _project.Combine("bridge.gd"), false);

        Assert.True(OverrideFile.Remove(_project.Path));
        Assert.False(File.Exists(OverrideFile.PathIn(_project.Path)));
    }

    [Fact]
    public void RemoveLeavesAnUnmarkedFileByteIdentical()
    {
        string path = OverrideFile.PathIn(_project.Path);
        File.WriteAllText(path, UserOverride);
        byte[] before = File.ReadAllBytes(path);

        Assert.False(OverrideFile.Remove(_project.Path));
        Assert.Equal(before, File.ReadAllBytes(path));
    }

    [Fact]
    public void RemoveWithoutAFileDoesNothing() => Assert.False(OverrideFile.Remove(_project.Path));
}
