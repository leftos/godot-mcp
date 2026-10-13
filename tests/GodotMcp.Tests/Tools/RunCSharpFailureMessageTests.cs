using GodotMcp.Server.CSharp;
using GodotMcp.Server.Session;
using GodotMcp.Server.Tools;

namespace GodotMcp.Tests.Tools;

/// <summary>The message a snippet that threw fails run_csharp with: its refusal, its log, and the errors the game raised.</summary>
public sealed class RunCSharpFailureMessageTests
{
    private const string Refusal =
        "run_csharp failed: The C# helper refused the request: the snippet threw InvalidOperationException: boom\n   at ... in snippet:line 1";

    [Fact]
    public void WithNoLogAndNoErrorsTheRefusalStandsAsItIs() => Assert.Equal(Refusal, RuntimeTools.FailureMessage(Refusal, SnippetLog.Empty, []));

    [Fact]
    public void TheLogFollowsTheRefusalsStackLine()
    {
        string message = RuntimeTools.FailureMessage(Refusal, new SnippetLog(["a", "b"], 0), []);

        Assert.Equal([.. Refusal.Split('\n'), "Log before the failure (2 lines):", "  a", "  b"], message.Split('\n'));
    }

    [Fact]
    public void ANewlineInsideALoggedValueIsIndentedSoItCannotLookLikeAHeader()
    {
        string message = RuntimeTools.FailureMessage(Refusal, new SnippetLog(["x\nGodot reported: y"], 0), []);

        Assert.EndsWith("Log before the failure (1 line):\n  x\n  Godot reported: y", message, StringComparison.Ordinal);
    }

    [Fact]
    public void ACappedLogCountsWhatTheHelperDidNotKeep()
    {
        string message = RuntimeTools.FailureMessage(Refusal, new SnippetLog(["a"], 7), []);

        Assert.EndsWith("Log before the failure (1 line):\n  a\n… 7 more not kept", message, StringComparison.Ordinal);
    }

    [Fact]
    public void TheGamesErrorsFollowTheLog()
    {
        ErrorEntry error = new(1, ErrorFeed.ErrorType, "boom", "res://x.gd", 5, "f", [], string.Empty);

        string message = RuntimeTools.FailureMessage(Refusal, new SnippetLog(["a"], 0), [error]);

        Assert.EndsWith("Log before the failure (1 line):\n  a\nGodot reported:\nboom at res://x.gd:5 in f", message, StringComparison.Ordinal);
    }
}
