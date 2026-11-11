using System.Diagnostics;

namespace GodotMcp.TestSupport;

/// <summary>
/// The links a test needs: a junction needs no privilege, unlike a directory symbolic link, and .NET has no API that
/// makes one.
/// </summary>
public static class Links
{
    /// <summary>
    /// Makes <paramref name="link"/> a junction to <paramref name="target"/>: <c>mklink /J</c> on Windows, and a
    /// symbolic link elsewhere.
    /// </summary>
    public static void MakeJunction(string link, string target)
    {
        if (!OperatingSystem.IsWindows())
        {
            Directory.CreateSymbolicLink(link, target);
            return;
        }

        Mklink("/J", link, target);
    }

    /// <summary>Makes <paramref name="link"/> a second name of the file <paramref name="target"/> (Windows only).</summary>
    public static void MakeHardLink(string link, string target) => Mklink("/H", link, target);

    /// <summary>Runs <c>mklink</c>, and throws with the command, its exit code and its output when it fails.</summary>
    private static void Mklink(string kind, string link, string target)
    {
        ProcessStartInfo start = new("cmd.exe")
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        foreach (string argument in new[] { "/c", "mklink", kind, link, target })
        {
            start.ArgumentList.Add(argument);
        }

        using Process mklink = Process.Start(start) ?? throw new InvalidOperationException("cmd.exe did not start");
        mklink.StandardInput.Close();
        // A line or two each way, which no pipe buffer fills, so reading one after the other cannot block.
        string output = mklink.StandardOutput.ReadToEnd() + mklink.StandardError.ReadToEnd();
        mklink.WaitForExit();
        if (mklink.ExitCode != 0)
        {
            throw new InvalidOperationException($"mklink {kind} {link} {target} failed ({mklink.ExitCode}): {output}");
        }
    }
}
