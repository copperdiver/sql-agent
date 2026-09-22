using SqlAgent.Core;

namespace SqlAgent.Storage;

internal sealed class FileStorageProviderRegistry(IEnumerable<IFileStorageProvider> providers) : IFileStorageProviderRegistry
{
    private readonly IReadOnlyDictionary<string, IFileStorageProvider> _providers =
        providers.ToDictionary(p => p.Key, StringComparer.Ordinal);

    public IFileStorageProvider Get(string key) => _providers.TryGetValue(key, out var provider)
        ? provider
        : throw new NotSupportedException($"No file storage provider registered for {key}.");
}
