using System.Collections.Concurrent;
using System.Globalization;

namespace GodotMcp.TestSupport;

/// <summary>
/// A fixture folder copied as a git repository with everything committed. Each fixture's repository is created once per
/// test process in a private template folder, and every copy takes that template's files and its <c>.git</c> along, so a
/// copy costs a file copy instead of a <c>git init</c> and a commit. Nothing may ever run a server or Godot in a
/// template: the server writes into the repository (<c>.git/info/exclude</c>, <c>override.cfg</c>) and the template has
/// to stay as it was committed.
/// </summary>
public static class CommittedFixture
{
    // One committed template per fixture folder, created once per test process.
    private static readonly ConcurrentDictionary<string, Lazy<string>> Templates = new(StringComparer.Ordinal);
    private static readonly Lazy<TempDirectory> TemplateRoot = new(CreateTemplateRoot);
    private static int _templateCount;

    /// <summary>
    /// Copies <paramref name="fixtureDir"/>'s top-level files into <paramref name="destination"/> together with a git
    /// repository that has them committed, creating the destination folder when it does not exist.
    /// </summary>
    public static void CopyTo(string fixtureDir, string destination)
    {
        string template = Templates.GetOrAdd(Path.GetFullPath(fixtureDir), path => new Lazy<string>(() => CreateTemplate(path))).Value;
        CopyTopLevelFiles(template, destination);
        CopyTree(Path.Combine(template, ".git"), Path.Combine(destination, ".git"));
    }

    /// <summary>Copies the fixture's top-level files into a fresh template folder, git-inits it and commits them.</summary>
    private static string CreateTemplate(string fixtureDir)
    {
        string template = TemplateRoot.Value.Combine(Interlocked.Increment(ref _templateCount).ToString(CultureInfo.InvariantCulture));
        CopyTopLevelFiles(fixtureDir, template);
        Git.InitAndCommitAll(template);
        return template;
    }

    /// <summary>The folder the templates live in, deleted when the test process exits.</summary>
    private static TempDirectory CreateTemplateRoot()
    {
        TempDirectory root = new();
        AppDomain.CurrentDomain.ProcessExit += (_, _) => root.Dispose();
        return root;
    }

    /// <summary>Copies every file directly under <paramref name="source"/> into <paramref name="destination"/>, times kept.</summary>
    private static void CopyTopLevelFiles(string source, string destination)
    {
        Directory.CreateDirectory(destination);
        foreach (string file in Directory.EnumerateFiles(source))
        {
            File.Copy(file, Path.Combine(destination, Path.GetFileName(file)));
        }
    }

    /// <summary>Copies every file under <paramref name="source"/> to the same place under <paramref name="destination"/>, times kept.</summary>
    private static void CopyTree(string source, string destination)
    {
        foreach (string file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
        {
            string target = Path.Combine(destination, Path.GetRelativePath(source, file));
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Copy(file, target);
        }
    }
}
