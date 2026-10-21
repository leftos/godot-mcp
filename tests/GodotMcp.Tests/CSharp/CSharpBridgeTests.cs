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
        CSharpRefusedException thrown = Assert.Throws<CSharpRefusedException>(() =>
            CSharpBridge.ParseReply(BridgeResult("""{"ok":false,"error":"Unknown op 'nope'."}""", false))
        );

        Assert.Equal("The C# helper refused the request: Unknown op 'nope'.", thrown.Message);
        Assert.True(thrown.Log.IsEmpty);
    }

    [Fact]
    public void AHelperErrorCarriesTheLogTheSnippetWrote()
    {
        const string Reply = """{"ok":false,"error":"the snippet threw InvalidOperationException: boom","log":["a","b"],"logOmitted":7}""";

        CSharpRefusedException thrown = Assert.Throws<CSharpRefusedException>(() => CSharpBridge.ParseReply(BridgeResult(Reply, false)));

        Assert.Equal("The C# helper refused the request: the snippet threw InvalidOperationException: boom", thrown.Message);
        Assert.Equal(["a", "b"], thrown.Log.Lines);
        Assert.Equal(7, thrown.Log.Dropped);
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
    public async Task ACSharpCallFindsTheHelpersCopyAgainWithoutReadingItsBuild()
    {
        string build = HelperBuild();
        CSharpBridge bridge = Bridge(Path.Combine(build, HelperCache.ExtensionFileName));
        string project = Built("probe");
        using BridgeListener listener = new(NullLogger<BridgeListener>.Instance);
        using SessionRegistry registry = new(listener, NullLogger<GodotSession>.Instance);
        using GodotSession session = new(new SessionSpec("probe", project, SessionKind.Attach, false, false), registry);
        Exception? first = await Record.ExceptionAsync(() => bridge.SendAsync(session, "{}", 1000, null, TestContext.Current.CancellationToken));
        Assert.IsAssignableFrom<SessionException>(first);

        Exception? again;
        using (File.Open(Path.Combine(build, HelperCache.ExtensionFileName), FileMode.Open, FileAccess.Read, FileShare.None))
        {
            again = await Record.ExceptionAsync(() => bridge.SendAsync(session, "{}", 1000, null, TestContext.Current.CancellationToken));
        }

        Assert.NotNull(again);
        Assert.False(again.Message.StartsWith("Preparing the C# helper's copy failed: ", StringComparison.Ordinal), again.Message);
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
        string build = HelperBuild();
        string extension = Path.Combine(build, HelperCache.ExtensionFileName);
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
        string build = HelperBuild();
        string extension = Path.Combine(build, HelperCache.ExtensionFileName);
        CSharpBridge bridge = Bridge(extension);
        string project = Built("probe");
        string copy = Path.GetDirectoryName(bridge.PrepareForState(project).Extension)!;
        File.Delete(Path.Combine(copy, ".complete"));

        StateHelper again = bridge.PrepareForState(project);

        Assert.Equal(copy, Path.GetDirectoryName(again.Extension));
        Assert.True(HelperCache.IsComplete(copy), "the copy is made again, marker and all");
    }

    [Fact]
    public void AHelperFolderIsFoundAgainWithoutReadingItsBuild()
    {
        string build = HelperBuild();
        CSharpBridge bridge = Bridge(Path.Combine(build, HelperCache.ExtensionFileName));
        string project = Built("probe");
        string first = bridge.PrepareHelperFolder(project);

        using (File.Open(Path.Combine(build, HelperCache.ExtensionFileName), FileMode.Open, FileAccess.Read, FileShare.None))
        {
            Assert.Equal(first, bridge.PrepareHelperFolder(project));
        }
    }

    [Fact]
    public void AHelperFolderMovesWhenItsBuildChanges()
    {
        string build = HelperBuild();
        string extension = Path.Combine(build, HelperCache.ExtensionFileName);
        CSharpBridge bridge = Bridge(extension);
        string project = Built("probe");
        string first = bridge.PrepareHelperFolder(project);

        File.WriteAllText(extension, "a different build");
        string rebuilt = bridge.PrepareHelperFolder(project);

        Assert.NotEqual(first, rebuilt);
        Assert.True(Directory.Exists(rebuilt), "the rebuilt helper's folder is there");
        Assert.True(File.Exists(Path.Combine(rebuilt, "GodotMcp.Dotnet.dll")), "it holds the helper's dll");
    }

    [Fact]
    public void AnExtensionIsFoundAgainWithoutReadingItsBuild()
    {
        string build = HelperBuild();
        CSharpBridge bridge = Bridge(Path.Combine(build, HelperCache.ExtensionFileName));
        string project = Built("probe");
        string first = bridge.PrepareExtension(project);

        using (File.Open(Path.Combine(build, HelperCache.ExtensionFileName), FileMode.Open, FileAccess.Read, FileShare.None))
        {
            Assert.Equal(first, bridge.PrepareExtension(project));
        }
    }

    private CSharpBridge Bridge(string? extension) => new(new HelperCache(_temp.Combine("cache")), () => extension);

    /// <summary>A helper build folder as run.ps1's dotnet lays it out: the extension, and the helper's managed dll beside it.</summary>
    private string HelperBuild()
    {
        string build = _temp.Combine("helper-build");
        string helper = Path.Combine(build, "helper");
        Directory.CreateDirectory(helper);
        File.WriteAllText(Path.Combine(build, HelperCache.ExtensionFileName), "build");
        File.WriteAllText(Path.Combine(helper, "GodotMcp.Dotnet.dll"), "dll");
        return build;
    }

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
