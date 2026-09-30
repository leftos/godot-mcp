using System.Text.Json;
using System.Text.Json.Nodes;
using GodotMcp.Server.Session;

namespace GodotMcp.Tests.Session;

/// <summary>
/// A PrepResult in the JSON a tool returns: each step that ran a process names its log, and a step that ran none leaves the
/// key out.
/// </summary>
public sealed class PrepResultLogTests
{
    private const string BuildLog = @"X:\game\.godot\godot-mcp\build.log";
    private const string ImportLog = @"X:\game\.godot\godot-mcp\import.log";

    // The options the tool classes serialise their results with (ProjectTools, HeadlessTools); unlike the MCP SDK's
    // argument binding options, they ignore no null by themselves, so the prep's own attribute is what leaves a key out.
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    [Fact]
    public void StepsThatRanAProcessNameTheirLogs()
    {
        PrepResult result = new()
        {
            Build = "built",
            BuildMs = 1200,
            BuildLog = BuildLog,
            Import = "done",
            ImportMs = 300,
            ImportLog = ImportLog,
        };

        JsonObject json = Write(result);

        Assert.Equal(BuildLog, json["buildLog"]!.GetValue<string>());
        Assert.Equal(ImportLog, json["importLog"]!.GetValue<string>());
    }

    [Fact]
    public void AStepThatRanAProcessAndOneThatDidNotWriteOnlyTheFirstsLog()
    {
        PrepResult result = new()
        {
            Build = "built",
            BuildMs = 1200,
            BuildLog = BuildLog,
            Import = "not-needed",
        };

        JsonObject json = Write(result);

        Assert.Equal(BuildLog, json["buildLog"]!.GetValue<string>());
        Assert.False(json.ContainsKey("importLog"), json.ToJsonString());
    }

    [Fact]
    public void StepsThatRanNoProcessWriteNoLogKeys()
    {
        JsonObject json = Write(PrepResult.Skipped);

        Assert.False(json.ContainsKey("buildLog"), json.ToJsonString());
        Assert.False(json.ContainsKey("importLog"), json.ToJsonString());
    }

    private static JsonObject Write(PrepResult result) => JsonNode.Parse(JsonSerializer.Serialize(result, Json))!.AsObject();
}
