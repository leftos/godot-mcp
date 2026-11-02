using System.Text.Json.Nodes;
using GodotMcp.Server.Session;
using Microsoft.Extensions.Logging.Abstractions;

namespace GodotMcp.Tests.Session;

/// <summary>The busy notification's decision: when it sends, what it sends, and how a failed send is retried.</summary>
public sealed class FrontBusyTests : IDisposable
{
    private readonly Lock _namesLock = new();
    private readonly List<string> _names = [];
    private readonly List<string> _sent = [];
    private readonly List<FrontBusy> _sources = [];
    private bool _failNextSend;

    public void Dispose()
    {
        foreach (FrontBusy source in _sources)
        {
            source.Dispose();
        }
    }

    [Fact]
    public async Task ADisabledSourceSendsNothing()
    {
        FrontBusy source = Create(enabled: false);
        Live("a");

        await source.RefreshAsync(TestContext.Current.CancellationToken);

        Assert.Empty(_sent);
    }

    [Fact]
    public async Task NoLiveSessionAtFirstSendsNothing()
    {
        FrontBusy source = Create();

        await source.RefreshAsync(TestContext.Current.CancellationToken);

        Assert.Empty(_sent);
    }

    [Fact]
    public async Task TheFirstLiveSessionSendsBusyWithItsName()
    {
        FrontBusy source = Create();
        Live("a");

        await source.RefreshAsync(TestContext.Current.CancellationToken);

        Assert.Equal(["""{"busy":true,"reason":"godot sessions live: a"}"""], _sent);
    }

    [Fact]
    public async Task ASecondRefreshWithTheSameSessionsSendsNothing()
    {
        FrontBusy source = Create();
        Live("a");
        await source.RefreshAsync(TestContext.Current.CancellationToken);

        await source.RefreshAsync(TestContext.Current.CancellationToken);

        Assert.Single(_sent);
    }

    [Fact]
    public async Task TheLastSessionEndingSendsNotBusyWithoutAReason()
    {
        FrontBusy source = Create();
        Live("a");
        await source.RefreshAsync(TestContext.Current.CancellationToken);
        Live();

        await source.RefreshAsync(TestContext.Current.CancellationToken);

        Assert.Equal(["""{"busy":true,"reason":"godot sessions live: a"}""", """{"busy":false}"""], _sent);
    }

    [Fact]
    public async Task AFailedSendIsRetriedByTheNextRefresh()
    {
        FrontBusy source = Create();
        Live("a");
        _failNextSend = true;

        await source.RefreshAsync(TestContext.Current.CancellationToken);
        Assert.Empty(_sent);

        await source.RefreshAsync(TestContext.Current.CancellationToken);
        Assert.Equal(["""{"busy":true,"reason":"godot sessions live: a"}"""], _sent);
    }

    private FrontBusy Create(bool enabled = true)
    {
        FrontBusy source = new(
            enabled,
            () =>
            {
                lock (_namesLock)
                {
                    return [.. _names];
                }
            },
            (payload, _) =>
            {
                if (_failNextSend)
                {
                    _failNextSend = false;
                    throw new InvalidOperationException("the front is gone");
                }

                _sent.Add(payload.ToJsonString());
                return Task.CompletedTask;
            },
            NullLogger.Instance
        );
        _sources.Add(source);
        return source;
    }

    private void Live(params string[] names)
    {
        lock (_namesLock)
        {
            _names.Clear();
            _names.AddRange(names);
        }
    }
}
