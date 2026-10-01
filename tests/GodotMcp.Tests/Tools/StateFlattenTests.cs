using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using GodotMcp.Server.Tools;

namespace GodotMcp.Tests.Tools;

/// <summary>A kept state read's flattening: one value per leaf of each node's state, keyed by its dotted path.</summary>
public sealed class StateFlattenTests
{
    private const string Path = "/root/Main/Hud";

    private static readonly JsonSerializerOptions Relaxed = new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    [Fact]
    public void NestedMembersJoinWithDots() =>
        Assert.Equal("""{"a.b.c":1,"a.d":"x","e":true}""", Leaves("""{"a": {"b": {"c": 1}, "d": "x"}, "e": true}"""));

    [Fact]
    public void ListElementsAreKeyedByIndex()
    {
        Assert.Equal(
            """{"seats[0].hp":3,"seats[1].hp":4,"grid[0][0]":1,"grid[0][1]":2,"tags[0]":"a"}""",
            Leaves("""{"seats": [{"hp": 3}, {"hp": 4}], "grid": [[1, 2]], "tags": ["a"]}""")
        );
    }

    [Fact]
    public void AStateThatIsAListIsKeyedFromItsFirstIndex() => Assert.Equal("""{"[0].hp":1,"[1]":2}""", Leaves("""[{"hp": 1}, 2]"""));

    [Theory]
    [InlineData("7")]
    [InlineData("\"text\"")]
    [InlineData("true")]
    [InlineData("null")]
    public void AScalarStateIsTheOneKeyDollar(string state) => Assert.Equal($$"""{"$":{{state}}}""", Leaves(state));

    [Fact]
    public void AnEmptyObjectOrListIsOneLeaf()
    {
        Assert.Equal("""{"a":{},"b":[],"c.d":[]}""", Leaves("""{"a": {}, "b": [], "c": {"d": []}}"""));
        Assert.Equal("""{"$":{}}""", Leaves("{}"));
        Assert.Equal("""{"$":[]}""", Leaves("[]"));
    }

    [Fact]
    public void CutMarksAreStringLeaves()
    {
        Assert.Equal(
            """{"deep.deeper":"<depth limit: Dictionary>","big":"<size limit>"}""",
            Leaves("""{"deep": {"deeper": "<depth limit: Dictionary>"}, "big": "<size limit>"}""")
        );
    }

    [Fact]
    public void AKeysFilteredStateKeepsItsKeysWholeAndFlattensBelowThem()
    {
        Assert.Equal(
            """{"seats[0].hp":7,"nested.deep.x[0]":1,"nested.deep.y":{}}""",
            Leaves("""{"seats[0].hp": 7, "nested.deep": {"x": [1], "y": {}}}""")
        );
    }

    [Fact]
    public void AStateCutToAPreviewIsItsTwoKeys() =>
        Assert.Equal("""{"valuePreview":"{\"a\":","valueLength":5000}""", Leaves("""{"valuePreview": "{\"a\":", "valueLength": 5000}"""));

    [Fact]
    public void AnErroredNodeIsItsErrorLeaf()
    {
        JsonObject result = Result(
            new JsonObject
            {
                ["path"] = Path,
                ["class"] = "Node",
                ["error"] = "_mcp_state raised: boom",
            }
        );

        Assert.Equal("""{"error":"_mcp_state raised: boom"}""", Text(StateFlatten.Nodes(result)[Path]));
    }

    [Fact]
    public void EachNodeIsKeyedByItsPathAndItsWarningIsNotALeaf()
    {
        JsonObject result = Result(
            new JsonObject
            {
                ["path"] = "/root/A",
                ["class"] = "Node",
                ["state"] = new JsonObject { ["hp"] = 1 },
                ["warning"] = "w",
            },
            new JsonObject
            {
                ["path"] = "/root/B",
                ["class"] = "Node",
                ["state"] = 2,
            }
        );

        Assert.Equal("""{"/root/A":{"hp":1},"/root/B":{"$":2}}""", Text(StateFlatten.Nodes(result)));
    }

    [Fact]
    public void AnEmptyReadIsNoNodesAndOmittedIsNotStored()
    {
        JsonObject empty = new()
        {
            ["frame"] = 10,
            ["nodes"] = new JsonArray(),
            ["total"] = 3,
            ["omitted"] = new JsonObject { ["count"] = 3, ["paths"] = new JsonArray("/root/A", "/root/B", "/root/C") },
        };

        Assert.Equal("{}", Text(StateFlatten.Nodes(empty)));
    }

    /// <summary>The leaves of one node whose state is <paramref name="stateJson"/>, as JSON.</summary>
    private static string Leaves(string stateJson)
    {
        JsonObject result = Result(
            new JsonObject
            {
                ["path"] = Path,
                ["class"] = "Node",
                ["state"] = JsonNode.Parse(stateJson),
            }
        );
        return Text(StateFlatten.Nodes(result)[Path]);
    }

    /// <summary>The value as JSON with only the escapes JSON needs, so a mark's angle brackets read as they are.</summary>
    private static string Text(JsonNode? value) => value!.ToJsonString(Relaxed);

    private static JsonObject Result(params JsonObject[] entries) =>
        new()
        {
            ["frame"] = 10,
            ["nodes"] = new JsonArray([.. entries]),
            ["total"] = entries.Length,
        };
}
