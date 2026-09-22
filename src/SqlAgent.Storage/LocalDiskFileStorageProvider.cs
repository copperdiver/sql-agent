using System.Globalization;
using SqlAgent.Core;

namespace SqlAgent.Storage;

public sealed record LocalStoredFile(string StorageKey, DateTime LastWriteTimeUtc);

public sealed class FileTooLargeException : IOException
{
    public FileTooLargeException(long maxBytes) : base("The file exceeds the configured size limit.") => MaxBytes = maxBytes;
    public long MaxBytes { get; }
}

public sealed class LocalDiskFileStorageProvider : IFileStorageProvider
{
    private readonly string _filesRoot;
    private readonly long _maxBytes;

    public LocalDiskFileStorageProvider(string storeDirectory, long maxBytes = 25 * 1024 * 1024)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(storeDirectory);
        if (maxBytes < 0) throw new ArgumentOutOfRangeException(nameof(maxBytes));
        _filesRoot = Path.GetFullPath(Path.Combine(storeDirectory, "files"));
        _maxBytes = maxBytes;
    }

    public string Key => "local-disk";

    public async Task<StoredFile> SaveAsync(FileUpload upload, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(upload);
        var now = DateTime.UtcNow;
        var relative = $"{now:yyyy}/{now:MM}/{Guid.NewGuid():D}{SafeExtension(upload.FileName)}";
        var path = ResolvePath(relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        try
        {
            await using var output = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None, 64 * 1024,
                FileOptions.Asynchronous | FileOptions.SequentialScan);
            var buffer = new byte[64 * 1024];
            long total = 0;
            int read;
            while ((read = await upload.Content.ReadAsync(buffer.AsMemory(), ct)) != 0)
            {
                total += read;
                if (total > _maxBytes) throw new FileTooLargeException(_maxBytes);
                await output.WriteAsync(buffer.AsMemory(0, read), ct);
            }
            await output.FlushAsync(ct);
            return new StoredFile(relative, relative, total);
        }
        catch
        {
            TryDelete(path);
            throw;
        }
    }

    public Task<Stream?> OpenReadAsync(string storageKey, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        try
        {
            var path = ResolvePath(storageKey);
            if (!File.Exists(path)) return Task.FromResult<Stream?>(null);
            return Task.FromResult<Stream?>(new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read,
                64 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan));
        }
        catch (FileNotFoundException) { return Task.FromResult<Stream?>(null); }
        catch (DirectoryNotFoundException) { return Task.FromResult<Stream?>(null); }
    }

    public Task<bool> DeleteAsync(string storageKey, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        var path = ResolvePath(storageKey);
        if (!File.Exists(path)) return Task.FromResult(false);
        File.Delete(path);
        return Task.FromResult(true);
    }

    public IEnumerable<LocalStoredFile> EnumerateFiles()
    {
        if (!Directory.Exists(_filesRoot)) yield break;
        foreach (var path in Directory.EnumerateFiles(_filesRoot, "*", SearchOption.AllDirectories))
        {
            var relative = Path.GetRelativePath(_filesRoot, path).Replace(Path.DirectorySeparatorChar, '/');
            yield return new LocalStoredFile(relative, File.GetLastWriteTimeUtc(path));
        }
    }

    private string ResolvePath(string storageKey)
    {
        if (string.IsNullOrWhiteSpace(storageKey)) throw new ArgumentException("Storage key is invalid.", nameof(storageKey));
        var normalized = storageKey.Replace('/', Path.DirectorySeparatorChar).Replace('\\', Path.DirectorySeparatorChar);
        var path = Path.GetFullPath(Path.Combine(_filesRoot, normalized));
        if (!path.StartsWith(_filesRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("Storage key is invalid.", nameof(storageKey));
        return path;
    }

    private static string SafeExtension(string? fileName)
    {
        var extension = Path.GetExtension(fileName ?? string.Empty);
        if (extension.Length is < 2 or > 16 || extension[0] != '.') return string.Empty;
        return extension.All(c => char.IsLetterOrDigit(c) || c == '.') ? extension.ToLowerInvariant() : string.Empty;
    }

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); } catch { }
    }
}
