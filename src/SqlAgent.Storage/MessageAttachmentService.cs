using Microsoft.EntityFrameworkCore;
using SqlAgent.Core;

namespace SqlAgent.Storage;

/// <summary>
/// Binds public file references to persisted metadata while keeping provider locators inside the storage
/// boundary. It also provides the reference lookup used by the provider's orphan-sweep seam.
/// </summary>
public sealed class MessageAttachmentService(SqlAgentDbContext db, FileStorageOptions? options = null)
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
}
