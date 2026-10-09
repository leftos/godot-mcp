using System.ComponentModel;
using System.Diagnostics;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;

namespace GodotMcp.Server.Session;

/// <summary>How the server starts child processes without handing one child's pipe ends to another.</summary>
internal static class ChildProcesses
{
    /// <summary>
    /// Held while a quiet run's pipe ends are created and made inheritable for its <c>CreateProcessW</c>, and around every
    /// <see cref="Process.Start()"/> in the server: a child started meanwhile on another thread would inherit the game's pipe
    /// ends and hold them open, so the game's output would not end when the game does.
    /// </summary>
    public static readonly Lock StartLock = new();

    /// <summary>Starts <paramref name="process"/> under <see cref="StartLock"/> and adds it to the server's <see cref="OwnWork"/>.</summary>
    public static void Start(Process process)
    {
        lock (StartLock)
        {
            process.Start();
        }

        OwnWork.Adopt(process);
    }
}

/// <summary>
/// A quiet run's process, started with <c>CreateProcessW</c> on the <see cref="HiddenDesktop"/>, which
/// <see cref="System.Diagnostics.Process.Start()"/> cannot choose. It is created suspended and watched through
/// <see cref="System.Diagnostics.Process.GetProcessById(int)"/> before <see cref="Start"/> lets it run, so it cannot exit
/// unwatched. It inherits exactly its three pipe handles (<c>PROC_THREAD_ATTRIBUTE_HANDLE_LIST</c>), never the server's
/// stdin, the MCP pipe. The command line, the working directory and the environment are the <see cref="ProcessStartInfo"/>'s,
/// quoted and laid out as <see cref="System.Diagnostics.Process.Start()"/> does.
/// </summary>
[SupportedOSPlatform("windows")]
internal sealed partial class DesktopProcess : IRunProcess
{
    private const uint CreateSuspendedFlag = 0x00000004;
    private const uint CreateUnicodeEnvironment = 0x00000400;
    private const uint ExtendedStartupInfoPresent = 0x00080000;
    private const uint CreateNoWindow = 0x08000000;
    private const int UseStdHandles = 0x00000100;
    private const nint HandleListAttribute = 0x00020002;
    private const uint HandleFlagInherit = 0x00000001;

    private readonly ProcessInformation _native;
    private readonly string _application;
    private readonly TaskCompletionSource<OutputHandlers> _handlers = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private volatile bool _cancelled;

    private DesktopProcess(Process process, ProcessInformation native, string application)
    {
        Process = process;
        _native = native;
        _application = application;
    }

    public Process Process { get; }

    /// <summary>
    /// Creates the process suspended on <paramref name="desktopPath"/>, with its stdin closed at once and its stdout and stderr
    /// on pipes that are read once <see cref="BeginRead"/> is called.
    /// </summary>
    /// <param name="startInfo">The file, arguments, working directory and environment to start with.</param>
    /// <param name="desktopPath">
    /// The desktop's full <c>&lt;station&gt;\&lt;desktop&gt;</c> path, passed to <c>STARTUPINFO.lpDesktop</c> as given: the
    /// station is the server's own (<see cref="HiddenDesktop.Path"/>), since a child cannot initialise on one it cannot open.
    /// </param>
    /// <exception cref="SessionException">A Win32 call failed; the message names it and its error.</exception>
    public static DesktopProcess CreateSuspended(ProcessStartInfo startInfo, string desktopPath)
    {
        string application = Path.GetFullPath(startInfo.FileName);
        AnonymousPipeServerStream stdout;
        AnonymousPipeServerStream stderr;
        ProcessInformation native;
        lock (ChildProcesses.StartLock)
        {
            (stdout, stderr, native) = CreateWithPipes(application, startInfo, desktopPath);
        }

        try
        {
            DesktopProcess created = new(Watch(native), native, application);
            _ = created.ReadLinesAsync(stdout, handlers => handlers.Stdout);
            _ = created.ReadLinesAsync(stderr, handlers => handlers.Stderr);
            return created;
        }
        catch
        {
            stdout.Dispose();
            stderr.Dispose();
            throw;
        }
    }

    /// <summary>
    /// Creates the process with its stdin closed at once and its stdout and stderr on pipes whose ends it alone holds. The
    /// caller holds <see cref="ChildProcesses.StartLock"/>.
    /// </summary>
    private static (AnonymousPipeServerStream Stdout, AnonymousPipeServerStream Stderr, ProcessInformation Native) CreateWithPipes(
        string application,
        ProcessStartInfo startInfo,
        string desktopPath
    )
    {
        using AnonymousPipeServerStream stdin = new(PipeDirection.Out, HandleInheritability.None);
        AnonymousPipeServerStream stdout = new(PipeDirection.In, HandleInheritability.None);
        AnonymousPipeServerStream stderr = new(PipeDirection.In, HandleInheritability.None);
        try
        {
            nint[] inherited = [ClientHandle(stdin), ClientHandle(stdout), ClientHandle(stderr)];
            ProcessInformation native = CreateInheriting(application, startInfo, desktopPath, inherited);
            stdout.DisposeLocalCopyOfClientHandle();
            stderr.DisposeLocalCopyOfClientHandle();
            return (stdout, stderr, native);
        }
        catch
        {
            stdout.Dispose();
            stderr.Dispose();
            throw;
        }
    }

    /// <summary>
    /// Creates the process with <paramref name="inherited"/> inheritable only for the <c>CreateProcessW</c> call, as the
    /// handle list requires.
    /// </summary>
    private static ProcessInformation CreateInheriting(string application, ProcessStartInfo startInfo, string desktopPath, nint[] inherited)
    {
        foreach (nint handle in inherited)
        {
            if (!SetHandleInformation(handle, HandleFlagInherit, HandleFlagInherit))
            {
                throw Failure("SetHandleInformation", Marshal.GetLastPInvokeError(), application);
            }
        }

        try
        {
            return CreateProcess(application, startInfo, desktopPath, inherited);
        }
        finally
        {
            foreach (nint handle in inherited)
            {
                // The handles are closed right after under the same lock, so a flag left set cannot leak them.
                _ = SetHandleInformation(handle, HandleFlagInherit, 0);
            }
        }
    }

    public void Start()
    {
        // Joined while suspended, so every process it starts joins too.
        OwnWork.Adopt(_native.Process);
        if (ResumeThread(_native.Thread) == uint.MaxValue)
        {
            int error = Marshal.GetLastPInvokeError();
            Abandon(_native);
            _handlers.TrySetCanceled();
            Process.Dispose();
            throw Failure("ResumeThread", error, _application);
        }

        CloseHandles(_native);
    }

    public void BeginRead(Action<string> stdout, Action<string> stderr) => _handlers.TrySetResult(new OutputHandlers(stdout, stderr));

    public void CancelRead()
    {
        if (!_handlers.Task.IsCompletedSuccessfully)
        {
            throw new InvalidOperationException("The process's output is not being read.");
        }

        _cancelled = true;
    }

    /// <summary>
    /// A command line for <c>CreateProcessW</c>: the file quoted, then each argument quoted by the MSVCRT rules
    /// <see cref="ProcessStartInfo.ArgumentList"/> uses, so <c>CommandLineToArgvW</c> gives the list back.
    /// </summary>
    internal static string BuildCommandLine(string fileName, IEnumerable<string> arguments)
    {
        StringBuilder line = new();
        line.Append('"').Append(fileName).Append('"');
        foreach (string argument in arguments)
        {
            line.Append(' ');
            AppendArgument(line, argument);
        }

        return line.ToString();
    }

    /// <summary>
    /// An environment block for <c>CREATE_UNICODE_ENVIRONMENT</c>: each <c>name=value</c> NUL-terminated, sorted by name
    /// without regard to case as Windows requires, and the block ended by one more NUL.
    /// </summary>
    internal static string BuildEnvironmentBlock(IDictionary<string, string?> environment)
    {
        StringBuilder block = new();
        foreach (KeyValuePair<string, string?> variable in environment.OrderBy(variable => variable.Key, StringComparer.OrdinalIgnoreCase))
        {
            block.Append(variable.Key).Append('=').Append(variable.Value).Append('\0');
        }

        return block.Append('\0').ToString();
    }

    private static void AppendArgument(StringBuilder line, string argument)
    {
        if (argument.Length > 0 && !argument.Any(c => char.IsWhiteSpace(c) || c == '"'))
        {
            line.Append(argument);
            return;
        }

        line.Append('"');
        int backslashes = 0;
        foreach (char c in argument)
        {
            if (c == '\\')
            {
                backslashes++;
                continue;
            }

            // Backslashes before a quote are doubled and the quote escaped; anywhere else they stand as they are.
            line.Append('\\', c == '"' ? (backslashes * 2) + 1 : backslashes).Append(c);
            backslashes = 0;
        }

        // Backslashes before the closing quote are doubled, so they do not escape it.
        line.Append('\\', backslashes * 2).Append('"');
    }

    private static nint ClientHandle(AnonymousPipeServerStream pipe) => pipe.ClientSafePipeHandle.DangerousGetHandle();

    private static ProcessInformation CreateProcess(string application, ProcessStartInfo startInfo, string desktopPath, nint[] inherited)
    {
        nint desktopName = Marshal.StringToHGlobalUni(desktopPath);
        nint commandLine = Marshal.StringToHGlobalUni(BuildCommandLine(application, startInfo.ArgumentList));
        nint environment = Marshal.StringToHGlobalUni(BuildEnvironmentBlock(startInfo.Environment));
        nint handles = Marshal.AllocHGlobal(inherited.Length * nint.Size);
        Marshal.Copy(inherited, 0, handles, inherited.Length);
        nint attributes = 0;
        try
        {
            attributes = CreateHandleList(handles, inherited.Length, application);
            StartupInfoEx info = new()
            {
                Size = Marshal.SizeOf<StartupInfoEx>(),
                Desktop = desktopName,
                Flags = UseStdHandles,
                StdInput = inherited[0],
                StdOutput = inherited[1],
                StdError = inherited[2],
                AttributeList = attributes,
            };
            const uint flags = CreateNoWindow | CreateSuspendedFlag | CreateUnicodeEnvironment | ExtendedStartupInfoPresent;
            return CreateProcessW(
                application,
                commandLine,
                0,
                0,
                true,
                flags,
                environment,
                startInfo.WorkingDirectory,
                ref info,
                out ProcessInformation native
            )
                ? native
                : throw Failure("CreateProcessW", Marshal.GetLastPInvokeError(), application);
        }
        finally
        {
            FreeHandleList(attributes);
            foreach (nint block in (nint[])[desktopName, commandLine, environment, handles])
            {
                Marshal.FreeHGlobal(block);
            }
        }
    }

    /// <summary>An attribute list that lets the child inherit exactly <paramref name="count"/> handles at <paramref name="handles"/>.</summary>
    private static nint CreateHandleList(nint handles, int count, string application)
    {
        nint size = 0;

        // The first call only sizes the list, and fails with ERROR_INSUFFICIENT_BUFFER by design.
        _ = InitializeProcThreadAttributeList(0, 1, 0, ref size);
        nint list = Marshal.AllocHGlobal(size);
        if (!InitializeProcThreadAttributeList(list, 1, 0, ref size))
        {
            int error = Marshal.GetLastPInvokeError();
            Marshal.FreeHGlobal(list);
            throw Failure("InitializeProcThreadAttributeList", error, application);
        }

        if (!UpdateProcThreadAttribute(list, 0, HandleListAttribute, handles, count * nint.Size, 0, 0))
        {
            int error = Marshal.GetLastPInvokeError();
            FreeHandleList(list);
            throw Failure("UpdateProcThreadAttribute", error, application);
        }

        return list;
    }

    private static void FreeHandleList(nint list)
    {
        if (list != 0)
        {
            DeleteProcThreadAttributeList(list);
            Marshal.FreeHGlobal(list);
        }
    }

    /// <summary>The suspended process as a <see cref="Process"/> that raises <c>Exited</c>; a process that cannot be watched is ended.</summary>
    private static Process Watch(ProcessInformation native)
    {
        try
        {
            var process = Process.GetProcessById(native.ProcessId);
            process.EnableRaisingEvents = true;
            return process;
        }
        catch (Exception e) when (e is ArgumentException or InvalidOperationException or Win32Exception)
        {
            Abandon(native);
            throw new SessionException($"Godot's suspended process {native.ProcessId} could not be watched, so it was ended: {e.Message}", e);
        }
    }

    private static void Abandon(ProcessInformation native)
    {
        // A process that is being abandoned is ended however TerminateProcess fares; its handles are closed next.
        _ = TerminateProcess(native.Process, 1);
        CloseHandles(native);
    }

    private static void CloseHandles(ProcessInformation native)
    {
        // Closing a handle this process owns fails only for an invalid handle, which leaves nothing to release.
        _ = CloseHandle(native.Thread);
        _ = CloseHandle(native.Process);
    }

    private static SessionException Failure(string call, int error, string application) =>
        new(
            $"Godot could not be started from {application} on the hidden desktop: {call} failed with Win32 error {error} "
                + $"({Marshal.GetPInvokeErrorMessage(error)}). Set {Installation.GodotPathVariable} to the Godot 4.7 console executable, "
                + "or pass options.quiet false to start on the user's desktop."
        );

    /// <summary>
    /// Passes the pipe's lines on once <see cref="BeginRead"/> names where, until the end of the stream; after
    /// <see cref="CancelRead"/> the lines are still read, so the game never writes into a closed pipe, and dropped.
    /// </summary>
    private async Task ReadLinesAsync(AnonymousPipeServerStream pipe, Func<OutputHandlers, Action<string>> select)
    {
        using StreamReader reader = new(pipe, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
        Action<string> onLine;
        try
        {
            onLine = select(await _handlers.Task);
        }
        catch (OperationCanceledException)
        {
            // The start failed and the process was ended: there is nothing to read.
            return;
        }

        try
        {
            while (await reader.ReadLineAsync() is { } line)
            {
                PassOn(onLine, line);
            }
        }
        catch (IOException e)
        {
            PassOn(onLine, $"[godot-mcp] reading the game's output failed: {e.Message}");
        }
    }

    private void PassOn(Action<string> onLine, string line)
    {
        if (!_cancelled)
        {
            onLine(line);
        }
    }

    [LibraryImport("kernel32.dll", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool CreateProcessW(
        string application,
        nint commandLine,
        nint processAttributes,
        nint threadAttributes,
        [MarshalAs(UnmanagedType.Bool)] bool inheritHandles,
        uint creationFlags,
        nint environment,
        string currentDirectory,
        ref StartupInfoEx startupInfo,
        out ProcessInformation processInformation
    );

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool InitializeProcThreadAttributeList(nint list, int attributeCount, int flags, ref nint size);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool UpdateProcThreadAttribute(
        nint list,
        uint flags,
        nint attribute,
        nint value,
        nint size,
        nint previousValue,
        nint returnSize
    );

    [LibraryImport("kernel32.dll")]
    private static partial void DeleteProcThreadAttributeList(nint list);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SetHandleInformation(nint handle, uint mask, uint flags);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    private static partial uint ResumeThread(nint thread);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool TerminateProcess(nint process, uint exitCode);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool CloseHandle(nint handle);

    /// <summary>Where <see cref="BeginRead"/> sends each stream's lines.</summary>
    private sealed record OutputHandlers(Action<string> Stdout, Action<string> Stderr);

    /// <summary><c>STARTUPINFOEXW</c>: <c>STARTUPINFOW</c> followed by the attribute list.</summary>
    [StructLayout(LayoutKind.Sequential)]
    internal struct StartupInfoEx
    {
        public int Size;
        public nint Reserved;
        public nint Desktop;
        public nint Title;
        public int X;
        public int Y;
        public int XSize;
        public int YSize;
        public int XCountChars;
        public int YCountChars;
        public int FillAttribute;
        public int Flags;
        public short ShowWindow;
        public short Reserved2Size;
        public nint Reserved2;
        public nint StdInput;
        public nint StdOutput;
        public nint StdError;
        public nint AttributeList;
    }

    /// <summary><c>PROCESS_INFORMATION</c>.</summary>
    [StructLayout(LayoutKind.Sequential)]
    internal struct ProcessInformation
    {
        public nint Process;
        public nint Thread;
        public int ProcessId;
        public int ThreadId;
    }
}
