using System.IO.Pipes;
using GodotMcp.Capture;

namespace GodotMcp.Tests.Capture;

public sealed class AudioPipeTests
{
    [Fact]
    public void APipeNameAlreadyInUseIsRefusedAsAnAudioActivationFailure()
    {
        string name = $"godot-mcp-test-{Guid.NewGuid():N}";
        using var held = new NamedPipeServerStream(name, PipeDirection.Out, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous);

        AudioPipeException failure = Assert.Throws<AudioPipeException>(() => AudioPipe.Create(name));

        Assert.IsAssignableFrom<AudioActivationException>(failure);
        Assert.Contains($"the audio pipe '{name}' could not be created: ", failure.Message, StringComparison.Ordinal);
    }
}
