using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using SqlAgent.Storage;

namespace SqlAgent.Host.Web;

public static class FileDownloadEndpoint
{
    public static RouteHandlerBuilder MapFileDownload(this IEndpointRouteBuilder endpoints) =>
        endpoints.MapGet("/files/{id:guid}", DownloadAsync);

    private static async Task<IResult> DownloadAsync(
        Guid id,
        MessageAttachmentService attachments,
        HttpResponse response,
        CancellationToken ct)
    {
        FileDownload? download;
        try
        {
            download = await attachments.OpenDownloadAsync(id, ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            return Results.NotFound();
        }

        if (download is null) return Results.NotFound();

        response.Headers["X-Content-Type-Options"] = "nosniff";
        response.Headers["Content-Security-Policy"] = "sandbox";
        response.ContentLength = download.SizeBytes >= 0 ? download.SizeBytes : null;

        return Results.File(
            download.Content,
            ContentType(download.ContentType),
            SafeFileName(download.FileName),
            enableRangeProcessing: false);
    }

    private static string ContentType(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return "application/octet-stream";

        var normalized = value.Trim();
        var mediaType = normalized.Split(';', 2)[0].Trim();
        if (mediaType.Equals("text/html", StringComparison.OrdinalIgnoreCase)
            || mediaType.Equals("application/xhtml+xml", StringComparison.OrdinalIgnoreCase)
            || mediaType.Equals("image/svg+xml", StringComparison.OrdinalIgnoreCase)
            || mediaType.Equals("text/xml", StringComparison.OrdinalIgnoreCase)
            || mediaType.Equals("application/xml", StringComparison.OrdinalIgnoreCase)
            || mediaType.EndsWith("+xml", StringComparison.OrdinalIgnoreCase))
            return "application/octet-stream";

        // Persisted metadata is trusted only as data. Reject control characters and malformed media
        // types so an old/corrupt row cannot turn into a response-header injection or an endpoint error.
        if (normalized.Any(char.IsControl) || mediaType.Length == 0 || !mediaType.Contains('/'))
            return "application/octet-stream";

        return normalized;
    }

    private static string SafeFileName(string? value)
    {
        var name = (value ?? string.Empty).Replace('\\', '/').Split('/').Last().Trim();
        name = new string(name.Where(c => !char.IsControl(c)).ToArray());
        return string.IsNullOrWhiteSpace(name) ? "file" : name;
    }
}
