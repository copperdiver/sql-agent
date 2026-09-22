using SqlAgent.Core;

namespace SqlAgent.Tests;

file sealed class FakeFileStorageProvider(string key) : IFileStorageProvider
{
    public string Key => key;

    public Task<StoredFile> SaveAsync(FileUpload upload, CancellationToken ct = default)
        => throw new NotSupportedException();

    public Task<Stream?> OpenReadAsync(string storageKey, CancellationToken ct = default)
        => throw new NotSupportedException();

    public Task<bool> DeleteAsync(string storageKey, CancellationToken ct = default)
        => throw new NotSupportedException();
}

public class FileStorageContractsTests
{
    [Fact]
    public void Registry_resolves_provider_by_key()
    {
        var provider = new FakeFileStorageProvider("local-disk");
        var registry = new FileStorageProviderRegistry([provider]);

        Assert.Same(provider, registry.Get("local-disk"));
    }

    [Fact]
    public void Registry_throws_stable_exception_for_unknown_key()
    {
        var registry = new FileStorageProviderRegistry([
            new FakeFileStorageProvider("local-disk")
        ]);

        var exception = Assert.Throws<NotSupportedException>(() => registry.Get("missing"));

        Assert.Equal("No file storage provider registered for missing.", exception.Message);
    }

    [Fact]
    public void Default_options_use_25_mib_and_ten_attachments()
    {
        var options = new FileStorageOptions();

        Assert.Equal("local-disk", options.Provider);
        Assert.Equal(25 * 1024 * 1024, options.MaxBytes);
        Assert.Equal(10, options.MaxAttachmentsPerMessage);
    }

    [Fact]
    public void Llm_request_keeps_three_argument_constructor_and_defaults_attachments_to_empty()
    {
        var request = new LlmSqlRequest("question", DatabaseProviderType.Postgres, "schema");

        Assert.Empty(request.Attachments);
    }
}
