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

        // CreateChatAsync applies TitleFrom itself, so passing the raw question here (rather than
        // pre-cutting it) is not a missed step — it is not doing this twice. Kept in CreateChatAsync
        // specifically because that is the one place no caller can bypass it; RenameChatAsync applies
        // it too, independently, for the same reason.
        var chat = chatId ?? await chats.CreateChatAsync(question, ct);
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
                // CancellationToken.None, matching the persist call below rather than the token this
                // method was called with: the question is already on disk (SendAsync wrote it before
                // this switch ran), and a stop click landing in this exact window would let saving this
                // answer with the caller's own spent token throw the save away — orphaning the question
                // with no reply, the outcome this whole service exists to prevent.
                return (null, await SaveErrorAsync(chat, NoDatabaseAttached,
                    "Attach a database from the composer's attachment menu, then ask again.",
                    CancellationToken.None));

            case 1:
                // The ordinary path: NlQueryService applies policy, executes through the same
                // QueryExecutionService every other surface uses, and audits the run.
                NlQueryResult result;
                try
                {
                    result = await nlQueries.AskAsync(databaseIds[0], question, ct);
                }
                catch (OperationCanceledException)
                {
                    // Cancelled before QueryExecutionService's own graceful handling could even apply —
                    // while the schema was being read, or while the model was still generating SQL
                    // (NlQueryService rethrows OperationCanceledException there rather than swallowing
                    // it, unlike the execute step below). The chat row and the question are already on
                    // disk; a bare exception here would leave both orphaned, with no id for the page to
                    // navigate to and no record of what happened. CancellationToken.None for the same
                    // reason as the graceful path below: the work already stopped, and the record of
                    // that must not be lost to the very token that ended it.
                    return (null, await SaveErrorAsync(chat, "execution_canceled",
                        "The request was canceled.", CancellationToken.None));
                }

                // ct may already be tripped here even though `result` came back gracefully:
                // QueryExecutionService deliberately catches OperationCanceledException and converts it
                // to execution_canceled/execution_timeout instead of rethrowing (its own audit write
                // does the same, using CancellationToken.None for the same reason). Persisting that
                // already-computed answer with the same spent token would throw it away right here,
                // leaving the question on disk with no reply — the exact outcome the layer below took
                // care to avoid one level down.
                return (result, await chats.AppendMessageAsync(FromResult(chat, result), CancellationToken.None));

            default:
                // Today's gateway takes one schema and returns one SQL string. Querying the first
                // attachment and calling it an answer would misreport what was asked. The model-service
                // phase replaces this branch with a tool-calling loop.
                //
                // CancellationToken.None for the same reason as the zero-database case above: the
                // question is already written, and persisting this answer with a spent token would
                // throw the save away and leave it orphaned.
                return (null, await SaveErrorAsync(chat, MultipleDatabasesUnsupported,
                    "One database at a time for now — detach the others and ask again.",
                    CancellationToken.None));
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
