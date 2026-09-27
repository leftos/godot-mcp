using System.Reflection;
using GodotMcp.Dotnet.Core;

namespace GodotMcp.Tests.Dotnet;

/// <summary>Which methods a call by name chooses among, and the refusal when a name is not a method it can call.</summary>
public sealed class CallCandidatesTests
{
    [Fact]
    public void ATypeTargetRefusesAnInstanceMethod() =>
        Assert.Equal(
            "'Hit' is an instance method of Fighter; target a {node} or {handle}",
            Refusal(typeof(Fighter), "Hit", staticsOnly: true, stopAt: _ => false)
        );

    [Fact]
    public void APropertyNamePointsToCsGet() =>
        Assert.Equal(
            "'Health' is a property of Fighter; cs_get reads it",
            Refusal(typeof(Fighter), "Health", staticsOnly: false, stopAt: _ => false)
        );

    [Fact]
    public void AGodotBaseMethodPointsToCallMethod() =>
        Assert.Equal(
            "'Rest' is a Godot method; call_method calls it",
            Refusal(typeof(Tutor), "Rest", staticsOnly: false, stopAt: type => type == typeof(Mentor))
        );

    [Fact]
    public void AnOverrideIsNotAmbiguousWithItsBase()
    {
        IReadOnlyList<MethodBase> candidates = CallCandidates.Find(typeof(Tutor), "Teach", staticsOnly: false, stopAt: _ => false);

        Assert.Equal(typeof(Tutor), Assert.Single(candidates).DeclaringType);
    }

    [Fact]
    public void AnOverrideWithANewAccessOrCovariantReturnIsOneCandidate()
    {
        IReadOnlyList<MethodBase> made = CallCandidates.Find(typeof(Smithy), "Make", staticsOnly: false, stopAt: _ => false);
        IReadOnlyList<MethodBase> shouted = CallCandidates.Find(typeof(Smithy), "Shout", staticsOnly: false, stopAt: _ => false);

        Assert.Equal(typeof(Smithy), Assert.Single(made).DeclaringType);
        Assert.Equal(typeof(Smithy), Assert.Single(shouted).DeclaringType);
    }

    private static string Refusal(Type type, string name, bool staticsOnly, Func<Type, bool> stopAt) =>
        Assert.Throws<OverloadException>(() => CallCandidates.Find(type, name, staticsOnly, stopAt)).Message;

    /// <summary>What <see cref="Forge"/> makes.</summary>
    public class Ware;

    /// <summary>The <see cref="Ware"/> <see cref="Smithy"/> makes instead.</summary>
    public sealed class Blade : Ware;

    /// <summary>Declares the base methods <see cref="Smithy"/> overrides with a covariant return and hides with another access.</summary>
    public class Forge
    {
        public virtual Ware Make() => new();

        protected int Loudness { get; } = 1;

        protected int Shout(int times) => times * Loudness;
    }

    /// <summary>Overrides <see cref="Forge.Make"/> returning a <see cref="Blade"/>, and hides <c>Forge.Shout</c> as public.</summary>
    public sealed class Smithy : Forge
    {
        public override Blade Make() => new();

        public new int Shout(int times) => times * Loudness * 2;
    }
}
