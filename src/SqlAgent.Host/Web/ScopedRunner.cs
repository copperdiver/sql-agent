namespace SqlAgent.Host.Web;

/// <summary>
/// Runs one user action inside a fresh DI scope.
///
/// A Blazor circuit lives as long as the browser tab — hours. Scoped services are bound to the circuit,
/// so injecting SqlAgentDbContext straight into a component would keep one context alive that whole
/// time, accumulating tracked entities and serving stale reads. Scoping per action keeps the Storage
/// services exactly as they are, which the alternative (moving all seven onto IDbContextFactory) would not.
/// </summary>
public sealed class ScopedRunner(IServiceScopeFactory scopeFactory)
{
    public async Task<TResult> RunAsync<TService, TResult>(Func<TService, Task<TResult>> action)
        where TService : notnull
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        return await action(scope.ServiceProvider.GetRequiredService<TService>());
    }

    public async Task RunAsync<TService>(Func<TService, Task> action)
        where TService : notnull
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        await action(scope.ServiceProvider.GetRequiredService<TService>());
    }
}
