using System.Diagnostics;
using System.Text.Json;
using GodotMcp.Server.Session;
using GodotMcp.Server.Tools;

namespace GodotMcp.Tests.Session;

/// <summary>
/// stop_project on a folder with no session clearing a dead server's leftover override.cfg and armed.json, and the strict
/// clear under the owners list it runs. A dead owner is this process's id with a start time it never had.
/// </summary>
public sealed class StopFolderLeftoverTests : IAsyncDisposable
{
    private static readonly OverrideOwner Dead = new(Environment.ProcessId, OverrideOwner.Current.StartTicks - 1);

    private readonly RegistryHarness _harness = new();

    public ValueTask DisposeAsync() => _harness.DisposeAsync();

    [Fact]
    public void ClearStaleTouchesNothingWhileTheListIsHeld()
    {
        string alpha = ProjectPaths.Normalise(_harness.Project("alpha"));
        string path = WriteMarked(alpha, Dead.ToString());
        byte[] before = File.ReadAllBytes(path);
        OverrideFolders folders = _harness.Sessions.OverrideFolders;

        LeftoverClear cleared;
        using (new FileStream(folders.ListPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None))
        {
            cleared = folders.ClearStale(alpha);
        }

        Assert.Equal((false, false), (cleared.OverrideRemoved, cleared.ArmedRemoved));
        Assert.Equal(["the owners list could not be opened (busy, or see the server's stderr), nothing removed"], cleared.Left);
        Assert.Equal(before, File.ReadAllBytes(path));
    }

    [Fact]
    public async Task StopRemovesAMarkedOverrideWhoseOwnersHaveExited()
    {
        string alpha = ProjectPaths.Normalise(_harness.Project("alpha"));
        string path = WriteMarked(alpha, Dead.ToString());

        object stopped = await _harness.Sessions.StopFolderAsync(alpha, null, TestContext.Current.CancellationToken);

        Assert.Equal(new HostStopResult(alpha, HeadlessHostStopped: false) { OverrideRemoved = true }, stopped);
        Assert.False(File.Exists(path));
    }

    [Fact]
    public async Task StopRemovesALeftoverArmFile()
    {
        string alpha = ProjectPaths.Normalise(_harness.Project("alpha"));
        string path = ArmFile.PathIn(alpha);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, $"{{\"quiet\":true,\"shutOutRealGamepads\":false,\"mute\":false,\"owners\":[\"{Dead}\"]}}");

        object stopped = await _harness.Sessions.StopFolderAsync(alpha, null, TestContext.Current.CancellationToken);

        Assert.Equal(new HostStopResult(alpha, HeadlessHostStopped: false) { ArmedRemoved = true }, stopped);
        Assert.False(File.Exists(path));
    }

    [Fact]
    public async Task StopRefusesAndLeavesTheProjectsOwnOverrideByteIdentical()
    {
        string alpha = ProjectPaths.Normalise(_harness.Project("alpha"));
        string path = OverrideFile.PathIn(alpha);
        File.WriteAllText(path, "[application]\nconfig/name=\"Mine\"\n");
        byte[] before = File.ReadAllBytes(path);

        SessionException refused = await Assert.ThrowsAsync<SessionException>(() =>
            _harness.Sessions.StopFolderAsync(alpha, null, TestContext.Current.CancellationToken)
        );

        Assert.Equal(
            $"No Godot session or warm headless host runs in {alpha}. Override.cfg is the project's own (no godot-mcp marker), left in place.",
            refused.Message
        );
        Assert.Equal(before, File.ReadAllBytes(path));
    }

    [Fact]
    public async Task StopRefusesAndKeepsAnOverrideALiveOwnerNames()
    {
        string alpha = ProjectPaths.Normalise(_harness.Project("alpha"));
        Process foreign = _harness.StartOwnedGame();
        string path = WriteMarked(alpha, new OverrideOwner(foreign.Id, foreign.StartTime.ToUniversalTime().Ticks).ToString());

        SessionException refused = await Assert.ThrowsAsync<SessionException>(() =>
            _harness.Sessions.StopFolderAsync(alpha, null, TestContext.Current.CancellationToken)
        );

        Assert.Equal(
            $"No Godot session or warm headless host runs in {alpha}. Override.cfg names a live owner (pid {foreign.Id}), left in place.",
            refused.Message
        );
        Assert.True(File.Exists(path));
    }

    [Fact]
    public async Task StopRefusesAndKeepsBothFilesOfAFolderThisServerArmed()
    {
        string alpha = ProjectPaths.Normalise(_harness.Project("alpha"));
        CancellationToken cancellation = TestContext.Current.CancellationToken;
        await _harness.Sessions.ArmAsync(alpha, new ArmSettings(Quiet: false, ShutOutRealGamepads: false, Mute: false), cancellation);
        byte[] overrideBefore = File.ReadAllBytes(OverrideFile.PathIn(alpha));
        byte[] armBefore = File.ReadAllBytes(ArmFile.PathIn(alpha));

        SessionException refused = await Assert.ThrowsAsync<SessionException>(() => _harness.Sessions.StopFolderAsync(alpha, null, cancellation));

        Assert.Equal(
            $"No Godot session or warm headless host runs in {alpha}. Override.cfg belongs to this server's own session or arm, left in "
                + "place; armed.json belongs to this server's own session or arm, left in place.",
            refused.Message
        );
        Assert.Equal(overrideBefore, File.ReadAllBytes(OverrideFile.PathIn(alpha)));
        Assert.Equal(armBefore, File.ReadAllBytes(ArmFile.PathIn(alpha)));
    }

    [Fact]
    public async Task StopRemovesTheOverrideAndWarnsOfAnArmFileHeldOpen()
    {
        string alpha = ProjectPaths.Normalise(_harness.Project("alpha"));
        string overridePath = WriteMarked(alpha, Dead.ToString());
        string armPath = ArmFile.PathIn(alpha);
        Directory.CreateDirectory(Path.GetDirectoryName(armPath)!);
        File.WriteAllText(armPath, $"{{\"quiet\":true,\"shutOutRealGamepads\":false,\"mute\":false,\"owners\":[\"{Dead}\"]}}");

        object stopped;
        using (new FileStream(armPath, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            stopped = await _harness.Sessions.StopFolderAsync(alpha, null, TestContext.Current.CancellationToken);
        }

        Assert.Equal(
            new HostStopResult(alpha, HeadlessHostStopped: false)
            {
                OverrideRemoved = true,
                Warning = "armed.json is open in another process (a running Godot?), left in place",
            },
            stopped
        );
        Assert.False(File.Exists(overridePath));
        Assert.True(File.Exists(armPath));
    }

    [Fact]
    public void TheResultLeavesOutWhatWasNotRemovedAndNoWarning() =>
        Assert.Equal(
            "{\"projectPath\":\"X:/game\",\"headlessHostStopped\":true}",
            JsonSerializer.Serialize(new HostStopResult("X:/game", HeadlessHostStopped: true), ProjectTools.Json)
        );

    [Fact]
    public void TheResultCarriesWhatWasRemovedAndTheWarning() =>
        Assert.Equal(
            "{\"projectPath\":\"X:/game\",\"headlessHostStopped\":false,\"overrideRemoved\":true,\"warning\":\"armed.json is open\"}",
            JsonSerializer.Serialize(
                new HostStopResult("X:/game", HeadlessHostStopped: false) { OverrideRemoved = true, Warning = "armed.json is open" },
                ProjectTools.Json
            )
        );

    [Fact]
    public async Task AFailedClearSaysWhatItRemovedFirstAndKeepsTheCause()
    {
        string alpha = ProjectPaths.Normalise(_harness.Project("alpha"));
        string overridePath = WriteMarked(alpha, Dead.ToString());
        string armPath = WriteReadOnlyStaleArmFile(alpha);
        try
        {
            SessionException refused = await Assert.ThrowsAsync<SessionException>(() =>
                _harness.Sessions.StopFolderAsync(alpha, null, TestContext.Current.CancellationToken)
            );

            Assert.StartsWith($"Removed override.cfg in {alpha}, then clearing armed.json failed: ", refused.Message, StringComparison.Ordinal);
            Assert.IsType<UnauthorizedAccessException>(refused.InnerException);
            Assert.False(File.Exists(overridePath));
            // No handle of the failed removal is left open: an exclusive open succeeds.
            new FileStream(armPath, FileMode.Open, FileAccess.Read, FileShare.None).Dispose();
        }
        finally
        {
            File.SetAttributes(armPath, FileAttributes.Normal);
        }
    }

    [Fact]
    public async Task AFailedClearEndsWithWhyItLeftAFile()
    {
        string alpha = ProjectPaths.Normalise(_harness.Project("alpha"));
        Process foreign = _harness.StartOwnedGame();
        string overridePath = WriteMarked(alpha, new OverrideOwner(foreign.Id, foreign.StartTime.ToUniversalTime().Ticks).ToString());
        string armPath = WriteReadOnlyStaleArmFile(alpha);
        try
        {
            SessionException refused = await Assert.ThrowsAsync<SessionException>(() =>
                _harness.Sessions.StopFolderAsync(alpha, null, TestContext.Current.CancellationToken)
            );

            Assert.StartsWith($"Clearing armed.json in {alpha} failed: ", refused.Message, StringComparison.Ordinal);
            Assert.EndsWith($". Override.cfg names a live owner (pid {foreign.Id}), left in place.", refused.Message, StringComparison.Ordinal);
            Assert.True(File.Exists(overridePath));
        }
        finally
        {
            File.SetAttributes(armPath, FileAttributes.Normal);
        }
    }

    /// <summary>Writes a stale armed.json naming only a dead owner and marks it read-only, so its delete throws; returns its path.</summary>
    private static string WriteReadOnlyStaleArmFile(string projectDir)
    {
        string path = ArmFile.PathIn(projectDir);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, $"{{\"quiet\":true,\"shutOutRealGamepads\":false,\"mute\":false,\"owners\":[\"{Dead}\"]}}");
        File.SetAttributes(path, FileAttributes.ReadOnly);
        return path;
    }

    /// <summary>Writes a marked override.cfg in the folder whose owners line names <paramref name="owners"/>; returns its path.</summary>
    private static string WriteMarked(string projectDir, string owners)
    {
        string path = OverrideFile.PathIn(projectDir);
        File.WriteAllText(path, $"{OverrideFile.Marker}\n{OverrideFile.OwnersPrefix}{owners}\n[autoload]\n\nGodotMcpBridge=\"*D:/gone/bridge.gd\"\n");
        return path;
    }
}
