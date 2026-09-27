using System.Reflection;
using GodotMcp.Dotnet.Core;

namespace GodotMcp.Tests.Dotnet;

public sealed class ClrOverloadResolverTests
{
    [Fact]
    public void PicksTheOverloadWhoseParameterTypeMatches()
    {
        MethodBase[] hits = Methods<Fighter>(nameof(Fighter.Hit));

        Assert.Equal(typeof(int), Choose(hits, 3).Method.GetParameters()[0].ParameterType);
        Assert.Equal(typeof(float), Choose(hits, 3f).Method.GetParameters()[0].ParameterType);
    }

    [Fact]
    public void PrefersAnExactMatchOverAnAssignableOne()
    {
        MethodBase[] takes = Methods<Mailbox>(nameof(Mailbox.Take));

        Assert.Equal("string Take(string value)", Signatures.Format(Choose(takes, "x").Method));
        Assert.Equal("string Take(object value)", Signatures.Format(Choose(takes, 5).Method));
    }

    [Fact]
    public void NullGoesToAReferenceParameterAndNotToAnInt()
    {
        OverloadChoice choice = Choose(Methods<Mailbox>(nameof(Mailbox.Aim)), [null]);

        Assert.Equal("string Aim(string? target)", Signatures.Format(choice.Method));
        Assert.Equal([null], choice.Arguments);
    }

    [Fact]
    public void ParamsPacksTrailingArguments()
    {
        MethodBase[] sums = Methods<Mailbox>(nameof(Mailbox.Sum));
        int[] given = [4, 5];

        Assert.Equal([1, 2, 3], (int[])Choose(sums, "s", 1, 2, 3).Arguments[1]!);
        Assert.Equal(Array.Empty<int>(), Choose(sums, "s").Arguments[1]);
        Assert.Same(given, Choose(sums, "s", given).Arguments[1]);
    }

    [Fact]
    public void AnOptionalParameterLeftOffTakesItsDefault() => Assert.Equal([5, 1], Choose(Methods<Fighter>(nameof(Fighter.Heal)), 5).Arguments);

    [Fact]
    public void AnAmbiguousCallListsEverySignature() =>
        Assert.Equal(
            "2 overloads of 'Mix' take these arguments; cast an argument to its parameter's type to pick one:\n"
                + "  string Mix(object a, string b)\n"
                + "  string Mix(string a, object b)",
            Refusal(Methods<Mailbox>(nameof(Mailbox.Mix)), "x", "y")
        );

    [Fact]
    public void NoMatchIsRefused() =>
        Assert.Equal(
            "no overload of 'Hit' takes these arguments:\n"
                + "  string Hit(int amount): parameter 'amount': a string is not an int\n"
                + "  string Hit(float amount): parameter 'amount': a string is not a float",
            Refusal(Methods<Fighter>(nameof(Fighter.Hit)), "x")
        );

    [Fact]
    public void AGenericMethodDefinitionIsRefused()
    {
        string refusal = Refusal(Methods<Mailbox>(nameof(Mailbox.Echo)), 3);

        Assert.StartsWith("no overload of 'Echo' takes these arguments:\n", refusal, StringComparison.Ordinal);
        Assert.EndsWith(
            ": it is generic, and Call cannot infer its type arguments; cs_call with options.typeArgs calls it",
            refusal,
            StringComparison.Ordinal
        );
    }

    [Fact]
    public void AnOutParameterIsRefused() =>
        Assert.EndsWith(
            ": parameter 'label' is out, which Call cannot pass",
            Refusal(Methods<Mailbox>(nameof(Mailbox.TryOpen)), 3, null),
            StringComparison.Ordinal
        );

    [Fact]
    public void ARefParameterIsRefused() =>
        Assert.EndsWith(
            ": parameter 'value' is ref, which Call cannot pass",
            Refusal(Methods<Mailbox>(nameof(Mailbox.Bump)), 3),
            StringComparison.Ordinal
        );

    [Fact]
    public void ASingleNullForParamsBindsAsOneNullArgument()
    {
        OverloadChoice choice = Choose(Methods<Mailbox>(nameof(Mailbox.Count)), [null]);

        Assert.Equal([null], choice.Arguments);
    }

    private static MethodBase[] Methods<T>(string name) => [.. typeof(T).GetMethods().Where(method => method.Name == name)];

    private static OverloadChoice Choose(MethodBase[] candidates, params object?[] args) => ClrOverloadResolver.Choose(candidates, args);

    private static string Refusal(MethodBase[] candidates, params object?[] args) =>
        Assert.Throws<OverloadException>(() => ClrOverloadResolver.Choose(candidates, args)).Message;
}

/// <summary>Overloads that only a CLR argument's runtime type, or its being null, tells apart.</summary>
internal sealed class Mailbox
{
    private readonly string _tag = "";

    public string Take(object value) => _tag + value;

    public string Take(string value) => _tag + value;

    public string Aim(int range) => _tag + range;

    public string Aim(string? target) => _tag + target;

    public int Sum(string label, params int[] values) => (_tag + label).Length + values.Sum();

    public string Mix(object a, string b) => _tag + a + b;

    public string Mix(string a, object b) => _tag + a + b;

    public string Echo<T>(T value) => _tag + value;

    public bool TryOpen(int code, out string label)
    {
        label = _tag + code;
        return code > 0;
    }

    public void Bump(ref int value) => value += _tag.Length + 1;

    public int Count(params string?[]? items) => _tag.Length + (items?.Length ?? -1);
}
