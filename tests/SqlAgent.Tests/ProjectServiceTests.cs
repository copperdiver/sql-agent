using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using SqlAgent.Storage;

namespace SqlAgent.Tests;

public class ProjectServiceTests : IDisposable
{
    private readonly SqliteConnection _conn = new("DataSource=:memory:");
    private readonly SqlAgentDbContext _db;
    private readonly ProjectService _projects;
    private readonly ChatService _chats;

    public ProjectServiceTests()
    {
        _conn.Open();
        _db = new SqlAgentDbContext(
            new DbContextOptionsBuilder<SqlAgentDbContext>().UseSqlite(_conn).Options);
        _db.Database.EnsureCreated();
        _projects = new ProjectService(_db);
        _chats = new ChatService(_db);
    }

    [Fact]
    public async Task A_project_lists_with_the_number_of_chats_in_it()
    {
        var id = await NewProjectAsync("quarterly");
        await MoveNewChatAsync("first", id);
        await MoveNewChatAsync("second", id);
        await _chats.CreateChatAsync("ungrouped");

        var summary = Assert.Single(await _projects.ListProjectsAsync());

        Assert.Equal("quarterly", summary.Name);
        Assert.Equal(2, summary.ChatCount);
    }

    [Fact]
    public async Task A_name_already_taken_is_reported_rather_than_thrown()
    {
        // The UI needs to say "that name is taken" beside the field. A bool could not tell that apart
        // from "the project is gone", and an exception would surface as the work-area error panel.
        await NewProjectAsync("quarterly");

        var again = await _projects.CreateProjectAsync("quarterly");

        Assert.Equal(ProjectWriteOutcome.NameTaken, again.Outcome);
        Assert.Null(again.Id);
        Assert.Single(await _projects.ListProjectsAsync());
    }

    [Fact]
    public async Task Names_differing_only_in_case_are_the_same_name()
    {
        // Two projects called "Quarterly" and "quarterly" in one sidebar is a papercut with no upside,
        // so the column collates NOCASE and the uniqueness check inherits that.
        await NewProjectAsync("Quarterly");

        var again = await _projects.CreateProjectAsync("quarterly");

        Assert.Equal(ProjectWriteOutcome.NameTaken, again.Outcome);
    }

    [Fact]
    public async Task Renaming_to_a_taken_name_is_refused_and_renaming_a_missing_project_says_so()
    {
        var first = await NewProjectAsync("quarterly");
        await NewProjectAsync("ad hoc");

        Assert.Equal(ProjectWriteOutcome.NameTaken,
            (await _projects.RenameProjectAsync(first, "ad hoc")).Outcome);
        Assert.Equal(ProjectWriteOutcome.NotFound,
            (await _projects.RenameProjectAsync(Guid.NewGuid(), "anything")).Outcome);
        Assert.Equal(ProjectWriteOutcome.Ok,
            (await _projects.RenameProjectAsync(first, "quarterly revenue")).Outcome);
    }

    [Fact]
    public async Task Renaming_a_project_to_its_own_name_is_allowed()
    {
        // Opening the rename dialog and pressing Save without editing must not report the project's own
        // name as taken by itself.
        var id = await NewProjectAsync("quarterly");

        Assert.Equal(ProjectWriteOutcome.Ok, (await _projects.RenameProjectAsync(id, "quarterly")).Outcome);
    }

    [Fact]
    public async Task Renaming_a_project_to_a_different_case_of_its_own_name_is_allowed()
    {
        // The test above passes an identical string, so it would pass even without the `p.Id != id`
        // exclusion in RenameProjectAsync — nothing else in the table matches "quarterly" either way.
        // "quarterly" -> "Quarterly" is the case that actually exercises it: the column collates NOCASE,
        // so without the exclusion the project's own unchanged name would compare equal to the new one and
        // get reported back as taken.
        var id = await NewProjectAsync("quarterly");

        var result = await _projects.RenameProjectAsync(id, "Quarterly");

        Assert.Equal(ProjectWriteOutcome.Ok, result.Outcome);
        Assert.Equal("Quarterly", Assert.Single(await _projects.ListProjectsAsync()).Name);
    }

    [Fact]
    public async Task Deleting_a_project_and_keeping_its_chats_returns_them_to_history()
    {
        var id = await NewProjectAsync("quarterly");
        var chat = await MoveNewChatAsync("kept", id);

        Assert.True(await _projects.DeleteProjectAsync(id, ProjectDeleteMode.KeepChats));

        Assert.Empty(await _projects.ListProjectsAsync());
        Assert.Contains(await _chats.ListHistoryAsync(), c => c.Id == chat);
    }

    [Fact]
    public async Task Deleting_a_project_with_its_chats_takes_them_with_it()
    {
        // The only operation in this phase that can destroy a conversation, which is why the caller has
        // to name the mode rather than getting a default.
        var id = await NewProjectAsync("quarterly");
        var chat = await MoveNewChatAsync("doomed", id);

        Assert.True(await _projects.DeleteProjectAsync(id, ProjectDeleteMode.DeleteChats));

        Assert.Empty(await _projects.ListProjectsAsync());
        Assert.Null(await _chats.GetChatAsync(chat));
        Assert.Empty(await _db.ChatMessages.ToListAsync());
    }

    [Fact]
    public async Task Deleting_a_project_that_is_gone_reports_it_rather_than_throwing()
    {
        Assert.False(await _projects.DeleteProjectAsync(Guid.NewGuid(), ProjectDeleteMode.KeepChats));
    }

    [Fact]
    public async Task A_chat_moves_into_a_project_and_back_out_again()
    {
        var id = await NewProjectAsync("quarterly");
        var chat = await _chats.CreateChatAsync("wandering");

        Assert.True(await _projects.MoveChatAsync(chat, id));
        Assert.Contains(await _projects.ListChatsInProjectAsync(id), c => c.Id == chat);
        Assert.DoesNotContain(await _chats.ListHistoryAsync(), c => c.Id == chat);

        Assert.True(await _projects.MoveChatAsync(chat, null));
        Assert.Empty(await _projects.ListChatsInProjectAsync(id));
        Assert.Contains(await _chats.ListHistoryAsync(), c => c.Id == chat);
    }

    [Fact]
    public async Task Moving_a_chat_that_is_gone_or_into_a_project_that_is_gone_reports_it()
    {
        var chat = await _chats.CreateChatAsync("wandering");

        Assert.False(await _projects.MoveChatAsync(Guid.NewGuid(), null));
        Assert.False(await _projects.MoveChatAsync(chat, Guid.NewGuid()));
        // The failed move left the chat where it was rather than orphaning its project id.
        Assert.Contains(await _chats.ListHistoryAsync(), c => c.Id == chat);
    }

    [Fact]
    public void A_blank_name_gets_a_placeholder_and_a_long_one_is_cut()
    {
        Assert.Equal("Untitled project", ProjectService.NameFrom("   "));
        Assert.Equal("quarterly", ProjectService.NameFrom("  quarterly  "));
        Assert.Equal(60, ProjectService.NameFrom(new string('x', 200)).Length);
    }

    private async Task<Guid> NewProjectAsync(string name)
    {
        var result = await _projects.CreateProjectAsync(name);
        Assert.Equal(ProjectWriteOutcome.Ok, result.Outcome);
        return result.Id!.Value;
    }

    private async Task<Guid> MoveNewChatAsync(string title, Guid projectId)
    {
        var chat = await _chats.CreateChatAsync(title);
        await _chats.AppendMessageAsync(new ChatMessageInput(chat, ChatRole.User, "q", []));
        Assert.True(await _projects.MoveChatAsync(chat, projectId));
        return chat;
    }

    public void Dispose()
    {
        _db.Dispose();
        _conn.Dispose();
    }
}
