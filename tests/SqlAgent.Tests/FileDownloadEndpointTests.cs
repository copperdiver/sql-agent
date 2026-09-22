using System.Net;
using System.Text;
using Microsoft.Extensions.DependencyInjection;
using SqlAgent.Core;
using SqlAgent.Storage;

namespace SqlAgent.Tests;

public sealed class FileDownloadEndpointTests : IClassFixture<WebTestHost>
{
    private readonly WebTestHost _host;

    public FileDownloadEndpointTests(WebTestHost host) => _host = host;

    [Fact]
    public async Task Missing_attachment_returns_not_found()
    {
        var client = await AuthenticatedClientAsync();

        var response = await client.GetAsync($"/files/{Guid.NewGuid()}");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task Download_requires_the_existing_token_middleware()
    {
        var response = await _host.NewClient().GetAsync($"/files/{Guid.NewGuid()}");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Authenticated_download_streams_content_with_hardened_attachment_headers()
    {
        var id = await SeedAttachmentAsync("résumé \"Q\".txt", "text/plain", "hello");
        var client = await AuthenticatedClientAsync();

        using var response = await client.GetAsync($"/files/{id}", HttpCompletionOption.ResponseHeadersRead);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("text/plain", response.Content.Headers.ContentType?.MediaType);
        Assert.Equal("nosniff", response.Headers.GetValues("X-Content-Type-Options").Single());
        Assert.Equal("sandbox", response.Headers.GetValues("Content-Security-Policy").Single());
        var disposition = Assert.Single(response.Content.Headers.GetValues("Content-Disposition"));
        Assert.StartsWith("attachment;", disposition, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("filename=\"", disposition, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("filename*=UTF-8''r%C3%A9sum%C3%A9%20%22Q%22.txt", disposition,
            StringComparison.OrdinalIgnoreCase);
        Assert.Equal("hello", await response.Content.ReadAsStringAsync());
    }

    [Theory]
    [InlineData("text/html")]
    [InlineData("image/svg+xml")]
    public async Task Active_content_types_are_neutralized(string contentType)
    {
        var id = await SeedAttachmentAsync("active-content", contentType, "<script>alert(1)</script>");
        var client = await AuthenticatedClientAsync();

        using var response = await client.GetAsync($"/files/{id}");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("application/octet-stream", response.Content.Headers.ContentType?.MediaType);
        Assert.Equal("nosniff", response.Headers.GetValues("X-Content-Type-Options").Single());
        Assert.Equal("sandbox", response.Headers.GetValues("Content-Security-Policy").Single());
    }

    [Fact]
    public async Task Missing_blob_returns_safe_not_found()
    {
        var id = Guid.NewGuid();
        await SeedMetadataAsync(id, "gone.txt", "text/plain", 4, "missing-storage-key");
        var client = await AuthenticatedClientAsync();

        var response = await client.GetAsync($"/files/{id}");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task Provider_failures_return_safe_not_found_without_exception_details()
    {
        var id = Guid.NewGuid();
        await SeedMetadataAsync(id, "gone.txt", "text/plain", 4, "provider-that-does-not-exist", "provider-that-does-not-exist");
        var client = await AuthenticatedClientAsync();

        using var response = await client.GetAsync($"/files/{id}");
        var body = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.DoesNotContain("provider-that-does-not-exist", body, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("No file storage provider registered", body, StringComparison.OrdinalIgnoreCase);
    }

    private async Task<HttpClient> AuthenticatedClientAsync()
    {
        var client = _host.NewClient();
        var login = await client.GetAsync($"/?token={WebTestHost.Token}");
        Assert.Equal(HttpStatusCode.OK, login.StatusCode);
        return client;
    }

    private async Task<Guid> SeedAttachmentAsync(string fileName, string contentType, string content)
    {
        using var scope = _host.Services.CreateScope();
        var provider = scope.ServiceProvider.GetRequiredService<IFileStorageProviderRegistry>().Get("local-disk");
        await using var bytes = new MemoryStream(Encoding.UTF8.GetBytes(content));
        var stored = await provider.SaveAsync(new FileUpload(fileName, contentType, bytes));
        var id = Guid.NewGuid();
        await SeedMetadataAsync(id, fileName, contentType, Encoding.UTF8.GetByteCount(content), stored.StorageKey);
        return id;
    }

    private async Task SeedMetadataAsync(Guid id, string fileName, string contentType, long size, string storageKey,
        string providerKey = "local-disk")
    {
        using var scope = _host.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<SqlAgentDbContext>();
        var chat = new Chat
        {
            Id = Guid.NewGuid(),
            Title = "download test",
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
            LastMessageAt = DateTime.UtcNow,
        };
        var message = new ChatMessage
        {
            Id = Guid.NewGuid(),
            ChatId = chat.Id,
            Sequence = 0,
            Role = ChatRole.User,
            Text = "download test",
            CreatedAt = DateTime.UtcNow,
        };
        message.Attachments.Add(new MessageAttachment
        {
            Id = id,
            ChatMessageId = message.Id,
            FileName = fileName,
            ContentType = contentType,
            SizeBytes = size,
            ProviderKey = providerKey,
            StorageKey = storageKey,
            Url = $"/files/{id}",
            CreatedAt = DateTime.UtcNow,
        });
        chat.Messages.Add(message);
        db.Chats.Add(chat);
        await db.SaveChangesAsync();
    }
}
