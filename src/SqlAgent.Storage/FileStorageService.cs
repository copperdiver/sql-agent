using Microsoft.Extensions.Logging;
using SqlAgent.Core;

namespace SqlAgent.Storage;

public sealed record PendingFileAttachment(string FileName, string ContentType, long SizeBytes,
    string ProviderKey, string StorageKey, string Url);

public sealed class FileRejectedException : Exception
{
    public FileRejectedException() : base("The file could not be stored.") { }
    public string ErrorCode => "file_rejected";
}

public interface IFileStorageReferenceReader
{
    Task<bool> ExistsAsync(string providerKey, string storageKey, CancellationToken ct = default);
}

public sealed class FileStorageService(
    IFileStorageProviderRegistry registry,
    FileStorageOptions options,
    ILogger<FileStorageService> logger,
    IFileStorageReferenceReader? references = null)
{
    public async Task<PendingFileAttachment> UploadAsync(FileUpload upload, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(upload);
        try
        {
            var provider = registry.Get(options.Provider);
            await using var bounded = new BoundedReadStream(upload.Content, options.MaxBytes);
            var stored = await provider.SaveAsync(upload with { Content = bounded }, ct);
            return new PendingFileAttachment(NormalizeFileName(upload.FileName),
                string.IsNullOrWhiteSpace(upload.ContentType) ? "application/octet-stream" : upload.ContentType,
                stored.SizeBytes, provider.Key, stored.StorageKey, stored.Url);
        }
        catch (FileTooLargeException) { throw; }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception ex)
        {
            logger.LogError(ex, "File storage provider {ProviderKey} failed while storing an upload.", options.Provider);
            throw new FileRejectedException();
        }
    }

    public async Task<bool> DeletePendingAsync(PendingFileAttachment pending, CancellationToken ct = default)
    {
        try { return await registry.Get(pending.ProviderKey).DeleteAsync(pending.StorageKey, ct); }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogError(ex, "File storage provider {ProviderKey} failed while deleting pending storage.", pending.ProviderKey);
            return false;
        }
    }

    public async Task SweepOrphansAsync(CancellationToken ct = default)
    {
        if (references is null) return;
        IFileStorageProvider provider;
        try
        {
            provider = registry.Get(options.Provider);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "File storage provider {ProviderKey} could not be selected for orphan cleanup.", options.Provider);
            return;
        }
        if (provider is not LocalDiskFileStorageProvider local) return;
        foreach (var file in local.EnumerateFiles())
        {
            ct.ThrowIfCancellationRequested();
            if (file.LastWriteTimeUtc > DateTime.UtcNow.AddHours(-24)) continue;
            if (await references.ExistsAsync(provider.Key, file.StorageKey, ct)) continue;
            try { await provider.DeleteAsync(file.StorageKey, ct); }
            catch (Exception ex) when (ex is not OperationCanceledException)
            { logger.LogWarning(ex, "Could not remove an orphaned file from provider {ProviderKey}.", provider.Key); }
        }
    }

    private static string NormalizeFileName(string? value)
    {
        var name = Path.GetFileName(value ?? string.Empty);
        if (string.IsNullOrWhiteSpace(name)) name = "file";
        var invalid = Path.GetInvalidFileNameChars();
        name = new string(name.Select(c => invalid.Contains(c) || char.IsControl(c) ? '_' : c).ToArray()).Trim();
        if (string.IsNullOrWhiteSpace(name)) name = "file";
        return name.Length <= 255 ? name : name[..255];
    }

    private sealed class BoundedReadStream(Stream inner, long maxBytes) : Stream
    {
        private long _read;
        public override bool CanRead => inner.CanRead;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => _read; set => throw new NotSupportedException(); }
        public override int Read(byte[] buffer, int offset, int count) => ReadAsync(buffer.AsMemory(offset, count)).GetAwaiter().GetResult();
        public override int Read(Span<byte> buffer)
        {
            var temp = new byte[buffer.Length];
            var count = Read(temp, 0, temp.Length);
            temp.AsSpan(0, count).CopyTo(buffer);
            return count;
        }
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            var count = await inner.ReadAsync(buffer, cancellationToken);
            _read += count;
            if (_read > maxBytes) throw new FileTooLargeException(maxBytes);
            return count;
        }
        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
            ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();
        protected override void Dispose(bool disposing) { if (disposing) inner.Dispose(); base.Dispose(disposing); }
        public override ValueTask DisposeAsync() { GC.SuppressFinalize(this); return ValueTask.CompletedTask; }
        public override void Flush() => throw new NotSupportedException();
        public override Task FlushAsync(CancellationToken cancellationToken) => Task.CompletedTask;
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override void Write(ReadOnlySpan<byte> buffer) => throw new NotSupportedException();
        public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) => throw new NotSupportedException();
        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }
}
