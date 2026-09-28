using GodotMcp.Server.Tools;

namespace GodotMcp.Tests.Tools;

/// <summary>The error a run_script or call_method the bridge stopped at its timeout fails with, without a game.</summary>
public sealed class StoppedMessageTests
{
    private const string ScriptStopped =
        "stopped: its coroutine will not resume; restored Engine.time_scale to 1.0. A coroutine it awaited on another object, "
        + "such as a node's own method, keeps running; restart_project stops everything.";

    private const string CallForgotten =
        "no longer awaited: the method keeps running on its node; restored SceneTree.paused to false; restart_project stops it.";

    [Fact]
    public void AStoppedRunScriptSaysSoWithItsHint()
    {
        string? message = RuntimeTools.StoppedMessage("run_script", TimeSpan.FromMilliseconds(1500), Refused("run_script", ScriptStopped));

        Assert.Equal(
            "run_script timed out after 1.5 s and was stopped: its coroutine will not resume; restored Engine.time_scale to 1.0. "
                + "A coroutine it awaited on another object, such as a node's own method, keeps running; restart_project stops "
                + "everything; a script that needs longer can raise timeoutMs.",
            message
        );
    }

    [Fact]
    public void AForgottenCallMethodSaysSoWithItsHint()
    {
        string? message = RuntimeTools.StoppedMessage("call_method", TimeSpan.FromSeconds(10), Refused("call_method", CallForgotten));

        Assert.Equal(
            "call_method timed out after 10 s; it was no longer awaited: the method keeps running on its node; restored "
                + "SceneTree.paused to false; restart_project stops it; a method that needs longer can raise options.timeoutMs.",
            message
        );
    }

    [Fact]
    public void ARefusalThatIsNotAStopIsNull()
    {
        string refusal = Refused("run_script", "the script did not compile (Parse error, error 43)");

        Assert.Null(RuntimeTools.StoppedMessage("run_script", TimeSpan.FromSeconds(1), refusal));
    }

    [Fact]
    public void AStopTextUnderAnotherToolsPrefixIsNull()
    {
        Assert.Null(RuntimeTools.StoppedMessage("run_script", TimeSpan.FromSeconds(1), Refused("call_method", ScriptStopped)));
        Assert.Null(RuntimeTools.StoppedMessage("call_method", TimeSpan.FromSeconds(1), Refused("call_method", ScriptStopped)));
        Assert.Null(RuntimeTools.StoppedMessage("wait_for", TimeSpan.FromSeconds(1), Refused("wait_for", ScriptStopped)));
    }

    private static string Refused(string command, string answer) => $"The bridge refused '{command}': {answer}";
}
