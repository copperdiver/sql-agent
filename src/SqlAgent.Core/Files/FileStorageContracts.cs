namespace SqlAgent.Core;

public interface IFileStorageProvider
{
    string Key { get; }
    Task<StoredFile> SaveAsync(FileUpload upload, CancellationToken ct = default);
    Task<Stream?> OpenReadAsync(string storageKey, CancellationToken ct = default);
    Task<bool> DeleteAsync(string storageKey, CancellationToken ct = default);
}

public interface IFileStorageProviderRegistry
{
    IFileStorageProvider Get(string key);
}

public class FileStorageProviderRegistry : IFileStorageProviderRegistry
{
    private readonly IReadOnlyDictionary<string, IFileStorageProvider> _providers;

    public FileStorageProviderRegistry(IEnumerable<IFileStorageProvider> providers)
        => _providers = providers.ToDictionary(p => p.Key, StringComparer.Ordinal);

    public IFileStorageProvider Get(string key)
        => _providers.TryGetValue(key, out var provider)
            ? provider
            : throw new NotSupportedException($"No file storage provider registered for {key}.");
}

public record FileUpload(string FileName, string ContentType, Stream Content);
public record StoredFile(string StorageKey, string Url, long SizeBytes);
public record LlmFileAttachment(string FileName, string ContentType, string Url);
public record FileStorageOptions(string Provider = "local-disk", long MaxBytes = 25 * 1024 * 1024,
    int MaxAttachmentsPerMessage = 10);
