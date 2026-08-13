using SqlAgent.Host.Web;

namespace SqlAgent.Tests;

/// <summary>
/// AppState exists because Blazor siblings do not re-render each other: the sidebar's history section
/// and the chat page are siblings under MainLayout, so a chat created on the page reaches the sidebar
/// only through an event. These pin the notification contract that makes that work.
/// </summary>
public class AppStateTests
{
    [Fact]
    public void Selecting_a_chat_announces_it_once()
    {
        var state = new AppState();
        var notified = 0;
        state.ChatsChanged += () => notified++;
        var id = Guid.NewGuid();

        state.SetActiveChat(id);

        Assert.Equal(id, state.ActiveChatId);
        Assert.Equal(1, notified);
    }

    [Fact]
    public void Re_selecting_the_same_chat_says_nothing()
    {
        // Every navigation to /chat/{id} sets the active chat, including a re-render of the page already
        // showing it. Announcing that would re-read the whole history list from SQLite for no change.
        var state = new AppState();
        var id = Guid.NewGuid();
        state.SetActiveChat(id);
        var notified = 0;
        state.ChatsChanged += () => notified++;

        state.SetActiveChat(id);

        Assert.Equal(0, notified);
    }

    [Fact]
    public void The_history_list_can_be_told_to_refresh_without_the_selection_moving()
    {
        // Rename and delete change the list while the selection stays put — the same distinction the
        // existing Changed/ConnectionsChanged pair already draws for connections.
        var state = new AppState();
        var id = Guid.NewGuid();
        state.SetActiveChat(id);
        var notified = 0;
        state.ChatsChanged += () => notified++;

        state.NotifyChatsChanged();

        Assert.Equal(1, notified);
        Assert.Equal(id, state.ActiveChatId);
    }

    [Fact]
    public void SQL_handed_to_the_editor_is_read_exactly_once()
    {
        // "Open in editor" sets this and navigates; /sql reads it on its first render. If the read did
        // not clear it, every later visit to /sql would silently overwrite whatever the user had typed
        // with the same old query.
        var state = new AppState();

        state.HandOffSql("SELECT 1");

        Assert.Equal("SELECT 1", state.TakePendingSql());
        Assert.Null(state.TakePendingSql());
    }
}
