using System.Reflection;

namespace GodotMcp.Dotnet.Core;

/// <summary>What game code threw, as a refusal spells it: <c>Type: message</c>, then its stack on lines of its own.</summary>
internal static class Thrown
{
    /// <summary>
    /// The exception the game code threw: out of the <see cref="TargetInvocationException"/> reflection wraps it in, and
    /// out of an <see cref="AggregateException"/> holding it alone, as a faulted task does.
    /// </summary>
    public static Exception Unwrap(Exception exception) =>
        exception switch
        {
            TargetInvocationException { InnerException: { } inner } => Unwrap(inner),
            AggregateException { InnerExceptions.Count: 1 } aggregate => Unwrap(aggregate.InnerExceptions[0]),
            _ => exception,
        };

    /// <summary><c>Type: message</c> of <paramref name="thrown"/>.</summary>
    public static string Describe(Exception thrown) => $"{thrown.GetType().Name}: {thrown.Message}";

    /// <summary>The stack of <paramref name="thrown"/> after a line break, or nothing when it has none.</summary>
    public static string Stack(Exception thrown) => thrown.StackTrace is { Length: > 0 } trace ? "\n" + trace : "";
}
