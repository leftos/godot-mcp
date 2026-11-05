using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using GodotMcp.Server.Tools;
using Microsoft.Extensions.Logging;
using ModelContextProtocol;

namespace GodotMcp.Server.Agents;

/// <summary>What --run-scratches asked for: the project folder, the scenes, run_scratches' options and whether the result is JSON.</summary>
internal sealed record ScratchArguments(string ProjectPath, string[]? Scenes, ScratchOptions Options, bool Json);

/// <summary>
/// The exe's --run-scratches command: it builds the singletons the MCP host builds, runs the project through run_scratches' own
/// code (<see cref="ScratchTools.RunScratchesAsync"/>) without the host, and prints a verdict line a scene with a summary line, or
/// with --json the tool's own result. It holds no verdict logic: the profile, the plan and the verdict are one rule for both
/// routes. Exit status 0 when the run passed, 1 when it did not, 2 for a usage error or a refusal before anything launched, and
/// 130 when interrupted after the games are stopped.
/// </summary>
internal static class ScratchCommand
{
    private const string Name = ServerCommands.RunScratches;

    /// <summary>The start of every session name the command's scenes run under: two runs from two processes never share one.</summary>
    private const string SessionPrefix = "cli-";

    /// <summary>
    /// The command's arguments as run_scratches' options, or null with <paramref name="error"/> naming what is wrong.
    /// </summary>
    /// <param name="args">The exe's arguments, the command's own name first.</param>
    /// <param name="error">Why the arguments cannot be read, or null when they can.</param>
    public static ScratchArguments? Parse(IReadOnlyList<string> args, out string? error)
    {
        error = null;
        Flags flags = new();
        List<string> scenes = [];
        for (int index = 1; index < args.Count; index++)
        {
            error = Read(args, ref index, flags, scenes);
            if (error is not null)
            {
                return null;
            }
        }

        if (flags.Project is null)
        {
            error = $"{Name} needs a project folder.";
            return null;
        }

        if (flags.CaptureSteps.Count > 0 && !flags.Capture)
        {
            error = "--capture-step needs --capture";
            return null;
        }

        return new ScratchArguments(flags.Project, scenes.Count == 0 ? null : [.. scenes], flags.Options, flags.Json);
    }

    /// <summary>
    /// Runs <paramref name="arguments"/> through <paramref name="tools"/> and prints the result: the verdict lines, or with
    /// --json the tool's own JSON. Returns 0 when the run passed, 1 when it did not and 2 when the call was refused before
    /// anything launched.
    /// </summary>
    /// <param name="tools">The scratch tools, as the server's host or a test's harness builds them.</param>
    /// <param name="arguments">The command's arguments, as <see cref="Parse"/> read them.</param>
    /// <param name="output">Where the result goes.</param>
    /// <param name="error">Where a refusal goes, as <c>godot-mcp: &lt;message&gt;</c>.</param>
    /// <param name="cancellation">The command's cancellation, which stops the scenes' games.</param>
    public static async Task<int> RunAsync(
        ScratchTools tools,
        ScratchArguments arguments,
        TextWriter output,
        TextWriter error,
        CancellationToken cancellation
    )
    {
        // A session name of the command's own keeps two runs from two processes on one project out of each other's session and
        // run log.
        ScratchOptions options = arguments.Options with
        {
            Session = $"{SessionPrefix}{Environment.ProcessId}",
        };
        string result;
        try
        {
            result = await tools.RunScratchesAsync(arguments.ProjectPath, arguments.Scenes, options, cancellation);
        }
        catch (McpException e)
        {
            error.WriteLine($"godot-mcp: {e.Message}");
            return 2;
        }

        JsonObject parsed = JsonNode.Parse(result)!.AsObject();
        if (arguments.Json)
        {
            output.WriteLine(result);
        }
        else
        {
            output.Write(Describe(parsed));
        }

        return Exit(parsed);
    }

    /// <summary>The result's verdict lines: one block a scene in the result's order, then the summary line.</summary>
    /// <param name="result">run_scratches' result, as the tool returns it.</param>
    public static string Describe(JsonObject result)
    {
        StringBuilder text = new();
        foreach (JsonNode? node in result["scenes"]?.AsArray() ?? [])
        {
            text.Append(Scene(node!.AsObject()));
        }

        bool passed = Passed(result);
        text.Append("scratch: ")
            .Append(passed ? "passed" : "failed")
            .Append(" (")
            .Append(passed ? PassedCounts(result) : FailedCounts(result))
            .AppendLine(")");
        return text.ToString();
    }

    /// <summary>The command's exit status for the result: 0 when the run passed, 1 when it did not.</summary>
    /// <param name="result">run_scratches' result, as the tool returns it.</param>
    public static int Exit(JsonObject result) => Passed(result) ? 0 : 1;

    /// <summary>
    /// The command itself: the arguments read, the server's singletons built without the MCP host, the run made, the games
    /// stopped and the exit status returned.
    /// </summary>
    /// <param name="args">The exe's arguments, the command's own name first.</param>
    /// <param name="output">Where the result goes.</param>
    /// <param name="error">Where a usage error or a refusal goes.</param>
    public static int Run(IReadOnlyList<string> args, TextWriter output, TextWriter error)
    {
        ScratchArguments? arguments = Parse(args, out string? problem);
        if (arguments is null)
        {
            return ServerCommands.Refuse(error, problem!);
        }

        using ILoggerFactory logging = LoggerFactory.Create(builder =>
            builder.AddConsole(options => options.LogToStandardErrorThreshold = LogLevel.Trace)
        );
        Log.Ambient = logging.CreateLogger("GodotMcp.Server.Load");
        using CancellationTokenSource cancellation = new();
        void OnCancel(object? _, ConsoleCancelEventArgs cancel)
        {
            cancel.Cancel = true;
            cancellation.Cancel();
        }
        Console.CancelKeyPress += OnCancel;
        GameServices? services = null;
        AppDomain.CurrentDomain.ProcessExit += StopGames;
        int status;
        try
        {
            var created = GameServices.Create(logging);
            services = created;
            status = RunAsync(new ScratchTools(created.Sessions, created.CSharp), arguments, output, error, cancellation.Token)
                .GetAwaiter()
                .GetResult();
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
            status = 130;
        }
        catch (Exception e)
        {
            // A failure the tool did not turn into a refusal is still this command's to report, not the runtime's to crash on.
            error.WriteLine($"godot-mcp: {e.Message}");
            status = 2;
        }
        finally
        {
            // Both handlers stay subscribed until the games are stopped, so a second Ctrl+C during the shutdown is this
            // command's too.
            services?.Sessions.Shutdown();
            services?.Sessions.Dispose();
            services?.Dispose();
            Console.CancelKeyPress -= OnCancel;
            AppDomain.CurrentDomain.ProcessExit -= StopGames;
        }

        return cancellation.IsCancellationRequested ? 130 : status;

        void StopGames(object? sender, EventArgs eventArgs) => services?.Sessions.Shutdown();
    }

    /// <summary>One argument read into <paramref name="flags"/> and <paramref name="scenes"/>, or the refusal it is.</summary>
    private static string? Read(IReadOnlyList<string> args, ref int index, Flags flags, List<string> scenes)
    {
        string arg = args[index];
        if (arg is "--keep-going")
        {
            flags.KeepGoing = true;
            return null;
        }

        if (arg is "--json")
        {
            flags.Json = true;
            return null;
        }

        if (arg is "--windowed")
        {
            flags.Windowed = true;
            return null;
        }

        if (arg is "--audible")
        {
            flags.Audible = true;
            return null;
        }

        if (arg is "--capture")
        {
            flags.Capture = true;
            return null;
        }

        return arg.StartsWith("--", StringComparison.Ordinal) ? Option(args, ref index, flags, scenes) : Project(flags, arg);
    }

    /// <summary>The project folder the argument names, refused when one is already given.</summary>
    private static string? Project(Flags flags, string arg)
    {
        if (flags.Project is not null)
        {
            return $"{Name} takes one project folder, not '{arg}'.";
        }

        flags.Project = arg;
        return null;
    }

    /// <summary>One option with its value, refused when the option is unknown, given twice or given without a value.</summary>
    private static string? Option(IReadOnlyList<string> args, ref int index, Flags flags, List<string> scenes)
    {
        string arg = args[index];
        if (!Known(arg))
        {
            return $"{Name} does not take '{arg}'.";
        }

        if (!Repeatable(arg) && !flags.Given.Add(arg))
        {
            return $"{arg} is given twice; give it once.";
        }

        if (MissingValue(args, index, arg))
        {
            return $"{arg} needs a value.";
        }

        string value = args[++index];
        return Value(flags, scenes, arg, value);
    }

    /// <summary>
    /// The options given more than once, each occurrence appending one value: --scene, --user-arg, --engine-arg, --steps and
    /// --capture-step.
    /// </summary>
    private static bool Repeatable(string option) => option is "--scene" or "--user-arg" or "--engine-arg" or "--steps" or "--capture-step";

    /// <summary>
    /// Whether <paramref name="option"/> is the last argument or is followed by another option. --user-arg and --engine-arg take
    /// the next argument verbatim, even when it begins with --, so only being last refuses them.
    /// </summary>
    private static bool MissingValue(IReadOnlyList<string> args, int index, string option) =>
        index + 1 == args.Count || (!TakesAnyValue(option) && args[index + 1].StartsWith("--", StringComparison.Ordinal));

    /// <summary>The options whose next argument is their value verbatim, even when it begins with --.</summary>
    private static bool TakesAnyValue(string option) => option is "--user-arg" or "--engine-arg";

    /// <summary>
    /// The options the command takes, each once but the repeatable ones, and each of them in the usage text.
    /// </summary>
    private static bool Known(string option) =>
        option
            is "--scene"
                or "--pace"
                or "--time-scale"
                or "--parallel"
                or "--prepare"
                or "--details"
                or "--only"
                or "--user-arg"
                or "--engine-arg"
                or "--steps"
                or "--capture-step";

    /// <summary>One known option's value, as the tool itself reads it: the command holds no check of its own.</summary>
    private static string? Value(Flags flags, List<string> scenes, string option, string value)
    {
        if (option is "--scene")
        {
            scenes.Add(value);
            return null;
        }

        if (option is "--user-arg" or "--engine-arg")
        {
            (option is "--user-arg" ? flags.UserArgs : flags.EngineArgs).Add(value);
            return null;
        }

        if (option is "--steps")
        {
            flags.Steps.Add(value);
            return null;
        }

        if (option is "--capture-step")
        {
            flags.CaptureSteps.Add(value);
            return null;
        }

        return option is "--prepare" or "--details" or "--only" ? Text(flags, option, value) : Amount(flags, option, value);
    }

    /// <summary>A number-taking option's value, refused when it is no number.</summary>
    private static string? Amount(Flags flags, string option, string value)
    {
        if (option is "--pace" or "--time-scale")
        {
            if (!double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out double seconds))
            {
                return $"{option} takes a number, not '{value}'.";
            }

            if (option is "--pace")
            {
                flags.Pace = seconds;
            }
            else
            {
                flags.TimeScale = seconds;
            }

            return null;
        }

        if (!int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out int count))
        {
            return $"{option} takes a number, not '{value}'.";
        }

        flags.Parallel = count;
        return null;
    }

    /// <summary>A text option's value, as the tool itself reads it: the command holds no check of its own.</summary>
    private static string? Text(Flags flags, string option, string value)
    {
        switch (option)
        {
            case "--prepare":
                flags.Prepare = value;
                break;
            case "--details":
                flags.Details = JsonSerializer.SerializeToElement(value);
                break;
            case "--only":
                flags.Only = value;
                break;
        }

        return null;
    }

    /// <summary>One scene's block: its verdict line, the step it failed at, what its exit did and the folder of its shots.</summary>
    private static string Scene(JsonObject scene)
    {
        JsonObject steps = scene["steps"]!.AsObject();
        StringBuilder text = new();
        text.Append(scene["scene"]!.GetValue<string>());
        if (scene["batch"] is JsonValue batch && scene["batches"] is JsonValue batches)
        {
            text.Append(" [batch ").Append(batch.GetValue<int>() + 1).Append('/').Append(batches.GetValue<int>()).Append(']');
        }

        text.Append(": ")
            .Append(scene["verdict"]!.GetValue<string>())
            .Append(" (")
            .Append(steps["played"]!.GetValue<int>())
            .Append('/')
            .Append(steps["total"]!.GetValue<int>())
            .Append(" steps, ")
            .Append(Seconds(scene["seconds"]!))
            .AppendLine("s)");
        if (scene["failedAt"] is JsonObject failedAt)
        {
            text.Append("  failed at step ")
                .Append(failedAt["index"]!.GetValue<int>())
                .Append(' ')
                .Append(failedAt["name"]!.GetValue<string>())
                .Append(": ")
                .Append(failedAt["error"]!.GetValue<string>())
                .AppendLine();
        }

        if (scene["exit"] is JsonObject exit)
        {
            text.Append(ExitLines(exit));
        }

        if (ShotFolder(scene) is string shots)
        {
            text.Append("  shots: ").AppendLine(shots);
        }

        return text.ToString();
    }

    /// <summary>The folder a scene's first shot was written to, or null when the scene took none.</summary>
    private static string? ShotFolder(JsonObject scene)
    {
        foreach (JsonNode? node in scene["details"]?.AsArray() ?? [])
        {
            if (node is JsonObject step && step["screenshot"] is JsonValue shot && shot.TryGetValue(out string? path))
            {
                return Path.GetDirectoryName(path);
            }
        }

        return null;
    }

    /// <summary>What a scene's exit did, one <c>  exit: </c> line a reason: the patterns it never printed, its error, or its kill.</summary>
    private static string ExitLines(JsonObject exit)
    {
        StringBuilder text = new();
        foreach (JsonNode? missing in exit["missing"]?.AsArray() ?? [])
        {
            text.Append("  exit: ").AppendLine(missing!.GetValue<string>());
        }

        if (exit["error"] is JsonNode error)
        {
            text.Append("  exit: ").AppendLine(error.GetValue<string>());
        }

        if (exit["killed"]?.GetValue<bool>() == true)
        {
            string reason = exit["killReason"] is JsonNode given ? $" ({given.GetValue<string>()})" : string.Empty;
            text.Append("  exit: killed").AppendLine(reason);
        }

        return text.ToString();
    }

    private static string Seconds(JsonNode seconds) => seconds.GetValue<double>().ToString("F1", CultureInfo.InvariantCulture);

    private static string PassedCounts(JsonObject result) =>
        $"{Count(result, "green")} green, {Count(result, "known")} known, {Count(result, "noSteps")} no steps{ListedCount(result)}";

    private static string FailedCounts(JsonObject result) =>
        $"{Count(result, "green")} green, {Count(result, "red")} red, {Count(result, "killed")} killed, "
        + $"{Count(result, "known")} known, {Count(result, "noSteps")} no steps{ListedCount(result)}";

    /// <summary>", N listed" for a run that listed scenes, which the command itself never asks for; nothing else.</summary>
    private static string ListedCount(JsonObject result) => result["listed"]?.GetValue<int>() is { } listed ? $", {listed} listed" : string.Empty;

    private static int Count(JsonObject result, string field) => result[field]?.GetValue<int>() ?? 0;

    private static bool Passed(JsonObject result) => result["passed"]?.GetValue<bool>() == true;

    /// <summary>The options the command reads, filled as its arguments are read.</summary>
    private sealed class Flags
    {
        public string? Project { get; set; }

        public double? Pace { get; set; }

        public double? TimeScale { get; set; }

        public int? Parallel { get; set; }

        public string? Prepare { get; set; }

        public JsonElement? Details { get; set; }

        public string? Only { get; set; }

        public bool KeepGoing { get; set; }

        public bool Json { get; set; }

        public bool Windowed { get; set; }

        public bool Audible { get; set; }

        public bool Capture { get; set; }

        /// <summary>The --user-arg values in the order given, which the tool appends to the profile's own.</summary>
        public List<string> UserArgs { get; } = [];

        /// <summary>The --engine-arg values in the order given, which the tool places in that order.</summary>
        public List<string> EngineArgs { get; } = [];

        /// <summary>The --steps values in the order given, which the tool reads as its list for every scene.</summary>
        public List<string> Steps { get; } = [];

        /// <summary>The --capture-step values in the order given, which the tool reads as the capture's own step list.</summary>
        public List<string> CaptureSteps { get; } = [];

        /// <summary>
        /// The options read so far, so a second one of them is refused; the repeatable ones (--scene, --user-arg, --engine-arg,
        /// --steps and --capture-step) are not ones.
        /// </summary>
        public HashSet<string> Given { get; } = new(StringComparer.Ordinal);

        /// <summary>The options left out stay null, so every one of them keeps the tool's own default.</summary>
        public ScratchOptions Options =>
            new(
                Pace: Pace,
                TimeScale: TimeScale,
                Steps: Tokens(Steps),
                Prepare: Prepare,
                Details: Details,
                Parallel: Parallel,
                KeepGoing: KeepGoing ? true : null,
                Only: Only,
                UserArgs: UserArgs.Count == 0 ? null : [.. UserArgs],
                EngineArgs: EngineArgs.Count == 0 ? null : [.. EngineArgs],
                Windowed: Windowed ? true : null,
                Audible: Audible ? true : null,
                Capture: CaptureOptions()
            );

        /// <summary>The capture the switches ask for, null when --capture is not given.</summary>
        private ScratchCaptureOptions? CaptureOptions() => Capture ? new ScratchCaptureOptions(Steps: Tokens(CaptureSteps), Screenshot: true) : null;

        /// <summary>The step tokens as the tool reads them, null when none is given.</summary>
        private static JsonElement? Tokens(List<string> tokens) => tokens.Count == 0 ? null : JsonSerializer.SerializeToElement(tokens);
    }
}
