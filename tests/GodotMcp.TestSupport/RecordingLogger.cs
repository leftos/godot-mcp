using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;

namespace GodotMcp.TestSupport;

/// <summary>
/// A logger that keeps every message it is given, formatted, with its level, for a test to read back.
/// </summary>
/// <remarks>
/// Safe to log to from any thread. It is enabled at every level and keeps no scope, event id or exception.
/// </remarks>
public class RecordingLogger : ILogger
{
    private readonly ConcurrentQueue<(LogLevel Level, string Text)> _entries = new();

    /// <summary>Every message logged so far, with its level, in order.</summary>
    public IReadOnlyList<(LogLevel Level, string Text)> Entries => [.. _entries];

    /// <summary>Every message logged so far, at any level, in order.</summary>
    public IReadOnlyList<string> Messages => [.. _entries.Select(entry => entry.Text)];

    /// <summary>The messages logged at <see cref="LogLevel.Warning"/>, in order.</summary>
    public IReadOnlyList<string> Warnings => [.. _entries.Where(entry => entry.Level == LogLevel.Warning).Select(entry => entry.Text)];

    public IDisposable? BeginScope<TState>(TState state)
        where TState : notnull => null;

    public bool IsEnabled(LogLevel logLevel) => true;

    public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
        _entries.Enqueue((logLevel, formatter(state, exception)));
}

/// <summary>A <see cref="RecordingLogger"/> for a category, for a caller that takes an <see cref="ILogger{TCategory}"/>.</summary>
public sealed class RecordingLogger<TCategory> : RecordingLogger, ILogger<TCategory>;
