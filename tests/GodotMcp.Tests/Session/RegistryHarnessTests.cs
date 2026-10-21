using Xunit.Sdk;

namespace GodotMcp.Tests.Session;

/// <summary>The harness's teardown: a step that throws still removes its folder, and the throw that reaches the test is the step's own.</summary>
public sealed class RegistryHarnessTests
{
    // HangProbeTests and RegistryHarness dispose their sessions, listener and games before the folder, with no finally: a
    // teardown step that throws (here the harness's own check that a waiting attach ended cancelled) must still delete the
    // folder, and the exception the test sees must be that step's own failure, not the folder's delete.
    [Fact]
    public async Task AFailedTeardownStillRemovesItsFolder()
    {
        RegistryHarness harness = new();
        harness.Project("alpha");
        var cancel = new CancellationTokenSource();
        harness.Waiting.Add((Task.FromException(new TimeoutException("the attach gave up")), cancel));
        try
        {
            ThrowsAnyException failed = await Assert.ThrowsAsync<ThrowsAnyException>(async () => await harness.DisposeAsync());

            Assert.Contains("OperationCanceledException", failed.Message, StringComparison.Ordinal);
            Assert.False(Directory.Exists(harness.Path), $"{harness.Path} was left after the teardown threw");
        }
        finally
        {
            harness.Sessions.Dispose();
            harness.Listener.Dispose();
            await cancel.CancelAsync();
            cancel.Dispose();
            // The sessions' own dispose writes their release records back into the folder the teardown deleted.
            RemoveFolder(harness.Path);
        }
    }

    /// <summary>Removes a folder the harness's teardown deleted and its own sessions, disposed after it, wrote into again.</summary>
    private static void RemoveFolder(string folder)
    {
        if (Directory.Exists(folder))
        {
            Directory.Delete(folder, recursive: true);
        }
    }
}
