using System.Reflection;
using System.Text.Json.Nodes;
using GodotMcp.Dotnet.Core;

namespace GodotMcp.Tests.Dotnet;

public sealed class OverloadResolverTests
{
    private static readonly MethodBase[] HitOverloads =
    [
        typeof(Fighter).GetMethod(nameof(Fighter.Hit), [typeof(int)])!,
        typeof(Fighter).GetMethod(nameof(Fighter.Hit), [typeof(float)])!,
    ];

    private readonly FakeResolver _resolver = new();

    [Fact]
    public void AnIntFitsBothHitOverloadsSoTheCallIsAmbiguous() =>
        Assert.Equal(
            "2 overloads of 'Hit' take these arguments; pass options.signature:\n  string Hit(int amount) — options.signature [\"int\"]\n"
                + "  string Hit(float amount) — options.signature [\"float\"]",
            Refusal(HitOverloads, "[3]")
        );

    [Fact]
    public void TheAmbiguityErrorPrintsEachSignatureArray()
    {
        MethodBase[] marks = VaultMethods(nameof(Vault.Mark));

        Assert.Equal(
            "2 overloads of 'Mark' take these arguments; pass options.signature:\n"
                + "  string Mark(int a, string b) — options.signature [\"int\", \"string\"]\n"
                + "  string Mark(long a, string? b) — options.signature [\"long\", \"string?\"]",
            Refusal(marks, "[1, \"x\"]")
        );
        Assert.Equal(typeof(long), Choose(marks, "[1, \"x\"]", signature: ["long", "string?"]).Method.GetParameters()[0].ParameterType);
    }

    [Fact]
    public void ABadHandleFailsOnlyThatCandidate()
    {
        OverloadChoice choice = Choose(VaultMethods(nameof(Vault.Take)), "[{\"$handle\": \"h9.9\"}]");

        Assert.Equal("string Take(out int count)", Signatures.Format(choice.Method));
    }

    [Fact]
    public void AnOutParameterTakesANullPlaceholder()
    {
        OverloadChoice choice = Choose(VaultMethods(nameof(Vault.TryOpen)), "[7, null]");

        object?[] arguments = [.. choice.Arguments];
        Assert.Equal([7, null], arguments);
        Assert.Equal(["secret"], choice.WrittenBack.Select(parameter => parameter.Name));
        Assert.Equal(true, choice.Method.Invoke(new Vault(), arguments));
        Assert.Equal("gold", arguments[1]);
    }

    [Fact]
    public void ARefParameterConverts()
    {
        OverloadChoice choice = Choose(VaultMethods(nameof(Vault.Grow)), "[4]");

        object?[] arguments = [.. choice.Arguments];
        Assert.Equal([4], arguments);
        Assert.Equal(["value"], choice.WrittenBack.Select(parameter => parameter.Name));
        choice.Method.Invoke(new Vault(), arguments);
        Assert.Equal(8, arguments[0]);
    }

    [Fact]
    public void APointerOrSpanParameterIsRefused() =>
        Assert.Equal(
            "no overload of 'Sum' takes these arguments:\n"
                + "  int Sum(Span<int> values): parameter 'values' is a Span<int>, a by-ref-like type cs_call cannot pass",
            Refusal(VaultMethods(nameof(Vault.Sum)), "[[1, 2]]")
        );

    [Fact]
    public void KeywordAndFullNameTypeArgsParse()
    {
        IReadOnlyList<Type> parsed = TypeArguments.Parse(
            ["int", " string ", "GodotMcp.Tests.Dotnet.Choice"],
            name => name == "GodotMcp.Tests.Dotnet.Choice" ? [typeof(Choice)] : []
        );

        Assert.Equal([typeof(int), typeof(string), typeof(Choice)], parsed);
    }

    [Fact]
    public void AFractionFitsOnlyHitFloat()
    {
        OverloadChoice choice = Choose(HitOverloads, "[3.5]");

        object?[] expected = [3.5f];
        Assert.Same(HitOverloads[1], choice.Method);
        Assert.Equal(expected, choice.Arguments);
    }

    [Fact]
    public void ASignaturePicksHitInt()
    {
        OverloadChoice choice = Choose(HitOverloads, "[3]", signature: ["int"]);

        object?[] expected = [3];
        Assert.Same(HitOverloads[0], choice.Method);
        Assert.Equal(expected, choice.Arguments);
    }

    [Fact]
    public void ASignatureMatchingNoOverloadIsRefused() =>
        Assert.Equal(
            "no overload of 'Hit' takes these arguments:\n  string Hit(int amount): its parameters are not (string)\n"
                + "  string Hit(float amount): its parameters are not (string)",
            Refusal(HitOverloads, "[3]", signature: ["string"])
        );

    [Fact]
    public void AnOptionalParameterTakesItsDefault()
    {
        object?[] expected = [4, 1];

        Assert.Equal(expected, Choose(Methods(nameof(Fighter.Heal)), "[4]").Arguments);
    }

    [Fact]
    public void TooManyArgumentsAreRefused() =>
        Assert.Equal(
            "no overload of 'Heal' takes these arguments:\n  int Heal(int amount, int bonus = 1): takes 1 to 2 arguments, got 3",
            Refusal(Methods(nameof(Fighter.Heal)), "[1, 2, 3]")
        );

    [Fact]
    public void AnArgumentThatDoesNotConvertNamesItsParameter() =>
        Assert.Equal(
            "no overload of 'Hit' takes these arguments:\n  string Hit(int amount): parameter 'amount': expected an int, got \"x\"\n"
                + "  string Hit(float amount): parameter 'amount': expected a float, got \"x\"",
            Refusal(HitOverloads, "[\"x\"]")
        );

    [Fact]
    public void AGenericMethodIsClosedOverTheTypeArguments()
    {
        OverloadChoice choice = Choose(Methods(nameof(Fighter.Echo)), "[5]", typeArgs: [typeof(int)]);

        Type[] closedOver = [typeof(int)];
        object?[] expected = [5];
        Assert.Equal(closedOver, choice.Method.GetGenericArguments());
        Assert.Equal(expected, choice.Arguments);
    }

    [Fact]
    public void AGenericMethodWithoutTypeArgumentsIsRefused() =>
        Assert.Equal(
            "no overload of 'Echo' takes these arguments:\n  T Echo<T>(T value): Echo<T> needs 1 type argument; pass options.typeArgs",
            Refusal(Methods(nameof(Fighter.Echo)), "[5]")
        );

    [Fact]
    public void AnInterfaceParameterTakesAHandle() =>
        Assert.Same(_resolver.Shield, Choose(Methods(nameof(Fighter.Guard)), "[{\"$handle\": \"h1.1\"}]").Arguments[0]);

    [Fact]
    public void AConstructorIsChosenLikeAMethod()
    {
        OverloadChoice choice = Choose([.. typeof(Choice).GetConstructors()], "[\"A\", 1]");

        object?[] expected = ["A", 1];
        Assert.IsAssignableFrom<ConstructorInfo>(choice.Method);
        Assert.Equal(expected, choice.Arguments);
    }

    private static MethodBase[] Methods(string name) => [.. typeof(Fighter).GetMethods().Where(method => method.Name == name)];

    private static MethodBase[] VaultMethods(string name) => [.. typeof(Vault).GetMethods().Where(method => method.Name == name)];

    private OverloadChoice Choose(MethodBase[] candidates, string args, string[]? signature = null, Type[]? typeArgs = null) =>
        OverloadResolver.Choose(candidates, JsonNode.Parse(args)!.AsArray(), signature, typeArgs, _resolver);

    private string Refusal(MethodBase[] candidates, string args, string[]? signature = null) =>
        Assert.Throws<OverloadException>(() => Choose(candidates, args, signature)).Message;
}
