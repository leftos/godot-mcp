using GodotMcp.Server.CSharp;

namespace GodotMcp.IntegrationTests.Fixtures;

/// <summary>
/// The C# bridge a <c>RuntimeTools</c> takes in a test that never reaches a C# call: it finds no helper, so the bridge
/// refuses before its cache is touched and nothing is ever copied into it.
/// </summary>
internal static class TestCSharp
{
    public static CSharpBridge Unused() =>
        new(new HelperCache(Path.Combine(Path.GetTempPath(), "godot-mcp-tests", "unused-dotnet-cache")), () => null);
}
