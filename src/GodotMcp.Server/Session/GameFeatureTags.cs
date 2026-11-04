using System.Collections.Frozen;
using System.Runtime.InteropServices;

namespace GodotMcp.Server.Session;

/// <summary>
/// The feature tags a game the server launches has, as <c>OS::has_feature</c> answers them for a game run of the editor build
/// (4.7.2 <c>core/os/os.cpp</c> L471-645): <c>editor</c> and <c>editor_runtime</c> (a tools build not in the editor),
/// <c>debug</c>, <c>single</c>, <c>64</c>, <c>threads</c>, the CPU's, and the platform's own (<c>pc</c> and
/// <c>system_fonts</c>, <c>platform/windows/os_windows.cpp</c> L2533-2542 and <c>platform/linuxbsd/os_linuxbsd.cpp</c>
/// L583-605; the platform's identifier, and on Linux its lowercased name), plus <c>movie</c> while the run writes a movie
/// (<c>main/main.cpp</c> L1968). Every other tag is absent: <c>editor_hint</c>, <c>embedded_in_editor</c>, the template
/// and release tags, <c>double</c>, <c>32</c>, <c>nothreads</c>, other platforms', the renderer's texture-format tags and a
/// project's custom export features. Tags are compared ordinally, as Godot's are lowercase.
/// </summary>
internal static class GameFeatureTags
{
    private const string MovieTag = "movie";

    private static readonly FrozenSet<string> Present = new[] { "editor", "editor_runtime", "debug", "64", "single", "threads", "pc", "system_fonts" }
        .Concat(CpuTags(RuntimeInformation.ProcessArchitecture))
        .Concat(PlatformTags())
        .ToFrozenSet(StringComparer.Ordinal);

    /// <summary>Whether the game has <paramref name="tag"/>; <c>movie</c> only when <paramref name="recording"/>.</summary>
    public static bool Has(string tag, bool recording) => string.Equals(tag, MovieTag, StringComparison.Ordinal) ? recording : Present.Contains(tag);

    /// <summary>The CPU's tags (<c>os.cpp</c> L529-574): x86_64 and x86 on x64, arm64 and arm on Arm64, none otherwise.</summary>
    private static string[] CpuTags(Architecture architecture) =>
        architecture switch
        {
            Architecture.X64 => ["x86_64", "x86"],
            Architecture.Arm64 => ["arm64", "arm"],
            _ => [],
        };

    /// <summary>The platform's tags: <c>windows</c> on Windows, <c>linuxbsd</c> and <c>linux</c> on Linux, none otherwise.</summary>
    private static string[] PlatformTags() =>
        OperatingSystem.IsWindows() ? ["windows"]
        : OperatingSystem.IsLinux() ? ["linuxbsd", "linux"]
        : [];
}
