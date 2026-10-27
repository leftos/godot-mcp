using System.Globalization;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using GodotMcp.Dotnet.Core;

namespace GodotMcp.Tests.Dotnet;

/// <summary>
/// Where <see cref="StateMethods"/> finds a type's <c>_McpState</c> (any accessibility, inherited or overridden, up to the stop)
/// and what it is not (generic, with parameters, static), and the errors it spells for a missing one and a task.
/// </summary>
public sealed class StateMethodsTests
{
    private static readonly StateMethods Everywhere = new(_ => false);

    [Theory]
    [InlineData(typeof(PrivateState), typeof(PrivateState))]
    [InlineData(typeof(InternalState), typeof(InternalState))]
    [InlineData(typeof(InheritedState), typeof(BaseState))]
    [InlineData(typeof(OverriddenState), typeof(OverriddenState))]
    [InlineData(typeof(PastTheStop), typeof(StoppedBase))]
    [InlineData(typeof(TaskState), typeof(TaskState))]
    public void FindsTheStateMethodWhereItIsDeclared(Type type, Type declaring)
    {
        MethodInfo? method = Everywhere.Find(type);

        Assert.NotNull(method);
        Assert.Equal((StateMethods.MethodName, declaring, 0), (method.Name, method.DeclaringType, method.GetParameters().Length));
    }

    [Theory]
    [InlineData(typeof(GenericState))]
    [InlineData(typeof(ParameterState))]
    [InlineData(typeof(StaticState))]
    [InlineData(typeof(Settings))]
    public void FindsNoStateMethodOnATypeWithoutAnInstanceParameterlessOne(Type type) => Assert.Null(Everywhere.Find(type));

    [Fact]
    public void StopsAtTheFirstTypeStopAtNames()
    {
        StateMethods stopped = new(type => type == typeof(StoppedBase));

        Assert.Null(stopped.Find(typeof(PastTheStop)));
    }

    [Fact]
    public void AFoundMethodIsTheSameOnTheNextFind()
    {
        StateMethods methods = new(_ => false);

        Assert.Same(methods.Find(typeof(PrivateState)), methods.Find(typeof(PrivateState)));
    }

    [Fact]
    public void AnInheritedMethodCallsTheOverride()
    {
        MethodInfo method = Everywhere.Find(typeof(InheritedState))!;

        Assert.Equal("overridden", method.Invoke(new OverriddenState(), null));
        Assert.Equal("base", method.Invoke(new InheritedState(), null));
    }

    [Fact]
    public void AMissingMethodIsNamedWithTheFullTypeName()
    {
        Assert.Equal(
            "GodotMcp.Tests.Dotnet.Settings has no _McpState() (an instance method with no parameters, any accessibility)",
            StateMethods.Missing(typeof(Settings))
        );
    }

    [Theory]
    [InlineData(typeof(Task<int>), "Task<int>")]
    [InlineData(typeof(Task), "Task")]
    [InlineData(typeof(ValueTask), "ValueTask")]
    [InlineData(typeof(ValueTask<string>), "ValueTask<string>")]
    public void ATaskReturnIsRefusedNamingIt(Type returned, string spelled)
    {
        Assert.Equal(
            $"GodotMcp.Tests.Dotnet.TaskState._McpState returns a {spelled}; a state method must return its value, not a task",
            StateMethods.TaskRefusal(typeof(TaskState), returned)
        );
    }

    [Theory]
    [InlineData(typeof(int))]
    [InlineData(typeof(object))]
    [InlineData(typeof(Offer))]
    public void AValueReturnIsNoTask(Type returned) => Assert.Null(StateMethods.TaskRefusal(typeof(TaskState), returned));

    [Fact]
    public void AReadWritesTheValueAsTheEntrysState() => Assert.Equal("""{"state":3}""", Read(new PrivateState()).ToJsonString());

    [Fact]
    public void AValueWhoseEnumerationThrowsWritesTheThrow()
    {
        JsonArray state = Read(new BrokenEnumerationState())["state"]!.AsArray();

        Assert.Equal(["<threw InvalidOperationException: enumeration broke>"], state.Select(item => item!.GetValue<string>()));
    }

    [Fact]
    public void WhatTheCallThrowsIsTheEntrysError()
    {
        JsonObject entry = Read(new SpanState());

        Assert.StartsWith("NotSupportedException: ", entry["error"]!.GetValue<string>(), StringComparison.Ordinal);
        Assert.False(entry.ContainsKey("state"), entry.ToJsonString());
    }

    [Fact]
    public void ARunningTaskReturnedAsObjectIsRefusedNamingItsPublicType()
    {
        JsonObject entry = Read(new RunningTaskState());

        Assert.Equal(
            "GodotMcp.Tests.Dotnet.RunningTaskState._McpState returns a Task<int>; a state method must return its value, not a task",
            entry["error"]?.GetValue<string>()
        );
        Assert.False(entry.ContainsKey("state"), entry.ToJsonString());
    }

    [Fact]
    public void AStatePastTheValueBudgetEndsAtTheSizeLimitMarkerQuickly()
    {
        var clock = System.Diagnostics.Stopwatch.StartNew();
        JsonArray state = Read(new HugeState())["state"]!.AsArray();
        clock.Stop();

        // The array itself is one of the values, so 4999 of its elements are written before the one marker that ends it.
        Assert.Equal(StateMethods.MaxValues, state.Count);
        Assert.Equal(StateMethods.MaxValues - 2, state[StateMethods.MaxValues - 2]!.GetValue<int>());
        Assert.Equal(ValueWriter.SizeLimit, state[^1]!.GetValue<string>());
        Assert.True(clock.Elapsed < TimeSpan.FromSeconds(5), $"the write took {clock.Elapsed}");
    }

    [Fact]
    public void AStateDictionaryPastTheValueBudgetEndsItsNextKeyAtTheSizeLimitMarker()
    {
        JsonObject state = Read(new HugeDictionaryState())["state"]!.AsObject();

        // The dictionary itself is one of the values, so 4999 of its entries are written before the marker's key.
        Assert.Equal(StateMethods.MaxValues, state.Count);
        Assert.Equal(ValueWriter.SizeLimit, state[$"k{StateMethods.MaxValues - 1:D4}"]!.GetValue<string>());
        Assert.False(state.ContainsKey($"k{StateMethods.MaxValues:D4}"), $"{state.Count} keys written");
    }

    [Fact]
    public void ANestedCollectionPastTheBudgetEndsItsEnclosingListAtOneMarker()
    {
        JsonArray outer = Read(new NestedHugeState())["state"]!["lists"]!.AsArray();

        // The first list is written whole; the second is cut, ending in its own marker; and the list holding both ends with
        // its one marker, after which the lists it still held are not visited.
        Assert.Equal(4000, outer[0]!.AsArray().Count);
        Assert.Equal(ValueWriter.SizeLimit, outer[1]!.AsArray()[^1]!.GetValue<string>());
        Assert.Equal(ValueWriter.SizeLimit, outer[^1]!.GetValue<string>());
        Assert.All(outer.Take(outer.Count - 1), item => Assert.IsType<JsonArray>(item));
        Assert.True(outer.Count < 10, $"{outer.Count} of the 10 lists are written");
    }

    [Fact]
    public void APairDictionaryPastTheValueBudgetEndsWithOneMarkerPair()
    {
        JsonArray pairs = Read(new HugePairDictionaryState())["state"]!.AsArray();

        // The dictionary itself is one value and each pair two, so the 2500th pair's key takes the last one: that pair carries
        // the marker and no pair follows it.
        Assert.Equal(1, pairs.Count(pair => pair!.AsObject()["value"]!.GetValueKind() == JsonValueKind.String));
        Assert.Equal(ValueWriter.SizeLimit, pairs[^1]!.AsObject()["value"]!.GetValue<string>());
        Assert.Equal(JsonValueKind.Number, pairs[^2]!.AsObject()["value"]!.GetValueKind());
        Assert.True(pairs.Count < 3000, $"{pairs.Count} pairs written");
    }

    [Fact]
    public void AStateOfExactlyTheBudgetCarriesNoMarker()
    {
        JsonArray state = Read(new ExactBudgetState())["state"]!.AsArray();

        // The list itself is one value, so 4999 ints fill the budget exactly: nothing was cut, so nothing is marked.
        Assert.Equal(StateMethods.MaxValues - 1, state.Count);
        Assert.All(state, item => Assert.Equal(JsonValueKind.Number, item!.GetValueKind()));
        Assert.Equal(StateMethods.MaxValues - 2, state[^1]!.GetValue<int>());
    }

    [Fact]
    public void AWriteOutsideAStateReadIsNotBounded()
    {
        JsonArray written = ValueWriter.Write(Enumerable.Range(0, 1_000_000).ToArray(), new NoFormatter(), 4)!.AsArray();

        Assert.All(written, item => Assert.Equal(JsonValueKind.Number, item!.GetValueKind()));
    }

    [Fact]
    public void AReadEntryWhoseMethodLookupThrowsIsThatEntrysError()
    {
        StateMethods unloadable = new(_ => throw new TypeLoadException("a base type is missing"));
        JsonObject entry = [];

        unloadable.ReadEntry(entry, new PrivateState(), new NoFormatter(), 4);

        Assert.Equal("""{"error":"TypeLoadException: a base type is missing"}""", entry.ToJsonString());
    }

    [Fact]
    public void AReadEntryOfATypeWithoutAStateMethodIsMissing()
    {
        JsonObject entry = [];

        Everywhere.ReadEntry(entry, new Settings(), new NoFormatter(), 4);

        Assert.True(entry["missing"]!.GetValue<bool>());
        Assert.Equal(StateMethods.Missing(typeof(Settings)), entry["error"]!.GetValue<string>());
    }

    [Fact]
    public void AReadEntryWithAStateMethodWritesItsState()
    {
        JsonObject entry = [];

        Everywhere.ReadEntry(entry, new PrivateState(), new NoFormatter(), 4);

        Assert.Equal("""{"state":3}""", entry.ToJsonString());
    }

    [Fact]
    public void AWriteAfterABoundedWriteThatThrewIsNotBounded()
    {
        Assert.Throws<InvalidOperationException>(() => ValueWriter.WriteBounded(new object[] { 1, "boom" }, new ThrowingFormatter(), 4, 3));

        JsonArray written = ValueWriter.Write(Enumerable.Range(0, 10).ToArray(), new NoFormatter(), 4)!.AsArray();

        Assert.All(written, item => Assert.Equal(JsonValueKind.Number, item!.GetValueKind()));
    }

    private static JsonObject Read(object target)
    {
        JsonObject entry = [];
        StateMethods.Read(entry, target, Everywhere.Find(target.GetType())!, new NoFormatter(), 4);
        return entry;
    }

    /// <summary>A formatter that throws on the string "boom" and formats nothing else.</summary>
    private sealed class ThrowingFormatter : IValueFormatter
    {
        public bool TryFormat(object value, out JsonNode? json)
        {
            json = null;
            return value is "boom" ? throw new InvalidOperationException("the formatter broke") : false;
        }
    }

    /// <summary>A formatter that formats nothing, so the writer's own rules write every value.</summary>
    private sealed class NoFormatter : IValueFormatter
    {
        public bool TryFormat(object value, out JsonNode? json)
        {
            json = null;
            return false;
        }
    }

    /// <summary>A state of 6000 entries, past the values one node's state writes; its sorted keys fix the order it walks.</summary>
    private sealed class HugeDictionaryState
    {
        private readonly SortedDictionary<string, int> _entries = new(
            Enumerable.Range(0, 6000).ToDictionary(index => $"k{index:D4}", index => index)
        );

        [System.Diagnostics.CodeAnalysis.SuppressMessage(
            "CodeQuality",
            "IDE0051:Remove unused private members",
            Justification = "The state method is found and called by reflection."
        )]
        private SortedDictionary<string, int> _McpState() => _entries;
    }

    /// <summary>A state of ten lists of 4000 numbers, past the values one node's state writes.</summary>
    private sealed class NestedHugeState
    {
        private readonly List<List<int>> _lists = [.. Enumerable.Range(0, 10).Select(_ => Enumerable.Range(0, 4000).ToList())];

        [System.Diagnostics.CodeAnalysis.SuppressMessage(
            "CodeQuality",
            "IDE0051:Remove unused private members",
            Justification = "The state method is found and called by reflection."
        )]
        private object _McpState() => new { lists = _lists };
    }

    /// <summary>A key the writer cannot write as an object's key text, so the dictionary holding it lists key/value pairs.</summary>
    private readonly record struct PairKey(int Value) : IComparable<PairKey>
    {
        public int CompareTo(PairKey other) => Value.CompareTo(other.Value);

        public override string ToString() => Value.ToString(CultureInfo.InvariantCulture);
    }

    /// <summary>A state of 3000 entries under pair keys, past the values one node's state writes; its sorted keys fix the order it walks.</summary>
    private sealed class HugePairDictionaryState
    {
        private readonly SortedDictionary<PairKey, int> _entries = new(
            Enumerable.Range(0, 3000).ToDictionary(index => new PairKey(index), index => index)
        );

        [System.Diagnostics.CodeAnalysis.SuppressMessage(
            "CodeQuality",
            "IDE0051:Remove unused private members",
            Justification = "The state method is found and called by reflection."
        )]
        private SortedDictionary<PairKey, int> _McpState() => _entries;
    }

    /// <summary>A state of exactly the values one node's state writes: the list itself and 4999 ints.</summary>
    private sealed class ExactBudgetState
    {
        private readonly List<int> _items = [.. Enumerable.Range(0, 4999)];

        [System.Diagnostics.CodeAnalysis.SuppressMessage(
            "CodeQuality",
            "IDE0051:Remove unused private members",
            Justification = "The state method is found and called by reflection."
        )]
        private List<int> _McpState() => _items;
    }
}
