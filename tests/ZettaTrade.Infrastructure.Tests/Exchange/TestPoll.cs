namespace ZettaTrade.Infrastructure.Tests.Exchange;

/// <summary>
/// Shared decision-free bounded poll for asynchronous test conditions
/// (state flags that settle in another thread's timing window).
/// </summary>
internal static class TestPoll
{
    /// <summary>Interval between condition checks while polling.</summary>
    private static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(25);

    /// <summary>
    /// Poll the condition until it holds or the timeout elapses (a
    /// TimeoutException reports the timeout, never a swallowed state).
    /// </summary>
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
