using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace GodotMcp.TestSupport;

/// <summary>
/// The test's own debugger on a child process, holding the child's exit once it is killed until <see cref="Release"/>: Windows
/// signals a debugged process only after its debugger continues its exit events, so the child stays not gone exactly as long
/// as the test wants, with no clock involved. Every thread of the child is frozen while an event is held.
/// </summary>
public sealed partial class ExitHold : IDisposable
{
    private const int ExceptionEvent = 1;
    private const int CreateProcessEvent = 3;
    private const int ExitThreadEvent = 4;
    private const int ExitProcessEvent = 5;
    private const int LoadDllEvent = 6;
    private const uint Continue = 0x00010002;
    private const uint NotHandled = 0x80010001;
    private const uint Breakpoint = 0x80000003;
    private readonly int _processId;
    private readonly TimeSpan _hangGuard;
    private readonly Action? _onFirstEvent;
    private readonly TaskCompletionSource _attached = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource _killed = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly ManualResetEventSlim _release = new();
    private readonly Thread _loop;
    private int _breakInThread = -1;
    private volatile bool _disposed;

    private ExitHold(int processId, TimeSpan hangGuard, Action? onFirstEvent)
    {
        _processId = processId;
        _hangGuard = hangGuard;
        _onFirstEvent = onFirstEvent;
        // The debug API answers only the thread that attached, so the loop owns one thread for the hold's life.
        _loop = new Thread(Run) { IsBackground = true, Name = $"exit hold of {processId}" };
        _loop.Start();
    }

    /// <summary>
    /// Completes once the child's first exit event after the attach, other than the break-in thread's, is being held: its kill's,
    /// or one of an idle thread that ended on its own before it.
    /// </summary>
    public Task Killed => _killed.Task;

    /// <summary>
    /// Attaches the test's debugger to <paramref name="child"/>, returning once its break-in has been answered; a hold whose attach
    /// fails or outlasts <paramref name="hangGuard"/> is disposed, letting the child go. The same guard bounds
    /// <see cref="Dispose"/>'s wait for the child's exit once its exit was held. <paramref name="onFirstEvent"/> runs once on the
    /// debug loop's thread while the debugger holds the child's first event, before that event is continued, so a child killed
    /// there can never raise the break-in: the seam a test uses to end the child before the attach completes.
    /// </summary>
    public static async Task<ExitHold> AttachAsync(Process child, TimeSpan hangGuard, Action? onFirstEvent = null)
    {
        ExitHold hold = new(child.Id, hangGuard, onFirstEvent);
        try
        {
            await hold._attached.Task.WaitAsync(hangGuard);
            return hold;
        }
        catch
        {
            hold.Dispose();
            throw;
        }
    }

    /// <summary>Lets the held exit go on, so the child is gone once Windows has closed it.</summary>
    public void Release() => _release.Set();

    /// <summary>
    /// Lets the held exit go and ends the hold. Once an exit was held, the debug loop runs until the child's process exit,
    /// waited for at most the hang guard.
    /// </summary>
    /// <exception cref="InvalidOperationException">The child never exited within the hang guard, so its hold could not end.</exception>
    public void Dispose()
    {
        _disposed = true;
        _release.Set();
        if (!_loop.Join(_hangGuard))
        {
            throw new InvalidOperationException($"the exit hold of pid {_processId} never saw its process exit");
        }

        _release.Dispose();
    }

    private void Run()
    {
        if (!DebugActiveProcess(_processId))
        {
            _attached.SetException(new Win32Exception());
            return;
        }

        DebugSetProcessKillOnExit(false);
        nint debugEvent = Marshal.AllocHGlobal(256);
        try
        {
            while (!_disposed || _killed.Task.IsCompleted)
            {
                if (WaitForDebugEvent(debugEvent, 100) && Handle(debugEvent))
                {
                    return;
                }
            }

            DebugActiveProcessStop(_processId);
        }
        finally
        {
            // Any other way the loop stops short of the break-in would leave the attach waiting out its whole guard.
            _attached.TrySetException(new InvalidOperationException("the debug loop ended before the debugger's break-in"));
            Marshal.FreeHGlobal(debugEvent);
        }
    }

    /// <summary>Answers one debug event (DEBUG_EVENT on x64: code at 0, thread at 8, its union at 16); true once the child has exited.</summary>
    private bool Handle(nint debugEvent)
    {
        int code = Marshal.ReadInt32(debugEvent, 0);
        int threadId = Marshal.ReadInt32(debugEvent, 8);
        uint status = Continue;
        if (code is CreateProcessEvent or LoadDllEvent && Marshal.ReadIntPtr(debugEvent, 16) is var file && file != 0)
        {
            CloseHandle(file);
        }
        else if (code == ExceptionEvent)
        {
            status = OnException((uint)Marshal.ReadInt32(debugEvent, 16), threadId);
        }
        else if (IsHeldExit(code, threadId) && _killed.TrySetResult())
        {
            _release.Wait();
        }
        else
        {
            FailAttachOnExit(code, debugEvent);
        }

        if (code == CreateProcessEvent)
        {
            FirstEvent();
        }

        ContinueDebugEvent(_processId, threadId, status);
        return code == ExitProcessEvent;
    }

    /// <summary>
    /// Fails the attach on a process exit it did not hold: a child that ended before its break-in has nothing left to raise it, so
    /// the attach fails with how it ended rather than waiting out its whole guard. A no-op once the attach has completed.
    /// </summary>
    private void FailAttachOnExit(int code, nint debugEvent)
    {
        if (code != ExitProcessEvent)
        {
            return;
        }

        _attached.TrySetException(
            new InvalidOperationException(
                $"pid {_processId} exited (code 0x{(uint)Marshal.ReadInt32(debugEvent, 16):X8}) before the debugger's break-in"
            )
        );
    }

    /// <summary>
    /// Runs the attach's <c>onFirstEvent</c> once, on the loop's own thread, while the child's first event is held and before it is
    /// continued: the break-in cannot be raised until that event is answered.
    /// </summary>
    private void FirstEvent()
    {
        try
        {
            _onFirstEvent?.Invoke();
        }
        catch (Exception e)
        {
            // The loop thread has no catch of its own, so a throwing seam would end the test host; the awaiting test gets it instead.
            _attached.TrySetException(e);
        }
    }

    /// <summary>
    /// Whether an event is an exit the hold keeps: only once the attach's break-in has been answered, so an exit before it never
    /// blocks the loop ahead of <see cref="AttachAsync"/>, and never the break-in thread's own.
    /// </summary>
    private bool IsHeldExit(int code, int threadId) =>
        code is ExitThreadEvent or ExitProcessEvent && _breakInThread >= 0 && threadId != _breakInThread;

    private uint OnException(uint exceptionCode, int threadId)
    {
        if (_breakInThread < 0 && exceptionCode == Breakpoint)
        {
            _breakInThread = threadId;
            _attached.TrySetResult();
            return Continue;
        }

        return NotHandled;
    }

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool DebugActiveProcess(int processId);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool DebugActiveProcessStop(int processId);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool DebugSetProcessKillOnExit([MarshalAs(UnmanagedType.Bool)] bool killOnExit);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool WaitForDebugEvent(nint debugEvent, int milliseconds);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool ContinueDebugEvent(int processId, int threadId, uint continueStatus);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool CloseHandle(nint handle);
}
