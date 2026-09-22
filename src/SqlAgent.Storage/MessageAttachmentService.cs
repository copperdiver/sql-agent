using Microsoft.EntityFrameworkCore;
using SqlAgent.Core;

namespace SqlAgent.Storage;

public record FileDownload(Stream Content, string FileName, string ContentType, long SizeBytes);

/// <summary>
/// Binds public file references to persisted metadata while keeping provider locators inside the storage
/// boundary. It also provides the reference lookup used by the provider's orphan-sweep seam.
/// </summary>
public sealed class MessageAttachmentService(
    SqlAgentDbContext db,
    FileStorageOptions? options = null,
    IFileStorageProviderRegistry? providers = null)
    : IFileStorageReferenceReader
{
    private readonly int maxAttachments = options?.MaxAttachmentsPerMessage ?? 10;
    private readonly string defaultProvider = options?.Provider ?? "local-disk";

    public IReadOnlyList<MessageAttachment> Bind(
        Guid messageId, IReadOnlyList<ChatFileRef>? files, DateTime createdAt)
    {
        var refs = files ?? [];
        if (refs.Count > maxAttachments)
            throw new FileRejectedException();

        return refs.Select(file =>
        {
            var id = file.Id == Guid.Empty ? Guid.NewGuid() : file.Id;
            return new MessageAttachment
            {
                Id = id,
                ChatMessageId = messageId,
                FileName = file.FileName,
                ContentType = string.IsNullOrWhiteSpace(file.ContentType)
                    ? "application/octet-stream" : file.ContentType,
                SizeBytes = file.SizeBytes,
                ProviderKey = string.IsNullOrWhiteSpace(file.ProviderKey) ? defaultProvider : file.ProviderKey,
                StorageKey = file.StorageKey,
                Url = string.IsNullOrWhiteSpace(file.Url) ? $"/files/{id}" : file.Url,
                CreatedAt = createdAt,
            };
        }).ToList();
    }

    /// <summary>Creates a public reference for a completed provider upload without exposing its locators.</summary>
    public static ChatFileRef CreateReference(PendingFileAttachment pending)
    {
        ArgumentNullException.ThrowIfNull(pending);
        var id = Guid.NewGuid();
        return new ChatFileRef(id, pending.FileName, pending.ContentType, pending.SizeBytes, $"/files/{id}")
        {
            ProviderKey = pending.ProviderKey,
            StorageKey = pending.StorageKey,
        };
    }

    public Task<bool> ExistsAsync(string providerKey, string storageKey, CancellationToken ct = default) =>
        db.MessageAttachments.AsNoTracking()
            .AnyAsync(a => a.ProviderKey == providerKey && a.StorageKey == storageKey, ct);

    public async Task<FileDownload?> OpenDownloadAsync(Guid attachmentId, CancellationToken ct = default)
    {
        var attachment = await db.MessageAttachments.AsNoTracking()
            .SingleOrDefaultAsync(a => a.Id == attachmentId, ct);
        if (attachment is null || providers is null) return null;

        try
        {
            var provider = providers.Get(attachment.ProviderKey);
            var content = await provider.OpenReadAsync(attachment.StorageKey, ct);
            return content is null
                ? null
                : new FileDownload(content, attachment.FileName, attachment.ContentType, attachment.SizeBytes);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            // A missing or unavailable provider/blob is intentionally indistinguishable from a missing
            // metadata row at the HTTP boundary. Provider details must never become a response body.
            return null;
        }
    }
}
