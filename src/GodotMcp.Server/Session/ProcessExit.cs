using System.Diagnostics;
using Microsoft.Win32.SafeHandles;

namespace GodotMcp.Server.Session;

/// <summary>Waiting for a process to be gone rather than merely exited.</summary>
internal static class ProcessExit
{
    /// <summary>How long past a deadline's backstop the wait's own thread blocks, so the deadline always ends the wait first.</summary>
    private static readonly TimeSpan BackstopMargin = TimeSpan.FromSeconds(1);

    /// <summary>Waits until Windows signals the process, which it does only after closing the process's handles (its current
    /// directory among them); Process.HasExited and WaitForExitAsync return as soon as the exit code is set, tens of ms
    /// earlier. True when the process is gone within the timeout.</summary>
    public static Task<bool> WaitUntilGoneAsync(Process process, TimeSpan timeout) => Task.Run(() => process.WaitForExit(timeout));

    /// <summary>
    /// Waits, as <see cref="WaitUntilGoneAsync(Process, TimeSpan)"/> does, until the process is gone or
    /// <paramref name="deadline"/> expires, at its load-adjusted budget or its backstop.
    /// </summary>
    /// <returns>True when the process is gone before the deadline expires.</returns>
    public static Task<bool> WaitUntilGoneAsync(Process process, LoadDeadline deadline) =>
        GoneWithinAsync(WaitUntilGoneAsync(process, deadline.Backstop + BackstopMargin), deadline);

    /// <summary>
    /// Waits until Windows signals every process of <paramref name="processes"/>, or <paramref name="deadline"/> expires, with
    /// no thread blocked per process: each handle's signal is a thread-pool wait. The caller keeps the handles open through the
    /// wait and disposes them after it.
    /// </summary>
    /// <returns>The pids of the processes still not gone when the deadline expired; empty when all are gone.</returns>
    public static async Task<IReadOnlyList<int>> WaitUntilGoneAsync(
        IReadOnlyList<(int Pid, SafeProcessHandle Handle)> processes,
        LoadDeadline deadline
    )
    {
        List<Signal> signals = new(processes.Count);
        try
        {
            // Built one at a time, so a registration that throws leaves the ones before it to the finally below.
            foreach ((_, SafeProcessHandle handle) in processes)
            {
                signals.Add(new Signal(handle));
            }

            try
            {
                await Task.WhenAll(signals.Select(signal => signal.Gone)).WaitAsync(deadline.Token);
            }
            catch (OperationCanceledException) when (deadline.Expired)
            {
                // The deadline ended the wait; the processes not yet signalled are reported below.
            }

            return [.. processes.Where((_, index) => !signals[index].Gone.IsCompleted).Select(process => process.Pid)];
        }
        finally
        {
            foreach (Signal signal in signals)
            {
                signal.Dispose();
            }
        }
    }

    /// <summary>Whether <paramref name="gone"/> reports the process gone before <paramref name="deadline"/> expires.</summary>
    /// <param name="gone">Completes with true once the process is gone, or false when its own wait gave up.</param>
    /// <param name="deadline">The load-adjusted wait.</param>
    internal static async Task<bool> GoneWithinAsync(Task<bool> gone, LoadDeadline deadline)
    {
        try
        {
            return await gone.WaitAsync(deadline.Token);
        }
        catch (OperationCanceledException) when (deadline.Expired)
        {
            return false;
        }
    }

    /// <summary>
    /// One process handle's signal as a task, completed by a thread-pool wait once Windows signals the process. It holds a
    /// reference on the handle from its start until the wait is unregistered, so the handle cannot be closed under a live wait.
    /// </summary>
    private sealed class Signal : IDisposable
    {
        private readonly TaskCompletionSource _gone = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly SafeProcessHandle _process;
        private readonly ProcessWaitHandle _handle;
        private readonly RegisteredWaitHandle _registration;

        public Signal(SafeProcessHandle process)
        {
            bool added = false;
            process.DangerousAddRef(ref added);
            _process = process;
            _handle = new ProcessWaitHandle(process);
            try
            {
                _registration = ThreadPool.RegisterWaitForSingleObject(
                    _handle,
                    (_, _) => _gone.TrySetResult(),
                    null,
                    Timeout.Infinite,
                    executeOnlyOnce: true
                );
            }
            catch
            {
                _handle.Dispose();
                process.DangerousRelease();
                throw;
            }
        }

        public Task Gone => _gone.Task;

        public void Dispose()
        {
            // Unregistering with an event returns once the wait is removed and its callback has run, if it was going to.
            using (ManualResetEvent unregistered = new(false))
            {
                if (_registration.Unregister(unregistered))
                {
                    unregistered.WaitOne();
                }
            }

            _handle.Dispose();
            _process.DangerousRelease();
        }
    }

    /// <summary>A process handle as a <see cref="WaitHandle"/>, borrowed: disposing it leaves the process handle open.</summary>
    private sealed class ProcessWaitHandle : WaitHandle
    {
        public ProcessWaitHandle(SafeProcessHandle process) => SafeWaitHandle = new SafeWaitHandle(process.DangerousGetHandle(), ownsHandle: false);
    }
}
