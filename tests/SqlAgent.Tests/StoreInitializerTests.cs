using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Logging;
using SqlAgent.Core;
using SqlAgent.Storage;

namespace SqlAgent.Tests;

public sealed class StoreInitializerTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "sql-agent-startup-tests", Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task Startup_sweep_deletes_old_orphans_but_respects_the_24_hour_floor()
    {
        var provider = new LocalDiskFileStorageProvider(_root);
        var old = await provider.SaveAsync(new FileUpload("old.txt", "text/plain", new MemoryStream("old"u8.ToArray())));
        var fresh = await provider.SaveAsync(new FileUpload("fresh.txt", "text/plain", new MemoryStream("fresh"u8.ToArray())));
        SetAge(provider, old.StorageKey, 25);
        var storage = new FileStorageService(
            new FileStorageProviderRegistry([provider]),
            new FileStorageOptions(),
            NullLogger<FileStorageService>.Instance,
            new NoReferences());

        await StoreInitializer.SweepOrphansAsync(storage, NullLogger.Instance);

        Assert.Null(await provider.OpenReadAsync(old.StorageKey));
        await using var freshStream = await provider.OpenReadAsync(fresh.StorageKey);
        Assert.NotNull(freshStream);
    }

    [Fact]
    public async Task Startup_sweep_failure_is_logged_as_a_warning_and_does_not_throw()
    {
        var provider = new LocalDiskFileStorageProvider(_root);
        var old = await provider.SaveAsync(new FileUpload("old.txt", "text/plain", new MemoryStream("old"u8.ToArray())));
        SetAge(provider, old.StorageKey, 25);
        var storage = new FileStorageService(
            new FileStorageProviderRegistry([provider]),
            new FileStorageOptions(),
            NullLogger<FileStorageService>.Instance,
            new ThrowingReferences());
        var logs = new RecordingLoggerProvider();
        using var loggerFactory = LoggerFactory.Create(builder => builder.AddProvider(logs));

        await StoreInitializer.SweepOrphansAsync(storage, loggerFactory.CreateLogger("StoreInitializer"));

        Assert.Contains(logs.Records, record => record.Level == LogLevel.Warning);
    }

    private static void SetAge(LocalDiskFileStorageProvider provider, string key, int hours)
    {
        var field = typeof(LocalDiskFileStorageProvider).GetField(
            "_filesRoot", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!;
        var root = (string)field.GetValue(provider)!;
        File.SetLastWriteTimeUtc(
            Path.Combine(root, key.Replace('/', Path.DirectorySeparatorChar)),
            DateTime.UtcNow.AddHours(-hours));
    }

    private sealed class NoReferences : IFileStorageReferenceReader
    {
        public Task<bool> ExistsAsync(string providerKey, string storageKey, CancellationToken ct = default) =>
            Task.FromResult(false);
    }

    private sealed class ThrowingReferences : IFileStorageReferenceReader
    {
        public Task<bool> ExistsAsync(string providerKey, string storageKey, CancellationToken ct = default) =>
            throw new InvalidOperationException("reference lookup failed");
    }

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }
}
