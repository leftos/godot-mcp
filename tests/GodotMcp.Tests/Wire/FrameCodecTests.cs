using System.Text;
using GodotMcp.Server.Wire;

namespace GodotMcp.Tests.Wire;

public sealed class FrameCodecTests
{
    [Fact]
    public void EncodeThenDecodeReturnsThePayload()
    {
        byte[] payload = Encoding.UTF8.GetBytes("{\"id\":7,\"text\":\"Grüße ✓\"}");

        byte[] frame = FrameCodec.Encode(payload);
        FrameDecoder decoder = new();
        decoder.Append(frame);

        Assert.Equal([0, 0, 0, (byte)payload.Length], frame[..4]);
        Assert.True(decoder.TryReadFrame(out byte[] decoded));
        Assert.Equal(payload, decoded);
        Assert.False(decoder.TryReadFrame(out _));
    }

    [Fact]
    public void EncodeRejectsAPayloadOverTheLimit()
    {
        byte[] payload = new byte[FrameCodec.MaxPayloadBytes + 1];

        Assert.Throws<InvalidDataException>(() => FrameCodec.Encode(payload));
    }

    [Fact]
    public void DecoderRejectsAHeaderOverTheLimit()
    {
        FrameDecoder decoder = new();
        decoder.Append(Header(FrameCodec.MaxPayloadBytes + 1));

        Assert.Throws<InvalidDataException>(() => decoder.TryReadFrame(out _));
    }

    [Fact]
    public void DecoderWaitsForTheBodyOfAFrameAtTheLimit()
    {
        FrameDecoder decoder = new();
        decoder.Append(Header(FrameCodec.MaxPayloadBytes));

        Assert.False(decoder.TryReadFrame(out _));
    }

    [Fact]
    public void DecoderReassemblesFramesFedOneByteAtATime()
    {
        byte[] first = Encoding.UTF8.GetBytes("{\"id\":1}");
        byte[] second = Encoding.UTF8.GetBytes("{\"id\":2,\"ok\":true}");
        byte[] stream = [.. FrameCodec.Encode(first), .. FrameCodec.Encode(second)];
        FrameDecoder decoder = new();
        List<byte[]> frames = [];

        foreach (byte b in stream)
        {
            decoder.Append([b]);
            while (decoder.TryReadFrame(out byte[] payload))
            {
                frames.Add(payload);
            }
        }

        Assert.Equal(2, frames.Count);
        Assert.Equal(first, frames[0]);
        Assert.Equal(second, frames[1]);
    }

    [Fact]
    public void DecoderSplitsTwoFramesThatArriveInOneChunk()
    {
        byte[] first = Encoding.UTF8.GetBytes("{\"id\":1}");
        byte[] second = Encoding.UTF8.GetBytes("{\"id\":2}");
        FrameDecoder decoder = new();

        decoder.Append([.. FrameCodec.Encode(first), .. FrameCodec.Encode(second)]);

        Assert.True(decoder.TryReadFrame(out byte[] one));
        Assert.True(decoder.TryReadFrame(out byte[] two));
        Assert.Equal(first, one);
        Assert.Equal(second, two);
        Assert.False(decoder.TryReadFrame(out _));
    }

    private static byte[] Header(long length) => [(byte)(length >> 24), (byte)(length >> 16), (byte)(length >> 8), (byte)length];
}
