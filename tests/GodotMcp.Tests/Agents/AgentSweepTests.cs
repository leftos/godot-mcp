using System.Text;
using GodotMcp.Server.Agents;
using GodotMcp.Server.Session;
using GodotMcp.TestSupport;
using Microsoft.Extensions.Logging.Abstractions;

namespace GodotMcp.Tests.Agents;

/// <summary>The agent sweep on temp folders: a home with .claude/agents, roots with child projects, and git repositories.</summary>
public sealed class AgentSweepTests : IDisposable
{
    private const string Marker = "<!-- godot-mcp tool classes: read, drive -->";

    private static readonly Dictionary<string, string> Catalog = new(StringComparer.Ordinal)
    {
        ["click"] = ToolClasses.Drive,
        ["hover"] = ToolClasses.Drive,
        ["get_scene_tree"] = ToolClasses.Read,
        ["add_node"] = ToolClasses.EditScene,
        ["run_script"] = ToolClasses.EditLive,
    };

    private readonly TempDirectory _temp = new();

    private string Home => _temp.Combine("home");

    private string HomeAgents => _temp.Combine("home", ".claude", "agents");

    public void Dispose() => _temp.Dispose();

    [Fact]
    public void AMarkerAddsItsClassesToolsAndRemovesTheOnesNoLongerServedOrInItsClasses()
    {
        string path = WriteAgent(HomeAgents, "player", "Read, mcp__godot__click, mcp__godot__gone, mcp__godot__add_node, Grep, SendMessage", Marker);

        (int status, string output) = Sweep(dryRun: false);

        Assert.Equal(0, status);
        Assert.Equal(
            "tools: Read, mcp__godot__click, mcp__godot__get_scene_tree, mcp__godot__hover, Grep, SendMessage",
            ToolsLine(File.ReadAllText(path))
        );
        Assert.Contains($"{path}: +get_scene_tree, hover -add_node, gone", output, StringComparison.Ordinal);
        Assert.Contains(
            "sweep: 1 changed, 0 unchanged, 0 skipped, 0 unmarked, 0 errors, 0 commits, 0 failed commits",
            output,
            StringComparison.Ordinal
        );
    }

    [Fact]
    public void OnlyTheToolsLineChangesAndCrlfEndingsAndTheByteOrderMarkAreKept()
    {
        string before =
            "---\r\nname: player\r\ntools: Read, Bash, SendMessage\r\nmodel: opus\r\n---\r\n\r\nBody line.\r\n" + Marker + "\r\nLast line.\r\n";
        string path = Path.Combine(HomeAgents, "player.md");
        Directory.CreateDirectory(HomeAgents);
        File.WriteAllText(path, before, new UTF8Encoding(true));

        (int status, _) = Sweep(dryRun: false);

        string expected = before.Replace(
            "tools: Read, Bash, SendMessage",
            "tools: Read, Bash, mcp__godot__click, mcp__godot__get_scene_tree, mcp__godot__hover, SendMessage",
            StringComparison.Ordinal
        );
        Assert.Equal(0, status);
        Assert.Equal([.. Encoding.UTF8.Preamble, .. Encoding.UTF8.GetBytes(expected)], File.ReadAllBytes(path));
    }

    [Theory]
    [InlineData("Read, Grep", "Read, Grep, mcp__godot__get_scene_tree")]
    [InlineData("Read, SendMessage, Grep", "Read, mcp__godot__get_scene_tree, SendMessage, Grep")]
    [InlineData("Read, mcp__godot__hover, Grep, mcp__godot__click", "Read, mcp__godot__get_scene_tree, Grep")]
    public void GodotToolsGoWhereTheFirstStoodElseBeforeSendMessageElseAtTheEnd(string tools, string expected)
    {
        string path = WriteAgent(HomeAgents, "reader", tools, "<!-- godot-mcp tool classes: read -->");

        Sweep(dryRun: false);

        Assert.Equal($"tools: {expected}", ToolsLine(File.ReadAllText(path)));
    }

    [Fact]
    public void AFileAlreadyInStepIsLeftUntouchedAndNotReported()
    {
        string tools = "Read, mcp__godot__click, mcp__godot__get_scene_tree, mcp__godot__hover, SendMessage";
        string path = WriteAgent(HomeAgents, "player", tools, Marker);
        byte[] before = File.ReadAllBytes(path);

        (int status, string output) = Sweep(dryRun: false);

        Assert.Equal(0, status);
        Assert.Equal(before, File.ReadAllBytes(path));
        Assert.DoesNotContain(path, output, StringComparison.Ordinal);
        Assert.Contains("0 changed, 1 unchanged", output, StringComparison.Ordinal);
    }

    [Fact]
    public void AnUnmarkedFileNamingAGodotToolIsReportedAndLeftAlone()
    {
        string path = WriteAgent(HomeAgents, "old", "Read, mcp__godot__gone", marker: null);
        byte[] before = File.ReadAllBytes(path);

        (int status, string output) = Sweep(dryRun: false);

        Assert.Equal(0, status);
        Assert.Equal(before, File.ReadAllBytes(path));
        Assert.Contains($"{path}: unmarked, skipped", output, StringComparison.Ordinal);
    }

    [Fact]
    public void AnUnmarkedFileWithoutGodotToolsIsIgnoredSilently()
    {
        string path = WriteAgent(HomeAgents, "plain", "Read, Grep", marker: null);

        (int status, string output) = Sweep(dryRun: false);

        Assert.Equal(0, status);
        Assert.DoesNotContain(path, output, StringComparison.Ordinal);
        Assert.Contains("0 changed, 0 unchanged, 0 skipped, 0 unmarked, 0 errors", output, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("<!-- godot-mcp tool classes: read, fly -->", "unknown tool class fly")]
    [InlineData("<!-- godot-mcp tool classes: -->", "names no tool class")]
    public void AMarkerWithAnUnknownOrNoClassIsAnErrorAndTheFileIsLeftAlone(string marker, string reason)
    {
        string path = WriteAgent(HomeAgents, "bad", "Read, mcp__godot__click", marker);
        string good = WriteAgent(HomeAgents, "good", "Read", Marker);
        byte[] before = File.ReadAllBytes(path);

        (int status, string output) = Sweep(dryRun: false);

        Assert.Equal(1, status);
        Assert.Equal(before, File.ReadAllBytes(path));
        Assert.Contains($"{path}: error: ", output, StringComparison.Ordinal);
        Assert.Contains(reason, output, StringComparison.Ordinal);
        Assert.Contains("mcp__godot__hover", File.ReadAllText(good), StringComparison.Ordinal);
    }

    [Fact]
    public void AMarkedFileWithoutAOneLineToolsListIsAnError()
    {
        Directory.CreateDirectory(HomeAgents);
        string path = Path.Combine(HomeAgents, "list.md");
        File.WriteAllText(path, $"---\nname: list\ntools:\n  - Read\n---\n{Marker}\n");

        (int status, string output) = Sweep(dryRun: false);

        Assert.Equal(1, status);
        Assert.Contains(
            $"{path}: error: it is marked for godot-mcp tool classes but its front matter has no one-line",
            output,
            StringComparison.Ordinal
        );
    }

    [Fact]
    public void ADryRunReportsTheChangesAndWritesNothing()
    {
        string path = WriteAgent(HomeAgents, "player", "Read", Marker);
        byte[] before = File.ReadAllBytes(path);

        (int status, string output) = Sweep(dryRun: true);

        Assert.Equal(0, status);
        Assert.Equal(before, File.ReadAllBytes(path));
        Assert.Contains($"{path}: +click, get_scene_tree, hover", output, StringComparison.Ordinal);
        Assert.Contains("sweep (dry run): 1 changed", output, StringComparison.Ordinal);
    }

    [Fact]
    public void ARepositoryGetsOneCommitHoldingOnlyTheChangedAgentFiles()
    {
        string repository = _temp.Combine("root", "game");
        string agents = Path.Combine(repository, ".claude", "agents");
        string changing = WriteAgent(agents, "player", "Read", Marker);
        WriteAgent(agents, "steady", "Read, mcp__godot__click, mcp__godot__get_scene_tree, mcp__godot__hover", Marker);
        WriteAgent(agents, "other", "Read", marker: null);
        File.WriteAllText(Path.Combine(repository, "notes.txt"), "first\n");
        InitRepository(repository);
        File.WriteAllText(Path.Combine(repository, "notes.txt"), "an uncommitted change of the user's\n");

        (int status, string output) = Sweep(dryRun: false, _temp.Combine("root"));

        Assert.Equal(0, status);
        Assert.Equal("2", Git(repository, "rev-list", "--count", "HEAD").Trim());
        Assert.Equal(".claude/agents/player.md", Git(repository, "show", "--name-only", "--format=", "HEAD").Trim());
        Assert.Equal(AgentSweep.CommitMessage, Git(repository, "log", "-1", "--format=%s").Trim());
        Assert.Equal(" M notes.txt", Git(repository, "status", "--porcelain").TrimEnd());
        string sha = Git(repository, "rev-parse", "--short", "HEAD").Trim();
        Assert.Contains($"{Path.GetFullPath(repository)}: committed {sha}", output, StringComparison.OrdinalIgnoreCase);
        Assert.Contains($"{changing}: +click, get_scene_tree, hover", output, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("1 commits, 0 failed commits", output, StringComparison.Ordinal);
    }

    [Fact]
    public void ARepositoryWithAnUncommittedChangeToAnAgentFileThatWouldChangeIsSkippedWhole()
    {
        string repository = _temp.Combine("root", "game");
        string agents = Path.Combine(repository, ".claude", "agents");
        string dirty = WriteAgent(agents, "dirty", "Read", Marker);
        string clean = WriteAgent(agents, "clean", "Grep", Marker);
        InitRepository(repository);
        File.AppendAllText(dirty, "An edit the user has not committed.\n");
        byte[] dirtyBefore = File.ReadAllBytes(dirty);
        byte[] cleanBefore = File.ReadAllBytes(clean);

        (int status, string output) = Sweep(dryRun: false, _temp.Combine("root"));

        Assert.Equal(0, status);
        Assert.Equal(dirtyBefore, File.ReadAllBytes(dirty));
        Assert.Equal(cleanBefore, File.ReadAllBytes(clean));
        Assert.Equal("1", Git(repository, "rev-list", "--count", "HEAD").Trim());
        Assert.Contains($"{dirty}: repo has uncommitted agent files, skipped", output, StringComparison.OrdinalIgnoreCase);
        Assert.Contains($"{clean}: repo has uncommitted agent files, skipped", output, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("committed", output.Replace("uncommitted", "", StringComparison.Ordinal), StringComparison.Ordinal);
    }

    [Fact]
    public void RootsFromTheOptionAndTheVariableSweepEachChildsAgentsFolder()
    {
        string fromOption = WriteAgent(_temp.Combine("rootA", "one", ".claude", "agents"), "a", "Read", Marker);
        string fromVariable = WriteAgent(_temp.Combine("rootB", "two", ".claude", "agents"), "b", "Read", Marker);
        string fromSecondVariable = WriteAgent(_temp.Combine("rootC", "three", ".claude", "agents"), "c", "Read", Marker);
        string nested = WriteAgent(_temp.Combine("rootA", "one", "deeper", ".claude", "agents"), "d", "Read", Marker);
        string atRoot = WriteAgent(_temp.Combine("rootA", ".claude", "agents"), "e", "Read", Marker);
        StringWriter output = new();
        string variable = $"{_temp.Combine("rootB")};  ;{_temp.Combine("rootC")}";

        int? status = ServerCommands.Run(
            ["--sweep-agents", "--dry-run", "--root", _temp.Combine("rootA")],
            output,
            new StringWriter(),
            new CommandEnvironment(Home, variable)
        );

        Assert.Equal(0, status);
        string report = output.ToString();
        Assert.Contains($"{fromOption}: +", report, StringComparison.Ordinal);
        Assert.Contains($"{fromVariable}: +", report, StringComparison.Ordinal);
        Assert.Contains($"{fromSecondVariable}: +", report, StringComparison.Ordinal);
        Assert.DoesNotContain(nested, report, StringComparison.Ordinal);
        Assert.DoesNotContain(atRoot, report, StringComparison.Ordinal);
        Assert.Contains("sweep (dry run): 3 changed", report, StringComparison.Ordinal);
    }

    [Fact]
    public void AMissingRootIsReportedAndTheRestStillSwept()
    {
        string path = WriteAgent(HomeAgents, "player", "Read", Marker);
        string missing = _temp.Combine("nowhere");

        (int status, string output) = Sweep(dryRun: false, missing);

        Assert.Equal(0, status);
        Assert.Contains($"{missing}: no such folder, skipped", output, StringComparison.Ordinal);
        Assert.Contains("mcp__godot__hover", File.ReadAllText(path), StringComparison.Ordinal);
    }

    [Fact]
    public void AChangedAgentFileGitDoesNotTrackIsWrittenButNotCommitted()
    {
        string repository = _temp.Combine("root", "game");
        string agents = Path.Combine(repository, ".claude", "agents");
        string tracked = WriteAgent(agents, "tracked", "Read", Marker);
        File.WriteAllText(Path.Combine(repository, ".gitignore"), ".claude/agents/ignored.md\n");
        InitRepository(repository);
        string ignored = WriteAgent(agents, "ignored", "Read", Marker);

        (int status, string output) = Sweep(dryRun: false, _temp.Combine("root"));

        Assert.Equal(0, status);
        Assert.Contains("mcp__godot__hover", File.ReadAllText(ignored), StringComparison.Ordinal);
        Assert.Contains($"{ignored}: written, not committed (not tracked by git)", output, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain($"{tracked}: written, not committed", output, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(".claude/agents/tracked.md", Git(repository, "show", "--name-only", "--format=", "HEAD").Trim());
        Assert.Contains("1 commits, 0 failed commits", output, StringComparison.Ordinal);
    }

    [Fact]
    public void TwoRootsReachingOneRepositoryVisitItOnceUnderOneSpelling()
    {
        string repository = _temp.Combine("root", "game");
        string agents = Path.Combine(repository, ".claude", "agents");
        WriteAgent(agents, "player", "Read", Marker);
        InitRepository(repository);
        string alias = _temp.Combine("alias");
        MakeJunction(alias, _temp.Combine("root"));

        (int status, string output) = Sweep(dryRun: false, _temp.Combine("root"), alias);
        // Removed as a link before the temp folder's cleanup, whose recursive delete would otherwise go through it.
        Directory.Delete(alias);

        Assert.Equal(0, status);
        Assert.Equal("2", Git(repository, "rev-list", "--count", "HEAD").Trim());
        Assert.Single(output.Split('\n'), line => line.Contains("player.md: +", StringComparison.Ordinal));
        Assert.DoesNotContain(alias, output, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("sweep: 1 changed, 0 unchanged", output, StringComparison.Ordinal);
        Assert.Contains("1 commits, 0 failed commits", output, StringComparison.Ordinal);
    }

    private static void MakeJunction(string link, string target)
    {
        using System.Diagnostics.Process mklink = System.Diagnostics.Process.Start("cmd", ["/c", "mklink", "/J", link, target])!;
        mklink.WaitForExit();
        Assert.Equal(0, mklink.ExitCode);
    }

    private (int Status, string Output) Sweep(bool dryRun, params string[] roots)
    {
        StringWriter output = new();
        int status = new AgentSweep(output).Run(new AgentSweepRequest(Home, roots, dryRun, Catalog));
        return (status, output.ToString());
    }

    private static string WriteAgent(string folder, string name, string tools, string? marker)
    {
        Directory.CreateDirectory(folder);
        string path = Path.GetFullPath(Path.Combine(folder, name + ".md"));
        string body = marker is null ? "Does its work.\n" : $"Does its work.\n\n{marker}\n";
        File.WriteAllText(path, $"---\nname: {name}\ndescription: An agent.\ntools: {tools}\nmodel: opus\n---\n\n{body}");
        return path;
    }

    private static string ToolsLine(string text) =>
        text.Split('\n').Single(line => line.StartsWith("tools:", StringComparison.Ordinal)).TrimEnd('\r');

    // A repository whose own config keeps the user's global hooks, signing and line-ending conversion out of the test.
    private void InitRepository(string repository)
    {
        Git(repository, "init", "--quiet");
        Git(repository, "config", "user.name", "Sweep Test");
        Git(repository, "config", "user.email", "sweep@example.invalid");
        Git(repository, "config", "commit.gpgsign", "false");
        Git(repository, "config", "core.autocrlf", "false");
        Git(repository, "config", "core.hooksPath", _temp.Combine("no-hooks"));
        Git(repository, "add", "--all");
        Git(repository, "commit", "--quiet", "-m", "first");
    }

    private static string Git(string repository, params string[] arguments)
    {
        GitResult result = GitRunner.RunForResult(repository, NullLogger.Instance, GitRunner.Ceiling, arguments);
        Assert.True(result.Succeeded, $"git {string.Join(' ', arguments)} failed: {result.Error}");
        return result.Output;
    }
}
