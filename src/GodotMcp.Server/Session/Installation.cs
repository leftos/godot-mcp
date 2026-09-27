namespace GodotMcp.Server.Session;

/// <summary>Finds the Godot executable, dotnet, the bridge and headless scripts the server ships, and the C# helper's extension.</summary>
internal static class Installation
{
    public const string GodotPathVariable = "GODOT_PATH";
    public const string FfmpegPathVariable = "FFMPEG_PATH";
    public const string SolutionFileName = "GodotMcp.slnx";

    /// <summary>A Godot console build's file name, matched without regard to case.</summary>
    private const string GodotConsolePattern = "Godot*console*.exe";

    /// <summary>What a refusal names as the server's executable when the process path cannot be read.</summary>
    private const string FallbackServerExecutable = "godot-mcp.exe";

    private static readonly EnumerationOptions ConsoleBuildMatch = new() { MatchCasing = MatchCasing.CaseInsensitive };

    public static readonly string BridgeRelativePath = Path.Combine("bridge", "godot_mcp_bridge.gd");

    /// <summary>
    /// <c>GODOT_PATH</c> when set and not blank, else the first folder of <c>PATH</c> that holds a console build.
    /// </summary>
    /// <exception cref="SessionException">Neither names an existing file.</exception>
    public static string FindGodot() =>
        FindGodot(
            Environment.GetEnvironmentVariable(GodotPathVariable),
            Environment.GetEnvironmentVariable("PATH"),
            Environment.ProcessPath ?? FallbackServerExecutable
        );

    /// <summary>
    /// The rule with its inputs passed in: <paramref name="configured"/> when it is set and not blank, and refused when it
    /// names no file; else the first folder of <paramref name="path"/> holding a <c>Godot*console*.exe</c>, and in that folder
    /// the one whose file name sorts last under <see cref="StringComparer.OrdinalIgnoreCase"/>.
    /// </summary>
    /// <param name="configured">The <c>GODOT_PATH</c> the server was started with.</param>
    /// <param name="path">The <c>PATH</c> the server was started with.</param>
    /// <param name="serverExe">The server's own executable, named in the refusal's <c>claude mcp add</c> line.</param>
    /// <exception cref="SessionException">Neither names an existing file.</exception>
    internal static string FindGodot(string? configured, string? path, string serverExe)
    {
        if (!string.IsNullOrWhiteSpace(configured))
        {
            return File.Exists(configured)
                ? configured
                : throw new SessionException(
                    $"{GodotPathVariable} is '{configured}', which does not exist. Point it at the Godot 4.7 console executable."
                );
        }

        foreach (string folder in PathFolders(path))
        {
            if (!Directory.Exists(folder))
            {
                continue;
            }

            string? found = FindConsoleBuild(folder);
            if (found is not null)
            {
                return found;
            }
        }

        throw new SessionException(
            $"Godot was not found: {GodotPathVariable} is not set and no {GodotConsolePattern} is on PATH. "
                + $"Set {GodotPathVariable} to the Godot 4.7 console executable; with Claude Code, register this server with: "
                + $"claude mcp add godot -s local -e {GodotPathVariable}=<path to the Godot console exe> -- \"{serverExe}\""
        );
    }

    /// <summary>
    /// The folder's <c>Godot*console*.exe</c> whose file name sorts last under
    /// <see cref="StringComparer.OrdinalIgnoreCase"/>, or null when it holds none.
    /// </summary>
    private static string? FindConsoleBuild(string folder) =>
        Directory
            .EnumerateFiles(folder, GodotConsolePattern, ConsoleBuildMatch)
            .OrderBy(file => Path.GetFileName(file), StringComparer.OrdinalIgnoreCase)
            .LastOrDefault();

    /// <summary>The <c>dotnet</c> executable in the first folder of <c>PATH</c> that has one.</summary>
    /// <exception cref="SessionException">No folder on <c>PATH</c> has it.</exception>
    public static string FindDotnet() =>
        FindOnPath("dotnet")
        ?? throw new SessionException(
            "dotnet was not found on PATH, and the project's C# assembly needs building before the run. Install the .NET SDK "
                + "and put dotnet on PATH, or pass options.prepare: \"never\" to launch without building."
        );

    /// <summary>
    /// <c>FFMPEG_PATH</c> when set, else the <c>ffmpeg</c> executable in the first folder of <c>PATH</c> that has one, else
    /// null. A set <c>FFMPEG_PATH</c> that names no file is not looked past: the result is null and
    /// <paramref name="configuredButMissing"/> is its value.
    /// </summary>
    public static string? FindFfmpeg(out string? configuredButMissing)
    {
        configuredButMissing = null;
        string? configured = Environment.GetEnvironmentVariable(FfmpegPathVariable);
        if (string.IsNullOrWhiteSpace(configured))
        {
            return FindOnPath("ffmpeg");
        }

        if (File.Exists(configured))
        {
            return configured;
        }

        configuredButMissing = configured;
        return null;
    }

    /// <summary>The executable in the first folder of <c>PATH</c> that has one, or null.</summary>
    private static string? FindOnPath(string name)
    {
        string executable = OperatingSystem.IsWindows() ? name + ".exe" : name;
        return PathFolders(Environment.GetEnvironmentVariable("PATH")).Select(folder => Path.Combine(folder, executable)).FirstOrDefault(File.Exists);
    }

    /// <summary>The folders of a <c>PATH</c>-style list, in order, without its empty entries or an entry's quotes.</summary>
    private static IEnumerable<string> PathFolders(string? path) =>
        (path ?? string.Empty).Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries).Select(folder => folder.Trim('"'));

    public static readonly string HeadlessRelativePath = Path.Combine("headless", "operations.gd");

    public const string DotnetExtensionFileName = "godot_mcp_dotnet.gdextension";

    private static readonly string DotnetExtensionBesideServer = Path.Combine("dotnet", DotnetExtensionFileName);
    private static readonly string DotnetExtensionInCheckout = Path.Combine("bin", "dotnet", DotnetExtensionFileName);

    public static string FindBridgeScript() => FindBridgeScript(AppContext.BaseDirectory);

    /// <summary>
    /// The bridge published beside the server (<c>bridge/</c> next to the exe), else the <c>bridge/</c> of the
    /// godot-mcp checkout the server was built in.
    /// </summary>
    /// <exception cref="SessionException">Neither exists.</exception>
    public static string FindBridgeScript(string serverDirectory) => FindShippedScript(serverDirectory, BridgeRelativePath, "The bridge script");

    public static string FindHeadlessScript() => FindHeadlessScript(AppContext.BaseDirectory);

    /// <summary>
    /// The headless operations script published beside the server (<c>headless/</c> next to the exe), else the
    /// <c>headless/</c> of the godot-mcp checkout the server was built in.
    /// </summary>
    /// <exception cref="SessionException">Neither exists.</exception>
    public static string FindHeadlessScript(string serverDirectory) =>
        FindShippedScript(serverDirectory, HeadlessRelativePath, "The headless operations script");

    public static string? FindDotnetExtension() => FindDotnetExtension(AppContext.BaseDirectory);

    /// <summary>
    /// The C# helper's extension published beside the server (<c>dotnet/</c> next to the exe), else the one
    /// <c>pwsh run.ps1 dotnet</c> put in <c>bin/dotnet/</c> of the godot-mcp checkout the server was built in; null when
    /// neither has it.
    /// </summary>
    public static string? FindDotnetExtension(string serverDirectory) =>
        FindShippedFile(serverDirectory, DotnetExtensionBesideServer, DotnetExtensionInCheckout);

    private static string FindShippedScript(string serverDirectory, string relativePath, string what) =>
        FindShippedFile(serverDirectory, relativePath, relativePath)
        ?? throw new SessionException(
            $"{what} was not found beside the server ({Path.Combine(serverDirectory, relativePath)}) or in a godot-mcp checkout above it. "
                + "Reinstall the server with 'pwsh run.ps1 publish'."
        );

    /// <summary>
    /// <paramref name="besideServer"/> under the server's folder when it exists, else <paramref name="inCheckout"/> under
    /// the first folder above the server that holds <see cref="SolutionFileName"/> and has it, else null.
    /// </summary>
    private static string? FindShippedFile(string serverDirectory, string besideServer, string inCheckout)
    {
        string published = Path.Combine(serverDirectory, besideServer);
        if (File.Exists(published))
        {
            return published;
        }

        for (DirectoryInfo? directory = new(serverDirectory); directory is not null; directory = directory.Parent)
        {
            string candidate = Path.Combine(directory.FullName, inCheckout);
            if (File.Exists(Path.Combine(directory.FullName, SolutionFileName)) && File.Exists(candidate))
            {
                return candidate;
            }
        }

        return null;
    }
}
