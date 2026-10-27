using System.Text.Json;
using GodotMcp.Server.Tools;

namespace GodotMcp.Tests.Tools;

/// <summary>How a condition's equals binds: a JSON null is kept apart from a missing equals, and only there.</summary>
public sealed class EqualsBindingTests
{
    private const string NullEquals = """{"equals":null}""";

    [Fact]
    public void AnExplicitNullEqualsIsKeptApartFromAMissingOne()
    {
        Assert.Equal(JsonValueKind.Null, EqualsKind<WaitCondition>(NullEquals, condition => condition.EqualsValue));
        Assert.Equal(JsonValueKind.Null, EqualsKind<BatchStep>(NullEquals, step => step.EqualsValue));
        Assert.Equal(JsonValueKind.Null, EqualsKind<CaptureStart>(NullEquals, start => start.EqualsValue));
        Assert.Null(EqualsKind<WaitCondition>("{}", condition => condition.EqualsValue));
        Assert.Null(EqualsKind<BatchStep>("{}", step => step.EqualsValue));
        Assert.Null(EqualsKind<CaptureStart>("{}", start => start.EqualsValue));
        Assert.Null(EqualsKind<ScratchOptions>("""{"steps":null}""", options => options.Steps));
    }

    private static JsonValueKind? EqualsKind<T>(string json, Func<T, JsonElement?> field) =>
        field(JsonSerializer.Deserialize<T>(json, ToolJson.Options)!)?.ValueKind;
}
