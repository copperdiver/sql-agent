using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace SqlAgent.Storage;

/// <summary>A project and how many chats are in it.</summary>
public record ProjectSummary(Guid Id, string Name, int ChatCount);

/// <summary>What a project write did. Not a <c>bool</c>: "that name is taken" and "the project is gone"
/// need different words in the dialog, and a bool cannot tell them apart.</summary>
public enum ProjectWriteOutcome { Ok, NameTaken, NotFound }

public record ProjectWriteResult(ProjectWriteOutcome Outcome, Guid? Id = null);

/// <summary>What to do with a project's chats when the project goes.</summary>
public enum ProjectDeleteMode { KeepChats, DeleteChats }

/// <summary>
/// Projects and which chats are in them. The store only: the dialogs that ask "keep or delete?" live in
/// the sidebar, and this class never decides that on the caller's behalf.
/// </summary>
public class ProjectService(SqlAgentDbContext db)
{
    /// <summary>SQLite's constraint-violation result code, as <see cref="ChatService"/> uses it.</summary>
    private const int SqliteConstraint = 19;

    public async Task<IReadOnlyList<ProjectSummary>> ListProjectsAsync(CancellationToken ct = default) =>
        // p.Chats.Count becomes a correlated subquery, so this is one round trip whatever the project
        // count is — not a query per row.
        await db.Projects
            .OrderBy(p => p.Name)
            .Select(p => new ProjectSummary(p.Id, p.Name, p.Chats.Count))
            .ToListAsync(ct);

    /// <summary>The chats in one project, ordered exactly as the history list orders its own, so the
    /// same row component renders both without either side knowing where it is.</summary>
    public async Task<IReadOnlyList<ChatSummary>> ListChatsInProjectAsync(
        Guid projectId, CancellationToken ct = default) =>
        await db.Chats
            .Where(c => c.ProjectId == projectId)
            .OrderByDescending(c => c.LastMessageAt).ThenByDescending(c => c.CreatedAt)
            .Select(c => new ChatSummary(c.Id, c.Title, c.LastMessageAt))
            .ToListAsync(ct);

    public async Task<ProjectWriteResult> CreateProjectAsync(string name, CancellationToken ct = default)
    {
        var trimmed = NameFrom(name);
        // Read first so the dialog can say "that name is taken" plainly; the catch below is the backstop
        // that makes the race between this read and the write harmless. Same two-layer shape
        // ChatService uses for message sequence numbers.
        if (await db.Projects.AnyAsync(p => p.Name == trimmed, ct))
            return new ProjectWriteResult(ProjectWriteOutcome.NameTaken);

        var now = DateTime.UtcNow;
        var project = new Project { Id = Guid.NewGuid(), Name = trimmed, CreatedAt = now, UpdatedAt = now };
        db.Projects.Add(project);
        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException ex) when (IsNameCollision(ex))
        {
            db.Entry(project).State = EntityState.Detached;
            return new ProjectWriteResult(ProjectWriteOutcome.NameTaken);
        }
        return new ProjectWriteResult(ProjectWriteOutcome.Ok, project.Id);
    }

    public async Task<ProjectWriteResult> RenameProjectAsync(
        Guid id, string name, CancellationToken ct = default)
    {
        var project = await db.Projects.FirstOrDefaultAsync(p => p.Id == id, ct);
        if (project is null) return new ProjectWriteResult(ProjectWriteOutcome.NotFound);

        var trimmed = NameFrom(name);
        // Excluding this project from the check: pressing Save without editing must not report a
        // project's own name as taken by itself.
        if (await db.Projects.AnyAsync(p => p.Id != id && p.Name == trimmed, ct))
            return new ProjectWriteResult(ProjectWriteOutcome.NameTaken);

        project.Name = trimmed;
        project.UpdatedAt = DateTime.UtcNow;
        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException ex) when (IsNameCollision(ex))
        {
            await db.Entry(project).ReloadAsync(ct);
            return new ProjectWriteResult(ProjectWriteOutcome.NameTaken);
        }
        return new ProjectWriteResult(ProjectWriteOutcome.Ok, id);
    }

    public async Task<bool> DeleteProjectAsync(
        Guid id, ProjectDeleteMode mode, CancellationToken ct = default)
    {
        var project = await db.Projects
            .Include(p => p.Chats)
            .FirstOrDefaultAsync(p => p.Id == id, ct);
        if (project is null) return false;

        // The chats have to be dealt with first: the foreign key is Restrict, so removing the project
        // while anything still points at it fails rather than deciding for the user.
        if (mode == ProjectDeleteMode.DeleteChats)
            db.Chats.RemoveRange(project.Chats);
        else
            foreach (var chat in project.Chats) chat.ProjectId = null;

        db.Projects.Remove(project);
        await db.SaveChangesAsync(ct);
        return true;
    }

    /// <summary>Moves a chat into a project, or out of every project when <paramref name="projectId"/>
    /// is null — which is what "remove from project" and "move to another" both are.</summary>
    public async Task<bool> MoveChatAsync(Guid chatId, Guid? projectId, CancellationToken ct = default)
    {
        var chat = await db.Chats.FirstOrDefaultAsync(c => c.Id == chatId, ct);
        if (chat is null) return false;

        // Checked rather than left to the foreign key: a missing project should read as "no" to the
        // caller, not as a DbUpdateException surfacing through the work-area error panel.
        if (projectId is { } target && !await db.Projects.AnyAsync(p => p.Id == target, ct))
            return false;

        chat.ProjectId = projectId;
        chat.UpdatedAt = DateTime.UtcNow;
        await db.SaveChangesAsync(ct);
        return true;
    }

    /// <summary>A project's display name: trimmed, cut to what the sidebar row fits, never blank.</summary>
    public static string NameFrom(string name)
    {
        var trimmed = name.Trim();
        if (trimmed.Length == 0) return "Untitled project";
        return trimmed.Length <= 60 ? trimmed : trimmed[..60];
    }

    private static bool IsNameCollision(DbUpdateException ex) =>
        ex.InnerException is SqliteException { SqliteErrorCode: SqliteConstraint } sqlite
        && sqlite.Message.Contains("Projects.Name", StringComparison.Ordinal);
}
