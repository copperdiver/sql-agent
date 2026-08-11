namespace SqlAgent.Tests;

/// <summary>
/// Shared bUnit polling helper. bunit 1.40.0 ships its own <c>WaitForStateAsync</c> in two assemblies
/// (Bunit.Core.dll and Bunit.Web.dll), and since the <c>bunit</c> meta-package references both, the
/// extension-method lookup is ambiguous and the compiler drops it from candidates entirely (confirmed
/// with a throwaway diagnostic file: calling the type name directly gives CS0433 "exists in both ...
/// Bunit.Core ... and ... Bunit.Web", while the extension-method call site just reports CS1061 "not
/// found"). This is a minimal local stand-in — same non-blocking polling idea, no dependency on the
/// ambiguous type. Originally written inline in <c>WorkspaceTests</c> (SQL tab cancel/re-run tests);
/// hoisted here so <c>WorkspaceChatTests</c> (chat tab in-flight/guard tests) does not need its own copy.
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
