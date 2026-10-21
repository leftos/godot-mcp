using System.Diagnostics;
using GodotMcp.Server.Session;
using GodotMcp.TestSupport;

namespace GodotMcp.Tests.TestSupport;

public sealed class TempDirectoryTests
{
    [Fact]
    public void DisposeRetriesAFileHeldBriefly()
    {
        TempDirectory temp = new();
        string held = temp.Combine("held.txt");
        File.WriteAllText(held, "held");

        var handle = new FileStream(held, FileMode.Open, FileAccess.Read, FileShare.None);
        // The hold is released the moment the first delete attempt is refused, so the retry is proven whatever the
        // machine is doing: a release scheduled on the thread pool can run after Dispose's budget has run out.
        temp.AfterRefusedAttempt = handle.Dispose;
        try
        {
            temp.Dispose();
        }
        finally
        {
            handle.Dispose();
        }

        Assert.False(Directory.Exists(temp.Path));
    }

    [Fact]
    public void DisposeGivesUpOnAFileHeldPastTheBudget()
    {
        TempDirectory temp = new();
        string held = temp.Combine("held.txt");
        File.WriteAllText(held, "held");

        var handle = new FileStream(held, FileMode.Open, FileAccess.Read, FileShare.None);
        try
        {
            var clock = Stopwatch.StartNew();
            Assert.Throws<IOException>(temp.Dispose);
            clock.Stop();

            Assert.True(clock.ElapsedMilliseconds >= 2000, $"Dispose gave up after {clock.ElapsedMilliseconds} ms");
        }
        finally
        {
            handle.Dispose();
            temp.Dispose();
        }
    }

    // A test process killed by its gate, or a teardown that throws before its delete, leaves its folder behind; the folder
    // carries a marker naming its owner, and a sweep takes the ones whose owner has exited and leaves the live ones alone.

    [Fact]
    public void ASweepTakesTheFolderOfAnOwnerThatHasExited()
    {
        using var sandbox = new TempDirectory();
        string leftover = Plant(sandbox.Path, ExitedOwner().ToString());
        try
        {
            TempDirectory.SweepStale(sandbox.Path);

            Assert.False(Directory.Exists(leftover), $"{leftover}, whose owner has exited, was left");
            Assert.False(File.Exists(leftover + Owner), "the exited owner's marker was left");
        }
        finally
        {
            Remove(leftover);
        }
    }

    [Fact]
    public void ASweepKeepsALiveOwnersFolder()
    {
        using var sandbox = new TempDirectory();
        string live = Plant(sandbox.Path, OverrideOwner.Current.ToString());
        try
        {
            TempDirectory.SweepStale(sandbox.Path);

            Assert.True(Directory.Exists(live), "a live owner's folder was taken");
            Assert.True(File.Exists(live + Owner), "a live owner's marker was taken");
        }
        finally
        {
            Remove(live);
        }
    }

    [Fact]
    public void ASweepKeepsAnUnmarkedFolderUnderADayOld()
    {
        using var sandbox = new TempDirectory();
        string young = PlantUnmarked(sandbox.Path, DateTime.UtcNow);
        try
        {
            TempDirectory.SweepStale(sandbox.Path);

            Assert.True(Directory.Exists(young), "an unmarked folder younger than a day was taken");
        }
        finally
        {
            Remove(young);
        }
    }

    [Fact]
    public void ASweepTakesAnUnmarkedFolderOverADayOld()
    {
        using var sandbox = new TempDirectory();
        string old = PlantUnmarked(sandbox.Path, DateTime.UtcNow.AddHours(-25));
        try
        {
            TempDirectory.SweepStale(sandbox.Path);

            Assert.False(Directory.Exists(old), "an unmarked folder older than a day was left");
        }
        finally
        {
            Remove(old);
        }
    }

    [Fact]
    public void ASweepKeepsAFolderWhoseMarkerItCannotRead()
    {
        using var sandbox = new TempDirectory();
        string folder = PlantUnmarked(sandbox.Path, DateTime.UtcNow.AddHours(-25));
        File.WriteAllText(folder + Owner, "not an owner");
        try
        {
            TempDirectory.SweepStale(sandbox.Path);

            Assert.True(Directory.Exists(folder), "a folder whose marker it cannot read was taken");
            Assert.True(File.Exists(folder + Owner), "a marker it cannot read was removed");
        }
        finally
        {
            Remove(folder);
        }
    }

    [Fact]
    public void ASweepLeavesEntriesThatAreNotFolders()
    {
        using var sandbox = new TempDirectory();
        string importLog = Path.Combine(sandbox.Path, "import-1.log");
        string runLog = Path.Combine(sandbox.Path, "run-1.log");
        string cache = Path.Combine(sandbox.Path, "unused-dotnet-cache");
        string other = Path.Combine(sandbox.Path, "not-a-guid");
        File.WriteAllText(importLog, "log");
        File.WriteAllText(runLog, "log");
        Directory.CreateDirectory(cache);
        Directory.CreateDirectory(other);
        Directory.SetCreationTimeUtc(cache, DateTime.UtcNow.AddHours(-25));
        Directory.SetCreationTimeUtc(other, DateTime.UtcNow.AddHours(-25));
        try
        {
            TempDirectory.SweepStale(sandbox.Path);

            Assert.True(File.Exists(importLog), "an import log was removed");
            Assert.True(File.Exists(runLog), "a run log was removed");
            Assert.True(Directory.Exists(cache), "the dotnet cache folder was removed");
            Assert.True(Directory.Exists(other), "a folder that is not a temp directory's was removed");
        }
        finally
        {
            Remove(cache);
            Remove(other);
            File.Delete(importLog);
            File.Delete(runLog);
        }
    }

    [Fact]
    public void ASweepRemovesTheMarkerOfAnExitedOwnerWhoseFolderIsGone()
    {
        using var sandbox = new TempDirectory();
        string marker = Path.Combine(sandbox.Path, Guid.NewGuid().ToString("N") + Owner);
        File.WriteAllText(marker, ExitedOwner().ToString());

        TempDirectory.SweepStale(sandbox.Path);

        Assert.False(File.Exists(marker), "the marker of an exited owner whose folder is gone was left");
    }

    [Fact]
    public void ASweepSkipsAFolderItCannotDelete()
    {
        using var sandbox = new TempDirectory();
        string folder = Plant(sandbox.Path, ExitedOwner().ToString());
        string held = Path.Combine(folder, "held.txt");
        File.WriteAllText(held, "held");
        try
        {
            using (new FileStream(held, FileMode.Open, FileAccess.Read, FileShare.None))
            {
                TempDirectory.SweepStale(sandbox.Path);

                Assert.True(Directory.Exists(folder), "a folder it could not delete was taken");
                Assert.True(File.Exists(folder + Owner), "the marker of a folder it could not delete was removed");
            }

            TempDirectory.SweepStale(sandbox.Path);

            Assert.False(Directory.Exists(folder), "the folder stayed after its holder released it");
            Assert.False(File.Exists(folder + Owner), "the marker stayed after its folder was taken");
        }
        finally
        {
            Remove(folder);
        }
    }

    [Fact]
    public void AFolderWritesItsOwnerMarkerAndRemovesItOnDispose()
    {
        var temp = new TempDirectory();
        string marker = temp.Path + Owner;
        try
        {
            Assert.True(File.Exists(marker), "no owner marker was written");
            Assert.Equal(OverrideOwner.Current.ToString(), File.ReadAllText(marker));
        }
        finally
        {
            temp.Dispose();
        }

        Assert.False(Directory.Exists(temp.Path), "the folder stayed after its dispose");
        Assert.False(File.Exists(marker), "the owner marker stayed after its dispose");
    }

    [Fact]
    public void ASweepGoesOnPastAnEntryItCannotRead()
    {
        using var sandbox = new TempDirectory();
        string unreadable = PlantUnmarked(sandbox.Path, DateTime.UtcNow);
        File.WriteAllText(unreadable + Owner, ExitedOwner().ToString());
        string leftover = Plant(sandbox.Path, ExitedOwner().ToString());
        try
        {
            using (new FileStream(unreadable + Owner, FileMode.Open, FileAccess.Read, FileShare.None))
            {
                TempDirectory.SweepStale(sandbox.Path);

                Assert.False(Directory.Exists(leftover), $"{leftover}, whose owner has exited, was left after an entry the sweep could not read");
                Assert.True(Directory.Exists(unreadable), "a folder whose marker it could not read was taken");
            }
        }
        finally
        {
            Remove(unreadable);
            Remove(leftover);
        }
    }

    [Fact]
    public void AFolderWhoseMarkerIsHeldStillDisposes()
    {
        var temp = new TempDirectory();
        string marker = temp.Path + Owner;
        using (new FileStream(marker, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            temp.Dispose();
        }

        Assert.False(Directory.Exists(temp.Path), "the folder stayed after its dispose");
        Assert.True(File.Exists(marker), "a marker held past the dispose was taken anyway");

        File.Delete(marker);
    }

    /// <summary>The suffix of the file beside a folder that names the process which made it.</summary>
    private const string Owner = ".owner";

    /// <summary>Plants a folder shaped as a killed run leaves one: a fixture copy with its git files, read-only as git writes them.</summary>
    private static string Plant(string root, string owner)
    {
        string folder = Path.Combine(root, Guid.NewGuid().ToString("N"));
        string objects = Path.Combine(folder, "InputProbe", ".git", "objects", "0f");
        Directory.CreateDirectory(objects);
        File.WriteAllText(Path.Combine(folder, "InputProbe", "project.godot"), "config_version=5\n");
        string blob = Path.Combine(objects, "10302583d4e9e89896975364ac6949dcdabeac");
        File.WriteAllText(blob, "x");
        File.SetAttributes(blob, FileAttributes.ReadOnly);
        File.WriteAllText(folder + Owner, owner);
        return folder;
    }

    /// <summary>Plants a folder with no marker, created at <paramref name="createdUtc"/>, as a build before the marker left it.</summary>
    private static string PlantUnmarked(string root, DateTime createdUtc)
    {
        string folder = Path.Combine(root, Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        File.WriteAllText(Path.Combine(folder, "project.godot"), "config_version=5\n");
        Directory.SetCreationTimeUtc(folder, createdUtc);
        return folder;
    }

    /// <summary>Removes a planted folder and its marker, read-only files included, so a red run leaves none behind.</summary>
    private static void Remove(string folder)
    {
        if (Directory.Exists(folder))
        {
            foreach (string file in Directory.EnumerateFiles(folder, "*", SearchOption.AllDirectories))
            {
                File.SetAttributes(file, FileAttributes.Normal);
            }

            Directory.Delete(folder, recursive: true);
        }

        File.Delete(folder + Owner);
    }

    /// <summary>The identity of a process that ran and exited: its id and start time, as a marker records them.</summary>
    private static OverrideOwner ExitedOwner()
    {
        using Process process = Process.Start(new ProcessStartInfo("cmd.exe", "/c exit 0") { UseShellExecute = false, CreateNoWindow = true })!;
        long started = process.StartTime.ToUniversalTime().Ticks;
        process.WaitForExit();
        return new OverrideOwner(process.Id, started);
    }
}
