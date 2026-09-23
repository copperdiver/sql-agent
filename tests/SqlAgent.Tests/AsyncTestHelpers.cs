namespace SqlAgent.Tests;

/// <summary>
/// Shared polling helper for tests that wait on application state rather than a bUnit render.
/// Originally written inline in <c>WorkspaceTests</c> (SQL tab cancel/re-run tests); hoisted here so
/// other test files (<c>ChatPageTests</c> among them) do not need their own copy.
/// </summary>
internal static class AsyncTestHelpers
{
    public static async Task WaitForConditionAsync(Func<bool> predicate, int timeoutMs = 5000)
    {
        var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        while (!predicate())
        {
            if (DateTime.UtcNow > deadline)
                throw new TimeoutException("Condition was not met within the timeout.");
            await Task.Delay(10);
        }
    }
}
