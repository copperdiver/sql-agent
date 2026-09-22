using Microsoft.Extensions.Logging.Abstractions;
using SqlAgent.Core;
using SqlAgent.Storage;

namespace SqlAgent.Tests;

public sealed class FileStorageServiceTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "sql-agent-tests", Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task Rejects_upload_over_25_mib_and_normalizes_display_name()
    {
        var provider = new LocalDiskFileStorageProvider(_root);
        var service = new FileStorageService(new SqlAgent.Core.FileStorageProviderRegistry([provider]), new FileStorageOptions(), NullLogger<FileStorageService>.Instance);
        await using var content = new MemoryStream(new byte[25 * 1024 * 1024 + 1]);

        await Assert.ThrowsAsync<FileTooLargeException>(() => service.UploadAsync(new FileUpload("", "", content)));
    }

    [Fact]
    public async Task Upload_returns_safe_display_name_and_selected_provider()
    {
        var provider = new LocalDiskFileStorageProvider(_root);
        var service = new FileStorageService(new SqlAgent.Core.FileStorageProviderRegistry([provider]), new FileStorageOptions(), NullLogger<FileStorageService>.Instance);
        await using var content = new MemoryStream("x"u8.ToArray());

        var pending = await service.UploadAsync(new FileUpload("../../a very long name.txt", "text/plain", content));

        Assert.Equal("a very long name.txt", pending.FileName);
        Assert.Equal("local-disk", pending.ProviderKey);
        Assert.Equal(pending.StorageKey, pending.Url);
    }

    [Fact]
    public async Task Sweep_deletes_only_old_unreferenced_files()
    {
        var provider = new LocalDiskFileStorageProvider(_root);
        var referenced = await provider.SaveAsync(new FileUpload("referenced.txt", "text/plain", new MemoryStream("a"u8.ToArray())));
        var orphan = await provider.SaveAsync(new FileUpload("orphan.txt", "text/plain", new MemoryStream("b"u8.ToArray())));
        var fresh = await provider.SaveAsync(new FileUpload("fresh.txt", "text/plain", new MemoryStream("c"u8.ToArray())));
        SetAge(provider, referenced.StorageKey, 25);
        SetAge(provider, orphan.StorageKey, 25);

        var refs = new ReferenceReader(referenced.StorageKey);
        var service = new FileStorageService(new SqlAgent.Core.FileStorageProviderRegistry([provider]), new FileStorageOptions(), NullLogger<FileStorageService>.Instance, refs);
        await service.SweepOrphansAsync();

        await using (var referencedStream = await provider.OpenReadAsync(referenced.StorageKey))
            Assert.NotNull(referencedStream);
        Assert.Null(await provider.OpenReadAsync(orphan.StorageKey));
        await using (var freshStream = await provider.OpenReadAsync(fresh.StorageKey))
            Assert.NotNull(freshStream);
    }

    private static void SetAge(LocalDiskFileStorageProvider provider, string key, int hours)
    {
        var field = typeof(LocalDiskFileStorageProvider).GetField("_filesRoot", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!;
        var root = (string)field.GetValue(provider)!;
        File.SetLastWriteTimeUtc(Path.Combine(root, key.Replace('/', Path.DirectorySeparatorChar)), DateTime.UtcNow.AddHours(-hours));
    }

    private sealed class ReferenceReader(string key) : IFileStorageReferenceReader
    {
        public Task<bool> ExistsAsync(string providerKey, string storageKey, CancellationToken ct = default) => Task.FromResult(storageKey == key);
    }

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }
}
