namespace GodotMcp.Server.CSharp;

/// <summary>
/// The helper refused a request. It stays an <see cref="InvalidOperationException"/>, so every C# tool still maps it to
/// <c>&lt;tool&gt; failed: …</c>, and it carries the log the snippet wrote before it failed, which a refusal would lose.
/// </summary>
internal sealed class CSharpRefusedException(string message, SnippetLog log) : InvalidOperationException(message)
{
    /// <summary>The lines the snippet logged before the refusal, empty when it logged none.</summary>
    public SnippetLog Log { get; } = log;
}
