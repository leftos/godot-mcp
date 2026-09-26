using System.Text.Json.Nodes;
using GodotMcp.Server.Session;

namespace GodotMcp.Tests.Session;

public sealed class HeadlessRunnerTests
{
    [Fact]
    public void AFailureQuotesTheErrorsGodotLoggedButNotItsWarnings()
    {
        JsonObject reply = Failed(
            "res://broken.tscn did not load as a scene; engineErrors say why",
            Entry("error", "Parse Error: Parse error. [Resource file res://broken.tscn:5]", "res://broken.tscn", 5),
            Entry("warning", "a warning", "res://other.gd", 3),
            Entry("error", "Failed loading resource: res://broken.tscn.", "core/io/resource_loader.cpp", 343)
        );

        string message = HeadlessRunner.FailureMessage("get_scene_file_tree", reply);

        Assert.Equal(
            "get_scene_file_tree failed: res://broken.tscn did not load as a scene; engineErrors say why\nGodot logged:\n"
                + "Parse Error: Parse error. [Resource file res://broken.tscn:5] (res://broken.tscn:5)\n"
                + "Failed loading resource: res://broken.tscn. (core/io/resource_loader.cpp:343)",
            message
        );
    }

    [Fact]
    public void AFailureWithoutLoggedErrorsKeepsItsMessage()
    {
        JsonObject withoutLog = new() { ["ok"] = false, ["error"] = "res://a.tscn has no node Nope" };
        JsonObject onlyWarnings = Failed("res://a.tscn has no node Nope", Entry("warning", "a warning", "res://a.gd", 1));
        JsonObject withoutReason = new() { ["ok"] = false };

        Assert.Equal("save_scene failed: res://a.tscn has no node Nope", HeadlessRunner.FailureMessage("save_scene", withoutLog));
        Assert.Equal("save_scene failed: res://a.tscn has no node Nope", HeadlessRunner.FailureMessage("save_scene", onlyWarnings));
        Assert.Equal("save_scene failed: it gave no reason", HeadlessRunner.FailureMessage("save_scene", withoutReason));
    }

    [Fact]
    public void AFailureQuotesTwentyErrorsAndCountsTheRest()
    {
        JsonObject reply = Failed("boom", [.. Enumerable.Range(1, 25).Select(index => Entry("error", $"error {index}", "res://a.gd", index))]);

        string[] lines = HeadlessRunner.FailureMessage("save_scene", reply).Split('\n');

        Assert.Equal(23, lines.Length);
        Assert.Equal("save_scene failed: boom", lines[0]);
        Assert.Equal("Godot logged:", lines[1]);
        Assert.Equal("error 1 (res://a.gd:1)", lines[2]);
        Assert.Equal("error 20 (res://a.gd:20)", lines[21]);
        Assert.Equal("(5 more)", lines[22]);
    }

    private static JsonObject Failed(string error, params JsonObject[] logged) =>
        new()
        {
            ["ok"] = false,
            ["error"] = error,
            ["engineErrors"] = new JsonArray([.. logged]),
        };

    private static JsonObject Entry(string type, string message, string file, int line) =>
        new()
        {
            ["type"] = type,
            ["message"] = message,
            ["file"] = file,
            ["line"] = line,
        };
}
