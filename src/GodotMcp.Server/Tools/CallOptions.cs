using System.ComponentModel;

namespace GodotMcp.Server.Tools;

/// <summary>How long call_method waits for the method, a coroutine's awaits included.</summary>
internal sealed record CallOptions(
    [property: Description("How long to wait for the method to return, in milliseconds, 1 to 120000, load-adjusted; 10000 by default.")]
        int? TimeoutMs = null
);
