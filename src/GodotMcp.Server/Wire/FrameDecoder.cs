using System.Buffers.Binary;

namespace GodotMcp.Server.Wire;

/// <summary>
/// Reassembles frames from bytes that arrive in arbitrary chunks: a chunk may hold part of a header, several frames, or
/// the tail of one frame and the start of the next.
/// </summary>
internal sealed class FrameDecoder
{
    private byte[] _buffer = new byte[4096];
    private int _count;

    public void Append(ReadOnlySpan<byte> chunk)
    {
        EnsureCapacity(_count + chunk.Length);
        chunk.CopyTo(_buffer.AsSpan(_count));
        _count += chunk.Length;
    }

    /// <summary>Takes the next whole frame's payload, if one has arrived.</summary>
    /// <exception cref="InvalidDataException">The next frame's header announces more than <see cref="FrameCodec.MaxPayloadBytes"/>.</exception>
    public bool TryReadFrame(out byte[] payload)
    {
        payload = [];
        if (_count < FrameCodec.HeaderLength)
        {
            return false;
        }

        uint length = BinaryPrimitives.ReadUInt32BigEndian(_buffer);
        if (length > FrameCodec.MaxPayloadBytes)
        {
            throw new InvalidDataException($"A frame header announces {length} bytes, over the {FrameCodec.MaxPayloadBytes}-byte limit.");
        }

        int frameLength = FrameCodec.HeaderLength + (int)length;
        if (_count < frameLength)
        {
            return false;
        }

        payload = _buffer.AsSpan(FrameCodec.HeaderLength, (int)length).ToArray();
        _buffer.AsSpan(frameLength, _count - frameLength).CopyTo(_buffer);
        _count -= frameLength;
        return true;
    }

    private void EnsureCapacity(int needed)
    {
        if (needed <= _buffer.Length)
        {
            return;
        }

        Array.Resize(ref _buffer, Math.Max(needed, _buffer.Length * 2));
    }
}
