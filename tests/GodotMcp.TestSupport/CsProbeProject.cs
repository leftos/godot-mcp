using System.Diagnostics;

namespace GodotMcp.TestSupport;

/// <summary>
/// A copy of the CsProbe fixture, a Godot C# project, in a fresh git repository with everything committed, and its
/// assembly built the way the editor builds it before a run, into the ignored <c>.godot/mono/temp/bin/Debug/</c>.
/// </summary>
public sealed class CsProbeProject : IDisposable
{
    private static readonly TimeSpan BuildTimeout = TimeSpan.FromMinutes(2);

    // Set by a dotnet test or build run above this one; they would point the nested build at another MSBuild.
    private static readonly string[] InheritedMsBuildVariables = ["MSBuildExtensionsPath", "MSBuildSDKsPath", "MSBUILD_EXE_PATH"];
    private readonly TempDirectory _temp = new();

    public CsProbeProject()
        : this(build: true) { }

    private CsProbeProject(bool build)
    {
        Directory = _temp.Combine("CsProbe");
        System.IO.Directory.CreateDirectory(Directory);
        foreach (string file in System.IO.Directory.EnumerateFiles(Path.Combine(RepoPaths.Root, "tests", "fixtures", "CsProbe")))
        {
            File.Copy(file, Path.Combine(Directory, Path.GetFileName(file)));
        }

        Git.InitAndCommitAll(Directory);
        if (build)
        {
            Build(Path.Combine(Directory, "CsProbe.csproj"));
        }
    }

    public string Directory { get; }

    /// <summary>A copy as a fresh checkout has it: committed, with no assembly built.</summary>
    public static CsProbeProject Unbuilt() => new(build: false);

    public string SourcePath(string fileName) => Path.Combine(Directory, fileName);

    /// <summary>Replaces a source file's text, leaving the change uncommitted.</summary>
    public void WriteSource(string fileName, string content) => File.WriteAllText(SourcePath(fileName), content);

    public void Dispose() => _temp.Dispose();

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
        string[] arguments = ["build", project, "-c", "Debug", "-p:GodotTargetPlatform=windows", "-p:UseSharedCompilation=false", "-nologo"];
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
