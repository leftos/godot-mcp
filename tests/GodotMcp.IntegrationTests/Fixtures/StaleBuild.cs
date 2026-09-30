using System.Collections.Immutable;
using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using System.Reflection.PortableExecutable;
using GodotMcp.Server.Session;

namespace GodotMcp.IntegrationTests.Fixtures;

/// <summary>
/// Makes the build on disk newer than the one a running game loaded: the game assembly's dll replaced by a copy whose MVID
/// differs, so the C# tools see the game running an older build; the original is put back afterwards.
/// </summary>
internal static class StaleBuild
{
    /// <summary>Runs <paramref name="action"/> while the game assembly on disk in <paramref name="projectDir"/> has another MVID.</summary>
    public static async Task WhileStaleAsync(string projectDir, string assemblyName, Func<Task> action, CancellationToken cancellation)
    {
        string built = PrepScan.AssemblyPath(projectDir, assemblyName);
        string aside = built + ".aside";

        // The game maps the built dll, which Windows lets be renamed but not overwritten, so the original moves aside.
        File.Move(built, aside);
        try
        {
            byte[] image = await File.ReadAllBytesAsync(aside, cancellation);
            Guid original = ReadMvid(image);
            image[MvidOffset(image)] ^= 0xFF;
            Assert.NotEqual(original, ReadMvid(image));
            await File.WriteAllBytesAsync(built, image, cancellation);

            await action();
        }
        finally
        {
            File.Delete(built);
            File.Move(aside, built);
        }
    }

    private static Guid ReadMvid(byte[] image)
    {
        using PEReader reader = new(ImmutableArray.Create(image));
        MetadataReader metadata = reader.GetMetadataReader();
        return metadata.GetGuid(metadata.GetModuleDefinition().Mvid);
    }

    /// <summary>Where the module's MVID sits in the file: the #GUID heap's start plus 16 bytes per guid before it (the index is 1-based).</summary>
    private static int MvidOffset(byte[] image)
    {
        using PEReader reader = new(ImmutableArray.Create(image));
        MetadataReader metadata = reader.GetMetadataReader();
        int index = MetadataTokens.GetHeapOffset(metadata.GetModuleDefinition().Mvid);
        return reader.PEHeaders.MetadataStartOffset + metadata.GetHeapMetadataOffset(HeapIndex.Guid) + ((index - 1) * 16);
    }
}
