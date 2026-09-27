using System.Runtime.Loader;
using System.Text.Json;
using GodotMcp.Server.CSharp;
using GodotMcp.TestSupport;

namespace GodotMcp.Tests.CSharp;

/// <summary>What a snippet compiles against, found in a project folder laid out as Godot builds it; no Godot runs here.</summary>
public sealed class SnippetReferencesTests : IDisposable
{
    private readonly TempDirectory _temp = new();
    private readonly string _output;
    private readonly string _helper;

    public SnippetReferencesTests()
    {
        _output = _temp.Combine(".godot", "mono", "temp", "bin", "Debug");
        _helper = _temp.Combine("helper-copy", "helper");
        Directory.CreateDirectory(_output);
        Directory.CreateDirectory(_helper);
        File.WriteAllText(_temp.Combine("project.godot"), "config_version=5\n");
        File.Copy(typeof(SnippetReferencesTests).Assembly.Location, Path.Combine(_output, "Game.dll"));
        File.Copy(typeof(TempDirectory).Assembly.Location, Path.Combine(_output, "Sibling.dll"));
        File.Copy(typeof(JsonSerializer).Assembly.Location, Path.Combine(_output, "System.Text.Json.dll"));
        File.WriteAllText(Path.Combine(_output, "native.dll"), "not a portable executable");
        // The build copies the engine's API beside the game; the game loads the engine's own copy instead.
        SnippetCompilerTests.BuildLibrary(Path.Combine(_output, "GodotSharp.dll"), "GodotSharp", "namespace Godot; public class Node { }");
        SnippetCompilerTests.BuildLibrary(HelperDll, "GodotMcp.Dotnet", "namespace GodotMcp.Dotnet; public abstract class SnippetGlobals { }");
        SnippetCompilerTests.BuildLibrary(HelperCoreDll, "GodotMcp.Dotnet.Core", "namespace GodotMcp.Dotnet.Core; public static class Marker { }");
    }

    public void Dispose() => _temp.Dispose();

    private string HelperDll => Path.Combine(_helper, "GodotMcp.Dotnet.dll");

    private string HelperCoreDll => Path.Combine(_helper, "GodotMcp.Dotnet.Core.dll");

    [Fact]
    public void TheGameFolderAndTheHelperAreReferencedAndAFrameworkNameIsDropped()
    {
        WriteCsproj(rootNamespace: null);

        SnippetReferences references = Find();

        Assert.Equal(
            [
                Path.Combine(_output, "Game.dll"),
                Path.Combine(_output, "GodotSharp.dll"),
                Path.Combine(_output, "Sibling.dll"),
                HelperDll,
                HelperCoreDll,
            ],
            references.Paths
        );
        Assert.Equal("Game", references.Game);
    }

    // GodotSharp is referenced but not expected: the game loads the engine's own copy, never the build folder's. The helper's
    // own dlls are expected, so a helper rebuilt since the game loaded it is refused as an older build.
    [Fact]
    public void EachKeptDllCarriesTheMvidItLoadsWith()
    {
        WriteCsproj(rootNamespace: null);

        SnippetReferences references = Find();

        Assert.Equal(
            new Dictionary<string, string>
            {
                [typeof(SnippetReferencesTests).Assembly.GetName().Name!] = Mvid(typeof(SnippetReferencesTests)),
                [typeof(TempDirectory).Assembly.GetName().Name!] = Mvid(typeof(TempDirectory)),
                ["GodotMcp.Dotnet"] = LoadedMvid(HelperDll),
                ["GodotMcp.Dotnet.Core"] = LoadedMvid(HelperCoreDll),
            },
            references.Expect
        );
    }

    [Fact]
    public void TheRootNamespaceComesFromTheCsproj()
    {
        WriteCsproj(rootNamespace: "Game.Root");

        Assert.Equal("Game.Root", Find().RootNamespace);
    }

    [Fact]
    public void WithoutARootNamespaceTheAssemblyNameIsTheRoot()
    {
        WriteCsproj(rootNamespace: null);

        Assert.Equal("Game", Find().RootNamespace);
    }

    private SnippetReferences Find() => SnippetReferences.Find(_temp.Path, _helper, SnippetCompilerTests.ServerFramework);

    private static string Mvid(Type type) => type.Assembly.ManifestModule.ModuleVersionId.ToString("D");

    /// <summary>The MVID the runtime reports for the dll at <paramref name="path"/>, loaded from a stream so no file stays locked.</summary>
    private static string LoadedMvid(string path)
    {
        AssemblyLoadContext context = new("mvid-check", isCollectible: true);
        try
        {
            using FileStream stream = File.OpenRead(path);
            return context.LoadFromStream(stream).ManifestModule.ModuleVersionId.ToString("D");
        }
        finally
        {
            context.Unload();
        }
    }

    private void WriteCsproj(string? rootNamespace)
    {
        string property = rootNamespace is null
            ? string.Empty
            : $"\n  <PropertyGroup>\n    <RootNamespace>{rootNamespace}</RootNamespace>\n  </PropertyGroup>";
        File.WriteAllText(_temp.Combine("Game.csproj"), $"<Project Sdk=\"Godot.NET.Sdk/4.7.2\">{property}\n</Project>\n");
    }
}
