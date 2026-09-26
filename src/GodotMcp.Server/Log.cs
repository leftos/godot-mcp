using Microsoft.Extensions.Logging;

namespace GodotMcp.Server;

/// <summary>Every message the server logs; all of it goes to stderr.</summary>
internal static partial class Log
{
    [LoggerMessage(Level = LogLevel.Information, Message = "Bridge connection ended: {Reason}")]
    public static partial void BridgeConnectionEnded(ILogger logger, string reason);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Dropped a {Length}-byte bridge frame without a numeric id.")]
    public static partial void DroppedFrameWithoutId(ILogger logger, int length);

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

    [LoggerMessage(Level = LogLevel.Warning, Message = "Removing the override.cfg of {Project} after Godot exited failed.")]
    public static partial void OverrideRemovalFailed(ILogger logger, Exception exception, string project);

    [LoggerMessage(Level = LogLevel.Information, Message = "Attached to a game on {Project}; the bridge is connected.")]
    public static partial void Attached(ILogger logger, string project);

    [LoggerMessage(Level = LogLevel.Information, Message = "Detached from the game on {Project}; it keeps running.")]
    public static partial void Detached(ILogger logger, string project);
}
