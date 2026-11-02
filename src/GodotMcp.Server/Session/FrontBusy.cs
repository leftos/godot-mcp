using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging;
using ModelContextProtocol;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace GodotMcp.Server.Session;

/// <summary>
/// Tells the MCP front whether a game is live, so the front never swaps servers under a running session: a swap closes the
/// server's stdin, and this server's shutdown stops every game it launched. Level-triggered through
/// <c>notifications/mcp-front/busy</c>, and only with <c>MCP_FRONT</c> set, since without the front an unsolicited
/// notification is a spec violation.
/// </summary>
internal sealed partial class FrontBusy : IDisposable
{
    /// <summary>The front's notification: the front reads it and never forwards it to the client.</summary>
    internal const string Method = "notifications/mcp-front/busy";

    /// <summary>How often the loop looks again while the last state sent was busy, for a game that ended with no tool call.</summary>
    internal static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(2);

    private readonly bool _enabled;
    private readonly Func<IReadOnlyList<string>> _liveNames;
    private readonly Func<JsonObject, CancellationToken, Task> _send;
    private readonly ILogger _logger;
    private readonly SemaphoreSlim _gate = new(1, 1);

    // The state the last successful send recorded; nothing goes out until the live set says otherwise.
    private bool _busy;

    // The loop re-checking while the state sent is busy, under _gate.
    private CancellationTokenSource? _polling;

    // The server the last tool call ran on, which is where a notification goes; a reference read or write is atomic.
    private McpServer? _server;
    private volatile bool _disposed;

    /// <summary>A source over the registry's live sessions, sending through the server a tool call last ran on.</summary>
    /// <param name="registry">The sessions whose liveness decides the state.</param>
    /// <param name="front">The <c>MCP_FRONT</c> value: a non-empty one enables the notification.</param>
    /// <param name="logger">Where a failed send is reported.</param>
    internal FrontBusy(SessionRegistry registry, string? front, ILogger<FrontBusy> logger)
    {
        _enabled = !string.IsNullOrEmpty(front);
        _liveNames = registry.LiveSessionNames;
        _send = SendThroughCapturedServerAsync;
        _logger = logger;
    }

    /// <summary>The same decision over a stand-in live-names source and a stand-in send action.</summary>
    /// <param name="enabled">Whether the front is there at all.</param>
    /// <param name="liveNames">The live sessions' names, as the registry would list them.</param>
    /// <param name="send">Where a payload goes; throws to model a failed send.</param>
    /// <param name="logger">Where a failed send is reported.</param>
    internal FrontBusy(bool enabled, Func<IReadOnlyList<string>> liveNames, Func<JsonObject, CancellationToken, Task> send, ILogger logger)
    {
        _enabled = enabled;
        _liveNames = liveNames;
        _send = send;
        _logger = logger;
    }

    /// <summary>
    /// The server's call-tool filter: keeps the server this call runs on, then, once the tool returns, sends the state the
    /// call may have changed before the response leaves, so busy:true is on the wire ahead of the run_project that made it so.
    /// </summary>
    internal static McpRequestHandler<CallToolRequestParams, CallToolResult> Filter(McpRequestHandler<CallToolRequestParams, CallToolResult> next) =>
        async (context, cancellationToken) =>
        {
            var busy = context.Services?.GetService(typeof(FrontBusy)) as FrontBusy;
            if (busy is not null && context.Server is { } server)
            {
                busy.Capture(server);
            }

            CallToolResult result = await next(context, cancellationToken).ConfigureAwait(false);
            if (busy is not null)
            {
                await busy.RefreshAsync(cancellationToken).ConfigureAwait(false);
            }

            return result;
        };

    /// <summary>
    /// Sends the front the current state when it differs from the last one a send recorded. Serialized, so two refreshes
    /// never put the two states on the wire out of order; a failed send records nothing, so the next refresh tries again.
    /// </summary>
    /// <param name="cancellationToken">Abandons the wait for the gate and the send itself.</param>
    internal async Task RefreshAsync(CancellationToken cancellationToken)
    {
        if (!_enabled)
        {
            return;
        }

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            IReadOnlyList<string> live = _liveNames();
            bool busy = live.Count > 0;
            if (_disposed || busy == _busy)
            {
                return;
            }

            try
            {
                await _send(Payload(busy, live), cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception e)
            {
                BusySendFailed(_logger, e);
                return;
            }

            _busy = busy;
            Watch(busy);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>The front's payload: the state, and while busy the live sessions' names, so the front logs why it waits.</summary>
    /// <param name="busy">Whether a session is live.</param>
    /// <param name="live">The live sessions' names, in the order they go into the reason.</param>
    internal static JsonObject Payload(bool busy, IReadOnlyList<string> live)
    {
        JsonObject payload = new() { ["busy"] = busy };
        if (busy)
        {
            payload["reason"] = $"godot sessions live: {string.Join(", ", live)}";
        }

        return payload;
    }

    /// <summary>Ends the loop; a send still in flight belongs to the exit, not to the front.</summary>
    public void Dispose()
    {
        _disposed = true;
        _polling?.Cancel();
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Telling the MCP front the busy state failed; the next refresh tries again.")]
    private static partial void BusySendFailed(ILogger logger, Exception error);

    private void Capture(McpServer server)
    {
        if (_enabled)
        {
            _server = server;
        }
    }

    private Task SendThroughCapturedServerAsync(JsonObject payload, CancellationToken cancellationToken)
    {
        if (_server is not { } server)
        {
            throw new InvalidOperationException("no tool call has run yet, so there is no server to send the busy state on");
        }

        return server.SendNotificationAsync(Method, payload, McpJsonUtilities.DefaultOptions, cancellationToken);
    }

    // Starts the re-check loop once the state sent is busy, and ends it once it is not; runs under _gate.
    private void Watch(bool busy)
    {
        if (!busy)
        {
            CancellationTokenSource? idle = _polling;
            _polling = null;
            idle?.Cancel();
            return;
        }

        if (_disposed || _polling is not null)
        {
            return;
        }

        CancellationTokenSource polling = new();
        _polling = polling;
        _ = Task.Run(() => PollAsync(polling));
    }

    // Looks again every PollInterval while the state sent is busy, so a game that ends with no tool call frees the front.
    private async Task PollAsync(CancellationTokenSource polling)
    {
        using (polling)
        {
            using PeriodicTimer timer = new(PollInterval);
            try
            {
                while (await timer.WaitForNextTickAsync(polling.Token).ConfigureAwait(false))
                {
                    await RefreshAsync(polling.Token).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException)
            {
                // The state sent is no longer busy, or the server is shutting down.
            }
        }
    }
}
