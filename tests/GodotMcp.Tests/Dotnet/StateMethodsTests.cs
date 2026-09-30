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
    public void AValueWhoseEnumerationThrowsIsWrittenAsItsTypeMarker() =>
        Assert.Equal(
            """{"state":"<BrokenCollection>"}""",
            Read(new BrokenEnumerationState())["state"] is { } state ? $"{{\"state\":\"{state.GetValue<string>()}\"}}" : "no state"
        );

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
    public void AStatePastTheValueBudgetWritesTheSizeLimitMarkerForTheRestQuickly()
    {
        var clock = System.Diagnostics.Stopwatch.StartNew();
        JsonArray state = Read(new HugeState())["state"]!.AsArray();
        clock.Stop();

        // The array itself is one of the values, so 4999 of its elements are written before the marker.
        Assert.Equal(1_000_000, state.Count);
        Assert.Equal(StateMethods.MaxValues - 1, state.TakeWhile(item => item!.GetValueKind() == JsonValueKind.Number).Count());
        Assert.Equal(StateMethods.MaxValues - 2, state[StateMethods.MaxValues - 2]!.GetValue<int>());
        Assert.All(state.Skip(StateMethods.MaxValues - 1), item => Assert.Equal(ValueWriter.SizeLimit, item!.GetValue<string>()));
        Assert.True(clock.Elapsed < TimeSpan.FromSeconds(5), $"the write took {clock.Elapsed}");
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
}
