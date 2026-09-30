using System.Text.Json;
using System.Text.Json.Nodes;
using GodotMcp.Server.Session;

namespace GodotMcp.Tests.Session;

/// <summary>
/// The restart result's warning on its own: the replaced game's debugger text joined with the relaunched window's size text,
/// and the window it reports being left out of the JSON when the bridge's hello carried none.
/// </summary>
public sealed class RestartResultTests
{
    private const string Debugger = "A debugger was attached to the game (pid 42); its debug session ended with the game.";
    private const string Size =
        "--resolution asked for a 1000x900 window and the game's window is 800x600: the system did not give it the size asked "
        + "for, so screenshots and input work in the window it has.";

    // Unlike ToolJson.Options, which ignores nulls globally and so would hide a missing attribute, this leaves a null out
    // only where the record's own JsonIgnore says so, making the test below able to fail.
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    [Fact]
    public void NoWarningOnEitherSideIsNone() => Assert.Null(GodotSession.JoinWarnings(null, null));

    [Fact]
    public void ADebuggerWarningAloneIsKept() => Assert.Equal(Debugger, GodotSession.JoinWarnings(Debugger, null));

    [Fact]
    public void ASizeWarningAloneIsKept() => Assert.Equal(Size, GodotSession.JoinWarnings(null, Size));

    [Fact]
    public void BothAreJoinedWithTheDebuggerFirst() => Assert.Equal($"{Debugger} {Size}", GodotSession.JoinWarnings(Debugger, Size));

    [Fact]
    public void ARestartWithoutAWindowWritesNoWindowKey()
    {
        RestartResult result = new("probe", @"X:\probe", 1, 2, 0, PrepResult.Skipped, @"F:\Godot\godot.exe")
        {
            PreviousAlreadyExited = false,
            PreviousGameExitCode = null,
        };

        JsonObject json = JsonNode.Parse(JsonSerializer.Serialize(result, Json))!.AsObject();

        Assert.False(json.ContainsKey("window"), json.ToJsonString());
    }
}
