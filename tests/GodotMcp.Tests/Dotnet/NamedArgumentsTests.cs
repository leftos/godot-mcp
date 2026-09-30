using System.Reflection;
using System.Text.Json.Nodes;
using GodotMcp.Dotnet.Core;

namespace GodotMcp.Tests.Dotnet;

public sealed class NamedArgumentsTests
{
    private readonly FakeResolver _resolver = new();

    [Fact]
    public void ArgumentsBindByNameWhateverTheirOrder()
    {
        OverloadChoice choice = Bind(nameof(ToolBench.Needed), """{"label": "x", "amount": 2}""");

        object?[] expected = [2, "x"];
        Assert.Equal(nameof(ToolBench.Needed), choice.Method.Name);
        Assert.Equal(expected, choice.Arguments);
    }

    [Fact]
    public void AnOmittedParameterTakesItsDefault()
    {
        OverloadChoice choice = Bind(nameof(ToolBench.Arranged), """{"amount": 1}""");

        object?[] expected = [1, null, Color.Green];
        Assert.Equal(expected, choice.Arguments);
    }

    [Fact]
    public void AnOmittedNullableParameterIsNull()
    {
        OverloadChoice choice = Bind(nameof(ToolBench.Freed), "{}");

        object?[] expected = [null, null];
        Assert.Equal(expected, choice.Arguments);
    }

    [Fact]
    public void ADeclaredNullStaysNull()
    {
        OverloadChoice choice = Bind(nameof(ToolBench.Arranged), """{"amount": 1, "label": null}""");

        object?[] expected = [1, null, Color.Green];
        Assert.Equal(expected, choice.Arguments);
    }

    [Fact]
    public void AnInParameterBindsLikeAPlainValue()
    {
        OverloadChoice choice = Bind(nameof(ToolBench.Inlined), """{"code": 4}""");
        object?[] arguments = [.. choice.Arguments];

        Assert.Equal([4], arguments);
        Assert.Equal(4, choice.Method.Invoke(new ToolBench(), arguments));
    }

    [Fact]
    public void AnOmittedVariadicIsAnEmptyArray()
    {
        OverloadChoice choice = Bind(nameof(ToolBench.Rest), "{}");

        Assert.Empty(Assert.IsType<string[]>(choice.Arguments[0]));
    }

    [Fact]
    public void AVariadicTakesItsValues()
    {
        OverloadChoice choice = Bind(nameof(ToolBench.Rest), """{"names": ["a", "b"]}""");

        Assert.Equal(["a", "b"], Assert.IsType<string[]>(choice.Arguments[0]));
    }

    [Fact]
    public void AnEmptyObjectBindsAParameterlessMethod()
    {
        OverloadChoice choice = Bind(nameof(ToolBench.Ping), "{}");

        Assert.Empty(choice.Arguments);
    }

    [Fact]
    public void AnUnknownNameListsTheParameters() =>
        Assert.Equal("Needed has no parameter 'amounts'; it takes amount, label.", Refusal(nameof(ToolBench.Needed), """{"amounts": 1}"""));

    [Fact]
    public void AnUnknownNameDifferingOnlyInCaseSuggestsIt() =>
        Assert.Equal(
            "Arranged has no parameter 'Amount'; it takes amount, label, tint. Did you mean 'amount'?",
            Refusal(nameof(ToolBench.Arranged), """{"Amount": 1}""")
        );

    [Fact]
    public void AnUnknownNameOnAParameterlessMethodSaysItTakesNone() =>
        Assert.Equal("Ping has no parameter 'x'; it takes none.", Refusal(nameof(ToolBench.Ping), """{"x": 1}"""));

    [Fact]
    public void AMissingRequiredParameterIsNamed() =>
        Assert.Equal("Needed needs 'amount' (int); list_game_tools shows its schema.", Refusal(nameof(ToolBench.Needed), """{"label": "x"}"""));

    [Fact]
    public void AMissingParameterIsRefusedBeforeAGivenOneIsConverted() =>
        Assert.Equal("Needed needs 'amount' (int); list_game_tools shows its schema.", Refusal(nameof(ToolBench.Needed), """{"label": 5}"""));

    [Fact]
    public void AValueOfTheWrongTypeNamesItsParameter() =>
        Assert.Equal("parameter 'amount': expected an int, got \"x\"", Refusal(nameof(ToolBench.Needed), """{"amount": "x", "label": "y"}"""));

    [Fact]
    public void AHandleOrANodeMarkerResolvesThroughTheResolver()
    {
        OverloadChoice byHandle = Bind(nameof(ToolBench.Warded), """{"shield": {"$handle": "h1.1"}, "plain": {"$handle": "h1.2"}}""");

        Assert.Same(_resolver.Shield, byHandle.Arguments[0]);
        Assert.Same(_resolver.Plain, byHandle.Arguments[1]);

        OverloadChoice byNode = Bind(nameof(ToolBench.Warded), """{"shield": {"$node": "/root/Shield"}}""");

        Assert.Same(_resolver.Shield, byNode.Arguments[0]);
        Assert.Null(byNode.Arguments[1]);
    }

    [Fact]
    public void AGoneHandleNamesItsParameter() =>
        Assert.Equal("parameter 'shield': handle h9.9 is gone", Refusal(nameof(ToolBench.Warded), """{"shield": {"$handle": "h9.9"}}"""));

    [Fact]
    public void ARecordParameterIsBuiltFromItsObject()
    {
        OverloadChoice choice = Bind(nameof(ToolBench.Assigned), """{"choice": {"Label": "A", "Cost": 2}}""");

        Assert.Equal(new Choice("A", 2), Assert.IsType<Choice>(choice.Arguments[0]));
    }

    [Fact]
    public void AMethodNoGameToolCanBeBuiltFromIsRefused() =>
        Assert.Equal("parameter 'count' is out int, which a game tool cannot pass", Refusal(nameof(ToolBench.Counted), "{}"));

    [Fact]
    public void ATokenParameterAlwaysBindsNone()
    {
        OverloadChoice choice = Bind(nameof(ToolBench.Waited), """{"amount": 1}""");
        object?[] expected = [1, CancellationToken.None];

        Assert.Equal(expected, choice.Arguments);
    }

    [Fact]
    public void ATokenIsNotBindableByName() =>
        Assert.Equal("Waited has no parameter 'token'; it takes amount.", Refusal(nameof(ToolBench.Waited), """{"amount": 1, "token": {}}"""));

    [Fact]
    public void AnUnknownKeyIsRefusedBeforeAValueIsConverted() =>
        Assert.Equal(
            "Needed has no parameter 'bogus'; it takes amount, label.",
            Refusal(nameof(ToolBench.Needed), """{"amount": "x", "bogus": 1}""")
        );

    [Fact]
    public void ARecordIsNotBuiltWhenAKeyIsUnknown()
    {
        int before = Ticket.Built;

        Assert.Equal(
            "Printed has no parameter 'bogus'; it takes ticket.",
            Refusal(nameof(ToolBench.Printed), """{"ticket": {"label": "x"}, "bogus": 1}""")
        );
        Assert.Equal(before, Ticket.Built);

        OverloadChoice choice = Bind(nameof(ToolBench.Printed), """{"ticket": {"label": "x"}}""");

        Assert.Equal(before + 1, Ticket.Built);
        Assert.Equal("x", Assert.IsType<Ticket>(choice.Arguments[0]).Label);
    }

    [Fact]
    public void ACaseHintListsEveryParameterThatMatches() =>
        Assert.Equal(
            "Cased has no parameter 'VALUE'; it takes value, Value. Did you mean 'value' or 'Value'?",
            Refusal(nameof(ToolBench.Cased), """{"VALUE": 1}""")
        );

    private OverloadChoice Bind(string method, string args) =>
        NamedArguments.Bind(typeof(ToolBench).GetMethod(method)!, JsonNode.Parse(args)!.AsObject(), _resolver);

    private string Refusal(string method, string args) => Assert.Throws<OverloadException>(() => Bind(method, args)).Message;
}
