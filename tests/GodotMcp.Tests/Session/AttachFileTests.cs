using System.Text.Json.Nodes;
using GodotMcp.Server.Session;
using GodotMcp.TestSupport;

namespace GodotMcp.Tests.Session;

public sealed class AttachFileTests : IDisposable
{
    private static readonly ArmSettings Plain = new(Quiet: false, ShutOutRealGamepads: false, Mute: false);

    private readonly TempDirectory _project = new();

    public void Dispose() => _project.Dispose();

    [Fact]
    public void WriteCreatesTheFolderAndHoldsThePortAndToken()
    {
        AttachFile.Write(_project.Path, new BridgeEndpoint(51234, "ABCDEF"), Plain);

        JsonNode content = JsonNode.Parse(File.ReadAllText(_project.Combine(".godot", "godot-mcp", "attach.json")))!;
        Assert.Equal((51234, "ABCDEF"), (content["port"]!.GetValue<int>(), content["token"]!.GetValue<string>()));
    }

    [Fact]
    public void WriteHoldsWhetherToShutOutRealGamepads()
    {
        AttachFile.Write(_project.Path, new BridgeEndpoint(51234, "ABCDEF"), Plain);
        bool byDefault = JsonNode.Parse(File.ReadAllText(AttachFile.PathIn(_project.Path)))!["shutOutRealGamepads"]!.GetValue<bool>();
        AttachFile.Write(_project.Path, new BridgeEndpoint(51234, "ABCDEF"), Plain with { ShutOutRealGamepads = true });
        bool shutOut = JsonNode.Parse(File.ReadAllText(AttachFile.PathIn(_project.Path)))!["shutOutRealGamepads"]!.GetValue<bool>();

        Assert.Equal((false, true), (byDefault, shutOut));
    }

    [Fact]
    public void WriteHoldsWhetherTheGameIsQuiet()
    {
        AttachFile.Write(_project.Path, new BridgeEndpoint(51234, "ABCDEF"), Plain);
        bool notQuiet = JsonNode.Parse(File.ReadAllText(AttachFile.PathIn(_project.Path)))!["quiet"]!.GetValue<bool>();
        AttachFile.Write(_project.Path, new BridgeEndpoint(51234, "ABCDEF"), Plain with { Quiet = true });
        JsonNode quiet = JsonNode.Parse(File.ReadAllText(AttachFile.PathIn(_project.Path)))!;

        Assert.Equal((false, true, false), (notQuiet, quiet["quiet"]!.GetValue<bool>(), quiet["shutOutRealGamepads"]!.GetValue<bool>()));
    }

    [Fact]
    public void WriteHoldsTheEffectiveMuteWhichAQuietGameHasToo()
    {
        bool plain = WrittenMute(Plain);
        bool muted = WrittenMute(Plain with { Mute = true });
        bool quiet = WrittenMute(Plain with { Quiet = true });

        Assert.Equal((false, true, true), (plain, muted, quiet));
    }

    [Fact]
    public void WriteReplacesAnOlderFile()
    {
        AttachFile.Write(_project.Path, new BridgeEndpoint(1111, "OLD"), Plain);

        AttachFile.Write(_project.Path, new BridgeEndpoint(2222, "NEW"), Plain);

        JsonNode content = JsonNode.Parse(File.ReadAllText(AttachFile.PathIn(_project.Path)))!;
        Assert.Equal((2222, "NEW"), (content["port"]!.GetValue<int>(), content["token"]!.GetValue<string>()));
    }

    [Fact]
    public void RemoveWithItsTokenDeletesTheFile()
    {
        AttachFile.Write(_project.Path, new BridgeEndpoint(51234, "ABCDEF"), Plain);

        Assert.True(AttachFile.Remove(_project.Path, "ABCDEF"));
        Assert.False(File.Exists(AttachFile.PathIn(_project.Path)));
    }

    [Fact]
    public void RemoveWithAnotherTokenLeavesTheFile()
    {
        AttachFile.Write(_project.Path, new BridgeEndpoint(51234, "ANOTHER"), Plain);

        Assert.False(AttachFile.Remove(_project.Path, "ABCDEF"));
        Assert.Equal("ANOTHER", JsonNode.Parse(File.ReadAllText(AttachFile.PathIn(_project.Path)))!["token"]!.GetValue<string>());
    }

    [Fact]
    public void RemoveWithoutAFileDoesNothing() => Assert.False(AttachFile.Remove(_project.Path, "ABCDEF"));

    [Theory]
    [InlineData("{not json")]
    [InlineData("{\"port\":51234}")]
    [InlineData("{\"token\":42}")]
    [InlineData("[\"ABCDEF\"]")]
    public void RemoveLeavesAFileThatIsNotAnAttachFile(string content)
    {
        string path = AttachFile.PathIn(_project.Path);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);

        Assert.False(AttachFile.Remove(_project.Path, "ABCDEF"));
        Assert.Equal(content, File.ReadAllText(path));
    }

    // Godot opens a file for reading sharing read and write but not delete, so a delete or replace landing during a bridge's
    // read fails with a sharing violation until the read ends.
    [Fact]
    public void ARemoveWaitsOutABridgeRead()
    {
        AttachFile.Write(_project.Path, new BridgeEndpoint(51234, "ABCDEF"), Plain);
        string path = AttachFile.PathIn(_project.Path);
        FileStream read = OpenAsGodotReads(path);
        int retries = 0;
        Action<string> onRetried = retried =>
        {
            if (retried == path)
            {
                retries++;
                read.Dispose();
            }
        };
        AttachFile.HeldOpenRetried += onRetried;
        try
        {
            Assert.True(AttachFile.Remove(_project.Path, "ABCDEF"));
        }
        finally
        {
            AttachFile.HeldOpenRetried -= onRetried;
            read.Dispose();
        }

        Assert.False(File.Exists(path));
        Assert.Equal(1, retries);
    }

    [Fact]
    public void AWriteWaitsOutABridgeRead()
    {
        AttachFile.Write(_project.Path, new BridgeEndpoint(1111, "OLD"), Plain);
        string path = AttachFile.PathIn(_project.Path);
        FileStream read = OpenAsGodotReads(path);
        int retries = 0;
        Action<string> onRetried = retried =>
        {
            if (retried == path)
            {
                retries++;
                read.Dispose();
            }
        };
        AttachFile.HeldOpenRetried += onRetried;
        try
        {
            AttachFile.Write(_project.Path, new BridgeEndpoint(2222, "NEW"), Plain);
        }
        finally
        {
            AttachFile.HeldOpenRetried -= onRetried;
            read.Dispose();
        }

        Assert.Equal("NEW", AttachFile.TokenIn(path));
        Assert.Equal(1, retries);
    }

    [Fact]
    public void ARemoveGivesUpAfterItsRetries()
    {
        AttachFile.Write(_project.Path, new BridgeEndpoint(51234, "ABCDEF"), Plain);
        string path = AttachFile.PathIn(_project.Path);
        int retries = 0;
        Action<string> onRetried = retried =>
        {
            if (retried == path)
            {
                retries++;
            }
        };
        AttachFile.HeldOpenRetried += onRetried;
        try
        {
            using FileStream read = OpenAsGodotReads(path);
            Assert.Throws<IOException>(() => AttachFile.Remove(_project.Path, "ABCDEF"));
        }
        finally
        {
            AttachFile.HeldOpenRetried -= onRetried;
        }

        Assert.Equal(19, retries);
    }

    [Fact]
    public void AWriteGivesUpAfterItsRetries()
    {
        AttachFile.Write(_project.Path, new BridgeEndpoint(1111, "OLD"), Plain);
        string path = AttachFile.PathIn(_project.Path);
        int retries = 0;
        Action<string> onRetried = retried =>
        {
            if (retried == path)
            {
                retries++;
            }
        };
        AttachFile.HeldOpenRetried += onRetried;
        try
        {
            using FileStream read = OpenAsGodotReads(path);
            Assert.Throws<UnauthorizedAccessException>(() => AttachFile.Write(_project.Path, new BridgeEndpoint(2222, "NEW"), Plain));
        }
        finally
        {
            AttachFile.HeldOpenRetried -= onRetried;
        }

        Assert.Equal(19, retries);
    }

    /// <summary>Opens the file as Godot's reader does on Windows: sharing read and write, not delete.</summary>
    internal static FileStream OpenAsGodotReads(string path) => new(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);

    private bool WrittenMute(ArmSettings settings)
    {
        AttachFile.Write(_project.Path, new BridgeEndpoint(51234, "ABCDEF"), settings);
        return JsonNode.Parse(File.ReadAllText(AttachFile.PathIn(_project.Path)))!["mute"]!.GetValue<bool>();
    }
}
