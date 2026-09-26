using Microsoft.Extensions.Logging;

namespace GodotMcp.Server;

/// <summary>Every message the server logs; all of it goes to stderr.</summary>
internal static partial class Log
{
    [LoggerMessage(Level = LogLevel.Error, Message = "batch_drive step {Index} ({Step}) failed with an unexpected exception.")]
    public static partial void BatchStepFailed(ILogger logger, Exception exception, int index, string step);

    [LoggerMessage(Level = LogLevel.Information, Message = "Bridge connection ended: {Reason}")]
    public static partial void BridgeConnectionEnded(ILogger logger, string reason);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Dropped a {Length}-byte bridge frame without a numeric id.")]
    public static partial void DroppedFrameWithoutId(ILogger logger, int length);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Dropped a bridge errors frame: handling it failed.")]
    public static partial void ErrorsFrameDropped(ILogger logger, Exception exception);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Dropped the bridge's reply to request {Id}: it timed out or was never sent.")]
    public static partial void DroppedLateReply(ILogger logger, long id);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Refused a bridge connection: {Reason}.")]
    public static partial void RefusedBridgeConnection(ILogger logger, string reason);

    [LoggerMessage(Level = LogLevel.Information, Message = "{ProjectDir} is not in a git repository; {FileName} is not added to any exclude file.")]
    public static partial void NotAGitRepository(ILogger logger, string projectDir, string fileName);

    [LoggerMessage(Level = LogLevel.Warning, Message = "git could not be started, so {Directory} is treated as outside any repository.")]
    public static partial void GitUnavailable(ILogger logger, Exception exception, string directory);

    [LoggerMessage(Level = LogLevel.Information, Message = "Godot {Pid} runs {Project}; the bridge is connected.")]
    public static partial void RunStarted(ILogger logger, int pid, string project);

    [LoggerMessage(Level = LogLevel.Debug, Message = "The handshake wait ended without a bridge.")]
    public static partial void HandshakeAbandoned(ILogger logger, Exception exception);

    [LoggerMessage(Level = LogLevel.Warning, Message = "The bridge did not acknowledge shutdown; waiting for the exit anyway.")]
    public static partial void ShutdownNotAcknowledged(ILogger logger, Exception exception);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Godot for {Project} did not exit within {Seconds} s of shutdown; killing it.")]
    public static partial void ExitGraceExpired(ILogger logger, string project, double seconds);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Killing Godot for {Project} failed; it may have exited already.")]
    public static partial void KillFailed(ILogger logger, Exception exception, string project);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Godot for {Project} was still running {Seconds} s after the kill.")]
    public static partial void StillRunningAfterKill(ILogger logger, string project, double seconds);

    [LoggerMessage(Level = LogLevel.Information, Message = "Godot for {Project} exited; its override.cfg is removed.")]
    public static partial void OverrideRemovedAfterExit(ILogger logger, string project);

    [LoggerMessage(
        Level = LogLevel.Information,
        Message = "A replaced Godot run on {Project} exited; the session runs a newer one, so its override.cfg stays."
    )]
    public static partial void ReplacedRunExited(ILogger logger, string project);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Stopping the output capture of the replaced Godot run on {Project} failed.")]
    public static partial void OutputCaptureStopFailed(ILogger logger, Exception exception, string project);

    [LoggerMessage(Level = LogLevel.Information, Message = "Restarted {Project}: Godot {PreviousPid} was stopped and Godot {Pid} runs it now.")]
    public static partial void RunRestarted(ILogger logger, string project, int previousPid, int pid);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Removing the override.cfg of {Project} after Godot exited failed.")]
    public static partial void OverrideRemovalFailed(ILogger logger, Exception exception, string project);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Removing the {What} of {Project} after a failed start failed.")]
    public static partial void CleanupFailed(ILogger logger, Exception exception, string what, string project);

    [LoggerMessage(
        Level = LogLevel.Warning,
        Message = "Godot for {Project} did not answer a ping within {Seconds} s; killing it without a shutdown."
    )]
    public static partial void StopFoundGameStuck(ILogger logger, string project, double seconds);

    [LoggerMessage(Level = LogLevel.Warning, Message = "The ping before stopping Godot for {Project} failed; shutting it down as usual.")]
    public static partial void StopPingFailed(ILogger logger, Exception exception, string project);

    [LoggerMessage(
        Level = LogLevel.Warning,
        Message = "'{Tool}' timed out on session {Session}; the hang probe found the main thread {Outcome}. {ProcessState}"
    )]
    public static partial void RequestTimedOut(ILogger logger, string tool, string session, string outcome, string processState);

    [LoggerMessage(Level = LogLevel.Information, Message = "Attached to a game on {Project}; the bridge is connected.")]
    public static partial void Attached(ILogger logger, string project);

    [LoggerMessage(Level = LogLevel.Information, Message = "Detached from the game on {Project}; it keeps running.")]
    public static partial void Detached(ILogger logger, string project);

    [LoggerMessage(Level = LogLevel.Information, Message = "Preparing {Project}: checking its C# build and its import.")]
    public static partial void PrepStarted(ILogger logger, string project);

    [LoggerMessage(Level = LogLevel.Information, Message = "Prepared {Project}: build {Build}, import {Import}.")]
    public static partial void PrepFinished(ILogger logger, string project, string build, string import);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Preparing {Project} failed; the game is not started.")]
    public static partial void PrepFailed(ILogger logger, Exception exception, string project);

    [LoggerMessage(Level = LogLevel.Information, Message = "Preparing {Project} was cancelled; the game is not started.")]
    public static partial void PrepCancelled(ILogger logger, Exception exception, string project);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Deleting the headless run's file {Path} failed; it is left behind.")]
    public static partial void HeadlessFileDeleteFailed(ILogger logger, Exception exception, string path);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Killing {File} and the processes it started failed; some of them may still run.")]
    public static partial void ToolKillFailed(ILogger logger, Exception exception, string file);

    [LoggerMessage(Level = LogLevel.Warning, Message = "{File} was still running {Seconds} s after it was killed.")]
    public static partial void ToolStillRunningAfterKill(ILogger logger, string file, double seconds);

    [LoggerMessage(
        Level = LogLevel.Warning,
        Message = "The game on {Project} was still running {Seconds} s after its run ended; its recording is cut anyway."
    )]
    public static partial void GameExitWaitEnded(ILogger logger, Exception exception, string project, double seconds);

    [LoggerMessage(
        Level = LogLevel.Warning,
        Message = "The game on {Project} (pid {ProcessId}) could not be watched for its exit; its recording is cut anyway."
    )]
    public static partial void GameExitNotObserved(ILogger logger, Exception exception, string project, int processId);

    [LoggerMessage(Level = LogLevel.Debug, Message = "The game on {Project} (pid {ProcessId}) had already exited when its recording was finished.")]
    public static partial void GameAlreadyExited(ILogger logger, Exception exception, string project, int processId);
}
