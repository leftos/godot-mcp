using System.Text.Json.Nodes;
using GodotMcp.Server.Tools;
using ModelContextProtocol;

namespace GodotMcp.Tests.Tools;

/// <summary>How get_game_state shapes the bridge's state reply: the keys filter, the per-node cut, the list budget and omitted.</summary>
public sealed class StateMergeTests
{
    private const string Hud = """{"path":"/root/Main/Hud","class":"Control","state":{"hp":3,"seats":[{"hp":7},{"hp":9}],"turn":"alex"}}""";

    [Fact]
    public void EveryKeyIsKeptWhenNoneAreAsked()
    {
        JsonObject result = StateMerge.Shape(Reply([Hud], total: 1), null, hintWhenEmpty: true);

        Assert.Equal("""{"frame":120,"nodes":[""" + Hud + """],"total":1}""", result.ToJsonString());
    }

    [Fact]
    public void KeysKeepTheirValuesUnderTheKeyAsGivenAndAMissingKeyIsLeftOut()
    {
        JsonObject result = StateMerge.Shape(Reply([Hud], total: 1), ["turn", "seats[1].hp", "mana", "seats[5].hp", "turn.x"], true);

        Assert.Equal("""{"turn":"alex","seats[1].hp":9}""", result["nodes"]![0]!["state"]!.ToJsonString());
    }

    [Theory]
    [InlineData("seats[0]", """{"hp":7}""")]
    [InlineData("seats", """[{"hp":7},{"hp":9}]""")]
    [InlineData("[1]", null)]
    [InlineData("seats[x].hp", null)]
    [InlineData("seats[0", null)]
    [InlineData("hp.", null)]
    [InlineData("seats[0][0]", null)]
    [InlineData("seats[-1]", null)]
    [InlineData("seats[2]", null)]
    [InlineData("hp[0]", null)]
    public void ADottedPathFindsWhatItNames(string path, string? expected)
    {
        JsonNode state = JsonNode.Parse("""{"hp":3,"seats":[{"hp":7},{"hp":9}]}""")!;

        bool found = StateMerge.TryFind(state, path, out JsonNode? value);

        Assert.Equal(expected is not null, found);
        Assert.Equal(expected, found ? value!.ToJsonString() : null);
    }

    [Fact]
    public void AnIndexCanStartAPathIntoAListState()
    {
        Assert.True(StateMerge.TryFind(JsonNode.Parse("""[{"hp":1},{"hp":2}]"""), "[1].hp", out JsonNode? value));
        Assert.Equal(2, value!.GetValue<int>());
    }

    [Fact]
    public void APathReachingACutMarkKeepsTheMarkAsItsValue()
    {
        const string Cut =
            """{"path":"/root/Main/Cut","class":"Node","state":{"a":{"b":"<depth limit: Dictionary>"},"c":"<size limit>","d":"<x>"}}""";

        JsonObject result = StateMerge.Shape(Reply([Cut], total: 1), ["a.b.x[0]", "a.b", "c.e", "d.e"], true);

        JsonObject state = result["nodes"]![0]!["state"]!.AsObject();
        Assert.Equal(["a.b.x[0]", "a.b", "c.e"], state.Select(key => key.Key));
        Assert.Equal("<depth limit: Dictionary>", state["a.b.x[0]"]!.GetValue<string>());
        Assert.Equal("<depth limit: Dictionary>", state["a.b"]!.GetValue<string>());
        Assert.Equal("<size limit>", state["c.e"]!.GetValue<string>());
    }

    [Fact]
    public void KeysLeaveAnErrorEntryAlone()
    {
        const string Broken = """{"path":"/root/Main/Broken","class":"Node","error":"_mcp_state raised: boom"}""";

        JsonObject result = StateMerge.Shape(Reply([Broken], total: 1), ["hp"], true);

        Assert.Equal(Broken, result["nodes"]![0]!.ToJsonString());
    }

    [Fact]
    public void AStateLongerThanFourThousandCharactersBecomesAPreview()
    {
        string text = new('a', StateMerge.MaxStateLength);
        string entry = Entry("/root/Main/Long", new JsonObject { ["text"] = text });

        JsonNode state = StateMerge.Shape(Reply([entry], total: 1), null, true)["nodes"]![0]!["state"]!;

        string json = new JsonObject { ["text"] = text }.ToJsonString();
        Assert.Equal(json[..StateMerge.MaxStateLength], state["valuePreview"]!.GetValue<string>());
        Assert.Equal(json.Length, state["valueLength"]!.GetValue<int>());
    }

    [Fact]
    public void AStateOfFourThousandCharactersIsKept()
    {
        // {"text":"…"} is 11 characters around the text.
        JsonObject value = new() { ["text"] = new string('a', StateMerge.MaxStateLength - 11) };
        string entry = Entry("/root/Main/Full", value);

        JsonNode state = StateMerge.Shape(Reply([entry], total: 1), null, true)["nodes"]![0]!["state"]!;

        Assert.Equal(value.ToJsonString(), state.ToJsonString());
    }

    [Fact]
    public void TheCutAppliesAfterTheKeys()
    {
        JsonObject value = new() { ["hp"] = 3, ["log"] = new string('a', 2 * StateMerge.MaxStateLength) };

        JsonNode state = StateMerge.Shape(Reply([Entry("/root/Main/Hud", value)], total: 1), ["hp"], true)["nodes"]![0]!["state"]!;

        Assert.Equal("""{"hp":3}""", state.ToJsonString());
    }

    [Fact]
    public void TheNodesAfterTheOneCrossingTheBudgetMoveToOmittedBeforeTheBridgesOwn()
    {
        // Each entry is a little over 3000 characters, so the fourteenth crosses 40000 and the fifteenth onward move.
        string[] entries =
        [
            .. Enumerable.Range(0, 20).Select(index => Entry($"/root/Main/N{index}", new JsonObject { ["text"] = new string('a', 3000) })),
        ];
        JsonObject bridgeOmitted = new()
        {
            ["count"] = 30,
            ["paths"] = new JsonArray([.. Enumerable.Range(20, 20).Select(i => (JsonNode)$"/root/Main/N{i}")]),
        };

        JsonObject result = StateMerge.Shape(Reply(entries, total: 50, bridgeOmitted), null, true);

        JsonArray nodes = result["nodes"]!.AsArray();
        Assert.Equal(14, nodes.Count);
        Assert.True(nodes.Take(13).Sum(node => node!.ToJsonString().Length) <= StateMerge.MaxNodesLength);
        Assert.True(nodes.Sum(node => node!.ToJsonString().Length) > StateMerge.MaxNodesLength);
        Assert.Equal(50, result["total"]!.GetValue<int>());
        JsonObject omitted = result["omitted"]!.AsObject();
        Assert.Equal(6 + 30, omitted["count"]!.GetValue<int>());
        Assert.Equal(
            Enumerable.Range(14, StateMerge.MaxOmittedPaths).Select(i => $"/root/Main/N{i}"),
            omitted["paths"]!.AsArray().Select(path => path!.GetValue<string>())
        );
    }

    [Fact]
    public void TheBridgesOmittedPassesThroughWhenTheBudgetHolds()
    {
        JsonObject bridgeOmitted = new() { ["count"] = 2, ["paths"] = new JsonArray("/root/Main/B", "/root/Main/C") };

        JsonObject result = StateMerge.Shape(Reply([Hud], total: 3, bridgeOmitted), null, true);

        Assert.Equal("""{"count":2,"paths":["/root/Main/B","/root/Main/C"]}""", result["omitted"]!.ToJsonString());
    }

    [Fact]
    public void NoMarkedNodeGivesTheHintOnlyForAWholeTreeRead()
    {
        JsonObject whole = StateMerge.Shape(Reply([], total: 0), null, hintWhenEmpty: true);
        JsonObject under = StateMerge.Shape(Reply([], total: 0), null, hintWhenEmpty: false);

        Assert.Equal(
            "No node is in the mcp_state group. A GDScript node joins it and defines _mcp_state() returning a Dictionary; a C# node "
                + "defines _McpState(). Until a game opts in, snapshot_subtree and get_ui_elements read the tree.",
            whole["hint"]!.GetValue<string>()
        );
        Assert.Equal("""{"frame":120,"nodes":[],"total":0}""", under.ToJsonString());
    }

    [Fact]
    public void CSharpEntriesAreFilledByIdInTreeOrderBetweenGdscriptOnes()
    {
        string[] entries =
        [
            Entry("/root/Main/A", new JsonObject { ["hp"] = 1 }),
            CSharpEntry("/root/Main/B", "11"),
            Entry("/root/Main/C", new JsonObject { ["hp"] = 3 }),
            CSharpEntry("/root/Main/D", "12", Both),
        ];
        string csharp = HelperReply("""{"id":"12","state":{"hp":4}}""", """{"id":"11","state":{"hp":2,"big":18446744073709551615}}""");

        JsonObject result = StateMerge.Shape(Reply(entries, total: 4, csharp: csharp), null, true);

        Assert.Equal(
            "["
                + """{"path":"/root/Main/A","class":"Node","state":{"hp":1}},"""
                + """{"path":"/root/Main/B","class":"Node","state":{"hp":2,"big":18446744073709551615}},"""
                + """{"path":"/root/Main/C","class":"Node","state":{"hp":3}},"""
                + """{"path":"/root/Main/D","class":"Node","warning":"reads _McpState; its _mcp_state is not read","state":{"hp":4}}"""
                + "]",
            result["nodes"]!.ToJsonString()
        );
        Assert.False(result.ContainsKey("csharp"), result.ToJsonString());
    }

    [Fact]
    public void KeysAndTheCutApplyToMergedEntries()
    {
        string log = new('a', 2 * StateMerge.MaxStateLength);
        string[] entries = [CSharpEntry("/root/Main/B", "11"), CSharpEntry("/root/Main/D", "12")];
        string csharp = HelperReply("""{"id":"11","state":{"hp":2,"mana":5}}""", $$$"""{"id":"12","state":{"hp":4,"log":"{{{log}}}"}}""");

        JsonArray whole = StateMerge.Shape(Reply(entries, total: 2, csharp: csharp), null, true)["nodes"]!.AsArray();
        JsonArray kept = StateMerge.Shape(Reply(entries, total: 2, csharp: csharp), ["hp"], true)["nodes"]!.AsArray();

        Assert.Equal("""{"hp":2,"mana":5}""", whole[0]!["state"]!.ToJsonString());
        Assert.True(whole[1]!["state"]!["valueLength"]!.GetValue<int>() > StateMerge.MaxStateLength, whole[1]!.ToJsonString());
        Assert.Equal(["""{"hp":2}""", """{"hp":4}"""], kept.Select(node => node!["state"]!.ToJsonString()));
    }

    [Fact]
    public void MergedEntriesPastTheBudgetMoveToOmitted()
    {
        // Each merged entry is a little over 3000 characters, so the fourteenth crosses 40000 and the fifteenth onward move.
        string text = new('a', 3000);
        string[] entries = [.. Enumerable.Range(0, 20).Select(index => CSharpEntry($"/root/Main/N{index}", $"{index}"))];
        string csharp = HelperReply([.. Enumerable.Range(0, 20).Select(index => $$$"""{"id":"{{{index}}}","state":{"text":"{{{text}}}"}}""")]);

        JsonObject result = StateMerge.Shape(Reply(entries, total: 20, csharp: csharp), null, true);

        Assert.Equal(14, result["nodes"]!.AsArray().Count);
        Assert.Equal(20, result["total"]!.GetValue<int>());
        Assert.Equal(6, result["omitted"]!["count"]!.GetValue<int>());
        Assert.Equal("/root/Main/N14", result["omitted"]!["paths"]![0]!.GetValue<string>());
    }

    [Fact]
    public void AnEntryTheBridgeReadItselfKeepsItsStateOverAMissingAnswer()
    {
        const string Missing = "CsProbe.Plain has no _McpState() (an instance method with no parameters, any accessibility)";
        string[] entries = [Entry("/root/Main/B", new JsonObject { ["from"] = "_mcp_state" }), CSharpEntry("/root/Main/D", "12")];
        string csharp = HelperReply(
            """{"id":"11","missing":true,"error":"CsProbe.Both has no _McpState()"}""",
            $$"""{"id":"12","missing":true,"error":"{{Missing}}"}"""
        );

        JsonArray nodes = StateMerge.Shape(Reply(entries, total: 2, csharp: csharp), null, true)["nodes"]!.AsArray();

        Assert.Equal("""{"path":"/root/Main/B","class":"Node","state":{"from":"_mcp_state"}}""", nodes[0]!.ToJsonString());
        Assert.Equal($$"""{"path":"/root/Main/D","class":"Node","error":"{{Missing}}"}""", nodes[1]!.ToJsonString());
    }

    [Fact]
    public void AnErrorAnswerAndAnUnansweredIdBecomeTheEntrysError()
    {
        string[] entries = [CSharpEntry("/root/Main/B", "11"), CSharpEntry("/root/Main/D", "12")];
        string csharp = HelperReply("""{"id":"11","error":"InvalidOperationException: state broke"}""");

        JsonArray nodes = StateMerge.Shape(Reply(entries, total: 2, csharp: csharp), null, true)["nodes"]!.AsArray();

        Assert.Equal("InvalidOperationException: state broke", nodes[0]!["error"]!.GetValue<string>());
        Assert.Equal(StateMerge.NoHelperEntry, nodes[1]!["error"]!.GetValue<string>());
        Assert.All(nodes, node => Assert.False(node!.AsObject().ContainsKey("id"), node.ToJsonString()));
    }

    [Theory]
    [InlineData("not json", "The C# helper's reply is not JSON: ")]
    [InlineData("[1]", "The C# helper's reply is not JSON: it is not a JSON object.")]
    [InlineData("""{"ok":false,"error":"Unknown op 'state'."}""", "The C# helper refused the request: Unknown op 'state'.")]
    public void AMalformedOrRefusedCSharpReplyIsAClearError(string csharp, string reason)
    {
        JsonNode reply = Reply([CSharpEntry("/root/Main/B", "11")], total: 1, csharp: csharp);

        McpException refused = Assert.Throws<McpException>(() => StateMerge.Shape(reply, null, true));

        Assert.StartsWith("get_game_state could not read the C# helper's state reply: " + reason, refused.Message, StringComparison.Ordinal);
    }

    private const string Both = "reads _McpState; its _mcp_state is not read";

    /// <summary>A C# node's entry as the bridge leaves it for the server: {path, class, id, warning?}.</summary>
    private static string CSharpEntry(string path, string id, string? warning = null)
    {
        JsonObject entry = new()
        {
            ["path"] = path,
            ["class"] = "Node",
            ["id"] = id,
        };
        if (warning is not null)
        {
            entry["warning"] = warning;
        }

        return entry.ToJsonString();
    }

    /// <summary>The C# helper's state reply holding the given entries.</summary>
    private static string HelperReply(params string[] answers) => $$$"""{"ok":true,"result":{"nodes":[{{{string.Join(',', answers)}}}]}}""";

    private static string Entry(string path, JsonObject state) =>
        new JsonObject
        {
            ["path"] = path,
            ["class"] = "Node",
            ["state"] = state,
        }.ToJsonString();

    /// <summary>The bridge's reply at frame 120, with the C# helper's reply string when given, parsed from JSON as the wire gives it.</summary>
    private static JsonNode Reply(string[] entries, int total, JsonObject? omitted = null, string? csharp = null)
    {
        string tail = omitted is null ? string.Empty : $",\"omitted\":{omitted.ToJsonString()}";
        string helper = csharp is null ? string.Empty : $",\"csharp\":{JsonValue.Create(csharp).ToJsonString()}";
        return JsonNode.Parse($"{{\"frame\":120,\"nodes\":[{string.Join(',', entries)}],\"total\":{total}{tail}{helper}}}")!;
    }
}
