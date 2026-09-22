using SqlAgent.Core;
using SqlAgent.Storage;

namespace SqlAgent.Tests;

public sealed class LocalDiskFileStorageProviderTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "sql-agent-tests", Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task Saves_under_guid_year_month_path_with_safe_extension_and_stream_copy()
    {
        var provider = new LocalDiskFileStorageProvider(_root);
        await using var content = new MemoryStream("hello"u8.ToArray());

        var stored = await provider.SaveAsync(new FileUpload("../../report.PdF", "application/pdf", content));

        Assert.Matches(@"^\d{4}/\d{2}/[0-9a-fA-F-]{36}\.pdf$", stored.StorageKey);
        Assert.Equal(stored.StorageKey, stored.Url);
        Assert.Equal(5, stored.SizeBytes);
        Assert.Equal("hello", await File.ReadAllTextAsync(Path.Combine(_root, "files", stored.StorageKey.Replace('/', Path.DirectorySeparatorChar))));
    }

    [Fact]
    public async Task Rejects_content_larger_than_exact_limit_without_leaving_a_file()
    {
        var provider = new LocalDiskFileStorageProvider(_root, maxBytes: 5);
        await using var content = new MemoryStream("hello!"u8.ToArray());

        await Assert.ThrowsAsync<FileTooLargeException>(() => provider.SaveAsync(new FileUpload("x.txt", "text/plain", content)));
        Assert.Empty(Directory.Exists(Path.Combine(_root, "files"))
            ? Directory.EnumerateFiles(Path.Combine(_root, "files"), "*", SearchOption.AllDirectories)
            : []);
    }

    [Fact]
    public async Task Missing_read_is_null_and_delete_is_idempotent()
    {
        var provider = new LocalDiskFileStorageProvider(_root);
        Assert.Null(await provider.OpenReadAsync("2026/09/missing.txt"));
        Assert.False(await provider.DeleteAsync("2026/09/missing.txt"));
    }

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }
}
