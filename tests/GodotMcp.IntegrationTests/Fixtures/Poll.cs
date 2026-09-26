namespace GodotMcp.IntegrationTests.Fixtures;

internal static class Poll
{
    /// <summary>Waits until <paramref name="condition"/> holds, checking every 100 ms; false when <paramref name="within"/> passes first.</summary>
    public static async Task<bool> UntilAsync(Func<bool> condition, TimeSpan within, CancellationToken cancellationToken)
    {
        DateTime deadline = DateTime.UtcNow + within;
        while (DateTime.UtcNow < deadline)
        {
            if (condition())
            {
                return true;
            }

            await Task.Delay(100, cancellationToken);
        }

        return condition();
    }
}
