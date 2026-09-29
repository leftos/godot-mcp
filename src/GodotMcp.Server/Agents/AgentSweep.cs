using System.Text;
using GodotMcp.Server.Session;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace GodotMcp.Server.Agents;

/// <summary>What an agent sweep reads and whether it writes.</summary>
/// <param name="Home">The user's home folder: its <c>.claude/agents</c> is always swept.</param>
/// <param name="Roots">Folders whose direct children's <c>.claude/agents</c> are swept.</param>
/// <param name="DryRun">Report what would change, writing and committing nothing.</param>
/// <param name="Catalog">Every served tool's short name mapped to its class.</param>
internal sealed record AgentSweepRequest(string Home, IReadOnlyList<string> Roots, bool DryRun, IReadOnlyDictionary<string, string> Catalog);

/// <summary>
/// Syncs the godot tools of every marked agent file (see <see cref="AgentFile"/>) in the user's <c>~/.claude/agents</c> and
/// in the <c>.claude/agents</c> of each direct child of the roots, then commits the changed files git tracks in each
/// repository in one commit on its current branch. A file inside a repository is known by the repository's top folder
/// and its path in it, so two roots reaching one repository (a substituted drive, a junction) visit it once. A
/// repository where a tracked file that would change has uncommitted changes is skipped whole. Prints one line per
/// changed, skipped or failed file, one per repository commit, and a summary line.
/// </summary>
/// <param name="output">Where the report goes.</param>
internal sealed class AgentSweep(TextWriter output)
{
    /// <summary>The environment variable holding more roots, separated by ';'.</summary>
    public const string RootsVariable = "GODOT_MCP_SWEEP_ROOTS";

    /// <summary>The message of the commit a sweep makes in each repository.</summary>
    public const string CommitMessage = "chore: sync godot-mcp tools";

    private static readonly ILogger GitLog = NullLogger.Instance;

    private readonly List<string> _commitLines = [];
    private readonly HashSet<string> _visited = new(StringComparer.OrdinalIgnoreCase);
    private int _changed;
    private int _unchanged;
    private int _skipped;
    private int _unmarked;
    private int _errors;
    private int _commits;
    private int _failedCommits;

    /// <summary>Runs the sweep; returns 0, or 1 when a file could not be synced or a commit failed.</summary>
    /// <param name="request">What to sweep.</param>
    public int Run(AgentSweepRequest request)
    {
        List<SweptFile> changed = [];
        foreach (string folder in AgentFolders(request))
        {
            (string? repository, string prefix) = Locate(folder);
            foreach (string path in Directory.EnumerateFiles(folder, "*.md").Order(StringComparer.OrdinalIgnoreCase))
            {
                Visit(Place(path, repository, prefix), request.Catalog, changed);
            }
        }

        foreach (IGrouping<string?, SweptFile> repository in changed.GroupBy(f => f.Repository, StringComparer.OrdinalIgnoreCase))
        {
            Apply(repository.Key, [.. repository], request.DryRun);
        }

        _commitLines.ForEach(output.WriteLine);
        string verb = request.DryRun ? "sweep (dry run)" : "sweep";
        string commits = request.DryRun ? "" : $", {_commits} commits, {_failedCommits} failed commits";
        output.WriteLine($"{verb}: {_changed} changed, {_unchanged} unchanged, {_skipped} skipped, {_unmarked} unmarked, {_errors} errors{commits}");
        return _errors > 0 || _failedCommits > 0 ? 1 : 0;
    }

    /// <summary>The <c>+added -removed</c> description of a changed file, or <c>sorted</c> when only the order changed.</summary>
    /// <param name="sync">The file's sync.</param>
    public static string Describe(AgentFileSync sync)
    {
        List<string> parts = [];
        if (sync.Added.Count > 0)
        {
            parts.Add("+" + string.Join(", ", sync.Added));
        }

        if (sync.Removed.Count > 0)
        {
            parts.Add("-" + string.Join(", ", sync.Removed));
        }

        return parts.Count > 0 ? string.Join(" ", parts) : "sorted";
    }

    private List<string> AgentFolders(AgentSweepRequest request)
    {
        HashSet<string> seen = new(StringComparer.OrdinalIgnoreCase);
        List<string> folders = [];
        AddAgentFolder(Path.Combine(request.Home, ".claude", "agents"), seen, folders);
        EnumerationOptions children = new() { IgnoreInaccessible = true };
        foreach (string root in request.Roots)
        {
            if (!Directory.Exists(root))
            {
                output.WriteLine($"{root}: no such folder, skipped");
                continue;
            }

            foreach (string child in Directory.EnumerateDirectories(root, "*", children).Order(StringComparer.OrdinalIgnoreCase))
            {
                AddAgentFolder(Path.Combine(child, ".claude", "agents"), seen, folders);
            }
        }

        return folders;
    }

    private static void AddAgentFolder(string folder, HashSet<string> seen, List<string> folders)
    {
        string full = Path.GetFullPath(folder);
        if (Directory.Exists(full) && seen.Add(full))
        {
            folders.Add(full);
        }
    }

    // The repository holding a folder, as git spells its top folder, and the folder's path in it ("" outside any).
    private static (string? Repository, string Prefix) Locate(string folder)
    {
        GitResult located = GitRunner.RunForResult(folder, GitLog, GitRunner.Ceiling, ["rev-parse", "--show-toplevel", "--show-prefix"]);
        string[] lines = located.Output.Split('\n', StringSplitOptions.TrimEntries);
        return located.Succeeded && located.OutputRead && lines.Length >= 2 ? (Path.GetFullPath(lines[0]), lines[1]) : (null, "");
    }

    // A file as the sweep knows it: inside a repository, by the top folder and its path in it.
    private static (string Path, string RepositoryPath, string? Repository) Place(string path, string? repository, string prefix)
    {
        if (repository is null)
        {
            return (path, "", null);
        }

        string repositoryPath = prefix + System.IO.Path.GetFileName(path);
        return (System.IO.Path.GetFullPath(System.IO.Path.Combine(repository, repositoryPath)), repositoryPath, repository);
    }

    private void Visit(
        (string Path, string RepositoryPath, string? Repository) file,
        IReadOnlyDictionary<string, string> catalog,
        List<SweptFile> changed
    )
    {
        if (!_visited.Add(file.Path) || !TryRead(file.Path, out string text, out Encoding encoding))
        {
            return;
        }

        AgentFileSync sync = AgentFile.Sync(text, catalog);
        switch (sync.State)
        {
            case AgentFileState.Unmarked:
                output.WriteLine($"{file.Path}: unmarked, skipped");
                _unmarked++;
                break;
            case AgentFileState.Error:
                Fail(file.Path, sync.Error);
                break;
            case AgentFileState.Unchanged:
                _unchanged++;
                break;
            case AgentFileState.Changed:
                changed.Add(new SweptFile(file.Path, file.RepositoryPath, sync, encoding, file.Repository));
                break;
        }
    }

    // The file's text and its encoding: UTF-8, with the byte order mark it had kept, so a rewrite changes no other byte.
    private bool TryRead(string path, out string text, out Encoding encoding)
    {
        text = "";
        encoding = new UTF8Encoding(false);
        try
        {
            byte[] bytes = File.ReadAllBytes(path);
            bool bom = bytes.AsSpan().StartsWith(Encoding.UTF8.Preamble);
            int skip = bom ? Encoding.UTF8.Preamble.Length : 0;
            text = new UTF8Encoding(false, throwOnInvalidBytes: true).GetString(bytes, skip, bytes.Length - skip);
            encoding = new UTF8Encoding(bom);
            return true;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or DecoderFallbackException)
        {
            Fail(path, $"could not be read as UTF-8 ({e.Message})");
            return false;
        }
    }

    // Writes (unless a dry run) and reports one repository's changed files, then commits the ones git tracks; files in no
    // repository are only written. git runs from the repository's top folder with paths relative to it.
    private void Apply(string? repository, List<SweptFile> files, bool dryRun)
    {
        if (repository is null)
        {
            WriteAll(files, dryRun);
            return;
        }

        HashSet<string>? tracked = Tracked(repository, files);
        if (tracked is null || !IsClean(repository, files, tracked))
        {
            return;
        }

        List<SweptFile> written = WriteAll(files, dryRun);
        written
            .FindAll(f => !tracked.Contains(f.RepositoryPath))
            .ForEach(f => output.WriteLine($"{f.Path}: written, not committed (not tracked by git)"));
        List<string> committing = [.. written.Where(f => tracked.Contains(f.RepositoryPath)).Select(f => f.RepositoryPath)];
        if (committing.Count == 0)
        {
            return;
        }

        if (dryRun)
        {
            _commitLines.Add($"{repository}: would commit {committing.Count} file(s)");
            return;
        }

        Commit(repository, committing);
    }

    // The files' repository paths git tracks, or null (each file reported as an error) when git cannot say.
    private HashSet<string>? Tracked(string repository, List<SweptFile> files)
    {
        GitResult listed = GitRunner.RunForResult(
            repository,
            GitLog,
            GitRunner.Ceiling,
            ["ls-files", "-z", "--", .. files.Select(f => f.RepositoryPath)]
        );
        if (!listed.Succeeded || !listed.OutputRead)
        {
            files.ForEach(f => Fail(f.Path, $"git ls-files failed: {listed.Message}"));
            return null;
        }

        return new HashSet<string>(listed.Output.Split('\0', StringSplitOptions.RemoveEmptyEntries), StringComparer.Ordinal);
    }

    // Whether no tracked file that would change has uncommitted changes; otherwise reports each file as skipped.
    private bool IsClean(string repository, List<SweptFile> files, HashSet<string> tracked)
    {
        if (tracked.Count == 0)
        {
            return true;
        }

        GitResult status = GitRunner.RunForResult(repository, GitLog, GitRunner.Ceiling, ["status", "--porcelain", "--", .. tracked]);
        if (!status.Succeeded || !status.OutputRead)
        {
            files.ForEach(f => Fail(f.Path, $"git status failed: {status.Message}"));
            return false;
        }

        if (status.Output.Trim().Length == 0)
        {
            return true;
        }

        foreach (SweptFile file in files)
        {
            output.WriteLine($"{file.Path}: repo has uncommitted agent files, skipped");
            _skipped++;
        }

        return false;
    }

    // Writes each file (none in a dry run) and reports it; returns those written.
    private List<SweptFile> WriteAll(List<SweptFile> files, bool dryRun)
    {
        List<SweptFile> written = [];
        foreach (SweptFile file in files)
        {
            if (dryRun || TryWrite(file))
            {
                output.WriteLine($"{file.Path}: {Describe(file.Sync)}");
                _changed++;
                written.Add(file);
            }
        }

        return written;
    }

    private bool TryWrite(SweptFile file)
    {
        try
        {
            File.WriteAllText(file.Path, file.Sync.Text, file.Encoding);
            return true;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            Fail(file.Path, $"could not be written ({e.Message})");
            return false;
        }
    }

    // The commit runs the repository's hooks, which may stash the user's unstaged work or build the project, so it has no
    // ceiling: stopping it halfway could leave that work in a hook's stash and a stale index.lock.
    private void Commit(string repository, List<string> paths)
    {
        GitResult commit = GitRunner.RunForResult(repository, GitLog, ceiling: null, ["commit", "-m", CommitMessage, "--", .. paths]);
        if (!commit.Succeeded)
        {
            _failedCommits++;
            _commitLines.Add($"{repository}: commit failed, the edits are left in place: {commit.Message}");
            _commitLines.AddRange(commit.Lines.Skip(1).Select(line => "    " + line));
            return;
        }

        GitResult head = GitRunner.RunForResult(repository, GitLog, GitRunner.Ceiling, ["rev-parse", "--short", "HEAD"]);
        _commits++;
        _commitLines.Add($"{repository}: committed {head.Output.Trim()}");
    }

    private void Fail(string path, string reason)
    {
        output.WriteLine($"{path}: error: {reason}");
        _errors++;
    }

    private sealed record SweptFile(string Path, string RepositoryPath, AgentFileSync Sync, Encoding Encoding, string? Repository);
}
