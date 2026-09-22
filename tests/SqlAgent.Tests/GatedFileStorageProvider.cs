using SqlAgent.Core;
using SqlAgent.Storage;

namespace SqlAgent.Tests;

/// <summary>A local-disk provider with a deterministic gate for upload-race tests.</summary>
internal sealed class GatedFileStorageProvider : IFileStorageProvider
{
    private readonly LocalDiskFileStorageProvider _inner;
    private TaskCompletionSource<object?>? _gate;
    private TaskCompletionSource<object?>? _started;
    private TaskCompletionSource<object?>? _deleteStarted;

    public GatedFileStorageProvider(string root) => _inner = new(root);

    public string Key => _inner.Key;

    public void HoldUpload()
    {
        _gate = new(TaskCreationOptions.RunContinuationsAsynchronously);
        _started = new(TaskCreationOptions.RunContinuationsAsynchronously);
        _deleteStarted = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    public Task WaitForUploadStartAsync() =>
        _started?.Task ?? throw new InvalidOperationException("Upload gate is not armed.");

    public void ReleaseUpload() => _gate?.TrySetResult(null);

    public bool DeleteStarted => _deleteStarted?.Task.IsCompleted == true;

    public Task WaitForDeleteStartAsync() =>
        _deleteStarted?.Task ?? throw new InvalidOperationException("Upload gate is not armed.");

    public async Task<StoredFile> SaveAsync(FileUpload upload, CancellationToken ct = default)
    {
        var gate = _gate;
        if (gate is not null)
        {
            _started!.TrySetResult(null);
            await gate.Task.WaitAsync(ct);
            _gate = null;
            _started = null;
        }

        return await _inner.SaveAsync(upload, ct);
    }

    public Task<Stream?> OpenReadAsync(string storageKey, CancellationToken ct = default) =>
        _inner.OpenReadAsync(storageKey, ct);

    public Task<bool> DeleteAsync(string storageKey, CancellationToken ct = default) =>
        DeleteTrackedAsync(storageKey, ct);

    private async Task<bool> DeleteTrackedAsync(string storageKey, CancellationToken ct)
    {
        _deleteStarted?.TrySetResult(null);
        return await _inner.DeleteAsync(storageKey, ct);
    }
}
