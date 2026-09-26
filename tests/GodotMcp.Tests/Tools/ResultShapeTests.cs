using System.Text.Json.Nodes;
using GodotMcp.Server.Tools;
using ModelContextProtocol;

namespace GodotMcp.Tests.Tools;

/// <summary>get_ui_elements' paging and run_script's value shape, without a game.</summary>
public sealed class ResultShapeTests
{
    [Fact]
    public void UiElementsPageCarriesTotalOffsetAndNext()
    {
        JsonObject reply = Elements(250);

        JsonObject first = RuntimeTools.PageElements(reply, 0, 100);
        JsonObject last = RuntimeTools.PageElements(reply, 200, 100);

        Assert.Equal(100, first["elements"]!.AsArray().Count);
        Assert.Equal("e0", first["elements"]![0]!["name"]!.GetValue<string>());
        Assert.Equal(250, first["total"]!.GetValue<int>());
        Assert.Equal(0, first["offset"]!.GetValue<int>());
        Assert.Equal(100, first["next"]!.GetValue<int>());
        Assert.Equal(50, last["elements"]!.AsArray().Count);
        Assert.Equal("e200", last["elements"]![0]!["name"]!.GetValue<string>());
        Assert.Null(last["next"]);
    }

    [Fact]
    public void UiElementsOffsetPastTheEndIsAnEmptyPage()
    {
        JsonObject page = RuntimeTools.PageElements(Elements(3), 10, 100);

        Assert.Empty(page["elements"]!.AsArray());
        Assert.Equal(3, page["total"]!.GetValue<int>());
        Assert.Null(page["next"]);
    }

    [Theory]
    [InlineData(-1, 100)]
    [InlineData(0, 0)]
    [InlineData(0, 501)]
    public void UiElementsPageRefusesABadOffsetOrLimit(int offset, int limit) =>
        Assert.Throws<McpException>(() => RuntimeTools.CheckPage(offset, limit));

    [Fact]
    public void AScriptValueWithinTheLimitComesBackWhole()
    {
        JsonObject shaped = RuntimeTools.ShapeScriptValue(JsonValue.Create(new string('v', RuntimeTools.MaxValueLength - 2)));

        Assert.Equal(RuntimeTools.MaxValueLength - 2, shaped["value"]!.GetValue<string>().Length);
        Assert.Null(shaped["valuePreview"]);
    }

    [Fact]
    public void ANullScriptValueIsKept() => Assert.Equal("""{"value":null}""", RuntimeTools.ShapeScriptValue(null).ToJsonString());

    [Fact]
    public void ALongScriptValueComesBackAsAPreviewAndItsLength()
    {
        JsonNode value = JsonValue.Create(new string('v', RuntimeTools.MaxValueLength));

        JsonObject shaped = RuntimeTools.ShapeScriptValue(value);

        // The serialised value is the string and its two quotes.
        Assert.Null(shaped["value"]);
        Assert.Equal(RuntimeTools.MaxValueLength + 2, shaped["valueLength"]!.GetValue<int>());
        string preview = shaped["valuePreview"]!.GetValue<string>();
        Assert.Equal(RuntimeTools.MaxValueLength, preview.Length);
        Assert.StartsWith("\"vvv", preview, StringComparison.Ordinal);
    }

    private static JsonObject Elements(int count) =>
        new() { ["elements"] = new JsonArray([.. Enumerable.Range(0, count).Select(index => new JsonObject { ["name"] = $"e{index}" })]) };
}
