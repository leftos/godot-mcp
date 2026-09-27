using GodotMcp.Server.Tools;
using ModelContextProtocol;

namespace GodotMcp.Tests.Tools;

/// <summary>describe_class's argument checks, which refuse before a bridge or a headless Godot is asked; no Godot runs here.</summary>
public sealed class DescribeValidationTests
{
    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void AnEmptyClassNameIsRefused(string className)
    {
        McpException refused = Assert.Throws<McpException>(() => HeadlessTools.DescribeParameters(className, null));

        Assert.Equal("className is empty; name an engine class (Node2D, Button) or a script class_name of the project.", refused.Message);
    }

    [Fact]
    public void ANegativeOffsetIsRefused()
    {
        McpException refused = Assert.Throws<McpException>(() => HeadlessTools.DescribeParameters("Node", new DescribeOptions(Offset: -1)));

        Assert.Equal("offset must be 0 or more; got -1.", refused.Message);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-3)]
    [InlineData(501)]
    public void ALimitOutsideOneTo500IsRefused(int limit)
    {
        McpException refused = Assert.Throws<McpException>(() => HeadlessTools.DescribeParameters("Node", new DescribeOptions(Limit: limit)));

        Assert.Equal($"limit must be 1 to 500; got {limit}.", refused.Message);
    }

    [Fact]
    public void TheParametersCarryTheTrimmedNameAndTheDefaults()
    {
        Assert.Equal(
            """{"className":"Node2D","inherited":false,"offset":0,"limit":100}""",
            HeadlessTools.DescribeParameters(" Node2D ", null).ToJsonString()
        );
        Assert.Equal(
            """{"className":"Button","inherited":true,"offset":500,"limit":500}""",
            HeadlessTools.DescribeParameters("Button", new DescribeOptions(true, 500, 500)).ToJsonString()
        );
    }
}
