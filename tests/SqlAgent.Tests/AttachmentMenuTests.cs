using Bunit;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Forms;
using Microsoft.Extensions.DependencyInjection;
using SqlAgent.Core;
using SqlAgent.Host.Components.Shared.Chat;
using SqlAgent.Host.Web;
using SqlAgent.Storage;

namespace SqlAgent.Tests;

public sealed class AttachmentMenuTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"sqlagent-attachment-menu-{Guid.NewGuid():N}");

    [Fact]
    public void Files_menu_has_an_empty_state_until_a_file_is_picked()
    {
        using var ctx = NewContext();
        var menu = ctx.Render<AttachmentMenu>();

        menu.Find(".menu-trigger").Click();

        Assert.Contains("Files", menu.Markup);
        Assert.Contains("No files attached", menu.Markup);
    }

    [Fact]
    public async Task Picking_a_file_reports_a_pending_file_and_renders_its_chip()
    {
        using var ctx = NewContext();
        IReadOnlyList<PendingFileAttachment>? pending = null;
        var menu = ctx.Render<AttachmentMenu>(p => p
            .Add(m => m.FileStorage, ctx.Services.GetRequiredService<FileStorageService>())
            .Add(m => m.OnFilesChanged, EventCallback.Factory.Create<IReadOnlyList<PendingFileAttachment>>(
                this, files => pending = files)));
        menu.Find(".menu-trigger").Click();

        menu.FindComponent<InputFile>().UploadFiles(
            InputFileContent.CreateFromText("hello", "notes.txt", contentType: "text/plain"));

        Assert.NotNull(pending);
        Assert.Single(pending!);
    }

    [Fact]
    public async Task A_file_over_25_mib_shows_stable_size_copy_without_provider_text()
    {
        using var ctx = NewContext();
        var menu = ctx.Render<AttachmentMenu>(p => p
            .Add(m => m.FileStorage, ctx.Services.GetRequiredService<FileStorageService>()));
        var content = new byte[(25 * 1024 * 1024) + 1];
        menu.Find(".menu-trigger").Click();

        menu.FindComponent<InputFile>().UploadFiles(InputFileContent.CreateFromBinary(
            content, "large.bin", contentType: "application/octet-stream"));

        Assert.Contains("file_too_large", menu.Markup);
        Assert.DoesNotContain("provider", menu.Markup, StringComparison.OrdinalIgnoreCase);
        Assert.Empty(menu.FindAll(".pending-file"));
    }

    [Fact]
    public async Task The_eleventh_file_is_rejected_without_dropping_the_first_ten()
    {
        using var ctx = NewContext();
        IReadOnlyList<PendingFileAttachment> pending = [];
        var menu = ctx.Render<AttachmentMenu>(p => p
            .Add(m => m.FileStorage, ctx.Services.GetRequiredService<FileStorageService>())
            .Add(m => m.OnFilesChanged, EventCallback.Factory.Create<IReadOnlyList<PendingFileAttachment>>(
                this, files => pending = files)));
        menu.Find(".menu-trigger").Click();
        var files = Enumerable.Range(1, 10)
            .Select(i => InputFileContent.CreateFromBinary([(byte)i], $"file-{i}.txt", contentType: "text/plain"))
            .ToArray();

        menu.FindComponent<InputFile>().UploadFiles(files);
        menu.Render(p => p.Add(m => m.Files, pending));
        menu.FindComponent<InputFile>().UploadFiles(
            InputFileContent.CreateFromBinary([11], "file-11.txt", contentType: "text/plain"));

        Assert.Contains("file_rejected", menu.Markup);
        Assert.Equal(10, pending.Count);
    }

    [Fact]
    public async Task Uploading_is_reported_and_a_second_selection_is_ignored_until_the_first_finishes()
    {
        using var ctx = NewContext();
        var provider = (GatedFileStorageProvider)ctx.Services.GetRequiredService<IFileStorageProvider>();
        provider.HoldUpload();
        var states = new List<bool>();
        IReadOnlyList<PendingFileAttachment> pending = [];
        var menu = ctx.Render<AttachmentMenu>(p => p
            .Add(m => m.FileStorage, ctx.Services.GetRequiredService<FileStorageService>())
            .Add(m => m.OnUploadingChanged, EventCallback.Factory.Create<bool>(
                this, uploading => states.Add(uploading)))
            .Add(m => m.OnFilesChanged, EventCallback.Factory.Create<IReadOnlyList<PendingFileAttachment>>(
                this, files => pending = files)));
        menu.Find(".menu-trigger").Click();
        var input = menu.FindComponent<InputFile>();

        var first = Task.Run(() => input.UploadFiles(
            InputFileContent.CreateFromText("first", "first.txt", contentType: "text/plain")));
        await provider.WaitForUploadStartAsync();
        Assert.Equal([true], states);

        input.UploadFiles(InputFileContent.CreateFromText("second", "second.txt", contentType: "text/plain"));
        provider.ReleaseUpload();
        await first;

        Assert.Equal([true, false], states);
        Assert.Single(pending);
        Assert.Equal("first.txt", pending[0].FileName);
    }

    private Bunit.BunitContext NewContext()
    {
        Directory.CreateDirectory(_root);
        var provider = new GatedFileStorageProvider(_root);
        var options = new FileStorageOptions();
        var ctx = new Bunit.BunitContext();
        ctx.Services.AddSingleton(options);
        ctx.Services.AddSingleton<IFileStorageProvider>(provider);
        ctx.Services.AddSingleton<IFileStorageProviderRegistry>(new FileStorageProviderRegistry([provider]));
        ctx.Services.AddScoped<FileStorageService>();
        ctx.Services.AddScoped<ShortcutService>();
        ctx.Services.AddLogging();
        ctx.JSInterop.Mode = JSRuntimeMode.Loose;
        return ctx;
    }

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }
}
