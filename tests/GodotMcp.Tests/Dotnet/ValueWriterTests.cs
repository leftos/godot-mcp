using System.Collections.Immutable;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using GodotMcp.Dotnet.Core;

namespace GodotMcp.Tests.Dotnet;

public sealed class ValueWriterTests
{
    // The default encoder escapes <, > and +, which the markers and offsets carry; relaxed escaping keeps the expectations readable.
    private static readonly JsonSerializerOptions Relaxed = new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    [Fact]
    public void NullWritesNull() => Assert.Null(ValueWriter.Write(null, new NoFormatter()));

    [Fact]
    public void PrimitivesWriteAsJson()
    {
        Assert.Equal("3", Write(3));
        Assert.Equal("true", Write(true));
        Assert.Equal("\"a\"", Write("a"));
        Assert.Equal("2.5", Write(2.5));
        Assert.Equal("\"c\"", Write('c'));
        Assert.Equal("1.5", Write(1.5m));
        Assert.Equal("18446744073709551615", Write(ulong.MaxValue));
    }

    [Fact]
    public void ANonFiniteDoubleWritesAsText() => Assert.Equal("\"NaN\"", Write(double.NaN));

    [Fact]
    public void EnumsWriteByName()
    {
        Assert.Equal("\"Blue\"", Write(Color.Blue));
        Assert.Equal("\"Read, Write\"", Write(Access.Read | Access.Write));
    }

    [Fact]
    public void DatesWriteAsIso8601()
    {
        Assert.Equal("\"2026-09-26T10:30:00.0000000Z\"", Write(new DateTime(2026, 9, 26, 10, 30, 0, DateTimeKind.Utc)));
        Assert.Equal("\"2026-09-26T10:30:00.0000000+02:00\"", Write(new DateTimeOffset(2026, 9, 26, 10, 30, 0, TimeSpan.FromHours(2))));
    }

    [Fact]
    public void CollectionsWriteAsArrays()
    {
        string[] words = ["a"];

        Assert.Equal("[1,2]", Write(new List<int> { 1, 2 }));
        Assert.Equal("[1,2]", Write(ImmutableArray.Create(1, 2)));
        Assert.Equal("[1]", Write(new HashSet<int> { 1 }));
        Assert.Equal("[\"a\"]", Write(words));
    }

    [Fact]
    public void ALazySequenceWritesItsTypeNameUnenumerated() => Assert.StartsWith("\"<", Write(Enumerable.Range(0, 3).Select(i => i * 2)));

    [Fact]
    public void DictionariesWithStringLikeKeysWriteAsObjects()
    {
        Assert.Equal("{\"a\":1}", Write(new Dictionary<string, int> { ["a"] = 1 }));
        Assert.Equal("{\"Red\":1}", Write(new Dictionary<Color, int> { [Color.Red] = 1 }));
        Assert.Equal("{\"2\":\"x\"}", Write(new Dictionary<int, string> { [2] = "x" }));
    }

    [Fact]
    public void DictionariesWithOtherKeysWriteAsKeyValuePairs() =>
        Assert.Equal("[{\"key\":{\"Label\":\"A\",\"Cost\":1},\"value\":5}]", Write(new Dictionary<Choice, int> { [new Choice("A", 1)] = 5 }));

    [Fact]
    public void ARecordWritesItsPropertiesInDeclarationOrder() =>
        Assert.Equal(
            "{\"Title\":\"Deal\",\"Best\":{\"Label\":\"A\",\"Cost\":1},\"Options\":[{\"Label\":\"B\",\"Cost\":2}]}",
            Write(new Offer("Deal", new Choice("A", 1), [new Choice("B", 2)]))
        );

    [Fact]
    public void PublicFieldsAreWrittenAfterProperties() => Assert.Equal("{\"Twice\":6,\"Count\":3}", Write(new Tally { Count = 3 }));

    [Fact]
    public void AThrowingGetterWritesTheExceptionAndTheWalkGoesOn() =>
        Assert.Equal("{\"Before\":1,\"Broken\":\"<threw InvalidOperationException: no>\",\"After\":2}", Write(new Fragile()));

    [Fact]
    public void AnObjectAlreadyOnThePathWritesACycle()
    {
        Link loop = new(1);
        loop.Next = loop;

        Assert.Equal("{\"Id\":1,\"Next\":\"<cycle: Link>\"}", Write(loop));
    }

    [Fact]
    public void AnObjectSeenTwiceOffThePathIsWrittenTwice()
    {
        Choice shared = new("A", 1);

        Assert.Equal("[{\"Label\":\"A\",\"Cost\":1},{\"Label\":\"A\",\"Cost\":1}]", Write(new List<Choice> { shared, shared }));
    }

    [Fact]
    public void AnObjectPastTheDepthLimitWritesADepthMarker()
    {
        Link root = new(1) { Next = new Link(2) { Next = new Link(3) } };

        Assert.Equal(
            "{\"Id\":1,\"Next\":{\"Id\":2,\"Next\":\"<depth limit: Link>\"}}",
            ValueWriter.Write(root, new NoFormatter(), 2)!.ToJsonString(Relaxed)
        );
    }

    [Fact]
    public void ACollectionPastTheDepthLimitWritesADepthMarker() =>
        Assert.Equal(
            "[\"<depth limit: List<int>>\"]",
            ValueWriter.Write(new List<List<int>> { new() { 1 } }, new NoFormatter(), 1)!.ToJsonString(Relaxed)
        );

    [Fact]
    public void DelegatesTypesAndTasksWriteTheirTypeName()
    {
        Action<int> action = _ => { };

        Assert.Equal("\"<Action<int>>\"", Write(action));
        Assert.Equal("\"<Type>\"", Write(typeof(int)));
        Assert.Equal("\"<Task<int>>\"", Write(Task.FromResult(3)));
    }

    [Fact]
    public void TheFormatterRunsFirstOnEveryValue()
    {
        OptionFormatter formatter = new();

        Assert.Equal("\"opt\"", ValueWriter.Write(new Choice("A", 1), formatter)!.ToJsonString(Relaxed));
        Assert.Equal("[\"opt\",3]", ValueWriter.Write(new List<object> { new Choice("A", 1), 3 }, formatter)!.ToJsonString(Relaxed));
    }

    private static string Write(object? value) => ValueWriter.Write(value, new NoFormatter())?.ToJsonString(Relaxed) ?? "null";
}
