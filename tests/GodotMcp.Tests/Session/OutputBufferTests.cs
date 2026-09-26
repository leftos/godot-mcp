using GodotMcp.Server.Session;

namespace GodotMcp.Tests.Session;

public sealed class OutputBufferTests
{
    [Fact]
    public void ThePageWithoutBeforeIsTheNewestLines()
    {
        OutputBuffer buffer = Filled(capacity: 10, lines: 25);

        OutputPage page = buffer.Page(3, null);

        Assert.Equal(23, page.FirstLine);
        Assert.Equal(["line 23", "line 24", "line 25"], page.Lines);
    }

    [Fact]
    public void BeforePagesBackByLineNumbersThatSurviveTheRingRolling()
    {
        OutputBuffer buffer = Filled(capacity: 10, lines: 25);

        OutputPage page = buffer.Page(3, 23);
        OutputPage clipped = buffer.Page(5, 18);
        OutputPage gone = buffer.Page(5, 16);

        Assert.Equal(20, page.FirstLine);
        Assert.Equal(["line 20", "line 21", "line 22"], page.Lines);
        Assert.Equal(16, clipped.FirstLine);
        Assert.Equal(["line 16", "line 17"], clipped.Lines);
        Assert.Null(gone.FirstLine);
        Assert.Empty(gone.Lines);
    }

    [Fact]
    public void BeforePastTheEndReturnsTheNewestLines()
    {
        OutputBuffer buffer = Filled(capacity: 10, lines: 4);

        OutputPage page = buffer.Page(10, 100);

        Assert.Equal(1, page.FirstLine);
        Assert.Equal(4, page.Lines.Count);
    }

    [Fact]
    public void AnEmptyStreamHasNoFirstLine()
    {
        OutputPage page = new OutputBuffer(10).Page(10, null);

        Assert.Null(page.FirstLine);
        Assert.Empty(page.Lines);
    }

    [Fact]
    public void LongLinesAreCutWithTheCountOfWhatWasDropped()
    {
        OutputBuffer buffer = new(10);
        buffer.Add(new string('x', OutputBuffer.MaxLineLength));
        buffer.Add(new string('y', OutputBuffer.MaxLineLength + 234));

        OutputPage page = buffer.Page(10, null);

        Assert.Equal(new string('x', OutputBuffer.MaxLineLength), page.Lines[0]);
        Assert.Equal(new string('y', OutputBuffer.MaxLineLength) + "… (+234 chars)", page.Lines[1]);
    }

    private static OutputBuffer Filled(int capacity, int lines)
    {
        OutputBuffer buffer = new(capacity);
        for (int line = 1; line <= lines; line++)
        {
            buffer.Add($"line {line}");
        }

        return buffer;
    }
}
