using System.Diagnostics.CodeAnalysis;
using GodotMcp.Server.Session;
using Microsoft.Extensions.Logging;
using ModelContextProtocol;

namespace GodotMcp.Server.Tools;

/// <summary>
/// A folder a named output may lie in, and the folder its link walk starts from: the folder itself, or the checkout above
/// its .tmp.
/// </summary>
/// <param name="Folder">The folder the output must lie in.</param>
/// <param name="WalkFrom">Where following links on the way to the output starts.</param>
internal sealed record OutputPlace(string Folder, string WalkFrom);

/// <summary>
/// Where a still or clip the caller names may be written, as git places them: the project; its checkout W as a whole tree
/// when W tracks the project's project.godot, else only W's .tmp folder; and the main checkout's .tmp folder when W is a
/// linked worktree the main checkout lists.
/// </summary>
/// <param name="Project">The project folder, fully qualified.</param>
/// <param name="Checkout">The top level of the git checkout holding the project; null outside git.</param>
/// <param name="CheckoutTracksProject">Whether the checkout tracks the project's project.godot.</param>
/// <param name="MainCheckout">The main checkout when <paramref name="Checkout"/> is one of its linked worktrees; else null.</param>
internal sealed record OutputPlaces(string Project, string? Checkout, bool CheckoutTracksProject, string? MainCheckout)
{
    private const string WorktreeField = "worktree ";
    private const string LongPathPrefix = @"\\?\";

    /// <summary>The project folder as Windows finally finds it, every link on its way followed; else as given.</summary>
    internal string FinalProject { get; init; } = Project;

    /// <summary>The checkout as Windows finally finds it; else as given.</summary>
    internal string? FinalCheckout { get; init; } = Checkout;

    /// <summary>The main checkout as Windows finally finds it; else as given.</summary>
    internal string? FinalMainCheckout { get; init; } = MainCheckout;

    /// <summary>
    /// Asks git where the project's checkout and main checkout are, and whether the checkout tracks the project, and reads
    /// each folder's final path.
    /// </summary>
    /// <param name="projectDir">The session's project folder.</param>
    /// <param name="logger">Where a missing or stopped git is reported.</param>
    /// <exception cref="McpException">Windows cannot give the final path of a folder that exists.</exception>
    internal static OutputPlaces Find(string projectDir, ILogger logger)
    {
        string project = Normalize(projectDir);
        string? topLevel = GitRunner.Run(project, logger, "rev-parse", "--show-toplevel")?.Trim();
        if (string.IsNullOrEmpty(topLevel))
        {
            return WithFinalForms(new OutputPlaces(project, null, false, null));
        }

        string checkout = Normalize(topLevel);
        GitResult tracked = GitRunner.RunForResult(project, logger, GitRunner.Ceiling, ["ls-files", "--error-unmatch", "--", "project.godot"]);
        string? listed = GitRunner.Run(project, logger, "worktree", "list", "--porcelain", "-z");
        return WithFinalForms(new OutputPlaces(project, checkout, tracked.Succeeded, listed is null ? null : MainOf(listed, checkout)));
    }

    /// <summary>The places a path may lie in, as given; a res:// path only in the project.</summary>
    internal IReadOnlyList<OutputPlace> Allowed(bool projectOnly) => PlacesOf(Project, Checkout, MainCheckout, projectOnly);

    /// <summary>The places a path may lie in, in their final forms; a res:// path only in the project.</summary>
    internal IReadOnlyList<OutputPlace> AllowedFinal(bool projectOnly) => PlacesOf(FinalProject, FinalCheckout, FinalMainCheckout, projectOnly);

    private List<OutputPlace> PlacesOf(string project, string? checkout, string? mainCheckout, bool projectOnly)
    {
        List<OutputPlace> places = [new(project, project)];
        if (projectOnly || checkout is null)
        {
            return places;
        }

        places.Add(CheckoutTracksProject ? new(checkout, checkout) : new(Path.Combine(checkout, ".tmp"), checkout));
        if (mainCheckout is not null)
        {
            places.Add(new(Path.Combine(mainCheckout, ".tmp"), mainCheckout));
        }

        return places;
    }

    private static OutputPlaces WithFinalForms(OutputPlaces places) =>
        places with
        {
            FinalProject = FinalOf(places.Project),
            FinalCheckout = FinalOf(places.Checkout),
            FinalMainCheckout = FinalOf(places.MainCheckout),
        };

    /// <summary>The final path of an existing folder on Windows; a missing folder, or any folder elsewhere, as given.</summary>
    [return: NotNullIfNotNull(nameof(folder))]
    private static string? FinalOf(string? folder)
    {
        if (folder is null || !OperatingSystem.IsWindows() || !Directory.Exists(folder))
        {
            return folder;
        }

        try
        {
            return Path.TrimEndingDirectorySeparator(FinalPath.Of(folder));
        }
        catch (IOException e)
        {
            throw new McpException(
                $"the folder {folder} has no final path Windows can give: {e.Message}; named outputs cannot be checked against it",
                e
            );
        }
    }

    /// <summary>
    /// The places a refusal names and the advice that ends it, as "the project P and …; save inside either"; null when the
    /// project is the only place, whose refusals keep their own wording.
    /// </summary>
    internal string? Describe(bool projectOnly)
    {
        if (projectOnly || Checkout is null)
        {
            return null;
        }

        List<string> names = [$"the project {Project}", CheckoutName(Checkout)];
        if (MainCheckout is not null)
        {
            names.Add($"the main checkout's .tmp folder {Path.Combine(MainCheckout, ".tmp")}");
        }

        return names.Count == 2
            ? $"{names[0]} and {names[1]}; save inside either"
            : $"{names[0]}, {names[1]} and {names[2]}; save inside one of them";
    }

    private string CheckoutName(string checkout) =>
        CheckoutTracksProject
            ? $"its checkout {checkout}"
            : $"the checkout's .tmp folder {Path.Combine(checkout, ".tmp")} (the checkout {checkout} does not track "
                + $"{Path.Combine(Project, "project.godot")}, so only its .tmp folder is open)";

    /// <summary>
    /// The first worktree of <c>git worktree list --porcelain -z</c>, which git lists first as the main one, when it is not
    /// bare, is not <paramref name="checkout"/> itself, and the list names <paramref name="checkout"/>.
    /// </summary>
    private static string? MainOf(string listed, string checkout)
    {
        List<(string Folder, bool Bare)> worktrees = ParseWorktrees(listed);
        if (worktrees.Count == 0 || worktrees[0].Bare || SameFolder(worktrees[0].Folder, checkout))
        {
            return null;
        }

        return worktrees.Exists(worktree => SameFolder(worktree.Folder, checkout)) ? worktrees[0].Folder : null;
    }

    // Each record is NUL-terminated fields, "worktree <path>" first; an empty field ends it.
    private static List<(string Folder, bool Bare)> ParseWorktrees(string listed)
    {
        List<(string Folder, bool Bare)> worktrees = [];
        foreach (string field in listed.Split('\0'))
        {
            if (field.StartsWith(WorktreeField, StringComparison.Ordinal))
            {
                worktrees.Add((Normalize(field[WorktreeField.Length..]), false));
            }
            else if (field == "bare" && worktrees.Count > 0)
            {
                worktrees[^1] = (worktrees[^1].Folder, true);
            }
        }

        return worktrees;
    }

    private static bool SameFolder(string first, string second) => Path.GetRelativePath(first, second) == ".";

    /// <summary>The folder fully qualified, without a trailing separator; <c>\\?\X:\…</c> as the plain <c>X:\…</c>.</summary>
    private static string Normalize(string folder)
    {
        bool prefixedDrive =
            folder.StartsWith(LongPathPrefix, StringComparison.Ordinal)
            && folder.Length > LongPathPrefix.Length + 1
            && char.IsAsciiLetter(folder[LongPathPrefix.Length])
            && folder[LongPathPrefix.Length + 1] == ':';
        return Path.TrimEndingDirectorySeparator(Path.GetFullPath(prefixedDrive ? folder[LongPathPrefix.Length..] : folder));
    }
}
