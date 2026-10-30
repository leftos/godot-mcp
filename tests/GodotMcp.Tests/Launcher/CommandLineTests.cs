using GodotMcp.Launcher;

namespace GodotMcp.Tests.Launcher;

/// <summary>The command line the launcher passes its own arguments on to the server with.</summary>
public sealed class CommandLineTests
{
    private const string Server = @"C:\Users\me\AppData\Local\godot-mcp\versions\0.23.0-abc1234\godot-mcp.exe";

    [Theory]
    [InlineData("--sweep-agents", "--sweep-agents")]
    [InlineData(@"C:\a\b", @"C:\a\b")]
    [InlineData("two words", "\"two words\"")]
    [InlineData("tab\there", "\"tab\there\"")]
    [InlineData("say \"hi\"", "\"say \\\"hi\\\"\"")]
    [InlineData("\"", "\"\\\"\"")]
    [InlineData(@"C:\folder\", @"C:\folder\")]
    [InlineData(@"C:\folder with space\", "\"C:\\folder with space\\\\\"")]
    [InlineData(@"a\""b", "\"a\\\\\\\"b\"")]
    [InlineData("", "\"\"")]
    [InlineData(" ", "\" \"")]
    public void EachArgumentIsQuotedAsArgumentListQuotesIt(string argument, string quoted) =>
        Assert.Equal($"\"{Server}\" {quoted}", CommandLine.Build(Server, [argument]));

    [Fact]
    public void TheArgumentsFollowTheQuotedServerInOrder() =>
        Assert.Equal($"\"{Server}\" --list-tools \"\" last", CommandLine.Build(Server, ["--list-tools", "", "last"]));

    [Fact]
    public void NoArgumentsIsTheServerAlone() => Assert.Equal($"\"{Server}\"", CommandLine.Build(Server, []));
}
