using System.Text.Json.Nodes;
using GodotMcp.Server.CSharp;
using GodotMcp.Server.Session;
using GodotMcp.Server.Wire;
using GodotMcp.TestSupport;
using Microsoft.Extensions.Logging.Abstractions;

namespace GodotMcp.Tests.CSharp;

public sealed class CSharpBridgeTests : IDisposable
{
    private const string NoAssembly = "This project has no C# assembly, so the C# tools cannot reach it.";
    private const string Extension = @"C:\helper\godot_mcp_dotnet.gdextension";

    private readonly TempDirectory _temp = new();

    public void Dispose() => _temp.Dispose();

    [Fact]
    public void AProjectWithNoCsprojIsRefused() => Assert.Equal(NoAssembly, CSharpBridge.Refusal(Project("plain"), Extension));

    [Fact]
    public void SeveralCsprojsAreRefusedWithTheLookupsNote()
    {
        string project = Project("several", "One", "Two");
        string? note = PrepScan.FindCsproj(project).Note;

        Assert.NotNull(note);
        Assert.Equal(note, CSharpBridge.Refusal(project, Extension));
    }

    [Fact]
    public void AnUnbuiltAssemblyIsRefusedNamingItsPath()
    {
        string project = Project("unbuilt", "Probe");
        string? refusal = CSharpBridge.Refusal(project, Extension);

        Assert.NotNull(refusal);
        Assert.Contains(PrepScan.AssemblyPath(project, "Probe"), refusal, StringComparison.Ordinal);
    }

    [Fact]
    public void AMissingHelperBuildIsRefusedNamingRunPs1Dotnet() =>
        Assert.Contains("run.ps1 dotnet", CSharpBridge.Refusal(Built("probe"), null), StringComparison.Ordinal);

    [Fact]
    public void ABuiltProjectWithAHelperIsNotRefused() => Assert.Null(CSharpBridge.Refusal(Built("probe"), Extension));

    [Fact]
    public void AnOkReplyYieldsItsResultAndLoadedNow()
    {
        CSharpReply reply = CSharpBridge.ParseReply(BridgeResult("""{"ok":true,"result":{"helperContext":"ctx"}}""", true));

        Assert.Equal("ctx", reply.Result?["helperContext"]?.GetValue<string>());
        Assert.True(reply.LoadedNow);
    }

    [Fact]
    public void AHelperErrorThrowsItsMessage()
    {
        InvalidOperationException thrown = Assert.Throws<InvalidOperationException>(() =>
            CSharpBridge.ParseReply(BridgeResult("""{"ok":false,"error":"Unknown op 'nope'."}""", false))
        );

        Assert.Equal("The C# helper refused the request: Unknown op 'nope'.", thrown.Message);
    }

    [Fact]
    public void AReplyThatIsNotJsonThrowsNamingIt()
    {
        InvalidOperationException thrown = Assert.Throws<InvalidOperationException>(() => CSharpBridge.ParseReply(BridgeResult("not json", false)));

        Assert.StartsWith("The C# helper's reply is not JSON: ", thrown.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AFileSystemErrorPreparingTheCopyIsNotReportedAsADeadConnection()
    {
        string project = Built("probe");
        string missingBuild = Path.Combine(_temp.Combine("no-helper-build"), "godot_mcp_dotnet.gdextension");
        CSharpBridge bridge = new(new HelperCache(_temp.Combine("cache")), () => missingBuild);
        using BridgeListener listener = new(NullLogger<BridgeListener>.Instance);
        using SessionRegistry registry = new(listener, NullLogger<GodotSession>.Instance);
        using GodotSession session = new(new SessionSpec("probe", project, SessionKind.Attach, false, false), registry);

        InvalidOperationException thrown = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            bridge.SendAsync(session, "{}", 1000, null, TestContext.Current.CancellationToken)
        );

        Assert.StartsWith("Preparing the C# helper's copy failed: ", thrown.Message, StringComparison.Ordinal);
        Assert.IsAssignableFrom<IOException>(thrown.InnerException);
    }

    [Fact]
    public void AStateReadInAProjectWithoutCSharpSendsNoHelper() =>
        Assert.Equal(StateHelper.None, Bridge(Extension).PrepareForState(Project("plain")));

    [Fact]
    public void AStateReadInAProjectWhoseHelperCannotRunSendsWhy()
    {
        string several = Project("several", "One", "Two");
        string unbuilt = Project("unbuilt", "Probe");

        Assert.Equal(new StateHelper(null, PrepScan.FindCsproj(several).Note), Bridge(Extension).PrepareForState(several));
        Assert.Contains(PrepScan.AssemblyPath(unbuilt, "Probe"), Bridge(Extension).PrepareForState(unbuilt).Error, StringComparison.Ordinal);
        Assert.Contains("run.ps1 dotnet", Bridge(null).PrepareForState(Built("probe")).Error, StringComparison.Ordinal);
    }

    [Fact]
    public void AStateReadWhoseHelperCopyFailsSendsTheFailure()
    {
        string missingBuild = Path.Combine(_temp.Combine("no-helper-build"), "godot_mcp_dotnet.gdextension");

        StateHelper helper = Bridge(missingBuild).PrepareForState(Built("probe"));

        Assert.Null(helper.Extension);
        Assert.StartsWith("Preparing the C# helper's copy failed: ", helper.Error, StringComparison.Ordinal);
    }

    [Fact]
    public void AStateReadKeepsTheHelpersCopyUntilItsBuildChanges()
    {
        string build = _temp.Combine("helper-build");
        Directory.CreateDirectory(build);
        string extension = Path.Combine(build, HelperCache.ExtensionFileName);
        File.WriteAllText(extension, "first");
        CSharpBridge bridge = Bridge(extension);
        string project = Built("probe");

        StateHelper first = bridge.PrepareForState(project);
        StateHelper again = bridge.PrepareForState(project);
        File.WriteAllText(extension, "second build");
        StateHelper rebuilt = bridge.PrepareForState(project);

        Assert.NotNull(first.Extension);
        Assert.Equal(first, again);
        Assert.NotEqual(first.Extension, rebuilt.Extension);
        Assert.Equal("second build", File.ReadAllText(rebuilt.Extension!));
    }

    [Fact]
    public void AStateReadRemakesAKeptCopyThatIsNoLongerComplete()
    {
        string build = _temp.Combine("helper-build");
        Directory.CreateDirectory(build);
        string extension = Path.Combine(build, HelperCache.ExtensionFileName);
        File.WriteAllText(extension, "build");
        CSharpBridge bridge = Bridge(extension);
        string project = Built("probe");
        string copy = Path.GetDirectoryName(bridge.PrepareForState(project).Extension)!;
        File.Delete(Path.Combine(copy, ".complete"));

        StateHelper again = bridge.PrepareForState(project);

        Assert.Equal(copy, Path.GetDirectoryName(again.Extension));
        Assert.True(HelperCache.IsComplete(copy), "the copy is made again, marker and all");
    }

    private CSharpBridge Bridge(string? extension) => new(new HelperCache(_temp.Combine("cache")), () => extension);

    /// <summary>A project folder with a project.godot and one csproj per name, or none when no name is given.</summary>
    private string Project(string name, params string[] projects)
    {
        string folder = _temp.Combine(name);
        Directory.CreateDirectory(folder);
        File.WriteAllText(Path.Combine(folder, "project.godot"), "[application]\n\nconfig/name=\"probe\"\n");
        foreach (string project in projects)
        {
            File.WriteAllText(Path.Combine(folder, project + ".csproj"), "<Project Sdk=\"Microsoft.NET.Sdk\" />\n");
        }

        return folder;
    }

    /// <summary>The same, with the assembly Godot loads present.</summary>
    private string Built(string name)
    {
        string folder = Project(name, name);
        string assembly = PrepScan.AssemblyPath(folder, name);
        Directory.CreateDirectory(Path.GetDirectoryName(assembly)!);
        File.WriteAllText(assembly, "assembly");
        return folder;
    }

    private static JsonObject BridgeResult(string reply, bool loadedNow) => new() { ["reply"] = reply, ["loadedNow"] = loadedNow };
}
