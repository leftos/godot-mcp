using System.Text.Json.Nodes;
using GodotMcp.Server.Session;
using GodotMcp.Server.Tools;

namespace GodotMcp.Tests.Tools;

/// <summary>The lane's wording: the call a refusal names, at its path in the parameters, and the refusal itself.</summary>
public sealed class CallLaneTextTests
{
    [Fact]
    public void NoCallMeansNoHolder()
    {
        JsonObject parameters = new() { ["kind"] = "frames", ["timeoutMs"] = 1000 };

        Assert.Null(RuntimeTools.CallHolderOf("wait_for", parameters));
    }

    [Fact]
    public void ACallIsNamedWithItsPath()
    {
        JsonObject parameters = new() { ["call"] = Method("/root/A", "f", new JsonArray(0, "x")) };

        Assert.Equal("wait_for with call /root/A.f(0, \"x\")", RuntimeTools.CallHolderOf("wait_for", parameters));
    }

    [Fact]
    public void AThenCallIsNamedThenCall()
    {
        JsonObject parameters = new() { ["then"] = new JsonObject { ["call"] = Method("/root/A", "f", new JsonArray()) } };

        Assert.Equal("wait_for with then.call /root/A.f()", RuntimeTools.CallHolderOf("wait_for", parameters));
    }

    [Fact]
    public void AStartCallIsNamedStartCall()
    {
        JsonObject parameters = new() { ["start"] = new JsonObject { ["call"] = Method("/root/A", "f", new JsonArray()) } };

        Assert.Equal("capture_frames with start.call /root/A.f()", RuntimeTools.CallHolderOf("capture_frames", parameters));
    }

    [Fact]
    public void AStartThenCallIsNamedStartThenCall()
    {
        JsonObject parameters = new()
        {
            ["start"] = new JsonObject { ["then"] = new JsonObject { ["call"] = Method("/root/A", "f", new JsonArray()) } },
        };

        Assert.Equal("wait_for with start.then.call /root/A.f()", RuntimeTools.CallHolderOf("wait_for", parameters));
    }

    [Fact]
    public void AGameToolIsNamedByItsName()
    {
        JsonObject parameters = new()
        {
            ["call"] = new JsonObject { ["tool"] = "Spawn", ["request"] = "{}" },
        };

        Assert.Equal("wait_for with call game tool Spawn", RuntimeTools.CallHolderOf("wait_for", parameters));
    }

    [Fact]
    public void ALongDescriptionIsCutToItsFirstHundredNineteenCharactersAndAnEllipsis()
    {
        // "/root/A." and "()" around a 190-letter method make the description exactly 200 characters.
        string described = RuntimeTools.DescribeCall(Method("/root/A", new string('m', 190), new JsonArray()));

        Assert.Equal(120, described.Length);
        Assert.EndsWith("…", described, StringComparison.Ordinal);
        Assert.StartsWith("/root/A.m", described, StringComparison.Ordinal);
    }

    [Fact]
    public void TheRefusalNamesTheHolderAndHowLongItHasRun() =>
        Assert.Equal(
            "session 'main' is running run_script for 1.2 s; send this call after it returns, or chain both as steps of one batch_drive.",
            RuntimeTools.CallLaneRefusal("main", new CallLaneHolder("run_script", TimeSpan.FromMilliseconds(1200)))
        );

    private static JsonObject Method(string node, string method, JsonArray args) =>
        new()
        {
            ["node"] = node,
            ["method"] = method,
            ["args"] = args,
        };
}
