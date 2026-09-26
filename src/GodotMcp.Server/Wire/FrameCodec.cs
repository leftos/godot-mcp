using System.Buffers.Binary;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace GodotMcp.Server.Wire;

/// <summary>
/// The bridge wire format: a 4-byte big-endian payload length followed by that many bytes of UTF-8 JSON.
/// </summary>
internal static class FrameCodec
{
    public const int HeaderLength = 4;
    public const int MaxPayloadBytes = 16 * 1024 * 1024;

    public static byte[] Encode(ReadOnlySpan<byte> payload)
    {
        if (payload.Length > MaxPayloadBytes)
        {
            throw new InvalidDataException($"A frame payload of {payload.Length} bytes exceeds the {MaxPayloadBytes}-byte limit.");
        }

        byte[] frame = new byte[HeaderLength + payload.Length];
        BinaryPrimitives.WriteInt32BigEndian(frame, payload.Length);
        payload.CopyTo(frame.AsSpan(HeaderLength));
        return frame;
    }

    public static byte[] EncodeJson(JsonNode message) => Encode(Encoding.UTF8.GetBytes(message.ToJsonString()));

    public static JsonObject DecodeJson(byte[] payload)
    {
        JsonNode? node;
        try
        {
            node = JsonNode.Parse(payload);
        }
        catch (JsonException e)
        {
            throw new InvalidDataException($"A frame is not valid JSON: {e.Message}", e);
        }

        return node as JsonObject ?? throw new InvalidDataException("A frame's JSON is not an object.");
    }
}
