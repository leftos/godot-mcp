using System.Collections.Immutable;
using System.Text.Json.Nodes;
using GodotMcp.Dotnet.Core;

namespace GodotMcp.Tests.Dotnet;

public sealed class ValueReaderTests
{
    private readonly FakeResolver _resolver = new();

    [Fact]
    public void IntegersReadAtEveryWidth()
    {
        Assert.Equal(42, Read<int>("42"));
        Assert.Equal((sbyte)-5, Read<sbyte>("-5"));
        Assert.Equal((ushort)65535, Read<ushort>("65535"));
        Assert.Equal(ulong.MaxValue, Read<ulong>("18446744073709551615"));
        Assert.Equal(3, Read<int>("3.0"));
    }

    [Fact]
    public void AnIntegerOutOfRangeIsRefused() => Assert.Equal("expected a byte, got 300 (out of range)", Refusal<byte>("300"));

    [Fact]
    public void AStringForAnIntIsRefused() => Assert.Equal("expected an int, got \"abc\"", Refusal<int>("\"abc\""));

    [Fact]
    public void AFractionForAnIntIsRefused() => Assert.Equal("expected an int, got 3.5", Refusal<int>("3.5"));

    [Fact]
    public void FloatingPointNumbersRead()
    {
        Assert.Equal(3.5f, Read<float>("3.5"));
        Assert.Equal(0.1, Read<double>("0.1"));
        Assert.Equal(1.25m, Read<decimal>("1.25"));
        Assert.Equal(3f, Read<float>("3"));
    }

    [Fact]
    public void BoolsStringsAndCharsRead()
    {
        Assert.True(Read<bool>("true"));
        Assert.Equal("hi", Read<string>("\"hi\""));
        Assert.Equal('x', Read<char>("\"x\""));
    }

    [Fact]
    public void ANumberForABoolIsRefused() => Assert.Equal("expected a bool, got 1", Refusal<bool>("1"));

    [Fact]
    public void ACharNeedsExactlyOneCharacter() => Assert.Equal("expected a char, got \"ab\"", Refusal<char>("\"ab\""));

    [Fact]
    public void GuidsDatesAndTimeSpansRead()
    {
        Assert.Equal(Guid.Parse("d3b07384-d9a7-4c3b-9a4f-0c1d2e3f4a5b"), Read<Guid>("\"d3b07384-d9a7-4c3b-9a4f-0c1d2e3f4a5b\""));
        Assert.Equal(new DateTime(2026, 9, 26, 10, 30, 0, DateTimeKind.Utc), Read<DateTime>("\"2026-09-26T10:30:00Z\""));
        Assert.Equal(new DateTimeOffset(2026, 9, 26, 10, 30, 0, TimeSpan.FromHours(2)), Read<DateTimeOffset>("\"2026-09-26T10:30:00+02:00\""));
        Assert.Equal(new TimeSpan(1, 2, 3, 4), Read<TimeSpan>("\"1.02:03:04\""));
    }

    [Fact]
    public void ADateThatIsNotIso8601IsRefused() => Assert.Equal("expected a DateTime, got \"09/26/2026\"", Refusal<DateTime>("\"09/26/2026\""));

    [Fact]
    public void ATimeSpanThatIsNotConstantFormatIsRefused() => Assert.Equal("expected a TimeSpan, got \"1h\"", Refusal<TimeSpan>("\"1h\""));

    [Fact]
    public void EnumsReadByNameIgnoringCaseOrByNumber()
    {
        Assert.Equal(Color.Green, Read<Color>("\"green\""));
        Assert.Equal(Color.Blue, Read<Color>("2"));
    }

    [Fact]
    public void AnUnknownEnumNameIsRefused() =>
        Assert.Equal("expected one of Red, Green, Blue (or its number), got \"Purple\"", Refusal<Color>("\"Purple\""));

    [Fact]
    public void AnUndefinedEnumNumberIsRefused() => Assert.Equal("expected one of Red, Green, Blue (or its number), got 7", Refusal<Color>("7"));

    [Fact]
    public void AFlagsEnumReadsJoinedNamesAndAnyNumber()
    {
        Assert.Equal(Access.Read | Access.Write, Read<Access>("\"Read, write\""));
        Assert.Equal(Access.Read | Access.Write, Read<Access>("3"));
    }

    [Fact]
    public void AnEnumWithoutFlagsRefusesJoinedNames() =>
        Assert.Equal("expected one of Red, Green, Blue (or its number), got \"Red, Green\"", Refusal<Color>("\"Red, Green\""));

    [Fact]
    public void ANullableReadsNullOrItsValue()
    {
        Assert.Null(Read<int?>("null"));
        Assert.Equal(5, Read<int?>("5"));
    }

    [Fact]
    public void NullIsRefusedForANonNullableValueType() => Assert.Equal("expected an int, got null", Refusal<int>("null"));

    [Fact]
    public void NullIsReadForAReferenceType() => Assert.Null(Read<string>("null"));

    [Fact]
    public void ArraysAndListShapesRead()
    {
        int[] expected = [1, 2];
        Assert.Equal(expected, Read<int[]>("[1, 2]"));
        Assert.Equal(expected, Read<List<int>>("[1, 2]"));
        Assert.IsType<List<int>>(ReadAs("[1]", typeof(IList<int>)));
        Assert.IsType<List<int>>(ReadAs("[1]", typeof(IReadOnlyList<int>)));
        Assert.IsType<List<int>>(ReadAs("[1]", typeof(IEnumerable<int>)));
        Assert.IsType<List<int>>(ReadAs("[1]", typeof(ICollection<int>)));
    }

    [Fact]
    public void ImmutableCollectionsAndSetsRead()
    {
        int[] expected = [1, 2];
        Assert.Equal(expected, Read<ImmutableArray<int>>("[1, 2]"));
        Assert.Equal(expected, Read<ImmutableList<int>>("[1, 2]"));
        Assert.Equal([.. expected], Read<HashSet<int>>("[1, 2, 2]"));
    }

    [Fact]
    public void StringKeyedDictionariesReadFromAnObject()
    {
        Dictionary<string, int> expected = new() { ["a"] = 1, ["b"] = 2 };
        Assert.Equal(expected, Read<Dictionary<string, int>>("{\"a\": 1, \"b\": 2}"));
        Assert.Equal(expected, Assert.IsType<Dictionary<string, int>>(ReadAs("{\"a\": 1, \"b\": 2}", typeof(IReadOnlyDictionary<string, int>))));
    }

    [Fact]
    public void AnElementThatDoesNotFitNamesItsIndex() => Assert.Equal("expected an int, got \"x\" at [1]", Refusal<List<int>>("[1, \"x\"]"));

    [Fact]
    public void ANonArrayForAListIsRefused() => Assert.Equal("expected a List<int>, got 3", Refusal<List<int>>("3"));

    [Fact]
    public void ACollectionOfAnotherShapeIsRefused() =>
        Assert.Equal(
            "cannot convert to Dictionary<int, string>: only arrays, lists, sets and string-keyed dictionaries convert",
            Refusal<Dictionary<int, string>>("{}")
        );

    [Fact]
    public void ARecordWithANestedRecordAndAnImmutableArrayReads()
    {
        Offer offer = Read<Offer>(
            "{\"title\": \"Deal\", \"best\": {\"label\": \"A\", \"cost\": 1}, "
                + "\"options\": [{\"label\": \"A\", \"cost\": 1}, {\"label\": \"B\", \"cost\": 2}]}"
        )!;

        Assert.Equal("Deal", offer.Title);
        Assert.Equal(new Choice("A", 1), offer.Best);
        Choice[] expected = [new("A", 1), new("B", 2)];
        Assert.Equal(expected, offer.Options);
    }

    [Fact]
    public void AClassTakesTheOtherKeysThroughSettersAndInit()
    {
        Settings settings = Read<Settings>("{\"volume\": 3, \"Name\": \"x\"}")!;

        Assert.Equal(3, settings.Volume);
        Assert.Equal("x", settings.Name);
    }

    [Fact]
    public void TheConstructorMatchingTheMostKeysIsCalled()
    {
        Assert.Equal("a,b", Read<Pair>("{\"A\": 1, \"b\": 2}")!.Made);
        Assert.Equal("a", Read<Pair>("{\"a\": 1}")!.Made);
        Assert.Equal("none", Read<Pair>("{}")!.Made);
    }

    [Fact]
    public void AnUnknownKeyIsRefused() => Assert.Equal("Settings has no member 'loud'", Refusal<Settings>("{\"loud\": 1}"));

    [Fact]
    public void AMissingConstructorParameterIsRefused() =>
        Assert.Equal("Choice has no constructor whose required parameters are all among the keys label", Refusal<Choice>("{\"label\": \"A\"}"));

    [Fact]
    public void AReadOnlyMemberIsRefused() => Assert.Equal("Settings.Fixed is read-only", Refusal<Settings>("{\"fixed\": 2}"));

    [Fact]
    public void ANestedMismatchNamesItsPath() =>
        Assert.Equal(
            "expected an int, got \"x\" at best.cost",
            Refusal<Offer>("{\"title\": \"Deal\", \"best\": {\"label\": \"A\", \"cost\": \"x\"}, \"options\": []}")
        );

    [Fact]
    public void AHandleResolvesToAnAssignableValue() => Assert.Same(_resolver.Shield, Read<IShield>("{\"$handle\": \"h1.1\"}"));

    [Fact]
    public void ANodeResolvesThroughTheResolver() => Assert.Same(_resolver.Shield, Read<IShield>("{\"$node\": \"/root/Shield\"}"));

    [Fact]
    public void AHandleToAValueOfAnotherTypeIsRefused() =>
        Assert.Equal("handle h1.2 is a Plain, not assignable to IShield", Refusal<IShield>("{\"$handle\": \"h1.2\"}"));

    [Fact]
    public void AnInterfaceWithoutAHandleIsRefused() =>
        Assert.Equal("IShield is an interface: pass {\"$handle\": id} or {\"$node\": path}", Refusal<IShield>("{}"));

    [Fact]
    public void AnAbstractClassWithoutAHandleIsRefused() =>
        Assert.Equal("Weapon is abstract: pass {\"$handle\": id} or {\"$node\": path}", Refusal<Weapon>("{\"damage\": 1}"));

    [Fact]
    public void AnObjectTargetReadsNaturalValues()
    {
        List<object?> list = Assert.IsType<List<object?>>(Read<object>("[1, 2.5, \"a\", true, {\"k\": null}]"));

        Assert.Equal(1L, list[0]);
        Assert.Equal(2.5, list[1]);
        Assert.Equal("a", list[2]);
        Assert.True((bool)list[3]!);
        Assert.Null(Assert.IsType<Dictionary<string, object?>>(list[4])["k"]);
    }

    private T? Read<T>(string json) => (T?)ReadAs(json, typeof(T));

    private object? ReadAs(string json, Type type) => ValueReader.Read(JsonNode.Parse(json), type, _resolver);

    private string Refusal<T>(string json) => Assert.Throws<ValueConversionException>(() => ReadAs(json, typeof(T))).Message;
}
