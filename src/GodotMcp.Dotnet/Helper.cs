using Godot;

namespace GodotMcp.Dotnet;

/// <summary>
/// The helper's entry, run by the loader on the main thread: it stores the callable GDScript reaches the helper through
/// as the SceneTree meta <see cref="MetaName"/>. The callable takes a JSON request and returns a JSON reply.
/// </summary>
public static class Helper
{
    public const string MetaName = "godot_mcp_dotnet";

    private const string Unanswered = """{"error":"The C# helper answers no requests yet."}""";

    public static void Install()
    {
        MainLoop tree = Engine.GetMainLoop();
        tree.SetMeta(MetaName, Callable.From<string, string>(_ => Unanswered));
    }
}
