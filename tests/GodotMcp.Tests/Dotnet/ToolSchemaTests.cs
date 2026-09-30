using System.Collections.Immutable;
using System.Reflection;
using System.Reflection.Emit;
using System.Text.Json.Nodes;
using GodotMcp.Dotnet.Core;

namespace GodotMcp.Tests.Dotnet;

public sealed class ToolSchemaTests
{
    private static readonly Func<Type, bool> IsNode = type => typeof(Mentor).IsAssignableFrom(type);

    [Fact]
    public void AScalarCarriesItsJsonTypeAndCSharpSpelling()
    {
        Schema("""{"type":"boolean","x-csharp":"bool"}""", Property(nameof(ToolBench.Flagged), typeof(bool)));
        Schema("""{"type":"string","x-csharp":"string"}""", Property(nameof(ToolBench.Text), typeof(string)));
        Schema("""{"type":"string","x-csharp":"char"}""", Property(nameof(ToolBench.Lettered), typeof(char)));
    }

    [Fact]
    public void ADateOrAnIdentifierIsAString()
    {
        Schema("""{"type":"string","x-csharp":"Guid"}""", Property(nameof(ToolBench.Identified), typeof(Guid)));
        Schema("""{"type":"string","x-csharp":"DateTime"}""", Property(nameof(ToolBench.Stamped), typeof(DateTime)));
        Schema("""{"type":"string","x-csharp":"DateTimeOffset"}""", Property(nameof(ToolBench.Shifted), typeof(DateTimeOffset)));
        Schema("""{"type":"string","x-csharp":"TimeSpan"}""", Property(nameof(ToolBench.Ticked), typeof(TimeSpan)));
    }

    [Fact]
    public void AFractionIsANumber()
    {
        Schema("""{"type":"number","x-csharp":"float"}""", Property(nameof(ToolBench.Small), typeof(float)));
        Schema("""{"type":"number","x-csharp":"double"}""", Property(nameof(ToolBench.Fraction), typeof(double)));
        Schema("""{"type":"number","x-csharp":"decimal"}""", Property(nameof(ToolBench.Money), typeof(decimal)));
    }

    [Fact]
    public void AnIntegerCarriesItsWidthsBounds()
    {
        JsonObject properties = Properties(
            nameof(ToolBench.Bounded),
            typeof(byte),
            typeof(sbyte),
            typeof(short),
            typeof(ushort),
            typeof(int),
            typeof(uint),
            typeof(long),
            typeof(ulong)
        );

        Bounds(properties, "small", "byte", 0m, 255m);
        Bounds(properties, "tiny", "sbyte", -128m, 127m);
        Bounds(properties, "medium", "short", -32768m, 32767m);
        Bounds(properties, "plus", "ushort", 0m, 65535m);
        Bounds(properties, "whole", "int", -2147483648m, 2147483647m);
        Bounds(properties, "count", "uint", 0m, 4294967295m);
        Bounds(properties, "large", "long", -9223372036854775808m, 9223372036854775807m);
        Bounds(properties, "big", "ulong", 0m, 18446744073709551615m);
    }

    [Fact]
    public void AnEnumListsItsNamesAndAFlagsEnumItsFlags()
    {
        JsonObject properties = Properties(nameof(ToolBench.Colored), typeof(Color), typeof(Access));

        Schema("""{"type":"string","enum":["Red","Green","Blue"],"x-csharp":"Color"}""", properties["color"]);
        Schema("""{"type":"string","x-flags":["None","Read","Write"],"x-csharp":"Access"}""", properties["access"]);
    }

    [Fact]
    public void ADefaultedEnumWritesItsName()
    {
        JsonObject properties = Properties(nameof(ToolBench.Tinted), typeof(Color), typeof(Access));

        Schema("""{"type":"string","enum":["Red","Green","Blue"],"x-csharp":"Color","default":"Green"}""", properties["tint"]);
        Schema("""{"type":"string","x-flags":["None","Read","Write"],"x-csharp":"Access","default":"Read, Write"}""", properties["access"]);
    }

    [Fact]
    public void ADefaultIsWrittenAndNothingDefaultsSoNothingIsRequired()
    {
        JsonObject properties = Properties(nameof(ToolBench.Supplied), typeof(int), typeof(string), typeof(int?), typeof(string));

        Schema("""{"type":"integer","minimum":-2147483648,"maximum":2147483647,"x-csharp":"int","default":3}""", properties["count"]);
        Schema("""{"type":"string","x-csharp":"string","default":"hi"}""", properties["label"]);
        Schema("""{"type":"integer","minimum":-2147483648,"maximum":2147483647,"x-csharp":"int?","default":null}""", properties["nothing"]);
        Schema("""{"type":"string","x-csharp":"string?","default":null}""", properties["maybe"]);
        Assert.Empty(Arguments(nameof(ToolBench.Supplied), typeof(int), typeof(string), typeof(int?), typeof(string))["required"]!.AsArray());
    }

    [Fact]
    public void ANullableParameterWithoutADefaultIsNotRequired()
    {
        JsonObject properties = Properties(nameof(ToolBench.Freed), typeof(int?), typeof(string));

        Schema("""{"type":"integer","minimum":-2147483648,"maximum":2147483647,"x-csharp":"int?"}""", properties["amount"]);
        Schema("""{"type":"string","x-csharp":"string?"}""", properties["label"]);
        Assert.Empty(Arguments(nameof(ToolBench.Freed), typeof(int?), typeof(string))["required"]!.AsArray());
    }

    [Fact]
    public void AVariadicIsANonRequiredArray()
    {
        Schema("""{"type":"array","items":{"type":"string"},"x-csharp":"params string[]"}""", Property(nameof(ToolBench.Rest), typeof(string[])));

        Assert.Empty(Arguments(nameof(ToolBench.Rest), typeof(string[]))["required"]!.AsArray());
    }

    [Fact]
    public void EveryCollectionFormIsAnArrayOfItsElements()
    {
        JsonObject properties = Properties(
            nameof(ToolBench.Listed),
            typeof(int[]),
            typeof(List<Color>),
            typeof(IList<int>),
            typeof(IReadOnlyList<string>),
            typeof(IEnumerable<int>),
            typeof(ICollection<int>),
            typeof(ImmutableArray<int>),
            typeof(ImmutableList<Color>)
        );
        Schema(Integers("int[]"), properties["numbers"]);
        Schema("""{"type":"array","items":{"type":"string","enum":["Red","Green","Blue"]},"x-csharp":"List<Color>"}""", properties["colors"]);
        Schema(Integers("IList<int>"), properties["ordered"]);
        Schema("""{"type":"array","items":{"type":"string"},"x-csharp":"IReadOnlyList<string>"}""", properties["labels"]);
        Schema(Integers("IEnumerable<int>"), properties["sequence"]);
        Schema(Integers("ICollection<int>"), properties["bag"]);
        Schema(Integers("ImmutableArray<int>"), properties["frozen"]);
        Schema(
            """{"type":"array","items":{"type":"string","enum":["Red","Green","Blue"]},"x-csharp":"ImmutableList<Color>"}""",
            properties["chained"]
        );
    }

    /// <summary>An array of <c>int</c> spelled as cs_members spells it.</summary>
    private static string Integers(string spelling) =>
        $$"""{"type":"array","items":{"type":"integer","minimum":-2147483648,"maximum":2147483647},"x-csharp":"{{spelling}}"}""";

    /// <summary>One <see cref="Purse"/> as an object of its one constructor's parameter, spelled as asked.</summary>
    private static string PurseSchema(string spelling) =>
        $$$"""
            {"type":"object","properties":{
            "Coins":{"type":"integer","minimum":-2147483648,"maximum":2147483647,"x-csharp":"int"}},
            "additionalProperties":false,"required":["Coins"],"x-csharp":"{{{spelling}}}"}
            """;

    [Fact]
    public void AHashSetIsAUniqueArray() =>
        Schema(
            """{"type":"array","items":{"type":"integer","minimum":-2147483648,"maximum":2147483647},"uniqueItems":true,"x-csharp":"HashSet<int>"}""",
            Property(nameof(ToolBench.Unique), typeof(HashSet<int>))
        );

    [Fact]
    public void AStringKeyedDictionaryIsAnObjectOfItsValues()
    {
        JsonObject properties = Properties(
            nameof(ToolBench.Mapped),
            typeof(Dictionary<string, int>),
            typeof(IReadOnlyDictionary<string, List<string?>>)
        );

        Schema(
            """
            {"type":"object",
            "additionalProperties":{"type":"integer","minimum":-2147483648,"maximum":2147483647},
            "x-csharp":"Dictionary<string, int>"}
            """,
            properties["counts"]
        );
        Schema(
            """
            {"type":"object","additionalProperties":{"type":"array","items":{"type":"string"}},
            "x-csharp":"IReadOnlyDictionary<string, List<string?>>"}
            """,
            properties["tags"]
        );
    }

    [Fact]
    public void AParameterDescriptionBecomesThePropertyDescription() =>
        Schema(
            """{"type":"integer","minimum":-2147483648,"maximum":2147483647,"x-csharp":"int","description":"How hard to hit"}""",
            Property(nameof(ToolBench.Powered), typeof(int))
        );

    [Fact]
    public void ARecordIsAnObjectOfItsOneConstructorsParameters() =>
        Schema(
            """
            {"type":"object","properties":{
            "Label":{"type":"string","x-csharp":"string"},
            "Cost":{"type":"integer","minimum":-2147483648,"maximum":2147483647,"x-csharp":"int"}},
            "additionalProperties":false,"required":["Label","Cost"],"x-csharp":"Choice"}
            """,
            Property(nameof(ToolBench.Assigned), typeof(Choice))
        );

    [Fact]
    public void AClassWithSeveralConstructorsRequiresNothing() =>
        Schema(
            """
            {"type":"object","properties":{
            "a":{"type":"integer","minimum":-2147483648,"maximum":2147483647,"x-csharp":"int"},
            "b":{"type":"integer","minimum":-2147483648,"maximum":2147483647,"x-csharp":"int"}},
            "additionalProperties":false,"x-csharp":"Pair"}
            """,
            Property(nameof(ToolBench.Chosen), typeof(Pair))
        );

    [Fact]
    public void AClassTakesItsSettableMembersAndLeavesOutTheReadOnlyOnes() =>
        Schema(
            """
            {"type":"object","properties":{
            "Volume":{"type":"integer","minimum":-2147483648,"maximum":2147483647,"x-csharp":"int"},
            "Name":{"type":"string","x-csharp":"string"}},
            "additionalProperties":false,"required":[],"x-csharp":"Settings"}
            """,
            Property(nameof(ToolBench.Configured), typeof(Settings))
        );

    [Fact]
    public void ANestedObjectIsOneLevelDeep() =>
        Schema(
            """
            {"type":"object","properties":{
            "Title":{"type":"string","x-csharp":"string"},
            "Best":{"type":"object","x-csharp":"Choice"},
            "Options":{"type":"array","items":{"type":"object","x-csharp":"Choice"},"x-csharp":"ImmutableArray<Choice>"}},
            "additionalProperties":false,"required":["Title","Best","Options"],"x-csharp":"Offer"}
            """,
            Property(nameof(ToolBench.Offered), typeof(Offer))
        );

    [Fact]
    public void ANodeIsItsPath()
    {
        JsonObject properties = Properties(nameof(ToolBench.Attached), typeof(Mentor), typeof(Tutor));

        Schema("""{"type":"object","properties":{"$node":{"type":"string"}},"required":["$node"],"x-csharp":"Mentor"}""", properties["mentor"]);
        Schema("""{"type":"object","properties":{"$node":{"type":"string"}},"required":["$node"],"x-csharp":"Tutor"}""", properties["tutor"]);
    }

    [Fact]
    public void AnInterfaceOrAbstractTypeIsAHandleOrANode()
    {
        JsonObject properties = Properties(nameof(ToolBench.Abstracted), typeof(IShield), typeof(Weapon));

        Schema("""{"type":"object","description":"pass {\"$handle\": id} or {\"$node\": path}","x-csharp":"IShield"}""", properties["shield"]);
        Schema("""{"type":"object","description":"pass {\"$handle\": id} or {\"$node\": path}","x-csharp":"Weapon"}""", properties["weapon"]);
    }

    [Fact]
    public void AnObjectParameterIsUnconstrained() => Schema("""{"x-csharp":"object"}""", Property(nameof(ToolBench.Anything), typeof(object)));

    [Fact]
    public void ACancellationTokenIsLeftOutOfTheSchema()
    {
        JsonObject arguments = Arguments(nameof(ToolBench.Waited), typeof(int), typeof(CancellationToken));
        string[] names = ["amount"];

        Assert.Equal(names, arguments["properties"]!.AsObject().Select(pair => pair.Key));
        Assert.Equal(names, arguments["required"]!.AsArray().Select(node => (string)node!));
    }

    [Fact]
    public void AnInParameterConvertsLikeAPlainValue() =>
        Schema(
            """{"type":"integer","minimum":-2147483648,"maximum":2147483647,"x-csharp":"in int"}""",
            Property(nameof(ToolBench.Inlined), typeof(int).MakeByRefType())
        );

    [Fact]
    public void AnOutOrRefParameterIsRefused()
    {
        Assert.Equal(
            "parameter 'count' is out int, which a game tool cannot pass",
            Refusal(nameof(ToolBench.Counted), typeof(int), typeof(int).MakeByRefType())
        );
        Assert.Equal("parameter 'value' is ref int, which a game tool cannot pass", Refusal(nameof(ToolBench.Grown), typeof(int).MakeByRefType()));
    }

    [Fact]
    public void AByRefLikeParameterOrReturnIsRefused()
    {
        Assert.Equal("parameter 'values' is Span<int>, which a game tool cannot pass", Refusal(nameof(ToolBench.Summed), typeof(Span<int>)));
        Assert.Equal("it returns Span<int>, which a game tool cannot return", Refusal(nameof(ToolBench.Returned)));
    }

    [Fact]
    public void AnUnreadableCollectionIsRefused()
    {
        Assert.Equal(
            "parameter 'items' is IReadOnlyCollection<int>, which a game tool cannot pass",
            Refusal(nameof(ToolBench.Collected), typeof(IReadOnlyCollection<int>))
        );
        Assert.Equal("parameter 'items' is ISet<int>, which a game tool cannot pass", Refusal(nameof(ToolBench.Setted), typeof(ISet<int>)));
        Assert.Equal(
            "parameter 'map' is Dictionary<int, string>, which a game tool cannot pass",
            Refusal(nameof(ToolBench.Keyed), typeof(Dictionary<int, string>))
        );
    }

    [Fact]
    public void ADelegateOrANativeIntegerIsRefused()
    {
        Assert.Equal("parameter 'handler' is Action<int>, which a game tool cannot pass", Refusal(nameof(ToolBench.Handled), typeof(Action<int>)));
        Assert.Equal("parameter 'source' is Func<int>, which a game tool cannot pass", Refusal(nameof(ToolBench.Sourced), typeof(Func<int>)));
        Assert.Equal("parameter 'size' is nint, which a game tool cannot pass", Refusal(nameof(ToolBench.Sized), typeof(nint), typeof(nuint)));
    }

    [Fact]
    public void APointerParameterIsRefused() => Assert.Equal("parameter 'address' is int*, which a game tool cannot pass", Refusal(Pointered()));

    [Fact]
    public void AGenericMethodDefinitionIsRefused() =>
        Assert.Equal("Echoed is generic, which a game tool cannot call", Refusal(Method(nameof(ToolBench.Echoed))));

    [Fact]
    public void AMethodOnAnOpenGenericTypeIsRefused() =>
        Assert.Equal("Add is on generic Pool<T>, which a game tool cannot call", Refusal(typeof(Pool<>).GetMethod(nameof(Pool<>.Add))!));

    [Fact]
    public void ANullableNativeIntegerIsRefused() =>
        Assert.Equal("parameter 'size' is nint?, which a game tool cannot pass", Refusal(nameof(ToolBench.Native), typeof(nint?)));

    [Fact]
    public void ANativeIntegerAloneIsRefused() =>
        Assert.Equal("parameter 'limit' is nuint, which a game tool cannot pass", Refusal(nameof(ToolBench.Limited), typeof(nuint)));

    [Fact]
    public void ACollectionNestedInACollectionIsRefused() =>
        Assert.Equal(
            "parameter 'maps' is List<Dictionary<int, string>>, which a game tool cannot pass",
            Refusal(nameof(ToolBench.ListedDeep), typeof(List<Dictionary<int, string>>))
        );

    [Fact]
    public void ANonStringKeyedReadOnlyDictionaryIsRefused() =>
        Assert.Equal(
            "parameter 'map' is IReadOnlyDictionary<int, string>, which a game tool cannot pass",
            Refusal(nameof(ToolBench.KeyedReadOnly), typeof(IReadOnlyDictionary<int, string>))
        );

    [Fact]
    public void ANullableElementTypeIsStillReadable() => Schema(Integers("int?[]"), Property(nameof(ToolBench.Nullables), typeof(int?[])));

    [Fact]
    public void ANullableStructIsAnObjectOfItsMembers() => Schema(PurseSchema("Purse?"), Property(nameof(ToolBench.Pursed), typeof(Purse?)));

    [Fact]
    public void ADescribedInterfaceParameterKeepsTheHandleHint() =>
        Schema(
            """{"type":"object","description":"The shield to ward with (pass {\"$handle\": id} or {\"$node\": path})","x-csharp":"IShield"}""",
            Property(nameof(ToolBench.Guarded), typeof(IShield))
        );

    [Fact]
    public void ANestedMemberKeepsItsNullability() =>
        Schema(
            """
            {"type":"object","properties":{
            "Title":{"type":"string","x-csharp":"string"},
            "Remark":{"type":"string","x-csharp":"string?"}},
            "additionalProperties":false,"required":["Title","Remark"],"x-csharp":"Note"}
            """,
            Property(nameof(ToolBench.Noted), typeof(Note))
        );

    [Fact]
    public void ADateTimeDefaultIsWrittenInIso8601() =>
        Schema("""{"type":"string","x-csharp":"DateTime","default":"2000-01-01T00:00:00"}""", Property(nameof(ToolBench.Timed), typeof(DateTime)));

    [Fact]
    public void ADefaultThatIsNullOnAValueTypeIsNotWritten()
    {
        JsonObject properties = Properties(nameof(ToolBench.Defaulted), typeof(Guid), typeof(Purse));

        Schema("""{"type":"string","x-csharp":"Guid"}""", properties["id"]);
        Schema(PurseSchema("Purse"), properties["purse"]);
    }

    [Fact]
    public void AnAvailableMethodCarriesAnArgumentsObjectAndNoReason()
    {
        ToolSignature signature = Describe(nameof(ToolBench.Needed), typeof(int), typeof(string));

        Assert.NotNull(signature.Arguments);
        Assert.Null(signature.Unavailable);
    }

    [Fact]
    public void AReturnTypeIsSpelledAsAMemberListSpellsIt()
    {
        Assert.Equal("Task<string>", Describe(nameof(ToolBench.Fetched)).Returns);
        Assert.Equal("void", Describe(nameof(ToolBench.Quiet)).Returns);
        Assert.Equal("string", Describe(nameof(ToolBench.Identified), typeof(Guid)).Returns);
        Assert.Equal("Span<int>", Describe(nameof(ToolBench.Returned)).Returns);
    }

    [Fact]
    public void AnUnavailableMethodStillCarriesItsReturns()
    {
        ToolSignature signature = Describe(nameof(ToolBench.Counted), typeof(int), typeof(int).MakeByRefType());

        Assert.Null(signature.Arguments);
        Assert.Equal("bool", signature.Returns);
        Assert.Equal("parameter 'count' is out int, which a game tool cannot pass", signature.Unavailable);
    }

    [Fact]
    public void AParameterlessMethodHasNoProperties() =>
        Schema("""{"type":"object","properties":{},"required":[],"additionalProperties":false}""", Arguments(nameof(ToolBench.Ping)));

    [Fact]
    public void EveryConvertibleSignatureIsAvailable()
    {
        string[] convertible =
        [
            nameof(ToolBench.Bounded),
            nameof(ToolBench.Colored),
            nameof(ToolBench.Listed),
            nameof(ToolBench.Mapped),
            nameof(ToolBench.Assigned),
            nameof(ToolBench.Offered),
            nameof(ToolBench.Chosen),
            nameof(ToolBench.Configured),
            nameof(ToolBench.Attached),
            nameof(ToolBench.Abstracted),
            nameof(ToolBench.Anything),
            nameof(ToolBench.Waited),
            nameof(ToolBench.Inlined),
            nameof(ToolBench.Unique),
            nameof(ToolBench.Supplied),
            nameof(ToolBench.Rest),
            nameof(ToolBench.Powered),
            nameof(ToolBench.Identified),
            nameof(ToolBench.Nullables),
            nameof(ToolBench.Pursed),
        ];

        Assert.All(convertible, name => Assert.Null(Describe(Method(name)).Unavailable));
    }

    /// <summary>A method with a pointer parameter, which no source here declares without <c>unsafe</c>.</summary>
    private static MethodInfo Pointered()
    {
        var assembly = AssemblyBuilder.DefineDynamicAssembly(new AssemblyName("Pointered"), AssemblyBuilderAccess.Run);
        TypeBuilder type = assembly.DefineDynamicModule("Pointered").DefineType("Pointered", TypeAttributes.Public);
        MethodBuilder method = type.DefineMethod(
            "Pointered",
            MethodAttributes.Public | MethodAttributes.Static,
            typeof(void),
            [typeof(int).MakePointerType()]
        );
        method.DefineParameter(1, ParameterAttributes.None, "address");
        return type.CreateType().GetMethod("Pointered")!;
    }

    private static void Bounds(JsonObject properties, string name, string spelling, decimal min, decimal max)
    {
        var schema = (JsonObject)properties[name]!;

        Assert.Equal("integer", (string?)schema["type"]);
        Assert.Equal(spelling, (string?)schema["x-csharp"]);
        Assert.Equal(min, (decimal?)schema["minimum"]);
        Assert.Equal(max, (decimal?)schema["maximum"]);
    }

    private static ToolSignature Describe(MethodInfo method) => ToolSchema.Describe(method, IsNode);

    private static ToolSignature Describe(string method, params Type[] parameters) => Describe(Method(method, parameters));

    private static JsonObject Arguments(string method, params Type[] parameters) => Describe(method, parameters).Arguments!;

    private static JsonObject Properties(string method, params Type[] parameters) => Arguments(method, parameters)["properties"]!.AsObject();

    private static JsonObject Property(string method, params Type[] parameters) => (JsonObject)Properties(method, parameters).First().Value!;

    private static string Refusal(string method, params Type[] parameters) => Refusal(Method(method, parameters));

    private static string Refusal(MethodInfo method) => Describe(method).Unavailable ?? $"{method.Name} is available";

    private static void Schema(string expected, JsonNode? actual) =>
        Assert.True(JsonNode.DeepEquals(JsonNode.Parse(expected), actual), $"expected {expected}, got {actual?.ToJsonString()}");

    private static MethodInfo Method(string name, params Type[] parameters) =>
        (parameters.Length == 0 ? typeof(ToolBench).GetMethod(name) : typeof(ToolBench).GetMethod(name, parameters))
        ?? throw new InvalidOperationException($"ToolBench has no {name}");
}
