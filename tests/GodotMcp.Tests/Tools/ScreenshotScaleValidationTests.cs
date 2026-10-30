using System.Text.Json.Nodes;
using GodotMcp.Server.Tools;
using ModelContextProtocol;

namespace GodotMcp.Tests.Tools;

/// <summary>take_screenshot's scale check, which refuses before the bridge is asked, and what it adds to the bridge's parameters.</summary>
public sealed class ScreenshotScaleValidationTests
{
    [Theory]
    [InlineData(0.5, "0.5")]
    [InlineData(0, "0")]
    [InlineData(-2, "-2")]
    [InlineData(4.5, "4.5")]
    [InlineData(5, "5")]
    [InlineData(double.NaN, "NaN")]
    public void AScaleOutsideOneToFourIsRefused(double scale, string shown)
    {
        McpException refused = Assert.Throws<McpException>(() => RuntimeTools.AddScale([], scale));

        Assert.Equal($"scale must be 1 to 4; got {shown}.", refused.Message);
    }

    [Theory]
    [InlineData(1.5)]
    [InlineData(2)]
    [InlineData(4)]
    public void AScaleAboveOneIsSentToTheBridge(double scale)
    {
        JsonObject parameters = RuntimeTools.AddScale(new JsonObject { ["previewMaxWidth"] = 480 }, scale);

        Assert.Equal(scale, parameters["scale"]!.GetValue<double>());
        Assert.Equal(480, parameters["previewMaxWidth"]!.GetValue<int>());
    }

    [Fact]
    public void TheDefaultScaleSendsNothingNew()
    {
        JsonObject parameters = RuntimeTools.AddScale(new JsonObject { ["previewMaxWidth"] = 480 }, 1);

        Assert.Equal("""{"previewMaxWidth":480}""", parameters.ToJsonString());
    }
}
