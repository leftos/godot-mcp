using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using GodotMcp.Server.CSharp;
using GodotMcp.Server.Tools;

namespace GodotMcp.Server.Session;

/// <summary>
/// What a scratch run tells as it plays, each scene keyed by its index in the plan: a scene's start, the session it took, each
/// step's start, its verdict (or that it plays again alone), and the start of its replay alone. A synchronous run tells nobody.
/// </summary>
internal interface IScratchRunObserver
{
    void SceneStarted(int scene);

    void SessionHeld(int scene, GodotSession session);

    void StepStarted(int scene, int step, string name, int total, double remainingSecondsAtMost);

    /// <summary>A scene's verdict; with <paramref name="playsAgain"/>, its first verdict, which a replay alone will settle.</summary>
    void SceneEnded(int scene, ScratchSceneResult result, bool playsAgain);

    void ReplayStarted(int scene);
}

/// <summary>Why a background scratch run ended: it returned, a stop, the server's exit, or an exception.</summary>
internal enum ScratchEnd
{
    Done,
    Stopped,
    Interrupted,
    Failed,
}

/// <summary>The clock a background run stamps its status with and the writer its skipped writes are reported to.</summary>
internal sealed record ScratchRunIo(Func<DateTimeOffset> Now, TextWriter Errors)
{
    public static ScratchRunIo System { get; } = new(() => DateTimeOffset.UtcNow, Console.Error);
}

/// <summary>A background scratch run's status file: the run, its state, the server playing it, and each scene in play order.</summary>
internal sealed record ScratchRunStatus(
    string RunId,
    string State,
    int ServerPid,
    string StartedAt,
    string UpdatedAt,
    IReadOnlyList<ScratchSceneStatus> Scenes
)
{
    /// <summary>The object run_scratches returns without background; present once the state is done.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public ScratchRunResult? Result { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Error { get; init; }

    /// <summary>options.timeScale, the time scale the run's games run at; null at 1.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public double? TimeScale { get; init; }
}

/// <summary>One scene of a background run's status: queued, playing (with its step), done (with its verdict), or the run's final word.</summary>
internal sealed record ScratchSceneStatus(string Scene, string State)
{
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? Step { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? StepName { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? Total { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public double? ElapsedSeconds { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public double? RemainingSecondsAtMost { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Verdict { get; init; }

    /// <summary>The time scale the scene's game runs at when it differs from the run's (its profile pace has "timeScale": 1); else null.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public double? TimeScale { get; init; }
}

/// <summary>What run_scratches with options.background returns: the run's id, its two files, and its scenes in play order.</summary>
internal sealed record ScratchBackgroundStart(string RunId, string StatusFile, string LogFile, IReadOnlyList<string> Scenes);

/// <summary>
/// One background scratch run: its id, project and files, the snapshot its status file holds, and its end. Every write goes
/// under the run's lock; the first end reason recorded wins, and the final write puts the status file first, then the completion
/// line, then marks the run finished, after which every write is skipped. A status file's move a holder refuses is retried for
/// half a second; a write that still fails is reported and skipped, never thrown, and the final one is retried for about a second.
/// </summary>
[SuppressMessage(
    "Design",
    "CA1001:Types that own disposable fields should be disposable",
    Justification = "The run's token source is disposed by EndTask once its task has ended and no cancel is under way; a Dispose "
        + "callable at any time would race the stops and the server's exit that cancel it."
)]
internal sealed partial class ScratchBackgroundRun : IScratchRunObserver
{
    internal const string Running = "running";
    internal const string Queued = "queued";
    internal const string Playing = "playing";
    internal const string SceneDone = "done";

    /// <summary>The start of every line of a run's log, before the run id.</summary>
    internal const string LinePrefix = "godot-mcp scratch run ";

    private const int IdAttempts = 5;
    private static readonly TimeSpan FinalRetry = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan FinalRetryPause = TimeSpan.FromMilliseconds(100);
    private static readonly TimeSpan MoveRetry = TimeSpan.FromMilliseconds(500);
    private static readonly TimeSpan MoveRetryPause = TimeSpan.FromMilliseconds(50);

    // The Win32 error codes in an IOException's HResult for a file another handle holds.
    private const int SharingViolation = 32;
    private const int LockViolation = 33;

    private readonly Lock _gate = new();
    private readonly ScratchRuns _owner;
    private readonly ScratchRunIo _io;
    private readonly SceneState[] _scenes;
    private readonly int[] _slotOf;
    private readonly string _startedAt;
    private readonly double? _timeScale;
    private readonly CancellationTokenSource _cancel = new();
    private readonly List<GodotSession> _held = [];
    private readonly TaskCompletionSource _finalWritten = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private string _runId;
    private string _updatedAt;
    private ScratchRunStatus? _closing;
    private ScratchEnd? _reason;
    private string? _error;
    private ScratchRunResult? _result;
    private ScratchRunStatus? _final;
    private Task? _task;
    private bool _taskEnded;
    private int _cancelling;

    internal ScratchBackgroundRun(ScratchRuns owner, ScratchPlan plan, int serverPid, ScratchRunIo io)
    {
        _owner = owner;
        _io = io;
        ProjectDir = plan.ProjectDir;
        ServerPid = serverPid;
        int[] order = ScratchRun.StartOrder(plan.Scenes, plan.Listed);
        _scenes = [.. order.Select(index => new SceneState(plan.Scenes[index].Name, OwnTimeScale(plan.Scenes[index], plan)))];
        _timeScale = plan.TimeScale == 1 ? null : plan.TimeScale;
        _slotOf = new int[order.Length];
        for (int slot = 0; slot < order.Length; slot++)
        {
            _slotOf[order[slot]] = slot;
        }

        DateTimeOffset now = io.Now();
        _startedAt = Stamp(now);
        _updatedAt = _startedAt;
        _runId = NewRunId(now);
    }

    public string ProjectDir { get; }

    public int ServerPid { get; }

    public string RunId
    {
        get
        {
            lock (_gate)
            {
                return _runId;
            }
        }
    }

    public string StatusFile => StatusPath(ProjectDir, RunId);

    public string LogFile => LogPath(ProjectDir, RunId);

    /// <summary>The scenes' names in play order.</summary>
    public IReadOnlyList<string> Scenes => [.. _scenes.Select(scene => scene.Name)];

    /// <summary>The token the run plays under, cancelled by a stop or the server's exit once its reason is recorded.</summary>
    public CancellationToken Token => _cancel.Token;

    /// <summary>Whether the run has not yet made its final write.</summary>
    public bool IsLive
    {
        get
        {
            lock (_gate)
            {
                return _final is null;
            }
        }
    }

    /// <summary>Whether the run's end has begun: its final status is frozen, whether or not its final writes have landed.</summary>
    public bool HasEnded
    {
        get
        {
            lock (_gate)
            {
                return _closing is not null;
            }
        }
    }

    /// <summary>The final snapshot once the run has made its final write; else null.</summary>
    public ScratchRunStatus? FinalStatus
    {
        get
        {
            lock (_gate)
            {
                return _final;
            }
        }
    }

    /// <summary>The run's task once started; null before.</summary>
    public Task? Task
    {
        get
        {
            lock (_gate)
            {
                return _task;
            }
        }
    }

    /// <summary>The sessions the run's scenes took.</summary>
    public IReadOnlyList<GodotSession> HeldSessions
    {
        get
        {
            lock (_gate)
            {
                return [.. _held];
            }
        }
    }

    /// <summary>A new run id: the UTC time as yyyyMMddTHHmmss, '-', and 4 random lowercase hex digits.</summary>
    internal static string NewRunId(DateTimeOffset now) =>
        now.UtcDateTime.ToString("yyyyMMdd'T'HHmmss", CultureInfo.InvariantCulture) + "-" + RandomNumberGenerator.GetHexString(4, lowercase: true);

    /// <summary>Whether <paramref name="runId"/> has a run id's form, so a path may be built from it.</summary>
    internal static bool IsRunId(string runId) => RunIdForm().IsMatch(runId);

    internal static string StatusPath(string projectDir, string runId) => Path.Combine(ProjectPrep.LogFolder(projectDir), $"scratch-{runId}.json");

    internal static string LogPath(string projectDir, string runId) => Path.Combine(ProjectPrep.LogFolder(projectDir), $"scratch-{runId}.log");

    /// <summary>
    /// The text with every control character (CR, LF and NEL included) and every Unicode line or paragraph separator taken out,
    /// so it stays on one log line.
    /// </summary>
    internal static string Clean(string text) =>
        string.Concat(
            text.Where(character =>
                !char.IsControl(character)
                && char.GetUnicodeCategory(character) is not (UnicodeCategory.LineSeparator or UnicodeCategory.ParagraphSeparator)
            )
        );

    /// <summary>
    /// Creates the log with CreateNew, drawing a new id when one by that name exists (up to 5 ids), then writes the status file.
    /// Nothing when the run has already begun its end, as a shutdown ends a run whose task has not started. On a failure, the
    /// log and the temp file it made are deleted before the exception goes on.
    /// </summary>
    /// <exception cref="IOException">The folder or a file could not be written.</exception>
    /// <exception cref="UnauthorizedAccessException">The folder or a file is not writable.</exception>
    internal void CreateFiles()
    {
        lock (_gate)
        {
            if (_closing is not null)
            {
                return;
            }

            bool logMade = false;
            string temp = string.Empty;
            try
            {
                logMade = CreateLogLocked();
                string path = StatusPath(ProjectDir, _runId);
                temp = TempPath(path);
                File.WriteAllText(temp, JsonSerializer.Serialize(SnapshotLocked(), ToolJson.Options));
                MoveIntoPlace(temp, path);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                DeleteMade(logMade ? LogPath(ProjectDir, _runId) : string.Empty);
                DeleteMade(temp);
                throw;
            }
        }
    }

    /// <summary>Creates the log with CreateNew, drawing a new id when one by that name exists, up to 5 ids; true once made.</summary>
    private bool CreateLogLocked()
    {
        Directory.CreateDirectory(ProjectPrep.LogFolder(ProjectDir));
        for (int attempt = 1; ; attempt++)
        {
            try
            {
                using FileStream log = new(LogPath(ProjectDir, _runId), FileMode.CreateNew, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete);
                return true;
            }
            catch (IOException) when (attempt < IdAttempts && File.Exists(LogPath(ProjectDir, _runId)))
            {
                _runId = NewRunId(_io.Now());
            }
        }
    }

    /// <summary>Deletes a file a failed <see cref="CreateFiles"/> made; nothing for an empty path, a failure reported.</summary>
    private void DeleteMade(string path)
    {
        if (path.Length > 0)
        {
            DeleteTemp(path);
        }
    }

    /// <summary>
    /// Starts the run's task with <paramref name="body"/> and no token, unless the run has already ended, in which case its entry
    /// is removed here, since no task's finally will.
    /// </summary>
    internal void Start(Func<Task> body)
    {
        bool started;
        lock (_gate)
        {
            started = _closing is null;
            if (started)
            {
                _task = System.Threading.Tasks.Task.Run(body);
            }
        }

        if (!started)
        {
            _owner.Remove(this);
            EndTask();
        }
    }

    /// <summary>
    /// The run's task: the run played under the run's own token, its result the final write, any exception (a prep failure, a
    /// cancellation by a stop or the exit) <see cref="ScratchEnd.Failed"/> unless a reason was recorded first; then the entry
    /// removed, after every game has stopped.
    /// </summary>
    [SuppressMessage(
        "Design",
        "CA1031:Do not catch general exception types",
        Justification = "A background run has no caller to throw to: whatever ends it must end in a final state and its completion line."
    )]
    internal async Task PlayAsync(SessionRegistry registry, CSharpBridge csharp, ScratchPlan plan)
    {
        try
        {
            Finish(await ScratchRun.RunAsync(registry, csharp, plan, this, Token));
        }
        catch (Exception e)
        {
            if (!Token.IsCancellationRequested)
            {
                await _io.Errors.WriteLineAsync($"godot-mcp: background scratch run {RunId} failed: {e}");
            }

            Fail(e.Message);
        }
        finally
        {
            _owner.Remove(this);
            EndTask();
        }
    }

    /// <summary>
    /// Records <see cref="ScratchEnd.Stopped"/>, cancels the run, waits for its task, and returns its final status with whether
    /// it is live.
    /// </summary>
    internal async Task<(ScratchRunStatus Status, bool Live)> StopAsync(CancellationToken cancellationToken)
    {
        Record(ScratchEnd.Stopped);
        Cancel();
        if (Task is { } task)
        {
            await task.WaitAsync(cancellationToken);
        }
        else
        {
            Finish(ScratchEnd.Stopped);
        }

        return Read();
    }

    /// <summary>Records <see cref="ScratchEnd.Interrupted"/> and cancels the run, as the server's exit begins.</summary>
    internal void Interrupt()
    {
        Record(ScratchEnd.Interrupted);
        Cancel();
    }

    /// <summary>Records <paramref name="reason"/> unless a reason or the final write came first; whether it was recorded.</summary>
    internal bool Record(ScratchEnd reason)
    {
        lock (_gate)
        {
            if (_reason is not null || _final is not null)
            {
                return false;
            }

            _reason = reason;
            return true;
        }
    }

    /// <summary>The run ended with <paramref name="result"/>: done, unless another reason was recorded first.</summary>
    internal void Finish(ScratchRunResult result) => End(new ScratchEnding(ScratchEnd.Done, result, null), FinalRetry);

    /// <summary>The run ended for <paramref name="reason"/>, unless another reason was recorded first.</summary>
    internal void Finish(ScratchEnd reason) => Finish(reason, FinalRetry);

    /// <summary>
    /// The run ended for <paramref name="reason"/>, unless another reason was recorded first, its final writes retried (or
    /// another caller's waited for) within <paramref name="budget"/>, at most about a second; each is tried once at least.
    /// </summary>
    internal void Finish(ScratchEnd reason, TimeSpan budget) => End(new ScratchEnding(reason, null, null), budget < FinalRetry ? budget : FinalRetry);

    /// <summary>The run ended on an exception whose message is <paramref name="error"/>, unless another reason was recorded first.</summary>
    internal void Fail(string error) => End(new ScratchEnding(ScratchEnd.Failed, null, error), FinalRetry);

    /// <summary>The run's status now, its final snapshot once its end has begun, and whether it is live, read under one lock.</summary>
    internal (ScratchRunStatus Status, bool Live) Read()
    {
        lock (_gate)
        {
            return (_final ?? _closing ?? SnapshotLocked(), _final is null);
        }
    }

    /// <summary>Marks the task ended and disposes the run's token source once no cancel is under way.</summary>
    internal void EndTask()
    {
        bool dispose;
        lock (_gate)
        {
            _taskEnded = true;
            dispose = _cancelling == 0;
        }

        if (dispose)
        {
            _cancel.Dispose();
        }
    }

    public void SceneStarted(int scene) =>
        Event(
            scene,
            state =>
            {
                state.Play(_io.Now());
                return $"scene {Clean(state.Name)} start";
            }
        );

    public void SessionHeld(int scene, GodotSession session)
    {
        lock (_gate)
        {
            _held.Add(session);
        }

        _owner.Hold(session);
    }

    public void StepStarted(int scene, int step, string name, int total, double remainingSecondsAtMost) =>
        Event(
            scene,
            state =>
            {
                state.State = Playing;
                state.Step = step;
                state.StepName = name;
                state.Total = total;
                state.Remaining = Math.Round(remainingSecondsAtMost, 1);
                return $"scene {Clean(state.Name)} step {step}/{total} {Clean(name)}";
            }
        );

    public void SceneEnded(int scene, ScratchSceneResult result, bool playsAgain) =>
        Event(
            scene,
            state =>
            {
                state.Clear();
                if (playsAgain)
                {
                    return null;
                }

                state.State = SceneDone;
                state.Verdict = result.Verdict;
                state.Elapsed = result.Seconds;
                return $"scene {Clean(state.Name)} {result.Verdict} in {result.Seconds.ToString("0.#", CultureInfo.InvariantCulture)} s";
            }
        );

    public void ReplayStarted(int scene) =>
        Event(
            scene,
            state =>
            {
                state.Play(_io.Now());
                return $"scene {Clean(state.Name)} start alone";
            }
        );

    /// <summary>
    /// One event: the scene's state changed by <paramref name="change"/>, the status file written, then the line it returns
    /// appended; skipped once a reason is recorded or the run has ended, so no scene changes after a stop.
    /// </summary>
    private void Event(int scene, Func<SceneState, string?> change)
    {
        lock (_gate)
        {
            if (_reason is not null || _final is not null)
            {
                return;
            }

            string? line = change(_scenes[_slotOf[scene]]);
            _updatedAt = Stamp(_io.Now());

            // Tried once, its move retried only within MoveIntoPlace's short window: a step's start is written inside the
            // scene's ceiling, which no write may fail.
            TryWriteStatus(SnapshotLocked());
            if (line is not null)
            {
                TryAppend(line);
            }
        }
    }

    /// <summary>How a caller would end the run: its reason, and the result or the error that goes with it.</summary>
    private sealed record ScratchEnding(ScratchEnd Reason, ScratchRunResult? Result, string? Error);

    /// <summary>
    /// The run's end. The first caller records its reason unless one was recorded first, freezes the final snapshot under the
    /// lock, then writes the status file and the completion line outside it, sharing <paramref name="budget"/> for their
    /// retries, and only then marks the run finished; no other writer can run meanwhile, since every event is skipped once a
    /// reason is recorded. A later caller waits for that within its own budget.
    /// </summary>
    private void End(ScratchEnding ending, TimeSpan budget)
    {
        ScratchRunStatus? final = null;
        lock (_gate)
        {
            if (_closing is null)
            {
                final = _closing = CloseLocked(ending);
            }
        }

        if (final is null)
        {
            _finalWritten.Task.Wait(budget > TimeSpan.Zero ? budget : TimeSpan.Zero);
            return;
        }

        WriteFinal(final, budget);
        lock (_gate)
        {
            _final = final;
        }

        _finalWritten.TrySetResult();
    }

    /// <summary>Records the ending's reason unless one came first, gives playing scenes the run's word, and freezes the snapshot.</summary>
    private ScratchRunStatus CloseLocked(ScratchEnding ending)
    {
        if (_reason is null)
        {
            _reason = ending.Reason;
            _error = ending.Error is null ? null : Clean(ending.Error.ReplaceLineEndings(" "));
            _result = ending.Result;
        }

        DateTimeOffset now = _io.Now();
        string word = StateWord(_reason.Value);
        foreach (SceneState scene in _scenes.Where(scene => scene.State == Playing))
        {
            scene.Elapsed = scene.ElapsedAt(now);
            scene.Remaining = null;
            scene.State = word;
        }

        _updatedAt = Stamp(now);
        return SnapshotLocked(final: true);
    }

    /// <summary>
    /// The status file, then the completion line, each retried until it lands or <paramref name="budget"/> has passed since the
    /// first try, each tried once at least; never under the run's lock.
    /// </summary>
    private void WriteFinal(ScratchRunStatus final, TimeSpan budget)
    {
        long started = Stopwatch.GetTimestamp();
        while (!TryWriteStatus(final) && Stopwatch.GetElapsedTime(started) < budget)
        {
            Thread.Sleep(Pause(started, budget));
        }

        string line = CompletionText();
        while (!TryAppend(line) && Stopwatch.GetElapsedTime(started) < budget)
        {
            Thread.Sleep(Pause(started, budget));
        }
    }

    /// <summary>The pause before a final write's next try: the retry pause, or what is left of the budget when that is less.</summary>
    private static TimeSpan Pause(long started, TimeSpan budget)
    {
        TimeSpan left = budget - Stopwatch.GetElapsedTime(started);
        return left < FinalRetryPause ? (left > TimeSpan.Zero ? left : TimeSpan.Zero) : FinalRetryPause;
    }

    /// <summary>Cancels the run's token, unless its task has ended and the source may be disposed.</summary>
    private void Cancel()
    {
        lock (_gate)
        {
            if (_taskEnded)
            {
                return;
            }

            _cancelling++;
        }

        bool dispose;
        try
        {
            // Cancelling runs the run's own continuations inline, which take the run's lock, so it is called outside it.
            _cancel.Cancel();
        }
        finally
        {
            lock (_gate)
            {
                _cancelling--;
                dispose = _taskEnded && _cancelling == 0;
            }
        }

        if (dispose)
        {
            _cancel.Dispose();
        }
    }

    private ScratchRunStatus SnapshotLocked() => SnapshotLocked(final: false);

    /// <summary>The snapshot: running until the final one, which carries the run's end, and its result or its error.</summary>
    private ScratchRunStatus SnapshotLocked(bool final)
    {
        DateTimeOffset now = _io.Now();
        ScratchEnd? end = final ? _reason : null;
        return new ScratchRunStatus(
            _runId,
            end is { } reason ? StateWord(reason) : Running,
            ServerPid,
            _startedAt,
            _updatedAt,
            [.. _scenes.Select(scene => scene.Status(now))]
        )
        {
            Result = end == ScratchEnd.Done ? _result : null,
            Error = end == ScratchEnd.Failed ? _error : null,
            TimeScale = _timeScale,
        };
    }

    /// <summary>The time scale a scene's game runs at when it differs from the call's, as for a profile pace's opt-out; else null.</summary>
    private static double? OwnTimeScale(ScratchScenePlan scene, ScratchPlan plan) => scene.TimeScale == plan.TimeScale ? null : scene.TimeScale;

    private string CompletionText() =>
        _reason switch
        {
            ScratchEnd.Done when _result is { } result => "finished: done, "
                + $"passed {(result.Passed ? "true" : "false")}, green {result.Green}, red {result.Red}, "
                + $"killed {result.Killed}, known {result.Known}",
            ScratchEnd.Failed => $"finished: failed: {_error}",
            _ => $"finished: {StateWord(_reason ?? ScratchEnd.Done)}",
        };

    private static string StateWord(ScratchEnd reason) =>
        reason switch
        {
            ScratchEnd.Done => "done",
            ScratchEnd.Stopped => "stopped",
            ScratchEnd.Interrupted => "interrupted",
            _ => "failed",
        };

    /// <summary>
    /// Writes the snapshot to a unique temp file and moves it into place (<see cref="MoveIntoPlace"/>); a failure is reported
    /// and skipped.
    /// </summary>
    private bool TryWriteStatus(ScratchRunStatus status)
    {
        string path = StatusPath(ProjectDir, _runId);
        string temp = TempPath(path);
        try
        {
            File.WriteAllText(temp, JsonSerializer.Serialize(status, ToolJson.Options));
            MoveIntoPlace(temp, path);
            return true;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            _io.Errors.WriteLine($"godot-mcp: writing scratch run {_runId}'s status file {path} failed, so the write was skipped: {e.Message}");
            DeleteTemp(temp);
            return false;
        }
    }

    /// <summary>
    /// Moves the temp file over the status file. A move refused because another handle holds the target (an outside reader
    /// that does not share delete, a scanner or an indexer, which let go within moments) is retried every
    /// <see cref="MoveRetryPause"/>, each retry reported, until <see cref="MoveRetry"/> has passed since the first try; any
    /// other failure, or the refusal past that window, goes on to the caller.
    /// </summary>
    /// <exception cref="IOException">The move failed.</exception>
    /// <exception cref="UnauthorizedAccessException">The move was refused.</exception>
    private void MoveIntoPlace(string temp, string path)
    {
        long started = Stopwatch.GetTimestamp();
        for (int attempt = 1; ; attempt++)
        {
            try
            {
                File.Move(temp, path, overwrite: true);
                return;
            }
            catch (Exception e) when (IsHeldTarget(e) && Stopwatch.GetElapsedTime(started) < MoveRetry)
            {
                // The one marked retry: a target another handle holds, never any other failure.
                _io.Errors.WriteLine(
                    $"godot-mcp: moving scratch run {_runId}'s status file into place at {path} was refused, so it is tried again "
                        + $"(try {attempt + 1}): {e.Message}"
                );
                Thread.Sleep(MoveRetryPause);
            }
        }
    }

    /// <summary>
    /// Whether a move failed because another handle holds its target: access denied, or a sharing or lock violation.
    /// </summary>
    private static bool IsHeldTarget(Exception e) =>
        e is UnauthorizedAccessException || (e is IOException && (e.HResult & 0xFFFF) is SharingViolation or LockViolation);

    /// <summary>Appends one line to the run's log; a failure is reported and skipped.</summary>
    private bool TryAppend(string line)
    {
        string path = LogPath(ProjectDir, _runId);
        try
        {
            using FileStream log = new(path, FileMode.Append, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete);
            log.Write(Encoding.UTF8.GetBytes($"{LinePrefix}{_runId} {line}\n"));
            return true;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            _io.Errors.WriteLine($"godot-mcp: appending to scratch run {_runId}'s log {path} failed, so the line was skipped: {e.Message}");
            return false;
        }
    }

    private void DeleteTemp(string temp)
    {
        try
        {
            File.Delete(temp);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            _io.Errors.WriteLine($"godot-mcp: deleting the temp file {temp} failed: {e.Message}");
        }
    }

    private static string TempPath(string path) => $"{path}.{Guid.NewGuid():N}.tmp";

    private static string Stamp(DateTimeOffset time) => time.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture);

    [GeneratedRegex(@"^[0-9]{8}T[0-9]{6}-[0-9a-f]{4}\z", RegexOptions.CultureInvariant)]
    private static partial Regex RunIdForm();

    /// <summary>One scene's state in the run, changed only under the run's lock.</summary>
    private sealed class SceneState(string name, double? timeScale)
    {
        public string Name { get; } = name;

        /// <summary>The time scale the scene's game runs at when it differs from the call's; else null.</summary>
        public double? TimeScale { get; } = timeScale;

        public string State { get; set; } = Queued;

        public int? Step { get; set; }

        public string? StepName { get; set; }

        public int? Total { get; set; }

        public DateTimeOffset? Started { get; set; }

        public double? Elapsed { get; set; }

        public double? Remaining { get; set; }

        public string? Verdict { get; set; }

        public void Play(DateTimeOffset now)
        {
            Clear();
            State = Playing;
            Started = now;
        }

        /// <summary>Back to queued with nothing of a play kept.</summary>
        public void Clear()
        {
            State = Queued;
            Step = null;
            StepName = null;
            Total = null;
            Started = null;
            Elapsed = null;
            Remaining = null;
            Verdict = null;
        }

        public double? ElapsedAt(DateTimeOffset now) => Started is { } started ? Math.Round((now - started).TotalSeconds, 1) : Elapsed;

        public ScratchSceneStatus Status(DateTimeOffset now) =>
            new(Name, State)
            {
                Step = Step,
                StepName = StepName,
                Total = Total,
                ElapsedSeconds = State == Playing ? ElapsedAt(now) : Elapsed,
                RemainingSecondsAtMost = Remaining,
                Verdict = Verdict,
                TimeScale = TimeScale,
            };
    }
}
