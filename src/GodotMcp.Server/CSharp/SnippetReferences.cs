using System.Collections.Frozen;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using System.Xml.Linq;
using GodotMcp.Server.Session;
using Microsoft.CodeAnalysis;

namespace GodotMcp.Server.CSharp;

/// <summary>
/// What a run_csharp snippet compiles against, and what the helper checks before loading it: the dlls to reference besides the
/// framework, the simple name and MVID of each game-folder and helper dll among them, the game assembly's simple name, and the
/// game's root namespace (null when it is no namespace name a using takes).
/// </summary>
internal sealed record SnippetReferences(IReadOnlyList<string> Paths, IReadOnlyDictionary<string, string> Expect, string Game, string? RootNamespace)
{
    /// <summary>The helper's own dlls, which hold the snippet's globals class; both sit in the helper copy's folder.</summary>
    private static readonly string[] HelperFiles = ["GodotMcp.Dotnet.dll", "GodotMcp.Dotnet.Core.dll"];

    /// <summary>
    /// The assemblies the engine loads from its own GodotSharp/Api folder for every game, whatever copy the build put beside
    /// the game (the NuGet package's, another build with another MVID): the game never loads the build folder's copy, so it
    /// is referenced but never checked.
    /// </summary>
    private static readonly FrozenSet<string> EngineAssemblies = FrozenSet.Create(StringComparer.Ordinal, "GodotSharp", "GodotSharpEditor");

    /// <summary>
    /// Every managed dll of the game assembly's build folder (the game's own among them) except those named as an assembly
    /// of the runtime in <paramref name="frameworkDirectory"/>, then the helper's dlls from <paramref name="helperFolder"/>.
    /// Each of them but the engine's own (GodotSharp, GodotSharpEditor) is expected at its MVID, the helper's included, so a
    /// helper rebuilt since the game loaded it is refused like a game dll rebuilt since.
    /// </summary>
    /// <exception cref="InvalidOperationException">The project has no single C# project to read.</exception>
    /// <exception cref="IOException">A dll, the framework folder or the csproj cannot be read.</exception>
    public static SnippetReferences Find(string projectDir, string helperFolder, string frameworkDirectory)
    {
        CsprojLookup lookup = PrepScan.FindCsproj(projectDir);
        if (lookup.Kind != CsprojKind.Found)
        {
            throw new InvalidOperationException(lookup.Note ?? "This project has no C# assembly, so the C# tools cannot reach it.");
        }

        string game = lookup.AssemblyName!;
        string folder = Path.GetDirectoryName(PrepScan.AssemblyPath(projectDir, game))!;
        FrozenSet<string> framework = FrameworkNames(frameworkDirectory);
        IEnumerable<string> gameDlls = Directory.EnumerateFiles(folder, "*.dll").Order(StringComparer.Ordinal).Where(SnippetCompiler.IsManaged);
        List<string> paths = [];
        Dictionary<string, string> expect = new(StringComparer.Ordinal);
        foreach (string path in gameDlls.Concat(HelperFiles.Select(file => Path.Combine(helperFolder, file))))
        {
            if (ReadIdentity(path) is (string name, Guid mvid) && !framework.Contains(name))
            {
                paths.Add(path);
                if (!EngineAssemblies.Contains(name))
                {
                    expect[name] = mvid.ToString("D");
                }
            }
        }

        return new SnippetReferences(paths, expect, game, RootNamespaceOf(lookup.ProjectFile!, game));
    }

    /// <summary>The simple names of the runtime's assemblies, as the compiler's framework reference set holds them.</summary>
    private static FrozenSet<string> FrameworkNames(string frameworkDirectory) =>
        SnippetCompiler
            .FrameworkIn(frameworkDirectory)
            .OfType<PortableExecutableReference>()
            .Select(reference => Path.GetFileNameWithoutExtension(reference.FilePath))
            .OfType<string>()
            .ToFrozenSet(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// The assembly's simple name and its module's MVID, as the runtime reports them for the loaded file; null for a module
    /// that is no assembly.
    /// </summary>
    private static (string Name, Guid Mvid)? ReadIdentity(string path)
    {
        using FileStream stream = File.OpenRead(path);
        using PEReader reader = new(stream);
        MetadataReader metadata = reader.GetMetadataReader();
        return metadata.IsAssembly
            ? (metadata.GetString(metadata.GetAssemblyDefinition().Name), metadata.GetGuid(metadata.GetModuleDefinition().Mvid))
            : null;
    }

    /// <summary>The csproj's <c>RootNamespace</c>, else the assembly name; null when that is no namespace name.</summary>
    private static string? RootNamespaceOf(string projectFile, string assemblyName)
    {
        string? declared = XDocument
            .Load(projectFile)
            .Descendants()
            .Where(element => element.Name.LocalName == "RootNamespace")
            .Select(element => element.Value.Trim())
            .FirstOrDefault(value => value.Length > 0);
        string root = declared ?? assemblyName;
        return SnippetCompiler.IsNamespaceName(root) ? root : null;
    }
}
