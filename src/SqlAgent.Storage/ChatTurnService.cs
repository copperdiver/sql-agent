namespace SqlAgent.Storage;

/// <summary>
/// One completed turn. <see cref="Live"/> is the in-memory result for the answer just produced,
/// including its rows — the page renders it once and it is never written anywhere. A reloaded
/// conversation has no Live for any message, which is why a restored answer shows metadata rather than
/// a grid.
/// </summary>
public record ChatTurnResult(
    Guid ChatId, ChatMessageView UserMessage, ChatMessageView AssistantMessage, NlQueryResult? Live);

/// <summary>
/// Runs one chat turn: persist the question, decide what the attached databases allow, ask, persist the
/// answer. Split out of <see cref="ChatService"/> so a whole turn is unit-testable without bUnit and the
/// page stays a thin caller.
///
/// The order is load-bearing. The user's message is written BEFORE the model is called, because
/// everything after that point can fail: a gateway that throws, a circuit that drops, a tab that closes.
/// Losing a typed question to any of those is the one outcome this design refuses.
/// </summary>
public class ChatTurnService(
    ChatService chats, NlQueryService nlQueries, DatabaseConnectionService connections)
{
    public const string NoDatabaseAttached = "no_database_attached";
    public const string MultipleDatabasesUnsupported = "multiple_databases_unsupported";

    /// <summary>Recorded for an attachment whose connection was deleted between attaching and sending.
    /// The chip carried a real name once; by send time there is nothing left to read it from, and
    /// inventing one would be worse than admitting the gap.</summary>
    private const string DeletedDatabaseName = "(deleted database)";

    public async Task<ChatTurnResult> SendAsync(
        Guid? chatId, string question, IReadOnlyList<Guid> databaseIds, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(question))
            throw new ArgumentException("A question is required.", nameof(question));

        // Names are resolved now, not at render time: this is the moment the snapshot is true.
        var attachments = new List<ChatDatabaseRef>(databaseIds.Count);
        foreach (var id in databaseIds)
        {
            var info = await connections.GetAsync(id, ct);
            attachments.Add(info is null
                ? new ChatDatabaseRef(null, DeletedDatabaseName)
                : new ChatDatabaseRef(info.Id, info.Name));
        }

        var chat = chatId ?? await chats.CreateChatAsync(ChatService.TitleFrom(question), ct);
        var userMessage = await chats.AppendMessageAsync(
            new ChatMessageInput(chat, ChatRole.User, question.Trim(), attachments), ct);

        var (live, answer) = await AnswerAsync(chat, question, databaseIds, ct);
        return new ChatTurnResult(chat, userMessage, answer, live);
    }

    private async Task<(NlQueryResult? Live, ChatMessageView Answer)> AnswerAsync(
        Guid chat, string question, IReadOnlyList<Guid> databaseIds, CancellationToken ct)
    {
        switch (databaseIds.Count)
        {
            case 0:
                return (null, await SaveErrorAsync(chat, NoDatabaseAttached,
                    "Attach a database from the composer's attachment menu, then ask again.", ct));

            case 1:
                // The ordinary path: NlQueryService applies policy, executes through the same
                // QueryExecutionService every other surface uses, and audits the run.
                var result = await nlQueries.AskAsync(databaseIds[0], question, ct);
                return (result, await chats.AppendMessageAsync(FromResult(chat, result), ct));

            default:
                // Today's gateway takes one schema and returns one SQL string. Querying the first
                // attachment and calling it an answer would misreport what was asked. The model-service
                // phase replaces this branch with a tool-calling loop.
                return (null, await SaveErrorAsync(chat, MultipleDatabasesUnsupported,
                    "One database at a time for now — detach the others and ask again.", ct));
        }
    }

    private Task<ChatMessageView> SaveErrorAsync(Guid chat, string code, string message, CancellationToken ct) =>
        chats.AppendMessageAsync(new ChatMessageInput(
            chat, ChatRole.Assistant, message, [], OutcomeKind: ChatOutcomeKind.Error, ErrorCode: code), ct);

    /// <summary>
    /// Maps an ask_database outcome onto a stored message. Failures are stored exactly like successes:
    /// dropping them would make a reloaded conversation shorter than the one the user watched, with
    /// their questions apparently unanswered.
    /// </summary>
    private static ChatMessageInput FromResult(Guid chat, NlQueryResult r) => r.Kind switch
    {
        NlResponseKind.QueryResult => new ChatMessageInput(
            chat, ChatRole.Assistant, "", [], r.GeneratedSql, ChatOutcomeKind.QueryResult,
            RowCount: r.RowCount, ElapsedMs: r.ElapsedMs, Truncated: r.Truncated),

        NlResponseKind.ClarificationRequired => new ChatMessageInput(
            chat, ChatRole.Assistant, r.ClarificationQuestion ?? "", [],
            OutcomeKind: ChatOutcomeKind.Clarification),

        _ => new ChatMessageInput(
            chat, ChatRole.Assistant, r.ErrorMessage ?? "", [], r.GeneratedSql, ChatOutcomeKind.Error,
            ErrorCode: r.ErrorCode, ElapsedMs: r.ElapsedMs == 0 ? null : r.ElapsedMs),
    };
}
