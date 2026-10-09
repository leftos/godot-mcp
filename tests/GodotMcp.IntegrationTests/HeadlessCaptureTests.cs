using System.Text.Json.Nodes;
using GodotMcp.IntegrationTests.Fixtures;
using GodotMcp.Server.Tools;
using ModelContextProtocol;

namespace GodotMcp.IntegrationTests;

/// <summary>
/// The frame-reading tools on a game run with --headless, whose display server draws no frames: each refuses before it reads
/// the viewport, so the game logs no engine error and goes on answering. Each test runs the InputProbe in a session of its own.
/// </summary>
public sealed class HeadlessCaptureTests : IAsyncLifetime
{
    private const string Refusal =
        "a headless game draws no frames, so there is nothing to capture; read state with get_game_state, get_ui_elements or "
        + "run_script, or run the scene windowed.";
    private const string BaselineName = "headless_probe";

    // A 1x1 PNG, so the baseline compare_screenshot is asked about exists on disk.
    private const string OnePixelPng = "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mNkYAAAAAYAAjCB0C8AAAAASUVORK5CYII=";

    private readonly ProbeProject _probe = new();
    private readonly SessionHarness _harness = new();
    private readonly ProjectTools _project;
    private readonly RuntimeTools _runtime;

    public HeadlessCaptureTests()
    {
        _project = new ProjectTools(_harness.Sessions);
        _runtime = new RuntimeTools(_harness.Sessions, TestCSharp.Unused());
    }

    public async ValueTask InitializeAsync() =>
        await _project.RunProjectAsync(_probe.Directory, engineArgs: ["--headless"], cancellationToken: TestContext.Current.CancellationToken);

    public async ValueTask DisposeAsync()
    {
        await _harness.DisposeAsync();
        _probe.Dispose();
    }

    [Fact(Timeout = TestTimeouts.OwnLaunchMs)]
    public async Task TakeScreenshotRefusesWithoutAnEngineErrorAndTheGameStillAnswers()
    {
        CancellationToken cancellation = TestContext.Current.CancellationToken;
        long cursor = ErrorCursor();

        McpException refused = await Assert.ThrowsAsync<McpException>(() => _runtime.TakeScreenshotAsync(cancellationToken: cancellation));
        JsonNode errors = JsonNode.Parse(_runtime.GetErrors(cursor))!;
        JsonNode state = JsonNode.Parse(await _runtime.GetGameStateAsync(cancellationToken: cancellation))!;

        Assert.Contains(Refusal, refused.Message, StringComparison.Ordinal);
        Assert.Empty(errors["errors"]!.AsArray());
        Assert.IsType<JsonObject>(state);
    }

    [Fact(Timeout = TestTimeouts.OwnLaunchMs)]
    public async Task CaptureFramesRefusesWithoutAnEngineError()
    {
        CancellationToken cancellation = TestContext.Current.CancellationToken;
        long cursor = ErrorCursor();

        McpException refused = await Assert.ThrowsAsync<McpException>(() => _runtime.CaptureFramesAsync([0.1], cancellationToken: cancellation));
        JsonNode errors = JsonNode.Parse(_runtime.GetErrors(cursor))!;

        Assert.Contains(Refusal, refused.Message, StringComparison.Ordinal);
        Assert.Empty(errors["errors"]!.AsArray());
    }

    [Fact(Timeout = TestTimeouts.OwnLaunchMs)]
    public async Task CompareScreenshotRefusesBeforeComparingWithoutAnEngineError()
    {
        CancellationToken cancellation = TestContext.Current.CancellationToken;
        string folder = Path.Combine(_probe.Directory, ".godot", "godot-mcp", "baselines");
        Directory.CreateDirectory(folder);
        await File.WriteAllBytesAsync(Path.Combine(folder, BaselineName + ".png"), Convert.FromBase64String(OnePixelPng), cancellation);
        long cursor = ErrorCursor();

        McpException refused = await Assert.ThrowsAsync<McpException>(() =>
            _runtime.CompareScreenshotAsync(BaselineName, cancellationToken: cancellation)
        );
        JsonNode errors = JsonNode.Parse(_runtime.GetErrors(cursor))!;

        Assert.Contains(Refusal, refused.Message, StringComparison.Ordinal);
        Assert.Empty(errors["errors"]!.AsArray());
    }

    [Fact(Timeout = TestTimeouts.OwnLaunchMs)]
    public async Task SaveScreenshotBaselineRefusesAndSavesNoBaseline()
    {
        CancellationToken cancellation = TestContext.Current.CancellationToken;
        string folder = Path.Combine(_probe.Directory, ".godot", "godot-mcp", "baselines");
        long cursor = ErrorCursor();

        McpException refused = await Assert.ThrowsAsync<McpException>(() =>
            _runtime.SaveScreenshotBaselineAsync(BaselineName, cancellationToken: cancellation)
        );
        JsonNode errors = JsonNode.Parse(_runtime.GetErrors(cursor))!;
        string[] saved = Directory.Exists(folder) ? Directory.GetFiles(folder) : [];

        Assert.Contains(Refusal, refused.Message, StringComparison.Ordinal);
        Assert.Empty(errors["errors"]!.AsArray());
        Assert.Empty(saved);
    }

    /// <summary>The error feed's cursor now, so a test reads only the entries its own calls add.</summary>
    private long ErrorCursor()
    {
        long cursor = 0;
        while (true)
        {
            JsonNode page = JsonNode.Parse(_runtime.GetErrors(cursor, 500))!;
            long next = page["next"]!.GetValue<long>();
            if (next == cursor)
            {
                return cursor;
            }

            cursor = next;
        }
    }
}
