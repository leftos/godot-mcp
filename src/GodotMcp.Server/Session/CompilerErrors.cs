using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace GodotMcp.Server.Session;

/// <summary>
/// A failed build's errors, each as <c>file:line: CODE message</c>, at most <see cref="CompilerErrors.Limit"/>, and how many there were.
/// </summary>
internal sealed record CompilerErrorList(IReadOnlyList<string> Errors, int Total)
{
    /// <summary>
    /// The errors one to a line, then <c>(and N more)</c> on a line of its own when some were left out; a sentence saying none
    /// were found when there are none. run_project's refusal and a headless C# refusal quote it.
    /// </summary>
    public string Quote()
    {
        if (Total == 0)
        {
            return "No compiler errors were found in its output.";
        }

        string omitted = Total > Errors.Count ? $"\n(and {Total - Errors.Count} more)" : string.Empty;
        return string.Join('\n', Errors) + omitted;
    }
}

/// <summary>
/// One error or warning of a build log: the file and position it names (none for an error such as <c>MSB1009</c>), its code,
/// its message, and <see cref="Severity"/> <c>error</c> or <c>warning</c>.
/// </summary>
internal sealed record BuildDiagnostic(
    string? File,
    int? Line,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] int? Column,
    string Code,
    string Message,
    string Severity
)
{
    public const string Error = "error";
    public const string Warning = "warning";
}

/// <summary>Reads the errors and warnings out of an MSBuild console log.</summary>
internal static partial class CompilerErrors
{
    public const int Limit = 20;

    /// <summary>
    /// Every <c>file(line,col): error|warning CODE: message [project]</c> line of the log, deduplicated (MSBuild repeats each
    /// in its closing summary), in the order they first appear. A diagnostic without a position has no line, and one without a
    /// file (<c>error MSB1009: ...</c>) no file either.
    /// </summary>
    public static IReadOnlyList<BuildDiagnostic> ParseDiagnostics(string log)
    {
        List<BuildDiagnostic> diagnostics = [];
        HashSet<BuildDiagnostic> seen = [];
        foreach (string line in log.Split('\n'))
        {
            Match match = DiagnosticLine().Match(line.TrimEnd('\r'));
            if (!match.Success)
            {
                continue;
            }

            BuildDiagnostic diagnostic = ToDiagnostic(match);
            if (seen.Add(diagnostic))
            {
                diagnostics.Add(diagnostic);
            }
        }

        return diagnostics;
    }

    /// <summary>The log's errors as <c>file:line: CODE message</c> (<see cref="Errors"/>).</summary>
    public static CompilerErrorList Parse(string log) => Errors(ParseDiagnostics(log));

    /// <summary>
    /// The errors among the diagnostics, each as <c>file:line: CODE message</c>, deduplicated; one without a position reads
    /// <c>file: CODE message</c>, and one without a file <c>CODE message</c>.
    /// </summary>
    public static CompilerErrorList Errors(IEnumerable<BuildDiagnostic> diagnostics)
    {
        string[] errors =
        [
            .. diagnostics.Where(diagnostic => diagnostic.Severity == BuildDiagnostic.Error).Select(Format).Distinct(StringComparer.Ordinal),
        ];
        return new CompilerErrorList([.. errors.Take(Limit)], errors.Length);
    }

    private static string Format(BuildDiagnostic diagnostic)
    {
        string what = $"{diagnostic.Code} {diagnostic.Message}";
        if (diagnostic.File is null)
        {
            return what;
        }

        string where = diagnostic.Line is { } line ? $"{diagnostic.File}:{line}" : diagnostic.File;
        return $"{where}: {what}";
    }

    private static BuildDiagnostic ToDiagnostic(Match match) =>
        new(
            match.Groups["file"].Success ? match.Groups["file"].Value : null,
            NumberOf(match.Groups["line"]),
            NumberOf(match.Groups["column"]),
            match.Groups["code"].Value,
            match.Groups["message"].Value,
            match.Groups["severity"].Value
        );

    private static int? NumberOf(Group group) => group.Success ? int.Parse(group.Value, System.Globalization.CultureInfo.InvariantCulture) : null;

    [GeneratedRegex(
        @"^\s*(?:(?<file>.+?)(?:\((?<line>\d+)(?:,(?<column>\d+))?(?:,\d+)*\))?\s*:\s*)?(?<severity>error|warning) (?<code>[A-Za-z]+\d+)\s*:\s*"
            + @"(?<message>.*?)(?:\s+\[[^\]]+\])?\s*$"
    )]
    private static partial Regex DiagnosticLine();
}

/// <summary>
/// The last prep build's diagnostics, saved beside its log as <c>.godot/godot-mcp/build-diagnostics.json</c>: when it ended
/// (UTC), its state (<c>built</c>, <c>failed</c>, or <c>stopped</c> at its ceiling), and every error and warning it logged.
/// </summary>
internal sealed record SavedBuild(DateTime BuiltAt, string State, IReadOnlyList<BuildDiagnostic> Diagnostics)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private static readonly UTF8Encoding Utf8NoBom = new(encoderShouldEmitUTF8Identifier: false);

    public static string PathIn(string projectDir) => Path.Combine(ProjectPrep.LogFolder(projectDir), "build-diagnostics.json");

    public static void Save(string projectDir, SavedBuild build) =>
        File.WriteAllText(PathIn(projectDir), JsonSerializer.Serialize(build, Json), Utf8NoBom);

    /// <summary>The saved build, or null when none has been saved.</summary>
    /// <exception cref="SessionException">The file is not a saved build.</exception>
    public static SavedBuild? Load(string projectDir)
    {
        string path = PathIn(projectDir);
        if (!File.Exists(path))
        {
            return null;
        }

        try
        {
            return JsonSerializer.Deserialize<SavedBuild>(File.ReadAllText(path), Json)
                ?? throw new SessionException($"The saved build diagnostics at {path} hold null; delete the file and validate again.");
        }
        catch (JsonException e)
        {
            throw new SessionException(
                $"The saved build diagnostics at {path} are not readable ({e.Message}); delete the file and validate again.",
                e
            );
        }
    }
}
