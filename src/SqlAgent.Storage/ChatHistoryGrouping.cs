namespace SqlAgent.Storage;

/// <summary>Which day-bucket a chat's last activity falls into.</summary>
public enum HistoryBucket { Today, Yesterday, Previous7Days, Previous30Days, Older }

/// <summary>One rendered section of the history list.</summary>
public record HistoryGroup(HistoryBucket Bucket, string Label, IReadOnlyList<ChatSummary> Chats);

/// <summary>
/// Groups history the way the sidebar shows it. Pure and clock-injected: the caller passes local "now",
/// so this is testable at 00:30 without waiting for 00:30.
///
/// Timestamps are stored in UTC and bucketed in LOCAL time. The host and the browser are the same
/// machine — the UI is loopback-only and single-user — so local time is genuinely the user's time here.
/// Day boundaries are calendar dates, not elapsed hours.
/// </summary>
public static class ChatHistoryGrouping
{
    public static HistoryBucket BucketOf(DateTime lastMessageAtUtc, DateTime nowLocal)
    {
        var days = (nowLocal.Date - lastMessageAtUtc.ToLocalTime().Date).Days;
        return days switch
        {
            // Negative means the stored timestamp is in the future — clock skew, a store copied from
            // another machine, or the timezone moving under a running host. Treated as Today so the
            // newest chat stays at the top instead of falling through to Older.
            <= 0 => HistoryBucket.Today,
            1 => HistoryBucket.Yesterday,
            <= 7 => HistoryBucket.Previous7Days,
            <= 30 => HistoryBucket.Previous30Days,
            _ => HistoryBucket.Older,
        };
    }

    public static string LabelOf(HistoryBucket bucket) => bucket switch
    {
        HistoryBucket.Today => "Today",
        HistoryBucket.Yesterday => "Yesterday",
        HistoryBucket.Previous7Days => "Previous 7 days",
        HistoryBucket.Previous30Days => "Previous 30 days",
        _ => "Older",
    };

    public static IReadOnlyList<HistoryGroup> Group(IEnumerable<ChatSummary> chats, DateTime nowLocal) =>
        chats
            .GroupBy(c => BucketOf(c.LastMessageAt, nowLocal))
            // Ordered by the enum value, not by contents: the members are declared newest-first, so this
            // is reading order, and a bucket with nothing in it simply never appears.
            .OrderBy(g => g.Key)
            .Select(g => new HistoryGroup(
                g.Key,
                LabelOf(g.Key),
                g.OrderByDescending(c => c.LastMessageAt).ToList()))
            .ToList();
}
