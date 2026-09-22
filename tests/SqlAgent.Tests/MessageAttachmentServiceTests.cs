using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using SqlAgent.Storage;

namespace SqlAgent.Tests;

public sealed class MessageAttachmentServiceTests : IDisposable
{
    private readonly SqliteConnection _connection = new("Data Source=:memory:");
    private readonly SqlAgentDbContext _db;

    public MessageAttachmentServiceTests()
    {
        _connection.Open();
        _db = new SqlAgentDbContext(new DbContextOptionsBuilder<SqlAgentDbContext>()
            .UseSqlite(_connection).Options);
        _db.Database.EnsureCreated();
    }

    [Fact]
    public void Binding_preserves_display_metadata_and_keeps_locator_data_internal()
    {
        var service = new MessageAttachmentService(_db);
        var messageId = Guid.NewGuid();
        var fileId = Guid.NewGuid();

        var rows = service.Bind(messageId,
            [new ChatFileRef(fileId, "notes.txt", "text/plain", 7, $"/files/{fileId}")],
            DateTime.UtcNow);

        var row = Assert.Single(rows);
        Assert.Equal(messageId, row.ChatMessageId);
        Assert.Equal("notes.txt", row.FileName);
        Assert.Equal("text/plain", row.ContentType);
        Assert.Equal(7, row.SizeBytes);
    }

    [Fact]
    public void Binding_more_than_ten_files_uses_stable_file_rejected_code()
    {
        var service = new MessageAttachmentService(_db);
        var files = Enumerable.Range(0, 11)
            .Select(i => new ChatFileRef(Guid.NewGuid(), $"{i}.txt", "text/plain", 1, $"/files/{i}"))
            .ToArray();

        var exception = Assert.Throws<FileRejectedException>(() => service.Bind(
            Guid.NewGuid(), files, DateTime.UtcNow));

        Assert.Equal("file_rejected", exception.ErrorCode);
    }

    [Fact]
    public void Pending_upload_reference_keeps_provider_locator_inside_bound_metadata()
    {
        var service = new MessageAttachmentService(_db);
        var pending = new PendingFileAttachment(
            "notes.txt", "text/plain", 7, "local-disk", "2026/09/blob.txt", "2026/09/blob.txt");

        var reference = MessageAttachmentService.CreateReference(pending);
        var row = Assert.Single(service.Bind(Guid.NewGuid(), [reference], DateTime.UtcNow));

        Assert.Equal("local-disk", row.ProviderKey);
        Assert.Equal("2026/09/blob.txt", row.StorageKey);
        Assert.StartsWith("/files/", row.Url);
    }

    public void Dispose()
    {
        _db.Dispose();
        _connection.Dispose();
    }
}
