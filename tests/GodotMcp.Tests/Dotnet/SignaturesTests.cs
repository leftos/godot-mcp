using System.Reflection;
using GodotMcp.Dotnet.Core;

namespace GodotMcp.Tests.Dotnet;

public sealed class SignaturesTests
{
    [Theory]
    [InlineData("Hit", "int Hit(int amount)")]
    [InlineData("Bind", "void Bind(Choice update)")]
    [InlineData("CountAsync", "Task<int> CountAsync()")]
    [InlineData("Echo", "T Echo<T>(T value)")]
    [InlineData("Scale", "int Scale(int amount, int factor = 2)")]
    [InlineData("Secret", "private int Secret()")]
    public void MethodsAreSpelledAsCSharp(string name, string signature) =>
        Assert.Equal(
            signature,
            Signatures.Format(typeof(Duel).GetMethod(name, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)!)
        );

    [Theory]
    [InlineData("Label", "static string Label { get; }")]
    [InlineData("Values", "List<int> Values { get; set; }")]
    [InlineData("PendingDecision", "Color? PendingDecision { get; }")]
    [InlineData("Note", "string? Note { get; set; }")]
    [InlineData("Name", "string Name { get; init; }")]
    [InlineData("Retries", "int Retries { get; private set; }")]
    [InlineData("Tags", "Dictionary<string, List<string?>> Tags { get; }")]
    public void PropertiesShowTheirAccessors(string name, string signature) =>
        Assert.Equal(signature, Signatures.Format(typeof(Duel).GetProperty(name)!));

    [Fact]
    public void FieldsShowTheirAccessibility() =>
        Assert.Equal(
            "private readonly int _seed",
            Signatures.Format(typeof(Duel).GetField("_seed", BindingFlags.NonPublic | BindingFlags.Instance)!)
        );

    [Fact]
    public void ConstructorsAreSpelledDotCtor() =>
        Assert.Equal(".ctor(int a, string b)", Signatures.Format(typeof(Duel).GetConstructor([typeof(int), typeof(string)])!));
}
