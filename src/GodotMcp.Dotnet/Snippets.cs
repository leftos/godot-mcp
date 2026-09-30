using System.Diagnostics.CodeAnalysis;
using System.Reflection;
using System.Runtime.Loader;
using System.Text.Json.Nodes;
using GodotMcp.Dotnet.Core;

namespace GodotMcp.Dotnet;

/// <summary>
/// The helper's <c>run</c> op: a snippet the server compiled, loaded into a collectible load context of its own, created and
/// awaited. An unfinished <c>RunAsync</c> answers <c>{"ok":true,"pending":"c&lt;n&gt;"}</c> from <see cref="PendingTasks"/>;
/// the context is unloaded once the result is written, or when the pending entry is settled or forgotten.
/// </summary>
internal static class Snippets
{
    private const int DefaultDepth = 8;

    private const int MaxDepth = 32;

    /// <summary>The class the server's snippet compiler generates (its <c>SnippetCompiler.ClassName</c>).</summary>
    private const string ClassName = "GodotMcpSnippet";

    /// <summary>The method that runs the snippet (the compiler's <c>SnippetCompiler.MethodName</c>).</summary>
    private const string MethodName = "RunAsync";

    /// <summary>
    /// <c>{"op":"run","assembly":"&lt;base64&gt;","pdb"?:"&lt;base64&gt;","game":"Name","expect":{"Name":"&lt;mvid&gt;",..},
    /// "maxDepth"?:int,"keep"?:bool}</c> → <c>{"value":..,"type":"..","handle"?:"h..","warning"?:".."}</c>, or the pending
    /// reply. An <c>expect</c> entry whose assembly the game has loaded from another build is refused before anything loads.
    /// </summary>
    public static JsonObject Run(JsonObject request)
    {
        int maxDepth = request["maxDepth"]?.GetValue<int>() ?? DefaultDepth;
        if (maxDepth is < 1 or > MaxDepth)
        {
            return Helper.Failure($"maxDepth {maxDepth} is out of range: give 1 to {MaxDepth}.");
        }
        if (Stale(request["expect"]?.AsObject()) is { } stale)
        {
            return Helper.Failure(stale);
        }
        SnippetContext context = new(SnippetContext.Holding(request["game"]!.GetValue<string>()));
        // Never disposed: the snippet may hold or have registered on the token after the entry is settled or forgotten, and a
        // source with no timer holds nothing the collector cannot take back.
        CancellationTokenSource cancellation = new();
        SnippetGlobals snippet;
        try
        {
            snippet = Create(context, request);
            snippet.Bind(cancellation.Token);
        }
        catch
        {
            context.Unload();
            throw;
        }
        return Execute(snippet, context, new Shape(maxDepth, request["keep"]?.GetValue<bool>() ?? false), cancellation);
    }

    /// <summary>The refusal naming each expected assembly the game has loaded from another build, in order; null when none is.</summary>
    private static string? Stale(JsonObject? expect)
    {
        List<string> stale = StaleDlls(expect);
        return stale.Count switch
        {
            0 => null,
            1 => $"the game runs an older build of {stale[0]} than the one on disk; restart_project loads it",
            _ => $"the game runs older builds of {string.Join(", ", stale)} than the ones on disk; restart_project loads them",
        };
    }

    /// <summary>
    /// The file name of each assembly in <paramref name="expect"/> (simple name to MVID, as the server reads the build on disk)
    /// that the game has loaded from another build, in order; none when <paramref name="expect"/> is null.
    /// </summary>
    internal static List<string> StaleDlls(JsonObject? expect) =>
        expect is null ? [] : [.. expect.Where(entry => IsStale(entry.Key, entry.Value!.GetValue<string>())).Select(entry => entry.Key + ".dll")];

    private static bool IsStale(string name, string mvid) =>
        SnippetContext.Loaded(name) is { } loaded && loaded.ManifestModule.ModuleVersionId != Guid.ParseExact(mvid, "D");

    private static SnippetGlobals Create(SnippetContext context, JsonObject request)
    {
        using MemoryStream image = new(Convert.FromBase64String(request["assembly"]!.GetValue<string>()));
        using MemoryStream? symbols = request["pdb"] is { } pdb ? new MemoryStream(Convert.FromBase64String(pdb.GetValue<string>())) : null;
        Assembly assembly = context.LoadFromStream(image, symbols);
        return (SnippetGlobals)Activator.CreateInstance(assembly.GetType(ClassName, throwOnError: true)!)!;
    }

    [SuppressMessage(
        "Design",
        "CA1031:Do not catch general exception types",
        Justification = "The snippet runs game code; whatever it throws is the failure to report."
    )]
    private static JsonObject Execute(SnippetGlobals snippet, SnippetContext context, Shape shape, CancellationTokenSource cancellation)
    {
        Task<object?> task;
        try
        {
            task = (Task<object?>)snippet.GetType().GetMethod(MethodName, Type.EmptyTypes)!.Invoke(snippet, null)!;
        }
        catch (Exception e)
        {
            context.Unload();
            return Threw(e);
        }
        if (!task.IsCompleted)
        {
            return PendingTasks.Add(task, () => Settled(task, shape), context.Unload, cancellation);
        }
        try
        {
            return Settled(task, shape);
        }
        finally
        {
            context.Unload();
        }
    }

    [SuppressMessage(
        "Design",
        "CA1031:Do not catch general exception types",
        Justification = "The awaited snippet ran game code; whatever it threw is the failure to report."
    )]
    private static JsonObject Settled(Task<object?> task, Shape shape)
    {
        object? value;
        try
        {
            value = task.GetAwaiter().GetResult();
        }
        catch (Exception e)
        {
            return Threw(e);
        }
        JsonObject result = new()
        {
            ["value"] = ValueWriter.Write(value, new GodotFormatter(shape.MaxDepth), shape.MaxDepth),
            ["type"] = value?.GetType().FullName,
        };
        if (shape.Keep)
        {
            MemberAccess.Keep(value, result);
        }
        return new JsonObject { ["ok"] = true, ["result"] = result };
    }

    private static JsonObject Threw(Exception e)
    {
        Exception thrown = Thrown.Unwrap(e);
        return Helper.Failure($"the snippet threw {Thrown.Describe(thrown)}{Thrown.Stack(thrown)}");
    }

    /// <summary>What the reply needs besides the value: the writer's depth and whether to keep the value.</summary>
    private sealed record Shape(int MaxDepth, bool Keep);
}

/// <summary>
/// One snippet's collectible load context: an assembly already loaded in another context (the game's, GodotSharp, the helper,
/// the framework) resolves to that one by simple name, and any other through the game's context.
/// </summary>
internal sealed class SnippetContext(AssemblyLoadContext? game) : AssemblyLoadContext("godot-mcp snippet", isCollectible: true)
{
    /// <summary>The first assembly of that simple name loaded outside every snippet's context, or null.</summary>
    public static Assembly? Loaded(string name) =>
        All.Where(context => context is not SnippetContext)
            .SelectMany(context => context.Assemblies)
            .FirstOrDefault(assembly => assembly.GetName().Name == name);

    /// <summary>The context holding the loaded assembly named <paramref name="game"/>, or null when none is loaded.</summary>
    public static AssemblyLoadContext? Holding(string game) => Loaded(game) is { } assembly ? GetLoadContext(assembly) : null;

    protected override Assembly? Load(AssemblyName assemblyName)
    {
        if (assemblyName.Name is { } name && Loaded(name) is { } loaded)
        {
            return loaded;
        }
        if (game is null)
        {
            return null;
        }
        try
        {
            return game.LoadFromAssemblyName(assemblyName);
        }
        catch (FileNotFoundException)
        {
            return null;
        }
    }
}
