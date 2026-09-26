using System.Text.Json.Nodes;
using GodotMcp.Server.Session;
using GodotMcp.Server.Tools;

namespace GodotMcp.Tests.Tools;

public sealed class ErrorReportTests
{
    [Fact]
    public void NoErrorsLeaveTheResultAsItIs()
    {
        JsonObject result = ErrorReport.AddTo(new JsonObject { ["value"] = 7 }, []);

        Assert.Equal("""{"value":7}""", result.ToJsonString());
    }

    [Fact]
    public void IdenticalErrorsCollapseWithACount()
    {
        ErrorEntry[] errors = [Entry(1, "boom", 5), Entry(2, "other", 5), Entry(3, "boom", 5), Entry(4, "boom", 6)];

        JsonArray shown = ErrorReport.AddTo([], errors)["errors"]!.AsArray();

        Assert.Equal(3, shown.Count);
        Assert.Equal(1, shown[0]!["seq"]!.GetValue<long>());
        Assert.Equal(2, shown[0]!["count"]!.GetValue<int>());
        Assert.Null(shown[1]!["count"]);
        Assert.Equal(6, shown[2]!["line"]!.GetValue<int>());
        Assert.Equal("res://x.gd", shown[0]!["file"]!.GetValue<string>());
        Assert.Equal("f", shown[0]!["function"]!.GetValue<string>());
        Assert.Equal("res://x.gd:5 in f", shown[0]!["stack"]![0]!.GetValue<string>());
    }

    [Fact]
    public void AtMostTwentyErrorsThenErrorsOmitted()
    {
        ErrorEntry[] errors = [.. Enumerable.Range(1, 23).Select(index => Entry(index, $"error {index}", index))];

        JsonObject result = ErrorReport.AddTo([], errors);

        Assert.Equal(ErrorReport.MaxPerResult, result["errors"]!.AsArray().Count);
        Assert.Equal(3, result["errorsOmitted"]!.GetValue<int>());
    }

    [Fact]
    public void LongMessagesAreCutAndEmptyFieldsLeftOut()
    {
        ErrorEntry error = new(9, ErrorFeed.ErrorType, new string('m', 2500), string.Empty, 0, string.Empty, [], string.Empty);

        JsonObject shown = ErrorReport.AddTo([], [error])["errors"]![0]!.AsObject();

        Assert.Equal(new string('m', 2000) + "… (+500 chars)", shown["message"]!.GetValue<string>());
        Assert.Equal(["seq", "message"], shown.Select(property => property.Key));
    }

    [Fact]
    public void SummaryNamesEachErrorsPlaceAndStack()
    {
        string summary = ErrorReport.Summarise([Entry(1, "boom", 5), Entry(2, "boom", 5)]);

        Assert.Equal("boom (x2) at res://x.gd:5 in f\n    res://x.gd:5 in f", summary);
    }

    private static ErrorEntry Entry(long seq, string message, int line) =>
        new(seq, ErrorFeed.ErrorType, message, "res://x.gd", line, "f", [$"res://x.gd:{line} in f"], string.Empty);
}
