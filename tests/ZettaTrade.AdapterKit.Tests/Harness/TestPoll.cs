namespace ZettaTrade.AdapterKit.Tests.Harness;

/// <summary>
/// Kit-private bounded poll for asynchronous test conditions (state that
/// settles in another thread's timing window). Mirrors the shared
/// <c>TestPoll</c> helper of the Infrastructure suite: the kit is
/// self-contained, so adapter teams never touch that other test project.
/// </summary>
internal static class TestPoll
{
    /// <summary>Interval between condition checks while polling.</summary>
    private static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(25);

    /// <summary>Poll the condition until it holds or the timeout elapses.</summary>
    /// <param name="condition">The condition to wait for.</param>
    /// <param name="timeout">The bounded wait; elapsed, a TimeoutException reports the timeout, never a swallowed state.</param>
    public static async Task PollUntilAsync(Func<bool> condition, TimeSpan timeout)
    {
        var start = DateTime.UtcNow;
        while (!condition())
        {
            if (DateTime.UtcNow - start > timeout)
            {
                throw new TimeoutException("The polled condition did not settle within the timeout.");
            }

            await Task.Delay(PollInterval);
        }
    }
}
