using System.Collections.Concurrent;
using System.Collections.ObjectModel;
using System.Diagnostics;
using GodotMcp.Server.Session;

namespace GodotMcp.TestSupport;

/// <summary>
/// A copy of the CsProbe fixture, a Godot C# project, in a fresh git repository with everything committed, and its
/// assembly built the way the editor builds it before a run, into the ignored <c>.godot/mono/temp/bin/Debug/</c>.
/// </summary>
public sealed class CsProbeProject : IDisposable
{
    private const string ProjectName = "CsProbe";
    private static readonly TimeSpan BuildTimeout = TimeSpan.FromMinutes(2);

    // Set by a dotnet test or build run above this one; they would point the nested build at another MSBuild.
    private static readonly string[] InheritedMsBuildVariables = ["MSBuildExtensionsPath", "MSBuildSDKsPath", "MSBUILD_EXE_PATH"];

    // One built template per distinct set of extra sources, keyed by their names and contents, built once per test process.
    private static readonly ConcurrentDictionary<string, Lazy<string>> Templates = new(StringComparer.Ordinal);
    private static readonly Lazy<TempDirectory> TemplateRoot = new(CreateTemplateRoot);
    private static int _templateCount;

    private readonly TempDirectory _temp = new();

    public CsProbeProject()
        : this(build: true) { }

    private CsProbeProject(bool build)
    {
        Directory = CommittedCopy(_temp);
        if (build)
        {
            Build(Path.Combine(Directory, ProjectName + ".csproj"));
        }
    }

    private CsProbeProject(string template, IEnumerable<string> extraSources)
    {
        Directory = CommittedCopy(_temp);
        foreach (string fileName in extraSources)
        {
            File.Copy(Path.Combine(template, fileName), SourcePath(fileName));
        }

        string assemblyFolder = Path.GetDirectoryName(PrepScan.AssemblyPath(template, ProjectName))!;
        CopyTree(assemblyFolder, Path.Combine(Directory, Path.GetRelativePath(template, assemblyFolder)));
    }

    public string Directory { get; }

    /// <summary>Whether the server's prep has run a C# build in this copy: each one saves its diagnostics.</summary>
    public bool PrepHasBuilt => File.Exists(SavedBuild.PathIn(Directory));

    /// <summary>No extra sources: the fixture as it is, built.</summary>
    public static IReadOnlyDictionary<string, string> NoExtraSources => ReadOnlyDictionary<string, string>.Empty;

    /// <summary>A copy as a fresh checkout has it: committed, with no assembly built.</summary>
    public static CsProbeProject Unbuilt() => new(build: false);

    /// <summary>
    /// A committed copy with <paramref name="extraSources"/> (file name to content) added uncommitted beside the fixture's
    /// sources, and the assembly they build with already in place, so the prep finds it up to date. Each distinct set is built
    /// once per test process into a template; every copy takes the template's sources and assembly with their times kept,
    /// never its <c>obj</c>, whose files hold the template's own paths.
    /// </summary>
    public static CsProbeProject BuiltWith(IReadOnlyDictionary<string, string> extraSources)
    {
        string key = string.Join(
            '\0',
            extraSources.OrderBy(source => source.Key, StringComparer.Ordinal).Select(source => source.Key + '\0' + source.Value)
        );
        string template = Templates.GetOrAdd(key, _ => new Lazy<string>(() => BuildTemplate(extraSources))).Value;
        return new CsProbeProject(template, extraSources.Keys);
    }

    public string SourcePath(string fileName) => Path.Combine(Directory, fileName);

    /// <summary>Replaces a source file's text, leaving the change uncommitted.</summary>
    public void WriteSource(string fileName, string content) => File.WriteAllText(SourcePath(fileName), content);

    public void Dispose() => _temp.Dispose();

    /// <summary>Copies the fixture's files into a CsProbe folder under <paramref name="temp"/>, git-inits it and commits them.</summary>
    private static string CommittedCopy(TempDirectory temp)
    {
        string directory = temp.Combine(ProjectName);
        CopyFixture(directory);
        Git.InitAndCommitAll(directory);
        return directory;
    }

    /// <summary>Copies the fixture's files into <paramref name="directory"/>, keeping their modification times.</summary>
    private static void CopyFixture(string directory)
    {
        System.IO.Directory.CreateDirectory(directory);
        foreach (string file in System.IO.Directory.EnumerateFiles(Path.Combine(RepoPaths.Root, "tests", "fixtures", ProjectName)))
        {
            File.Copy(file, Path.Combine(directory, Path.GetFileName(file)));
        }
    }

    /// <summary>Copies every file under <paramref name="source"/> to the same place under <paramref name="destination"/>, times kept.</summary>
    private static void CopyTree(string source, string destination)
    {
        foreach (string file in System.IO.Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
        {
            string target = Path.Combine(destination, Path.GetRelativePath(source, file));
            System.IO.Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Copy(file, target);
        }
    }

    /// <summary>The fixture with <paramref name="extraSources"/> written beside it and built, in a folder of its own.</summary>
    private static string BuildTemplate(IReadOnlyDictionary<string, string> extraSources)
    {
        int number = Interlocked.Increment(ref _templateCount);
        string directory = TemplateRoot.Value.Combine(number.ToString(System.Globalization.CultureInfo.InvariantCulture), ProjectName);
        CopyFixture(directory);
        foreach ((string fileName, string content) in extraSources)
        {
            File.WriteAllText(Path.Combine(directory, fileName), content);
        }

        Build(Path.Combine(directory, ProjectName + ".csproj"));
        return directory;
    }

    /// <summary>The folder the templates live in, deleted when the test process exits.</summary>
    private static TempDirectory CreateTemplateRoot()
    {
        TempDirectory root = new();
        AppDomain.CurrentDomain.ProcessExit += (_, _) => root.Dispose();
        return root;
    }

    /// <summary>
    /// Runs <c>dotnet build</c> on the project with its own stdin, closed at once, no reused MSBuild nodes and no shared
    /// compiler server, so nothing it starts outlives it; throws with the output's tail when it fails or outlives
    /// <see cref="BuildTimeout"/>.
    /// </summary>
    private static void Build(string project)
    {
        ProcessStartInfo startInfo = new("dotnet")
        {
            WorkingDirectory = Path.GetDirectoryName(project)!,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        string[] arguments =
        [
            "build",
            project,
            "-c",
            ProjectPrep.Configuration,
            "-p:GodotTargetPlatform=windows",
            "-p:UseSharedCompilation=false",
            "-nologo",
        ];
        foreach (string argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        foreach (string name in InheritedMsBuildVariables)
        {
            startInfo.Environment.Remove(name);
        }

        startInfo.Environment["MSBUILDDISABLENODEREUSE"] = "1";
        using Process process = Process.Start(startInfo)!;
        process.StandardInput.Close();
        Task<string> output = process.StandardOutput.ReadToEndAsync();
        Task<string> error = process.StandardError.ReadToEndAsync();
        if (!process.WaitForExit(BuildTimeout))
        {
            process.Kill(entireProcessTree: true);
            process.WaitForExit();
            throw new TimeoutException($"dotnet build {project} did not finish within {BuildTimeout.TotalSeconds} s:\n{Tail(output, error)}");
        }

        process.WaitForExit();
        if (process.ExitCode != 0)
        {
            throw new InvalidOperationException($"dotnet build {project} exited {process.ExitCode}:\n{Tail(output, error)}");
        }
    }

    /// <summary>The last 40 lines of the build's stdout and stderr, once both have ended.</summary>
    private static string Tail(Task<string> output, Task<string> error) => string.Join('\n', (output.Result + error.Result).Split('\n').TakeLast(40));
}
